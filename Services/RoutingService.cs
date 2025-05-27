// Services/RoutingService.cs
using LLMProxy.Models;

using System.Collections.Concurrent;
using System.Text.Json; // For JsonException, JsonArray, JsonNode if used directly (though mostly in Dispatcher)
using System.Text.Json.Nodes; // For JsonArray, JsonNode if used directly
using System.Text.RegularExpressions;

namespace LLMProxy.Services;

public class RoutingService
{
    private readonly DynamicConfigurationService _configService;
    private readonly ILogger<RoutingService> _logger;

    // State for RoundRobin on a model's backends: ModelName -> BackendIndex
    private readonly ConcurrentDictionary<string, int> _roundRobinBackendStateIndex = new();

    // State for RoundRobin on a backend's API keys: ModelName_BackendName -> ApiKeyIndex
    private readonly ConcurrentDictionary<string, int> _roundRobinApiKeyIndex = new();

    // State for RoundRobin on a group's member models: GroupName -> MemberModelIndex
    private readonly ConcurrentDictionary<string, int> _roundRobinGroupModelStateIndex = new();

    // Cache for compiled Regex to improve performance for ContentBased routing
    private readonly ConcurrentDictionary<string, Regex> _regexCache = new();

    public RoutingService(DynamicConfigurationService configService, ILogger<RoutingService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Tries to get the direct routing configuration for a single, specific model ID.
    /// This is a pass-through to the DynamicConfigurationService.
    /// </summary>
    public bool TryGetModelRouting(string modelId, out ModelRoutingConfig? modelConfig)
    {
        // This method is called by DispatcherService.ExecuteSingleModelRequestAsync
        return _configService.TryGetModelRouting(modelId, out modelConfig);
    }

    private string? ExtractLastUserMessage(string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
            return null;
        try
        {
            var jsonNode = JsonNode.Parse(requestBody);
            var messagesNode = jsonNode?["messages"];
            if (messagesNode is JsonArray messagesArray && messagesArray.Any())
            {
                for (int i = messagesArray.Count - 1; i >= 0; i--)
                {
                    var message = messagesArray[i];
                    if (message?["role"]?.GetValue<string>() == "user")
                    {
                        return message["content"]?.GetValue<string>();
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse request body or extract last user message for content-based routing.");
        }
        return null;
    }

    private Regex GetCompiledRegex(string pattern)
    {
        return _regexCache.GetOrAdd(pattern, p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline));
    }

    /// <summary>
    /// Resolves the routing configuration.
    /// For regular models or non-MoA groups, returns the effective single model config.
    /// For MoA groups, returns the group config itself to signal MoA workflow.
    /// </summary>
    public (string? EffectiveModelName, ModelRoutingConfig? ModelConfig, ModelGroupConfig? GroupConfig) ResolveRoutingConfig(
        string requestedModelOrGroupName,
        string originalRequestBody,
        List<string> triedMemberModelsInGroup)
    {
        var currentFullConfig = _configService.GetCurrentConfig();

        if (currentFullConfig.ModelGroups.TryGetValue(requestedModelOrGroupName, out var groupConfig) && groupConfig != null)
        {
            _logger.LogDebug("'{Name}' is a Model Group. Strategy: '{Strategy}'.", requestedModelOrGroupName, groupConfig.Strategy);

            if (groupConfig.Strategy == RoutingStrategyType.MixtureOfAgents)
            {
                // Validate MoA config (though DynamicConfigService should also validate on save)
                if (string.IsNullOrWhiteSpace(groupConfig.OrchestratorModelName) ||
                    !currentFullConfig.Models.ContainsKey(groupConfig.OrchestratorModelName) ||
                    groupConfig.Models.Count < 1) // UI/Save enforces >=2 agents, but check for >=1 here for safety
                {
                    _logger.LogError("MixtureOfAgents group '{Name}' is improperly configured (missing orchestrator or agents, or they don't exist in Models).", requestedModelOrGroupName);
                    return (null, null, null); // Invalid MoA config
                }
                bool allAgentsExist = groupConfig.Models.All(agent => currentFullConfig.Models.ContainsKey(agent));
                if (!allAgentsExist)
                {
                    _logger.LogError("MixtureOfAgents group '{Name}' has one or more agent models not defined in the main Models configuration.", requestedModelOrGroupName);
                    return (null, null, null);
                }
                return (null, null, groupConfig); // Signal MoA workflow to DispatcherService
            }

            // --- Handle other group strategies (Failover, RoundRobin, Weighted, ContentBased) ---
            string? chosenMemberModelName = null;
            var availableMemberModels = groupConfig.Models // groupConfig.Models are the member/agent models.
                .Where(m => !triedMemberModelsInGroup.Contains(m) && currentFullConfig.Models.ContainsKey(m))
                .ToList();

            if (!availableMemberModels.Any())
            {
                _logger.LogWarning("Model Group '{Name}': No available member models left to try or members are not defined in Models config.", requestedModelOrGroupName);
                return (null, null, groupConfig); // Return groupConfig to indicate group was found but exhausted
            }

            switch (groupConfig.Strategy)
            {
                case RoutingStrategyType.ContentBased:
                    string? lastUserMessage = ExtractLastUserMessage(originalRequestBody);
                    if (lastUserMessage != null)
                    {
                        var matchedRule = groupConfig.ContentRules
                            .Where(r => availableMemberModels.Contains(r.TargetModelName)) // Target must be an available member
                            .OrderBy(r => r.Priority)
                            .FirstOrDefault(r => !string.IsNullOrEmpty(r.RegexPattern) && GetCompiledRegex(r.RegexPattern).IsMatch(lastUserMessage));

                        if (matchedRule != null)
                        {
                            chosenMemberModelName = matchedRule.TargetModelName;
                            _logger.LogInformation("ContentBased for group '{Group}': Matched rule for '{Pattern}', target '{Model}'.",
                                requestedModelOrGroupName, matchedRule.RegexPattern, chosenMemberModelName);
                        }
                    }
                    // Fallback for ContentBased if no rule matched or no message
                    if (chosenMemberModelName == null)
                    {
                        if (!string.IsNullOrEmpty(groupConfig.DefaultModelForContentBased) &&
                            availableMemberModels.Contains(groupConfig.DefaultModelForContentBased))
                        {
                            chosenMemberModelName = groupConfig.DefaultModelForContentBased;
                            _logger.LogInformation("ContentBased for group '{Group}': No rule match, using default '{Model}'.",
                                requestedModelOrGroupName, chosenMemberModelName);
                        }
                        else
                        {
                            // Ultimate fallback for ContentBased: Apply Failover to available members
                            chosenMemberModelName = availableMemberModels.FirstOrDefault();
                            _logger.LogInformation("ContentBased for group '{Group}': No rule/default, falling back to Failover, selected '{Model}'.",
                               requestedModelOrGroupName, chosenMemberModelName);
                        }
                    }
                    break;

                case RoutingStrategyType.Failover:
                    chosenMemberModelName = availableMemberModels.FirstOrDefault();
                    break;

                case RoutingStrategyType.RoundRobin:
                    if (availableMemberModels.Any())
                    {
                        // Use groupName for the state key for member model round robin
                        int currentIndex = _roundRobinGroupModelStateIndex.AddOrUpdate(requestedModelOrGroupName, 0, (key, oldValue) => (oldValue + 1) % availableMemberModels.Count);
                        chosenMemberModelName = availableMemberModels[currentIndex];
                    }
                    break;

                case RoutingStrategyType.Weighted:
                    // Simplified Weighted for group members: assumes equal weight (random selection).
                    // For true weighted, ModelGroupConfig.Models would need to be List<MemberModelWithWeight>.
                    if (availableMemberModels.Any())
                    {
                        chosenMemberModelName = availableMemberModels[Random.Shared.Next(availableMemberModels.Count)];
                        _logger.LogDebug("Weighted strategy for group '{Group}' members (random selection): chose '{Model}'.", requestedModelOrGroupName, chosenMemberModelName);
                    }
                    break;

                default:
                    _logger.LogWarning("Unsupported strategy '{Strategy}' for model group '{Name}'. Defaulting to Failover.", groupConfig.Strategy, requestedModelOrGroupName);
                    chosenMemberModelName = availableMemberModels.FirstOrDefault();
                    break;
            }

            if (chosenMemberModelName != null && currentFullConfig.Models.TryGetValue(chosenMemberModelName, out var memberModelConfig))
            {
                _logger.LogInformation("Model Group '{Group}' (Strategy: {Strategy}) resolved to member model '{MemberModel}'.",
                    requestedModelOrGroupName, groupConfig.Strategy, chosenMemberModelName);
                return (chosenMemberModelName, memberModelConfig, groupConfig); // Return chosen model and its config, plus the original group
            }
            _logger.LogWarning("Model Group '{Name}': Could not resolve to a valid member model config (chosen name: {ChosenName}).",
                requestedModelOrGroupName, chosenMemberModelName ?? "null");
            return (null, null, groupConfig); // Group found but couldn't pick a valid member
        }
        else if (currentFullConfig.Models.TryGetValue(requestedModelOrGroupName, out var modelConfig) && modelConfig != null)
        {
            // It's a direct model request
            return (requestedModelOrGroupName, modelConfig, null);
        }

        _logger.LogWarning("No model or model group configuration found for ID: {ID}", requestedModelOrGroupName);
        return (null, null, null);
    }

    /// <summary>
    /// Gets the next backend and API key for a specific, resolved model configuration.
    /// </summary>
    public (BackendConfig? Backend, string? ApiKey) GetNextBackendAndKeyForModel(
        ModelRoutingConfig modelConfig, // This is the config of the *specific model* (either direct or resolved group member)
        string modelName, // Name of the effective model, for logging and state keys
        List<BackendConfig> triedBackends)
    {
        if (modelConfig == null || !modelConfig.Backends.Any())
        {
            _logger.LogWarning("No backends configured for model: {ModelName}", modelName);
            return (null, null);
        }

        var availableBackends = modelConfig.Backends
            .Where(b => b.Enabled && b.ApiKeys.Any() && !triedBackends.Any(tried => tried.Name == b.Name))
            .ToList();

        if (!availableBackends.Any())
        {
            _logger.LogWarning("No enabled backends with API keys available for model {ModelName} after considering tried backends.", modelName);
            return (null, null);
        }

        BackendConfig? selectedBackend = null;

        // This strategy is the one defined *within* the individual ModelRoutingConfig for its backends
        switch (modelConfig.Strategy)
        {
            case RoutingStrategyType.Failover:
                selectedBackend = availableBackends.FirstOrDefault();
                break;

            case RoutingStrategyType.RoundRobin:
                if (availableBackends.Any())
                {
                    // Note: _roundRobinBackendStateIndex key should be modelName (the effective model name)
                    int backendIndex = _roundRobinBackendStateIndex.AddOrUpdate(modelName, 0, (key, oldValue) => (oldValue + 1));
                    selectedBackend = availableBackends[backendIndex % availableBackends.Count];
                }
                break;

            case RoutingStrategyType.Weighted:
                if (availableBackends.Any())
                {
                    int totalWeight = availableBackends.Sum(b => b.Weight > 0 ? b.Weight : 1);
                    if (totalWeight <= 0)
                    {
                        selectedBackend = availableBackends.FirstOrDefault(); // Should not happen if weights are positive
                    }
                    else
                    {
                        int randomValue = Random.Shared.Next(1, totalWeight + 1);
                        int cumulativeWeight = 0;
                        foreach (var backend in availableBackends)
                        {
                            cumulativeWeight += (backend.Weight > 0 ? backend.Weight : 1);
                            if (randomValue <= cumulativeWeight)
                            {
                                selectedBackend = backend;
                                break;
                            }
                        }
                        selectedBackend ??= availableBackends.Last(); // Fallback if something went wrong
                    }
                }
                break;

            // These are group-level strategies. If a ModelConfig has these, it's a configuration error. Default to Failover.
            case RoutingStrategyType.ContentBased:
            case RoutingStrategyType.MixtureOfAgents:
                _logger.LogError("Invalid strategy '{Strategy}' found for backend selection of model '{ModelName}'. This strategy is group-level. Defaulting to Failover for its backends.", modelConfig.Strategy, modelName);
                selectedBackend = availableBackends.FirstOrDefault();
                break;

            default:
                _logger.LogWarning("Unsupported backend strategy {Strategy} for model {ModelName}. Defaulting to Failover.", modelConfig.Strategy, modelName);
                selectedBackend = availableBackends.FirstOrDefault();
                break;
        }

        string? selectedApiKey = null;
        if (selectedBackend != null)
        {
            if (selectedBackend.ApiKeys.Any())
            {
                string apiKeyStateKey = $"{modelName}_{selectedBackend.Name}"; // Key uses effective model name
                int apiKeyIndex = _roundRobinApiKeyIndex.AddOrUpdate(apiKeyStateKey, 0, (key, oldValue) => (oldValue + 1));
                selectedApiKey = selectedBackend.ApiKeys[apiKeyIndex % selectedBackend.ApiKeys.Count];
                _logger.LogInformation("Routing model '{ModelName}' (backend strategy {Strategy}) to backend '{BackendName}'. API Key index {ApiKeyIdx}",
                    modelName, modelConfig.Strategy, selectedBackend.Name, apiKeyIndex % selectedBackend.ApiKeys.Count);
            }
            else
            {
                _logger.LogWarning("Selected backend '{BackendName}' for model '{ModelName}' has no API keys, though it passed initial checks.", selectedBackend.Name, modelName);
            }
        }
        else
        {
            _logger.LogWarning("No backend selected for model '{ModelName}' based on its strategy '{Strategy}' and tried backends.", modelName, modelConfig.Strategy);
        }

        return (selectedBackend, selectedApiKey);
    }
}
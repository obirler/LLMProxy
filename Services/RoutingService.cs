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
    List<string> triedMemberModelsInGroup) // triedMemberModelsInGroup are model *names*
    {
        var currentFullConfig = _configService.GetCurrentConfig();

        if (currentFullConfig.ModelGroups.TryGetValue(requestedModelOrGroupName, out var groupConfig) && groupConfig != null)
        {
            _logger.LogDebug("'{Name}' is a Model Group. Strategy: '{Strategy}'.", requestedModelOrGroupName, groupConfig.Strategy);

            if (groupConfig.Strategy == RoutingStrategyType.MixtureOfAgents)
            {
                // Validate MoA config using AgentModelNames
                if (string.IsNullOrWhiteSpace(groupConfig.OrchestratorModelName) ||
                    !currentFullConfig.Models.ContainsKey(groupConfig.OrchestratorModelName) ||
                    !groupConfig.AgentModelNames.Any()) // Check AgentModelNames for MoA
                {
                    _logger.LogError("MixtureOfAgents group '{Name}' is improperly configured (missing orchestrator or agents in AgentModelNames, or they don't exist in Models).", requestedModelOrGroupName);
                    return (null, null, null);
                }
                bool allAgentsExist = groupConfig.AgentModelNames.All(agentName => currentFullConfig.Models.ContainsKey(agentName));
                if (!allAgentsExist)
                {
                    _logger.LogError("MixtureOfAgents group '{Name}' has one or more agent models in AgentModelNames not defined in the main Models configuration.", requestedModelOrGroupName);
                    return (null, null, null);
                }
                // For MoA, we also pass the groupConfig.AgentModelNames to DispatcherService through the groupConfig object itself.
                // The DispatcherService will iterate over groupConfig.AgentModelNames.
                return (null, null, groupConfig); // Signal MoA workflow
            }

            // --- Handle other group strategies (Failover, RoundRobin, Weighted, ContentBased) using MemberModels ---
            string? chosenMemberModelName = null;

            // Get member models that are enabled, exist in the main Models config, and haven't been tried yet.
            var availableWeightedMembers = groupConfig.MemberModels
                .Where(m => m.Enabled &&
                            !string.IsNullOrWhiteSpace(m.Name) &&
                            currentFullConfig.Models.ContainsKey(m.Name) &&
                            !triedMemberModelsInGroup.Contains(m.Name))
                .ToList();

            if (!availableWeightedMembers.Any())
            {
                _logger.LogWarning("Model Group '{Name}': No available member models left to try from MemberModels or members are not defined in Models config.", requestedModelOrGroupName);
                return (null, null, groupConfig); // Group found but exhausted
            }

            // For strategies other than Weighted, we primarily need the names.
            // For RoundRobin, we'll pick from availableWeightedMembers then get the name.
            var availableMemberModelNamesForNonWeighted = availableWeightedMembers.Select(m => m.Name).ToList();

            switch (groupConfig.Strategy)
            {
                case RoutingStrategyType.ContentBased:
                    string? lastUserMessage = ExtractLastUserMessage(originalRequestBody);
                    if (lastUserMessage != null)
                    {
                        var matchedRule = groupConfig.ContentRules
                            .Where(r => availableMemberModelNamesForNonWeighted.Contains(r.TargetModelName)) // Target must be an available member name
                            .OrderBy(r => r.Priority)
                            .FirstOrDefault(r => !string.IsNullOrEmpty(r.RegexPattern) && GetCompiledRegex(r.RegexPattern).IsMatch(lastUserMessage));

                        if (matchedRule != null)
                        {
                            chosenMemberModelName = matchedRule.TargetModelName;
                            _logger.LogInformation("ContentBased for group '{Group}': Matched rule for '{Pattern}', target '{Model}'.",
                                requestedModelOrGroupName, matchedRule.RegexPattern, chosenMemberModelName);
                        }
                    }
                    if (chosenMemberModelName == null)
                    {
                        if (!string.IsNullOrEmpty(groupConfig.DefaultModelForContentBased) &&
                            availableMemberModelNamesForNonWeighted.Contains(groupConfig.DefaultModelForContentBased))
                        {
                            chosenMemberModelName = groupConfig.DefaultModelForContentBased;
                            _logger.LogInformation("ContentBased for group '{Group}': No rule match, using default '{Model}'.",
                                requestedModelOrGroupName, chosenMemberModelName);
                        }
                        else
                        {
                            chosenMemberModelName = availableMemberModelNamesForNonWeighted.FirstOrDefault(); // Fallback to Failover on names
                            _logger.LogInformation("ContentBased for group '{Group}': No rule/default, falling back to Failover, selected '{Model}'.",
                               requestedModelOrGroupName, chosenMemberModelName);
                        }
                    }
                    break;

                case RoutingStrategyType.Failover:
                    chosenMemberModelName = availableMemberModelNamesForNonWeighted.FirstOrDefault();
                    break;

                case RoutingStrategyType.RoundRobin:
                    if (availableMemberModelNamesForNonWeighted.Any()) // Use names for round robin index
                    {
                        int currentIndex = _roundRobinGroupModelStateIndex.AddOrUpdate(requestedModelOrGroupName, 0, (key, oldValue) => (oldValue + 1) % availableMemberModelNamesForNonWeighted.Count);
                        chosenMemberModelName = availableMemberModelNamesForNonWeighted[currentIndex];
                    }
                    break;

                case RoutingStrategyType.Weighted: // <<<< NEW WEIGHTED LOGIC FOR GROUPS >>>>
                    if (availableWeightedMembers.Any())
                    {
                        // Ensure weights are positive for calculation
                        int totalWeight = availableWeightedMembers.Sum(m => Math.Max(1, m.Weight));
                        if (totalWeight <= 0) // Should not happen if weights are at least 1
                        {
                            chosenMemberModelName = availableWeightedMembers.First().Name; // Fallback
                            _logger.LogWarning("Weighted strategy for group '{Group}': Total weight is zero or negative, falling back to first available.", requestedModelOrGroupName);
                        }
                        else
                        {
                            int randomValue = Random.Shared.Next(1, totalWeight + 1);
                            int cumulativeWeight = 0;
                            foreach (var member in availableWeightedMembers)
                            {
                                cumulativeWeight += Math.Max(1, member.Weight);
                                if (randomValue <= cumulativeWeight)
                                {
                                    chosenMemberModelName = member.Name;
                                    break;
                                }
                            }
                            chosenMemberModelName ??= availableWeightedMembers.Last().Name; // Fallback if something went wrong
                            _logger.LogDebug("Weighted strategy for group '{Group}' members: chose '{ModelName}'.", requestedModelOrGroupName, chosenMemberModelName);
                        }
                    }
                    break;

                default:
                    _logger.LogWarning("Unsupported strategy '{Strategy}' for model group '{Name}'. Defaulting to Failover.", groupConfig.Strategy, requestedModelOrGroupName);
                    chosenMemberModelName = availableMemberModelNamesForNonWeighted.FirstOrDefault();
                    break;
            }

            if (chosenMemberModelName != null && currentFullConfig.Models.TryGetValue(chosenMemberModelName, out var memberModelConfig))
            {
                _logger.LogInformation("Model Group '{Group}' (Strategy: {Strategy}) resolved to member model '{MemberModel}'.",
                    requestedModelOrGroupName, groupConfig.Strategy, chosenMemberModelName);
                return (chosenMemberModelName, memberModelConfig, groupConfig);
            }
            _logger.LogWarning("Model Group '{Name}': Could not resolve to a valid member model config (chosen name: {ChosenName}). Tried members: {TriedCount}",
                requestedModelOrGroupName, chosenMemberModelName ?? "null", triedMemberModelsInGroup.Count);
            return (null, null, groupConfig);
        }
        else if (currentFullConfig.Models.TryGetValue(requestedModelOrGroupName, out var modelConfig) && modelConfig != null)
        {
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
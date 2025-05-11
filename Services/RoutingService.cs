// Services/RoutingService.cs
using LLMProxy.Models;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// Resolves the effective model to use, which might involve group logic.
    /// </summary>
    /// <param name="requestedModelOrGroupName">The ID sent by the client.</param>
    /// <param name="originalRequestBody">Full request body, used for content-based routing.</param>
    /// <param name="triedMemberModelsInGroup">List of member models already tried from this group in the current request.</param>
    /// <returns>The name of the effective model and its configuration, or nulls if resolution fails.</returns>
    public (string? EffectiveModelName, ModelRoutingConfig? ModelConfig) ResolveEffectiveModelConfig(
        string requestedModelOrGroupName,
        string originalRequestBody,
        List<string> triedMemberModelsInGroup)
    {
        var currentFullConfig = _configService.GetCurrentConfig();

        if (currentFullConfig.ModelGroups.TryGetValue(requestedModelOrGroupName, out var groupConfig) && groupConfig != null)
        {
            // It's a model group
            _logger.LogDebug("'{Name}' is a Model Group. Applying group strategy '{Strategy}'. Tried members: {TriedCount}",
                requestedModelOrGroupName, groupConfig.Strategy, triedMemberModelsInGroup.Count);

            string? chosenMemberModelName = null;
            var availableMemberModels = groupConfig.Models
                .Where(m => !triedMemberModelsInGroup.Contains(m) && currentFullConfig.Models.ContainsKey(m))
                .ToList();

            if (!availableMemberModels.Any())
            {
                _logger.LogWarning("Model Group '{Name}': No available member models left to try or members are not defined in Models config.", requestedModelOrGroupName);
                return (null, null);
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
                            _logger.LogInformation("ContentBased routing for group '{Group}': Matched rule for pattern '{Pattern}', targeting model '{Model}'.",
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
                            _logger.LogInformation("ContentBased routing for group '{Group}': No rule matched, using default model '{Model}'.",
                                requestedModelOrGroupName, chosenMemberModelName);
                        }
                        else
                        {
                            // Ultimate fallback: Apply Failover to available members
                            chosenMemberModelName = availableMemberModels.FirstOrDefault();
                            _logger.LogInformation("ContentBased routing for group '{Group}': No rule matched and no valid default, falling back to Failover, selected '{Model}'.",
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
                        int currentIndex = _roundRobinGroupModelStateIndex.AddOrUpdate(requestedModelOrGroupName, 0, (key, oldValue) => (oldValue + 1) % availableMemberModels.Count);
                        chosenMemberModelName = availableMemberModels[currentIndex];
                    }
                    break;

                case RoutingStrategyType.Weighted:
                    // Simplified Weighted for group members: sum of 1 for each, pick randomly.
                    // True weighted would require weights on group members. For now, treat as RoundRobin or Failover.
                    // Let's use Failover as a stand-in if proper member weights aren't defined.
                    // Or, if we assume all member models have equal weight for now:
                    if (availableMemberModels.Any())
                    {
                        // This is effectively random selection if weights are equal
                        // For a more robust weighted selection among members, ModelGroupConfig.Models would need to be List<MemberModelWithWeight>
                        // For now, let's just pick the first available for simplicity, like Failover.
                        // Or, implement a simple random pick:
                        chosenMemberModelName = availableMemberModels[Random.Shared.Next(availableMemberModels.Count)];
                        _logger.LogDebug("Weighted strategy for group '{Group}' members (currently implemented as random selection): chose '{Model}'.", requestedModelOrGroupName, chosenMemberModelName);
                    }
                    break;

                default:
                    _logger.LogWarning("Unsupported strategy '{Strategy}' for model group '{Name}'. Defaulting to Failover.", groupConfig.Strategy, requestedModelOrGroupName);
                    chosenMemberModelName = availableMemberModels.FirstOrDefault();
                    break;
            }

            if (chosenMemberModelName != null && currentFullConfig.Models.TryGetValue(chosenMemberModelName, out var memberModelConfig))
            {
                _logger.LogInformation("Model Group '{Group}' resolved to member model '{MemberModel}'.", requestedModelOrGroupName, chosenMemberModelName);
                return (chosenMemberModelName, memberModelConfig);
            }
            _logger.LogWarning("Model Group '{Name}': Could not resolve to a valid member model config.", requestedModelOrGroupName);
            return (null, null);
        }
        else if (currentFullConfig.Models.TryGetValue(requestedModelOrGroupName, out var modelConfig) && modelConfig != null)
        {
            // It's a direct model
            return (requestedModelOrGroupName, modelConfig);
        }

        _logger.LogWarning("No model or model group configuration found for ID: {ID}", requestedModelOrGroupName);
        return (null, null);
    }

    /// <summary>
    /// Gets the next backend and API key for a specific, resolved model configuration.
    /// </summary>
    public (BackendConfig? Backend, string? ApiKey) GetNextBackendAndKeyForModel(
        ModelRoutingConfig modelConfig,
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
                    // This is the original weighted selection for backends of a model
                    int totalWeight = availableBackends.Sum(b => b.Weight > 0 ? b.Weight : 1); // Ensure weight is at least 1
                    if (totalWeight <= 0)
                        selectedBackend = availableBackends.FirstOrDefault(); // Should not happen if weights are positive
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
                        selectedBackend ??= availableBackends.Last(); // Fallback
                    }
                }
                break;

            case RoutingStrategyType.ContentBased:
                _logger.LogError("Invalid Strategy 'ContentBased' encountered for a direct model's backend selection ('{ModelName}'). This strategy is only for groups. Defaulting to Failover.", modelName);
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
                _logger.LogWarning("Selected backend '{BackendName}' for model '{ModelName}' has no API keys.", selectedBackend.Name, modelName);
            }
        }
        else
        {
            _logger.LogWarning("No backend selected for model '{ModelName}' based on its strategy and tried backends.", modelName);
        }

        return (selectedBackend, selectedApiKey);
    }
}
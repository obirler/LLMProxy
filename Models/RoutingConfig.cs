// Models/RoutingConfig.cs
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LLMProxy.Models;

public class RoutingConfig
{
    // Key: Model Name (e.g., "gpt-4", "mistral-7b")
    public Dictionary<string, ModelRoutingConfig> Models { get; set; } = new();

    // NEW: Key: Model Group Name (e.g., "FancyCoder", "GeneralTask")
    public Dictionary<string, ModelGroupConfig> ModelGroups { get; set; } = new();
}

public class ModelRoutingConfig // Existing class
{
    public RoutingStrategyType Strategy
    {
        get; set;
    } // Failover, RoundRobin, Weighted for backends

    public List<BackendConfig> Backends { get; set; } = new();

    // Internal state for round-robin on backends (if needed at this level, or managed by RoutingService)
    [JsonIgnore]
    internal int CurrentBackendIndex { get; set; } = 0; // For RoundRobin/Weighted state if managed here
}

public class BackendConfig // Existing class
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public List<string> ApiKeys { get; set; } = new();
    public int Weight { get; set; } = 1;
    public bool Enabled { get; set; } = true;

    public string? BackendModelName
    {
        get; set;
    }

    [JsonIgnore]
    internal int CurrentApiKeyIndex { get; set; } = 0;
}

// NEW: Configuration for a Model Group
public class ModelGroupConfig
{
    /// <summary>
    /// Strategy for selecting a model from the 'Models' list within this group.
    /// Can be Failover, RoundRobin, Weighted, or ContentBased.
    /// </summary>
    public RoutingStrategyType Strategy
    {
        get; set;
    }

    /// <summary>
    /// List of regular model names that are members of this group.
    /// These models must be defined in the main 'Models' section of RoutingConfig.
    /// </summary>
    public List<string> Models { get; set; } = new();

    /// <summary>
    /// Rules for 'ContentBased' strategy. Evaluated by 'Priority' (lower first).
    /// </summary>
    public List<ContentRule> ContentRules { get; set; } = new();

    /// <summary>
    /// Optional: For 'ContentBased' strategy, the model to use if no ContentRules match.
    /// Must be one of the models in the 'Models' list of this group.
    /// If null and no rules match, 'Failover' will be applied to the group's 'Models'.
    /// </summary>
    public string? DefaultModelForContentBased
    {
        get; set;
    }

    // Internal state for round-robin on member models (if needed at this level)
    [JsonIgnore]
    internal int CurrentModelIndex { get; set; } = 0; // For RoundRobin/Weighted state on group's models
}

// NEW: Rule for content-based routing within a model group
public class ContentRule
{
    /// <summary>
    /// Regex pattern to match against the last user message content.
    /// </summary>
    public string RegexPattern { get; set; } = string.Empty;

    /// <summary>
    /// The name of the target model (from the group's 'Models' list) to route to if the pattern matches.
    /// </summary>
    public string TargetModelName { get; set; } = string.Empty;

    /// <summary>
    /// Priority of the rule. Lower numbers are evaluated first.
    /// </summary>
    public int Priority { get; set; } = 0;
}

// UPDATED Enum: Added ContentBased
public enum RoutingStrategyType
{
    Failover,
    RoundRobin,
    Weighted,
    ContentBased // New strategy, applicable to ModelGroups
}

// Optional: Model for client-facing error response (Keep as is)
public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
}
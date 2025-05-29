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

public class ModelMemberConfig
{
    public string Name { get; set; } = string.Empty; // Name of the model (must exist in main Models dictionary)
    public int Weight { get; set; } = 1;
    public bool Enabled { get; set; } = true; // Optional: allow disabling a member without removing
}

// Configuration for a Model Group
public class ModelGroupConfig
{
    public RoutingStrategyType Strategy
    {
        get; set;
    }

    /// <summary>
    /// For Failover, RoundRobin, Weighted: List of member model configurations (name + weight).
    /// For ContentBased: List of member model configurations available for rules.
    /// For MixtureOfAgents: List of AGENT model names (simple strings, as weights aren't typically used for agent selection in basic MoA).
    /// These models must be defined in the main 'Models' section.
    /// </summary>
    // public List<string> Models { get; set; } = new(); // OLD: This was used for all member/agent lists
    public List<ModelMemberConfig> MemberModels { get; set; } = new(); // NEW: For strategies that use members (Failover, RR, Weighted, ContentBased targets)

    /// <summary>
    /// For MixtureOfAgents: List of AGENT model names (simple strings).
    /// These models must be defined in the main 'Models' section.
    /// Weights are not typically applied to MoA agent selection in this manner.
    /// </summary>
    public List<string> AgentModelNames { get; set; } = new(); // NEW: Specifically for MoA agent models

    public List<ContentRule> ContentRules { get; set; } = new();

    public string? DefaultModelForContentBased
    {
        get; set;
    }

    [JsonIgnore]
    internal int CurrentModelIndex { get; set; } = 0;

    public string? OrchestratorModelName
    {
        get; set;
    }

    // Helper method to get just the names of enabled member models
    [JsonIgnore]
    public List<string> EnabledMemberModelNames => MemberModels.Where(m => m.Enabled).Select(m => m.Name).ToList();
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
    ContentBased, // New strategy, applicable to ModelGroups
    MixtureOfAgents // New strategy
}

// Optional: Model for client-facing error response (Keep as is)
public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
}
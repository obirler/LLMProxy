// Models/RoutingConfig.cs (Essentially the same, maybe add IDs later if needed)
using System.Collections.Generic;
using System.Text.Json.Serialization; // Needed for internal property ignore

namespace LLMProxy.Models;

public class RoutingConfig
{
    // Key: Model Name (e.g., "gpt-4", "mistral-7b")
    public Dictionary<string, ModelRoutingConfig> Models { get; set; } = new();
}

public class ModelRoutingConfig
{
    public RoutingStrategyType Strategy
    {
        get; set;
    }

    public List<BackendConfig> Backends { get; set; } = new();
}

public class BackendConfig
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public List<string> ApiKeys { get; set; } = new();
    public int Weight { get; set; } = 1;
    public bool Enabled { get; set; } = true;

    // *** NEW PROPERTY ***
    /// <summary>
    /// Optional: If set, use this model name when sending requests to *this* specific backend,
    /// overriding the model name originally requested by the client.
    /// Leave null or empty to use the client's requested model name.
    /// </summary>
    public string? BackendModelName
    {
        get; set;
    } // e.g., "microsoft/phi-3-mini-128k-instruct:free"

    [JsonIgnore]
    internal int CurrentApiKeyIndex { get; set; } = 0;
}

public enum RoutingStrategyType
{
    RoundRobin,
    Failover,
    Weighted,
    // Custom // Placeholder
}

// Optional: Model for client-facing error response (Keep as is)
public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
}
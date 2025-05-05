// Models/ModelInfo.cs
namespace LLMProxy.Models;

/// <summary>
/// Represents a single model entry in the /v1/models list.
/// </summary>
public class ModelInfo
{
    /// <summary>
    /// The unique identifier (name) of the model configured in the proxy.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The object type, typically "model".
    /// </summary>
    public string Object { get; set; } = "model"; // Default value

    /// <summary>
    /// Placeholder for ownership, mimicking OpenAI/LM Studio response.
    /// </summary>
    public string OwnedBy { get; set; } = "organization_owner"; // Default value for compatibility
}

// Models/ModelListResponse.cs

/// <summary>
/// Represents the response structure for the /v1/models endpoint.
/// </summary>
public class ModelListResponse
{
    /// <summary>
    /// The object type, typically "list".
    /// </summary>
    public string Object { get; set; } = "list"; // Default value

    /// <summary>
    /// The list of available models configured in the proxy.
    /// </summary>
    public List<ModelInfo> Data { get; set; } = new();
}
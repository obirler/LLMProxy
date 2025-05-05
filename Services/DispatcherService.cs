// Services/DispatcherService.cs
using LLMProxy.Models;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMProxy.Services;

public class DispatcherService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RoutingService _routingService;
    private readonly ILogger<DispatcherService> _logger;
    private const string BackendClientName = "LLMBackendClient";

    public DispatcherService(IHttpClientFactory httpClientFactory, RoutingService routingService, ILogger<DispatcherService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _routingService = routingService;
        _logger = logger;
    }

    public async Task DispatchRequestAsync(HttpContext context)
    {
        string requestPath = context.Request.Path.Value ?? string.Empty; // e.g., "/v1/chat/completions"
        string method = context.Request.Method;

        // Get the last part of the incoming path (e.g., "chat/completions", "embeddings")
        string? backendPathSuffix;
        // Use EndsWith for robustness against potential leading slashes or variations
        if (requestPath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            backendPathSuffix = "chat/completions";
        }
        else if (requestPath.EndsWith("/completions", StringComparison.OrdinalIgnoreCase))
        {
            backendPathSuffix = "completions";
        }
        else if (requestPath.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
        {
            backendPathSuffix = "embeddings";
        }
        else
        {
            _logger.LogError("Unsupported proxy request path: {RequestPath}", requestPath);
            context.Response.StatusCode = StatusCodes.Status404NotFound; // Use 404 for unsupported paths
            await context.Response.WriteAsJsonAsync(new ErrorResponse { Error = "Unsupported proxy endpoint path." });
            return;
        }
        _logger.LogDebug("Determined backend path suffix '{BackendPathSuffix}' for request path '{RequestPath}'", backendPathSuffix, requestPath);
        string originalRequestBody;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
        {
            originalRequestBody = await reader.ReadToEndAsync();
        }

        // Extract the* general*model name requested by the client
        string? generalModelName = TryExtractModelName(originalRequestBody);

        if (string.IsNullOrEmpty(generalModelName))
        {
            _logger.LogWarning("Could not extract model name from request body for path {Path}.", requestPath);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new ErrorResponse { Error = "Could not determine model name from request." });
            return;
        }

        bool isStreaming = originalRequestBody.Contains("\"stream\": true");

        List<BackendConfig> triedBackends = new();
        HttpResponseMessage? response = null;

        while (true)
        {
            var (selectedBackend, apiKey) = _routingService.GetNextBackendAndKey(generalModelName, triedBackends);

            if (selectedBackend == null || string.IsNullOrEmpty(apiKey))
            {
                _logger.LogError("All backends failed or no backend/key available for model {ModelName}.", generalModelName);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new ErrorResponse { Error = "All language model backends are temporarily unavailable." });
                return;
            }

            triedBackends.Add(selectedBackend);

            // Get the configured BaseUrl
            string configuredBaseUrl = selectedBackend.BaseUrl;
            if (string.IsNullOrEmpty(configuredBaseUrl))
            {
                _logger.LogError("BaseUrl is empty or null for backend {BackendName}. Skipping.", selectedBackend.Name);
                continue;
            }

            // Ensure the BaseUrl has a trailing slash for correct relative path combination
            string baseUriStringForCombination = configuredBaseUrl;
            if (!baseUriStringForCombination.EndsWith('/'))
            {
                baseUriStringForCombination += "/";
            }

            // Try creating the base Uri object from the adjusted string
            if (!Uri.TryCreate(baseUriStringForCombination, UriKind.Absolute, out var baseUri))
            {
                _logger.LogError("Invalid BaseUrl format in configuration for backend {BackendName}: {AdjustedBaseUrl}. Skipping this backend.", selectedBackend.Name, selectedBackend.BaseUrl);
                continue; // Skip to the next backend
            }

            // 2. Use the Uri constructor that correctly combines the base Uri and the *final segment*
            Uri upstreamUri;
            try
            {
                // Combine the configured BaseUrl (e.g., ".../v1beta/openai") with the correct suffix ("chat/completions")
                upstreamUri = new Uri(baseUri, backendPathSuffix);
            }
            catch (UriFormatException ex)
            {
                // Use backendPathSuffix in the log message
                _logger.LogError(ex, "Could not create upstream URI from BaseUrl '{BaseUrl}' and backend path suffix '{BackendPathSuffix}'. Skipping backend {BackendName}.",
                    selectedBackend.BaseUrl,
                    backendPathSuffix, // Correct variable here
                    selectedBackend.Name);
                continue; // Skip to the next backend
            }
            // --- END FIX ---

            // --- Modify Request Body if BackendModelName is set (Keep this logic) ---
            string outgoingRequestBody = originalRequestBody; // Default to original
            string modelNameToLog = generalModelName; // Model name used in logs

            if (!string.IsNullOrWhiteSpace(selectedBackend.BackendModelName))
            {
                // ... (logic to parse JSON, update model, serialize back) ...
                // (Keep the existing code block for this)
                _logger.LogInformation("Backend '{BackendName}' specifies overriding model name to '{BackendModelName}' for original request model '{GeneralModelName}'.",
                   selectedBackend.Name, selectedBackend.BackendModelName, generalModelName);
                modelNameToLog = selectedBackend.BackendModelName;

                try
                {
                    // Parse the original JSON body
                    var jsonNode = JsonNode.Parse(originalRequestBody);
                    if (jsonNode is JsonObject jsonObject && jsonObject.ContainsKey("model"))
                    {
                        jsonObject["model"] = selectedBackend.BackendModelName;
                        outgoingRequestBody = jsonObject.ToJsonString();
                        _logger.LogDebug("Modified request body for backend {BackendName}: {RequestBody}", selectedBackend.Name, outgoingRequestBody);
                    }
                    else
                    {
                        _logger.LogWarning("Could not find 'model' property in request body JSON when trying to apply backend-specific model name for backend {BackendName}. Sending original body.", selectedBackend.Name);
                    }
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogError(jsonEx, "Failed to parse or modify request body JSON for backend-specific model name override for backend {BackendName}. Sending original body.", selectedBackend.Name);
                    // Fallback to originalRequestBody which is already set
                }
            }

            var httpClient = _httpClientFactory.CreateClient(BackendClientName);
            using var upstreamRequest = new HttpRequestMessage(new HttpMethod(method), upstreamUri)
            {
                Content = new StringContent(outgoingRequestBody, Encoding.UTF8, "application/json")
            };
            upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            // Copy other relevant headers if needed

            _logger.LogInformation("Dispatching request for model '{ModelNameUsed}' to {Backend} ({UpstreamUri})", modelNameToLog, selectedBackend.Name, upstreamUri);
            try
            {
                // --- Request Sending and Response Handling (Keep as is) ---
                HttpCompletionOption completionOption = isStreaming
                    ? HttpCompletionOption.ResponseHeadersRead
                    : HttpCompletionOption.ResponseContentRead;

                response = await httpClient.SendAsync(upstreamRequest, completionOption, context.RequestAborted);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Received successful response ({StatusCode}) from {Backend} for model '{ModelNameUsed}'", response.StatusCode, selectedBackend.Name, modelNameToLog);
                    await RelayResponseAsync(context, response, isStreaming);
                    return;
                }
                else
                {
                    _logger.LogWarning("Backend {Backend} returned non-success status code: {StatusCode} for model '{ModelNameUsed}'",
                                           selectedBackend.Name, response.StatusCode, modelNameToLog);

                    if (IsRetryableError(response.StatusCode))
                    {
                        _logger.LogWarning("Retryable error ({StatusCode}) from {Backend}. Trying next backend...", response.StatusCode, selectedBackend.Name);
                        response.Dispose(); // Dispose response before retrying
                        continue; // Go to the next iteration of the while loop
                    }
                    else
                    {
                        // Non-retryable error from the backend, relay it to the client
                        _logger.LogError("Non-retryable error ({StatusCode}) from {Backend}. Failing request.", response.StatusCode, selectedBackend.Name);
                        await RelayResponseAsync(context, response, isStreaming); // Relay the error response
                        return;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP request exception when contacting {Backend} ({UpstreamUri}). Trying next backend...", selectedBackend.Name, upstreamUri);
                response?.Dispose();
                continue;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || context.RequestAborted.IsCancellationRequested)
            {
                if (context.RequestAborted.IsCancellationRequested)
                {
                    _logger.LogInformation("Client cancelled the request to {Path}", requestPath);
                }
                else // Timeout
                {
                    _logger.LogError(ex, "Timeout when contacting {Backend} ({UpstreamUri}). Trying next backend...", selectedBackend.Name, upstreamUri);
                    response?.Dispose();
                    continue; // Try next backend on timeout
                }
                // Don't continue loop if client cancelled
                return;
            }
            catch (Exception ex) // Catch broader exceptions
            {
                _logger.LogError(ex, "Unexpected error dispatching request to {Backend} ({UpstreamUri}). Trying next backend...", selectedBackend.Name, upstreamUri);
                response?.Dispose();
                continue;
            }
        }
    }

    // --- Keep IsRetryableError, RelayResponseAsync, TryExtractModelName as they were ---
    private bool IsRetryableError(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.TooManyRequests // 429
            || (int)statusCode >= 500; // 5xx errors
    }

    private async Task RelayResponseAsync(HttpContext context, HttpResponseMessage upstreamResponse, bool isStreaming)
    {
        context.Response.StatusCode = (int)upstreamResponse.StatusCode;
        foreach (var header in upstreamResponse.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }
        foreach (var header in upstreamResponse.Content.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }
        context.Response.Headers.Remove("Transfer-Encoding");

        if (isStreaming && upstreamResponse.IsSuccessStatusCode)
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Append("Connection", "keep-alive");
            _logger.LogInformation("Relaying stream response from backend.");
            using var responseStream = await upstreamResponse.Content.ReadAsStreamAsync(context.RequestAborted);
            await responseStream.CopyToAsync(context.Response.Body, context.RequestAborted);
            _logger.LogInformation("Stream relay finished.");
        }
        else
        {
            _logger.LogInformation("Relaying standard (non-stream) response from backend.");
            var responseBody = await upstreamResponse.Content.ReadAsByteArrayAsync(context.RequestAborted);
            await context.Response.Body.WriteAsync(responseBody, context.RequestAborted);
            _logger.LogInformation("Standard relay finished.");
        }
    }

    private string? TryExtractModelName(string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
            return null;
        try
        {
            var jsonNode = JsonNode.Parse(requestBody);
            if (jsonNode != null && jsonNode["model"] != null)
            {
                return jsonNode["model"]!.GetValue<string>();
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse request body as JSON to extract model name.");
        }
        return null;
    }
}
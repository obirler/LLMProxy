// Services/DispatcherService.cs
using LLMProxy.Models;
using LLMProxy.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http; // Required for EnableBuffering

namespace LLMProxy.Services;

public class DispatcherService
{
    private const string BackendClientName = "LLMBackendClient";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RoutingService _routingService;
    private readonly ILogger<DispatcherService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public DispatcherService(IHttpClientFactory httpClientFactory, RoutingService routingService, ILogger<DispatcherService> logger, IServiceScopeFactory scopeFactory)
    {
        _httpClientFactory = httpClientFactory;
        _routingService = routingService;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    private async Task LogRequestAsync(ApiLogEntry logEntry)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();
        dbContext.ApiLogEntries.Add(logEntry);
        await dbContext.SaveChangesAsync();
    }

    public async Task DispatchRequestAsync(HttpContext context)
    {
        var clientRequestPath = context.Request.Path.Value ?? string.Empty;
        var clientRequestMethod = context.Request.Method;

        context.Request.EnableBuffering();

        var logEntry = new ApiLogEntry
        {
            RequestPath = clientRequestPath,
            RequestMethod = clientRequestMethod,
            Timestamp = DateTime.UtcNow
        };

        string? backendPathSuffix = GetBackendPathSuffix(clientRequestPath);
        if (backendPathSuffix == null)
        {
            _logger.LogError("Unsupported proxy request path: {RequestPath}", clientRequestPath);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new ErrorResponse { Error = "Unsupported proxy endpoint path." });
            logEntry.ProxyResponseStatusCode = context.Response.StatusCode;
            logEntry.WasSuccess = false;
            logEntry.ErrorMessage = "Unsupported proxy endpoint path.";
            await LogRequestAsync(logEntry);
            return;
        }

        string originalRequestBody;
        // It's good practice to ensure the stream is at the beginning before reading
        context.Request.Body.Seek(0, SeekOrigin.Begin); // Or context.Request.Body.Position = 0; THIS NOW WORKS
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true)) // leaveOpen for potential re-reads if needed, though we read once
        {
            originalRequestBody = await reader.ReadToEndAsync();
        }
        // Reset stream position again if anything else in *your* code might need to re-read it from the start.
        // If only ASP.NET Core framework might re-read it (e.g. for model binding if you were using controllers with [FromBody]),
        // it often handles this itself after EnableBuffering. But being explicit is safer.
        context.Request.Body.Seek(0, SeekOrigin.Begin); // Or context.Request.Body.Position = 0;

        logEntry.ClientRequestBody = originalRequestBody;

        string? requestedModelOrGroupName = TryExtractModelName(originalRequestBody, "model");
        logEntry.RequestedModel = requestedModelOrGroupName;

        if (string.IsNullOrEmpty(requestedModelOrGroupName))
        {
            HandleBadRequest("Could not determine model name from request.", context, logEntry);
            return;
        }

        _logger.LogInformation("Initial request for model/group: '{RequestedModelOrGroupName}' on path '{Path}'",
            requestedModelOrGroupName, clientRequestPath);

        bool isStreaming = originalRequestBody.Contains("\"stream\": true");

        List<string> triedMemberModelsInGroup = new(); // Tracks member models tried if requestedModelOrGroupName is a group
        HttpResponseMessage? backendResponse = null;

        // Outer loop: Retrying different effective models (if requestedModelOrGroupName is a group)
        while (true)
        {
            var (effectiveModelName, modelConfig) = _routingService.ResolveEffectiveModelConfig(
                requestedModelOrGroupName,
                originalRequestBody,
                triedMemberModelsInGroup
            );

            if (string.IsNullOrEmpty(effectiveModelName) || modelConfig == null)
            {
                _logger.LogError("Failed to resolve an effective model for '{RequestedInitial}' after trying {TriedCount} members (if group). All options exhausted.",
                    requestedModelOrGroupName, triedMemberModelsInGroup.Count);
                SetFinalFailureResponse(context, logEntry, "All language model backends or group members are temporarily unavailable or misconfigured.");
                await LogRequestAsync(logEntry);
                return;
            }

            logEntry.EffectiveModelName = effectiveModelName; // Log the model actually being used for this attempt
            _logger.LogInformation("Attempting dispatch with effective model: '{EffectiveModelName}'", effectiveModelName);

            List<BackendConfig> triedBackendsForEffectiveModel = new();

            // Inner loop: Retrying different backends for the current effectiveModelName
            while (true)
            {
                var (selectedBackend, apiKey) = _routingService.GetNextBackendAndKeyForModel(
                    modelConfig,
                    effectiveModelName, // Pass effective model name for logging/state within RoutingService
                    triedBackendsForEffectiveModel
                );

                if (selectedBackend == null || string.IsNullOrEmpty(apiKey))
                {
                    _logger.LogWarning("All backends exhausted or no backend/key available for effective model '{EffectiveModelName}'.", effectiveModelName);
                    // This effective model has failed. Add to triedMemberModelsInGroup and break inner loop to try next member model (if group).
                    triedMemberModelsInGroup.Add(effectiveModelName);
                    break; // Breaks inner loop, outer loop will call ResolveEffectiveModelConfig again
                }

                triedBackendsForEffectiveModel.Add(selectedBackend);
                logEntry.UpstreamBackendName = selectedBackend.Name;

                // --- URI Construction ---
                if (!TryConstructUpstreamUri(selectedBackend.BaseUrl, backendPathSuffix, out Uri? upstreamUri, out string uriError))
                {
                    _logger.LogError("Skipping backend '{BackendName}' for model '{EffectiveModel}' due to URI error: {Error}", selectedBackend.Name, effectiveModelName, uriError);
                    logEntry.ErrorMessage = $"URI Error for backend {selectedBackend.Name}: {uriError}"; // Log per-attempt error
                    // Continue inner loop for next backend of this effectiveModelName
                    continue;
                }
                logEntry.UpstreamUrl = upstreamUri!.ToString();

                // --- Modify Request Body for Backend-Specific Model Name ---
                string outgoingRequestBody = originalRequestBody;
                string modelNameToLogForBackend = effectiveModelName; // Default to effective model name for logging this attempt

                if (!string.IsNullOrWhiteSpace(selectedBackend.BackendModelName))
                {
                    _logger.LogInformation("Backend '{BackendName}' specifies overriding model name to '{BackendSpecificModel}' for effective model '{EffectiveModel}'.",
                       selectedBackend.Name, selectedBackend.BackendModelName, effectiveModelName);
                    modelNameToLogForBackend = selectedBackend.BackendModelName;
                    if (!TryModifyRequestBodyModel(originalRequestBody, selectedBackend.BackendModelName, out outgoingRequestBody))
                    {
                        _logger.LogWarning("Failed to modify request body for backend-specific model name for backend {BackendName}. Sending original body for field 'model'.", selectedBackend.Name);
                        // outgoingRequestBody is already originalRequestBody if modification fails
                    }
                }
                logEntry.UpstreamRequestBody = outgoingRequestBody;

                // --- HTTP Dispatch ---
                var httpClient = _httpClientFactory.CreateClient(BackendClientName);
                using var upstreamRequest = new HttpRequestMessage(new HttpMethod(clientRequestMethod), upstreamUri)
                {
                    Content = new StringContent(outgoingRequestBody, Encoding.UTF8, "application/json")
                };
                upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                // Copy other relevant headers from context.Request.Headers if needed

                _logger.LogInformation("Dispatching to {Backend} ({UpstreamUri}) for effective model '{EffectiveModel}' (using model ID '{ModelForBackend}' for backend).",
                    selectedBackend.Name, upstreamUri, effectiveModelName, modelNameToLogForBackend);

                try
                {
                    HttpCompletionOption completionOption = isStreaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
                    backendResponse = await httpClient.SendAsync(upstreamRequest, completionOption, context.RequestAborted);
                    logEntry.UpstreamStatusCode = (int)backendResponse.StatusCode;

                    if (!isStreaming || !backendResponse.IsSuccessStatusCode)
                    {
                        logEntry.UpstreamResponseBody = await backendResponse.Content.ReadAsStringAsync(context.RequestAborted);
                        // Truncate for log if too long
                        if (logEntry.UpstreamResponseBody != null && logEntry.UpstreamResponseBody.Length > 1000)
                        {
                            logEntry.UpstreamResponseBody = logEntry.UpstreamResponseBody.Substring(0, 1000) + "... (truncated)";
                        }
                    }

                    if (backendResponse.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("Successful response ({StatusCode}) from {Backend} for effective model '{EffectiveModel}'.",
                            backendResponse.StatusCode, selectedBackend.Name, effectiveModelName);
                        await RelayResponseAsync(context, backendResponse, isStreaming);
                        logEntry.ProxyResponseStatusCode = context.Response.StatusCode;
                        logEntry.WasSuccess = true;
                        await LogRequestAsync(logEntry);
                        return; // Successful completion of the entire request
                    }
                    else // Non-success status code from backend
                    {
                        logEntry.ErrorMessage = $"Backend '{selectedBackend.Name}' Error: {backendResponse.StatusCode}. Body: {logEntry.UpstreamResponseBody ?? "N/A"}";
                        _logger.LogWarning("Backend {Backend} returned {StatusCode} for effective model '{EffectiveModel}'.",
                            selectedBackend.Name, backendResponse.StatusCode, effectiveModelName);

                        if (IsRetryableError(backendResponse.StatusCode))
                        {
                            _logger.LogInformation("Retryable error from {Backend}. Trying next backend for '{EffectiveModel}'.", selectedBackend.Name, effectiveModelName);
                            backendResponse.Dispose(); // Dispose before retrying with next backend
                            // Continue inner loop for next backend
                            continue;
                        }
                        else // Non-retryable error from this backend
                        {
                            _logger.LogError("Non-retryable error ({StatusCode}) from {Backend} for '{EffectiveModel}'. Relaying error to client.",
                                backendResponse.StatusCode, selectedBackend.Name, effectiveModelName);
                            await RelayResponseAsync(context, backendResponse, isStreaming); // Relay the specific error
                            logEntry.ProxyResponseStatusCode = context.Response.StatusCode;
                            logEntry.WasSuccess = false;
                            // ErrorMessage already set for this attempt
                            await LogRequestAsync(logEntry);
                            return; // Fail the entire request with this backend's non-retryable error
                        }
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, "HTTP request exception for {Backend} ({UpstreamUri}) for model '{EffectiveModel}'. Trying next backend.", selectedBackend.Name, upstreamUri, effectiveModelName);
                    logEntry.ErrorMessage = $"HTTP Exception for backend {selectedBackend.Name}: {ex.Message}";
                    backendResponse?.Dispose();
                    // Continue inner loop for next backend
                    continue;
                }
                catch (TaskCanceledException ex) // Handles timeouts and client cancellations
                {
                    if (context.RequestAborted.IsCancellationRequested)
                    {
                        _logger.LogInformation("Client cancelled request to {Path} while waiting for {Backend}.", clientRequestPath, selectedBackend.Name);
                        logEntry.ErrorMessage = "Client cancelled request.";
                        logEntry.WasSuccess = false;
                        logEntry.ProxyResponseStatusCode = 499; // Client Closed Request
                        await LogRequestAsync(logEntry); // Log attempt
                        backendResponse?.Dispose();
                        return;
                    }
                    else // Timeout
                    {
                        _logger.LogError(ex, "Timeout contacting {Backend} ({UpstreamUri}) for model '{EffectiveModel}'. Trying next backend.", selectedBackend.Name, upstreamUri, effectiveModelName);
                        logEntry.ErrorMessage = $"Timeout for backend {selectedBackend.Name}";
                        backendResponse?.Dispose();
                        // Continue inner loop for next backend
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error dispatching to {Backend} ({UpstreamUri}) for model '{EffectiveModel}'. Trying next backend.", selectedBackend.Name, upstreamUri, effectiveModelName);
                    logEntry.UpstreamStatusCode = null;
                    logEntry.ErrorMessage = $"Dispatch Exception for backend {selectedBackend.Name}: {ex.Message}";
                    logEntry.UpstreamResponseBody = null;
                    backendResponse?.Dispose();
                    // Continue inner loop for next backend
                    continue;
                }
                // Ensure backendResponse is disposed if loop continues without returning
                finally { backendResponse?.Dispose(); }
            } // End inner loop (backend retries for effectiveModelName)

            // If we reach here, it means all backends for the current 'effectiveModelName' failed with retryable errors.
            // The outer loop will now try to resolve the next 'effectiveModelName' if the initial request was a group.
            _logger.LogWarning("All backends for effective model '{EffectiveModelName}' failed. Will try next group member if applicable.", effectiveModelName);
        } // End outer loop (group member retries)
    }

    // Helper methods (IsRetryableError, RelayResponseAsync, TryExtractModelName, etc.)

    private string? GetBackendPathSuffix(string requestPath)
    {
        if (requestPath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return "chat/completions";
        if (requestPath.EndsWith("/completions", StringComparison.OrdinalIgnoreCase))
            return "completions";
        if (requestPath.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
            return "embeddings";
        return null;
    }

    private bool TryConstructUpstreamUri(string configuredBaseUrl, string backendPathSuffix, out Uri? upstreamUri, out string errorMessage)
    {
        upstreamUri = null;
        errorMessage = string.Empty;

        if (string.IsNullOrEmpty(configuredBaseUrl))
        {
            errorMessage = "BaseUrl is empty or null.";
            return false;
        }

        string baseUriStringForCombination = configuredBaseUrl;
        if (!baseUriStringForCombination.EndsWith('/'))
        {
            baseUriStringForCombination += "/";
        }

        if (!Uri.TryCreate(baseUriStringForCombination, UriKind.Absolute, out var baseUri))
        {
            errorMessage = $"Invalid BaseUrl format: {configuredBaseUrl}";
            return false;
        }

        try
        {
            upstreamUri = new Uri(baseUri, backendPathSuffix);
            return true;
        }
        catch (UriFormatException ex)
        {
            errorMessage = $"Could not create upstream URI from BaseUrl '{configuredBaseUrl}' and suffix '{backendPathSuffix}': {ex.Message}";
            return false;
        }
    }

    private bool TryModifyRequestBodyModel(string originalBody, string newModelName, out string modifiedBody)
    {
        modifiedBody = originalBody;
        try
        {
            var jsonNode = JsonNode.Parse(originalBody);
            if (jsonNode is JsonObject jsonObject && jsonObject.ContainsKey("model"))
            {
                jsonObject["model"] = newModelName;
                modifiedBody = jsonObject.ToJsonString();
                return true;
            }
            else if (jsonNode is JsonObject jsonObjectForEmbed) // For /embeddings, model field might be at top level
            {
                // OpenAI embeddings take "model" and "input". Some other providers might vary.
                // If it's an embedding request and we're overriding, we assume the 'model' field is what needs changing.
                // This might need more specific handling if embedding request structures vary significantly.
                if (jsonObjectForEmbed.ContainsKey("model"))
                {
                    jsonObjectForEmbed["model"] = newModelName;
                    modifiedBody = jsonObjectForEmbed.ToJsonString();
                    return true;
                }
            }
            _logger.LogWarning("Could not find 'model' property in request body JSON to apply backend-specific model name, or JSON structure not recognized for override.");
        }
        catch (JsonException jsonEx)
        {
            _logger.LogError(jsonEx, "Failed to parse or modify request body JSON for backend-specific model name override.");
        }
        return false;
    }

    private void HandleBadRequest(string message, HttpContext context, ApiLogEntry logEntry)
    {
        _logger.LogWarning(message);
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        // Using WriteAsJsonAsync will set content type and write. No need to await here as it's the end of response.
        context.Response.WriteAsJsonAsync(new ErrorResponse { Error = message });
        logEntry.ProxyResponseStatusCode = context.Response.StatusCode;
        logEntry.WasSuccess = false;
        logEntry.ErrorMessage = message;
        // LogRequestAsync is fire-and-forget or should be awaited if critical before returning
        _ = LogRequestAsync(logEntry);
    }

    private void SetFinalFailureResponse(HttpContext context, ApiLogEntry logEntry, string errorMessage)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        // Using WriteAsJsonAsync.
        context.Response.WriteAsJsonAsync(new ErrorResponse { Error = errorMessage });
        logEntry.ProxyResponseStatusCode = context.Response.StatusCode;
        logEntry.WasSuccess = false;
        logEntry.ErrorMessage = errorMessage;
    }

    private bool IsRetryableError(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.TooManyRequests // 429
            || (int)statusCode >= 500; // 5xx errors
    }

    private async Task RelayResponseAsync(HttpContext context, HttpResponseMessage upstreamResponse, bool isStreaming)
    {
        context.Response.StatusCode = (int)upstreamResponse.StatusCode;

        // Define well-known header names as strings
        const string ContentLengthHeader = "Content-Length";
        const string TransferEncodingHeader = "Transfer-Encoding";
        const string ContentTypeHeader = "Content-Type"; // For setting response content type
        const string CacheControlHeader = "Cache-Control"; // For SSE
        const string ConnectionHeader = "Connection"; // For SSE

        foreach (var header in upstreamResponse.Headers)
        {
            // Don't copy Content-Length for streams from upstream, as chunked encoding will be used.
            if (ContentLengthHeader.Equals(header.Key, StringComparison.OrdinalIgnoreCase) && isStreaming)
                continue;
            // Don't copy Transfer-Encoding as the proxy will handle its own transfer or ASP.NET Core will.
            if (TransferEncodingHeader.Equals(header.Key, StringComparison.OrdinalIgnoreCase))
                continue;

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in upstreamResponse.Content.Headers) // Copy content headers (like Content-Type)
        {
            // Again, don't copy Content-Length for streams.
            if (ContentLengthHeader.Equals(header.Key, StringComparison.OrdinalIgnoreCase) && isStreaming)
                continue;
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        // Explicitly remove problematic headers if they snuck in or were set by default by Kestrel/ASP.NET Core
        context.Response.Headers.Remove(TransferEncodingHeader);
        if (isStreaming)
        {
            context.Response.Headers.Remove(ContentLengthHeader);
        }

        if (isStreaming && upstreamResponse.IsSuccessStatusCode)
        {
            // Set SSE specific headers
            context.Response.Headers[ContentTypeHeader] = "text/event-stream; charset=utf-8";
            context.Response.Headers[CacheControlHeader] = "no-cache";
            context.Response.Headers[ConnectionHeader] = "keep-alive";

            _logger.LogInformation("Relaying stream response from backend.");
            using var responseStream = await upstreamResponse.Content.ReadAsStreamAsync(context.RequestAborted);
            await responseStream.CopyToAsync(context.Response.Body, context.RequestAborted);
            _logger.LogInformation("Stream relay finished.");
        }
        else
        {
            _logger.LogInformation("Relaying standard (non-stream) response from backend.");
            var responseBodyBytes = await upstreamResponse.Content.ReadAsByteArrayAsync(context.RequestAborted);

            // For non-streaming, Content-Length might be important if not already set by ASP.NET Core
            // and if the upstream response had it.
            if (!isStreaming &&
                !context.Response.Headers.ContainsKey(ContentLengthHeader) &&
                upstreamResponse.Content.Headers.ContentLength.HasValue)
            {
                context.Response.Headers.ContentLength = upstreamResponse.Content.Headers.ContentLength.Value;
            }
            await context.Response.Body.WriteAsync(responseBodyBytes, context.RequestAborted);
            _logger.LogInformation("Standard relay finished.");
        }
    }

    private string? TryExtractModelName(string requestBody, string fieldName = "model")
    {
        if (string.IsNullOrWhiteSpace(requestBody))
            return null;
        try
        {
            var jsonNode = JsonNode.Parse(requestBody);
            if (jsonNode != null && jsonNode[fieldName] != null)
            {
                return jsonNode[fieldName]!.GetValue<string>();
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse request body as JSON to extract '{Field}' name.", fieldName);
        }
        return null;
    }
}

// Add new field to ApiLogEntry.cs
// Models/ApiLogEntry.cs
// ... (existing fields) ...
// public string? RequestedModel { get; set; } // General model requested by client (already exists)
// public string? EffectiveModelName { get; set; } // The actual model used after group resolution (NEW)
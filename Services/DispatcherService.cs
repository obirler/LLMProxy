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
    #region Constructors

    public DispatcherService(IHttpClientFactory httpClientFactory, RoutingService routingService, ILogger<DispatcherService> logger, IServiceScopeFactory scopeFactory)
    {
        _httpClientFactory = httpClientFactory;
        _routingService = routingService;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    #endregion Constructors

    #region Fields

    private const string BackendClientName = "LLMBackendClient";

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly RoutingService _routingService;

    private readonly ILogger<DispatcherService> _logger;

    private readonly IServiceScopeFactory _scopeFactory;

    #endregion Fields

    #region Methods

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

                HttpResponseMessage? currentBackendResponse = null; // Renamed from backendResponse to avoid conflict
                try
                {
                    HttpCompletionOption completionOption = isStreaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
                    currentBackendResponse = await httpClient.SendAsync(upstreamRequest, completionOption, context.RequestAborted);

                    logEntry.UpstreamStatusCode = (int)currentBackendResponse.StatusCode;

                    // The full response body will be captured by RelayResponseAsync.
                    // We will set logEntry.UpstreamResponseBody after calling it.
                    // No need to read currentBackendResponse.Content here for logging the body anymore.

                    if (currentBackendResponse.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("Successful status ({StatusCode}) from {Backend} for effective model '{EffectiveModel}'. Relaying and checking content...",
                            currentBackendResponse.StatusCode, selectedBackend.Name, effectiveModelName);

                        var (relayCompleted, internalErrorInContent, relayedBody) = await RelayResponseAsync(context, currentBackendResponse, isStreaming, selectedBackend.Name, _logger);

                        // Log the full relayed body (streamed or non-streamed)
                        if (relayedBody != null)
                        {
                            logEntry.UpstreamResponseBody = relayedBody.Length > 200000 ? relayedBody.Substring(0, 200000) + "... (truncated for log)" : relayedBody; // Increased truncation limit
                        }

                        if (!relayCompleted)
                        {
                            _logger.LogWarning("Relay did not complete for backend {BackendName}. Client might have disconnected.", selectedBackend.Name);
                            logEntry.ErrorMessage = $"Relay failed or was cancelled for backend {selectedBackend.Name}.";
                            logEntry.WasSuccess = false;
                            await LogRequestAsync(logEntry);
                            currentBackendResponse.Dispose();
                            return;
                        }

                        if (internalErrorInContent)
                        {
                            _logger.LogWarning("Backend {Backend} ({UpstreamUri}) returned {StatusCode} OK but an application-level error was detected in the content. Treating as retryable for backend selection.",
                                selectedBackend.Name, upstreamUri, currentBackendResponse.StatusCode);
                            logEntry.ErrorMessage = $"Backend '{selectedBackend.Name}' (URI: {upstreamUri}) returned {currentBackendResponse.StatusCode} OK with internal error (content indicates failure). Body: {logEntry.UpstreamResponseBody ?? "N/A"}";
                            logEntry.WasSuccess = false;
                            currentBackendResponse.Dispose();
                            continue;
                        }
                        else
                        {
                            logEntry.WasSuccess = true;
                            logEntry.ErrorMessage = null;
                            await LogRequestAsync(logEntry);
                            currentBackendResponse.Dispose();
                            return;
                        }
                    }
                    else // Non-success status code from backend (e.g., 4xx, 5xx from backend directly)
                    {
                        // For direct errors from backend, read the content here for logging
                        if (currentBackendResponse.Content != null)
                        {
                            var errorBody = await currentBackendResponse.Content.ReadAsStringAsync(context.RequestAborted);
                            logEntry.UpstreamResponseBody = errorBody.Length > 1000 ? errorBody.Substring(0, 1000) + "... (truncated)" : errorBody;
                        }

                        logEntry.ErrorMessage = $"Backend '{selectedBackend.Name}' Error: {currentBackendResponse.StatusCode}. Body: {logEntry.UpstreamResponseBody ?? "N/A"}";
                        _logger.LogWarning("Backend {Backend} returned {StatusCode} for effective model '{EffectiveModel}'. Upstream Body: {UpstreamBody}",
                            selectedBackend.Name, currentBackendResponse.StatusCode, effectiveModelName, logEntry.UpstreamResponseBody ?? "N/A");

                        if (IsRetryableError(currentBackendResponse.StatusCode))
                        {
                            _logger.LogInformation("Retryable error from {Backend}. Trying next backend for '{EffectiveModel}'.", selectedBackend.Name, effectiveModelName);
                            currentBackendResponse.Dispose();
                            continue;
                        }
                        else
                        {
                            _logger.LogError("Non-retryable error ({StatusCode}) from {Backend} for '{EffectiveModel}'. Relaying error to client.",
                                currentBackendResponse.StatusCode, selectedBackend.Name, effectiveModelName);

                            // Relay the original error response from the backend.
                            // The body for RelayResponseAsync will be read from currentBackendResponse again.
                            // We pass false for isStreaming as this is a direct error relay.
                            // The relayedBody from this call isn't strictly needed for the logEntry here as we've already captured it.
                            await RelayResponseAsync(context, currentBackendResponse, false, selectedBackend.Name, _logger);

                            logEntry.ProxyResponseStatusCode = context.Response.StatusCode;
                            logEntry.WasSuccess = false;
                            await LogRequestAsync(logEntry);
                            currentBackendResponse.Dispose();
                            return;
                        }
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, "HTTP request exception for {Backend} ({UpstreamUri}) for model '{EffectiveModel}'. Trying next backend.", selectedBackend.Name, upstreamUri, effectiveModelName);
                    logEntry.ErrorMessage = $"HTTP Exception for backend {selectedBackend.Name}: {ex.Message}";
                    currentBackendResponse?.Dispose();
                    continue; // Continue inner loop for next backend
                }
                catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || context.RequestAborted.IsCancellationRequested) // Catches timeouts and client cancellations
                {
                    if (context.RequestAborted.IsCancellationRequested)
                    {
                        _logger.LogInformation("Client cancelled request to {Path} while waiting for {Backend}.", clientRequestPath, selectedBackend.Name);
                        logEntry.ErrorMessage = "Client cancelled request.";
                        logEntry.WasSuccess = false;
                        logEntry.ProxyResponseStatusCode = 499; // Client Closed Request
                                                                // Don't try to write to response if client closed it.
                        currentBackendResponse?.Dispose();
                        // Log the attempt, but don't try to send response to client.
                        // Use a separate task to avoid issues if the request is fully aborted.
                        _ = LogRequestAsync(logEntry);
                        return;
                    }
                    else // Timeout
                    {
                        _logger.LogError(ex, "Timeout contacting {Backend} ({UpstreamUri}) for model '{EffectiveModel}'. Trying next backend.", selectedBackend.Name, upstreamUri, effectiveModelName);
                        logEntry.ErrorMessage = $"Timeout for backend {selectedBackend.Name}";
                        currentBackendResponse?.Dispose();
                        continue; // Continue inner loop for next backend on timeout
                    }
                }
                catch (Exception ex) // Catch broader exceptions during dispatch
                {
                    _logger.LogError(ex, "Unexpected error dispatching to {Backend} ({UpstreamUri}) for model '{EffectiveModel}'. Trying next backend.", selectedBackend.Name, upstreamUri, effectiveModelName);
                    logEntry.UpstreamStatusCode = null; // No HTTP response received or processed
                    logEntry.ErrorMessage = $"Dispatch Exception for backend {selectedBackend.Name}: {ex.Message}";
                    logEntry.UpstreamResponseBody = null;
                    currentBackendResponse?.Dispose();
                    continue; // Try next backend
                }
                finally
                {
                    // Ensure the response is disposed if not already handled by a return/continue path that disposes it.
                    // However, most paths above now explicitly dispose. This is a safeguard.
                    currentBackendResponse?.Dispose();
                }
            } // End inner loop (backend retries for effectiveModelName)

            // If we reach here, it means all backends for the current 'effectiveModelName' failed with retryable errors.
            // The outer loop will now try to resolve the next 'effectiveModelName' if the initial request was a group.
            _logger.LogWarning("All backends for effective model '{EffectiveModelName}' failed. Will try next group member if applicable.", effectiveModelName);
        } // End outer loop (group member retries)
    }

    private async Task LogRequestAsync(ApiLogEntry logEntry)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();
        dbContext.ApiLogEntries.Add(logEntry);
        await dbContext.SaveChangesAsync();
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

    private async Task<(bool relayCompletedSuccessfully, bool internalErrorDetectedInContent, string? fullResponseBody)> RelayResponseAsync(
    HttpContext context,
    HttpResponseMessage upstreamResponse,
    bool isStreaming,
    string backendNameForErrorCheck, // For logging within ContainsApplicationLevelError
    ILogger loggerForErrorCheck) // Pass logger for ContainsApplicationLevelError
    {
        bool internalErrorDetectedInContent = false;
        bool relayCompletedSuccessfully = false;
        string? fullResponseBody = null; // To store the complete response body

        try
        {
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;

            // ... (header copying logic remains the same) ...
            const string ContentLengthHeader = "Content-Length";
            const string TransferEncodingHeader = "Transfer-Encoding";
            const string ContentTypeHeader = "Content-Type";
            const string CacheControlHeader = "Cache-Control";
            const string ConnectionHeader = "Connection";

            foreach (var header in upstreamResponse.Headers)
            {
                if (ContentLengthHeader.Equals(header.Key, StringComparison.OrdinalIgnoreCase) && isStreaming)
                    continue;
                if (TransferEncodingHeader.Equals(header.Key, StringComparison.OrdinalIgnoreCase))
                    continue;
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            foreach (var header in upstreamResponse.Content.Headers)
            {
                if (ContentLengthHeader.Equals(header.Key, StringComparison.OrdinalIgnoreCase) && isStreaming)
                    continue;
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            context.Response.Headers.Remove(TransferEncodingHeader);
            if (isStreaming)
                context.Response.Headers.Remove(ContentLengthHeader);

            if (isStreaming && upstreamResponse.IsSuccessStatusCode)
            {
                context.Response.Headers[ContentTypeHeader] = "text/event-stream; charset=utf-8";
                context.Response.Headers[CacheControlHeader] = "no-cache";
                context.Response.Headers[ConnectionHeader] = "keep-alive";

                loggerForErrorCheck.LogInformation("Relaying stream response from backend {BackendName}.", backendNameForErrorCheck);

                using var responseStream = await upstreamResponse.Content.ReadAsStreamAsync(context.RequestAborted);
                using var memoryBufferForInspection = new MemoryStream();

                byte[] buffer = new byte[81920]; // 80KB buffer
                int bytesRead;

                while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, context.RequestAborted)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer, 0, bytesRead, context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    await memoryBufferForInspection.WriteAsync(buffer, 0, bytesRead, context.RequestAborted);
                }
                loggerForErrorCheck.LogInformation("Stream relay finished for backend {BackendName}.", backendNameForErrorCheck);
                relayCompletedSuccessfully = true;

                memoryBufferForInspection.Position = 0;
                using var streamReader = new StreamReader(memoryBufferForInspection, Encoding.UTF8);
                fullResponseBody = await streamReader.ReadToEndAsync(); // Capture full streamed response
                if (ContainsApplicationLevelError(fullResponseBody, backendNameForErrorCheck, loggerForErrorCheck))
                {
                    internalErrorDetectedInContent = true;
                }
            }
            else // Non-streaming or non-success status code from upstream
            {
                loggerForErrorCheck.LogInformation("Relaying standard (non-stream) response from backend {BackendName}.", backendNameForErrorCheck);
                var responseBodyBytes = await upstreamResponse.Content.ReadAsByteArrayAsync(context.RequestAborted);
                fullResponseBody = Encoding.UTF8.GetString(responseBodyBytes); // Capture full non-streamed response

                if (!isStreaming && !context.Response.Headers.ContainsKey(ContentLengthHeader) && upstreamResponse.Content.Headers.ContentLength.HasValue)
                {
                    context.Response.Headers.ContentLength = upstreamResponse.Content.Headers.ContentLength.Value;
                }
                await context.Response.Body.WriteAsync(responseBodyBytes, context.RequestAborted);
                loggerForErrorCheck.LogInformation("Standard relay finished for backend {BackendName}.", backendNameForErrorCheck);
                relayCompletedSuccessfully = true;

                if (upstreamResponse.IsSuccessStatusCode)
                {
                    if (ContainsApplicationLevelError(fullResponseBody, backendNameForErrorCheck, loggerForErrorCheck))
                    {
                        internalErrorDetectedInContent = true;
                    }
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            loggerForErrorCheck.LogInformation(ex, "Relay operation cancelled for backend {BackendName}. Client likely disconnected.", backendNameForErrorCheck);
            relayCompletedSuccessfully = false;
        }
        catch (Exception ex)
        {
            loggerForErrorCheck.LogError(ex, "Error during relay from backend {BackendName}.", backendNameForErrorCheck);
            relayCompletedSuccessfully = false;
        }
        return (relayCompletedSuccessfully, internalErrorDetectedInContent, fullResponseBody);
    }

    // Add this new helper method to DispatcherService
    private bool ContainsApplicationLevelError(string responseContent, string backendName, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return false;
        }

        // Normalize for easier checking, but be mindful if case sensitivity is needed for some errors.
        // For now, ToLowerInvariant is a reasonable approach for broad checks.
        string lowerResponse = responseContent.ToLowerInvariant(); // Do this once

        // 1. Check for OpenAI/OpenRouter style JSON error object at the root
        //    Example: { "error": { "message": "...", "code": ... } }
        if (lowerResponse.Contains("\"error\":")) // Quick check before parsing
        {
            try
            {
                var jsonNode = JsonNode.Parse(responseContent); // Parse original content for case sensitivity if needed by structure
                if (jsonNode?["error"] is JsonObject errorObject)
                {
                    // You can be more specific here, e.g., check errorObject["code"] or specific messages.
                    // For instance, some 400 errors are non-retryable from the proxy's perspective (bad client request),
                    // but a 400 error like "context_length_exceeded" from the backend *is* something we might want the proxy to retry with another backend.
                    // The user's example had a "code: 400" inside a 200 OK response's error object.
                    var messageNode = errorObject["message"];
                    string errorMessage = messageNode?.GetValue<string>()?.ToLowerInvariant() ?? "";

                    logger.LogWarning("Detected JSON error structure in response from backend {BackendName}. Message: {ErrorMessage}", backendName, errorMessage);

                    if (errorMessage.Contains("maximum context length") || errorMessage.Contains("tokens. however, you requested"))
                    {
                        return true; // Specific known retryable error message
                    }
                    // Add other specific message checks from JSON errors if needed.
                    // If it's a generic JSON error and we're not sure, we might decide to retry.
                    // For now, any "error" object in a 200 OK is suspicious.
                    return true;
                }
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Tried to parse response from {BackendName} as JSON for error check but failed. Content might not be JSON or malformed.", backendName);
            }
        }

        // 2. Check for known error substrings in the general response (could be HTML, plain text, or non-root JSON error)
        //    These should ideally be configurable.
        var knownErrorSubstrings = new List<string>
    {
        "maximum context length",       // General phrase
        "tokens. however, you requested", // From user's example, more specific
        "context_length_exceeded",      // Common error code as text
        "model_not_found",              // Some APIs might return this in a 200 OK with an error object/text
        "insufficient_quota",           // Could be a 200 OK with an error message
        "input validation error"        // General validation error
    };

        foreach (var sub in knownErrorSubstrings)
        {
            if (lowerResponse.Contains(sub))
            {
                logger.LogWarning("Detected known error substring '{Substring}' in response from backend {BackendName}.", sub, backendName);
                return true;
            }
        }

        // 3. Anthropic specific error in stream (often comes as a separate JSON object if streaming)
        // Example: {"type": "error", "error": {"type": "overloaded_error", "message": "Anthropic's API is temporarily overloaded"}}
        if (lowerResponse.Contains("\"type\":\"error\"") && lowerResponse.Contains("\"error\":"))
        { // Quick check
            try
            {
                // This might need to handle multiple JSON objects in a stream if not already parsed as a single one.
                // For simplicity, if the string contains this pattern, assume it's an error.
                // A more robust solution would parse line by line if it's a stream of JSON objects.
                var jsonNode = JsonNode.Parse(responseContent);
                if (jsonNode?["type"]?.GetValue<string>() == "error" && jsonNode?["error"] is JsonObject)
                {
                    logger.LogWarning("Detected Anthropic-style error structure in response from backend {BackendName}.", backendName);
                    return true;
                }
            }
            catch (JsonException) { /* Ignore if not parseable as a single JSON object */ }
        }

        return false;
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

    #endregion Methods
}
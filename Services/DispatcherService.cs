using LLMProxy.Models;
using LLMProxy.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using System.Diagnostics; // For Stopwatch

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
            errorMessage = "BaseUrl is empty.";
            return false;
        }
        string baseUriStr = configuredBaseUrl.EndsWith('/') ? configuredBaseUrl : configuredBaseUrl + "/";
        if (!Uri.TryCreate(baseUriStr, UriKind.Absolute, out var baseUri))
        {
            errorMessage = $"Invalid BaseUrl format: {configuredBaseUrl}";
            return false;
        }
        try
        {
            upstreamUri = new Uri(baseUri, backendPathSuffix);
            return true;
        }
        catch (UriFormatException ex) { errorMessage = $"URI creation error: {ex.Message}"; return false; }
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
            _logger.LogWarning("Could not find 'model' property in request body JSON to apply backend-specific model name.");
        }
        catch (JsonException jsonEx) { _logger.LogError(jsonEx, "Failed to parse/modify request body for backend-specific model name."); }
        return false;
    }

    private JsonNode? TryParseJson(string jsonString)
    {
        try
        {
            return JsonNode.Parse(jsonString);
        }
        catch { return null; }
    }

    private void HandleBadRequest(string message, HttpContext context, ApiLogEntry logEntry)
    {
        _logger.LogWarning(message);
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.WriteAsJsonAsync(new ErrorResponse { Error = message });
        logEntry.ProxyResponseStatusCode = context.Response.StatusCode;
        logEntry.WasSuccess = false;
        logEntry.ErrorMessage = message;
        _ = LogRequestAsync(logEntry); // Fire and forget for logging
    }

    private void SetFinalFailureResponse(HttpContext context, ApiLogEntry logEntry, string errorMessage)
    {
        _logger.LogError("Final failure for request: {ErrorMessage}", errorMessage);
        if (!context.Response.HasStarted) // Important: Only set status if headers not sent
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.WriteAsJsonAsync(new ErrorResponse { Error = errorMessage });
        }
        logEntry.ProxyResponseStatusCode = context.Response.StatusCode; // Log what was actually set or attempted
        logEntry.WasSuccess = false;
        logEntry.ErrorMessage = errorMessage;
    }

    private bool IsRetryableError(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private bool ContainsApplicationLevelError(string responseContent, string backendName, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(responseContent))
            return false;
        string lowerResponse = responseContent.ToLowerInvariant();
        if (lowerResponse.Contains("\"error\":"))
        {
            try
            {
                var jsonNode = JsonNode.Parse(responseContent);
                if (jsonNode?["error"] is JsonObject errorObject)
                {
                    var messageNode = errorObject["message"];
                    string errorMessage = messageNode?.GetValue<string>()?.ToLowerInvariant() ?? "";
                    logger.LogWarning("Detected JSON error structure from {BackendName}. Message: {ErrorMessage}", backendName, errorMessage);
                    if (errorMessage.Contains("maximum context length") || errorMessage.Contains("tokens. however, you requested"))
                        return true;
                    return true; // Generic JSON error in 200 OK is suspicious
                }
            }
            catch (JsonException ex) { logger.LogDebug(ex, "JSON error check parse failed for {BackendName}.", backendName); }
        }
        var knownErrorSubstrings = new List<string> { "maximum context length", "tokens. however, you requested", "context_length_exceeded", "model_not_found", "insufficient_quota", "input validation error" };
        if (knownErrorSubstrings.Any(sub => lowerResponse.Contains(sub)))
        {
            logger.LogWarning("Detected known error substring in response from {BackendName}.", backendName);
            return true;
        }
        if (lowerResponse.Contains("\"type\":\"error\"") && lowerResponse.Contains("\"error\":"))
        {
            try
            {
                var jsonNode = JsonNode.Parse(responseContent);
                if (jsonNode?["type"]?.GetValue<string>() == "error" && jsonNode?["error"] is JsonObject)
                {
                    logger.LogWarning("Detected Anthropic-style error from {BackendName}.", backendName);
                    return true;
                }
            }
            catch (JsonException) { /* ignore */ }
        }
        return false;
    }

    private async Task<(bool relayCompletedSuccessfully, bool internalErrorDetectedInContent, string? fullResponseBody)>
        RelayResponseAsync(HttpContext context, HttpResponseMessage upstreamResponse, bool isStreaming, string backendNameForErrorCheck, ILogger loggerForErrorCheck)
    {
        bool internalErrorDetectedInContent = false;
        bool relayCompletedSuccessfully = false;
        string? fullResponseBody = null;
        try
        {
            if (context.Response.HasStarted && context.Response.StatusCode != (int)upstreamResponse.StatusCode)
            {
                loggerForErrorCheck.LogWarning("RelayResponseAsync: Response has already started with status {ExistingStatus}, cannot change to {NewStatus} for backend {BackendName}.",
                   context.Response.StatusCode, (int)upstreamResponse.StatusCode, backendNameForErrorCheck);
            }
            else if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            }

            const string ContentLengthHeader = "Content-Length", TransferEncodingHeader = "Transfer-Encoding", ContentTypeHeader = "Content-Type", CacheControlHeader = "Cache-Control", ConnectionHeader = "Connection";
            foreach (var header in upstreamResponse.Headers)
            {
                if ((ContentLengthHeader.Equals(header.Key, StringComparison.OrdinalIgnoreCase) && isStreaming) || TransferEncodingHeader.Equals(header.Key, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!context.Response.HasStarted)
                    context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            foreach (var header in upstreamResponse.Content.Headers)
            {
                if (ContentLengthHeader.Equals(header.Key, StringComparison.OrdinalIgnoreCase) && isStreaming)
                    continue;
                if (!context.Response.HasStarted)
                    context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            if (!context.Response.HasStarted)
            {
                context.Response.Headers.Remove(TransferEncodingHeader);
                if (isStreaming)
                    context.Response.Headers.Remove(ContentLengthHeader);
            }

            if (isStreaming && upstreamResponse.IsSuccessStatusCode)
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.Headers[ContentTypeHeader] = "text/event-stream; charset=utf-8";
                    context.Response.Headers[CacheControlHeader] = "no-cache";
                    context.Response.Headers[ConnectionHeader] = "keep-alive";
                }
                loggerForErrorCheck.LogInformation("Relaying stream from {BackendName}.", backendNameForErrorCheck);
                using var responseStream = await upstreamResponse.Content.ReadAsStreamAsync(context.RequestAborted);
                using var memoryBuffer = new MemoryStream();
                byte[] buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, context.RequestAborted)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer, 0, bytesRead, context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    await memoryBuffer.WriteAsync(buffer, 0, bytesRead, context.RequestAborted);
                }
                relayCompletedSuccessfully = true;
                memoryBuffer.Position = 0;
                using var sr = new StreamReader(memoryBuffer, Encoding.UTF8);
                fullResponseBody = await sr.ReadToEndAsync();
                if (ContainsApplicationLevelError(fullResponseBody, backendNameForErrorCheck, loggerForErrorCheck))
                    internalErrorDetectedInContent = true;
            }
            else
            {
                var responseBytes = await upstreamResponse.Content.ReadAsByteArrayAsync(context.RequestAborted);
                fullResponseBody = Encoding.UTF8.GetString(responseBytes);
                if (!isStreaming && !context.Response.HasStarted && !context.Response.Headers.ContainsKey(ContentLengthHeader) && upstreamResponse.Content.Headers.ContentLength.HasValue)
                {
                    context.Response.Headers.ContentLength = upstreamResponse.Content.Headers.ContentLength.Value;
                }
                await context.Response.Body.WriteAsync(responseBytes, context.RequestAborted);
                relayCompletedSuccessfully = true;
                if (upstreamResponse.IsSuccessStatusCode && ContainsApplicationLevelError(fullResponseBody, backendNameForErrorCheck, loggerForErrorCheck))
                    internalErrorDetectedInContent = true;
            }
        }
        catch (OperationCanceledException ex) { loggerForErrorCheck.LogInformation(ex, "Relay cancelled for {BackendName}.", backendNameForErrorCheck); relayCompletedSuccessfully = false; }
        catch (Exception ex) { loggerForErrorCheck.LogError(ex, "Error during relay from {BackendName}.", backendNameForErrorCheck); relayCompletedSuccessfully = false; }
        return (relayCompletedSuccessfully, internalErrorDetectedInContent, fullResponseBody);
    }

    private string? TryExtractModelName(string requestBody, string fieldName = "model")
    {
        if (string.IsNullOrWhiteSpace(requestBody))
            return null;
        try
        {
            var jsonNode = JsonNode.Parse(requestBody);
            return jsonNode?[fieldName]?.GetValue<string>();
        }
        catch (JsonException ex) { _logger.LogWarning(ex, "Failed to parse JSON to extract '{Field}'.", fieldName); }
        return null;
    }

    /// <summary>
    /// Executes a request to a single, specific model, handling its backend selection and retries.
    /// This is used internally for MoA agent/orchestrator calls.
    /// It always attempts to get the full response body.
    /// </summary>
    private async Task<(bool success, string? responseBody, int? statusCode, string? errorMessage, string? finalBackendName)>
        ExecuteSingleModelRequestAsync(
            string modelId,                         // The logical model ID (e.g., "gpt-4-proxy", "agent-phi3")
            string baseRequestBodyPayload,          // The minimal JSON payload (e.g., just messages for an agent/orchestrator)
            bool isCallActuallyStreaming,           // Whether this specific backend call should be streaming
            string originalRequestPath,             // e.g., "/v1/chat/completions (the path client called)
            string originalRequestMethod,           // e.g., "POST"
                                                    // ApiLogEntry mainLogEntryForContext,  // REMOVED
            JsonNode? originalClientJsonNodeForParams) // Parsed JSON of the *original client request* for passthrough params
    {
        var modelStopwatch = Stopwatch.StartNew();
        _logger.LogInformation("[SubCall] Executing for model ID: {ModelId}. Streaming for this call: {IsCallStreaming}", modelId, isCallActuallyStreaming);

        // Create a NEW ApiLogEntry for this specific sub-call
        var subCallLogEntry = new ApiLogEntry
        {
            Timestamp = DateTime.UtcNow,
            RequestPath = originalRequestPath, // Log the original client path that triggered this sub-call
            RequestMethod = originalRequestMethod,
            RequestedModel = modelId, // This is the "requested model" for this specific sub-call
            EffectiveModelName = modelId, // Initially, effective is same as requested
                                          // ClientRequestBody for sub-calls can be a summary or the actual payload sent to this specific model
            ClientRequestBody = $"[Internal Sub-Call to '{modelId}'] Base Payload (first 500 chars): {baseRequestBodyPayload.Substring(0, Math.Min(baseRequestBodyPayload.Length, 500))}..."
        };

        if (!_routingService.TryGetModelRouting(modelId, out ModelRoutingConfig? modelConfig) || modelConfig == null)
        {
            _logger.LogError("[SubCall] No model configuration found for model ID: {ModelId}.", modelId);
            subCallLogEntry.WasSuccess = false;
            subCallLogEntry.ErrorMessage = $"No model configuration for '{modelId}'";
            subCallLogEntry.ProxyResponseStatusCode = StatusCodes.Status500InternalServerError;
            await LogRequestAsync(subCallLogEntry);
            return (false, null, null, subCallLogEntry.ErrorMessage, null);
        }

        string? backendPathSuffix = GetBackendPathSuffix(originalRequestPath);
        if (backendPathSuffix == null)
        {
            _logger.LogError("[SubCall] Invalid backend path suffix for request path {OriginalRequestPath} with model ID {ModelId}.", originalRequestPath, modelId);
            subCallLogEntry.WasSuccess = false;
            subCallLogEntry.ErrorMessage = "Internal error: Invalid backend path for sub-call";
            subCallLogEntry.ProxyResponseStatusCode = StatusCodes.Status500InternalServerError;
            await LogRequestAsync(subCallLogEntry);
            return (false, null, null, subCallLogEntry.ErrorMessage, null);
        }

        List<BackendConfig> triedBackends = new();
        HttpResponseMessage? backendResponse = null;
        string? lastAttemptedBackendName = null;

        while (true) // Backend retry loop for this modelId
        {
            var (selectedBackend, apiKey) = _routingService.GetNextBackendAndKeyForModel(modelConfig, modelId, triedBackends);
            if (selectedBackend == null || string.IsNullOrEmpty(apiKey))
            {
                _logger.LogWarning("[SubCall] All backends exhausted for model ID {ModelId}. Last tried: {LastBackend}", modelId, lastAttemptedBackendName ?? "None");
                subCallLogEntry.WasSuccess = false;
                subCallLogEntry.ErrorMessage = $"All backends failed for model '{modelId}'";
                subCallLogEntry.ProxyResponseStatusCode = StatusCodes.Status503ServiceUnavailable;
                subCallLogEntry.UpstreamBackendName = lastAttemptedBackendName;
                await LogRequestAsync(subCallLogEntry);
                return (false, null, null, subCallLogEntry.ErrorMessage, lastAttemptedBackendName);
            }
            triedBackends.Add(selectedBackend);
            lastAttemptedBackendName = selectedBackend.Name;
            string currentBackendName = selectedBackend.Name;

            subCallLogEntry.UpstreamBackendName = currentBackendName;

            if (!TryConstructUpstreamUri(selectedBackend.BaseUrl, backendPathSuffix, out Uri? upstreamUri, out string uriError))
            {
                _logger.LogError("[SubCall] URI construction error for backend '{BackendName}' of model '{ModelId}': {Error}", currentBackendName, modelId, uriError);
                subCallLogEntry.ErrorMessage = $"URI error for {currentBackendName}: {uriError}";
                continue;
            }
            subCallLogEntry.UpstreamUrl = upstreamUri.ToString();

            string finalOutgoingRequestBody;
            try
            {
                var payloadObject = JsonNode.Parse(baseRequestBodyPayload)?.AsObject();
                if (payloadObject == null)
                {
                    _logger.LogWarning("[SubCall] Base request body for model '{ModelId}' could not be parsed as a JSON object. Using raw.", modelId);
                    finalOutgoingRequestBody = baseRequestBodyPayload;
                    subCallLogEntry.EffectiveModelName = modelId; // Still log the intended model
                }
                else
                {
                    ApplyPassthroughParameters(originalClientJsonNodeForParams, payloadObject);
                    string backendSpecificModelName = modelId;
                    if (!string.IsNullOrWhiteSpace(selectedBackend.BackendModelName))
                    {
                        payloadObject["model"] = selectedBackend.BackendModelName;
                        backendSpecificModelName = selectedBackend.BackendModelName;
                    }
                    else
                    {
                        payloadObject["model"] = modelId;
                    }
                    subCallLogEntry.EffectiveModelName = backendSpecificModelName;

                    if (isCallActuallyStreaming)
                        payloadObject["stream"] = true;
                    else if (payloadObject.ContainsKey("stream"))
                        payloadObject.Remove("stream");
                    finalOutgoingRequestBody = payloadObject.ToJsonString();
                }
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "[SubCall] Error processing base request body for model '{ModelId}'.", modelId);
                subCallLogEntry.ErrorMessage = $"Payload processing error for {currentBackendName}: {jsonEx.Message}";
                // This attempt for this backend failed due to payload issue, try next backend.
                continue;
            }

            subCallLogEntry.UpstreamRequestBody = finalOutgoingRequestBody;

            var httpClient = _httpClientFactory.CreateClient(BackendClientName);
            using var upstreamRequest = new HttpRequestMessage(new HttpMethod(originalRequestMethod), upstreamUri)
            {
                Content = new StringContent(finalOutgoingRequestBody, Encoding.UTF8, "application/json")
            };
            upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            _logger.LogInformation("[SubCall] Dispatching to backend '{BackendName}' for model '{ModelId}'. URI: {UpstreamUri}. Body (first 200 chars): {BodyStart}",
                currentBackendName, modelId, upstreamUri, finalOutgoingRequestBody.Substring(0, Math.Min(finalOutgoingRequestBody.Length, 200)));

            try
            {
                HttpCompletionOption completionOption = isCallActuallyStreaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
                backendResponse = await httpClient.SendAsync(upstreamRequest, completionOption, CancellationToken.None);

                subCallLogEntry.UpstreamStatusCode = (int)backendResponse.StatusCode;
                subCallLogEntry.ProxyResponseStatusCode = subCallLogEntry.UpstreamStatusCode.Value;

                string? responseBodyContent = await backendResponse.Content.ReadAsStringAsync();
                subCallLogEntry.UpstreamResponseBody = responseBodyContent?.Length > 200000
                    ? responseBodyContent.Substring(0, 200000) + "..."
                    : responseBodyContent;

                if (backendResponse.IsSuccessStatusCode)
                {
                    if (ContainsApplicationLevelError(responseBodyContent ?? "", currentBackendName, _logger))
                    {
                        _logger.LogWarning("[SubCall] Model '{ModelId}' to '{BackendName}' OK but content error. Retrying.", modelId, currentBackendName);
                        subCallLogEntry.WasSuccess = false;
                        subCallLogEntry.ErrorMessage = $"Content error from {currentBackendName}";
                        backendResponse.Dispose();
                        continue;
                    }
                    _logger.LogInformation("[SubCall] Model '{ModelId}' to '{BackendName}' success. Status: {StatusCode}.", modelId, currentBackendName, backendResponse.StatusCode);
                    subCallLogEntry.WasSuccess = true;
                    subCallLogEntry.ErrorMessage = null;
                    await LogRequestAsync(subCallLogEntry);
                    return (true, responseBodyContent, (int)backendResponse.StatusCode, null, currentBackendName);
                }
                else
                {
                    _logger.LogWarning("[SubCall] Model '{ModelId}' to '{BackendName}' failed. Status: {StatusCode}.", modelId, currentBackendName, backendResponse.StatusCode);
                    subCallLogEntry.WasSuccess = false;
                    subCallLogEntry.ErrorMessage = $"Backend error {backendResponse.StatusCode} from {currentBackendName}. Body: {responseBodyContent?.Substring(0, Math.Min(responseBodyContent.Length, 200))}";
                    if (IsRetryableError(backendResponse.StatusCode))
                    {
                        backendResponse.Dispose();
                        continue;
                    }
                    await LogRequestAsync(subCallLogEntry);
                    return (false, responseBodyContent, (int)backendResponse.StatusCode, subCallLogEntry.ErrorMessage, currentBackendName);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                _logger.LogError(ex, "[SubCall] Network/Timeout for '{ModelId}' to '{BackendName}'.", modelId, currentBackendName);
                subCallLogEntry.WasSuccess = false;
                subCallLogEntry.ErrorMessage = $"Network/Timeout for {currentBackendName}: {ex.Message}";
                subCallLogEntry.UpstreamStatusCode = null;
                subCallLogEntry.ProxyResponseStatusCode = StatusCodes.Status504GatewayTimeout;
                backendResponse?.Dispose();
                continue;
            }
            finally
            {
                backendResponse?.Dispose();
            }
        }
    }

    private async Task HandleMoAStrategyAsync(
        HttpContext context, ApiLogEntry logEntry, /* This is the PARENT MoA logEntry */
        string groupName, ModelGroupConfig groupConfig,
        string originalRequestBody, bool clientRequestsStreaming, string clientRequestPath, string clientRequestMethod)
    {
        _logger.LogInformation("Initiating MoA workflow for group: {GroupName}. Main Request Log ID: {MainLogId}", groupName, logEntry.Id);
        logEntry.EffectiveModelName = $"MoA_Group_{groupName}"; // Mark the main log entry

        if (string.IsNullOrWhiteSpace(groupConfig.OrchestratorModelName) || !groupConfig.AgentModelNames.Any())
        {
            _logger.LogError("MoA group '{GroupName}' critically misconfigured (missing orchestrator or agent models in AgentModelNames).", groupName);
            logEntry.WasSuccess = false;
            logEntry.ErrorMessage = "MoA group is misconfigured: missing orchestrator or agent models.";
            logEntry.ProxyResponseStatusCode = StatusCodes.Status500InternalServerError;
            SetFinalFailureResponse(context, logEntry, logEntry.ErrorMessage); // This will log the main 'logEntry'
            return;
        }

        JsonNode? originalClientJsonNode = TryParseJson(originalRequestBody);

        var agentTasks = groupConfig.AgentModelNames.Select(agentModelId =>
        {
            var agentBasePayloadObject = new JsonObject();
            if (originalClientJsonNode?["messages"] is JsonArray clientMessages)
            {
                agentBasePayloadObject["messages"] = clientMessages.DeepClone();
            }
            else if (originalClientJsonNode?["prompt"] is JsonNode clientPrompt)
            {
                agentBasePayloadObject["prompt"] = clientPrompt.DeepClone();
            }

            // Call 7-argument ExecuteSingleModelRequestAsync
            return ExecuteSingleModelRequestAsync(
                    agentModelId,
                    agentBasePayloadObject.ToJsonString(),
                    false, // Agents are non-streaming for MoA
                    clientRequestPath,
                    clientRequestMethod,
                    // No mainLogEntryForContext, no clientIp, no workflowId
                    originalClientJsonNode)
                .ContinueWith(t => (Result: t.Result, AgentName: agentModelId));
        }).ToList();

        if (!agentTasks.Any())
        {
            logEntry.WasSuccess = false;
            logEntry.ErrorMessage = "MoA group has no configured agents to execute.";
            logEntry.ProxyResponseStatusCode = StatusCodes.Status500InternalServerError;
            SetFinalFailureResponse(context, logEntry, logEntry.ErrorMessage);
            return;
        }

        var agentResults = await Task.WhenAll(agentTasks);

        var agentResponses = new Dictionary<string, string>();
        var failedAgentDetails = new List<string>();

        foreach (var agentTaskResult in agentResults)
        {
            var (success, body, statusCode, errMsg, backendName) = agentTaskResult.Result;
            string agentName = agentTaskResult.AgentName;

            if (success && body != null)
            {
                try
                {
                    var agentJsonNode = JsonNode.Parse(body);
                    string? agentContent = agentJsonNode?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
                    if (agentContent == null && agentJsonNode?["choices"]?[0]?["text"] is JsonNode textNode)
                    {
                        agentContent = textNode.GetValue<string>();
                    }

                    if (agentContent != null)
                    {
                        agentResponses[agentName] = agentContent;
                        _logger.LogInformation("MoA: Agent {AgentName} succeeded. Content extracted.", agentName);
                    }
                    else
                    {
                        _logger.LogError("MoA: Agent {AgentName} succeeded but could not extract content.", agentName);
                        failedAgentDetails.Add($"{agentName} (ContentExtractionError)");
                    }
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogError(jsonEx, "MoA: Agent {AgentName} response was not valid JSON.", agentName);
                    failedAgentDetails.Add($"{agentName} (InvalidJSON)");
                }
            }
            else
            {
                failedAgentDetails.Add($"{agentName} (Status: {statusCode}, Err: {errMsg?.Substring(0, Math.Min(errMsg.Length, 50))})");
                _logger.LogError("MoA: Agent {AgentName} failed. Status: {StatusCode}, Error: {ErrorMessage}", agentName, statusCode, errMsg);
            }
        }

        if (failedAgentDetails.Any() || !agentResponses.Any())
        {
            string failureMessage = !agentResponses.Any()
                ? "MoA failed: No successful agent responses with extractable content."
                : $"MoA failed: Agent(s) [{string.Join("; ", failedAgentDetails)}] did not respond successfully or provide usable content.";
            _logger.LogError("MoA workflow failed for {GroupName}: {FailureDetails}", groupName, failureMessage);
            logEntry.WasSuccess = false;
            logEntry.ErrorMessage = failureMessage;
            logEntry.ProxyResponseStatusCode = StatusCodes.Status503ServiceUnavailable;
            SetFinalFailureResponse(context, logEntry, logEntry.ErrorMessage);
            return;
        }

        // --- Build Orchestrator Prompt ---
        string systemPromptContent = "You are an expert orchestrator. Your task is to synthesize a final, comprehensive, and accurate answer for the user based on their original query and the responses provided by several specialist agents. Ensure your final answer directly addresses the user's original query, integrating the insights from the agents.";
        string userQueryContent = "Could not extract original user query.";
        if (originalClientJsonNode?["messages"] is JsonArray messages)
        {
            var lastUserMessage = messages.LastOrDefault(m => m?["role"]?.GetValue<string>() == "user");
            userQueryContent = lastUserMessage?["content"]?.GetValue<string>() ?? userQueryContent;
        }
        else if (originalClientJsonNode?["prompt"] is JsonNode promptNode)
        {
            userQueryContent = promptNode.GetValue<string>() ?? userQueryContent;
        }
        var userPromptForOrchestratorBuilder = new StringBuilder();
        userPromptForOrchestratorBuilder.AppendLine($"--- ORIGINAL USER QUERY ---\n{userQueryContent}");
        userPromptForOrchestratorBuilder.AppendLine("\n--- AGENT RESPONSES ---");
        foreach (var ar in agentResponses)
        {
            userPromptForOrchestratorBuilder.AppendLine($"\nAgent [{ar.Key}]:\n{ar.Value}");
        }
        userPromptForOrchestratorBuilder.AppendLine("\n--- FINAL SYNTHESIZED ANSWER ---");
        var orchestratorMessages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPromptContent },
            new JsonObject { ["role"] = "user", ["content"] = userPromptForOrchestratorBuilder.ToString() }
        };
        var orchestratorBasePayloadObject = new JsonObject
        {
            ["messages"] = orchestratorMessages
        };
        string orchestratorBaseRequestBody = orchestratorBasePayloadObject.ToJsonString();

        // Update parent MoA log for the orchestrator step (before the call)
        logEntry.UpstreamRequestBody = $"[Orchestrator Call to {groupConfig.OrchestratorModelName}] Input based on agents: {string.Join(", ", agentResponses.Keys)}. See separate log entry for orchestrator's full request.";
        logEntry.EffectiveModelName = groupConfig.OrchestratorModelName; // Parent log shows orchestrator as key step

        _logger.LogInformation("MoA: Preparing to call orchestrator {OrchestratorModel}. Client streaming: {ClientStreaming}",
            groupConfig.OrchestratorModelName, clientRequestsStreaming);

        // Call 7-argument ExecuteSingleModelRequestAsync
        
        var (orchSuccess, orchBody, orchStatusCode, orchError, orchBackendName) = await ExecuteSingleModelRequestAsync(
            groupConfig.OrchestratorModelName!,
            orchestratorBaseRequestBody,
            clientRequestsStreaming,
            clientRequestPath,
            clientRequestMethod,
            // No mainLogEntryForContext, no clientIp, no workflowId
            originalClientJsonNode);

        // Update parent MoA log entry with orchestrator's outcome
        logEntry.UpstreamBackendName = orchBackendName;
        logEntry.UpstreamStatusCode = orchStatusCode;

        if (orchSuccess && orchBody != null)
        {
            _logger.LogInformation("MoA: Orchestrator {OrchestratorModel} successful. Relaying response to client.", groupConfig.OrchestratorModelName);
            HttpStatusCode responseStatusCodeToRelay = orchStatusCode.HasValue ? (HttpStatusCode)orchStatusCode.Value : HttpStatusCode.OK;
            if (responseStatusCodeToRelay == HttpStatusCode.NoContent && clientRequestsStreaming)
            {
                _logger.LogWarning("Orchestrator returned 204 NoContent but client requested streaming. Relaying as 200 OK with potentially empty stream body for {OrchestratorModel}.", groupConfig.OrchestratorModelName);
                responseStatusCodeToRelay = HttpStatusCode.OK;
            }
            else if (responseStatusCodeToRelay == HttpStatusCode.NoContent)
            {
                _logger.LogInformation("Orchestrator returned 204 NoContent for {OrchestratorModel}.", groupConfig.OrchestratorModelName);
            }
            using var tempOrchResponse = new HttpResponseMessage(responseStatusCodeToRelay);
            string mediaType = clientRequestsStreaming ? "text/event-stream" : "application/json";
            tempOrchResponse.Content = new StringContent(orchBody, Encoding.UTF8, mediaType);
            if (clientRequestsStreaming)
            {
                tempOrchResponse.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            }

            var (relayCompleted, internalErrorDetectedInContent, finalOrchBodyForLog) = await RelayResponseAsync(
                context,
                tempOrchResponse,
                clientRequestsStreaming,
                groupConfig.OrchestratorModelName!,
                _logger);

            // Update parent MoA log
            logEntry.WasSuccess = relayCompleted && !internalErrorDetectedInContent;
            logEntry.ErrorMessage = internalErrorDetectedInContent ? "Orchestrator response contained an internal error pattern." : (relayCompleted ? null : "Relay of orchestrator response failed or was cancelled.");
            logEntry.UpstreamResponseBody = "[MoA Orchestrator Succeeded] See separate log entry for full orchestrator response. Snippet: " + (finalOrchBodyForLog?.Substring(0, Math.Min(finalOrchBodyForLog.Length, 100)) + "...");
        }
        else
        {
            _logger.LogError("MoA: Orchestrator {OrchestratorModel} failed. Status: {StatusCode}, Error: {Error}, Body: {Body}",
                groupConfig.OrchestratorModelName, orchStatusCode, orchError, orchBody?.Substring(0, Math.Min(orchBody?.Length ?? 0, 500)));
            logEntry.WasSuccess = false;
            logEntry.ErrorMessage = $"MoA Orchestrator '{groupConfig.OrchestratorModelName}' failed: {orchError ?? "Unknown error"} (Status: {orchStatusCode?.ToString() ?? "N/A"})";
            logEntry.UpstreamResponseBody = orchBody?.Length > 200000 ? orchBody.Substring(0, 200000) + "..." : orchBody;
            SetFinalFailureResponse(context, logEntry, logEntry.ErrorMessage); // This will log the main 'logEntry'
        }

        // The main 'logEntry' for the MoA request is logged by DispatchRequestAsync if no early SetFinalFailureResponse was called.
        // If SetFinalFailureResponse was called, it handles logging 'logEntry'.
        // If we reach here successfully, DispatchRequestAsync will log 'logEntry'.
        if (logEntry.WasSuccess && !context.Response.HasStarted) // If successful but relay didn't start response (e.g. 204)
        {
            // This case should be rare if RelayResponseAsync handles 204 correctly.
            // But as a safeguard, ensure the parent log reflects success.
            logEntry.ProxyResponseStatusCode = context.Response.StatusCode == 0 ? (int)HttpStatusCode.OK : context.Response.StatusCode;
        }
        else if (!logEntry.WasSuccess && !context.Response.HasStarted)
        {
            // If it failed but SetFinalFailureResponse wasn't called (e.g. an error path missed it)
            // this is a fallback.
            SetFinalFailureResponse(context, logEntry, logEntry.ErrorMessage ?? "Unknown MoA error before response relay.");
        }
        // else, response has started or SetFinalFailureResponse handled it.
    }

    private async Task<bool> HandleSingleEffectiveModelDispatchAsync(
    HttpContext context, ApiLogEntry logEntry, string effectiveModelName, ModelRoutingConfig modelConfig,
    string originalRequestBody, // This is the full original request body string from the client
    bool clientRequestsStreaming, string clientRequestPath, string clientRequestMethod)
    {
        _logger.LogInformation("Dispatching for effective model: {EffectiveModelName}. Client streaming: {ClientStreaming}", effectiveModelName, clientRequestsStreaming);
        logEntry.EffectiveModelName = effectiveModelName;
        List<BackendConfig> triedBackends = new();
        HttpResponseMessage? backendHttpResponse = null;

        JsonNode? originalClientJsonNode = TryParseJson(originalRequestBody); // Parse original client request for parameters

        while (true) // Backend retry loop for this effectiveModelName
        {
            var (selectedBackend, apiKey) = _routingService.GetNextBackendAndKeyForModel(modelConfig, effectiveModelName, triedBackends);
            if (selectedBackend == null || string.IsNullOrEmpty(apiKey))
            {
                _logger.LogWarning("All backends exhausted for effective model {EffectiveModelName}.", effectiveModelName);
                logEntry.ErrorMessage = $"All backends failed for {effectiveModelName}."; // Update error for this attempt
                return false; // Signal failure for this effective model
            }
            triedBackends.Add(selectedBackend);
            logEntry.UpstreamBackendName = selectedBackend.Name;

            string? backendPathSuffix = GetBackendPathSuffix(clientRequestPath);
            if (backendPathSuffix == null) // Should ideally not happen if request path was validated earlier
            {
                _logger.LogError("Invalid backend path suffix for request path {ClientRequestPath} with effective model {EffectiveModelName}.", clientRequestPath, effectiveModelName);
                logEntry.ErrorMessage = $"Internal error: Invalid backend path for {effectiveModelName}.";
                // This is an internal error, probably shouldn't retry with another backend for the same reason.
                // Consider how to handle this. For now, we'll continue, but it will likely fail URI construction.
                // A more robust solution might be to return false or throw an exception earlier.
                continue;
            }

            if (!TryConstructUpstreamUri(selectedBackend.BaseUrl, backendPathSuffix, out Uri? upstreamUri, out string uriError))
            {
                _logger.LogError("URI error for backend {BackendName} of {EffectiveModelName}: {Error}", selectedBackend.Name, effectiveModelName, uriError);
                logEntry.ErrorMessage = $"URI error for {selectedBackend.Name}: {uriError}";
                continue; // Try next backend
            }
            logEntry.UpstreamUrl = upstreamUri!.ToString();

            string finalOutgoingRequestBody;
            try
            {
                // For direct model calls, the originalRequestBody contains the primary payload (e.g., messages).
                // We'll parse it, apply passthrough parameters, then ensure 'model' and 'stream' are correct.
                var payloadObject = JsonNode.Parse(originalRequestBody)?.AsObject();

                if (payloadObject != null)
                {
                    // Apply passthrough parameters from the original client request.
                    // originalClientJsonNode is the parsed version of originalRequestBody.
                    ApplyPassthroughParameters(originalClientJsonNode, payloadObject);

                    // CRITICAL: Explicitly set/override 'model' and 'stream' after passthrough.
                    // This ensures proxy control over these essential parameters.
                    if (!string.IsNullOrWhiteSpace(selectedBackend.BackendModelName))
                    {
                        payloadObject["model"] = selectedBackend.BackendModelName;
                        _logger.LogDebug("Overriding model to backend-specific: '{BackendModelName}' for effective model '{EffectiveModelName}'", selectedBackend.BackendModelName, effectiveModelName);
                    }
                    else
                    {
                        // If no backend-specific model name, ensure the 'model' field reflects the effectiveModelName.
                        payloadObject["model"] = effectiveModelName;
                        _logger.LogDebug("Setting model to effective: '{EffectiveModelName}'", effectiveModelName);
                    }

                    if (clientRequestsStreaming)
                    {
                        payloadObject["stream"] = true;
                        _logger.LogDebug("Setting stream to true for effective model '{EffectiveModelName}'", effectiveModelName);
                    }
                    else
                    {
                        // If not streaming, ensure 'stream' is false or removed.
                        // Removing might be cleaner if the backend defaults to non-streaming.
                        if (payloadObject.ContainsKey("stream"))
                        {
                            payloadObject.Remove("stream");
                            _logger.LogDebug("Removing stream parameter for non-streaming call to effective model '{EffectiveModelName}'", effectiveModelName);
                            // Alternatively: payloadObject["stream"] = false;
                        }
                    }
                    finalOutgoingRequestBody = payloadObject.ToJsonString();
                }
                else
                {
                    _logger.LogWarning("Original request body for {EffectiveModelName} could not be parsed as a JSON object. Sending as is, parameter passthrough skipped.", effectiveModelName);
                    finalOutgoingRequestBody = originalRequestBody; // Send original body if it wasn't a valid JSON object
                }
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Error processing original request body for {EffectiveModelName} to apply parameters. Sending original body. Error: {JsonExMessage}", effectiveModelName, jsonEx.Message);
                finalOutgoingRequestBody = originalRequestBody; // Fallback to original body on error
            }

            logEntry.UpstreamRequestBody = finalOutgoingRequestBody; // Log the actual body being sent

            var httpClient = _httpClientFactory.CreateClient(BackendClientName);
            using var upstreamRequest = new HttpRequestMessage(new HttpMethod(clientRequestMethod), upstreamUri)
            {
                Content = new StringContent(finalOutgoingRequestBody, Encoding.UTF8, "application/json")
            };
            upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            _logger.LogInformation("Dispatching to {Backend} ({UpstreamUri}) for model {EffectiveModel}. Body (first 200): {BodyStart}",
                selectedBackend.Name, upstreamUri, effectiveModelName, finalOutgoingRequestBody.Substring(0, Math.Min(finalOutgoingRequestBody.Length, 200)));
            try
            {
                HttpCompletionOption completionOption = clientRequestsStreaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
                backendHttpResponse = await httpClient.SendAsync(upstreamRequest, completionOption, context.RequestAborted);
                logEntry.UpstreamStatusCode = (int)backendHttpResponse.StatusCode;

                if (backendHttpResponse.IsSuccessStatusCode)
                {
                    var (relayCompleted, internalError, relayedBody) = await RelayResponseAsync(context, backendHttpResponse, clientRequestsStreaming, selectedBackend.Name, _logger);
                    if (relayedBody != null)
                        logEntry.UpstreamResponseBody = relayedBody.Length > 200000 ? relayedBody.Substring(0, 200000) + "..." : relayedBody;

                    if (!relayCompleted)
                    {
                        logEntry.ErrorMessage = $"Relay failed/cancelled for {selectedBackend.Name}.";
                        logEntry.WasSuccess = false;
                        // Don't await LogRequestAsync here if we are returning immediately due to client disconnect.
                        // The log will be captured at the end of DispatchRequestAsync or if an exception occurs.
                        // However, if this is the final attempt, it should be logged.
                        // For now, we assume the main DispatchRequestAsync will handle final logging.
                        backendHttpResponse.Dispose();
                        return true; // Request processing ends here (e.g., client disconnected during stream)
                    }
                    if (internalError)
                    {
                        _logger.LogWarning("Backend {BackendName} OK but content error. Retrying with next backend.", selectedBackend.Name);
                        logEntry.ErrorMessage = $"Backend {selectedBackend.Name} OK but internal error. Body: {logEntry.UpstreamResponseBody ?? "N/A"}";
                        logEntry.WasSuccess = false; // Mark this specific backend attempt as failed
                                                     // Log this attempt before trying next backend
                                                     // await LogRequestAsync(logEntry); // Careful with multiple logs for one client request
                        backendHttpResponse.Dispose();
                        continue; // Try next backend
                    }
                    logEntry.WasSuccess = true;
                    logEntry.ErrorMessage = null;
                    logEntry.ProxyResponseStatusCode = context.Response.StatusCode;
                    await LogRequestAsync(logEntry); // Successful attempt for this effective model
                    backendHttpResponse.Dispose();
                    return true; // Success for this effective model
                }
                else // Non-success from backend
                {
                    if (backendHttpResponse.Content != null)
                    {
                        var errBody = await backendHttpResponse.Content.ReadAsStringAsync();
                        logEntry.UpstreamResponseBody = errBody.Length > 1000 ? errBody.Substring(0, 1000) + "..." : errBody;
                    }
                    logEntry.ErrorMessage = $"Backend {selectedBackend.Name} error: {backendHttpResponse.StatusCode}. Body: {logEntry.UpstreamResponseBody ?? "N/A"}";
                    _logger.LogWarning("Backend {BackendName} returned {StatusCode} for {EffectiveModelName}.", selectedBackend.Name, backendHttpResponse.StatusCode, effectiveModelName);

                    if (IsRetryableError(backendHttpResponse.StatusCode))
                    {
                        backendHttpResponse.Dispose();
                        // Log this attempt before trying next backend
                        // await LogRequestAsync(logEntry); // Careful with multiple logs
                        continue; // Try next backend
                    }

                    // Non-retryable error from this backend, relay to client
                    await RelayResponseAsync(context, backendHttpResponse, false, selectedBackend.Name, _logger); // isStreaming false for error relay
                    logEntry.ProxyResponseStatusCode = context.Response.StatusCode;
                    logEntry.WasSuccess = false;
                    await LogRequestAsync(logEntry); // Log the failed attempt that was relayed
                    backendHttpResponse.Dispose();
                    return true; // Request processing ends, even if with an error relayed
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP exception for {BackendName} during request to {EffectiveModelName}.", selectedBackend.Name, effectiveModelName);
                logEntry.ErrorMessage = $"HTTP Exc: {ex.Message}";
                backendHttpResponse?.Dispose();
                // Log this attempt before trying next backend
                // await LogRequestAsync(logEntry); // Careful with multiple logs
                continue; // Try next backend
            }
            catch (TaskCanceledException ex)
            {
                if (context.RequestAborted.IsCancellationRequested)
                {
                    _logger.LogInformation("Client cancelled request to {BackendName} for {EffectiveModelName}.", selectedBackend.Name, effectiveModelName);
                    logEntry.ErrorMessage = "Client cancelled.";
                    logEntry.WasSuccess = false;
                    logEntry.ProxyResponseStatusCode = 499; // Client Closed Request
                                                            // Log this attempt
                                                            // await LogRequestAsync(logEntry); // Careful with multiple logs
                    backendHttpResponse?.Dispose();
                    return true; // Stop processing for this request
                }
                _logger.LogError(ex, "Timeout for {BackendName} during request to {EffectiveModelName}.", selectedBackend.Name, effectiveModelName);
                logEntry.ErrorMessage = "Timeout.";
                backendHttpResponse?.Dispose();
                // Log this attempt before trying next backend
                // await LogRequestAsync(logEntry); // Careful with multiple logs
                continue; // Try next backend
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error for {BackendName} during request to {EffectiveModelName}.", selectedBackend.Name, effectiveModelName);
                logEntry.ErrorMessage = $"Dispatch Exc: {ex.Message}";
                backendHttpResponse?.Dispose();
                // Log this attempt before trying next backend
                // await LogRequestAsync(logEntry); // Careful with multiple logs
                continue; // Try next backend
            }
            finally
            {
                backendHttpResponse?.Dispose();
            }
        }
    }

    private void ApplyPassthroughParameters(JsonNode? sourceClientJson, JsonObject targetPayload)
    {
        if (sourceClientJson == null || sourceClientJson.GetValueKind() != JsonValueKind.Object)
        {
            _logger.LogDebug("ApplyPassthroughParameters: Source client JSON is null or not an object. No parameters to apply.");
            return;
        }

        var clientRequestObject = sourceClientJson.AsObject();

        // Parameters explicitly managed by the proxy or that should not be blindly passed.
        // 'model' and 'stream' are handled specifically after this method is called.
        // 'messages' (or 'prompt' for legacy) is the core content and is constructed specifically.
        var excludedParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "model",        // Handled by routing and BackendModelName
            "stream",       // Handled by clientRequestsStreaming and isCallActuallyStreaming
            "messages",     // Constructed specifically for agents/orchestrator or is the main content
            "prompt",       // Legacy, also core content
            // Add any other parameters you want the proxy to exclusively control or ignore from client.
            // Example: "n" (number of completions) might be complex to handle consistently,
            // especially with MoA, so you might choose to exclude it or handle it specially.
            // "n",
        };

        _logger.LogDebug("ApplyPassthroughParameters: Starting to iterate client request properties.");
        foreach (var property in clientRequestObject)
        {
            if (!excludedParams.Contains(property.Key))
            {
                // DeepClone is crucial to avoid shared references if the source node is complex
                // or if targetPayload is modified elsewhere.
                targetPayload[property.Key] = property.Value?.DeepClone();
                _logger.LogDebug("Applying passthrough parameter '{ParamName}' with value: {ParamValue}", property.Key, property.Value?.ToJsonString() ?? "null");
            }
            else
            {
                _logger.LogDebug("Skipping excluded parameter '{ParamName}' from client request.", property.Key);
            }
        }
        _logger.LogDebug("ApplyPassthroughParameters: Finished applying parameters.");
    }

    public async Task DispatchRequestAsync(HttpContext context)
    {
        var clientRequestPath = context.Request.Path.Value ?? string.Empty;
        var clientRequestMethod = context.Request.Method;
        var logEntry = new ApiLogEntry
        {
            RequestPath = clientRequestPath,
            RequestMethod = clientRequestMethod,
            Timestamp = DateTime.UtcNow
        };

        context.Request.EnableBuffering();
        string originalRequestBody;
        try
        {
            context.Request.Body.Seek(0, SeekOrigin.Begin);
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
            {
                originalRequestBody = await reader.ReadToEndAsync();
            }
            context.Request.Body.Seek(0, SeekOrigin.Begin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading request body.");
            HandleBadRequest("Error reading request body.", context, logEntry);
            return;
        }

        logEntry.ClientRequestBody = originalRequestBody;
        string? requestedModelOrGroupName = TryExtractModelName(originalRequestBody);
        logEntry.RequestedModel = requestedModelOrGroupName;

        if (string.IsNullOrEmpty(requestedModelOrGroupName))
        {
            HandleBadRequest("Could not determine model name from request.", context, logEntry);
            return;
        }

        bool clientRequestsStreaming = originalRequestBody.Contains("\"stream\": true");
        _logger.LogInformation("Dispatch request for: {ReqModelOrGroup}. Client requests streaming: {ClientStreaming}", requestedModelOrGroupName, clientRequestsStreaming);

        List<string> triedMemberModelsInGroup = new List<string>();

        // OUTER LOOP: For retrying different member models of a non-MoA group, or runs once for direct model/MoA group.
        while (true)
        {
            var (effectiveModelNameFromRouting, modelConfigFromRouting, groupConfigFromRouting) =
                _routingService.ResolveRoutingConfig(requestedModelOrGroupName, originalRequestBody, triedMemberModelsInGroup);

            if (groupConfigFromRouting != null && groupConfigFromRouting.Strategy == RoutingStrategyType.MixtureOfAgents)
            {
                await HandleMoAStrategyAsync(context, logEntry, requestedModelOrGroupName, groupConfigFromRouting, originalRequestBody, clientRequestsStreaming, clientRequestPath, clientRequestMethod);
                return; // MoA path completes the request.
            }
            else if (modelConfigFromRouting != null && !string.IsNullOrEmpty(effectiveModelNameFromRouting))
            {
                bool successForThisEffectiveModel = await HandleSingleEffectiveModelDispatchAsync(
                    context, logEntry, effectiveModelNameFromRouting, modelConfigFromRouting,
                    originalRequestBody, clientRequestsStreaming, clientRequestPath, clientRequestMethod
                );

                if (successForThisEffectiveModel)
                {
                    // If WasSuccess is false in logEntry here, it means an error was relayed or client disconnected.
                    // If WasSuccess is true, it was a genuine success.
                    // In either case, the request processing for this attempt is done.
                    return;
                }
                else // False means this effectiveModelName failed all its backends with retryable errors
                {
                    if (groupConfigFromRouting != null) // It was a member of a non-MoA group
                    {
                        _logger.LogWarning("Effective model '{EffectiveModelName}' from group '{GroupName}' failed all its backends. Trying next member model.", effectiveModelNameFromRouting, requestedModelOrGroupName);
                        triedMemberModelsInGroup.Add(effectiveModelNameFromRouting);
                        // Clear per-model attempt details from logEntry for the next group member attempt
                        logEntry.UpstreamBackendName = null;
                        logEntry.UpstreamUrl = null;
                        logEntry.UpstreamRequestBody = null;
                        logEntry.UpstreamStatusCode = null;
                        logEntry.UpstreamResponseBody = null;
                        logEntry.ErrorMessage = $"Attempt for model {effectiveModelNameFromRouting} failed, trying next group member.";
                        continue; // Continue OUTER LOOP to try next group member
                    }
                    else // It was a direct model request that failed all backends
                    {
                        _logger.LogError("Direct model request for '{ModelName}' failed all its backends.", requestedModelOrGroupName);
                        SetFinalFailureResponse(context, logEntry, $"All backends failed for model {requestedModelOrGroupName}.");
                        await LogRequestAsync(logEntry);
                        return;
                    }
                }
            }
            else // No model/group config found or group exhausted
            {
                string failureMsg = $"Failed to resolve any model or group configuration for '{requestedModelOrGroupName}'";
                if (triedMemberModelsInGroup.Any())
                    failureMsg += $" after trying members: {string.Join(", ", triedMemberModelsInGroup)}";
                failureMsg += ". All options exhausted.";
                _logger.LogError(failureMsg);
                SetFinalFailureResponse(context, logEntry, "All language model backends or group members are temporarily unavailable or misconfigured.");
                await LogRequestAsync(logEntry);
                return;
            }
        } // End OUTER LOOP
    }
}
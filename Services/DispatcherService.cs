// Services/DispatcherService.cs
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
            string modelId,
            string requestBodyPayload,
            bool isCallActuallyStreaming, // How this specific call to backend should behave
            string originalRequestPath,
            string originalRequestMethod,
            ApiLogEntry mainLogEntryForContext) // For context, not direct modification
    {
        var modelStopwatch = Stopwatch.StartNew();
        _logger.LogInformation("[SubCall] Executing for: {ModelId}. StreamingHint: {IsCallStreaming}", modelId, isCallActuallyStreaming);

        if (!_routingService.TryGetModelRouting(modelId, out ModelRoutingConfig? modelConfig) || modelConfig == null)
        {
            _logger.LogError("[SubCall] No config for {ModelId}.", modelId);
            return (false, null, null, $"No model configuration for {modelId}", null);
        }

        string? backendPathSuffix = GetBackendPathSuffix(originalRequestPath);
        if (backendPathSuffix == null)
        {
            _logger.LogError("[SubCall] Invalid path for {ModelId}: {Path}", modelId, originalRequestPath);
            return (false, null, null, "Invalid path for sub-call", null);
        }

        List<BackendConfig> triedBackends = new();
        HttpResponseMessage? backendResponse = null;

        while (true) // Backend retry loop for this modelId
        {
            var (selectedBackend, apiKey) = _routingService.GetNextBackendAndKeyForModel(modelConfig, modelId, triedBackends);
            if (selectedBackend == null || string.IsNullOrEmpty(apiKey))
            {
                _logger.LogWarning("[SubCall] All backends exhausted for {ModelId}.", modelId);
                return (false, null, null, $"All backends failed for {modelId}", null);
            }
            triedBackends.Add(selectedBackend);
            string currentBackendName = selectedBackend.Name;

            if (!TryConstructUpstreamUri(selectedBackend.BaseUrl, backendPathSuffix, out Uri? upstreamUri, out string uriError))
            {
                _logger.LogError("[SubCall] URI error for {BackendName} of {ModelId}: {Error}", currentBackendName, modelId, uriError);
                continue;
            }

            string outgoingRequestBody = requestBodyPayload;
            if (!string.IsNullOrWhiteSpace(selectedBackend.BackendModelName) &&
                !TryModifyRequestBodyModel(requestBodyPayload, selectedBackend.BackendModelName, out outgoingRequestBody))
            {
                _logger.LogWarning("[SubCall] Failed to modify body for backend-specific model {BackendSpecific} for {BackendName}.", selectedBackend.BackendModelName, currentBackendName);
            }

            var httpClient = _httpClientFactory.CreateClient(BackendClientName);
            using var upstreamRequest = new HttpRequestMessage(new HttpMethod(originalRequestMethod), upstreamUri)
            {
                Content = new StringContent(outgoingRequestBody, Encoding.UTF8, "application/json")
            };
            upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            _logger.LogInformation("[SubCall] Dispatching to {Backend} for {ModelId}. URI: {UpstreamUri}", currentBackendName, modelId, upstreamUri);
            try
            {
                HttpCompletionOption completionOption = isCallActuallyStreaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
                backendResponse = await httpClient.SendAsync(upstreamRequest, completionOption, CancellationToken.None);

                int currentStatusCode = (int)backendResponse.StatusCode;
                string? responseBodyContent;

                if (isCallActuallyStreaming && backendResponse.IsSuccessStatusCode)
                {
                    // For a streaming call that is successful, we need to read the stream to get the full content.
                    // We'll use a MemoryStream to buffer it.
                    using var memoryStream = new MemoryStream();
                    await backendResponse.Content.CopyToAsync(memoryStream);
                    memoryStream.Position = 0;
                    using var reader = new StreamReader(memoryStream, Encoding.UTF8);
                    responseBodyContent = await reader.ReadToEndAsync();
                }
                else // Non-streaming call OR any failure OR non-success streaming
                {
                    responseBodyContent = await backendResponse.Content.ReadAsStringAsync();
                }

                if (backendResponse.IsSuccessStatusCode)
                {
                    if (ContainsApplicationLevelError(responseBodyContent ?? "", currentBackendName, _logger))
                    {
                        _logger.LogWarning("[SubCall] {ModelId} backend {BackendName} OK but content error.", modelId, currentBackendName);
                        backendResponse.Dispose();
                        continue;
                    }
                    _logger.LogInformation("[SubCall] {ModelId} backend {BackendName} success. Status: {StatusCode}. Took: {ElapsedMs}ms", modelId, currentBackendName, currentStatusCode, modelStopwatch.ElapsedMilliseconds);
                    return (true, responseBodyContent, currentStatusCode, null, currentBackendName);
                }
                else
                {
                    _logger.LogWarning("[SubCall] {ModelId} backend {BackendName} failed. Status: {StatusCode}. Body: {Body}", modelId, currentBackendName, currentStatusCode, responseBodyContent?.Substring(0, Math.Min(responseBodyContent.Length, 200)));
                    if (IsRetryableError(backendResponse.StatusCode))
                    {
                        backendResponse.Dispose();
                        continue;
                    }
                    return (false, responseBodyContent, currentStatusCode, $"Non-retryable error {currentStatusCode}", currentBackendName);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                _logger.LogError(ex, "[SubCall] Network/Timeout for {ModelId} backend {BackendName}.", modelId, currentBackendName);
                backendResponse?.Dispose();
                continue;
            }
            finally { backendResponse?.Dispose(); }
        }
    }

    private async Task HandleMoAStrategyAsync(
        HttpContext context, ApiLogEntry logEntry, string groupName, ModelGroupConfig groupConfig,
        string originalRequestBody, bool clientRequestsStreaming, string clientRequestPath, string clientRequestMethod)
    {
        _logger.LogInformation("Initiating MoA workflow for group: {GroupName}", groupName);
        logEntry.EffectiveModelName = $"MoA_Group_{groupName}"; // Mark as MoA

        // Validation already done by DynamicConfigService and RoutingService, but double check critical parts
        if (string.IsNullOrWhiteSpace(groupConfig.OrchestratorModelName) || !groupConfig.Models.Any())
        {
            _logger.LogError("MoA group '{GroupName}' critically misconfigured (missing orchestrator or agent models).", groupName);
            SetFinalFailureResponse(context, logEntry, "MoA group is misconfigured: missing orchestrator or agent models.");
            await LogRequestAsync(logEntry);
            return;
        }
        if (groupConfig.Models.Count < 2) // As per Readme/UI, MoA typically expects at least 2 agents
        {
            _logger.LogWarning("MoA group '{GroupName}' has fewer than 2 agent models configured. Proceeding, but this might not be optimal.", groupName);
        }

        var agentTasks = groupConfig.Models.Select(agentModelId =>
            ExecuteSingleModelRequestAsync(agentModelId, originalRequestBody, false, clientRequestPath, clientRequestMethod, logEntry)
                .ContinueWith(t => (t.Result.success, t.Result.responseBody, agentModelId, t.Result.statusCode, t.Result.errorMessage))
        ).ToList();

        await Task.WhenAll(agentTasks);

        var agentResponses = new Dictionary<string, string>();
        var failedAgentDetails = new List<string>();

        foreach (var task in agentTasks)
        {
            var (success, body, agentName, statusCode, errMsg) = task.Result;
            if (success && body != null)
            {
                try
                {
                    // Parse the agent's JSON response
                    var agentJsonNode = JsonNode.Parse(body);
                    // Extract the actual content (typical OpenAI compatible path)
                    string? agentContent = agentJsonNode?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();

                    if (agentContent != null)
                    {
                        agentResponses[agentName] = agentContent; // Store only the extracted content
                        _logger.LogInformation("MoA: Agent {AgentName} succeeded. Content extracted.", agentName);
                    }
                    else
                    {
                        _logger.LogError("MoA: Agent {AgentName} succeeded but could not extract message content from response: {ResponseBody}", agentName, body.Substring(0, Math.Min(body.Length, 500)));
                        failedAgentDetails.Add($"{agentName} (Status: {statusCode}, Error: Malformed response content or missing content field)");
                    }
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogError(jsonEx, "MoA: Agent {AgentName} response was not valid JSON: {ResponseBody}", agentName, body.Substring(0, Math.Min(body.Length, 500)));
                    failedAgentDetails.Add($"{agentName} (Status: {statusCode}, Error: Invalid JSON response)");
                }
            }
            else
            {
                failedAgentDetails.Add($"{agentName} (Status: {statusCode}, Error: {errMsg})");
                _logger.LogError("MoA: Agent {AgentName} failed. Status: {StatusCode}, Error: {ErrorMessage}", agentName, statusCode, errMsg);
            }
        }

        if (failedAgentDetails.Any())
        {
            string failureMessage = $"MoA failed: Agent(s) [{string.Join("; ", failedAgentDetails)}] did not respond successfully or provide usable content.";
            _logger.LogError("MoA workflow failed for {GroupName} due to agent failures: {FailureDetails}", groupName, string.Join("; ", failedAgentDetails));
            SetFinalFailureResponse(context, logEntry, failureMessage);
            logEntry.ErrorMessage = failureMessage;
            await LogRequestAsync(logEntry);
            return;
        }
        if (!agentResponses.Any()) // Should be caught by failedAgentDetails, but as a safeguard
        {
            _logger.LogError("MoA workflow failed for {GroupName}: No successful agent responses with extractable content.", groupName);
            SetFinalFailureResponse(context, logEntry, "MoA failed: No successful agent responses.");
            logEntry.ErrorMessage = "MoA failed: No successful agent responses.";
            await LogRequestAsync(logEntry);
            return;
        }

        // --- Build Orchestrator Prompt ---
        string systemPromptContent = "You are an expert orchestrator. Your task is to synthesize a final, comprehensive, and accurate answer for the user based on their original query and the responses provided by several specialist agents. Ensure your final answer directly addresses the user's original query, integrating the insights from the agents.";

        string userQueryContent = "Could not extract original user query.";
        JsonNode? originalClientJson = TryParseJson(originalRequestBody); // Parse once for multiple uses

        if (originalClientJson?["messages"] is JsonArray messages)
        {
            var lastUserMessage = messages.LastOrDefault(m => m?["role"]?.GetValue<string>() == "user");
            userQueryContent = lastUserMessage?["content"]?.GetValue<string>() ?? userQueryContent;
        }
        else if (originalClientJson?["prompt"] is JsonNode promptNode) // For legacy completion
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
        userPromptForOrchestratorBuilder.AppendLine("\n--- FINAL SYNTHESIZED ANSWER ---"); // Instruction for orchestrator output placement

        var orchestratorMessages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPromptContent },
            new JsonObject { ["role"] = "user", ["content"] = userPromptForOrchestratorBuilder.ToString() }
        };

        var orchestratorPayload = new JsonObject
        {
            // The "model" field here is the logical model name defined in the proxy's config.
            // ExecuteSingleModelRequestAsync will resolve it to the backend-specific model name if necessary.
            ["model"] = groupConfig.OrchestratorModelName,
            ["messages"] = orchestratorMessages
        };

        if (clientRequestsStreaming)
        {
            orchestratorPayload["stream"] = true;
        }

        // Attempt to pass through temperature and max_tokens from the original client request
        if (originalClientJson != null)
        {
            if (originalClientJson["temperature"] is JsonNode tempNode && (tempNode.GetValueKind() == JsonValueKind.Number))
            {
                orchestratorPayload["temperature"] = tempNode.DeepClone();
            }
            if (originalClientJson["max_tokens"] is JsonNode maxTokensNode && (maxTokensNode.GetValueKind() == JsonValueKind.Number))
            {
                orchestratorPayload["max_tokens"] = maxTokensNode.DeepClone();
            }
            // You could add other passthrough parameters here (e.g., top_p, presence_penalty)
        }

        string orchestratorRequestBody = orchestratorPayload.ToJsonString();

        logEntry.UpstreamRequestBody = orchestratorRequestBody; // Log the request to the orchestrator
        logEntry.EffectiveModelName = groupConfig.OrchestratorModelName; // Log the orchestrator model as the effective one for this step
        _logger.LogInformation("MoA: Calling orchestrator {OrchestratorModel}. Client streaming: {ClientStreaming}. Orchestrator Request Body (first 500 chars): {BodyStart}",
            groupConfig.OrchestratorModelName, clientRequestsStreaming, orchestratorRequestBody.Substring(0, Math.Min(orchestratorRequestBody.Length, 500)));

        var (orchSuccess, orchBody, orchStatusCode, orchError, orchBackendName) = await ExecuteSingleModelRequestAsync(
            groupConfig.OrchestratorModelName!, // OrchestratorModelName is validated not to be null
            orchestratorRequestBody,
            clientRequestsStreaming, // Orchestrator call respects client's streaming preference
            clientRequestPath,
            clientRequestMethod,
            logEntry);

        logEntry.UpstreamBackendName = orchBackendName; // Backend used for the orchestrator
        logEntry.UpstreamStatusCode = orchStatusCode;   // Status code from the orchestrator's backend

        if (orchSuccess && orchBody != null)
        {
            _logger.LogInformation("MoA: Orchestrator {OrchestratorModel} successful. Relaying response to client.", groupConfig.OrchestratorModelName);

            // For relaying, we need to construct a temporary HttpResponseMessage
            // as ExecuteSingleModelRequestAsync returns the body as a string.
            // The RelayResponseAsync expects an HttpResponseMessage.
            // We assume the orchestrator (if successful) returns JSON or text/event-stream.
            HttpStatusCode responseStatusCodeToRelay = orchStatusCode.HasValue ? (HttpStatusCode)orchStatusCode.Value : HttpStatusCode.OK;
            if (responseStatusCodeToRelay == HttpStatusCode.NoContent)
                responseStatusCodeToRelay = HttpStatusCode.OK; // Avoid issues with NoContent and streaming

            using var tempOrchResponse = new HttpResponseMessage(responseStatusCodeToRelay);
            string contentType = clientRequestsStreaming ? "text/event-stream" : "application/json";
            tempOrchResponse.Content = new StringContent(orchBody, Encoding.UTF8, contentType);

            // If the orchestrator was streaming, its body (orchBody) will contain the full concatenated stream.
            // RelayResponseAsync will handle re-streaming this if clientRequestsStreaming is true.
            var (relayCompleted, internalErrorDetectedInContent, finalOrchBodyForLog) = await RelayResponseAsync(
                context,
                tempOrchResponse,
                clientRequestsStreaming,
                groupConfig.OrchestratorModelName!, // For logging within RelayResponseAsync
                _logger);

            logEntry.UpstreamResponseBody = finalOrchBodyForLog?.Length > 200000 ? finalOrchBodyForLog.Substring(0, 200000) + "..." : finalOrchBodyForLog;
            logEntry.WasSuccess = relayCompleted && !internalErrorDetectedInContent;
            logEntry.ErrorMessage = internalErrorDetectedInContent ? "Orchestrator response contained an internal error pattern." : (relayCompleted ? null : "Relay of orchestrator response failed or was cancelled.");
        }
        else
        {
            _logger.LogError("MoA: Orchestrator {OrchestratorModel} failed. Status: {StatusCode}, Error: {Error}, Body: {Body}",
                groupConfig.OrchestratorModelName, orchStatusCode, orchError, orchBody?.Substring(0, Math.Min(orchBody?.Length ?? 0, 500)));
            string finalErrorMessage = $"MoA Orchestrator '{groupConfig.OrchestratorModelName}' failed: {orchError ?? "Unknown error"} (Status: {orchStatusCode?.ToString() ?? "N/A"})";
            SetFinalFailureResponse(context, logEntry, finalErrorMessage);
            logEntry.ErrorMessage = finalErrorMessage;
            logEntry.UpstreamResponseBody = orchBody?.Length > 200000 ? orchBody.Substring(0, 200000) + "..." : orchBody;
        }
        logEntry.ProxyResponseStatusCode = context.Response.StatusCode; // Log the final status code sent to client
        await LogRequestAsync(logEntry);
    }

    private async Task<bool> HandleSingleEffectiveModelDispatchAsync(
        HttpContext context, ApiLogEntry logEntry, string effectiveModelName, ModelRoutingConfig modelConfig,
        string originalRequestBody, bool clientRequestsStreaming, string clientRequestPath, string clientRequestMethod)
    {
        _logger.LogInformation("Dispatching for effective model: {EffectiveModelName}. Client streaming: {ClientStreaming}", effectiveModelName, clientRequestsStreaming);
        logEntry.EffectiveModelName = effectiveModelName;
        List<BackendConfig> triedBackends = new();
        HttpResponseMessage? backendHttpResponse = null;

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

            if (!TryConstructUpstreamUri(selectedBackend.BaseUrl, GetBackendPathSuffix(clientRequestPath)!, out Uri? upstreamUri, out string uriError))
            {
                _logger.LogError("URI error for backend {BackendName} of {EffectiveModelName}: {Error}", selectedBackend.Name, effectiveModelName, uriError);
                logEntry.ErrorMessage = $"URI error for {selectedBackend.Name}: {uriError}";
                continue;
            }
            logEntry.UpstreamUrl = upstreamUri!.ToString();

            string outgoingRequestBody = originalRequestBody;
            string modelNameToLogForBackend = effectiveModelName;
            if (!string.IsNullOrWhiteSpace(selectedBackend.BackendModelName))
            {
                modelNameToLogForBackend = selectedBackend.BackendModelName;
                if (!TryModifyRequestBodyModel(originalRequestBody, selectedBackend.BackendModelName, out outgoingRequestBody))
                {
                    _logger.LogWarning("Failed to modify body for backend-specific model {BackendSpecific} for {BackendName}.", selectedBackend.BackendModelName, selectedBackend.Name);
                }
            }
            logEntry.UpstreamRequestBody = outgoingRequestBody;

            var httpClient = _httpClientFactory.CreateClient(BackendClientName);
            using var upstreamRequest = new HttpRequestMessage(new HttpMethod(clientRequestMethod), upstreamUri)
            {
                Content = new StringContent(outgoingRequestBody, Encoding.UTF8, "application/json")
            };
            upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            _logger.LogInformation("Dispatching to {Backend} ({UpstreamUri}) for model {EffectiveModel} (using '{ModelForBackend}' for backend).",
                selectedBackend.Name, upstreamUri, effectiveModelName, modelNameToLogForBackend);
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
                        await LogRequestAsync(logEntry);
                        backendHttpResponse.Dispose();
                        return true; // Return true as request processing ends here due to client disconnect
                    }
                    if (internalError)
                    {
                        _logger.LogWarning("Backend {BackendName} OK but content error. Retrying.", selectedBackend.Name);
                        logEntry.ErrorMessage = $"Backend {selectedBackend.Name} OK but internal error. Body: {logEntry.UpstreamResponseBody ?? "N/A"}";
                        logEntry.WasSuccess = false; // Mark this attempt as failed
                        backendHttpResponse.Dispose();
                        continue; // Try next backend
                    }
                    logEntry.WasSuccess = true;
                    logEntry.ErrorMessage = null;
                    logEntry.ProxyResponseStatusCode = context.Response.StatusCode;
                    await LogRequestAsync(logEntry);
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
                        continue;
                    } // Try next backend

                    // Non-retryable error from this backend, relay to client
                    await RelayResponseAsync(context, backendHttpResponse, false, selectedBackend.Name, _logger); // isStreaming false for error relay
                    logEntry.ProxyResponseStatusCode = context.Response.StatusCode;
                    logEntry.WasSuccess = false;
                    await LogRequestAsync(logEntry);
                    backendHttpResponse.Dispose();
                    return true; // Request processing ends, even if with an error relayed
                }
            }
            catch (HttpRequestException ex) { _logger.LogError(ex, "HTTP exception for {BackendName}.", selectedBackend.Name); logEntry.ErrorMessage = $"HTTP Exc: {ex.Message}"; backendHttpResponse?.Dispose(); continue; }
            catch (TaskCanceledException ex)
            {
                if (context.RequestAborted.IsCancellationRequested)
                {
                    _logger.LogInformation("Client cancelled for {BackendName}.", selectedBackend.Name);
                    logEntry.ErrorMessage = "Client cancelled.";
                    logEntry.WasSuccess = false;
                    logEntry.ProxyResponseStatusCode = 499;
                    _ = LogRequestAsync(logEntry);
                    backendHttpResponse?.Dispose();
                    return true;
                }
                _logger.LogError(ex, "Timeout for {BackendName}.", selectedBackend.Name);
                logEntry.ErrorMessage = "Timeout.";
                backendHttpResponse?.Dispose();
                continue;
            }
            catch (Exception ex) { _logger.LogError(ex, "Unexpected error for {BackendName}.", selectedBackend.Name); logEntry.ErrorMessage = $"Dispatch Exc: {ex.Message}"; backendHttpResponse?.Dispose(); continue; }
            finally { backendHttpResponse?.Dispose(); }
        }
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
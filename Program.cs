using System.Diagnostics;
using System.Text.Json.Serialization;

using LLMProxy.Models; // Required for RoutingConfig, ModelRoutingConfig, ModelInfo, ModelListResponse etc.
using LLMProxy.Services; // Required for DynamicConfigurationService, RoutingService, DispatcherService

using LLMProxy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc; // Required for FromBody attribute and Results class
using Microsoft.AspNetCore.Http;
using System.Text.RegularExpressions;

// --- 1. Application Builder Setup ---
var builder = WebApplication.CreateBuilder(args);

// --- Add Database Context ---
var dbPath = Path.Combine(AppContext.BaseDirectory, "data", "llmproxy_log.db");
// Ensure directory exists
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDbContext<ProxyDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// --- 2. Configuration Sources ---
// Reads appsettings.json, environment-specific appsettings, and environment variables.
// Kestrel port (e.g., 1852) and Logging levels are typically configured here.
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

// --- 3. Logging Configuration ---
builder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.ClearProviders(); // Optional: Remove default providers if needed
    loggingBuilder.AddConsole();
    // Add other logging providers here (e.g., File, Debug, Serilog) if desired
    // Configure minimum levels via appsettings.json or code:
    // loggingBuilder.SetMinimumLevel(LogLevel.Information);
});

// --- 4. Dependency Injection Setup (Services) ---

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    // Tell the serializer to convert enums to/from strings (using their names)
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // Optional: Configure other serialization settings if needed
    // options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    // options.SerializerOptions.WriteIndented = true; // (Usually only for development)
});

// Configuration Service: Manages loading/saving LLM routing rules from dynamic_routing.json
// Registered as Singleton to maintain the in-memory cache and handle file access safely.
builder.Services.AddSingleton<DynamicConfigurationService>();

// Routing Service: Selects the appropriate backend based on strategy and dynamic config.
// Registered as Singleton because it maintains state (like round-robin indices) using ConcurrentDictionary.
builder.Services.AddSingleton<RoutingService>();

// Dispatcher Service: Handles the actual HTTP request forwarding to the selected backend LLM.
// Registered as Scoped because it uses HttpClientFactory and potentially other scoped services per request.
builder.Services.AddScoped<DispatcherService>();

// HttpClientFactory: Best practice for managing HttpClient instances.
// Named client allows specific configuration (like timeout).
builder.Services.AddHttpClient("LLMBackendClient")
    .ConfigureHttpClient(client =>
    {
        // Set a default timeout for requests to backend LLMs. Adjust as needed.
        client.Timeout = TimeSpan.FromSeconds(180);
    });
// Optionally add Polly resilience policies here (e.g., retries for transient network errors)
// .AddTransientHttpErrorPolicy(policyBuilder => policyBuilder.RetryAsync(3));

// CORS (Cross-Origin Resource Sharing): Allows web frontends from different domains to call the proxy.
// Configure origins appropriately for production instead of AllowAnyOrigin().
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin() // WARNING: For development only. Restrict in production.
              .AllowAnyHeader()
              .AllowAnyMethod();
        // If clients need to read specific headers like Content-Disposition, expose them:
        // .WithExposedHeaders("Content-Disposition");
    });
});

// --- 5. Build the Application ---
var app = builder.Build();

// --- Apply Migrations (Important!) ---
// This should be done carefully in production (e.g., during deployment script)
// For development, this is convenient.
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<ProxyDbContext>();
        context.Database.Migrate(); // Applies pending migrations
        app.Logger.LogInformation("Database migrations applied successfully or no migrations pending.");
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while migrating the database.");
        // Optionally, throw to stop startup if DB is critical
    }
}

// --- 6. Middleware Pipeline Configuration ---
// Order is important here!

// Use developer exception page for detailed errors in development environment.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    // Add production error handling middleware here (e.g., UseExceptionHandler("/Error"))
    app.UseExceptionHandler("/Error"); // Example - requires an /Error endpoint
    app.UseHsts(); // Enforce HTTPS Strict Transport Security
}

// Redirect HTTP requests to HTTPS.
app.UseHttpsRedirection();

// Enable serving static files (like admin.html, CSS, JS) from the wwwroot folder.
app.UseStaticFiles();

// Enable CORS - Must come before endpoints that need CORS.
app.UseCors();

// Optional: Add routing middleware if not already implicitly added by MapEndpoints
// app.UseRouting();

// Optional: Add Authentication/Authorization middleware here if securing endpoints
// app.UseAuthentication();
// app.UseAuthorization();

// --- 7. Endpoint Mapping ---

// --- Health Check Endpoint ---
app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }))
   .WithTags("Management") // Grouping for Swagger/OpenAPI
   .WithName("GetHealth"); // Unique name for the endpoint

// --- Admin Configuration API Endpoints ---
// Group admin endpoints for organization and potential shared configuration (like auth policies)
// !!! WARNING: These endpoints are NOT secured by default. Add authorization !!!
var adminApiGroup = app.MapGroup("/admin/config")
                       .WithTags("Admin Config API");
// .RequireAuthorization("AdminPolicy"); // Example of securing the group

// GET /admin/config - Retrieve the current full configuration
adminApiGroup.MapGet("/", (DynamicConfigurationService configService) =>
{
    Debug.WriteLine("Getting /admin/config");
    return Results.Ok(configService.GetCurrentConfig());
})
.WithName("GetFullConfig");

// POST /admin/config/{modelName} - Add or update the configuration for a specific model
adminApiGroup.MapPost("/{modelName}", (string modelName, [FromBody] ModelRoutingConfig modelConfig, DynamicConfigurationService configService, ILogger<Program> logger) =>
{
    Debug.WriteLine("Posting /admin/config");
    var decodedModelName = System.Net.WebUtility.UrlDecode(modelName); // Handle URL encoding
    logger.LogInformation("Received request to update config for model: {ModelName}", decodedModelName);

    if (string.IsNullOrWhiteSpace(decodedModelName) || modelConfig == null)
    {
        logger.LogWarning("Update config request failed: Invalid model name or missing body.");
        return Results.BadRequest("Model name and configuration body are required.");
    }
    // Consider adding more robust validation of the modelConfig object here

    bool success = configService.UpdateModelConfiguration(decodedModelName, modelConfig);

    if (success)
    {
        logger.LogInformation("Successfully updated config for model: {ModelName}", decodedModelName);
        return Results.Ok(new
        {
            Message = $"Configuration for model '{decodedModelName}' updated."
        });
    }
    else
    {
        // This might happen if saving the file fails, for example.
        logger.LogError("Failed to update configuration for model: {ModelName}", decodedModelName);
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
})
.WithName("UpdateModelConfig");

// DELETE /admin/config/{modelName} - Delete the configuration for a specific model
adminApiGroup.MapDelete("/{modelName}", (string modelName, DynamicConfigurationService configService, ILogger<Program> logger) =>
{
    Debug.WriteLine("Deleting /admin/config");
    var decodedModelName = System.Net.WebUtility.UrlDecode(modelName); // Handle URL encoding
    logger.LogInformation("Received request to delete config for model: {ModelName}", decodedModelName);

    if (string.IsNullOrWhiteSpace(decodedModelName))
    {
        logger.LogWarning("Delete config request failed: Invalid model name.");
        return Results.BadRequest("Model name is required.");
    }

    bool success = configService.DeleteModelConfiguration(decodedModelName);

    if (success)
    {
        logger.LogInformation("Successfully deleted config for model: {ModelName}", decodedModelName);
        return Results.Ok(new
        {
            Message = $"Configuration for model '{decodedModelName}' deleted."
        });
    }
    else
    {
        logger.LogWarning("Attempted to delete non-existent model configuration: {ModelName}", decodedModelName);
        return Results.NotFound(new
        {
            Message = $"Model '{decodedModelName}' not found."
        });
    }
})
.WithName("DeleteModelConfig");

// --- Admin UI Endpoint ---
// Redirects the base /admin path to the actual HTML file.
app.MapGet("/admin", (HttpContext context) =>
{
    Debug.WriteLine("Getting /admin");
    context.Response.Redirect("/admin.html", permanent: false); // Use temporary redirect
    return Task.CompletedTask;
})
.ExcludeFromDescription(); // Hide from Swagger/OpenAPI if it's just a redirect

// --- LLM Proxy Endpoints ---
var proxyApiGroup = app.MapGroup("/v1")
                       .WithTags("LLM Proxy API");

// --- Modify LLM Proxy Endpoints ---
// GET /v1/models - List models configured in the proxy (now includes model groups)
proxyApiGroup.MapGet("/models", (DynamicConfigurationService configService) =>
{
    Debug.WriteLine("Getting /v1/models (including groups)");
    var currentConfig = configService.GetCurrentConfig();

    var modelIds = currentConfig.Models.Keys.Select(id => new ModelInfo { Id = id });
    var groupIds = currentConfig.ModelGroups.Keys.Select(id => new ModelInfo { Id = id }); // Groups are also presented as "models"

    var allProxyModels = modelIds.Concat(groupIds)
        .OrderBy(m => m.Id)
        .ToList();

    var response = new ModelListResponse { Data = allProxyModels };
    return Results.Ok(response);
})
.WithName("GetModels"); // Name remains the same, but behavior is updated.

// Generic handler for POST proxy requests (Chat, Completions, Embeddings)
var proxyPostHandler = async (HttpContext context, DispatcherService dispatcher, ILogger<Program> logger) =>
{
    logger.LogDebug("Proxy request received for path: {Path}", context.Request.Path);
    // DispatcherService now handles reading body, routing, forwarding, and streaming
    await dispatcher.DispatchRequestAsync(context);
};

// Map specific POST endpoints to the generic handler
proxyApiGroup.MapPost("/chat/completions", proxyPostHandler).WithName("ProxyChatCompletions");
proxyApiGroup.MapPost("/completions", proxyPostHandler).WithName("ProxyCompletions"); // Legacy
proxyApiGroup.MapPost("/embeddings", proxyPostHandler).WithName("ProxyEmbeddings");

// --- Log API Endpoint ---
var logApiGroup = app.MapGroup("/admin/logs").WithTags("Admin Logs API");
// .RequireAuthorization("AdminPolicy"); // Secure this!

logApiGroup.MapGet("/", async (HttpContext httpContext, ProxyDbContext dbContext, int start = 0, int length = 10, string? search = null) =>
{
    // Basic pagination and search
    var query = dbContext.ApiLogEntries.AsQueryable();

    if (!string.IsNullOrWhiteSpace(search))
    {
        search = search.ToLower();
        query = query.Where(l =>
            (l.RequestPath != null && l.RequestPath.ToLower().Contains(search)) ||
            (l.RequestedModel != null && l.RequestedModel.ToLower().Contains(search)) ||
            (l.UpstreamBackendName != null && l.UpstreamBackendName.ToLower().Contains(search)) ||
            (l.ErrorMessage != null && l.ErrorMessage.ToLower().Contains(search))
        );
    }

    var totalRecords = await query.CountAsync();
    var data = await query.OrderByDescending(l => l.Timestamp)
                          .Skip(start)
                          .Take(length)
                          .Select(l => new // Select only needed fields for the summary list
                          {
                              l.Id,
                              Timestamp = l.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"), // Format for display
                              l.RequestPath,
                              l.RequestMethod,
                              l.RequestedModel,
                              l.UpstreamBackendName,
                              l.UpstreamStatusCode,
                              l.ProxyResponseStatusCode,
                              l.WasSuccess,
                              BriefError = l.ErrorMessage != null ? (l.ErrorMessage.Length > 70 ? l.ErrorMessage.Substring(0, 70) + "..." : l.ErrorMessage) : null
                          })
                          .ToListAsync();

    var draw = httpContext.Request.Query["draw"].FirstOrDefault();

    return Results.Ok(new
    {
        draw = Convert.ToInt32(draw),
        recordsTotal = totalRecords,
        recordsFiltered = totalRecords, // Simplified for now; full server-side search needs more
        data = data
    });
});

logApiGroup.MapGet("/{id:long}", async (long id, ProxyDbContext dbContext) =>
{
    var logEntry = await dbContext.ApiLogEntries.FindAsync(id);
    return logEntry == null ? Results.NotFound() : Results.Ok(logEntry); // Return full entry for detail view
});

// --- Admin Configuration API Endpoints for Model Groups ---
var adminGroupApiGroup = app.MapGroup("/admin/config/groups")
                           .WithTags("Admin Model Group API");

// GET /admin/config/groups - Retrieve all model group configurations
adminGroupApiGroup.MapGet("/", (DynamicConfigurationService configService) =>
{
    var config = configService.GetCurrentConfig();
    return Results.Ok(config.ModelGroups ?? new Dictionary<string, ModelGroupConfig>());
})
.WithName("GetAllModelGroupConfigs");

// GET /admin/config/groups/{groupName} - Retrieve a specific model group configuration
adminGroupApiGroup.MapGet("/{groupName}", (string groupName, DynamicConfigurationService configService) =>
{
    var decodedGroupName = System.Net.WebUtility.UrlDecode(groupName);
    if (configService.TryGetModelGroupRouting(decodedGroupName, out var groupConfig) && groupConfig != null)
    {
        return Results.Ok(groupConfig);
    }
    return Results.NotFound(new
    {
        Message = $"Model group '{decodedGroupName}' not found."
    });
})
.WithName("GetModelGroupConfig");

// POST /admin/config/groups/{groupName} - Add or update a model group's configuration
adminGroupApiGroup.MapPost("/{groupName}", (string groupName, [FromBody] ModelGroupConfig groupConfig, DynamicConfigurationService configService, ILogger<Program> logger) =>
{
    var decodedGroupName = System.Net.WebUtility.UrlDecode(groupName);
    logger.LogInformation("Received request to update config for model group: {GroupName}, Strategy: {Strategy}", decodedGroupName, groupConfig.Strategy);

    if (string.IsNullOrWhiteSpace(decodedGroupName))
    {
        logger.LogWarning("Update model group config request failed: Group name is required.");
        return Results.BadRequest("Group name is required.");
    }
    if (groupConfig == null)
    {
        logger.LogWarning("Update model group config request failed for '{GroupName}': Missing configuration body.", decodedGroupName);
        return Results.BadRequest("Configuration body is required.");
    }

    // Ensure lists are not null, even if empty, to simplify downstream logic in DynamicConfigurationService
    groupConfig.MemberModels ??= new List<ModelMemberConfig>();
    groupConfig.AgentModelNames ??= new List<string>();
    groupConfig.ContentRules ??= new List<ContentRule>();

    var currentModels = configService.GetCurrentConfig().Models; // Get all defined regular models

    // --- Strategy-Specific Validations ---

    if (groupConfig.Strategy == RoutingStrategyType.MixtureOfAgents)
    {
        // 1. Validate Orchestrator
        if (string.IsNullOrWhiteSpace(groupConfig.OrchestratorModelName) || !currentModels.ContainsKey(groupConfig.OrchestratorModelName))
        {
            logger.LogWarning("Update MoA group '{GroupName}' failed: Orchestrator model '{OrchestratorName}' is invalid or not defined.",
                decodedGroupName, groupConfig.OrchestratorModelName ?? "NULL");
            return Results.BadRequest($"Orchestrator model '{groupConfig.OrchestratorModelName ?? "NULL"}' is invalid or not defined. It must exist as a regular model.");
        }

        // 2. Validate AgentModelNames
        if (!groupConfig.AgentModelNames.Any()) // UI might enforce >= 2, but allow >=1 for API
        {
            logger.LogWarning("Update MoA group '{GroupName}' failed: At least one agent model is required in AgentModelNames.", decodedGroupName);
            return Results.BadRequest("MixtureOfAgents strategy requires at least one agent model in AgentModelNames.");
        }
        var invalidAgents = groupConfig.AgentModelNames
            .Where(name => string.IsNullOrWhiteSpace(name) || !currentModels.ContainsKey(name))
            .ToList();
        if (invalidAgents.Any())
        {
            logger.LogWarning("Update MoA group '{GroupName}' failed: Contains invalid or undefined agent models in AgentModelNames: {InvalidModels}",
                decodedGroupName, string.Join(", ", invalidAgents));
            return Results.BadRequest($"The following agent models in AgentModelNames are invalid or not defined: {string.Join(", ", invalidAgents)}. They must exist as regular models.");
        }
        // Check if orchestrator is also an agent
        if (groupConfig.AgentModelNames.Contains(groupConfig.OrchestratorModelName))
        {
            logger.LogWarning("Update MoA group '{GroupName}' failed: Orchestrator model '{OrchestratorName}' cannot also be listed as an agent model.",
                decodedGroupName, groupConfig.OrchestratorModelName);
            return Results.BadRequest($"The orchestrator model '{groupConfig.OrchestratorModelName}' cannot also be an agent model.");
        }
        // For MoA, MemberModels are not used for routing, so we don't need to validate them here for existence,
        // DynamicConfigurationService might clear them.
    }
    else // For Failover, RoundRobin, Weighted, ContentBased (these use MemberModels)
    {
        if (!groupConfig.MemberModels.Any())
        {
            logger.LogWarning("Update group '{GroupName}' (Strategy: {Strategy}) failed: At least one member model is required in MemberModels.",
                decodedGroupName, groupConfig.Strategy);
            return Results.BadRequest($"Strategy '{groupConfig.Strategy}' requires at least one member model in MemberModels.");
        }

        // Validate each MemberModel in MemberModels
        var invalidMembers = groupConfig.MemberModels
            .Where(m => string.IsNullOrWhiteSpace(m.Name) || !currentModels.ContainsKey(m.Name))
            .ToList();
        if (invalidMembers.Any())
        {
            var invalidNames = invalidMembers.Select(m => m.Name ?? "NULL_NAME");
            logger.LogWarning("Update group '{GroupName}' failed: MemberModels contains models not defined in main configuration: {InvalidModels}",
                decodedGroupName, string.Join(", ", invalidNames));
            return Results.BadRequest($"The following member models in MemberModels are not defined: {string.Join(", ", invalidNames)}. Please define them as regular models first.");
        }

        // Ensure weights are positive for MemberModels
        foreach (var member in groupConfig.MemberModels)
        {
            if (member.Weight < 1)
            {
                logger.LogInformation("Adjusting weight for member '{MemberName}' in group '{GroupName}' from {OriginalWeight} to 1.", member.Name, decodedGroupName, member.Weight);
                member.Weight = 1; // Auto-correct, or could return BadRequest
            }
        }

        if (groupConfig.Strategy == RoutingStrategyType.ContentBased)
        {
            var currentGroupMemberNames = groupConfig.MemberModels.Where(m => m.Enabled).Select(m => m.Name).ToList();
            if (!currentGroupMemberNames.Any() && (groupConfig.ContentRules.Any() || !string.IsNullOrWhiteSpace(groupConfig.DefaultModelForContentBased)))
            {
                logger.LogWarning("Update ContentBased group '{GroupName}' failed: No enabled member models available for content rules or default model.", decodedGroupName);
                return Results.BadRequest("ContentBased strategy requires at least one enabled member model if rules or a default model are set.");
            }

            foreach (var rule in groupConfig.ContentRules)
            {
                if (string.IsNullOrWhiteSpace(rule.RegexPattern))
                {
                    return Results.BadRequest($"Content rule in group '{decodedGroupName}' is missing a Regex pattern.");
                }
                if (string.IsNullOrWhiteSpace(rule.TargetModelName))
                {
                    return Results.BadRequest($"Content rule in group '{decodedGroupName}' (Regex: {rule.RegexPattern}) is missing a TargetModelName.");
                }
                if (!currentGroupMemberNames.Contains(rule.TargetModelName))
                {
                    return Results.BadRequest($"Content rule in group '{decodedGroupName}' targets model '{rule.TargetModelName}', which is not an enabled member of the group.");
                }
            }
            if (!string.IsNullOrWhiteSpace(groupConfig.DefaultModelForContentBased) && !currentGroupMemberNames.Contains(groupConfig.DefaultModelForContentBased))
            {
                return Results.BadRequest($"DefaultModelForContentBased '{groupConfig.DefaultModelForContentBased}' in group '{decodedGroupName}' is not an enabled member of the group.");
            }
        }
    }

    // --- End Strategy-Specific Validations ---

    bool success = configService.UpdateModelGroupConfiguration(decodedGroupName, groupConfig);

    if (success)
    {
        logger.LogInformation("Successfully updated configuration for model group: {GroupName}", decodedGroupName);
        return Results.Ok(new
        {
            Message = $"Configuration for model group '{decodedGroupName}' updated successfully."
        });
    }
    else
    {
        // This could be due to file save errors or other issues in DynamicConfigurationService not caught by prior validation
        logger.LogError("Failed to update configuration for model group: {GroupName} within DynamicConfigurationService.", decodedGroupName);
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
})
.WithName("UpdateModelGroupConfig");

// DELETE /admin/config/groups/{groupName} - Delete a model group's configuration
adminGroupApiGroup.MapDelete("/{groupName}", (string groupName, DynamicConfigurationService configService, ILogger<Program> logger) =>
{
    var decodedGroupName = System.Net.WebUtility.UrlDecode(groupName);
    logger.LogInformation("Received request to delete config for model group: {GroupName}", decodedGroupName);

    if (string.IsNullOrWhiteSpace(decodedGroupName))
    {
        logger.LogWarning("Delete model group config request failed: Invalid group name.");
        return Results.BadRequest("Group name is required.");
    }

    bool success = configService.DeleteModelGroupConfiguration(decodedGroupName);
    return success
        ? Results.Ok(new
        {
            Message = $"Configuration for model group '{decodedGroupName}' deleted."
        })
        : Results.NotFound(new
        {
            Message = $"Model group '{decodedGroupName}' not found."
        });
})
.WithName("DeleteModelGroupConfig");

// --- 8. Run the Application ---
app.Run(); // Starts listening for incoming HTTP requests
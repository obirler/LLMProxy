using System.Diagnostics;
using System.Text.Json.Serialization;

using LLMProxy.Models; // Required for RoutingConfig, ModelRoutingConfig, ModelInfo, ModelListResponse etc.
using LLMProxy.Services; // Required for DynamicConfigurationService, RoutingService, DispatcherService

using Microsoft.AspNetCore.Mvc; // Required for FromBody attribute and Results class

// --- 1. Application Builder Setup ---
var builder = WebApplication.CreateBuilder(args);

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

// GET /v1/models - List models configured in the proxy
proxyApiGroup.MapGet("/models", (DynamicConfigurationService configService) =>
{
    Debug.WriteLine("Getting /models");
    var currentConfig = configService.GetCurrentConfig();
    var modelList = currentConfig.Models.Keys
        .OrderBy(name => name)
        .Select(modelName => new ModelInfo { Id = modelName }) // Assumes ModelInfo sets defaults for Object and OwnedBy
        .ToList();

    var response = new ModelListResponse { Data = modelList };
    return Results.Ok(response);
})
.WithName("GetModels");

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

// --- 8. Run the Application ---
app.Run(); // Starts listening for incoming HTTP requests
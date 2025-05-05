using LLMProxy.Models;

using Microsoft.Extensions.Options;

namespace LLMProxy.Services;

public class ConfigurationService
{
    // Using IOptionsMonitor to allow for configuration hot-reloading if needed
    private readonly IOptionsMonitor<RoutingConfig> _routingConfigMonitor;

    // Simple locking for thread safety when updating internal state (like RoundRobin index)
    // A more robust solution might use ConcurrentDictionary or dedicated state management
    private static readonly object _configLock = new();

    public ConfigurationService(IOptionsMonitor<RoutingConfig> routingConfigMonitor)
    {
        _routingConfigMonitor = routingConfigMonitor;
        // Optional: Subscribe to changes if hot-reload is desired
        // _routingConfigMonitor.OnChange(newConfig => { /* Handle updates */ });
    }

    public RoutingConfig GetCurrentConfig()
    {
        // Return a snapshot of the current configuration
        return _routingConfigMonitor.CurrentValue;
    }

    public bool TryGetModelRouting(string modelName, out ModelRoutingConfig? modelConfig)
    {
        var config = GetCurrentConfig();
        return config.Models.TryGetValue(modelName, out modelConfig);
    }

    // NOTE: Updating config via API needs careful handling of concurrency and persistence.
    // This is a simplified example; a real implementation would likely involve saving
    // back to the source (appsettings.json, DB) and triggering IOptionsMonitor reload.
    public void UpdateConfig(RoutingConfig newConfig)
    {
        lock (_configLock)
        {
            // This only updates the in-memory representation reflected by _routingConfigMonitor
            // if configured correctly with reloadOnChange: true for the config source.
            // Persisting this change requires more setup (e.g., writing to appsettings.json
            // or a database and ensuring the configuration provider reloads it).
            // For simplicity, this example doesn't implement persistence.
            Console.WriteLine("Configuration update requested (Persistence not implemented in this example).");
            // To make IOptionsMonitor see the change *if* using reloadable providers:
            // 1. Save newConfig to the underlying source (e.g., appsettings.json file).
            // 2. The file provider should detect the change and reload IOptionsMonitor.
        }
    }
}
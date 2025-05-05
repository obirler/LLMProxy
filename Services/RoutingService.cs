// Services/RoutingService.cs
using LLMProxy.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent; // Use ConcurrentDictionary for state

namespace LLMProxy.Services;

public class RoutingService
{
    // Inject the new dynamic config service
    private readonly DynamicConfigurationService _configService;

    private readonly ILogger<RoutingService> _logger;

    // State for RoundRobin - needs to be thread-safe if service is singleton
    // Key: Model Name. Value: Index of the next backend to try.
    private readonly ConcurrentDictionary<string, int> _roundRobinBackendIndex = new();

    // Key: ModelName + "_" + BackendName. Value: Index of next API key.
    private readonly ConcurrentDictionary<string, int> _roundRobinApiKeyIndex = new();

    private static readonly object _lock = new(); // Still useful for complex logic like weighted selection if needed

    public RoutingService(DynamicConfigurationService configService, ILogger<RoutingService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public (BackendConfig? Backend, string? ApiKey) GetNextBackendAndKey(string modelName, List<BackendConfig> triedBackends)
    {
        // Get config dynamically on each request
        if (!_configService.TryGetModelRouting(modelName, out var modelConfig) || modelConfig == null)
        {
            _logger.LogWarning("No routing configuration found for model: {ModelName}", modelName);
            return (null, null);
        }

        var availableBackends = modelConfig.Backends
            .Where(b => b.Enabled && b.ApiKeys.Any() && !triedBackends.Any(tried => tried.Name == b.Name)) // Ensure enabled, has keys, and not already tried by Name
            .ToList();

        if (!availableBackends.Any())
        {
            _logger.LogWarning("No available backends left for model: {ModelName} after trying {TriedCount} backends.", modelName, triedBackends.Count);
            return (null, null);
        }

        BackendConfig? selectedBackend = null;
        string? selectedApiKey = null;

        // Lock only needed if modifying shared state in complex ways (like weighted with history)
        // For simple round-robin with ConcurrentDictionary, AddOrUpdate handles thread safety.

        switch (modelConfig.Strategy)
        {
            case RoutingStrategyType.Failover:
                selectedBackend = availableBackends.FirstOrDefault(); // Always try the first available in the list
                break;

            case RoutingStrategyType.RoundRobin:
                // Atomically get and update the index for the model's backend selection
                int backendIndex = _roundRobinBackendIndex.AddOrUpdate(modelName, 0, (key, oldValue) => (oldValue + 1));
                selectedBackend = availableBackends[backendIndex % availableBackends.Count];
                break;

            case RoutingStrategyType.Weighted:
                // Lock needed here because selection depends on multiple weights
                lock (_lock)
                {
                    selectedBackend = SelectWeightedBackend(availableBackends);
                }
                break;

            default:
                _logger.LogWarning("Unsupported strategy type {Strategy} for model {ModelName}. Falling back to Failover.", modelConfig.Strategy, modelName);
                selectedBackend = availableBackends.FirstOrDefault();
                break;
        }

        if (selectedBackend != null)
        {
            // Simple round-robin for API keys within the selected backend
            if (selectedBackend.ApiKeys.Any())
            {
                // Use a composite key for API key index state
                string apiKeyStateKey = $"{modelName}_{selectedBackend.Name}";
                int apiKeyIndex = _roundRobinApiKeyIndex.AddOrUpdate(apiKeyStateKey, 0, (key, oldValue) => (oldValue + 1));
                selectedApiKey = selectedBackend.ApiKeys[apiKeyIndex % selectedBackend.ApiKeys.Count];
            }
            else
            {
                _logger.LogWarning("Selected backend '{BackendName}' has no API keys configured.", selectedBackend.Name);
                // Let dispatcher handle potential failure
            }
        }

        if (selectedBackend != null)
        {
            _logger.LogInformation("Routing model {ModelName} to backend {BackendName} using strategy {Strategy}.", modelName, selectedBackend.Name, modelConfig.Strategy);
        }

        return (selectedBackend, selectedApiKey);
    }

    // SelectWeightedBackend remains the same as before
    private BackendConfig SelectWeightedBackend(List<BackendConfig> availableBackends)
    {
        int totalWeight = availableBackends.Sum(b => b.Weight);
        if (totalWeight <= 0)
            return availableBackends.FirstOrDefault()!;

        int randomValue = Random.Shared.Next(1, totalWeight + 1);
        int cumulativeWeight = 0;

        foreach (var backend in availableBackends)
        {
            cumulativeWeight += backend.Weight;
            if (randomValue <= cumulativeWeight)
            {
                return backend;
            }
        }
        return availableBackends.Last();
    }
}
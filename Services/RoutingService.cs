// Services/RoutingService.cs
using LLMProxy.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;

namespace LLMProxy.Services;

public class RoutingService
{
    #region Constructors

    public RoutingService(DynamicConfigurationService configService, ILogger<RoutingService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    #endregion Constructors

    #region Fields

    private static readonly object _weightedLock = new();

    private readonly DynamicConfigurationService _configService;

    private readonly ILogger<RoutingService> _logger;

    // State for RoundRobin - Model Name -> Index in the *original* configured list
    private readonly ConcurrentDictionary<string, int> _roundRobinStateIndex = new();

    // State for API Keys - Composite Key (Model_Backend) -> Index
    private readonly ConcurrentDictionary<string, int> _roundRobinApiKeyIndex = new();

    #endregion Fields

    #region Methods

    // Lock only for weighted selection logic
    public (BackendConfig? Backend, string? ApiKey) GetNextBackendAndKey(string modelName, List<BackendConfig> triedBackends)
    {
        if (!_configService.TryGetModelRouting(modelName, out var modelConfig) || modelConfig == null || !modelConfig.Backends.Any())
        {
            _logger.LogWarning("No valid routing configuration or backends found for model: {ModelName}", modelName);
            return (null, null);
        }

        // Get the full list of configured backends for this model
        var allConfiguredBackends = modelConfig.Backends;
        int totalConfiguredCount = allConfiguredBackends.Count;

        BackendConfig? selectedBackend = null;

        // --- Strategy-Specific Selection Logic ---

        switch (modelConfig.Strategy)
        {
            case RoutingStrategyType.Failover:
                // Find the first enabled backend that hasn't been tried yet
                selectedBackend = allConfiguredBackends.FirstOrDefault(b => b.Enabled && b.ApiKeys.Any() && !triedBackends.Any(tried => tried.Name == b.Name));
                break;

            case RoutingStrategyType.RoundRobin:
                // Get the current index, default to 0 if not present
                int startIndex = _roundRobinStateIndex.GetOrAdd(modelName, 0);
                int currentIndex = startIndex;
                int attempts = 0;

                // Loop through the configured backends starting from the current index,
                // wrapping around, until an available one is found or all are checked.
                do
                {
                    // Get backend based on current index in the *full* list
                    var candidateBackend = allConfiguredBackends[currentIndex % totalConfiguredCount];

                    // Check if it's enabled, has keys, and hasn't been tried in *this request*
                    if (candidateBackend.Enabled && candidateBackend.ApiKeys.Any() && !triedBackends.Any(tried => tried.Name == candidateBackend.Name))
                    {
                        selectedBackend = candidateBackend;
                        // Atomically update the index for the *next* request to start after this one
                        _roundRobinStateIndex.AddOrUpdate(modelName, (currentIndex + 1), (key, oldValue) => (currentIndex + 1));
                        break; // Found one
                    }

                    // Move to the next index and increment attempt count
                    currentIndex++;
                    attempts++;
                } while (selectedBackend == null && attempts < totalConfiguredCount); // Stop if we've checked all

                if (selectedBackend == null)
                {
                    _logger.LogWarning("RoundRobin: No available backend found for model {ModelName} after checking all options and considering tried backends.", modelName);
                }
                break;

            case RoutingStrategyType.Weighted:
                // Filter available backends first for weighted selection
                var availableForWeighted = allConfiguredBackends
                    .Where(b => b.Enabled && b.ApiKeys.Any() && !triedBackends.Any(tried => tried.Name == b.Name))
                    .ToList();

                if (availableForWeighted.Any())
                {
                    lock (_weightedLock) // Lock needed for random selection based on weights
                    {
                        selectedBackend = SelectWeightedBackend(availableForWeighted);
                    }
                }
                else
                {
                    _logger.LogWarning("Weighted: No available backend found for model {ModelName} after considering tried backends.", modelName);
                }
                break;

            default:
                _logger.LogWarning("Unsupported strategy type {Strategy} for model {ModelName}. Falling back to Failover.", modelConfig.Strategy, modelName);
                selectedBackend = allConfiguredBackends.FirstOrDefault(b => b.Enabled && b.ApiKeys.Any() && !triedBackends.Any(tried => tried.Name == b.Name));
                break;
        }

        // --- API Key Selection (Common Logic) ---
        string? selectedApiKey = null;
        if (selectedBackend != null)
        {
            if (selectedBackend.ApiKeys.Any())
            {
                string apiKeyStateKey = $"{modelName}_{selectedBackend.Name}";
                int apiKeyIndex = _roundRobinApiKeyIndex.AddOrUpdate(apiKeyStateKey, 0, (key, oldValue) => (oldValue + 1));
                selectedApiKey = selectedBackend.ApiKeys[apiKeyIndex % selectedBackend.ApiKeys.Count];
                _logger.LogInformation("Routing model {ModelName} to backend {BackendName} using strategy {Strategy}. Selected API Key Index: {ApiKeyIndex}", modelName, selectedBackend.Name, modelConfig.Strategy, apiKeyIndex % selectedBackend.ApiKeys.Count);
            }
            else
            {
                // This case should ideally be filtered out earlier, but log just in case
                _logger.LogWarning("Selected backend '{BackendName}' has no API keys configured, though it passed initial checks.", selectedBackend.Name);
            }
        }
        else
        {
            _logger.LogWarning("No backend selected for model {ModelName} based on strategy {Strategy} and tried backends.", modelName, modelConfig.Strategy);
        }

        return (selectedBackend, selectedApiKey);
    }

    // SelectWeightedBackend remains the same
    private BackendConfig SelectWeightedBackend(List<BackendConfig> availableBackends)
    {
        // ... (existing weighted selection logic) ...
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

    #endregion Methods
}
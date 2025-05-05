// Services/DynamicConfigurationService.cs
using LLMProxy.Models;
using System.Text.Json;
using System.Collections.Concurrent;

namespace LLMProxy.Services;

public class DynamicConfigurationService
{
    private readonly ILogger<DynamicConfigurationService> _logger;
    private readonly string _configFilePath;
    private ConcurrentDictionary<string, ModelRoutingConfig> _modelConfigs = new();
    private static readonly object _fileLock = new();

    public DynamicConfigurationService(ILogger<DynamicConfigurationService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configFilePath = Path.Combine(AppContext.BaseDirectory, "config", "dynamic_routing.json");
        EnsureConfigDirectoryExists();
        LoadConfigFromFile(); // This will now potentially load defaults
    }

    private void EnsureConfigDirectoryExists()
    {
        var dir = Path.GetDirectoryName(_configFilePath);
        if (dir != null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            _logger.LogInformation("Created configuration directory: {DirectoryPath}", dir);
        }
    }

    private void LoadConfigFromFile()
    {
        bool loadedFromFile = false;
        lock (_fileLock)
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    _logger.LogInformation("Attempting to load dynamic configuration from {FilePath}", _configFilePath);
                    string json = File.ReadAllText(_configFilePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var loadedConfig = JsonSerializer.Deserialize<RoutingConfig>(json);
                        if (loadedConfig?.Models != null && loadedConfig.Models.Any())
                        {
                            _modelConfigs = new ConcurrentDictionary<string, ModelRoutingConfig>(loadedConfig.Models);
                            _logger.LogInformation("Successfully loaded {Count} model configurations from file.", _modelConfigs.Count);
                            loadedFromFile = true; // Mark as loaded
                        }
                        else
                        {
                            _logger.LogWarning("Dynamic configuration file exists but was empty or invalid JSON structure.");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Dynamic configuration file exists but is empty.");
                    }
                }
                else
                {
                    _logger.LogInformation("Dynamic configuration file not found at {FilePath}.", _configFilePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading or parsing dynamic configuration file {FilePath}. Will proceed with defaults if applicable.", _configFilePath);
                // Ensure _modelConfigs is initialized even after error before potentially loading defaults
                _modelConfigs = new ConcurrentDictionary<string, ModelRoutingConfig>();
            }
        } // End lock

        // If nothing was loaded from the file, load defaults
        if (!loadedFromFile)
        {
            _logger.LogInformation("Loading default debug configurations as no valid configuration was loaded from file.");
            _modelConfigs = new ConcurrentDictionary<string, ModelRoutingConfig>(GetDefaultDebugConfig());

            // Optional: Save the defaults to the file immediately so they appear on next load
            // Be cautious if you *don't* want defaults to overwrite an intentionally empty file.
            // If you want an empty file to mean "no config", comment out the SaveConfigToFile() call below.
            if (_modelConfigs.Any())
            {
                _logger.LogInformation("Saving loaded default configurations to {FilePath}", _configFilePath);
                SaveConfigToFile();
            }
        }
    }

    // --- Keep SaveConfigToFile, GetCurrentConfig, TryGetModelRouting, UpdateModelConfiguration, DeleteModelConfiguration as they were ---
    private void SaveConfigToFile()
    {
        lock (_fileLock)
        {
            try
            {
                _logger.LogInformation("Saving dynamic configuration to {FilePath}", _configFilePath);
                var configToSave = new RoutingConfig { Models = new Dictionary<string, ModelRoutingConfig>(_modelConfigs) };
                string json = JsonSerializer.Serialize(configToSave, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configFilePath, json);
                _logger.LogInformation("Successfully saved dynamic configuration.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save dynamic configuration to {FilePath}", _configFilePath);
            }
        }
    }

    public RoutingConfig GetCurrentConfig()
    {
        return new RoutingConfig { Models = new Dictionary<string, ModelRoutingConfig>(_modelConfigs) };
    }

    public bool TryGetModelRouting(string modelName, out ModelRoutingConfig? modelConfig)
    {
        return _modelConfigs.TryGetValue(modelName, out modelConfig);
    }

    public bool UpdateModelConfiguration(string modelName, ModelRoutingConfig newConfig)
    {
        if (string.IsNullOrWhiteSpace(modelName) || newConfig == null)
            return false;
        if (newConfig.Backends == null)
            newConfig.Backends = new List<BackendConfig>();
        newConfig.Backends.ForEach(b =>
        {
            if (b.ApiKeys == null)
                b.ApiKeys = new List<string>();
            if (string.IsNullOrWhiteSpace(b.Name))
                b.Name = Guid.NewGuid().ToString("N").Substring(0, 8);
        });
        _modelConfigs[modelName] = newConfig;
        _logger.LogInformation("Updated configuration for model: {ModelName}", modelName);
        SaveConfigToFile();
        return true;
    }

    public bool DeleteModelConfiguration(string modelName)
    {
        if (_modelConfigs.TryRemove(modelName, out _))
        {
            _logger.LogInformation("Deleted configuration for model: {ModelName}", modelName);
            SaveConfigToFile();
            return true;
        }
        _logger.LogWarning("Attempted to delete non-existent model configuration: {ModelName}", modelName);
        return false;
    }

    // --- NEW METHOD: Define default configurations ---
    private Dictionary<string, ModelRoutingConfig> GetDefaultDebugConfig()
    {
        // IMPORTANT: Replace "YOUR_..." placeholders with dummy or real keys for testing.
        // Using placeholder strings will cause API calls to fail, but allows testing the UI.
        return new Dictionary<string, ModelRoutingConfig>
        {
            {
                "gpt-4-debug", new ModelRoutingConfig
                {
                    Strategy = RoutingStrategyType.Failover,
                    Backends = new List<BackendConfig>
                    {
                        new BackendConfig
                        {
                            Name = "OpenAI-Debug-1",
                            BaseUrl = "https://api.openai.com/v1",
                            ApiKeys = new List<string> { "YOUR_OPENAI_KEY_1_OR_PLACEHOLDER" },
                            Weight = 1,
                            Enabled = true
                        },
                        new BackendConfig
                        {
                            Name = "OpenAI-Debug-2",
                            BaseUrl = "https://api.openai.com/v1",
                            ApiKeys = new List<string> { "YOUR_OPENAI_KEY_2_OR_PLACEHOLDER" },
                            Weight = 1,
                            Enabled = true
                        }
                    }
                }
            },
            {
                "mistral-debug", new ModelRoutingConfig
                {
                    Strategy = RoutingStrategyType.RoundRobin,
                    Backends = new List<BackendConfig>
                    {
                        new BackendConfig
                        {
                            Name = "Mistral-Debug",
                            BaseUrl = "https://api.mistral.ai/v1", // Correct Mistral endpoint
                            ApiKeys = new List<string> { "YOUR_MISTRAL_KEY_OR_PLACEHOLDER" },
                            Weight = 1,
                            Enabled = true
                        }
                        // Add another Mistral backend/key here if you want to test RoundRobin properly
                    }
                }
            },
             {
                "deepseek-debug", new ModelRoutingConfig
                {
                    Strategy = RoutingStrategyType.Failover,
                    Backends = new List<BackendConfig>
                    {
                        new BackendConfig
                        {
                            Name = "DeepSeek-Debug",
                            BaseUrl = "https://api.deepseek.com/v1", // Correct DeepSeek endpoint
                            ApiKeys = new List<string> { "YOUR_DEEPSEEK_KEY_OR_PLACEHOLDER" },
                            Weight = 1,
                            Enabled = true
                        }
                    }
                }
            }
            // Add more default models as needed
        };
    }
}
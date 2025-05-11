// Services/DynamicConfigurationService.cs
using LLMProxy.Models;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Text.Json.Serialization; // Not strictly needed here as we load/save whole file

namespace LLMProxy.Services;

public class DynamicConfigurationService
{
    private readonly ILogger<DynamicConfigurationService> _logger;
    private readonly string _configFilePath;
    private RoutingConfig _currentConfig = new(); // Store the entire config object
    private static readonly object _fileLock = new();

    public DynamicConfigurationService(ILogger<DynamicConfigurationService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configFilePath = Path.Combine(AppContext.BaseDirectory, "config", "dynamic_routing.json");
        EnsureConfigDirectoryExists();
        LoadConfigFromFile();
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
                        var loadedConfig = JsonSerializer.Deserialize<RoutingConfig>(json, new JsonSerializerOptions
                        {
                            // Allow enums to be read as strings or numbers
                            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) }
                        });
                        if (loadedConfig != null)
                        {
                            _currentConfig = loadedConfig;
                            // Ensure dictionaries are initialized if null after deserialization
                            _currentConfig.Models ??= new Dictionary<string, ModelRoutingConfig>();
                            _currentConfig.ModelGroups ??= new Dictionary<string, ModelGroupConfig>();

                            _logger.LogInformation("Successfully loaded dynamic configuration. Models: {ModelCount}, Model Groups: {GroupCount}",
                                _currentConfig.Models.Count, _currentConfig.ModelGroups.Count);
                            loadedFromFile = true;
                        }
                        else
                        {
                            _logger.LogWarning("Dynamic configuration file existed but deserialized to null.");
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
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Error parsing dynamic configuration file {FilePath}. Check for malformed JSON (e.g. missing comma in sample provided by user for ModelGroups). Will proceed with defaults if applicable.", _configFilePath);
                _currentConfig = new RoutingConfig { Models = new Dictionary<string, ModelRoutingConfig>(), ModelGroups = new Dictionary<string, ModelGroupConfig>() };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Generic error reading dynamic configuration file {FilePath}. Will proceed with defaults if applicable.", _configFilePath);
                _currentConfig = new RoutingConfig { Models = new Dictionary<string, ModelRoutingConfig>(), ModelGroups = new Dictionary<string, ModelGroupConfig>() };
            }
        }

        if (!loadedFromFile)
        {
            _logger.LogInformation("Loading default debug configurations as no valid configuration was loaded from file.");
            _currentConfig = GetDefaultDebugConfig();
            if (_currentConfig.Models.Any() || _currentConfig.ModelGroups.Any())
            {
                _logger.LogInformation("Saving loaded default configurations to {FilePath}", _configFilePath);
                SaveConfigToFile();
            }
        }
    }

    private void SaveConfigToFile()
    {
        lock (_fileLock)
        {
            try
            {
                _logger.LogInformation("Saving dynamic configuration to {FilePath}", _configFilePath);
                string json = JsonSerializer.Serialize(_currentConfig, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) } // Ensure enums are written as strings
                });
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
        // Return a copy to prevent external modification of the live config object,
        // though consumers should ideally not modify it.
        // For simplicity, we'll return the direct reference for now, assuming services use it read-only mostly.
        // If deep cloning is needed:
        // var json = JsonSerializer.Serialize(_currentConfig);
        // return JsonSerializer.Deserialize<RoutingConfig>(json);
        return _currentConfig;
    }

    public bool TryGetModelRouting(string modelName, out ModelRoutingConfig? modelConfig)
    {
        return _currentConfig.Models.TryGetValue(modelName, out modelConfig);
    }

    public bool UpdateModelConfiguration(string modelName, ModelRoutingConfig newConfig)
    {
        if (string.IsNullOrWhiteSpace(modelName) || newConfig == null)
            return false;

        newConfig.Backends ??= new List<BackendConfig>();
        newConfig.Backends.ForEach(b =>
        {
            b.ApiKeys ??= new List<string>();
            if (string.IsNullOrWhiteSpace(b.Name))
                b.Name = Guid.NewGuid().ToString("N").Substring(0, 8); // Auto-generate name if empty
        });

        _currentConfig.Models[modelName] = newConfig;
        _logger.LogInformation("Updated configuration for model: {ModelName}", modelName);
        SaveConfigToFile();
        return true;
    }

    public bool DeleteModelConfiguration(string modelName)
    {
        if (_currentConfig.Models.Remove(modelName))
        {
            _logger.LogInformation("Deleted configuration for model: {ModelName}", modelName);
            SaveConfigToFile();
            return true;
        }
        _logger.LogWarning("Attempted to delete non-existent model configuration: {ModelName}", modelName);
        return false;
    }

    // --- New methods for Model Groups ---
    public bool TryGetModelGroupRouting(string groupName, out ModelGroupConfig? groupConfig)
    {
        return _currentConfig.ModelGroups.TryGetValue(groupName, out groupConfig);
    }

    public bool UpdateModelGroupConfiguration(string groupName, ModelGroupConfig newGroupConfig)
    {
        if (string.IsNullOrWhiteSpace(groupName) || newGroupConfig == null)
            return false;

        newGroupConfig.Models ??= new List<string>();
        newGroupConfig.ContentRules ??= new List<ContentRule>();
        // Basic validation for ContentRules and DefaultModelForContentBased
        newGroupConfig.ContentRules.ForEach(rule =>
        {
            if (string.IsNullOrWhiteSpace(rule.TargetModelName) || !newGroupConfig.Models.Contains(rule.TargetModelName))
            {
                _logger.LogWarning("Invalid TargetModelName '{TargetModel}' in ContentRule for group '{GroupName}'. It must be one of the group's member models.", rule.TargetModelName, groupName);
                // Decide: throw, remove rule, or log and proceed? For now, log. Admin UI should prevent this.
            }
        });
        if (!string.IsNullOrWhiteSpace(newGroupConfig.DefaultModelForContentBased) && !newGroupConfig.Models.Contains(newGroupConfig.DefaultModelForContentBased))
        {
            _logger.LogWarning("Invalid DefaultModelForContentBased '{DefaultModel}' for group '{GroupName}'. It must be one of the group's member models. Clearing it.", newGroupConfig.DefaultModelForContentBased, groupName);
            newGroupConfig.DefaultModelForContentBased = null; // Or handle error differently
        }

        _currentConfig.ModelGroups[groupName] = newGroupConfig;
        _logger.LogInformation("Updated configuration for model group: {GroupName}", groupName);
        SaveConfigToFile();
        return true;
    }

    public bool DeleteModelGroupConfiguration(string groupName)
    {
        if (_currentConfig.ModelGroups.Remove(groupName))
        {
            _logger.LogInformation("Deleted configuration for model group: {GroupName}", groupName);
            SaveConfigToFile();
            return true;
        }
        _logger.LogWarning("Attempted to delete non-existent model group configuration: {GroupName}", groupName);
        return false;
    }

    private RoutingConfig GetDefaultDebugConfig()
    {
        var defaultConfig = new RoutingConfig
        {
            Models = new Dictionary<string, ModelRoutingConfig>
            {
                {
                    "gpt-4-debug", new ModelRoutingConfig
                    {
                        Strategy = RoutingStrategyType.Failover,
                        Backends = new List<BackendConfig>
                        {
                            new BackendConfig { Name = "OpenAI-Debug-1", BaseUrl = "https://api.openai.com/v1", ApiKeys = new List<string> { "YOUR_OPENAI_KEY_1_OR_PLACEHOLDER" }, Enabled = true },
                            new BackendConfig { Name = "OpenAI-Debug-2", BaseUrl = "https://api.openai.com/v1", ApiKeys = new List<string> { "YOUR_OPENAI_KEY_2_OR_PLACEHOLDER" }, Enabled = true }
                        }
                    }
                },
                {
                    "mistral-debug", new ModelRoutingConfig
                    {
                        Strategy = RoutingStrategyType.RoundRobin,
                        Backends = new List<BackendConfig>
                        {
                            new BackendConfig { Name = "Mistral-Debug", BaseUrl = "https://api.mistral.ai/v1", ApiKeys = new List<string> { "YOUR_MISTRAL_KEY_OR_PLACEHOLDER" }, Enabled = true }
                        }
                    }
                },
                {
                    "phi-3-mini-debug", new ModelRoutingConfig
                    {
                        Strategy = RoutingStrategyType.Failover,
                        Backends = new List<BackendConfig>
                        {
                            new BackendConfig { Name = "LMStudio-Phi3", BaseUrl = "http://localhost:1234/v1", ApiKeys = new List<string> { "lm-studio" }, BackendModelName = "microsoft/Phi-3-mini-4k-instruct-gguf", Enabled = true }
                        }
                    }
                }
            },
            ModelGroups = new Dictionary<string, ModelGroupConfig>
            {
                {
                    "DebugCoderGroup", new ModelGroupConfig
                    {
                        Strategy = RoutingStrategyType.Failover,
                        Models = new List<string> { "mistral-debug", "gpt-4-debug" } // Assumes these models are defined above
                    }
                },
                {
                    "SmartChatGroup-Debug", new ModelGroupConfig
                    {
                        Strategy = RoutingStrategyType.ContentBased,
                        Models = new List<string> { "phi-3-mini-debug", "gpt-4-debug" }, // These models must be in the Models dict
                        ContentRules = new List<ContentRule>
                        {
                            new ContentRule { RegexPattern = "(summarize|summary)", TargetModelName = "phi-3-mini-debug", Priority = 0 },
                            new ContentRule { RegexPattern = "(code|python|javascript)", TargetModelName = "gpt-4-debug", Priority = 0 }
                        },
                        DefaultModelForContentBased = "phi-3-mini-debug"
                    }
                }
            }
        };
        // Validate that models referenced in groups exist
        foreach (var groupPair in defaultConfig.ModelGroups.ToList()) // ToList to allow modification
        {
            var group = groupPair.Value;
            group.Models.RemoveAll(modelName => !defaultConfig.Models.ContainsKey(modelName));
            group.ContentRules.RemoveAll(rule => !group.Models.Contains(rule.TargetModelName));
            if (!string.IsNullOrWhiteSpace(group.DefaultModelForContentBased) && !group.Models.Contains(group.DefaultModelForContentBased))
            {
                group.DefaultModelForContentBased = group.Models.FirstOrDefault();
            }
            if (!group.Models.Any()) // If a group has no valid member models, remove the group
            {
                defaultConfig.ModelGroups.Remove(groupPair.Key);
                _logger.LogWarning("Default debug group '{GroupName}' removed because it had no valid member models after validation.", groupPair.Key);
            }
        }
        return defaultConfig;
    }
}
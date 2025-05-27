// Services/DynamicConfigurationService.cs
using LLMProxy.Models;

using System.Text.Json;
using System.Text.Json.Serialization; // Required for JsonStringEnumConverter if not already there

namespace LLMProxy.Services;

public class DynamicConfigurationService
{
    private readonly ILogger<DynamicConfigurationService> _logger;
    private readonly string _configFilePath;
    private RoutingConfig _currentConfig = new();
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

    private JsonSerializerOptions GetJsonSerializerOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
            // DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull // Optional: to not write null fields
        };
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
                        var loadedConfig = JsonSerializer.Deserialize<RoutingConfig>(json, GetJsonSerializerOptions());
                        if (loadedConfig != null)
                        {
                            _currentConfig = loadedConfig;
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading or parsing dynamic configuration file {FilePath}. Will proceed with defaults if applicable.", _configFilePath);
                _currentConfig = new RoutingConfig { Models = new(), ModelGroups = new() };
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
                string json = JsonSerializer.Serialize(_currentConfig, GetJsonSerializerOptions());
                File.WriteAllText(_configFilePath, json);
                _logger.LogInformation("Successfully saved dynamic configuration.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save dynamic configuration to {FilePath}", _configFilePath);
            }
        }
    }

    public RoutingConfig GetCurrentConfig() => _currentConfig;

    public bool TryGetModelRouting(string modelName, out ModelRoutingConfig? modelConfig) =>
        _currentConfig.Models.TryGetValue(modelName, out modelConfig);

    public bool UpdateModelConfiguration(string modelName, ModelRoutingConfig newConfig)
    {
        if (string.IsNullOrWhiteSpace(modelName) || newConfig == null)
            return false;
        newConfig.Backends ??= new List<BackendConfig>();
        newConfig.Backends.ForEach(b =>
        {
            b.ApiKeys ??= new List<string>();
            if (string.IsNullOrWhiteSpace(b.Name))
                b.Name = Guid.NewGuid().ToString("N").Substring(0, 8);
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
            // Also remove this model from any group that might be using it
            foreach (var group in _currentConfig.ModelGroups.Values)
            {
                group.Models.RemoveAll(m => m == modelName);
                if (group.OrchestratorModelName == modelName)
                    group.OrchestratorModelName = null;
                if (group.Strategy == RoutingStrategyType.ContentBased)
                {
                    group.ContentRules.RemoveAll(r => r.TargetModelName == modelName);
                    if (group.DefaultModelForContentBased == modelName)
                        group.DefaultModelForContentBased = null;
                }
            }
            _logger.LogInformation("Deleted configuration for model: {ModelName} and cleaned from groups.", modelName);
            SaveConfigToFile();
            return true;
        }
        _logger.LogWarning("Attempted to delete non-existent model configuration: {ModelName}", modelName);
        return false;
    }

    public bool TryGetModelGroupRouting(string groupName, out ModelGroupConfig? groupConfig) =>
        _currentConfig.ModelGroups.TryGetValue(groupName, out groupConfig);

    public bool UpdateModelGroupConfiguration(string groupName, ModelGroupConfig newGroupConfig)
    {
        if (string.IsNullOrWhiteSpace(groupName) || newGroupConfig == null)
        {
            _logger.LogWarning("UpdateModelGroupConfiguration: Invalid group name or null config.");
            return false;
        }

        newGroupConfig.Models ??= new List<string>(); // Agent models for MoA, member models otherwise
        newGroupConfig.ContentRules ??= new List<ContentRule>();

        // --- Validation specific to MixtureOfAgents ---
        if (newGroupConfig.Strategy == RoutingStrategyType.MixtureOfAgents)
        {
            if (string.IsNullOrWhiteSpace(newGroupConfig.OrchestratorModelName))
            {
                _logger.LogWarning("Validation failed for MoA group '{GroupName}': OrchestratorModelName is required.", groupName);
                return false; // Orchestrator is mandatory
            }
            if (!_currentConfig.Models.ContainsKey(newGroupConfig.OrchestratorModelName))
            {
                _logger.LogWarning("Validation failed for MoA group '{GroupName}': OrchestratorModelName '{Orchestrator}' does not exist in defined Models.", groupName, newGroupConfig.OrchestratorModelName);
                return false;
            }
            if (newGroupConfig.Models.Count < 2) // `Models` list is used for AgentModelNames here
            {
                _logger.LogWarning("Validation failed for MoA group '{GroupName}': At least two Agent Models are required.", groupName);
                return false;
            }
            foreach (var agentModelName in newGroupConfig.Models)
            {
                if (!_currentConfig.Models.ContainsKey(agentModelName))
                {
                    _logger.LogWarning("Validation failed for MoA group '{GroupName}': AgentModelName '{AgentModel}' does not exist in defined Models.", groupName, agentModelName);
                    return false;
                }
                if (agentModelName == newGroupConfig.OrchestratorModelName)
                {
                    _logger.LogWarning("Validation failed for MoA group '{GroupName}': OrchestratorModelName '{Orchestrator}' cannot also be an AgentModel.", groupName, newGroupConfig.OrchestratorModelName);
                    return false;
                }
            }
            // For MoA, ContentRules and DefaultModelForContentBased are not used, clear them.
            newGroupConfig.ContentRules.Clear();
            newGroupConfig.DefaultModelForContentBased = null;
        }
        // --- Validation for ContentBased (existing) ---
        else if (newGroupConfig.Strategy == RoutingStrategyType.ContentBased)
        {
            newGroupConfig.OrchestratorModelName = null; // Not used for ContentBased
            // (Existing ContentBased validation logic...)
            foreach (var rule in newGroupConfig.ContentRules)
            {
                if (!newGroupConfig.Models.Contains(rule.TargetModelName))
                {
                    _logger.LogWarning("Invalid TargetModelName '{TargetModel}' in ContentRule for group '{GroupName}'.", rule.TargetModelName, groupName);
                    return false;
                }
            }
            if (!string.IsNullOrWhiteSpace(newGroupConfig.DefaultModelForContentBased) && !newGroupConfig.Models.Contains(newGroupConfig.DefaultModelForContentBased))
            {
                _logger.LogWarning("Invalid DefaultModelForContentBased '{DefaultModel}' for group '{GroupName}'. Clearing it.", newGroupConfig.DefaultModelForContentBased, groupName);
                newGroupConfig.DefaultModelForContentBased = null;
            }
        }
        else // For Failover, RoundRobin, Weighted
        {
            newGroupConfig.OrchestratorModelName = null;
            newGroupConfig.ContentRules.Clear();
            newGroupConfig.DefaultModelForContentBased = null;
        }

        _currentConfig.ModelGroups[groupName] = newGroupConfig;
        _logger.LogInformation("Updated configuration for model group: {GroupName} with strategy {Strategy}", groupName, newGroupConfig.Strategy);
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
                { "gpt-4-debug", new ModelRoutingConfig { Strategy = RoutingStrategyType.Failover, Backends = new List<BackendConfig> { new BackendConfig { Name = "OpenAI-GPT4-D1", BaseUrl = "https://api.openai.com/v1", ApiKeys = new List<string> { "YOUR_KEY" }, BackendModelName="gpt-4" } } } },
                { "gpt-3.5-turbo-debug", new ModelRoutingConfig { Strategy = RoutingStrategyType.Failover, Backends = new List<BackendConfig> { new BackendConfig { Name = "OpenAI-GPT35T-D1", BaseUrl = "https://api.openai.com/v1", ApiKeys = new List<string> { "YOUR_KEY" }, BackendModelName="gpt-3.5-turbo" } } } },
                { "claude-haiku-debug", new ModelRoutingConfig { Strategy = RoutingStrategyType.Failover, Backends = new List<BackendConfig> { new BackendConfig { Name = "Anthropic-Haiku-D1", BaseUrl = "https://api.anthropic.com/v1", ApiKeys = new List<string> { "YOUR_ANTHROPIC_KEY" }, BackendModelName="claude-3-haiku-20240307" } } } }, // Placeholder URL, needs Anthropic SDK or compatible proxy
                { "phi-3-mini-debug", new ModelRoutingConfig { Strategy = RoutingStrategyType.Failover, Backends = new List<BackendConfig> { new BackendConfig { Name = "LMStudio-Phi3D", BaseUrl = "http://localhost:1234/v1", ApiKeys = new List<string> { "lm-studio" }, BackendModelName = "microsoft/Phi-3-mini-4k-instruct-gguf" } } } }
            },
            ModelGroups = new Dictionary<string, ModelGroupConfig>
            {
                { "DebugFailoverGroup", new ModelGroupConfig { Strategy = RoutingStrategyType.Failover, Models = new List<string> { "phi-3-mini-debug", "gpt-3.5-turbo-debug" } } },
                {
                    "MoA-Debug-Group", new ModelGroupConfig
                    {
                        Strategy = RoutingStrategyType.MixtureOfAgents,
                        OrchestratorModelName = "gpt-4-debug", // Must exist in Models
                        Models = new List<string> { "gpt-3.5-turbo-debug", "claude-haiku-debug" } // Agent models, must exist in Models
                    }
                }
            }
        };
        // Basic validation for default config
        foreach (var groupPair in defaultConfig.ModelGroups.ToList())
        {
            var group = groupPair.Value;
            group.Models.RemoveAll(modelName => !defaultConfig.Models.ContainsKey(modelName));
            if (group.Strategy == RoutingStrategyType.MixtureOfAgents)
            {
                if (string.IsNullOrWhiteSpace(group.OrchestratorModelName) || !defaultConfig.Models.ContainsKey(group.OrchestratorModelName) || group.Models.Count < 1) // Min 1 agent for default, UI will enforce 2
                {
                    _logger.LogWarning("Default MoA group '{GroupName}' invalid, removing.", groupPair.Key);
                    defaultConfig.ModelGroups.Remove(groupPair.Key);
                    continue;
                }
            }
            if (!group.Models.Any() && group.Strategy != RoutingStrategyType.MixtureOfAgents) // MoA agents are in Models list
            {
                if (group.Strategy == RoutingStrategyType.MixtureOfAgents && (string.IsNullOrWhiteSpace(group.OrchestratorModelName) || group.Models.Count == 0))
                {
                } // Already handled
                else if (group.Strategy != RoutingStrategyType.MixtureOfAgents)
                {
                    _logger.LogWarning("Default group '{GroupName}' has no valid member models, removing.", groupPair.Key);
                    defaultConfig.ModelGroups.Remove(groupPair.Key);
                }
            }
        }
        return defaultConfig;
    }
}
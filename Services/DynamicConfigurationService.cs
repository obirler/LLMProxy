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

    public bool DeleteModelConfiguration(string modelName) // When deleting a model, remove it from group members/agents
    {
        if (_currentConfig.Models.Remove(modelName))
        {
            foreach (var group in _currentConfig.ModelGroups.Values)
            {
                group.MemberModels.RemoveAll(m => m.Name == modelName);
                group.AgentModelNames.RemoveAll(name => name == modelName); // For MoA
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

        // Initialize new lists if null
        newGroupConfig.MemberModels ??= new List<ModelMemberConfig>();
        newGroupConfig.AgentModelNames ??= new List<string>();
        newGroupConfig.ContentRules ??= new List<ContentRule>();

        // Validate that member/agent models exist in the main Models configuration
        var currentModels = _currentConfig.Models; // Get current models from the service's state

        // Validate MemberModels (used by Failover, RR, Weighted, ContentBased targets)
        foreach (var member in newGroupConfig.MemberModels)
        {
            if (string.IsNullOrWhiteSpace(member.Name) || !currentModels.ContainsKey(member.Name))
            {
                _logger.LogWarning("Validation failed for group '{GroupName}': MemberModel '{MemberName}' is invalid or does not exist in defined Models.", groupName, member.Name ?? "NULL_NAME");
                return false;
            }
            if (member.Weight < 1)
                member.Weight = 1; // Ensure weight is at least 1
        }

        if (newGroupConfig.Strategy == RoutingStrategyType.MixtureOfAgents)
        {
            if (string.IsNullOrWhiteSpace(newGroupConfig.OrchestratorModelName) || !currentModels.ContainsKey(newGroupConfig.OrchestratorModelName))
            {
                _logger.LogWarning("Validation failed for MoA group '{GroupName}': OrchestratorModelName '{Orchestrator}' is invalid or does not exist.", groupName, newGroupConfig.OrchestratorModelName ?? "NULL");
                return false;
            }
            if (newGroupConfig.AgentModelNames.Count < 1) // UI might enforce 2, but allow 1 for programmatic config
            {
                _logger.LogWarning("Validation failed for MoA group '{GroupName}': At least one Agent Model in AgentModelNames is required.", groupName);
                return false;
            }
            foreach (var agentModelName in newGroupConfig.AgentModelNames)
            {
                if (string.IsNullOrWhiteSpace(agentModelName) || !currentModels.ContainsKey(agentModelName))
                {
                    _logger.LogWarning("Validation failed for MoA group '{GroupName}': AgentModelName '{AgentModel}' in AgentModelNames is invalid or does not exist.", groupName, agentModelName ?? "NULL");
                    return false;
                }
                if (agentModelName == newGroupConfig.OrchestratorModelName)
                {
                    _logger.LogWarning("Validation failed for MoA group '{GroupName}': OrchestratorModelName '{Orchestrator}' cannot also be an AgentModel.", groupName, newGroupConfig.OrchestratorModelName);
                    return false;
                }
            }
            // For MoA, MemberModels, ContentRules and DefaultModelForContentBased are not directly used by the MoA routing logic itself.
            // Clear them to avoid confusion, or the UI should hide them.
            newGroupConfig.MemberModels.Clear();
            newGroupConfig.ContentRules.Clear();
            newGroupConfig.DefaultModelForContentBased = null;
        }
        else if (newGroupConfig.Strategy == RoutingStrategyType.ContentBased)
        {
            newGroupConfig.OrchestratorModelName = null;
            newGroupConfig.AgentModelNames.Clear();
            var currentGroupMemberNames = newGroupConfig.MemberModels.Select(m => m.Name).ToList();
            foreach (var rule in newGroupConfig.ContentRules)
            {
                if (!currentGroupMemberNames.Contains(rule.TargetModelName))
                {
                    _logger.LogWarning("Invalid TargetModelName '{TargetModel}' in ContentRule for group '{GroupName}'. Not in MemberModels.", rule.TargetModelName, groupName);
                    return false;
                }
            }
            if (!string.IsNullOrWhiteSpace(newGroupConfig.DefaultModelForContentBased) && !currentGroupMemberNames.Contains(newGroupConfig.DefaultModelForContentBased))
            {
                _logger.LogWarning("Invalid DefaultModelForContentBased '{DefaultModel}' for group '{GroupName}'. Not in MemberModels. Clearing it.", newGroupConfig.DefaultModelForContentBased, groupName);
                newGroupConfig.DefaultModelForContentBased = null; // Or return false
            }
        }
        else // For Failover, RoundRobin, Weighted (non-MoA, non-ContentBased)
        {
            newGroupConfig.OrchestratorModelName = null;
            newGroupConfig.AgentModelNames.Clear();
            newGroupConfig.ContentRules.Clear();
            newGroupConfig.DefaultModelForContentBased = null;
            if (!newGroupConfig.MemberModels.Any() && newGroupConfig.Strategy != RoutingStrategyType.MixtureOfAgents /*MoA handled above*/)
            {
                _logger.LogWarning("Validation failed for group '{GroupName}' (Strategy: {Strategy}): At least one MemberModel is required.", groupName, newGroupConfig.Strategy);
                return false;
            }
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
            { "claude-haiku-debug", new ModelRoutingConfig { Strategy = RoutingStrategyType.Failover, Backends = new List<BackendConfig> { new BackendConfig { Name = "Anthropic-Haiku-D1", BaseUrl = "https://api.anthropic.com/v1", ApiKeys = new List<string> { "YOUR_ANTHROPIC_KEY" }, BackendModelName="claude-3-haiku-20240307" } } } },
            { "phi-3-mini-debug", new ModelRoutingConfig { Strategy = RoutingStrategyType.Failover, Backends = new List<BackendConfig> { new BackendConfig { Name = "LMStudio-Phi3D", BaseUrl = "http://localhost:1234/v1", ApiKeys = new List<string> { "lm-studio" }, BackendModelName = "microsoft/Phi-3-mini-4k-instruct-gguf" } } } }
        },
            ModelGroups = new Dictionary<string, ModelGroupConfig>
        {
            {
                "DebugWeightedGroup", new ModelGroupConfig
                {
                    Strategy = RoutingStrategyType.Weighted,
                    MemberModels = new List<ModelMemberConfig> // Use new structure
                    {
                        new ModelMemberConfig { Name = "phi-3-mini-debug", Weight = 3 }, // phi-3 gets 3x weight
                        new ModelMemberConfig { Name = "gpt-3.5-turbo-debug", Weight = 1 }
                    }
                }
            },
            {
                "MoA-Debug-Group", new ModelGroupConfig
                {
                    Strategy = RoutingStrategyType.MixtureOfAgents,
                    OrchestratorModelName = "gpt-4-debug",
                    AgentModelNames = new List<string> { "gpt-3.5-turbo-debug", "claude-haiku-debug" } // Use AgentModelNames
                }
            }
        }
        };
        // Basic validation for default config
        foreach (var groupPair in defaultConfig.ModelGroups.ToList())
        {
            var group = groupPair.Value;
            // Validate MemberModels
            group.MemberModels.RemoveAll(member => !defaultConfig.Models.ContainsKey(member.Name));
            // Validate AgentModelNames for MoA
            if (group.Strategy == RoutingStrategyType.MixtureOfAgents)
            {
                group.AgentModelNames.RemoveAll(agentName => !defaultConfig.Models.ContainsKey(agentName));
                if (string.IsNullOrWhiteSpace(group.OrchestratorModelName) ||
                    !defaultConfig.Models.ContainsKey(group.OrchestratorModelName) ||
                    group.AgentModelNames.Count < 1)
                {
                    _logger.LogWarning("Default MoA group '{GroupName}' invalid (orchestrator or agents), removing.", groupPair.Key);
                    defaultConfig.ModelGroups.Remove(groupPair.Key);
                    continue;
                }
            }
            else // For non-MoA strategies
            {
                if (!group.MemberModels.Any())
                {
                    _logger.LogWarning("Default group '{GroupName}' (Strategy: {Strategy}) has no valid member models, removing.", groupPair.Key, group.Strategy);
                    defaultConfig.ModelGroups.Remove(groupPair.Key);
                }
            }
        }
        return defaultConfig;
    }
}
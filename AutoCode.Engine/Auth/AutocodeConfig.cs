using System.Text.Json;
using System.Text.Json.Serialization;
using AutoCode.Engine.Agent;

namespace AutoCode.Engine.Auth;

public sealed class AutocodeConfig
{
    public string? DefaultProvider { get; set; } = "anthropic";

    public string? DefaultModel { get; set; } = "claude-opus-4-7";

    public Dictionary<string, string> ApiKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? ProxyToken { get; set; }

    public string? ProxyBaseUrl { get; set; }

    /// <summary>When false, the proxy token is ignored and BYOK keys are used instead.</summary>
    public bool UseProxy { get; set; } = true;

    /// <summary>Non-secret cached account display fields (tokens live in the DPAPI session file).</summary>
    public string? AccountEmail { get; set; }

    public string? AccountDisplayName { get; set; }

    public string? AccountPhotoUrl { get; set; }

    /// <summary>True once the user has dismissed/skipped the optional sign-in prompt.</summary>
    public bool LoginPromptSeen { get; set; }

    public string? VerifyCommand { get; set; }

    public bool AutoVerify { get; set; } = true;

    public string Theme { get; set; } = "light";

    public double TextScale { get; set; } = 1.0;

    public bool KeepAwakeEnabled { get; set; }

    /// <summary>Voice-to-text backend selection as "prefix:model" (e.g. "openai:whisper-1"). Null = auto-select best available.</summary>
    public string? TranscriptionProvider { get; set; }

    /// <summary>When true, a finished voice transcript is submitted immediately; otherwise it is inserted into the prompt box for review.</summary>
    public bool AutoSubmitVoice { get; set; }

    public WebToolsConfig WebTools { get; set; } = new();

    public ModelConfig ToModelConfig() =>
        new(DefaultProvider ?? "anthropic", DefaultModel ?? "claude-opus-4-7");
}

public sealed class WebToolsConfig
{
    public bool Enabled { get; set; } = true;

    public bool AllowHttp { get; set; }

    public bool BlockPrivateHosts { get; set; } = true;

    public List<string> ExtraAllowedHosts { get; set; } = [];

    public List<string> ExtraBlockedHosts { get; set; } = [];
}

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ConfigStore(string? configDirectory = null)
    {
        ConfigDirectory = configDirectory ?? Paths.ConfigDirectory();
        Directory.CreateDirectory(ConfigDirectory);
        ConfigPath = Path.Combine(ConfigDirectory, "config.json");
    }

    public string ConfigDirectory { get; }

    public string ConfigPath { get; }

    public AutocodeConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return new AutocodeConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<AutocodeConfig>(File.ReadAllText(ConfigPath), JsonOptions)
                ?? new AutocodeConfig();
        }
        catch
        {
            return new AutocodeConfig();
        }
    }

    public void Save(AutocodeConfig config)
    {
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
    }
}

public static class Paths
{
    public static string DataDirectory()
    {
        var overrideValue = Environment.GetEnvironmentVariable("AUTOCODE_GUI_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            return Path.GetFullPath(overrideValue);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "autocode-gui");
    }

    public static string ConfigDirectory()
    {
        var overrideValue = Environment.GetEnvironmentVariable("AUTOCODE_GUI_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            return Path.GetFullPath(overrideValue);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".autocode-gui");
    }

    public static string SessionsDirectory() => Path.Combine(DataDirectory(), "sessions");
}

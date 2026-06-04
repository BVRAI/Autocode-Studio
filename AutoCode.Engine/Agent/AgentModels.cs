namespace AutoCode.Engine.Agent;

public enum AgentMode
{
    Planning,
    Default,
    Autocode,
    Admin
}

public sealed record ModelConfig(string Provider, string Model);

public sealed record SessionContext(
    string SessionId,
    string ProjectRoot,
    string DataDir,
    string SessionDir,
    ModelConfig Model,
    DateTimeOffset StartedAt,
    AgentMode Mode)
{
    /// <summary>Sampling temperature; null keeps the provider/request default (1.0).</summary>
    public double? Temperature { get; init; }

    /// <summary>Per-turn USD cost ceiling; when exceeded the loop stops before the next call. Null = unlimited.</summary>
    public double? MaxCostUsd { get; init; }

    /// <summary>Per-turn iteration override; null uses the default backstop.</summary>
    public int? MaxIterations { get; init; }

    public SessionContext WithMode(AgentMode mode) => this with { Mode = mode };

    public SessionContext WithModel(ModelConfig model) => this with { Model = model };

    public SessionContext WithProjectRoot(string projectRoot) => this with { ProjectRoot = Path.GetFullPath(projectRoot) };
}

public static class SessionIds
{
    public static string NewId(DateTimeOffset? now = null)
    {
        var t = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        var suffix = Guid.NewGuid().ToString("N")[..6];
        return $"{t:yyyyMMdd-HHmmss}-{suffix}";
    }
}

public static class AgentModeExtensions
{
    public static AgentMode Next(this AgentMode mode) =>
        mode switch
        {
            AgentMode.Default => AgentMode.Autocode,
            AgentMode.Autocode => AgentMode.Planning,
            AgentMode.Planning => AgentMode.Default,
            AgentMode.Admin => AgentMode.Default,
            _ => AgentMode.Default
        };

    public static string WireName(this AgentMode mode) =>
        mode switch
        {
            AgentMode.Planning => "planning",
            AgentMode.Default => "default",
            AgentMode.Autocode => "autocode",
            AgentMode.Admin => "admin",
            _ => "default"
        };

    public static AgentMode Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "planning" => AgentMode.Planning,
            "autocode" => AgentMode.Autocode,
            "admin" => AgentMode.Admin,
            _ => AgentMode.Default
        };
}

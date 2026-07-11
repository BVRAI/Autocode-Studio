namespace AutoCode.Engine.Backends;

/// <summary>A selectable mode for an agent harness: the wire value stored per session and passed
/// to the backend, an English label/description (the shell may localize), and an icon key.</summary>
public sealed record AgentModeInfo(string Wire, string Label, string Description, string GlyphKey);

/// <summary>A selectable model for an external harness. "default" defers to the CLI's own config —
/// the maintenance-proof choice; Claude Code aliases (opus/sonnet/haiku) resolve server-side to the
/// latest model, so neither list needs upkeep as vendors ship new models.</summary>
public sealed record AgentModelInfo(string Id, string Label);

/// <summary>
/// Static metadata for the agent harnesses: which modes and models each supports. Mode sets are
/// small and version-stable (flag spellings probe-verified against the installed CLIs, 2026-07);
/// the builtin wires match <see cref="Agent.AgentModeExtensions"/>. Lives in the engine per the
/// "model/agent metadata only in the engine" rule; the shell renders menus from it.
/// </summary>
public static class AgentCatalog
{
    public const string Builtin = "builtin";
    public const string ClaudeCode = "claude-code";
    public const string Codex = "codex";

    private static readonly AgentModeInfo[] BuiltinModes =
    [
        new("default", "Default", "Edits & shell require approval", "IconShieldCheck"),
        new("autocode", "Auto", "Auto-approve file edits & shell", "IconBolt"),
        new("planning", "Plan only", "Read-only — no mutations", "IconPlan"),
        new("admin", "Admin", "Unrestricted, incl. system ops", "IconCrown"),
    ];

    // Claude Code headless (-p) can't pause for permission prompts, so "manual" needs an approval
    // bridge (backlog); the flag-mapped trio below covers plan / acceptEdits / skip-permissions.
    private static readonly AgentModeInfo[] ClaudeCodeModes =
    [
        new("plan", "Plan", "Read-only — plans, no edits", "IconPlan"),
        new("accept-edits", "Accept edits", "File edits auto-approved", "IconShieldCheck"),
        new("auto", "Auto", "All permission checks bypassed", "IconBolt"),
    ];

    private static readonly AgentModeInfo[] CodexModes =
    [
        new("read-only", "Read only", "Sandboxed — no writes or commands", "IconPlan"),
        new("auto", "Auto", "Sandboxed writes inside the workspace", "IconBolt"),
        new("full-access", "Full access", "No sandbox — everything allowed", "IconCrown"),
    ];

    private static readonly AgentModelInfo[] ClaudeCodeModels =
    [
        new("default", "Default (CLI setting)"),
        new("opus", "Opus (latest)"),
        new("sonnet", "Sonnet (latest)"),
        new("haiku", "Haiku (latest)"),
    ];

    private static readonly AgentModelInfo[] CodexModels =
    [
        new("default", "Default (CLI setting)"),
    ];

    public static IReadOnlyList<AgentModeInfo> ModesFor(string? agentId) => agentId switch
    {
        ClaudeCode => ClaudeCodeModes,
        Codex => CodexModes,
        _ => BuiltinModes,
    };

    /// <summary>The mode a fresh session starts in. External defaults preserve the pre-catalog
    /// behavior (fully autonomous CLI runs).</summary>
    public static string DefaultWireFor(string? agentId) => agentId switch
    {
        ClaudeCode => "auto",
        Codex => "full-access",
        _ => "default",
    };

    /// <summary>The harness's fully-autonomous mode — used when an orchestrator (manager dispatch)
    /// overrides a member's mode so an approved task runs without further prompts.</summary>
    public static string AutoWireFor(string? agentId) => agentId switch
    {
        ClaudeCode => "auto",
        Codex => "full-access",
        _ => "autocode",
    };

    /// <summary>Models for external harnesses; the builtin harness uses <see cref="Llm.ModelCatalog"/>.</summary>
    public static IReadOnlyList<AgentModelInfo> ModelsFor(string? agentId) => agentId switch
    {
        ClaudeCode => ClaudeCodeModels,
        Codex => CodexModels,
        _ => [],
    };
}

using AutoCode.Engine.Agent;

namespace AutoCode.Engine.Backends;

/// <summary>
/// The thing that drives one workspace's turns and emits <c>AgentEvent</c>s. The built-in engine is
/// one implementation (<see cref="BuiltinAgentBackend"/>); external CLI agents (Claude Code, Codex)
/// run in a worktree and parse their output into the same event stream behind this same seam — so the
/// UI renders every backend identically and a workspace can pick which agent runs it.
///
/// Construction wiring (the emit/approve/confirm/choose callbacks, the CLI path, etc.) is each
/// backend's own concern; this interface is only the per-turn contract the shell drives.
/// </summary>
public interface IAgentBackend
{
    /// <summary>Stable identifier: "builtin" | "claude-code" | "codex".</summary>
    string Id { get; }

    /// <summary>Human label for the agent picker (e.g. "Built-in", "Claude Code", "Codex").</summary>
    string DisplayName { get; }

    /// <summary>Cumulative token usage when the backend reports it; (0, 0) when it doesn't.</summary>
    (int InputTokens, int OutputTokens) CumulativeUsage { get; }

    /// <summary>Run one user turn against <paramref name="context"/> (its ProjectRoot is the work dir),
    /// emitting events through the callbacks the backend was constructed with.</summary>
    Task SubmitAsync(string input, SessionContext context, CancellationToken cancellationToken);

    /// <summary>Cooperatively cancel the in-flight turn.</summary>
    void Cancel();

    /// <summary>Rehydrate prior conversation on reopen (built-in restores in-memory history; external
    /// backends may resume by session id or no-op).</summary>
    void LoadHistory(IEnumerable<(string Role, string Text)> history);

    /// <summary>Clear the conversation; returns the number of entries cleared.</summary>
    int ClearConversation();
}

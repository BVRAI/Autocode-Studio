using AutoCode.Engine.Agent;

namespace AutoCode.Engine.Backends;

/// <summary>
/// The native AutoCode engine as a backend — a thin adapter over <see cref="AgentLoop"/>. This is the
/// default agent; external backends (Claude Code / Codex) implement the same <see cref="IAgentBackend"/>
/// seam so the shell drives every workspace the same way.
/// </summary>
public sealed class BuiltinAgentBackend : IAgentBackend
{
    private readonly AgentLoop _loop;

    public BuiltinAgentBackend(AgentLoop loop) => _loop = loop;

    public string Id => "builtin";

    public string DisplayName => "Built-in";

    public (int InputTokens, int OutputTokens) CumulativeUsage => _loop.CumulativeUsage;

    public Task SubmitAsync(string input, SessionContext context, CancellationToken cancellationToken)
        => _loop.SubmitAsync(input, context, cancellationToken);

    public void Cancel() => _loop.Cancel();

    public void LoadHistory(IEnumerable<(string Role, string Text)> history) => _loop.LoadHistory(history);

    public int ClearConversation() => _loop.ClearConversation();

    // The built-in engine rehydrates via LoadHistory; it has no external continuity handle.
    public string? ResumeId
    {
        get => null;
        set { }
    }
}

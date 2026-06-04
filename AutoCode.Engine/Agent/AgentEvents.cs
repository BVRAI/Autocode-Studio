namespace AutoCode.Engine.Agent;

public abstract record AgentEvent(DateTimeOffset At);

public sealed record ChatEvent(DateTimeOffset At, string Role, string Text) : AgentEvent(At);

public sealed record StatusEvent(DateTimeOffset At, string Text) : AgentEvent(At);

public sealed record ToolCallEvent(DateTimeOffset At, string ToolName, string ArgumentsJson) : AgentEvent(At);

public sealed record ToolResultEvent(DateTimeOffset At, string ToolName, string Summary, string Content, bool IsError, long DurationMs) : AgentEvent(At);

public sealed record VerificationEvent(DateTimeOffset At, string Command, bool? Passed, string Output) : AgentEvent(At);

public enum ApprovalDecisionKind
{
    Accept,
    Decline,
    Revise
}

public sealed record ApprovalDecision(ApprovalDecisionKind Decision, string? Guidance = null)
{
    public static ApprovalDecision Accept() => new(ApprovalDecisionKind.Accept);

    public static ApprovalDecision Decline() => new(ApprovalDecisionKind.Decline);

    public static ApprovalDecision Revise(string? guidance) => new(ApprovalDecisionKind.Revise, guidance);
}

public sealed record ToolApprovalRequest(string ToolName, Dictionary<string, object?> Input, string Preview);

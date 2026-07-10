namespace AutoCode.Desktop.Orchestration;

/// <summary>Result of routing a task to a member session: whether the member's turn completed
/// normally, and its outcome text (the member's final message, or the reason it didn't run).</summary>
public sealed record DispatchOutcome(bool Ok, string Text);

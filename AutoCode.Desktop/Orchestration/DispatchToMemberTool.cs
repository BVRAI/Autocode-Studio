using AutoCode.Engine.Tools;

namespace AutoCode.Desktop.Orchestration;

/// <summary>
/// The manager agent's dispatch channel: routes a task to a member project's own agent session and
/// waits for that session's turn to finish (sync await; the orchestrating loop runs tool calls
/// sequentially). Declared Mutating so the agent loop's mode gate applies — blocked in Planning,
/// user-approved in Default, auto in Full access. The tool holds no app state: the shell injects
/// the dispatch delegate at wire time (registered only for builtin-driven ecosystem chats), which
/// handles member resolution, reject-if-busy, and cancellation linking.
/// </summary>
public sealed class DispatchToMemberTool(Func<string, string, CancellationToken, Task<DispatchOutcome>> dispatchAsync) : ITool
{
    public ToolDefinition Definition { get; } = new(
        "dispatch_to_member",
        "Send a task to one member project's agent and wait for it to finish. The member works in its own project with no visibility into this conversation, so write the task as a complete, self-contained instruction (goal, relevant paths/contracts, and what done looks like). Returns the member's final report; dispatch one task at a time and react to the outcome before the next. If the member is busy the call fails — adjust your plan or retry later.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "member": { "type": "string", "description": "The member's handle: its project folder name as listed by list_members (e.g. \"web\")." },
            "task": { "type": "string", "description": "Complete, self-contained instruction for the member. Include everything it needs; it cannot see this chat." }
          },
          "required": ["member", "task"]
        }
        """),
        Mutating: true);

    public async Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var member = ToolArgs.RequiredString(args, "member").Trim();
        var task = ToolArgs.RequiredString(args, "task").Trim();
        if (member.Length == 0 || task.Length == 0)
        {
            return new ToolResult("bad dispatch", "Both member and task must be non-empty.", true);
        }

        var outcome = await dispatchAsync(member, task, cancellationToken).ConfigureAwait(false);
        return new ToolResult(
            outcome.Ok ? $"{member} finished" : $"{member} dispatch failed",
            outcome.Text,
            !outcome.Ok);
    }
}

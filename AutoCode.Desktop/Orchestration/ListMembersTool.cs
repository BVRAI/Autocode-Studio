using AutoCode.Engine.Tools;

namespace AutoCode.Desktop.Orchestration;

/// <summary>
/// Read-only roster for the manager agent: the ecosystem's member handles with live busy/idle
/// status, so it can pick dispatch targets and avoid busy members. Stateless — the shell injects
/// the snapshot delegate at wire time (registered only for builtin-driven ecosystem chats).
/// </summary>
public sealed class ListMembersTool(Func<Task<string>> listAsync) : ITool
{
    public ToolDefinition Definition { get; } = new(
        "list_members",
        "List this ecosystem's member projects: each member's handle (used by dispatch_to_member) and whether its agent is currently busy or idle. Call before dispatching when unsure of handles or availability.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {}
        }
        """));

    public async Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var roster = await listAsync().ConfigureAwait(false);
        return new ToolResult("members listed", roster);
    }
}

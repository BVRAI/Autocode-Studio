using AutoCode.Engine.Tools;

namespace AutoCode.Desktop.Tools;

/// <summary>
/// Pure reporting channel for member agents inside an ecosystem: the tool itself only echoes the
/// message back (no side effects, no UI references) — the shell's ecosystem feed tee observes the
/// ToolCallEvent and does the real work (feed row + durable reports.md append). Ecosystems are a
/// Desktop concept, so this ITool lives in Desktop and is registered into the engine's ToolRegistry
/// by WireLoop only for builtin-backend sessions whose project belongs to an ecosystem.
/// </summary>
public sealed class ReportToEcosystemTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        "report_to_ecosystem",
        "Report a milestone to this project's ecosystem: something completed, a cross-cutting bug fixed, or a change to a shared interface/data shape. One short line; the ecosystem's shared log records it. Do not use for routine progress narration.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "message": { "type": "string", "description": "One-line milestone report, e.g. \"login flow complete; /auth/session endpoint added to the contract\"." }
          },
          "required": ["message"]
        }
        """));

    public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var message = ToolArgs.RequiredString(args, "message").Trim();
        return Task.FromResult(message.Length == 0
            ? new ToolResult("empty report", "message must be a non-empty one-line report.", true)
            : new ToolResult("reported", $"Reported to the ecosystem: {message}"));
    }
}

using System.Text.Json.Nodes;

namespace AutoCode.Engine.Llm;

public sealed class AgentMessage
{
    public required string Role { get; init; }

    public string? Text { get; init; }

    public List<ContentBlock>? Blocks { get; init; }

    public static AgentMessage User(string text) => new() { Role = "user", Text = text };

    public static AgentMessage User(IEnumerable<ContentBlock> blocks) => new() { Role = "user", Blocks = blocks.ToList() };

    public static AgentMessage Assistant(IEnumerable<ContentBlock> blocks) => new() { Role = "assistant", Blocks = blocks.ToList() };
}

public abstract record ContentBlock(string Type);

public sealed record TextBlock(string Text) : ContentBlock("text");

public sealed record ToolUseBlock(string Id, string Name, Dictionary<string, object?> Input) : ContentBlock("tool_use");

public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError = false) : ContentBlock("tool_result");

public sealed record ImageBlock(string MediaType, string Data) : ContentBlock("image");

public sealed record ThinkingBlock(
    string Text,
    string? Signature = null,
    string? RedactedData = null,
    JsonNode? Opaque = null) : ContentBlock("thinking");

public sealed record ToolSchema(string Name, string Description, JsonNode InputSchema);

public sealed record ThinkingConfig(int BudgetTokens);

public sealed record ContextEditingConfig(int TriggerInputTokens);

public sealed record CompletionRequest(
    string Model,
    string System,
    string? SystemVolatile,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<ToolSchema> Tools,
    int MaxTokens = 8192,
    double Temperature = 1.0,
    // Request extended thinking / reasoning. Providers map it natively: Anthropic
    // thinking:{type:enabled,budget_tokens} (forces temperature 1); OpenAI o-series & gpt-5 family
    // reasoning_effort. Null → provider default (off).
    ThinkingConfig? Thinking = null,
    // Ask the provider to clear stale tool results SERVER-SIDE once the prompt crosses the trigger
    // (Anthropic context-management beta; applied after cache lookup, so unlike client-side masking it
    // does NOT bust the prompt cache). Providers without support ignore it — client tiers remain the fallback.
    ContextEditingConfig? ContextEditing = null);

public sealed record CompletionUsage(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0);

public sealed record CompletionResponse(
    string Model,
    string StopReason,
    IReadOnlyList<ContentBlock> Content,
    CompletionUsage Usage);

public interface ILlmProvider
{
    string Name { get; }

    Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken);
}

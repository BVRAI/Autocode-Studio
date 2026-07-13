namespace AutoCode.Engine.Llm;

/// <summary>
/// A user-selectable model: its provider-specific id, a short display label, and (computed,
/// never duplicated) its context window and price. The id MUST be recognizable to
/// <see cref="Pricing"/> and <see cref="ContextWindow"/> so the picker, cost estimate, and
/// usage meter can never disagree.
/// </summary>
public sealed record ModelInfo(string Provider, string Id, string Label, bool SupportsThinking = false, int? ThinkingBudgetDefault = null)
{
    public int ContextWindow => Llm.ContextWindow.ContextWindowFor(Provider, Id);

    public ModelRate? Rate => Pricing.RateFor(Provider, Id);
}

/// <summary>
/// Single source of truth for the provider -> selectable-models list shown in the UI. The
/// Desktop model picker reads from here; do not hardcode a model catalog in the shell.
/// Keep ids in sync with <see cref="Pricing"/> rows.
/// </summary>
public static class ModelCatalog
{
    public static readonly IReadOnlyList<string> Providers = ["anthropic", "openai", "xai", "openrouter"];

    private static readonly Dictionary<string, ModelInfo[]> Models = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] =
        [
            // The Claude 4 family accepts the extended-thinking param.
            new("anthropic", "claude-opus-4-7", "Opus 4.7", SupportsThinking: true),
            new("anthropic", "claude-sonnet-4-6", "Sonnet 4.6", SupportsThinking: true),
            new("anthropic", "claude-haiku-4-5", "Haiku 4.5", SupportsThinking: true),
        ],
        ["openai"] =
        [
            // o-series and the gpt-5 family accept reasoning_effort; gpt-4.1 does not.
            new("openai", "gpt-5.1", "GPT-5.1", SupportsThinking: true),
            new("openai", "gpt-4.1", "GPT-4.1"),
            new("openai", "o4-mini", "o4-mini", SupportsThinking: true),
        ],
        ["xai"] =
        [
            new("xai", "grok-code-fast-1", "Grok Code Fast"),
            new("xai", "grok-4-fast", "Grok 4 Fast"),
            new("xai", "grok-4", "Grok 4"),
        ],
        ["openrouter"] =
        [
            new("openrouter", "anthropic/claude-opus-4-7", "Claude Opus 4.7"),
            new("openrouter", "openai/gpt-5.1", "GPT-5.1"),
            new("openrouter", "meta-llama/llama-3.3-70b", "Llama 3.3 70B"),
        ],
    };

    public static IReadOnlyList<ModelInfo> ModelsFor(string provider)
        => Models.TryGetValue(provider, out var list) ? list : [];

    public static string? DefaultModelFor(string provider)
        => ModelsFor(provider).FirstOrDefault()?.Id;

    // Cheap same-provider models for internal summarization (compaction). Summarizing a long
    // transcript with the flagship session model is pure waste — the quality delta is negligible.
    // Falls back to the session model when the provider has no cheaper bundled option.
    private static readonly Dictionary<string, string> CheapSummarizer = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] = "claude-haiku-4-5",
        ["openai"] = "gpt-4.1",
        ["xai"] = "grok-code-fast-1",
    };

    public static string SummarizerModelFor(string provider, string sessionModel)
        => CheapSummarizer.TryGetValue(provider, out var cheap) ? cheap : sessionModel;

    // Anthropic's floor is 1024; 8K is enough for multi-step code reasoning without dominating output.
    private const int DefaultThinkingBudget = 8_192;

    // Longest-prefix match so a dated variant id (e.g. "claude-opus-4-7-20251001") still resolves its
    // base entry. Mirrors the TS findModel rule.
    public static ModelInfo? FindModel(string provider, string model)
    {
        ModelInfo? best = null;
        foreach (var m in ModelsFor(provider))
        {
            if (model.StartsWith(m.Id, StringComparison.Ordinal) && (best is null || m.Id.Length > best.Id.Length))
            {
                best = m;
            }
        }

        return best;
    }

    // Resolve whether (and how much) to arm extended thinking for a model. Null when the model has no
    // thinking param or the user disabled it (AUTOCODE_NO_THINKING=1). This is what AgentLoop passes as
    // CompletionRequest.Thinking.
    public static ThinkingConfig? ThinkingFor(string provider, string model)
    {
        if (Environment.GetEnvironmentVariable("AUTOCODE_NO_THINKING") == "1")
        {
            return null;
        }

        var m = FindModel(provider, model);
        if (m is null || !m.SupportsThinking)
        {
            return null;
        }

        return new ThinkingConfig(m.ThinkingBudgetDefault ?? DefaultThinkingBudget);
    }
}

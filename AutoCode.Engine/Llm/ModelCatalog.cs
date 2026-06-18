namespace AutoCode.Engine.Llm;

/// <summary>
/// A user-selectable model: its provider-specific id, a short display label, and (computed,
/// never duplicated) its context window and price. The id MUST be recognizable to
/// <see cref="Pricing"/> and <see cref="ContextWindow"/> so the picker, cost estimate, and
/// usage meter can never disagree.
/// </summary>
public sealed record ModelInfo(string Provider, string Id, string Label)
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
            new("anthropic", "claude-opus-4-7", "Opus 4.7"),
            new("anthropic", "claude-sonnet-4-6", "Sonnet 4.6"),
            new("anthropic", "claude-haiku-4-5", "Haiku 4.5"),
        ],
        ["openai"] =
        [
            new("openai", "gpt-5.1", "GPT-5.1"),
            new("openai", "gpt-4.1", "GPT-4.1"),
            new("openai", "o4-mini", "o4-mini"),
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
}

using System.Text.RegularExpressions;

namespace AutoCode.Engine.Llm;

/// <summary>
/// Model context-window heuristics + auto-compaction threshold. Ported from the
/// AutoCode TUI (src/util/contextWindow.ts). Exact sizes matter less than triggering
/// compaction before the real limit.
/// </summary>
public static class ContextWindow
{
    public const double AutoCompactThreshold = 0.8;
    public const double MaskThreshold = 0.6;
    private const int DefaultWindow = 128_000;

    // The explicit 1M rule comes first so a long-context variant id wins over its base family.
    private static readonly (Regex Match, int Tokens)[] Windows =
    [
        (new Regex(@"\[1m\]|[-:]1m\b", RegexOptions.IgnoreCase), 1_000_000),
        (new Regex("claude-opus-4", RegexOptions.IgnoreCase), 200_000),
        (new Regex("claude", RegexOptions.IgnoreCase), 200_000),
        (new Regex("grok", RegexOptions.IgnoreCase), 200_000),
        (new Regex("gpt-5", RegexOptions.IgnoreCase), 200_000),
        (new Regex("gpt-4", RegexOptions.IgnoreCase), 128_000),
        (new Regex("gemini", RegexOptions.IgnoreCase), 1_000_000),
    ];

    public static int ContextWindowFor(string provider, string model)
    {
        foreach (var (match, tokens) in Windows)
        {
            if (match.IsMatch(model))
            {
                return tokens;
            }
        }

        return DefaultWindow;
    }

    /// <summary>True when a turn whose input was <paramref name="inputTokens"/> has filled enough of the window to compact.</summary>
    public static bool ShouldAutoCompact(int inputTokens, string provider, string model)
        => inputTokens > 0 && inputTokens >= ContextWindowFor(provider, model) * AutoCompactThreshold;

    /// <summary>True when old tool outputs should be cleared before full summarizing compaction is needed.</summary>
    public static bool ShouldMaskObservations(int inputTokens, string provider, string model)
        => inputTokens > 0 && inputTokens >= ContextWindowFor(provider, model) * MaskThreshold;

    // Output-token cap for agent calls. Providers default to 8192 when the request doesn't say
    // otherwise, which truncates large single-file writes (a real failure mode on big edits).
    // Family heuristic, conservative for providers whose per-model output limits vary by route
    // (openrouter). Ported from contextWindow.ts.
    private static readonly (Regex Match, int Tokens)[] MaxOutput =
    [
        (new Regex("claude", RegexOptions.IgnoreCase), 32_000),
        (new Regex(@"^(openai/)?o\d", RegexOptions.IgnoreCase), 32_000), // o-series: cap includes reasoning tokens
        (new Regex("gpt-5", RegexOptions.IgnoreCase), 32_000),
        (new Regex(@"gpt-4\.1", RegexOptions.IgnoreCase), 32_000),
        (new Regex(@"gemini-2\.5|gemini-3", RegexOptions.IgnoreCase), 32_000),
        (new Regex("grok", RegexOptions.IgnoreCase), 16_384),
    ];

    private const int DefaultMaxOutput = 16_384;

    /// <summary>Output-token cap for agent calls, by model family (avoids the 8192 default truncating large writes).</summary>
    public static int DefaultMaxOutputTokens(string model)
    {
        foreach (var (match, tokens) in MaxOutput)
        {
            if (match.IsMatch(model))
            {
                return tokens;
            }
        }

        return DefaultMaxOutput;
    }
}

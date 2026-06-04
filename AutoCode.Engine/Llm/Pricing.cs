namespace AutoCode.Engine.Llm;

/// <summary>Per-million-token USD rates. Ported from the AutoCode TUI (src/util/pricing.ts).</summary>
public sealed record ModelRate(double InputPerM, double OutputPerM, double? CacheReadPerM = null, double? CacheWritePerM = null);

public static class Pricing
{
    // Provider -> model-prefix -> rate. Longest matching prefix wins so date/length
    // suffixes (e.g. "claude-opus-4-7-20251001") still resolve to the right row.
    private static readonly Dictionary<string, Dictionary<string, ModelRate>> Rates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] = new(StringComparer.Ordinal)
        {
            ["claude-opus-4-7"] = new(15, 75, 1.5, 18.75),
            ["claude-sonnet-4-6"] = new(3, 15, 0.3, 3.75),
            ["claude-haiku-4-5"] = new(1, 5, 0.1, 1.25),
            ["claude-opus-4"] = new(15, 75, 1.5, 18.75),
            ["claude-sonnet-4"] = new(3, 15),
            ["claude-haiku-4"] = new(1, 5),
        },
        ["xai"] = new(StringComparer.Ordinal)
        {
            ["grok-code-fast-1"] = new(0.2, 1.5),
            ["grok-4-fast"] = new(0.5, 2.0),
            ["grok-4"] = new(3.0, 15.0),
        },
        ["openai"] = new(StringComparer.Ordinal)
        {
            ["gpt-5.1"] = new(5, 20),
            ["gpt-5"] = new(5, 20),
            ["gpt-4.1"] = new(2.5, 10),
            ["o3"] = new(15, 60),
            ["o4-mini"] = new(1.1, 4.4),
        },
        ["openrouter"] = new(StringComparer.Ordinal)
        {
            ["anthropic/claude-opus-4-7"] = new(15, 75),
            ["openai/gpt-5.1"] = new(5, 20),
            ["meta-llama/llama-3.3-70b"] = new(0.4, 0.6),
        },
    };

    public static ModelRate? RateFor(string provider, string model)
    {
        if (!Rates.TryGetValue(provider.Trim(), out var providerRates))
        {
            return null;
        }

        ModelRate? best = null;
        var bestLen = -1;
        foreach (var (key, rate) in providerRates)
        {
            if (model.StartsWith(key, StringComparison.Ordinal) && key.Length > bestLen)
            {
                best = rate;
                bestLen = key.Length;
            }
        }

        return best;
    }

    /// <summary>Estimate USD cost for a usage snapshot. Returns 0 when the model isn't priced.</summary>
    public static double EstimateCost(CompletionUsage usage, string provider, string model)
    {
        var rate = RateFor(provider, model);
        if (rate is null)
        {
            return 0;
        }

        // Anthropic input_tokens already excludes the cached-read portion, so sum separately.
        var fresh = Math.Max(0, usage.InputTokens);
        var total = fresh / 1_000_000.0 * rate.InputPerM
            + usage.OutputTokens / 1_000_000.0 * rate.OutputPerM;
        if (usage.CacheReadTokens > 0 && rate.CacheReadPerM is { } cr)
        {
            total += usage.CacheReadTokens / 1_000_000.0 * cr;
        }

        if (usage.CacheWriteTokens > 0 && rate.CacheWritePerM is { } cw)
        {
            total += usage.CacheWriteTokens / 1_000_000.0 * cw;
        }

        return total;
    }
}

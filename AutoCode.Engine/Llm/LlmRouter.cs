using AutoCode.Engine.Auth;

namespace AutoCode.Engine.Llm;

public sealed class LlmRouter
{
    private const int MaxRetries = 3;
    private readonly AuthResolver _authResolver;

    public LlmRouter(AuthResolver authResolver)
    {
        _authResolver = authResolver;
    }

    public async Task<CompletionResponse> CompleteAsync(
        string provider,
        CompletionRequest request,
        CancellationToken cancellationToken)
    {
        var p = ProviderFor(provider);
        Exception? last = null;
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                return await p.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRetryable(ex) && attempt < MaxRetries - 1)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(800 * Math.Pow(2, attempt)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw last ?? new InvalidOperationException("LLM request failed.");
    }

    private ILlmProvider ProviderFor(string provider)
    {
        // Resolve auth per request (providers use a shared static HttpClient, so this is cheap)
        // so a refreshed proxy/login token is always picked up mid-session.
        provider = provider.Trim().ToLowerInvariant();
        var auth = _authResolver.Resolve(provider);
        return provider switch
        {
            "anthropic" => new AnthropicProvider(auth),
            "openai" => new OpenAiCompatProvider("openai", "https://api.openai.com/v1", auth),
            "xai" => new OpenAiCompatProvider("xai", "https://api.x.ai/v1", auth),
            "openrouter" => new OpenAiCompatProvider("openrouter", "https://openrouter.ai/api/v1", auth, isOpenRouter: true),
            _ => throw new NotSupportedException($"Provider is not implemented: {provider}")
        };
    }

    private static bool IsRetryable(Exception ex)
    {
        var msg = ex.Message.ToLowerInvariant();
        return msg.Contains("overloaded", StringComparison.Ordinal)
            || msg.Contains("rate limit", StringComparison.Ordinal)
            || msg.Contains("timeout", StringComparison.Ordinal)
            || msg.Contains("econnreset", StringComparison.Ordinal)
            || msg.Contains(" 429:", StringComparison.Ordinal)
            || msg.Contains(" 500:", StringComparison.Ordinal)
            || msg.Contains(" 502:", StringComparison.Ordinal)
            || msg.Contains(" 503:", StringComparison.Ordinal)
            || msg.Contains(" 504:", StringComparison.Ordinal);
    }
}

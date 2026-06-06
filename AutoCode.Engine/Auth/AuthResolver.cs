namespace AutoCode.Engine.Auth;

public enum AuthKind
{
    Missing,
    Byok,
    Proxy
}

public sealed record AuthMode(AuthKind Kind, string Provider, string? ApiKey, string? ProxyToken, string? BaseUrl)
{
    public static AuthMode Missing(string provider) => new(AuthKind.Missing, provider, null, null, null);

    public static AuthMode Byok(string provider, string apiKey) => new(AuthKind.Byok, provider, apiKey, null, null);

    public static AuthMode Proxy(string provider, string token, string baseUrl) => new(AuthKind.Proxy, provider, null, token, baseUrl);
}

public sealed class AuthResolver
{
    private const string DefaultProxyRoot = "https://automax-proxy.fly.dev";

    private readonly AutocodeConfig _config;
    private readonly Func<string?>? _proxyTokenProvider;

    public AuthResolver(AutocodeConfig config, Func<string?>? proxyTokenProvider = null)
    {
        _config = config;
        _proxyTokenProvider = proxyTokenProvider;
    }

    public AuthMode Resolve(string provider)
    {
        provider = provider.Trim().ToLowerInvariant();

        // Proxy is honoured only when UseProxy is on. The token comes from (in order):
        // an env override, the dynamic provider (a live/refreshing login token), then config.
        if (_config.UseProxy)
        {
            var envProxyToken = Environment.GetEnvironmentVariable("AUTOCODE_GUI_PROXY_TOKEN")
                ?? Environment.GetEnvironmentVariable("AUTOMAX_PROXY_TOKEN");
            var dynamicToken = _proxyTokenProvider?.Invoke();
            var proxyToken = !string.IsNullOrWhiteSpace(envProxyToken) ? envProxyToken
                : !string.IsNullOrWhiteSpace(dynamicToken) ? dynamicToken
                : _config.ProxyToken;
            if (!string.IsNullOrWhiteSpace(proxyToken))
            {
                var root = Environment.GetEnvironmentVariable("AUTOCODE_GUI_PROXY_URL")
                    ?? Environment.GetEnvironmentVariable("AUTOMAX_PROXY_URL")
                    ?? _config.ProxyBaseUrl
                    ?? DefaultProxyRoot;
                return AuthMode.Proxy(provider, proxyToken, $"{root.TrimEnd('/')}/v1/{provider}");
            }
        }

        var envKey = EnvVarFor(provider);
        var fromEnv = envKey is null ? null : Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return AuthMode.Byok(provider, fromEnv);
        }

        if (_config.ApiKeys.TryGetValue(provider, out var configuredKey) && !string.IsNullOrWhiteSpace(configuredKey))
        {
            return AuthMode.Byok(provider, configuredKey);
        }

        return AuthMode.Missing(provider);
    }

    public static string? EnvVarFor(string provider) =>
        provider.Trim().ToLowerInvariant() switch
        {
            "anthropic" => "ANTHROPIC_API_KEY",
            "openai" => "OPENAI_API_KEY",
            "xai" => "XAI_API_KEY",
            "openrouter" => "OPENROUTER_API_KEY",
            "groq" => "GROQ_API_KEY",
            "google" => "GOOGLE_API_KEY",
            "brave" => "BRAVE_API_KEY",
            _ => null
        };
}

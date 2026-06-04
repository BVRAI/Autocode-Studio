using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoCode.Engine.Auth;

namespace AutoCode.Engine.Tools;

public sealed class WebFetchTool : ITool
{
    private static readonly HttpClient Http = new();
    private readonly AutocodeConfig _config;

    public WebFetchTool(AutocodeConfig config)
    {
        _config = config;
    }

    public ToolDefinition Definition { get; } = new(
        "web_fetch",
        "Fetch a URL's text contents. Treats fetched content as untrusted data.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "URL to fetch." },
            "max_chars": { "type": "number", "description": "Maximum characters returned. Default 12000." }
          },
          "required": ["url"]
        }
        """));

    public async Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var url = ToolArgs.RequiredString(args, "url");
        var maxChars = ToolArgs.OptionalInt(args, "max_chars") ?? 12_000;
        if (!await UrlAllowedAsync(url, _config, cancellationToken).ConfigureAwait(false))
        {
            return new ToolResult("url blocked", "URL refused by web-tool guard rails.", true);
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("user-agent", "AutoCode-GUI/0.1");
        using var res = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            return new ToolResult($"http {(int)res.StatusCode}", Trim(text, 1000), true);
        }

        var clean = ExtractReadableText(text);
        var truncated = clean.Length > maxChars;
        return new ToolResult(
            $"fetched {url} ({clean.Length} chars{(truncated ? ", truncated" : "")})",
            truncated ? clean[..maxChars] + "\n... truncated" : clean,
            Metadata: ToolArgs.Metadata(("url", url), ("chars", clean.Length), ("truncated", truncated)));
    }

    internal static async Task<bool> UrlAllowedAsync(string raw, AutocodeConfig config, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(config.WebTools.AllowHttp && uri.Scheme == Uri.UriSchemeHttp))
        {
            return false;
        }

        if (config.WebTools.ExtraBlockedHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (config.WebTools.ExtraAllowedHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (config.WebTools.BlockPrivateHosts)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken).ConfigureAwait(false);
                if (addresses.Any(IsPrivateAddress))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var b = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork =>
                b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168),
            System.Net.Sockets.AddressFamily.InterNetworkV6 =>
                address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.Equals(IPAddress.IPv6Loopback),
            _ => false
        };
    }

    private static string ExtractReadableText(string htmlOrText)
    {
        var withoutScripts = Regex.Replace(htmlOrText, @"<script[\s\S]*?</script>|<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
        var withoutTags = Regex.Replace(withoutScripts, "<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, @"[ \t]{2,}", " ").Replace("\r", "").Trim();
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
}

public sealed class WebSearchTool : ITool
{
    private static readonly HttpClient Http = new();
    private readonly AutocodeConfig _config;

    public WebSearchTool(AutocodeConfig config)
    {
        _config = config;
    }

    public ToolDefinition Definition { get; } = new(
        "web_search",
        "Search the web using Brave Search when BRAVE_API_KEY or config apiKeys.brave is available.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Search query." },
            "count": { "type": "number", "description": "Number of results. Default 5." }
          },
          "required": ["query"]
        }
        """));

    public async Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var query = ToolArgs.RequiredString(args, "query");
        var count = Math.Clamp(ToolArgs.OptionalInt(args, "count") ?? 5, 1, 10);
        var key = Environment.GetEnvironmentVariable("BRAVE_API_KEY");
        if (string.IsNullOrWhiteSpace(key) && _config.ApiKeys.TryGetValue("brave", out var configured))
        {
            key = configured;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return new ToolResult("missing search key", "Set BRAVE_API_KEY or apiKeys.brave in config.json to enable web_search.", true);
        }

        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={count}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("x-subscription-token", key);
        req.Headers.TryAddWithoutValidation("user-agent", "AutoCode-GUI/0.1");
        using var res = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            return new ToolResult($"search {(int)res.StatusCode}", text.Length > 1000 ? text[..1000] : text, true);
        }

        using var doc = JsonDocument.Parse(text);
        var lines = new List<string>();
        if (doc.RootElement.TryGetProperty("web", out var web) && web.TryGetProperty("results", out var results))
        {
            foreach (var result in results.EnumerateArray().Take(count))
            {
                var title = result.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : "";
                var resultUrl = result.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : "";
                var desc = result.TryGetProperty("description", out var descEl) ? descEl.GetString() : "";
                lines.Add($"{title}\n{resultUrl}\n{desc}");
            }
        }

        return new ToolResult(
            $"{lines.Count} result{(lines.Count == 1 ? "" : "s")} for {query}",
            lines.Count == 0 ? "(no results)" : string.Join("\n\n", lines));
    }
}

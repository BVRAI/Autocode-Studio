using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AutoCode.Engine.Auth;

namespace AutoCode.Desktop.Voice;

/// <summary>Shared HTTP + auth helpers for the cloud transcription providers.</summary>
internal static class VoiceHttp
{
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>
    /// Resolve auth for a provider. Returns the effective base URL (proxy base, or the BYOK default)
    /// and the resolved <see cref="AuthMode"/>, or null when no credentials are available.
    /// </summary>
    public static (string baseUrl, AuthMode mode)? Resolve(AuthResolver auth, string provider, string byokBaseUrl)
    {
        var mode = auth.Resolve(provider);
        if (mode.Kind == AuthKind.Missing)
        {
            return null;
        }

        var baseUrl = mode.Kind == AuthKind.Proxy ? mode.BaseUrl! : byokBaseUrl;
        return (baseUrl.TrimEnd('/'), mode);
    }

    /// <summary>Attach a <c>Authorization: Bearer</c> header using the proxy token (proxy) or API key (BYOK).</summary>
    public static void ApplyBearer(HttpRequestMessage req, AuthMode mode)
    {
        var token = mode.Kind == AuthKind.Proxy ? mode.ProxyToken : mode.ApiKey;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>POST a WAV to an OpenAI-compatible <c>/audio/transcriptions</c> endpoint (OpenAI, Groq). Returns the transcript.</summary>
    public static async Task<string> PostWhisperAsync(string baseUrl, AuthMode mode, string model, byte[] wav, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent(model), "model");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/audio/transcriptions") { Content = form };
        ApplyBearer(req, mode);

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Transcription failed ({(int)resp.StatusCode}). {Snippet(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
    }

    /// <summary>Trim a server error body to a short, log-safe snippet.</summary>
    public static string Snippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        body = body.Trim();
        return body.Length > 300 ? body[..300] + "…" : body;
    }
}

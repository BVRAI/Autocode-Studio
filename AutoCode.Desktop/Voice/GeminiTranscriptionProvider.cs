using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoCode.Engine.Auth;

namespace AutoCode.Desktop.Voice;

/// <summary>
/// Google Gemini multimodal transcription. Uses the "google" auth slot (proxy or BYOK Google key);
/// the picker labels it "gemini". BYOK authenticates with the <c>x-goog-api-key</c> header.
/// </summary>
public sealed class GeminiTranscriptionProvider : ITranscriptionProvider
{
    private const string ByokBaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    private const string Instruction = "Transcribe the speech in this audio verbatim. Return only the transcript text, with no commentary.";

    private readonly AuthResolver _auth;

    public GeminiTranscriptionProvider(AuthResolver auth) => _auth = auth;

    public string Prefix => "gemini";

    public bool IsAvailable => _auth.Resolve("google").Kind != AuthKind.Missing;

    public IReadOnlyList<TranscriptionOption> Options { get; } =
    [
        new TranscriptionOption("gemini:gemini-2.0-flash", "gemini", "gemini-2.0-flash", "Gemini · 2.0 Flash", false),
    ];

    public async Task<string> TranscribeAsync(byte[] wavBytes, string model, IProgress<string>? onDelta, CancellationToken ct)
    {
        var mode = _auth.Resolve("google");
        if (mode.Kind == AuthKind.Missing)
        {
            throw new InvalidOperationException("Gemini transcription is not configured (no proxy or Google key).");
        }

        var baseUrl = (mode.Kind == AuthKind.Proxy ? mode.BaseUrl! : ByokBaseUrl).TrimEnd('/');

        var body = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["parts"] = new JsonArray
                    {
                        new JsonObject { ["text"] = Instruction },
                        new JsonObject
                        {
                            ["inline_data"] = new JsonObject
                            {
                                ["mime_type"] = "audio/wav",
                                ["data"] = Convert.ToBase64String(wavBytes),
                            },
                        },
                    },
                },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/models/{model}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        if (mode.Kind == AuthKind.Proxy)
        {
            VoiceHttp.ApplyBearer(req, mode);
        }
        else
        {
            req.Headers.TryAddWithoutValidation("x-goog-api-key", mode.ApiKey);
        }

        using var resp = await VoiceHttp.Http.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Transcription failed ({(int)resp.StatusCode}). {VoiceHttp.Snippet(responseBody)}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        if (doc.RootElement.TryGetProperty("candidates", out var candidates)
            && candidates.GetArrayLength() > 0
            && candidates[0].TryGetProperty("content", out var content)
            && content.TryGetProperty("parts", out var parts)
            && parts.GetArrayLength() > 0
            && parts[0].TryGetProperty("text", out var text))
        {
            return text.GetString() ?? "";
        }

        return "";
    }
}

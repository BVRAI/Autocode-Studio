using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AutoCode.Engine.Auth;

namespace AutoCode.Desktop.Voice;

/// <summary>
/// OpenAI streaming transcription. Posts with <c>stream=true</c> and parses the SSE response,
/// emitting partial words via <c>onDelta</c> as they arrive. Same availability as the batch provider.
/// </summary>
public sealed class OpenAiStreamingProvider : ITranscriptionProvider
{
    private const string ByokBaseUrl = "https://api.openai.com/v1";

    private readonly AuthResolver _auth;

    public OpenAiStreamingProvider(AuthResolver auth) => _auth = auth;

    public string Prefix => "openai";

    public bool IsAvailable => _auth.Resolve("openai").Kind != AuthKind.Missing;

    public IReadOnlyList<TranscriptionOption> Options { get; } =
    [
        new TranscriptionOption("openai-stream:gpt-4o-mini-transcribe", "openai", "gpt-4o-mini-transcribe", "OpenAI · gpt-4o-mini (live)", true),
        new TranscriptionOption("openai-stream:gpt-4o-transcribe", "openai", "gpt-4o-transcribe", "OpenAI · gpt-4o (live)", true),
    ];

    public async Task<string> TranscribeAsync(byte[] wavBytes, string model, IProgress<string>? onDelta, CancellationToken ct)
    {
        var resolved = VoiceHttp.Resolve(_auth, "openai", ByokBaseUrl)
            ?? throw new InvalidOperationException("OpenAI transcription is not configured (no proxy or OpenAI key).");

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wavBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("true"), "stream");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{resolved.baseUrl}/audio/transcriptions") { Content = form };
        VoiceHttp.ApplyBearer(req, resolved.mode);

        using var resp = await VoiceHttp.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"Transcription failed ({(int)resp.StatusCode}). {VoiceHttp.Snippet(err)}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var accumulated = new StringBuilder();
        string? final = null;
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch { continue; }

            using (doc)
            {
                var type = doc.RootElement.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                if (type == "transcript.text.delta" && doc.RootElement.TryGetProperty("delta", out var d))
                {
                    var delta = d.GetString() ?? "";
                    if (delta.Length > 0)
                    {
                        accumulated.Append(delta);
                        onDelta?.Report(delta);
                    }
                }
                else if (type == "transcript.text.done" && doc.RootElement.TryGetProperty("text", out var tx))
                {
                    final = tx.GetString();
                }
            }
        }

        return final ?? accumulated.ToString();
    }
}

using AutoCode.Engine.Auth;

namespace AutoCode.Desktop.Voice;

/// <summary>Groq-hosted Whisper (OpenAI-compatible). BYOK-only — the proxy has no Groq route.</summary>
public sealed class GroqTranscriptionProvider : ITranscriptionProvider
{
    private const string ByokBaseUrl = "https://api.groq.com/openai/v1";

    private readonly AuthResolver _auth;

    public GroqTranscriptionProvider(AuthResolver auth) => _auth = auth;

    public string Prefix => "groq";

    // Groq is BYOK-only: require an actual key, never the proxy.
    public bool IsAvailable => _auth.Resolve("groq").Kind == AuthKind.Byok;

    public IReadOnlyList<TranscriptionOption> Options { get; } =
    [
        new TranscriptionOption("groq:whisper-large-v3-turbo", "groq", "whisper-large-v3-turbo", "Groq · whisper-large-v3-turbo", false),
        new TranscriptionOption("groq:whisper-large-v3", "groq", "whisper-large-v3", "Groq · whisper-large-v3", false),
    ];

    public Task<string> TranscribeAsync(byte[] wavBytes, string model, IProgress<string>? onDelta, CancellationToken ct)
    {
        var mode = _auth.Resolve("groq");
        if (mode.Kind != AuthKind.Byok)
        {
            throw new InvalidOperationException("Groq transcription requires a Groq API key (Settings ▸ Bring your own keys).");
        }

        return VoiceHttp.PostWhisperAsync(ByokBaseUrl, mode, model, wavBytes, ct);
    }
}

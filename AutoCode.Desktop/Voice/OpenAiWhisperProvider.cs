using AutoCode.Engine.Auth;

namespace AutoCode.Desktop.Voice;

/// <summary>OpenAI Whisper transcription (batch). Available via proxy (subscribed) or a BYOK OpenAI key.</summary>
public sealed class OpenAiWhisperProvider : ITranscriptionProvider
{
    private const string ByokBaseUrl = "https://api.openai.com/v1";

    private readonly AuthResolver _auth;

    public OpenAiWhisperProvider(AuthResolver auth) => _auth = auth;

    public string Prefix => "openai";

    public bool IsAvailable => _auth.Resolve("openai").Kind != AuthKind.Missing;

    public IReadOnlyList<TranscriptionOption> Options { get; } =
    [
        new TranscriptionOption("openai:whisper-1", "openai", "whisper-1", "OpenAI · whisper-1", false),
        new TranscriptionOption("openai:gpt-4o-transcribe", "openai", "gpt-4o-transcribe", "OpenAI · gpt-4o-transcribe", false),
        new TranscriptionOption("openai:gpt-4o-mini-transcribe", "openai", "gpt-4o-mini-transcribe", "OpenAI · gpt-4o-mini", false),
    ];

    public Task<string> TranscribeAsync(byte[] wavBytes, string model, IProgress<string>? onDelta, CancellationToken ct)
    {
        var resolved = VoiceHttp.Resolve(_auth, "openai", ByokBaseUrl)
            ?? throw new InvalidOperationException("OpenAI transcription is not configured (no proxy or OpenAI key).");
        return VoiceHttp.PostWhisperAsync(resolved.baseUrl, resolved.mode, model, wavBytes, ct);
    }
}

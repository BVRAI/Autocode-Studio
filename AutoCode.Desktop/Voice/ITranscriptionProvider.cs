namespace AutoCode.Desktop.Voice;

/// <summary>One selectable backend+model entry shown in the voice picker.</summary>
/// <param name="Id">Persisted identifier "prefix:model" (e.g. "openai:whisper-1"). Opaque and unique across providers.</param>
/// <param name="Prefix">Backend family for grouping/availability ("windows" | "openai" | "groq" | "gemini").</param>
/// <param name="Model">Backend model id passed to the provider ("dictation" for Windows).</param>
/// <param name="DisplayName">Human label for the menu row.</param>
/// <param name="Streaming">True if this option streams partial transcripts via <c>onDelta</c>.</param>
public sealed record TranscriptionOption(
    string Id,
    string Prefix,
    string Model,
    string DisplayName,
    bool Streaming);

/// <summary>A voice-to-text backend. One provider may expose several <see cref="TranscriptionOption"/>s.</summary>
public interface ITranscriptionProvider
{
    /// <summary>Backend family ("windows" | "openai" | "groq" | "gemini").</summary>
    string Prefix { get; }

    /// <summary>True if this backend can run right now (key/proxy present, or always for Windows).</summary>
    bool IsAvailable { get; }

    /// <summary>Catalog entries this provider contributes to the picker.</summary>
    IReadOnlyList<TranscriptionOption> Options { get; }

    /// <summary>
    /// Transcribe 16 kHz / 16-bit / mono WAV bytes. Streaming providers emit partial text via
    /// <paramref name="onDelta"/> (the caller appends raw deltas) and also return the final transcript.
    /// </summary>
    Task<string> TranscribeAsync(byte[] wavBytes, string model, IProgress<string>? onDelta, CancellationToken ct);
}

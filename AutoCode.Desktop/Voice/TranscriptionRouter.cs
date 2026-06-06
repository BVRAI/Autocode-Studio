using AutoCode.Engine.Auth;

namespace AutoCode.Desktop.Voice;

/// <summary>
/// Owns the transcription provider set and resolves availability, the picker catalog, selection
/// parsing ("prefix:model"), best-available defaulting, and dispatch. Cheap to construct — build a
/// fresh one whenever auth state may have changed (popup open / recording stop) so availability is current.
/// </summary>
public sealed class TranscriptionRouter
{
    private readonly List<ITranscriptionProvider> _providers;
    private readonly List<TranscriptionOption> _all;
    private readonly Dictionary<string, (ITranscriptionProvider Provider, TranscriptionOption Option)> _byId;

    // Best-available fallback order when no (valid, available) selection is saved.
    private static readonly string[] FallbackOrder =
    [
        "openai-stream:gpt-4o-mini-transcribe",
        "openai:whisper-1",
        "gemini:gemini-2.0-flash",
        "groq:whisper-large-v3-turbo",
        "windows:dictation",
    ];

    public TranscriptionRouter(AuthResolver auth)
    {
        // Display order: OpenAI (batch then live), Gemini, Groq, Windows last.
        _providers =
        [
            new OpenAiWhisperProvider(auth),
            new OpenAiStreamingProvider(auth),
            new GeminiTranscriptionProvider(auth),
            new GroqTranscriptionProvider(auth),
            new WindowsTranscriptionProvider(),
        ];

        _all = _providers.SelectMany(p => p.Options).ToList();
        _byId = new Dictionary<string, (ITranscriptionProvider, TranscriptionOption)>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in _providers)
        {
            foreach (var option in provider.Options)
            {
                _byId[option.Id] = (provider, option);
            }
        }
    }

    /// <summary>All catalog options, in display order.</summary>
    public IReadOnlyList<TranscriptionOption> AllOptions => _all;

    /// <summary>True if the option's backend can run right now.</summary>
    public bool IsAvailable(TranscriptionOption option)
        => _byId.TryGetValue(option.Id, out var entry) && entry.Provider.IsAvailable;

    /// <summary>Resolve a persisted "prefix:model" id to a catalog option, or null if unknown.</summary>
    public TranscriptionOption? Parse(string? id)
        => id is not null && _byId.TryGetValue(id, out var entry) ? entry.Option : null;

    /// <summary>
    /// Pick the option to use: the saved one if still valid and available, else the best available
    /// (Windows is the guaranteed terminal fallback). Never mutates the saved preference.
    /// </summary>
    public TranscriptionOption ResolveSelection(string? savedId)
    {
        var saved = Parse(savedId);
        if (saved is not null && IsAvailable(saved))
        {
            return saved;
        }

        foreach (var id in FallbackOrder)
        {
            var option = Parse(id);
            if (option is not null && IsAvailable(option))
            {
                return option;
            }
        }

        return Parse("windows:dictation")!; // WindowsTranscriptionProvider is always available
    }

    /// <summary>Dispatch transcription to the provider that owns the option (batch vs streaming handled by id).</summary>
    public Task<string> TranscribeAsync(TranscriptionOption option, byte[] wav, IProgress<string>? onDelta, CancellationToken ct)
    {
        if (!_byId.TryGetValue(option.Id, out var entry))
        {
            throw new InvalidOperationException($"Unknown transcription option: {option.Id}");
        }

        return entry.Provider.TranscribeAsync(wav, option.Model, onDelta, ct);
    }
}

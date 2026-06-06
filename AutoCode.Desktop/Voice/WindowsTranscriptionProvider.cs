using System.IO;
using System.Speech.Recognition;
using System.Text;

namespace AutoCode.Desktop.Voice;

/// <summary>Offline Windows dictation via System.Speech. Always available; lower accuracy; no network.</summary>
public sealed class WindowsTranscriptionProvider : ITranscriptionProvider
{
    public string Prefix => "windows";

    public bool IsAvailable => true;

    public IReadOnlyList<TranscriptionOption> Options { get; } =
    [
        new TranscriptionOption("windows:dictation", "windows", "dictation", "Windows (offline)", false),
    ];

    public Task<string> TranscribeAsync(byte[] wavBytes, string model, IProgress<string>? onDelta, CancellationToken ct)
        => Task.Run(() =>
        {
            SpeechRecognitionEngine engine;
            try
            {
                engine = new SpeechRecognitionEngine();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Windows speech recognition is unavailable on this system.", ex);
            }

            using (engine)
            {
                engine.LoadGrammar(new DictationGrammar());
                using var ms = new MemoryStream(wavBytes);
                engine.SetInputToWaveStream(ms);

                var sb = new StringBuilder();
                while (!ct.IsCancellationRequested)
                {
                    RecognitionResult? result;
                    try
                    {
                        result = engine.Recognize(TimeSpan.FromSeconds(3));
                    }
                    catch
                    {
                        break;
                    }

                    if (result is null)
                    {
                        break;
                    }

                    if (sb.Length > 0)
                    {
                        sb.Append(' ');
                    }

                    sb.Append(result.Text);
                }

                return sb.ToString();
            }
        }, ct);
}

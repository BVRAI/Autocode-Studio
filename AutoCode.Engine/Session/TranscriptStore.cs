using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoCode.Engine.Session;

public sealed record TranscriptEntry(DateTimeOffset At, string Role, string Text);

public sealed record ToolLogEntry(
    DateTimeOffset At,
    string Tool,
    string ArgumentsJson,
    string Status,
    long DurationMs,
    string Summary,
    string? Error);

public sealed class TranscriptStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _transcriptPath;
    private readonly string _toolLogPath;
    private readonly string _touchPath;

    public TranscriptStore(string sessionDirectory)
    {
        SessionDirectory = sessionDirectory;
        Directory.CreateDirectory(SessionDirectory);
        _transcriptPath = Path.Combine(SessionDirectory, "transcript.jsonl");
        _toolLogPath = Path.Combine(SessionDirectory, "tools.jsonl");
        _touchPath = Path.Combine(SessionDirectory, "last-task.txt");
    }

    public string SessionDirectory { get; }

    public void AppendTranscript(string role, string text)
    {
        AppendJsonLine(_transcriptPath, new TranscriptEntry(DateTimeOffset.Now, role, text));
    }

    public void AppendToolLog(ToolLogEntry entry)
    {
        AppendJsonLine(_toolLogPath, entry);
    }

    public void Touch(string? currentTask)
    {
        if (currentTask is null)
        {
            if (File.Exists(_touchPath))
            {
                File.Delete(_touchPath);
            }

            return;
        }

        File.WriteAllText(_touchPath, currentTask);
    }

    private static void AppendJsonLine<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine);
    }
}

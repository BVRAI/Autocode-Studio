using System.IO;
using System.Text.Json;
using AutoCode.Engine.Auth;

namespace AutoCode.Desktop.Misc;

/// <summary>
/// Desktop-only session index. The engine stores sessions flat with no project mapping,
/// so we drop a small session.json sidecar in each session dir (id, title, project, model,
/// startedAt) and group by project for the sidebar. No engine changes.
/// </summary>
public sealed record SessionSidecar(
    string Id,
    string Title,
    string ProjectRoot,
    string Model,
    DateTimeOffset StartedAt,
    string? GitBranch = null,
    string? GitWorktreePath = null,
    string? GitBaseBranch = null);

public static class SessionIndex
{
    private const string SidecarName = "session.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Write(string sessionDir, SessionSidecar sidecar)
    {
        try
        {
            Directory.CreateDirectory(sessionDir);
            File.WriteAllText(Path.Combine(sessionDir, SidecarName), JsonSerializer.Serialize(sidecar, Options));
        }
        catch
        {
            // Sidecar is best-effort; never block session creation on it.
        }
    }

    public static IReadOnlyList<SessionSidecar> LoadAll()
    {
        var list = new List<SessionSidecar>();
        string root;
        try
        {
            root = Paths.SessionsDirectory();
        }
        catch
        {
            return list;
        }

        if (!Directory.Exists(root))
        {
            return list;
        }

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var path = Path.Combine(dir, SidecarName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var sidecar = JsonSerializer.Deserialize<SessionSidecar>(File.ReadAllText(path), Options);
                if (sidecar is not null && !string.IsNullOrEmpty(sidecar.ProjectRoot))
                {
                    list.Add(sidecar);
                }
            }
            catch
            {
                // Skip unreadable sidecars.
            }
        }

        return list;
    }

    public static string SessionDir(string sessionId) => Path.Combine(Paths.SessionsDirectory(), sessionId);

    /// <summary>
    /// Reads transcript.jsonl. It is written with WriteIndented=true, so it is a stream of
    /// pretty-printed JSON objects (not line-delimited); parse with a streaming reader.
    /// </summary>
    public static IReadOnlyList<(string Role, string Text)> LoadTranscript(string sessionDir)
    {
        var result = new List<(string, string)>();
        var path = Path.Combine(sessionDir, "transcript.jsonl");
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    continue;
                }

                using var doc = JsonDocument.ParseValue(ref reader);
                var rootEl = doc.RootElement;
                var role = rootEl.TryGetProperty("role", out var r) ? r.GetString() : null;
                var text = rootEl.TryGetProperty("text", out var t) ? t.GetString() : null;
                if (!string.IsNullOrEmpty(role) && text is not null)
                {
                    result.Add((role!, text));
                }
            }
        }
        catch
        {
            // Corrupt transcript -> show what we parsed.
        }

        return result;
    }
}

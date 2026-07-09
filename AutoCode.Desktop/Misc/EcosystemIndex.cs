using System.IO;
using System.Text.Json;
using AutoCode.Engine.Auth;

namespace AutoCode.Desktop.Misc;

/// <summary>
/// One ecosystem: a named group of project roots plus the on-disk manifest git repo that agents
/// read (manifest/checklist/contract/design tokens). A project root belongs to at most one
/// ecosystem. Roots are stored normalized (see <see cref="EcosystemIndex.NormalizeRoot"/>).
/// </summary>
public sealed record EcosystemRecord(
    string Id,
    string Name,
    string ManifestRoot,
    List<string> MemberRoots,
    DateTimeOffset CreatedAt);

/// <summary>
/// Desktop-only ecosystems registry: a single ecosystems.json under the app data directory
/// (beside sessions/), mirroring the SessionIndex sidecar idiom. The registry is the UI's source
/// of truth for grouping; the manifest repo on disk is derived from it (agent-facing, best-effort).
/// </summary>
public static class EcosystemIndex
{
    private const string FileName = "ecosystems.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string FilePath() => Path.Combine(Paths.DataDirectory(), FileName);

    public static List<EcosystemRecord> LoadAll()
    {
        try
        {
            var path = FilePath();
            if (!File.Exists(path))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<EcosystemRecord>>(File.ReadAllText(path), Options) ?? [];
        }
        catch
        {
            return []; // Corrupt/unreadable registry -> start empty; SaveAll rewrites it.
        }
    }

    public static void SaveAll(IReadOnlyList<EcosystemRecord> ecosystems)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDirectory());
            File.WriteAllText(FilePath(), JsonSerializer.Serialize(ecosystems, Options));
        }
        catch
        {
            // Best-effort; never block UI actions on registry IO.
        }
    }

    /// <summary>The single normalization authority for project roots: full path, no trailing
    /// separator. Every root stored in MemberRoots or compared against them goes through this.</summary>
    public static string NormalizeRoot(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies inside it
    /// (separator-boundary comparison — "C:\ab" is not inside "C:\a").</summary>
    public static bool IsInside(string path, string root)
    {
        var p = NormalizeRoot(path);
        var r = NormalizeRoot(root);
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

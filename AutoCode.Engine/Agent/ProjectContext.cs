using System.Text;
using System.Text.RegularExpressions;
using AutoCode.Engine.Tools;

namespace AutoCode.Engine.Agent;

public sealed record ProjectContextInfo(IReadOnlyList<string> Types, bool IsGitRepository);

public static class ProjectContext
{
    private const int MaxDigestBytes = 6_000;
    private const int MaxFiles = 400;
    private const int MaxSymbolsPerFile = 10;
    private const int MaxReadBytes = 64_000;
    private const double PhaseOneBudgetFraction = 0.75;
    private static readonly Dictionary<string, RepoMapData> RepoMapCache = new(StringComparer.OrdinalIgnoreCase);
    // Roots whose files changed since their map was built. The rebuild is deferred to a turn
    // boundary (see RefreshRepoMapIfStale) — the digest is part of the cached system-prompt prefix,
    // so rebuilding between iterations would bust the provider prompt cache within a single turn.
    private static readonly HashSet<string> DirtyRoots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
        ".cs", ".py", ".rs", ".go", ".java", ".kt", ".cpp", ".cc", ".cxx", ".h", ".hpp"
    };

    public static ProjectContextInfo Detect(string root)
    {
        var types = new List<string>();
        if (File.Exists(Path.Combine(root, "package.json"))) types.Add("node");
        if (File.Exists(Path.Combine(root, "tsconfig.json"))) types.Add("typescript");
        if (File.Exists(Path.Combine(root, "pyproject.toml")) || File.Exists(Path.Combine(root, "requirements.txt"))) types.Add("python");
        if (File.Exists(Path.Combine(root, "Cargo.toml"))) types.Add("rust");
        if (File.Exists(Path.Combine(root, "go.mod"))) types.Add("go");
        if (Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).Any() || Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).Any()) types.Add("dotnet");
        if (File.Exists(Path.Combine(root, "pom.xml")) || File.Exists(Path.Combine(root, "build.gradle")) || File.Exists(Path.Combine(root, "build.gradle.kts"))) types.Add("jvm");
        return new ProjectContextInfo(types, Directory.Exists(Path.Combine(root, ".git")));
    }

    public static string RepoMap(string root)
        => GetRepoMapInfo(root).Digest;

    public static ImportGraph GetImportGraph(string root)
        => GetRepoMapInfo(root).Graph;

    /// <summary>Mark a root's map stale (a file was created/edited/deleted). Cheap — the rebuild happens at the next turn boundary.</summary>
    public static void InvalidateRepoMap(string root)
    {
        var full = Path.GetFullPath(root);
        if (RepoMapCache.ContainsKey(full))
        {
            DirtyRoots.Add(full);
        }
    }

    /// <summary>
    /// Rebuild a stale map. Called at TURN boundaries (AgentLoop.SubmitAsync), not per-edit: the
    /// digest is part of the cached system-prompt prefix, so rebuilding between iterations would bust
    /// the provider prompt cache repeatedly within a single turn. Returns true when a rebuild happened.
    /// </summary>
    public static bool RefreshRepoMapIfStale(string root)
    {
        var full = Path.GetFullPath(root);
        if (!DirtyRoots.Contains(full))
        {
            return false;
        }

        DirtyRoots.Remove(full);
        RepoMapCache[full] = BuildRepoMapInfo(full);
        return true;
    }

    /// <summary>Unconditional rebuild — so a freshly-created file appears immediately (e.g. file_deps retry).</summary>
    public static void ForceRefreshRepoMap(string root)
    {
        var full = Path.GetFullPath(root);
        DirtyRoots.Remove(full);
        RepoMapCache[full] = BuildRepoMapInfo(full);
    }

    private static RepoMapData GetRepoMapInfo(string root)
    {
        var full = Path.GetFullPath(root);
        if (RepoMapCache.TryGetValue(full, out var cached))
        {
            return cached;
        }

        var info = BuildRepoMapInfo(full);
        RepoMapCache[full] = info;
        return info;
    }

    private static RepoMapData BuildRepoMapInfo(string root)
    {
        var files = new List<string>();
        void Walk(string dir, int depth)
        {
            if (files.Count >= MaxFiles || depth > 12)
            {
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (ToolConstants.NoiseDirectories.Contains(name))
                    {
                        continue;
                    }

                    Walk(entry, depth + 1);
                }
                else
                {
                    if (!SourceExtensions.Contains(Path.GetExtension(entry)))
                    {
                        continue;
                    }

                    files.Add(entry);
                    if (files.Count >= MaxFiles)
                    {
                        return;
                    }
                }
            }
        }

        try
        {
            if (Directory.Exists(root))
            {
                Walk(root, 0);
            }
        }
        catch
        {
            return RepoMapData.Empty;
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        var rels = new List<string>();
        var textByRel = new Dictionary<string, string>(StringComparer.Ordinal);
        var symbolsByRel = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var abs in files)
        {
            var rel = Path.GetRelativePath(root, abs).Replace('\\', '/');
            rels.Add(rel);
            var text = "";
            try
            {
                text = File.ReadAllText(abs);
            }
            catch
            {
                // Unreadable files still appear in the digest.
            }

            if (text.Length > MaxReadBytes)
            {
                text = text[..MaxReadBytes];
            }

            textByRel[rel] = text;
            symbolsByRel[rel] = ExtractSymbolsFromText(text, Path.GetExtension(abs));
        }

        var graph = ImportGraphBuilder.Build(rels, rel => textByRel.GetValueOrDefault(rel));
        int InDegree(string rel) => graph.Importers.TryGetValue(rel, out var list) ? list.Count : 0;
        double Score(string rel) => graph.Rank.GetValueOrDefault(rel) * (1 + InDegree(rel));
        var ordered = rels
            .OrderByDescending(Score)
            .ThenByDescending(InDegree)
            .ThenBy(r => r, StringComparer.Ordinal)
            .ToList();

        var lines = new List<string>();
        var bytes = 0;
        var phaseOneBudget = (int)(MaxDigestBytes * PhaseOneBudgetFraction);
        var index = 0;
        for (; index < ordered.Count; index++)
        {
            var rel = ordered[index];
            var symbols = symbolsByRel.GetValueOrDefault(rel) ?? [];
            var indegree = InDegree(rel);
            var line = (symbols.Count > 0 ? $"{rel}  -  {string.Join(", ", symbols)}" : rel)
                + (indegree >= 2 ? $"  (imported by {indegree})" : "");
            if (bytes + line.Length + 1 > phaseOneBudget)
            {
                break;
            }

            lines.Add(line);
            bytes += line.Length + 1;
        }

        var truncated = false;
        if (index < ordered.Count)
        {
            var rest = ordered.Skip(index).OrderBy(r => r, StringComparer.Ordinal).ToList();
            var header = "-- other files --";
            lines.Add(header);
            bytes += header.Length + 1;
            var current = "";
            foreach (var rel in rest)
            {
                var candidate = current.Length == 0 ? rel : current + ", " + rel;
                if (candidate.Length > 100 && current.Length > 0)
                {
                    if (bytes + current.Length + 1 > MaxDigestBytes)
                    {
                        truncated = true;
                        current = "";
                        break;
                    }

                    lines.Add(current);
                    bytes += current.Length + 1;
                    current = rel;
                }
                else
                {
                    current = candidate;
                }
            }

            if (current.Length > 0)
            {
                if (bytes + current.Length + 1 > MaxDigestBytes)
                {
                    truncated = true;
                }
                else
                {
                    lines.Add(current);
                }
            }

            if (lines.Count > 0 && lines[^1] == header)
            {
                lines.RemoveAt(lines.Count - 1);
                truncated = true;
            }
        }

        var digest = lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines) + (truncated ? Environment.NewLine + "... (repo map truncated)" : "");
        return new RepoMapData(digest, graph);
    }

    private static IReadOnlyList<string> ExtractSymbolsFromText(string text, string ext)
    {
        var pattern = ext.ToLowerInvariant() switch
        {
            ".cs" => @"^\s*(?:public|private|internal|protected|static|sealed|abstract|partial|readonly|\s)*\s*(?:class|interface|record|struct|enum)\s+([A-Za-z_]\w*)",
            ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" => @"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?(?:function|class|interface|type|const|let|var)\s+([A-Za-z_$][\w$]*)",
            ".py" => @"^(?:def|class)\s+([A-Za-z_]\w*)",
            ".rs" => @"^\s*(?:pub\s+)?(?:fn|struct|enum|trait|impl)\s+([A-Za-z_]\w*)",
            ".go" => @"^\s*(?:func|type)\s+(?:\([^)]+\)\s*)?([A-Za-z_]\w*)",
            ".java" or ".kt" => @"^\s*(?:public|private|protected|internal|abstract|final|open|\s)*\s*(?:class|interface|enum|object|fun)\s+([A-Za-z_]\w*)",
            ".cpp" or ".cc" or ".cxx" or ".h" or ".hpp" => @"^\s*(?:class|struct|enum)\s+([A-Za-z_]\w*)",
            _ => ""
        };
        if (string.IsNullOrEmpty(pattern))
        {
            return [];
        }

        var names = new List<string>();
        foreach (Match match in Regex.Matches(text, pattern, RegexOptions.Multiline))
        {
            var name = match.Groups[1].Value;
            if (!names.Contains(name, StringComparer.Ordinal) && names.Count < MaxSymbolsPerFile)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private sealed record RepoMapData(string Digest, ImportGraph Graph)
    {
        public static readonly RepoMapData Empty = new("", new ImportGraph([], new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, double>()));
    }
}

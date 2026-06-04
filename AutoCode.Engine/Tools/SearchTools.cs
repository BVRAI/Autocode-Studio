using System.Text;
using System.Text.RegularExpressions;
using AutoCode.Engine.Safety;

namespace AutoCode.Engine.Tools;

public sealed class GlobTool : ITool
{
    private const int DefaultLimit = 200;

    public ToolDefinition Definition { get; } = new(
        "glob",
        "Find files by name pattern under the project root. Supports *, ?, **, and comma-separated patterns.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Glob pattern, e.g. src/**/*.cs." },
            "cwd": { "type": "string", "description": "Subdirectory to search under. Default project root." },
            "limit": { "type": "number", "description": "Max results. Default 200." }
          },
          "required": ["pattern"]
        }
        """));

    public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var pattern = ToolArgs.RequiredString(args, "pattern");
        var cwd = ToolArgs.OptionalString(args, "cwd");
        var limit = ToolArgs.OptionalInt(args, "limit") ?? DefaultLimit;
        var root = cwd is null
            ? context.Session.ProjectRoot
            : PathSafety.ResolveInsideRoot(context.Session.ProjectRoot, cwd);
        var patterns = pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(GlobToRegex)
            .ToList();
        var matches = EnumerateFiles(root, cancellationToken)
            .Select(f => PathSafety.ToRelative(root, f))
            .Where(rel => patterns.Any(re => re.IsMatch(rel.Replace('\\', '/'))))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var truncated = matches.Count > limit;
        var shown = matches.Take(limit).Select(p => cwd is null ? p : PathSafety.ToRelative(context.Session.ProjectRoot, Path.Combine(root, p))).ToList();
        var content = shown.Count == 0
            ? "(no matches)"
            : string.Join(Environment.NewLine, shown) + (truncated ? Environment.NewLine + $"... {matches.Count - limit} more" : "");
        return Task.FromResult(new ToolResult(
            $"{matches.Count} match{(matches.Count == 1 ? "" : "es")} for {pattern}{(truncated ? " (truncated)" : "")}",
            content,
            Metadata: ToolArgs.Metadata(("total", matches.Count), ("truncated", truncated))));
    }

    internal static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    if (i + 2 < glob.Length && glob[i + 2] == '/')
                    {
                        sb.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        sb.Append(".*");
                        i++;
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    internal static IEnumerable<string> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        IEnumerable<string> Walk(string dir)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (ToolConstants.NoiseDirectories.Contains(name))
                    {
                        continue;
                    }

                    foreach (var child in Walk(entry))
                    {
                        yield return child;
                    }
                }
                else
                {
                    yield return entry;
                }
            }
        }

        return Walk(root);
    }
}

public sealed class GrepTool : ITool
{
    private const int DefaultMaxMatches = 200;
    private const int MaxFileBytes = 1_000_000;

    public ToolDefinition Definition { get; } = new(
        "grep",
        "Search file contents for a regex pattern. Returns path:line matches and skips common binary/noise files.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Regex pattern." },
            "glob": { "type": "string", "description": "Optional glob filter." },
            "case_insensitive": { "type": "boolean", "description": "Case-insensitive search. Default false." },
            "max_matches": { "type": "number", "description": "Max matches returned. Default 200." },
            "path": { "type": "string", "description": "Subdirectory to search under. Default root." }
          },
          "required": ["pattern"]
        }
        """));

    public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var pattern = ToolArgs.RequiredString(args, "pattern");
        var filterGlob = ToolArgs.OptionalString(args, "glob");
        var caseInsensitive = ToolArgs.OptionalBool(args, "case_insensitive") ?? false;
        var maxMatches = ToolArgs.OptionalInt(args, "max_matches") ?? DefaultMaxMatches;
        var subpath = ToolArgs.OptionalString(args, "path");
        var root = subpath is null ? context.Session.ProjectRoot : PathSafety.ResolveInsideRoot(context.Session.ProjectRoot, subpath);
        Regex regex;
        try
        {
            regex = new Regex(pattern, caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult("invalid regex", $"Could not compile /{pattern}/: {ex.Message}", true));
        }

        var globRegex = filterGlob is null ? null : GlobTool.GlobToRegex(filterGlob);
        var matches = new List<string>();
        var total = 0;
        var filesScanned = 0;
        foreach (var file in GlobTool.EnumerateFiles(root, cancellationToken))
        {
            if (globRegex is not null && !globRegex.IsMatch(PathSafety.ToRelative(root, file).Replace('\\', '/')))
            {
                continue;
            }

            if (IsLikelyBinaryName(file))
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch
            {
                continue;
            }

            if (bytes.Length > MaxFileBytes || bytes.Contains((byte)0))
            {
                continue;
            }

            filesScanned++;
            var text = Encoding.UTF8.GetString(bytes);
            var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
            for (var i = 0; i < lines.Length; i++)
            {
                if (regex.IsMatch(lines[i]))
                {
                    total++;
                    if (matches.Count < maxMatches)
                    {
                        var rel = PathSafety.ToRelative(context.Session.ProjectRoot, file);
                        matches.Add($"{rel}:{i + 1}: {Trim(lines[i], 400)}");
                    }
                }
            }
        }

        var truncated = total > matches.Count;
        var content = matches.Count == 0
            ? "(no matches)"
            : string.Join(Environment.NewLine, matches) + (truncated ? Environment.NewLine + $"... {total - matches.Count} more matches" : "");
        return Task.FromResult(new ToolResult(
            $"{total} match{(total == 1 ? "" : "es")} for /{pattern}/{(caseInsensitive ? "i" : "")}{(truncated ? " (truncated)" : "")}",
            content,
            Metadata: ToolArgs.Metadata(("total", total), ("truncated", truncated), ("filesScanned", filesScanned))));
    }

    private static bool IsLikelyBinaryName(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" or ".webp"
            or ".pdf" or ".zip" or ".gz" or ".tar" or ".exe" or ".dll" or ".so" or ".dylib"
            or ".bin" or ".wasm" or ".mp3" or ".mp4" or ".mov" or ".avi";
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
}

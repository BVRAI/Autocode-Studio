using AutoCode.Engine.Tools;

namespace AutoCode.Engine.Agent;

public sealed record ProjectInstruction(string FileName, string RelativeDirectory, string Content, string? VerifyCommand, bool IsAuthoritative);

public static class ProjectInstructions
{
    private static readonly string[] CandidateNames = ["AGENTS.md", "AUTOCODE.md", "master.md"];

    public static IReadOnlyList<ProjectInstruction> Load(string root)
    {
        var found = new List<(string Path, string RelativeDirectory, int Depth, int Index)>();
        Walk(root, root, 0, found);
        var total = 0;
        var output = new List<ProjectInstruction>();
        foreach (var item in found.OrderBy(f => f.Depth).ThenBy(f => f.RelativeDirectory, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Index))
        {
            if (total >= 40_000)
            {
                break;
            }

            string raw;
            try
            {
                raw = File.ReadAllText(item.Path);
            }
            catch
            {
                continue;
            }

            var (meta, body) = ParseFrontMatter(raw);
            var remaining = 40_000 - total;
            var content = body.Length > remaining ? body[..remaining] + "\n[...truncated]" : body;
            total += content.Length;
            var fileName = Path.GetFileName(item.Path);
            output.Add(new ProjectInstruction(
                fileName,
                item.RelativeDirectory,
                content,
                meta.TryGetValue("verify", out var verify) ? verify : null,
                fileName.Equals("master.md", StringComparison.OrdinalIgnoreCase)));
        }

        return output;
    }

    private static void Walk(string root, string dir, int depth, List<(string Path, string RelativeDirectory, int Depth, int Index)> found)
    {
        if (depth > 8)
        {
            return;
        }

        for (var i = 0; i < CandidateNames.Length; i++)
        {
            var path = Path.Combine(dir, CandidateNames[i]);
            if (File.Exists(path))
            {
                found.Add((path, Path.GetRelativePath(root, dir).Replace('\\', '/').Replace(".", ""), depth, i));
            }
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (ToolConstants.NoiseDirectories.Contains(Path.GetFileName(sub)))
            {
                continue;
            }

            Walk(root, sub, depth + 1, found);
        }
    }

    private static (Dictionary<string, string> Meta, string Body) ParseFrontMatter(string raw)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!raw.StartsWith("---", StringComparison.Ordinal))
        {
            return (meta, raw);
        }

        var end = raw.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            return (meta, raw);
        }

        var head = raw[3..end];
        foreach (var line in head.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var idx = line.IndexOf(':');
            if (idx > 0)
            {
                meta[line[..idx].Trim()] = line[(idx + 1)..].Trim().Trim('"');
            }
        }

        return (meta, raw[(end + 4)..].TrimStart());
    }
}

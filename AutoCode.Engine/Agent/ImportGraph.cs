using System.Text.RegularExpressions;

namespace AutoCode.Engine.Agent;

public sealed record ImportGraph(
    IReadOnlyList<string> Files,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Imports,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Importers,
    IReadOnlyDictionary<string, double> Rank);

public static class ImportGraphBuilder
{
    private static readonly HashSet<string> JsExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"
    };

    public static IReadOnlyList<string> ExtractImportSpecifiers(string text, string ext)
    {
        var output = new List<string>();
        if (JsExts.Contains(ext))
        {
            var patterns = new[]
            {
                @"(?:^|\s)(?:import|export)\s[^;'""`]*?from\s*['""]([^'""]+)['""]",
                @"(?:^|\s)import\s*['""]([^'""]+)['""]",
                @"\b(?:require|import)\s*\(\s*['""]([^'""]+)['""]\s*\)"
            };
            foreach (var pattern in patterns)
            {
                foreach (Match match in Regex.Matches(text, pattern, RegexOptions.Multiline))
                {
                    var spec = match.Groups[1].Value;
                    if (spec.StartsWith(".", StringComparison.Ordinal))
                    {
                        output.Add(spec);
                    }
                }
            }
        }
        else if (ext.Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match match in Regex.Matches(text, @"^\s*from\s+([.\w]+)\s+import\s", RegexOptions.Multiline))
            {
                output.Add(match.Groups[1].Value);
            }

            foreach (Match match in Regex.Matches(text, @"^\s*import\s+([\w.]+)", RegexOptions.Multiline))
            {
                output.Add(match.Groups[1].Value);
            }
        }
        else if (ext.Equals(".rs", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match match in Regex.Matches(text, @"^\s*(?:pub\s+)?mod\s+(\w+)\s*;", RegexOptions.Multiline))
            {
                output.Add("mod:" + match.Groups[1].Value);
            }

            foreach (Match match in Regex.Matches(text, @"^\s*use\s+crate::([\w:]+)", RegexOptions.Multiline))
            {
                output.Add("crate:" + match.Groups[1].Value.Replace("::", "/"));
            }
        }

        return output;
    }

    public static string? ResolveImport(string spec, string importerRel, ISet<string> fileSet)
    {
        var ext = ExtOf(importerRel);
        if (JsExts.Contains(ext))
        {
            return ResolveJs(spec, importerRel, fileSet);
        }

        if (ext.Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            return ResolvePython(spec, importerRel, fileSet);
        }

        if (ext.Equals(".rs", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveRust(spec, importerRel, fileSet);
        }

        return null;
    }

    public static ImportGraph Build(IReadOnlyList<string> filesRel, Func<string, string?> readText)
    {
        var fileSet = new HashSet<string>(filesRel, StringComparer.Ordinal);
        var imports = filesRel.ToDictionary(f => f, _ => (IReadOnlyList<string>)[], StringComparer.Ordinal);
        var importers = filesRel.ToDictionary(f => f, _ => (IReadOnlyList<string>)[], StringComparer.Ordinal);
        var importsMutable = filesRel.ToDictionary(f => f, _ => new List<string>(), StringComparer.Ordinal);
        var importersMutable = filesRel.ToDictionary(f => f, _ => new List<string>(), StringComparer.Ordinal);

        foreach (var file in filesRel)
        {
            var text = readText(file);
            if (text is null)
            {
                continue;
            }

            var targets = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var spec in ExtractImportSpecifiers(text, ExtOf(file)))
            {
                var target = ResolveImport(spec, file, fileSet);
                if (target is not null && target != file)
                {
                    targets.Add(target);
                }
            }

            importsMutable[file].AddRange(targets);
            foreach (var target in targets)
            {
                importersMutable[target].Add(file);
            }
        }

        foreach (var file in filesRel)
        {
            imports[file] = importsMutable[file];
            importers[file] = importersMutable[file];
        }

        return new ImportGraph(filesRel, imports, importers, PageRank(filesRel, importsMutable));
    }

    public static IReadOnlyDictionary<string, double> PageRank(
        IReadOnlyList<string> nodes,
        IReadOnlyDictionary<string, List<string>> edges,
        int iterations = 15,
        double damping = 0.85)
    {
        var rank = new Dictionary<string, double>(StringComparer.Ordinal);
        if (nodes.Count == 0)
        {
            return rank;
        }

        foreach (var node in nodes)
        {
            rank[node] = 1.0 / nodes.Count;
        }

        for (var iter = 0; iter < iterations; iter++)
        {
            var next = nodes.ToDictionary(n => n, _ => (1 - damping) / nodes.Count, StringComparer.Ordinal);
            var dangling = 0.0;
            foreach (var node in nodes)
            {
                var outgoing = edges.TryGetValue(node, out var list) ? list : [];
                var r = rank[node];
                if (outgoing.Count == 0)
                {
                    dangling += r;
                }
                else
                {
                    var share = damping * r / outgoing.Count;
                    foreach (var target in outgoing)
                    {
                        next[target] = next.GetValueOrDefault(target) + share;
                    }
                }
            }

            var danglingShare = damping * dangling / nodes.Count;
            foreach (var node in nodes)
            {
                rank[node] = next[node] + danglingShare;
            }
        }

        return rank;
    }

    private static string? ResolveJs(string spec, string importerRel, ISet<string> fileSet)
    {
        if (!spec.StartsWith(".", StringComparison.Ordinal))
        {
            return null;
        }

        var basePath = PosixJoin(PosixDirname(importerRel), spec);
        if (basePath is null)
        {
            return null;
        }

        var candidates = new List<string> { basePath };
        var jsExt = Regex.Match(basePath, @"^(.*)\.(js|jsx|mjs|cjs)$", RegexOptions.IgnoreCase);
        if (jsExt.Success)
        {
            candidates.Add(jsExt.Groups[1].Value + ".ts");
            candidates.Add(jsExt.Groups[1].Value + ".tsx");
        }

        var exts = new[] { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs" };
        candidates.AddRange(exts.Select(e => basePath + e));
        candidates.AddRange(exts.Select(e => basePath + "/index" + e));
        return candidates.FirstOrDefault(fileSet.Contains);
    }

    private static string? ResolvePython(string spec, string importerRel, ISet<string> fileSet)
    {
        var dots = spec.TakeWhile(c => c == '.').Count();
        var rest = spec[dots..].Replace('.', '/');
        var candidates = new List<string>();
        if (dots > 0)
        {
            var dir = PosixDirname(importerRel);
            for (var i = 1; i < dots; i++)
            {
                dir = PosixDirname(dir);
            }

            var prefix = dir.Length == 0 ? "" : dir + "/";
            candidates.Add(rest.Length == 0 ? prefix + "__init__.py" : prefix + rest + ".py");
            if (rest.Length > 0)
            {
                candidates.Add(prefix + rest + "/__init__.py");
            }
        }
        else
        {
            candidates.Add(rest + ".py");
            candidates.Add(rest + "/__init__.py");
            candidates.Add("src/" + rest + ".py");
            candidates.Add("src/" + rest + "/__init__.py");
        }

        return candidates.FirstOrDefault(fileSet.Contains);
    }

    private static string? ResolveRust(string spec, string importerRel, ISet<string> fileSet)
    {
        var candidates = new List<string>();
        if (spec.StartsWith("mod:", StringComparison.Ordinal))
        {
            var name = spec[4..];
            var dir = PosixDirname(importerRel);
            var prefix = dir.Length == 0 ? "" : dir + "/";
            candidates.Add(prefix + name + ".rs");
            candidates.Add(prefix + name + "/mod.rs");
        }
        else if (spec.StartsWith("crate:", StringComparison.Ordinal))
        {
            var path = spec[6..];
            while (path.Length > 0)
            {
                candidates.Add("src/" + path + ".rs");
                candidates.Add("src/" + path + "/mod.rs");
                var i = path.LastIndexOf('/');
                if (i < 0)
                {
                    break;
                }

                path = path[..i];
            }
        }

        return candidates.FirstOrDefault(fileSet.Contains);
    }

    private static string ExtOf(string rel)
    {
        var slash = rel.LastIndexOf('/');
        var dot = rel.LastIndexOf('.');
        return dot > slash ? rel[dot..] : "";
    }

    private static string PosixDirname(string rel)
    {
        var i = rel.LastIndexOf('/');
        return i < 0 ? "" : rel[..i];
    }

    private static string? PosixJoin(string dir, string spec)
    {
        var parts = (dir.Length == 0 ? spec : dir + "/" + spec).Split('/');
        var output = new List<string>();
        foreach (var part in parts)
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (output.Count == 0)
                {
                    return null;
                }

                output.RemoveAt(output.Count - 1);
            }
            else
            {
                output.Add(part);
            }
        }

        return string.Join('/', output);
    }
}

using AutoCode.Engine.Agent;
using AutoCode.Engine.Safety;

namespace AutoCode.Engine.Tools;

public sealed class FileDepsTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        "file_deps",
        "Show a file's position in the project import graph: which files import it and which files it imports. Use before changing a shared file to understand blast radius.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File path relative to project root." },
            "direction": { "type": "string", "enum": ["importers", "imports", "both"], "description": "Default both." }
          },
          "required": ["path"]
        }
        """));

    public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var rawPath = ToolArgs.RequiredString(args, "path");
        var direction = ToolArgs.OptionalString(args, "direction") ?? "both";
        if (direction is not ("importers" or "imports" or "both"))
        {
            return Task.FromResult(new ToolResult("bad direction", $"direction must be importers | imports | both. Got: {direction}", true));
        }

        var abs = PathSafety.ResolveInsideRoot(context.Session.ProjectRoot, rawPath);
        var rel = PathSafety.ToRelative(context.Session.ProjectRoot, abs).Replace('\\', '/');
        var graph = ProjectContext.GetImportGraph(context.Session.ProjectRoot);
        if (!graph.Imports.ContainsKey(rel) && File.Exists(abs))
        {
            // Exists on disk but not in the graph — most likely created after the last scan (the map
            // refreshes at turn boundaries). Rebuild once and retry so freshly-written files answer.
            ProjectContext.ForceRefreshRepoMap(context.Session.ProjectRoot);
            graph = ProjectContext.GetImportGraph(context.Session.ProjectRoot);
        }

        if (!graph.Imports.ContainsKey(rel))
        {
            return Task.FromResult(new ToolResult(
                $"not in import graph: {rel}",
                $"{rel} is not in the scanned import graph. Possible reasons: unsupported file type, inside an ignored directory (node_modules etc.), or beyond the scan cap on a very large repo. Use grep/find_symbol for files outside the graph.",
                true));
        }

        var importers = (graph.Importers.TryGetValue(rel, out var importerList) ? importerList : [])
            .OrderByDescending(f => graph.Rank.GetValueOrDefault(f))
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
        var imports = (graph.Imports.TryGetValue(rel, out var importList) ? importList : []).ToList();
        var lines = new List<string> { $"deps for {rel}" };
        if (direction != "imports")
        {
            lines.Add($"imported by ({importers.Count}):");
            lines.AddRange(importers.Select(f => "  " + f));
        }

        if (direction != "importers")
        {
            lines.Add($"imports ({imports.Count}):");
            lines.AddRange(imports.Select(f => "  " + f));
        }

        return Task.FromResult(new ToolResult(
            $"{rel}: {importers.Count} importer(s), {imports.Count} import(s)",
            string.Join(Environment.NewLine, lines),
            Metadata: ToolArgs.Metadata(("path", rel), ("importers", importers), ("imports", imports))));
    }
}

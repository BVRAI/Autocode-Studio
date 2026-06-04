using System.Text;
using AutoCode.Engine.Agent;
using AutoCode.Engine.Safety;

namespace AutoCode.Engine.Tools;

public static class ToolConstants
{
    public static readonly HashSet<string> NoiseDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", "bin", "obj", "dist", "build", ".next", ".turbo",
        ".vite", "__pycache__", ".venv", "venv", ".idea", ".vscode", ".cache", "coverage"
    };
}

public sealed class ListDirectoryTool : ITool
{
    private const int DefaultMaxEntries = 200;

    public ToolDefinition Definition { get; } = new(
        "list_directory",
        "List files and subdirectories under a path relative to the project root. Filters common noise directories.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path relative to project root. Use . for the root." },
            "recursive": { "type": "boolean", "description": "Recurse into subdirectories. Default false." },
            "max_entries": { "type": "number", "description": "Max entries returned. Default 200." }
          },
          "required": ["path"]
        }
        """));

    public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var path = ToolArgs.OptionalString(args, "path") ?? ".";
        var recursive = ToolArgs.OptionalBool(args, "recursive") ?? false;
        var maxEntries = ToolArgs.OptionalInt(args, "max_entries") ?? DefaultMaxEntries;
        var target = PathSafety.ResolveInsideRoot(context.Session.ProjectRoot, path);
        PathSafety.EnsureDirectory(target);

        var entries = new List<string>();
        var truncated = false;
        void Walk(string directory, int depth)
        {
            if (entries.Count >= maxEntries)
            {
                truncated = true;
                return;
            }

            foreach (var full in Directory.EnumerateFileSystemEntries(directory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(full);
                if (Directory.Exists(full) && ToolConstants.NoiseDirectories.Contains(name))
                {
                    continue;
                }

                var rel = PathSafety.ToRelative(context.Session.ProjectRoot, full);
                var isDir = Directory.Exists(full);
                entries.Add(isDir ? rel + "/" : rel);
                if (entries.Count >= maxEntries)
                {
                    truncated = true;
                    return;
                }

                if (recursive && isDir && depth < 32)
                {
                    Walk(full, depth + 1);
                }
            }
        }

        Walk(target, 0);
        var content = entries.Count == 0
            ? "(empty)"
            : string.Join(Environment.NewLine, entries) + (truncated ? Environment.NewLine + "... truncated" : "");
        return Task.FromResult(new ToolResult(
            $"{entries.Count} {(entries.Count == 1 ? "entry" : "entries")}{(truncated ? " (truncated)" : "")} in {PathSafety.ToRelative(context.Session.ProjectRoot, target)}",
            content,
            Metadata: ToolArgs.Metadata(("count", entries.Count), ("truncated", truncated))));
    }
}

public sealed class ReadFileTool : ITool
{
    private const int DefaultLength = 50_000;

    public ToolDefinition Definition { get; } = new(
        "read_file",
        "Read text contents of a file under the project root. Returns numbered lines and refuses binary files.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path relative to project root." },
            "offset": { "type": "number", "description": "Byte offset. Default 0." },
            "length": { "type": "number", "description": "Bytes to read. Default 50000." }
          },
          "required": ["path"]
        }
        """));

    public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var path = ToolArgs.RequiredString(args, "path");
        var offset = Math.Max(0, ToolArgs.OptionalInt(args, "offset") ?? 0);
        var length = Math.Max(1, ToolArgs.OptionalInt(args, "length") ?? DefaultLength);
        var target = PathSafety.ResolveInsideRoot(context.Session.ProjectRoot, path);
        if (Directory.Exists(target))
        {
            return Task.FromResult(new ToolResult("not a file", $"{path} is a directory", true));
        }

        var bytes = File.ReadAllBytes(target);
        if (bytes.Contains((byte)0))
        {
            return Task.FromResult(new ToolResult("binary file refused", $"{path} appears to be binary (contains null byte)", true));
        }

        var safeOffset = Math.Min(offset, bytes.Length);
        var count = Math.Min(length, bytes.Length - safeOffset);
        var text = Encoding.UTF8.GetString(bytes, safeOffset, count);
        var prefix = Encoding.UTF8.GetString(bytes, 0, safeOffset);
        var startLine = safeOffset == 0 ? 1 : prefix.Split(["\r\n", "\n"], StringSplitOptions.None).Length;
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
        var numbered = string.Join(Environment.NewLine, lines.Select((line, i) => $"{startLine + i,6}\t{line}"));
        var truncated = safeOffset + count < bytes.Length;
        return Task.FromResult(new ToolResult(
            $"{PathSafety.ToRelative(context.Session.ProjectRoot, target)}: {lines.Length} lines, {count} bytes{(truncated ? " (truncated)" : "")}",
            numbered,
            Metadata: ToolArgs.Metadata(("bytes", count), ("totalBytes", bytes.Length), ("truncated", truncated), ("startLine", startLine))));
    }
}

public sealed class EditFileTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        "edit_file",
        "Modify an existing file by replacing an exact text span. old_text must occur exactly once unless replace_all is true.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path relative to project root. Must exist." },
            "old_text": { "type": "string", "description": "Exact existing text to replace." },
            "new_text": { "type": "string", "description": "Replacement text." },
            "replace_all": { "type": "boolean", "description": "Replace every occurrence. Default false." }
          },
          "required": ["path", "old_text", "new_text"]
        }
        """));

    public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var path = ToolArgs.RequiredString(args, "path");
        var oldText = ToolArgs.RequiredString(args, "old_text");
        var newText = ToolArgs.RequiredString(args, "new_text");
        var replaceAll = ToolArgs.OptionalBool(args, "replace_all") ?? false;
        if (oldText == newText)
        {
            return Task.FromResult(new ToolResult("no-op", "old_text equals new_text", true));
        }

        var target = PathSafety.ResolveInsideRoot(context.Session.ProjectRoot, path);
        var original = File.ReadAllText(target);
        var count = CountOccurrences(original, oldText);
        if (count == 0)
        {
            return Task.FromResult(new ToolResult(
                "old_text not found",
                $"Could not find old_text in {PathSafety.ToRelative(context.Session.ProjectRoot, target)}. Read the file first and retry with exact text.",
                true));
        }

        if (count > 1 && !replaceAll)
        {
            return Task.FromResult(new ToolResult(
                $"ambiguous ({count} matches)",
                $"old_text appears {count} times in {PathSafety.ToRelative(context.Session.ProjectRoot, target)}. Provide a larger unique anchor or set replace_all=true.",
                true));
        }

        var updated = replaceAll ? original.Replace(oldText, newText) : ReplaceFirst(original, oldText, newText);
        context.Checkpoint?.SnapshotBeforeWrite(target);
        File.WriteAllText(target, updated);
        var rel = PathSafety.ToRelative(context.Session.ProjectRoot, target);
        return Task.FromResult(new ToolResult(
            $"edited {rel} ({(replaceAll ? count + " replacements" : "1 replacement")})",
            $"OK: {oldText.Length} -> {newText.Length} chars, {count} replacement(s)",
            Metadata: ToolArgs.Metadata(("replacements", count), ("replaceAll", replaceAll), ("before", original), ("after", updated), ("path", rel))));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string text, string oldText, string newText)
    {
        var index = text.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + newText + text[(index + oldText.Length)..];
    }
}

public sealed class WriteFileTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        "write_file",
        "Create a new file or overwrite an existing one. create_only refuses to clobber existing files.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path relative to project root." },
            "content": { "type": "string", "description": "Full file contents." },
            "mode": { "type": "string", "enum": ["create_only", "overwrite"], "description": "Default create_only." }
          },
          "required": ["path", "content"]
        }
        """));

    public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var path = ToolArgs.RequiredString(args, "path");
        var content = ToolArgs.RequiredString(args, "content");
        var mode = ToolArgs.OptionalString(args, "mode") ?? "create_only";
        if (mode is not ("create_only" or "overwrite"))
        {
            return Task.FromResult(new ToolResult("bad mode", $"unknown mode: {mode}", true));
        }

        var target = PathSafety.ResolveInsideRoot(context.Session.ProjectRoot, path);
        var exists = File.Exists(target);
        if (exists && mode == "create_only")
        {
            return Task.FromResult(new ToolResult(
                "file exists",
                $"{PathSafety.ToRelative(context.Session.ProjectRoot, target)} already exists. Use edit_file or set mode=overwrite.",
                true));
        }

        var before = exists ? File.ReadAllText(target) : "";
        context.Checkpoint?.SnapshotBeforeWrite(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
        var rel = PathSafety.ToRelative(context.Session.ProjectRoot, target);
        return Task.FromResult(new ToolResult(
            $"{(exists ? "overwrote" : "created")} {rel} ({content.Length} bytes)",
            "OK",
            Metadata: ToolArgs.Metadata(("bytes", content.Length), ("mode", mode), ("existed", exists), ("before", before), ("after", content), ("path", rel))));
    }
}

public sealed class CreateDirectoryTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        "create_directory",
        "Create a new directory under the project root. Intermediate parent directories are created automatically.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Directory path relative to the project root." },
            "exist_ok": { "type": "boolean", "description": "If true, return success when the directory already exists. Default false." }
          },
          "required": ["path"]
        }
        """));

    public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var path = ToolArgs.RequiredString(args, "path");
        var existOk = ToolArgs.OptionalBool(args, "exist_ok") ?? false;
        string target;
        try
        {
            target = PathSafety.ResolveInsideRoot(context.Session.ProjectRoot, path);
        }
        catch (PathSafetyException ex)
        {
            return Task.FromResult(new ToolResult("path escapes project root", $"Refused: {ex.Message}", true));
        }

        var rel = PathSafety.ToRelative(context.Session.ProjectRoot, target);
        if (Directory.Exists(target))
        {
            return Task.FromResult(existOk
                ? new ToolResult($"already existed: {rel}", "OK", Metadata: ToolArgs.Metadata(("path", rel), ("preExisted", true)))
                : new ToolResult($"already exists: {rel}", $"{rel} already exists. Pass exist_ok=true to ignore.", true));
        }

        if (File.Exists(target))
        {
            return Task.FromResult(new ToolResult("path exists but is not a directory", $"{rel} already exists as a file.", true));
        }

        Directory.CreateDirectory(target);
        return Task.FromResult(new ToolResult($"created {rel}", "OK", Metadata: ToolArgs.Metadata(("path", rel), ("preExisted", false))));
    }
}

public sealed class DeletePathTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        "delete_path",
        "Delete files or directories inside the project by moving them to recoverable session trash.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "paths": { "type": "array", "items": { "type": "string" }, "description": "Paths relative to project root." },
            "path": { "type": "string", "description": "A single path to delete." }
          }
        }
        """));

    public async Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var raw = ToolArgs.StringList(args, "paths");
        var single = ToolArgs.OptionalString(args, "path");
        if (!string.IsNullOrWhiteSpace(single))
        {
            raw.Add(single);
        }

        if (raw.Count == 0)
        {
            return new ToolResult("no paths", "Provide paths or path.", true);
        }

        if (context.Checkpoint is null)
        {
            return new ToolResult("delete unavailable", "No checkpoint store attached; refusing to delete without recoverable trash.", true);
        }

        var targets = new List<(string Abs, string Rel, bool IsDir, int Files, int Dirs)>();
        foreach (var p in raw)
        {
            string abs;
            try
            {
                abs = PathSafety.ResolveInsideRoot(context.Session.ProjectRoot, p);
            }
            catch (PathSafetyException ex)
            {
                return new ToolResult("refused", $"Refused: {ex.Message}", true);
            }

            var stats = Inspect(abs);
            targets.Add((abs, PathSafety.ToRelative(context.Session.ProjectRoot, abs), stats.IsDir, stats.Files, stats.Dirs));
        }

        if (context.Session.Mode == AgentMode.Autocode && context.ConfirmAsync is not null)
        {
            var large = targets.Any(t => t.IsDir && (t.Dirs > 0 || t.Files > 5));
            if (large)
            {
                var summary = string.Join(", ", targets.Select(t => $"{t.Rel} ({(t.IsDir ? $"{t.Files} files, {t.Dirs} dirs" : "file")})"));
                var ok = await context.ConfirmAsync($"Delete {summary}? Recoverable from trash.", cancellationToken).ConfigureAwait(false);
                if (!ok)
                {
                    return new ToolResult("user declined", "User declined the deletion.", true);
                }
            }
        }

        var trashed = new List<string>();
        foreach (var target in targets)
        {
            context.Checkpoint.Trash(target.Abs);
            trashed.Add(target.Rel);
        }

        return new ToolResult(
            $"moved {trashed.Count} path{(trashed.Count == 1 ? "" : "s")} to trash",
            "Moved to recoverable trash:\n" + string.Join(Environment.NewLine, trashed),
            Metadata: ToolArgs.Metadata(("trashed", trashed)));
    }

    private static (bool IsDir, int Files, int Dirs) Inspect(string abs)
    {
        if (File.Exists(abs))
        {
            return (false, 1, 0);
        }

        var files = 0;
        var dirs = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(abs, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(entry))
            {
                dirs++;
            }
            else
            {
                files++;
            }
        }

        return (true, files, dirs);
    }
}

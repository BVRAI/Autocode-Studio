using System.Text.RegularExpressions;
using AutoCode.Engine.Auth;
using AutoCode.Engine.Safety;

namespace AutoCode.Engine.Tools;

public sealed record TodoItem(string Id, string Text, string Status);

public sealed class TodoWriteTool : ITool
{
    private static readonly Dictionary<string, List<TodoItem>> SessionLists = new(StringComparer.Ordinal);

    public ToolDefinition Definition { get; } = new(
        "todo_write",
        "Maintain a checklist of subtasks for the current request. Use for non-trivial tasks and update statuses as work progresses.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["set", "update"], "description": "set replaces the list; update changes one item." },
            "items": { "type": "array", "items": { "type": "object" }, "description": "Required for action=set." },
            "id": { "type": "string", "description": "Required for action=update." },
            "text": { "type": "string", "description": "Optional updated text." },
            "status": { "type": "string", "enum": ["pending", "in_progress", "completed", "interrupted"] }
          },
          "required": ["action"]
        }
        """));

    public static IReadOnlyList<TodoItem> CurrentTodos(string sessionId) =>
        SessionLists.TryGetValue(sessionId, out var list) ? list : [];

    public static void MarkInProgressInterrupted(string sessionId)
    {
        if (!SessionLists.TryGetValue(sessionId, out var list))
        {
            return;
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Status == "in_progress")
            {
                list[i] = list[i] with { Status = "interrupted" };
            }
        }
    }

    public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var action = ToolArgs.RequiredString(args, "action");
        var list = SessionLists.TryGetValue(context.Session.SessionId, out var existing) ? existing : [];
        if (action == "set")
        {
            if (!args.TryGetValue("items", out var raw) || raw is not IEnumerable<object?> items)
            {
                return Task.FromResult(new ToolResult("bad input", "items must be an array", true));
            }

            var newList = new List<TodoItem>();
            var idx = 1;
            foreach (var item in items)
            {
                if (item is not Dictionary<string, object?> dict)
                {
                    continue;
                }

                var id = dict.TryGetValue("id", out var idValue) ? Convert.ToString(idValue) : null;
                var text = dict.TryGetValue("text", out var textValue) ? Convert.ToString(textValue) : "";
                var status = dict.TryGetValue("status", out var statusValue) ? NormalizeStatus(Convert.ToString(statusValue)) : "pending";
                newList.Add(new TodoItem(string.IsNullOrWhiteSpace(id) ? $"t{idx}" : id, text ?? "", status));
                idx++;
            }

            SessionLists[context.Session.SessionId] = newList;
            return Task.FromResult(new ToolResult($"set {newList.Count} todo{(newList.Count == 1 ? "" : "s")}", Render(newList), Metadata: ToolArgs.Metadata(("count", newList.Count))));
        }

        if (action == "update")
        {
            var id = ToolArgs.RequiredString(args, "id");
            var todoIndex = list.FindIndex(t => t.Id == id);
            if (todoIndex < 0)
            {
                return Task.FromResult(new ToolResult("todo not found", $"no todo with id={id}", true));
            }

            var current = list[todoIndex];
            var text = ToolArgs.OptionalString(args, "text") ?? current.Text;
            var status = NormalizeStatus(ToolArgs.OptionalString(args, "status") ?? current.Status);
            list[todoIndex] = new TodoItem(id, text, status);
            SessionLists[context.Session.SessionId] = list;
            return Task.FromResult(new ToolResult($"updated {id} -> {status}", Render(list), Metadata: ToolArgs.Metadata(("id", id), ("status", status))));
        }

        return Task.FromResult(new ToolResult("bad action", $"unknown action: {action}", true));
    }

    private static string NormalizeStatus(string? raw) =>
        raw is "in_progress" or "completed" or "interrupted" ? raw : "pending";

    private static string Render(IReadOnlyList<TodoItem> items) =>
        items.Count == 0
            ? "(no todos)"
            : string.Join(Environment.NewLine, items.Select(t => $"{Symbol(t.Status)} {t.Id}. {t.Text}"));

    private static string Symbol(string status) =>
        status switch
        {
            "in_progress" => "[~]",
            "completed" => "[x]",
            "interrupted" => "[!]",
            _ => "[ ]"
        };
}

public sealed class AskUserTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        "ask_user",
        "Ask the user a multiple-choice question and get their selection instead of guessing an ambiguous requirement.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "question": { "type": "string", "description": "The question to ask." },
            "options": { "type": "array", "items": { "type": "string" }, "description": "Choices to offer." },
            "multi_select": { "type": "boolean", "description": "Allow several selections. Default false." }
          },
          "required": ["question", "options"]
        }
        """));

    public async Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var question = ToolArgs.RequiredString(args, "question");
        var options = ToolArgs.StringList(args, "options");
        var multi = ToolArgs.OptionalBool(args, "multi_select") ?? false;
        if (options.Count < 2)
        {
            return new ToolResult("bad options", "Provide at least 2 options.", true);
        }

        if (context.ChooseAsync is null)
        {
            return new ToolResult("no interactive user", "No interactive user is available; proceed with best judgment and state the assumption.", true);
        }

        var picked = await context.ChooseAsync(new AskUserRequest(question, options, multi), cancellationToken).ConfigureAwait(false);
        if (picked.Count == 0)
        {
            return new ToolResult("no selection", "The user made no selection; proceed with best judgment.");
        }

        var chosen = picked.Where(i => i >= 0 && i < options.Count).Select(i => $"{(char)('A' + i)}) {options[i]}").ToList();
        return new ToolResult($"user selected {chosen.Count} option{(chosen.Count == 1 ? "" : "s")}", "User selected: " + string.Join(", ", chosen));
    }
}

public sealed class UseSkillTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        "use_skill",
        "Load the full body of a named skill from project or user skill directories.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "The skill name." }
          },
          "required": ["name"]
        }
        """));

    public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var name = ToolArgs.RequiredString(args, "name");
        var skills = SkillLoader.GetSkills(context.Session.ProjectRoot);
        var match = skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return Task.FromResult(new ToolResult(
                $"unknown skill: {name}",
                skills.Count == 0
                    ? "No skills are configured. Add Markdown files under .autocode/skills or ~/.autocode-gui/skills."
                    : "Available: " + string.Join(", ", skills.Select(s => s.Name)),
                true));
        }

        return Task.FromResult(new ToolResult($"loaded skill {match.Name}", match.Body, Metadata: ToolArgs.Metadata(("skill", match.Name), ("source", match.Source), ("bytes", match.Body.Length))));
    }
}

public sealed record SkillInfo(string Name, string Description, string Body, string Source);

public static class SkillLoader
{
    public static IReadOnlyList<SkillInfo> GetSkills(string projectRoot)
    {
        var dirs = new[]
        {
            Path.Combine(projectRoot, ".autocode", "skills"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".autocode-gui", "skills"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".autocode", "skills")
        };
        var skills = new List<SkillInfo>();
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
            {
                try
                {
                    var raw = File.ReadAllText(file);
                    var (meta, body) = ParseFrontMatter(raw);
                    var fallback = Path.GetFileNameWithoutExtension(file);
                    skills.Add(new SkillInfo(
                        meta.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n) ? n : fallback,
                        meta.TryGetValue("description", out var d) ? d : "",
                        body,
                        file));
                }
                catch
                {
                    // Ignore unreadable skill files.
                }
            }
        }

        return skills
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

public sealed class FindSymbolTool : ITool
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".go", ".rs", ".java", ".rb", ".php", ".cpp", ".c", ".h", ".hpp"
    };

    public ToolDefinition Definition { get; } = new(
        "find_symbol",
        "Locate where a named identifier is declared and/or used across source files.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "Identifier to look up." },
            "kind": { "type": "string", "enum": ["definition", "reference", "any"], "description": "Default any." },
            "language": { "type": "string", "description": "Optional language family such as csharp, typescript, python." }
          },
          "required": ["name"]
        }
        """));

    public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var name = ToolArgs.RequiredString(args, "name");
        var kind = ToolArgs.OptionalString(args, "kind") ?? "any";
        var language = ToolArgs.OptionalString(args, "language");
        var extensions = ExtensionsFor(language);
        if (language is not null && extensions.Count == 0)
        {
            return Task.FromResult(new ToolResult("unsupported language", "Unsupported language filter.", true));
        }

        var refRegex = new Regex(@"\b" + Regex.Escape(name) + @"\b");
        var hits = new List<string>();
        var total = 0;
        foreach (var file in GlobTool.EnumerateFiles(context.Session.ProjectRoot, cancellationToken).Take(800))
        {
            var ext = Path.GetExtension(file);
            if (extensions.Count > 0 ? !extensions.Contains(ext) : !SourceExtensions.Contains(ext))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            if (text.Length > 64_000)
            {
                text = text[..64_000];
            }

            var declRegex = DeclarationRegex(ext, name);
            var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var isDefinition = declRegex?.IsMatch(line) ?? false;
                var isReference = refRegex.IsMatch(line);
                if (!isDefinition && !isReference)
                {
                    continue;
                }

                if (kind == "definition" && !isDefinition)
                {
                    continue;
                }

                total++;
                if (hits.Count < 200)
                {
                    var column = Math.Max(1, line.IndexOf(name, StringComparison.Ordinal) + 1);
                    hits.Add($"{PathSafety.ToRelative(context.Session.ProjectRoot, file)}:{i + 1}:{column}  {Trim(line.Trim(), 200)}");
                }
            }
        }

        var truncated = total > hits.Count;
        return Task.FromResult(new ToolResult(
            total == 0 ? $"no matches for {name}" : $"{total}{(truncated ? "+" : "")} matches for {name}",
            hits.Count == 0 ? $"(no matches for `{name}`)" : string.Join(Environment.NewLine, hits) + (truncated ? Environment.NewLine + "... truncated at 200 hits" : ""),
            Metadata: ToolArgs.Metadata(("total", total), ("truncated", truncated), ("name", name), ("kind", kind))));
    }

    private static HashSet<string> ExtensionsFor(string? language) =>
        language?.ToLowerInvariant() switch
        {
            "csharp" => [".cs"],
            "typescript" => [".ts", ".tsx"],
            "javascript" => [".js", ".jsx", ".mjs", ".cjs"],
            "python" => [".py"],
            "go" => [".go"],
            "rust" => [".rs"],
            "java" => [".java"],
            "ruby" => [".rb"],
            "php" => [".php"],
            null => [],
            _ => []
        };

    private static Regex? DeclarationRegex(string ext, string name)
    {
        var n = Regex.Escape(name);
        return ext.ToLowerInvariant() switch
        {
            ".cs" => new Regex(@"\b(class|interface|record|struct|enum|void|var|public|private|protected|internal|static|async)\b.*\b" + n + @"\b"),
            ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" => new Regex(@"\b(function|class|interface|type|const|let|var)\s+" + n + @"\b"),
            ".py" => new Regex(@"^\s*(def|class)\s+" + n + @"\b"),
            ".go" => new Regex(@"\b(func|type|var|const)\s+" + n + @"\b"),
            ".rs" => new Regex(@"\b(fn|struct|enum|trait|impl|mod|type|let)\s+" + n + @"\b"),
            _ => null
        };
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}

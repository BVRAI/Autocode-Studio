using System.Diagnostics;
using System.Text.Json.Nodes;
using AutoCode.Engine.Agent;
using AutoCode.Engine.Auth;
using AutoCode.Engine.Llm;
using AutoCode.Engine.Session;

namespace AutoCode.Engine.Tools;

public sealed record ToolDefinition(string Name, string Description, JsonNode InputSchema);

public sealed record ToolResult(
    string Summary,
    string Content,
    bool IsError = false,
    Dictionary<string, object?>? Metadata = null);

public interface ITool
{
    ToolDefinition Definition { get; }

    Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken);
}

public sealed class ToolExecutionContext
{
    public required SessionContext Session { get; init; }

    public Func<string, CancellationToken, Task<bool>>? ConfirmAsync { get; init; }

    public Func<AskUserRequest, CancellationToken, Task<IReadOnlyList<int>>>? ChooseAsync { get; init; }

    public CheckpointStore? Checkpoint { get; init; }
}

public sealed record AskUserRequest(string Question, IReadOnlyList<string> Options, bool MultiSelect);

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    public ToolRegistry(AutocodeConfig config)
    {
        Register(new ListDirectoryTool());
        Register(new ReadFileTool());
        Register(new EditFileTool());
        Register(new WriteFileTool());
        Register(new CreateDirectoryTool());
        Register(new DeletePathTool());
        Register(new RunShellTool());
        Register(new GlobTool());
        Register(new GrepTool());
        Register(new TodoWriteTool());
        Register(new FindSymbolTool());
        Register(new FileDepsTool());
        Register(new UseSkillTool());
        Register(new AskUserTool());
        if (config.WebTools.Enabled)
        {
            Register(new WebFetchTool(config));
            Register(new WebSearchTool(config));
        }
    }

    public IReadOnlyList<ToolSchema> Schemas() =>
        _tools.Values.Select(t => new ToolSchema(t.Definition.Name, t.Definition.Description, t.Definition.InputSchema)).ToList();

    public IReadOnlyList<string> Names => _tools.Keys.OrderBy(k => k).ToList();

    public void Register(ITool tool)
    {
        _tools[tool.Definition.Name] = tool;
    }

    public async Task<ToolResult> ExecuteAsync(
        string name,
        Dictionary<string, object?> args,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            return new ToolResult("unknown tool", $"no such tool: {name}", true);
        }

        try
        {
            return await tool.ExecuteAsync(args, context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ToolResult("tool error", $"{ex.GetType().Name}: {ex.Message}", true);
        }
    }
}

public static class ToolArgs
{
    public static string RequiredString(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            throw new ArgumentException($"argument '{key}' must be a non-empty string");
        }

        var s = value as string ?? Convert.ToString(value);
        if (string.IsNullOrEmpty(s))
        {
            throw new ArgumentException($"argument '{key}' must be a non-empty string");
        }

        return s;
    }

    public static string? OptionalString(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value as string ?? Convert.ToString(value);
    }

    public static bool? OptionalBool(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is bool b)
        {
            return b;
        }

        return bool.TryParse(Convert.ToString(value), out var parsed)
            ? parsed
            : throw new ArgumentException($"argument '{key}' must be a boolean");
    }

    public static int? OptionalInt(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is int i)
        {
            return i;
        }

        if (value is long l)
        {
            return checked((int)l);
        }

        if (value is double d)
        {
            return checked((int)d);
        }

        return int.TryParse(Convert.ToString(value), out var parsed)
            ? parsed
            : throw new ArgumentException($"argument '{key}' must be a number");
    }

    public static List<string> StringList(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        if (value is List<object?> list)
        {
            return list.Select(v => Convert.ToString(v)).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList();
        }

        if (value is IEnumerable<object?> items)
        {
            return items.Select(v => Convert.ToString(v)).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList();
        }

        if (value is IEnumerable<string> strings)
        {
            return strings.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }

        return [];
    }

    public static JsonNode Schema(string json) => JsonNode.Parse(json)!;

    public static Dictionary<string, object?> Metadata(params (string Key, object? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    public static string Json(Dictionary<string, object?> args) => JsonHelpers.DictionaryToJson(args).ToJsonString();

    public static async Task<ProcessResult> RunProcessAsync(
        string command,
        string workingDirectory,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeoutMs);
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c " + command : "-c \"" + command.Replace("\"", "\\\"") + "\"",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new List<string>();
        var stderr = new List<string>();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.Add(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, string.Join(Environment.NewLine, stdout), string.Join(Environment.NewLine, stderr), false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore process kill failures.
            }

            return new ProcessResult(null, string.Join(Environment.NewLine, stdout), string.Join(Environment.NewLine, stderr), true);
        }
    }
}

public sealed record ProcessResult(int? ExitCode, string Stdout, string Stderr, bool TimedOut);

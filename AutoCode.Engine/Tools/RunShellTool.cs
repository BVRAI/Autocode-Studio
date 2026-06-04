using System.Diagnostics;
using AutoCode.Engine.Safety;

namespace AutoCode.Engine.Tools;

public sealed class RunShellTool : ITool
{
    private const int DefaultTimeoutSeconds = 300;
    private const int MaxOutputChars = 100_000;
    private static readonly List<Process> BackgroundProcesses = [];

    public ToolDefinition Definition { get; } = new(
        "run_shell",
        "Run a shell command under the project root. Commands are classified by the safety policy. Supports background:true for dev servers.",
        ToolArgs.Schema("""
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "The shell command to run." },
            "working_directory": { "type": "string", "description": "Subdirectory relative to project root. Default root." },
            "timeout_seconds": { "type": "number", "description": "Hard timeout. Default 300." },
            "background": { "type": "boolean", "description": "Run as a long-lived process. Default false." }
          },
          "required": ["command"]
        }
        """));

    public async Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var command = ToolArgs.RequiredString(args, "command");
        var workingDirectory = ToolArgs.OptionalString(args, "working_directory");
        var timeout = ToolArgs.OptionalInt(args, "timeout_seconds") ?? DefaultTimeoutSeconds;
        var background = ToolArgs.OptionalBool(args, "background") ?? false;
        var verdict = SafetyPolicy.Classify(command, context.Session.ProjectRoot);
        if (verdict.Kind == SafetyKind.Block)
        {
            return new ToolResult(
                $"blocked: {verdict.Reason}",
                $"Command refused by safety policy: {verdict.Reason}\nPattern: {verdict.Pattern ?? "(n/a)"}",
                true,
                ToolArgs.Metadata(("verdict", verdict.Kind.ToString())));
        }

        if (verdict.Kind == SafetyKind.Confirm)
        {
            if (context.ConfirmAsync is null)
            {
                return new ToolResult("confirm required", $"Command requires confirmation ({verdict.Reason}) but no prompt is attached.", true);
            }

            var ok = await context.ConfirmAsync($"Risky command ({verdict.Reason}): {command}\nRun it?", cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                return new ToolResult("user declined", "User declined to run the command.", true);
            }
        }

        var cwd = workingDirectory is null
            ? context.Session.ProjectRoot
            : PathSafety.ResolveInsideRoot(context.Session.ProjectRoot, workingDirectory);

        if (background)
        {
            return await RunBackgroundAsync(command, cwd, context.Session.ProjectRoot, cancellationToken).ConfigureAwait(false);
        }

        var result = await ToolArgs.RunProcessAsync(command, cwd, timeout * 1000, cancellationToken).ConfigureAwait(false);
        var output = TrimOutput(result.Stdout, result.Stderr);
        var rel = PathSafety.ToRelative(context.Session.ProjectRoot, cwd);
        return new ToolResult(
            $"exit {result.ExitCode?.ToString() ?? "n/a"} in {(string.IsNullOrWhiteSpace(rel) ? "." : rel)}{(result.TimedOut ? " (timed out)" : "")}",
            output,
            result.ExitCode != 0 || result.TimedOut,
            ToolArgs.Metadata(("exitCode", result.ExitCode), ("timedOut", result.TimedOut), ("stdoutBytes", result.Stdout.Length), ("stderrBytes", result.Stderr.Length)));
    }

    public static void StopBackgroundProcesses()
    {
        foreach (var process in BackgroundProcesses.ToList())
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
                // Ignore shutdown failures.
            }
        }

        BackgroundProcesses.Clear();
    }

    private static async Task<ToolResult> RunBackgroundAsync(string command, string cwd, string projectRoot, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c " + command : "-c \"" + command.Replace("\"", "\\\"") + "\"",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new List<string>();
        var exited = false;
        int? code = null;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null && string.Join('\n', output).Length < MaxOutputChars) output.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null && string.Join('\n', output).Length < MaxOutputChars) output.Add(e.Data); };
        process.Exited += (_, _) =>
        {
            exited = true;
            code = process.ExitCode;
            BackgroundProcesses.Remove(process);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        BackgroundProcesses.Add(process);
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

        var text = string.Join(Environment.NewLine, output);
        return new ToolResult(
            exited
                ? $"process exited (code {code?.ToString() ?? "n/a"}) in {PathSafety.ToRelative(projectRoot, cwd)}"
                : $"started in background (pid {process.Id}) in {PathSafety.ToRelative(projectRoot, cwd)}",
            (exited ? $"Process already exited (code {code?.ToString() ?? "n/a"})." : $"Process is running (pid {process.Id}); it will stop when the app exits.")
            + (text.Length > 0 ? "\n--- startup output ---\n" + text : "\n(no startup output)"),
            exited && code != 0,
            ToolArgs.Metadata(("background", true), ("pid", process.Id), ("exited", exited), ("exitCode", code)));
    }

    private static string TrimOutput(string stdout, string stderr)
    {
        var combined = (stdout.Length > 0 ? "--- stdout ---\n" + stdout + "\n" : "")
            + (stderr.Length > 0 ? "--- stderr ---\n" + stderr + "\n" : "");
        if (combined.Length == 0)
        {
            return "(no output)";
        }

        return combined.Length <= MaxOutputChars
            ? combined
            : combined[..MaxOutputChars] + $"\n... truncated ({combined.Length - MaxOutputChars} chars more)";
    }
}

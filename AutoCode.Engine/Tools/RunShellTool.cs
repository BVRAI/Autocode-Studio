using System.Diagnostics;
using System.Text;
using AutoCode.Engine.Safety;

namespace AutoCode.Engine.Tools;

public sealed class RunShellTool : ITool
{
    private const int DefaultTimeoutSeconds = 300;
    internal const int MaxModelOutputChars = 30_000;
    private const int StderrReservedChars = 10_000;
    private const double HeadFraction = 0.3;
    private const int CaptureHeadChars = 200_000;
    private const int CaptureTailChars = 200_000;
    private const int BackgroundStartupChars = 20_000;

    // Background processes keyed by session id so concurrent workspaces don't share (or kill)
    // each other's dev servers. Guarded by BackgroundLock — Exited handlers fire on pool threads.
    private static readonly object BackgroundLock = new();
    private static readonly Dictionary<string, List<Process>> BackgroundProcesses = new(StringComparer.Ordinal);

    public ToolDefinition Definition { get; } = new(
        "run_shell",
        "Run a shell command under the project root. Commands are classified by the safety policy. Supports background:true for dev servers. Output is middle-truncated to preserve both startup context and failure summaries at the end.",
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
            return await RunBackgroundAsync(command, cwd, context.Session.ProjectRoot, context.Session.SessionId, cancellationToken).ConfigureAwait(false);
        }

        var result = await RunCommandAsync(command, cwd, timeout * 1000, cancellationToken).ConfigureAwait(false);
        var output = TrimOutput(result.Stdout, result.Stderr);
        var rel = PathSafety.ToRelative(context.Session.ProjectRoot, cwd);
        return new ToolResult(
            $"exit {result.ExitCode?.ToString() ?? "n/a"} in {(string.IsNullOrWhiteSpace(rel) ? "." : rel)}{(result.TimedOut ? " (timed out)" : "")}",
            output.Content,
            result.ExitCode != 0 || result.TimedOut,
            ToolArgs.Metadata(
                ("exitCode", result.ExitCode),
                ("timedOut", result.TimedOut),
                ("stdoutBytes", result.Stdout.Bytes),
                ("stderrBytes", result.Stderr.Bytes),
                ("stdoutChars", result.Stdout.Chars),
                ("stderrChars", result.Stderr.Chars),
                ("stdoutTruncated", output.StdoutTruncated),
                ("stderrTruncated", output.StderrTruncated)));
    }

    /// <summary>Stop background processes for one session, or all sessions when
    /// <paramref name="sessionId"/> is null (app exit).</summary>
    public static void StopBackgroundProcesses(string? sessionId = null)
    {
        List<Process> toStop;
        lock (BackgroundLock)
        {
            if (sessionId is null)
            {
                toStop = BackgroundProcesses.Values.SelectMany(list => list).ToList();
                BackgroundProcesses.Clear();
            }
            else if (BackgroundProcesses.Remove(sessionId, out var list))
            {
                toStop = list;
            }
            else
            {
                return;
            }
        }

        foreach (var process in toStop)
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
    }

    private static async Task<ToolResult> RunBackgroundAsync(string command, string cwd, string projectRoot, string sessionId, CancellationToken cancellationToken)
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
        var output = new StringBuilder();
        var clipped = false;
        var exited = false;
        int? code = null;
        void Cap(string? value)
        {
            if (value is null || clipped)
            {
                return;
            }

            var line = value + Environment.NewLine;
            var room = BackgroundStartupChars - output.Length;
            if (room <= 0)
            {
                clipped = true;
                return;
            }

            output.Append(line.Length <= room ? line : line[..room]);
            if (line.Length > room)
            {
                clipped = true;
            }
        }

        process.OutputDataReceived += (_, e) => Cap(e.Data);
        process.ErrorDataReceived += (_, e) => Cap(e.Data);
        process.Exited += (_, _) =>
        {
            exited = true;
            code = process.ExitCode;
            lock (BackgroundLock)
            {
                if (BackgroundProcesses.TryGetValue(sessionId, out var list))
                {
                    list.Remove(process);
                }
            }
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (BackgroundLock)
        {
            if (!BackgroundProcesses.TryGetValue(sessionId, out var list))
            {
                BackgroundProcesses[sessionId] = list = [];
            }

            list.Add(process);
        }
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

        var text = output.ToString();
        return new ToolResult(
            exited
                ? $"process exited (code {code?.ToString() ?? "n/a"}) in {PathSafety.ToRelative(projectRoot, cwd)}"
                : $"started in background (pid {process.Id}) in {PathSafety.ToRelative(projectRoot, cwd)}",
            (exited ? $"Process already exited (code {code?.ToString() ?? "n/a"})." : $"Process is running (pid {process.Id}); it will stop when the app exits.")
            + (text.Length > 0 ? "\n--- startup output ---\n" + text + (clipped ? "\n... [startup output truncated]" : "") : "\n(no startup output)"),
            exited && code != 0,
            ToolArgs.Metadata(("background", true), ("pid", process.Id), ("exited", exited), ("exitCode", code)));
    }

    private static Task<CommandResult> RunCommandAsync(string command, string cwd, int timeoutMs, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);
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

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdout = CreateCapture();
            var stderr = CreateCapture();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stdout.Push(e.Data + Environment.NewLine);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stderr.Push(e.Data + Environment.NewLine);
                }
            };

            var timedOut = false;
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                process.WaitForExit();
                return new CommandResult(process.ExitCode, stdout.Snapshot(), stderr.Snapshot(), false);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
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

                return new CommandResult(null, stdout.Snapshot(), stderr.Snapshot(), timedOut);
            }
            catch (Exception ex)
            {
                stderr.Push(Environment.NewLine + "[spawn error] " + ex.Message);
                return new CommandResult(1, stdout.Snapshot(), stderr.Snapshot(), timedOut);
            }
        }, cancellationToken);
    }

    internal static Capture CreateCapture() => new(CaptureHeadChars, CaptureTailChars);

    internal static TruncatedStream MiddleTruncate(CapturedStream stream, int budget)
    {
        if (budget <= 0)
        {
            return new TruncatedStream("", stream.Chars > 0);
        }

        var captureGap = stream.Chars - stream.Head.Length - stream.Tail.Length;
        if (captureGap == 0)
        {
            var full = stream.Head + stream.Tail;
            if (full.Length <= budget)
            {
                return new TruncatedStream(full, false);
            }

            var headKeep = Math.Max(0, (int)Math.Floor(budget * HeadFraction));
            var tailKeep = Math.Max(0, budget - headKeep);
            return new TruncatedStream(
                full[..headKeep] + OmissionMarker(full.Length - headKeep - tailKeep) + full[(full.Length - tailKeep)..],
                true);
        }

        var keepHead = Math.Min(stream.Head.Length, Math.Max(0, (int)Math.Floor(budget * HeadFraction)));
        var keepTail = Math.Min(stream.Tail.Length, Math.Max(0, budget - keepHead));
        return new TruncatedStream(
            stream.Head[..keepHead] + OmissionMarker(stream.Chars - keepHead - keepTail) + stream.Tail[(stream.Tail.Length - keepTail)..],
            true);
    }

    internal static TrimmedOutput TrimOutput(CapturedStream stdout, CapturedStream stderr)
    {
        var err = MiddleTruncate(stderr, Math.Min(StderrReservedChars, MaxModelOutputChars));
        var outText = MiddleTruncate(stdout, Math.Max(0, MaxModelOutputChars - err.Text.Length));
        var combined = (outText.Text.Length > 0 ? "--- stdout ---\n" + outText.Text + "\n" : "")
            + (err.Text.Length > 0 ? "--- stderr ---\n" + err.Text + "\n" : "");
        return new TrimmedOutput(
            combined.Length == 0 ? "(no output)" : combined,
            outText.Truncated,
            err.Truncated);
    }

    private static string OmissionMarker(int chars)
        => $"\n... [{chars} chars omitted - output was middle-truncated; failures usually appear near the end] ...\n";

    private sealed record CommandResult(int? ExitCode, CapturedStream Stdout, CapturedStream Stderr, bool TimedOut);
}

internal sealed class Capture
{
    private readonly int _headChars;
    private readonly int _tailChars;
    private readonly object _lock = new();
    private string _head = "";
    private string _tail = "";
    private int _chars;
    private int _bytes;

    public Capture(int headChars, int tailChars)
    {
        _headChars = headChars;
        _tailChars = tailChars;
    }

    public void Push(string text)
    {
        lock (_lock)
        {
            _chars += text.Length;
            _bytes += Encoding.UTF8.GetByteCount(text);
            if (_head.Length < _headChars)
            {
                var room = _headChars - _head.Length;
                _head += text.Length <= room ? text : text[..room];
                if (text.Length > room)
                {
                    _tail = (_tail + text[room..]).TakeLastString(_tailChars);
                }

                return;
            }

            _tail = (_tail + text).TakeLastString(_tailChars);
        }
    }

    public CapturedStream Snapshot()
    {
        lock (_lock)
        {
            return new CapturedStream(_head, _tail, _chars, _bytes);
        }
    }
}

internal sealed record CapturedStream(string Head, string Tail, int Chars, int Bytes);

internal sealed record TruncatedStream(string Text, bool Truncated);

internal sealed record TrimmedOutput(string Content, bool StdoutTruncated, bool StderrTruncated);

internal static class CaptureStringExtensions
{
    public static string TakeLastString(this string value, int count)
        => value.Length <= count ? value : value[^count..];
}

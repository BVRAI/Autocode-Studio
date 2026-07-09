using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AutoCode.Engine.Agent;

namespace AutoCode.Engine.Backends;

/// <summary>
/// Runs the OpenAI <c>codex</c> CLI headlessly in the workspace's project root and translates its
/// <c>codex exec --json</c> NDJSON into the shared <see cref="AgentEvent"/> stream — so an external
/// agent renders in the UI exactly like the built-in one.
///
/// Event schema verified live against codex-cli 0.142.5 (2026-07-07): <c>thread.started</c> carries the
/// thread id used for multi-turn continuity (<c>codex exec … resume &lt;id&gt;</c> — exec flags come
/// BEFORE the <c>resume</c> subcommand); items arrive as <c>item.started</c>/<c>item.completed</c> with
/// <c>item.type</c> of <c>agent_message</c> / <c>command_execution</c> / <c>file_change</c> /
/// <c>reasoning</c>; usage arrives per turn on <c>turn.completed</c>. The prompt is passed as <c>-</c>
/// and written to stdin (codex always reads stdin when it is a pipe, so it must be closed either way).
/// <c>--skip-git-repo-check</c> is required for non-git project roots.
///
/// Auth defaults to the user's ChatGPT login (<c>codex login</c>): <c>OPENAI_API_KEY</c> is removed from
/// the child env unless the user configured api-key mode. The CLI runs with
/// <c>--dangerously-bypass-approvals-and-sandbox</c> because each workspace is isolated in its own git
/// worktree.
/// </summary>
public sealed class CodexBackend : IAgentBackend
{
    private readonly Func<AgentEvent, Task> _emit;
    private readonly Func<ExternalAgentAuth>? _authProvider;
    private readonly HashSet<string> _startedItems = new(StringComparer.Ordinal);
    private Process? _process;
    private string? _threadId;
    private int _cumIn;
    private int _cumOut;
    private volatile bool _cancelled;

    /// <param name="authProvider">Resolved live per submit (like the router's AuthResolver) so auth
    /// settings changes apply without rebuilding the backend — rebuilding would drop the thread id.</param>
    public CodexBackend(Func<AgentEvent, Task> emit, Func<ExternalAgentAuth>? authProvider = null)
    {
        _emit = emit;
        _authProvider = authProvider;
    }

    public string Id => "codex";

    public string DisplayName => "Codex";

    public (int InputTokens, int OutputTokens) CumulativeUsage => (_cumIn, _cumOut);

    public void Cancel()
    {
        _cancelled = true;
        TryKill();
    }

    // Codex keeps its own conversation (resumed by thread id); nothing to rehydrate here.
    public void LoadHistory(IEnumerable<(string Role, string Text)> history)
    {
    }

    public int ClearConversation()
    {
        _threadId = null;
        return 0;
    }

    /// <summary>Codex's own thread id (persisted in the sidecar; restored on reopen).</summary>
    public string? ResumeId
    {
        get => _threadId;
        set => _threadId = value;
    }

    public async Task SubmitAsync(string input, SessionContext context, CancellationToken cancellationToken)
    {
        _cancelled = false;
        _startedItems.Clear();

        var psi = BuildStartInfo(context.ProjectRoot);
        using var proc = new Process { StartInfo = psi };
        _process = proc;

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            await _emit(new ChatEvent(DateTimeOffset.Now, "assistant",
                $"Couldn't launch the `codex` CLI ({ex.Message}). Install Codex (`npm install -g @openai/codex`) and run `codex login`, then try again."));
            _process = null;
            return;
        }

        // Feed the prompt on stdin (the trailing "-" arg) and close it — codex blocks on an open pipe.
        await proc.StandardInput.WriteAsync(input);
        await proc.StandardInput.FlushAsync();
        proc.StandardInput.Close();

        var stderrTask = proc.StandardError.ReadToEndAsync();

        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync()) is not null)
        {
            if (_cancelled || cancellationToken.IsCancellationRequested)
            {
                TryKill();
                break;
            }

            await HandleLineAsync(line);
        }

        try { await proc.WaitForExitAsync(cancellationToken); } catch (OperationCanceledException) { TryKill(); }

        var stderr = await stderrTask;
        if (!_cancelled && !cancellationToken.IsCancellationRequested && proc.HasExited && proc.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? $"codex exited with code {proc.ExitCode}." : stderr.Trim();
            await _emit(new ChatEvent(DateTimeOffset.Now, "assistant", $"Codex error: {detail}"));
        }

        _process = null;
    }

    private async Task HandleLineAsync(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return; // non-JSON noise (rare) — skip
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeEl))
        {
            return;
        }

        switch (typeEl.GetString())
        {
            case "thread.started":
                if (root.TryGetProperty("thread_id", out var tid))
                {
                    _threadId = tid.GetString();
                }

                break;

            case "item.started":
                await HandleItemAsync(root, completed: false);
                break;

            case "item.completed":
                await HandleItemAsync(root, completed: true);
                break;

            case "turn.completed":
                if (root.TryGetProperty("usage", out var usage))
                {
                    // input_tokens already includes cached_input_tokens (OpenAI convention).
                    _cumIn += ReadInt(usage, "input_tokens");
                    _cumOut += ReadInt(usage, "output_tokens");
                }

                break;

            case "turn.failed":
            case "error":
                await _emit(new ChatEvent(DateTimeOffset.Now, "assistant", $"Codex error: {ExtractErrorMessage(root)}"));
                break;
        }
    }

    private async Task HandleItemAsync(JsonElement root, bool completed)
    {
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var itemType = item.TryGetProperty("type", out var t) ? t.GetString() : null;
        var itemId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";

        switch (itemType)
        {
            case "agent_message":
                if (completed && item.TryGetProperty("text", out var textEl) && textEl.GetString() is { Length: > 0 } text)
                {
                    await _emit(new ChatEvent(DateTimeOffset.Now, "assistant", text));
                }

                break;

            case "command_execution":
            {
                var command = item.TryGetProperty("command", out var cmd) ? cmd.GetString() ?? "" : "";
                if (!completed)
                {
                    _startedItems.Add(itemId);
                    await _emit(new ToolCallEvent(DateTimeOffset.Now, "shell", JsonSerializer.Serialize(new { command })));
                    return;
                }

                // Pair every result with a call so the UI's running-tool queue stays balanced.
                if (!_startedItems.Remove(itemId))
                {
                    await _emit(new ToolCallEvent(DateTimeOffset.Now, "shell", JsonSerializer.Serialize(new { command })));
                }

                var exitCode = item.TryGetProperty("exit_code", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : (int?)null;
                var output = item.TryGetProperty("aggregated_output", out var ao) ? ao.GetString() ?? "" : "";
                await _emit(new ToolResultEvent(
                    DateTimeOffset.Now,
                    "shell",
                    $"exit {exitCode?.ToString() ?? "n/a"}",
                    output,
                    exitCode is not (null or 0),
                    0));
                break;
            }

            case "file_change":
            {
                var summary = DescribeChanges(item, out var detail);
                if (!completed)
                {
                    _startedItems.Add(itemId);
                    await _emit(new ToolCallEvent(DateTimeOffset.Now, "apply_patch", detail));
                    return;
                }

                if (!_startedItems.Remove(itemId))
                {
                    await _emit(new ToolCallEvent(DateTimeOffset.Now, "apply_patch", detail));
                }

                await _emit(new ToolResultEvent(DateTimeOffset.Now, "apply_patch", summary, detail, false, 0));
                break;
            }

            // reasoning / unknown item types: intentionally dropped (parity with the Claude backend
            // dropping thinking blocks).
        }
    }

    private static string DescribeChanges(JsonElement item, out string detailJson)
    {
        var count = 0;
        if (item.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
        {
            detailJson = changes.GetRawText();
            count = changes.GetArrayLength();
        }
        else
        {
            detailJson = "[]";
        }

        return count == 1 ? "1 file changed" : $"{count} files changed";
    }

    private static string ExtractErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("error", out var err))
        {
            if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var msg))
            {
                return msg.GetString() ?? "unknown error";
            }

            if (err.ValueKind == JsonValueKind.String)
            {
                return err.GetString() ?? "unknown error";
            }
        }

        return root.TryGetProperty("message", out var m) ? m.GetString() ?? "unknown error" : "unknown error";
    }

    private ProcessStartInfo BuildStartInfo(string workdir)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workdir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        // .cmd shims on Windows must be run through cmd.exe; elsewhere invoke the launcher directly.
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("codex");
        }
        else
        {
            psi.FileName = "codex";
        }

        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--cd");
        psi.ArgumentList.Add(workdir);
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("--skip-git-repo-check");
        psi.ArgumentList.Add("--dangerously-bypass-approvals-and-sandbox");
        if (_threadId is not null)
        {
            // Subcommand comes after the exec flags: codex exec [flags] resume <id> <prompt>.
            psi.ArgumentList.Add("resume");
            psi.ArgumentList.Add(_threadId);
        }

        psi.ArgumentList.Add("-"); // read the prompt from stdin

        var auth = _authProvider?.Invoke() ?? ExternalAgentAuth.Subscription;
        if (auth.UsesApiKey)
        {
            psi.Environment["OPENAI_API_KEY"] = auth.ApiKey!;
        }
        else
        {
            // Force the user's ChatGPT login rather than any inherited (or metered) API key.
            psi.Environment.Remove("OPENAI_API_KEY");
        }

        return psi;
    }

    private void TryKill()
    {
        try { _process?.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private static int ReadInt(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}

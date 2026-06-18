using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AutoCode.Engine.Agent;

namespace AutoCode.Engine.Backends;

/// <summary>
/// Runs the <c>claude</c> CLI (Claude Code) headlessly in the workspace's project root and translates
/// its <c>--output-format stream-json</c> NDJSON into the shared <see cref="AgentEvent"/> stream — so an
/// external agent renders in the UI exactly like the built-in one.
///
/// Auth is the user's **subscription** (`claude login`), not an API key: the child process is spawned
/// with <c>ANTHROPIC_API_KEY</c>/<c>ANTHROPIC_AUTH_TOKEN</c> removed so the CLI falls back to its OAuth
/// session instead of any inherited (or metered) key. Multi-turn continuity uses Claude Code's own
/// session via <c>--resume &lt;session_id&gt;</c>. The CLI runs with <c>--dangerously-skip-permissions</c>
/// because each workspace is isolated in its own git worktree.
/// </summary>
public sealed class ClaudeCodeBackend : IAgentBackend
{
    private readonly Func<AgentEvent, Task> _emit;
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private Process? _process;
    private string? _claudeSessionId;
    private int _cumIn;
    private int _cumOut;
    private volatile bool _cancelled;

    public ClaudeCodeBackend(Func<AgentEvent, Task> emit) => _emit = emit;

    public string Id => "claude-code";

    public string DisplayName => "Claude Code";

    public (int InputTokens, int OutputTokens) CumulativeUsage => (_cumIn, _cumOut);

    public void Cancel()
    {
        _cancelled = true;
        TryKill();
    }

    // Claude Code keeps its own conversation (resumed by session id); nothing to rehydrate here.
    public void LoadHistory(IEnumerable<(string Role, string Text)> history)
    {
    }

    public int ClearConversation()
    {
        _claudeSessionId = null;
        return 0;
    }

    public async Task SubmitAsync(string input, SessionContext context, CancellationToken cancellationToken)
    {
        _cancelled = false;
        _toolNames.Clear();

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
                $"Couldn't launch the `claude` CLI ({ex.Message}). Install Claude Code and run `claude login`, then try again."));
            _process = null;
            return;
        }

        // Feed the prompt on stdin (avoids any command-line escaping of the user's text).
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
            var detail = string.IsNullOrWhiteSpace(stderr) ? $"claude exited with code {proc.ExitCode}." : stderr.Trim();
            await _emit(new ChatEvent(DateTimeOffset.Now, "assistant", $"Claude Code error: {detail}"));
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
            case "system":
                if (root.TryGetProperty("subtype", out var sub) && sub.GetString() == "init"
                    && root.TryGetProperty("session_id", out var sid))
                {
                    _claudeSessionId = sid.GetString();
                }

                break;

            case "assistant":
                await HandleAssistantAsync(root);
                break;

            case "user":
                await HandleToolResultsAsync(root);
                break;

            case "result":
                await HandleResultAsync(root);
                break;
        }
    }

    private async Task HandleAssistantAsync(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg))
        {
            return;
        }

        if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var bt = block.TryGetProperty("type", out var btEl) ? btEl.GetString() : null;
                if (bt == "text" && block.TryGetProperty("text", out var textEl))
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        await _emit(new ChatEvent(DateTimeOffset.Now, "assistant", text));
                    }
                }
                else if (bt == "tool_use")
                {
                    var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "tool" : "tool";
                    var argsJson = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                    if (block.TryGetProperty("id", out var idEl) && idEl.GetString() is { } useId)
                    {
                        _toolNames[useId] = name;
                    }

                    await _emit(new ToolCallEvent(DateTimeOffset.Now, name, argsJson));
                }
            }
        }
    }

    private async Task HandleToolResultsAsync(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)
            || !msg.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var btEl) || btEl.GetString() != "tool_result")
            {
                continue;
            }

            var useId = block.TryGetProperty("tool_use_id", out var u) ? u.GetString() : null;
            var toolName = useId is not null && _toolNames.TryGetValue(useId, out var nm) ? nm : "tool";
            var isError = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            var text = ExtractText(block.TryGetProperty("content", out var c) ? c : default);
            var summary = Summarize(text);

            await _emit(new ToolResultEvent(DateTimeOffset.Now, toolName, summary, text, isError, 0));
        }
    }

    private async Task HandleResultAsync(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var usage))
        {
            _cumIn = ReadInt(usage, "input_tokens") + ReadInt(usage, "cache_read_input_tokens") + ReadInt(usage, "cache_creation_input_tokens");
            _cumOut += ReadInt(usage, "output_tokens");
        }

        var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
        if (isError && root.TryGetProperty("result", out var r))
        {
            await _emit(new ChatEvent(DateTimeOffset.Now, "assistant", $"Claude Code: {r.GetString()}"));
        }
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
            psi.ArgumentList.Add("claude");
        }
        else
        {
            psi.FileName = "claude";
        }

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");
        psi.ArgumentList.Add("--dangerously-skip-permissions");
        if (_claudeSessionId is not null)
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(_claudeSessionId);
        }

        // Force the user's subscription (OAuth) session rather than any inherited / metered API key.
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        psi.Environment.Remove("ANTHROPIC_AUTH_TOKEN");

        return psi;
    }

    private void TryKill()
    {
        try { _process?.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private static int ReadInt(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    /// <summary>Tool-result content is a string or an array of content blocks; flatten to text.</summary>
    private static string ExtractText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("text", out var t))
                {
                    sb.Append(t.GetString());
                }
            }

            return sb.ToString();
        }

        return "";
    }

    private static string Summarize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var firstLine = text.Split('\n', 2)[0].Trim();
        return firstLine.Length > 120 ? firstLine[..120] + "…" : firstLine;
    }
}

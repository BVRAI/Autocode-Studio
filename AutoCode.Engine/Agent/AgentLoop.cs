using System.Diagnostics;
using AutoCode.Engine.Auth;
using AutoCode.Engine.Llm;
using AutoCode.Engine.Session;
using AutoCode.Engine.Tools;

namespace AutoCode.Engine.Agent;

public sealed class AgentLoop
{
    private const int MaxIterations = 200;
    private const int MaxVerifyRounds = 3;
    private const int MaxRetriesPerTool = 3;
    private const int LoopDetectWindow = 10;
    private const int LoopDetectThreshold = 3;
    private const string MaskedToolResultMarker = "[old tool output cleared to save context - re-run the tool if needed]";
    private const int MaskMinChars = 500;
    private const int MaskMinTotalSavings = 2_000;

    private static readonly HashSet<string> MutatingTools = new(StringComparer.Ordinal)
    {
        "edit_file", "write_file", "create_directory", "delete_path", "run_shell"
    };

    private static readonly HashSet<string> FileMutatingTools = new(StringComparer.Ordinal)
    {
        "edit_file", "write_file", "create_directory", "delete_path"
    };

    // First words that make a shell segment read-only. Anything not on this list (or any output
    // redirect) is treated as potentially file-mutating so the verify loop fires. Deliberately
    // conservative — an unnecessary verify run is cheap; a missed mutation escapes the safety net.
    private static readonly HashSet<string> ReadonlyShellHeads = new(StringComparer.Ordinal)
    {
        "ls", "dir", "cat", "type", "head", "tail", "more", "less", "pwd", "cd",
        "echo", "printf", "which", "where", "whoami", "hostname", "date", "env",
        "printenv", "grep", "rg", "findstr", "find", "fd", "wc", "uniq", "du",
        "df", "ps", "tree", "file", "stat", "diff",
    };

    private static readonly HashSet<string> ReadonlyGitSubcommands = new(StringComparer.Ordinal)
    {
        "status", "log", "diff", "show", "branch", "blame", "remote", "describe",
        "rev-parse", "ls-files", "shortlog", "reflog", "grep",
    };

    private readonly AutocodeConfig _config;
    private readonly TranscriptStore _store;
    private readonly LlmRouter _router;
    private readonly ToolRegistry _registry;
    private readonly CheckpointStore _checkpoints;
    private readonly List<AgentMessage> _conversation = [];
    private readonly Func<AgentEvent, Task> _emitAsync;
    private readonly Func<ToolApprovalRequest, CancellationToken, Task<ApprovalDecision>> _approveAsync;
    private readonly Func<string, CancellationToken, Task<bool>> _confirmAsync;
    private readonly Func<AskUserRequest, CancellationToken, Task<IReadOnlyList<int>>> _chooseAsync;
    private bool _cancelled;
    private int _cumIn;
    private int _cumOut;
    private int _lastInputTokens;

    public AgentLoop(
        AutocodeConfig config,
        TranscriptStore store,
        CheckpointStore checkpoints,
        LlmRouter router,
        ToolRegistry registry,
        Func<AgentEvent, Task> emitAsync,
        Func<ToolApprovalRequest, CancellationToken, Task<ApprovalDecision>> approveAsync,
        Func<string, CancellationToken, Task<bool>> confirmAsync,
        Func<AskUserRequest, CancellationToken, Task<IReadOnlyList<int>>> chooseAsync)
    {
        _config = config;
        _store = store;
        _checkpoints = checkpoints;
        _router = router;
        _registry = registry;
        _emitAsync = emitAsync;
        _approveAsync = approveAsync;
        _confirmAsync = confirmAsync;
        _chooseAsync = chooseAsync;
    }

    public (int InputTokens, int OutputTokens) CumulativeUsage => (_cumIn, _cumOut);

    public void Cancel() => _cancelled = true;

    public int ClearConversation()
    {
        var count = _conversation.Count;
        _conversation.Clear();
        return count;
    }

    /// <summary>
    /// Rehydrate the in-memory conversation from a prior transcript (text-only — tool blocks
    /// aren't persisted) so reopening a session continues the thread instead of starting cold.
    /// </summary>
    public void LoadHistory(IEnumerable<(string Role, string Text)> history)
    {
        _conversation.Clear();
        foreach (var (role, text) in history)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            _conversation.Add(string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? AgentMessage.Assistant(new ContentBlock[] { new TextBlock(text) })
                : AgentMessage.User(text));
        }
    }

    public async Task SubmitAsync(string input, SessionContext context, CancellationToken cancellationToken)
    {
        _cancelled = false;
        _checkpoints.BeginTurn();
        // Rebuild the repo map if last turn's edits made it stale. Turn-boundary (not per-edit) so the
        // system prompt stays byte-stable within a turn — the digest is part of the cached prefix.
        ProjectContext.RefreshRepoMapIfStale(context.ProjectRoot);
        _conversation.Add(AgentMessage.User(input));
        _store.AppendTranscript("user", input);
        await EmitAsync(new ChatEvent(DateTimeOffset.Now, "user", input)).ConfigureAwait(false);

        var totals = new TokenTotals();
        var filesChanged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mutated = false;
        for (var round = 0; round <= MaxVerifyRounds; round++)
        {
            var resultMutated = await RunIterationsAsync(context, input, totals, filesChanged, cancellationToken).ConfigureAwait(false);
            mutated = mutated || resultMutated;
            if (_cancelled
                || !mutated
                || context.Mode is AgentMode.Planning or AgentMode.Admin
                || !_config.AutoVerify)
            {
                break;
            }

            var instructions = ProjectInstructions.Load(context.ProjectRoot);
            var plan = Verification.ResolvePlan(context.ProjectRoot, _config.VerifyCommand, instructions, filesChanged.ToList());
            if (plan is null)
            {
                break;
            }

            await EmitAsync(new VerificationEvent(DateTimeOffset.Now, plan.Command, null, "running")).ConfigureAwait(false);
            var verify = await Verification.RunAsync(plan.Command, context.ProjectRoot, cancellationToken).ConfigureAwait(false);
            await EmitAsync(new VerificationEvent(DateTimeOffset.Now, plan.Command, verify.Passed, verify.Output)).ConfigureAwait(false);

            if (!verify.Passed && plan.FullCommand is not null && verify.Output.Contains("No test files found", StringComparison.OrdinalIgnoreCase))
            {
                await EmitAsync(new VerificationEvent(DateTimeOffset.Now, plan.FullCommand, null, "running")).ConfigureAwait(false);
                verify = await Verification.RunAsync(plan.FullCommand, context.ProjectRoot, cancellationToken).ConfigureAwait(false);
                await EmitAsync(new VerificationEvent(DateTimeOffset.Now, plan.FullCommand, verify.Passed, verify.Output)).ConfigureAwait(false);
                plan = plan with { Command = plan.FullCommand, FullCommand = null };
            }

            if (verify.Passed && plan.FullCommand is not null)
            {
                await EmitAsync(new StatusEvent(DateTimeOffset.Now, $"focused tests passed ({plan.Command})")).ConfigureAwait(false);
                await EmitAsync(new VerificationEvent(DateTimeOffset.Now, plan.FullCommand, null, "running")).ConfigureAwait(false);
                var fullRun = await Verification.RunAsync(plan.FullCommand, context.ProjectRoot, cancellationToken).ConfigureAwait(false);
                await EmitAsync(new VerificationEvent(DateTimeOffset.Now, plan.FullCommand, fullRun.Passed, fullRun.Output)).ConfigureAwait(false);
                if (fullRun.Passed)
                {
                    break;
                }

                if (round == MaxVerifyRounds)
                {
                    await EmitAsync(new StatusEvent(DateTimeOffset.Now, $"full suite still failing after {MaxVerifyRounds} fix attempts")).ConfigureAwait(false);
                    break;
                }

                _conversation.Add(AgentMessage.User(
                    $"The focused tests for your changed files pass, but the full suite `{plan.FullCommand}` fails (exit {fullRun.ExitCode?.ToString() ?? "?"}) - likely a regression elsewhere caused by your changes:\n\n```\n{fullRun.Output}\n```\n\nFix the regressions, then stop. If these failures are pre-existing and unrelated to your changes, say so briefly and stop."));
                continue;
            }

            if (verify.Passed)
            {
                break;
            }

            if (round == MaxVerifyRounds)
            {
                await EmitAsync(new StatusEvent(DateTimeOffset.Now, $"verification still failing after {MaxVerifyRounds} fix attempts")).ConfigureAwait(false);
                break;
            }

            _conversation.Add(AgentMessage.User(
                $"The verification command `{plan.Command}` failed (exit {verify.ExitCode?.ToString() ?? "?"}) after your changes:\n\n```\n{verify.Output}\n```\n\nFix the failures, then stop. If failures are pre-existing and unrelated, say so briefly and stop."));
        }

        var usageLine = $"in: {totals.Input}, out: {totals.Output}";
        await EmitAsync(new StatusEvent(DateTimeOffset.Now, usageLine)).ConfigureAwait(false);
        _store.Touch(null);
    }

    private async Task<bool> RunIterationsAsync(
        SessionContext context,
        string userText,
        TokenTotals totals,
        HashSet<string> filesChanged,
        CancellationToken cancellationToken)
    {
        var mutated = false;
        var consecutiveFailures = new Dictionary<string, int>(StringComparer.Ordinal);
        var recentToolSigs = new List<string>();
        var maxIterations = context.MaxIterations is > 0 ? context.MaxIterations.Value : MaxIterations;
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_cancelled)
            {
                TodoWriteTool.MarkInProgressInterrupted(context.SessionId);
                await EmitPlanAsync(context).ConfigureAwait(false);
                _conversation.Add(AgentMessage.User("[user cancelled the task]"));
                await EmitAsync(new StatusEvent(DateTimeOffset.Now, "cancelled")).ConfigureAwait(false);
                return mutated;
            }

            // Two-tier context management: cheaply clear old tool outputs before paying
            // for a full summarizing compaction.
            if (ContextWindow.ShouldAutoCompact(_lastInputTokens, context.Model.Provider, context.Model.Model))
            {
                await EmitAsync(new StatusEvent(DateTimeOffset.Now, "compacting context")).ConfigureAwait(false);
                await CompactConversationAsync(context, cancellationToken).ConfigureAwait(false);
                _lastInputTokens = 0;
            }
            else if (ContextWindow.ShouldMaskObservations(_lastInputTokens, context.Model.Provider, context.Model.Model))
            {
                var masked = MaskOldToolResults(_conversation);
                if (masked > 0)
                {
                    await EmitAsync(new StatusEvent(DateTimeOffset.Now, $"cleared {masked} old tool outputs to save context")).ConfigureAwait(false);
                }
            }

            // Cost-budget backstop: stop before paying for another call once the turn's spend
            // crosses the ceiling. The model is deliberately NOT told (a generous budget lets
            // honest work finish first; a visible shrinking budget provokes rushed attempts).
            if (context.MaxCostUsd is > 0)
            {
                var spent = Pricing.EstimateCost(
                    new CompletionUsage(totals.Input, totals.Output, totals.CacheRead, totals.CacheWrite),
                    context.Model.Provider, context.Model.Model);
                if (spent > context.MaxCostUsd.Value)
                {
                    await EmitAsync(new StatusEvent(DateTimeOffset.Now,
                        $"stopped — turn cost ${spent:0.00} reached the ${context.MaxCostUsd.Value:0.00} budget")).ConfigureAwait(false);
                    return mutated;
                }
            }

            _store.Touch(userText[..Math.Min(userText.Length, 80)]);
            await EmitAsync(new StatusEvent(DateTimeOffset.Now, "thinking")).ConfigureAwait(false);
            var prompt = PromptBuilder.Build(context, _registry.Names);
            var response = await _router.CompleteAsync(
                context.Model.Provider,
                new CompletionRequest(
                    context.Model.Model,
                    prompt.System,
                    prompt.SystemVolatile,
                    _conversation,
                    _registry.Schemas(),
                    // Providers default to 8192 output tokens — too small for large single-file writes.
                    MaxTokens: ContextWindow.DefaultMaxOutputTokens(context.Model.Model),
                    Temperature: context.Temperature ?? 1.0,
                    // Extended thinking when the model supports it (kill switch: AUTOCODE_NO_THINKING=1).
                    Thinking: ModelCatalog.ThinkingFor(context.Model.Provider, context.Model.Model),
                    // Server-side context editing at 50% of the window — below the client mask tier (60%),
                    // so the cache-preserving server path clears first. Ignored by providers without support.
                    ContextEditing: new ContextEditingConfig((int)(ContextWindow.ContextWindowFor(context.Model.Provider, context.Model.Model) * 0.5))),
                cancellationToken).ConfigureAwait(false);

            totals.Input += response.Usage.InputTokens;
            totals.Output += response.Usage.OutputTokens;
            totals.CacheRead += response.Usage.CacheReadTokens;
            totals.CacheWrite += response.Usage.CacheWriteTokens;
            _cumIn += response.Usage.InputTokens;
            _cumOut += response.Usage.OutputTokens;
            _lastInputTokens = response.Usage.InputTokens;

            _conversation.Add(AgentMessage.Assistant(response.Content));
            var assistantText = string.Join("\n", response.Content.OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
            if (!string.IsNullOrWhiteSpace(assistantText))
            {
                _store.AppendTranscript("assistant", assistantText);
                await EmitAsync(new ChatEvent(DateTimeOffset.Now, "assistant", assistantText)).ConfigureAwait(false);
            }

            var toolUses = response.Content.OfType<ToolUseBlock>().ToList();
            if (toolUses.Count == 0 || response.StopReason == "end_turn")
            {
                return mutated;
            }

            var toolResults = new List<ContentBlock>();
            var toolContext = new ToolExecutionContext
            {
                Session = context,
                ConfirmAsync = _confirmAsync,
                ChooseAsync = _chooseAsync,
                Checkpoint = _checkpoints
            };

            foreach (var toolUse in toolUses)
            {
                recentToolSigs.Add($"{toolUse.Name}:{StableStringify(toolUse.Input)}");
                if (recentToolSigs.Count > LoopDetectWindow)
                {
                    recentToolSigs.RemoveAt(0);
                }

                await EmitAsync(new ToolCallEvent(DateTimeOffset.Now, toolUse.Name, ToolArgs.Json(toolUse.Input))).ConfigureAwait(false);
                var gate = GateFor(context.Mode, toolUse.Name);
                if (gate == GateDecision.Block)
                {
                    var content = "Planning mode is active. File edits and shell commands are disabled; produce a clear plan instead.";
                    toolResults.Add(new ToolResultBlock(toolUse.Id, content, true));
                    await EmitAsync(new ToolResultEvent(DateTimeOffset.Now, toolUse.Name, "blocked (planning mode)", content, true, 0)).ConfigureAwait(false);
                    continue;
                }

                if (gate == GateDecision.Approve)
                {
                    var preview = FormatToolPreview(toolUse.Name, toolUse.Input);
                    var verdict = await _approveAsync(new ToolApprovalRequest(toolUse.Name, toolUse.Input, preview), cancellationToken).ConfigureAwait(false);
                    if (verdict.Decision != ApprovalDecisionKind.Accept)
                    {
                        var content = verdict.Decision == ApprovalDecisionKind.Revise
                            ? $"User declined this tool call and asks you to revise: {verdict.Guidance ?? "(no guidance)"}"
                            : "User declined this tool call. Adapt your plan.";
                        toolResults.Add(new ToolResultBlock(toolUse.Id, content, true));
                        await EmitAsync(new ToolResultEvent(DateTimeOffset.Now, toolUse.Name, verdict.Decision.ToString().ToLowerInvariant(), content, true, 0)).ConfigureAwait(false);
                        continue;
                    }
                }

                var sw = Stopwatch.StartNew();
                _checkpoints.BeginStep();
                var result = await _registry.ExecuteAsync(toolUse.Name, toolUse.Input, toolContext, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                if (result.IsError)
                {
                    consecutiveFailures[toolUse.Name] = consecutiveFailures.TryGetValue(toolUse.Name, out var count) ? count + 1 : 1;
                }
                else
                {
                    consecutiveFailures[toolUse.Name] = 0;
                    if (FileMutatingTools.Contains(toolUse.Name))
                    {
                        mutated = true;
                        ProjectContext.InvalidateRepoMap(context.ProjectRoot);
                        foreach (var p in PathsTouched(toolUse.Input))
                        {
                            filesChanged.Add(p);
                        }
                    }
                    else if (toolUse.Name == "run_shell")
                    {
                        // Shell commands can change files too (sed -i, codegen, mv, npm install …) — those
                        // edits must not escape the verify loop. We can't know which files changed, so mark
                        // the turn mutated unless the command is conservatively read-only. A false positive
                        // just runs verify once; a false negative skips the safety net entirely.
                        var cmd = ToolArgs.OptionalString(toolUse.Input, "command") ?? "";
                        if (!IsReadOnlyShellCommand(cmd))
                        {
                            mutated = true;
                            ProjectContext.InvalidateRepoMap(context.ProjectRoot);
                        }
                    }
                }

                _store.AppendToolLog(new ToolLogEntry(
                    DateTimeOffset.Now,
                    toolUse.Name,
                    ToolArgs.Json(toolUse.Input),
                    result.IsError ? "error" : "success",
                    sw.ElapsedMilliseconds,
                    result.Summary,
                    result.IsError ? result.Content[..Math.Min(result.Content.Length, 500)] : null));

                await EmitAsync(new ToolResultEvent(DateTimeOffset.Now, toolUse.Name, result.Summary, result.Content, result.IsError, sw.ElapsedMilliseconds)).ConfigureAwait(false);
                toolResults.Add(new ToolResultBlock(toolUse.Id, WrapExternalResult(toolUse.Name, toolUse.Input, result), result.IsError));

                if (toolUse.Name == "todo_write" && !result.IsError)
                {
                    await EmitPlanAsync(context).ConfigureAwait(false);
                }
            }

            // Harness advisories (loop detection, retry caps) ride in a SEPARATE follow-up user
            // message as plain text — NOT as tool_result blocks. A tool_result whose id has no
            // matching tool_use is an API error on Anthropic ("unexpected tool_use_id"), and other
            // providers translate tool_results into role:"tool" messages that must reference a real
            // call id. Carried as a blocks message (not string) so it isn't counted as a plain user
            // turn by the compaction cut.
            var advisories = new List<string>();
            var loopOffender = DetectLoop(recentToolSigs, LoopDetectThreshold);
            if (loopOffender is not null)
            {
                advisories.Add(
                    $"[harness advisory] You have called `{loopOffender}` with the same (or very similar) arguments {LoopDetectThreshold}+ times recently. " +
                    "Stop and reflect: the previous calls likely already gave you the information you need, or the approach is wrong. " +
                    "Summarize what you have learned and propose a different next step. Do not call this tool with these arguments again.");
                recentToolSigs.Clear();
            }

            foreach (var (tool, count) in consecutiveFailures.ToList())
            {
                if (count >= MaxRetriesPerTool)
                {
                    advisories.Add(
                        $"[harness advisory] `{tool}` has failed {count} times in a row. Stop retrying. " +
                        "Summarize what went wrong and ask the user for guidance, or try a fundamentally different approach.");
                    consecutiveFailures[tool] = 0;
                }
            }

            _conversation.Add(AgentMessage.User(toolResults));
            if (advisories.Count > 0)
            {
                _conversation.Add(AgentMessage.User(new List<ContentBlock> { new TextBlock(string.Join("\n\n", advisories)) }));
            }
        }

        await EmitAsync(new StatusEvent(DateTimeOffset.Now, $"stopped after {maxIterations} iterations")).ConfigureAwait(false);
        return mutated;
    }

    private async Task CompactConversationAsync(SessionContext context, CancellationToken cancellationToken)
    {
        const int keepPairs = 4;
        var cut = FindCompactionCut(_conversation, keepPairs);
        if (cut <= 0)
        {
            return;
        }

        var older = _conversation.GetRange(0, cut);
        var kept = _conversation.GetRange(cut, _conversation.Count - cut);

        string? summary = null;
        try
        {
            summary = await SummarizeMessagesAsync(older, context, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            summary = null; // fall back to plain truncation
        }

        _conversation.Clear();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            _conversation.Add(AgentMessage.User($"[Summary of earlier conversation]\n{summary}"));
        }

        _conversation.AddRange(kept);
    }

    private async Task<string> SummarizeMessagesAsync(List<AgentMessage> messages, SessionContext context, CancellationToken cancellationToken)
    {
        var transcript = string.Join("\n\n", messages.Select(RenderForSummary));
        var response = await _router.CompleteAsync(
            context.Model.Provider,
            new CompletionRequest(
                // Summarization doesn't need the flagship session model — use the provider's cheap
                // tier (falls back to the session model when the provider has no cheaper option).
                ModelCatalog.SummarizerModelFor(context.Model.Provider, context.Model.Model),
                "You compress coding-assistant conversations. Produce a concise but complete summary that preserves: what the user asked for, key decisions, files created or modified, important findings, and any unfinished work. Use compact bullet points.",
                null,
                [AgentMessage.User($"Summarize this conversation excerpt:\n\n{transcript}")],
                []),
            cancellationToken).ConfigureAwait(false);
        _cumIn += response.Usage.InputTokens;
        _cumOut += response.Usage.OutputTokens;
        var text = string.Join("\n", response.Content.OfType<TextBlock>().Select(b => b.Text)).Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidOperationException("empty summary");
        }

        return text;
    }

    // Index before which messages are summarized: everything before the keepPairs-th most
    // recent plain (string-content) user turn. Returns 0 when there is nothing to compact.
    private static int FindCompactionCut(List<AgentMessage> conversation, int keepPairs)
    {
        var userSeen = 0;
        for (var i = conversation.Count - 1; i >= 0; i--)
        {
            var m = conversation[i];
            if (m.Role == "user" && m.Text is not null)
            {
                userSeen++;
                if (userSeen == keepPairs)
                {
                    return i;
                }
            }
        }

        return 0;
    }

    private static string RenderForSummary(AgentMessage m)
    {
        if (m.Text is not null)
        {
            return $"{m.Role}: {m.Text}";
        }

        var parts = new List<string>();
        foreach (var b in m.Blocks ?? [])
        {
            switch (b)
            {
                case TextBlock t:
                    parts.Add(t.Text);
                    break;
                case ToolUseBlock u:
                    parts.Add($"[tool_use {u.Name} {Truncate(ToolArgs.Json(u.Input), 200)}]");
                    break;
                case ToolResultBlock r:
                    parts.Add($"[tool_result {Truncate(r.Content, 200)}]");
                    break;
                case ThinkingBlock:
                    parts.Add("[thinking]");
                    break;
            }
        }

        return $"{m.Role}: {string.Join(' ', parts)}";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    internal static string? DetectLoop(List<string> window, int threshold)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in window)
        {
            counts[s] = counts.TryGetValue(s, out var n) ? n + 1 : 1;
        }

        foreach (var (sig, n) in counts)
        {
            if (n >= threshold)
            {
                var colon = sig.IndexOf(':');
                return colon >= 0 ? sig[..colon] : sig;
            }
        }

        return null;
    }

    internal static string StableStringify(Dictionary<string, object?> input)
    {
        try
        {
            var norm = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in input.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var v = input[key];
                norm[key] = v is string s ? System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim() : v;
            }

            return ToolArgs.Json(norm);
        }
        catch
        {
            return ToolArgs.Json(input);
        }
    }

    // True when every segment of the command (split on |, ||, &&, ;) starts with a known read-only
    // program and there is no output redirect anywhere. Mirrors the TS isReadOnlyShellCommand.
    internal static bool IsReadOnlyShellCommand(string command)
    {
        var cmd = command.Trim();
        if (cmd.Length == 0)
        {
            return true;
        }

        if (cmd.Contains('>'))
        {
            return false; // any redirect can write a file
        }

        foreach (var segment in System.Text.RegularExpressions.Regex.Split(cmd, @"\|\||&&|;|\|"))
        {
            var words = segment.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                continue;
            }

            var head = words[0].ToLowerInvariant();
            if (head == "git")
            {
                var sub = words.Length > 1 ? words[1].ToLowerInvariant() : "";
                if (!ReadonlyGitSubcommands.Contains(sub))
                {
                    return false;
                }

                continue;
            }

            if (!ReadonlyShellHeads.Contains(head))
            {
                return false;
            }
        }

        return true;
    }

    internal static int MaskOldToolResults(List<AgentMessage> conversation, int keepPairs = 2)
    {
        var cut = FindCompactionCut(conversation, keepPairs);
        if (cut <= 0)
        {
            return 0;
        }

        var candidates = new List<(List<ContentBlock> Blocks, int Index, ToolResultBlock Result)>();
        var savings = 0;
        for (var i = 0; i < cut; i++)
        {
            var message = conversation[i];
            if (message.Role != "user" || message.Blocks is null)
            {
                continue;
            }

            for (var j = 0; j < message.Blocks.Count; j++)
            {
                if (message.Blocks[j] is not ToolResultBlock result)
                {
                    continue;
                }

                if (result.Content.Length <= MaskMinChars || result.Content == MaskedToolResultMarker)
                {
                    continue;
                }

                candidates.Add((message.Blocks, j, result));
                savings += result.Content.Length - MaskedToolResultMarker.Length;
            }
        }

        if (savings < MaskMinTotalSavings)
        {
            return 0;
        }

        foreach (var (blocks, index, result) in candidates)
        {
            blocks[index] = new ToolResultBlock(result.ToolUseId, MaskedToolResultMarker, result.IsError);
        }

        return candidates.Count;
    }

    private Task EmitAsync(AgentEvent evt) => _emitAsync(evt);

    // Push the session's current todo checklist to the UI (used after each todo_write and on cancel).
    private Task EmitPlanAsync(SessionContext context)
    {
        var items = TodoWriteTool.CurrentTodos(context.SessionId)
            .Select(t => new PlanItem(t.Id, t.Text, t.Status))
            .ToList();
        return EmitAsync(new PlanEvent(DateTimeOffset.Now, items));
    }

    private GateDecision GateFor(AgentMode mode, string toolName)
    {
        // Mutating = the hardcoded built-in set, plus any registered tool that declared itself
        // mutating via ToolDefinition.Mutating (e.g. host-injected orchestration tools).
        if (!MutatingTools.Contains(toolName) && !_registry.IsMutating(toolName))
        {
            return GateDecision.Allow;
        }

        return mode switch
        {
            AgentMode.Planning => GateDecision.Block,
            AgentMode.Default => GateDecision.Approve,
            AgentMode.Autocode => GateDecision.Allow,
            AgentMode.Admin => GateDecision.Allow,
            _ => GateDecision.Approve
        };
    }

    private static string FormatToolPreview(string toolName, Dictionary<string, object?> input)
    {
        if (toolName == "edit_file")
        {
            return $"{ToolArgs.OptionalString(input, "path") ?? "?"}\n--- old_text ---\n{ToolArgs.OptionalString(input, "old_text")}\n--- new_text ---\n{ToolArgs.OptionalString(input, "new_text")}";
        }

        if (toolName == "write_file")
        {
            var content = ToolArgs.OptionalString(input, "content") ?? "";
            var preview = string.Join("\n", content.Split(["\r\n", "\n"], StringSplitOptions.None).Take(12));
            return $"{ToolArgs.OptionalString(input, "path") ?? "?"} ({ToolArgs.OptionalString(input, "mode") ?? "create_only"}, {content.Length} chars)\n{preview}";
        }

        if (toolName == "run_shell")
        {
            return "$ " + (ToolArgs.OptionalString(input, "command") ?? "?");
        }

        if (toolName == "delete_path")
        {
            var paths = ToolArgs.StringList(input, "paths");
            var single = ToolArgs.OptionalString(input, "path");
            if (!string.IsNullOrWhiteSpace(single))
            {
                paths.Add(single);
            }

            return "delete (to trash): " + string.Join(", ", paths);
        }

        return ToolArgs.Json(input);
    }

    private static IEnumerable<string> PathsTouched(Dictionary<string, object?> input)
    {
        var path = ToolArgs.OptionalString(input, "path");
        if (!string.IsNullOrWhiteSpace(path))
        {
            yield return path;
        }

        foreach (var p in ToolArgs.StringList(input, "paths"))
        {
            yield return p;
        }
    }

    private static string WrapExternalResult(string toolName, Dictionary<string, object?> input, ToolResult result)
    {
        if (result.IsError || toolName is not ("web_fetch" or "web_search"))
        {
            return result.Content;
        }

        var source = ToolArgs.OptionalString(input, "url") ?? ToolArgs.OptionalString(input, "query") ?? "";
        return $"<external_untrusted_content tool=\"{toolName}\" source=\"{source}\">\n{result.Content}\n</external_untrusted_content>";
    }

    private enum GateDecision
    {
        Block,
        Approve,
        Allow
    }

    private sealed class TokenTotals
    {
        public int Input { get; set; }
        public int Output { get; set; }
        public int CacheRead { get; set; }
        public int CacheWrite { get; set; }
    }
}

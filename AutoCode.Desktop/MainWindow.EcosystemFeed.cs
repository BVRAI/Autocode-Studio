using System.Text.Json;
using System.Windows.Threading;
using AutoCode.Desktop.Misc;
using AutoCode.Desktop.ViewModels;
using AutoCode.Engine.Agent;

namespace AutoCode.Desktop;

// Ecosystem feed (design spec §2): a member session's agent events tee into the open ecosystem chat.
// Routine turn activity folds into one MemberTurnCardBlock per member turn (live "working · Ns"
// status, newest-3 steps, auto-collapse on finish); milestone reports and errors break out as
// standalone blocks; queued dispatches show as ghost rows that swap into a card when the turn
// starts. Reports (report_to_ecosystem tool for builtin members, "ECOSYSTEM_REPORT:" lines for CLI
// members) additionally persist to the manifest repo's reports.md — even when the chat is closed.
// Everything here runs on the Dispatcher (called from EmitAsync / the submit path).
public partial class MainWindow
{
    private const string ReportMarker = "ECOSYSTEM_REPORT:";
    private DispatcherTimer? _feedTimer;

    /// <summary>The live ecosystem chat hosting the given ecosystem, if one is open.</summary>
    private WorkspaceSession? FindEcosystemChat(string ecosystemId)
        => _vm.Sessions.Sessions.FirstOrDefault(
            s => s.Kind == WorkspaceSession.EcosystemKind && s.EcosystemId == ecosystemId);

    /// <summary>The ecosystem a member session belongs to (null for ecosystem chats / unaffiliated roots).
    /// Maps via the real ProjectRoot, never Context.ProjectRoot (a worktree path won't match a member root).</summary>
    private EcosystemRecord? EcosystemForMember(WorkspaceSession source)
    {
        if (source.Kind == WorkspaceSession.EcosystemKind || string.IsNullOrEmpty(source.ProjectRoot))
        {
            return null;
        }

        return _ecosystemByRoot.TryGetValue(EcosystemIndex.NormalizeRoot(source.ProjectRoot), out var eco) ? eco : null;
    }

    /// <summary>Tee one member event: persist reports (always), fold activity into the member's turn
    /// card / standalone blocks in the ecosystem chat's feed (only if that chat is open).</summary>
    private void TeeToEcosystemFeed(WorkspaceSession source, AgentEvent evt)
    {
        var eco = EcosystemForMember(source);
        if (eco is null)
        {
            return;
        }

        var member = MemberDisplayName(source);
        var chat = FindEcosystemChat(eco.Id);

        switch (evt)
        {
            case ChatEvent { Role: "user" }:
                return;

            case ChatEvent chatEvt when !string.IsNullOrWhiteSpace(chatEvt.Text):
                var (reports, rest) = SplitReports(chatEvt.Text);
                foreach (var message in reports)
                {
                    _ = EcosystemManifestService.AppendReportAsync(eco, member, message);
                    AddFeedBlock(chat, ReportBlock(member, message));
                }

                if (!string.IsNullOrWhiteSpace(rest) && chat is not null)
                {
                    TurnCardFor(chat, member).AddStep(new MemberTurnStep { IsMessage = true, Text = Condense(rest, 200) });
                }

                break;

            case ToolCallEvent { ToolName: "report_to_ecosystem" } reportCall:
                var reported = ReportMessage(reportCall.ArgumentsJson);
                _ = EcosystemManifestService.AppendReportAsync(eco, member, reported);
                AddFeedBlock(chat, ReportBlock(member, reported));
                break;

            case ToolCallEvent call when chat is not null:
                TurnCardFor(chat, member).AddStep(new MemberTurnStep
                {
                    GlyphKey = ToolGlyph(call.ToolName),
                    ToolName = call.ToolName,
                    Detail = Condense(call.ArgumentsJson, 120),
                });
                break;

            case ToolResultEvent { IsError: true } err:
                AddFeedBlock(chat, new MemberActivityBlock
                {
                    Member = member,
                    Kind = "error",
                    Summary = Loc.F("Feed_ErrorIn", err.ToolName),
                    Detail = Condense(err.Summary, 200),
                    TimeText = DateTimeOffset.Now.ToString("HH:mm"),
                });
                break;

            case VerificationEvent verification when chat is not null:
                TurnCardFor(chat, member).AddStep(new MemberTurnStep
                {
                    GlyphKey = "IconCheck",
                    ToolName = "verified",
                    Detail = verification.Passed == true ? "passed" : "failed",
                });
                break;
        }

        if (chat is not null && IsActiveSession(chat))
        {
            ScrollChatToEnd();
        }
    }

    /// <summary>Turn boundaries drive the card lifecycle: start = create the card (swapping out this
    /// member's queued ghost, if showing); finish = freeze the status and auto-collapse.</summary>
    private void NoteMemberTurnBoundary(WorkspaceSession source, bool starting)
    {
        var eco = EcosystemForMember(source);
        var chat = eco is null ? null : FindEcosystemChat(eco.Id);
        if (chat is null)
        {
            return;
        }

        var member = MemberDisplayName(source);
        if (starting)
        {
            var ghost = chat.Conversation.OfType<MemberActivityBlock>()
                .LastOrDefault(b => b.Kind == "queued" && b.Member.Equals(member, StringComparison.OrdinalIgnoreCase));
            if (ghost is not null)
            {
                chat.Conversation.Remove(ghost);
            }

            TurnCardFor(chat, member);
        }
        else if (chat.RunningTurnCards.TryGetValue(member, out var card))
        {
            var seconds = (int)(DateTimeOffset.Now - card.StartedAt).TotalSeconds;
            card.StatusText = Loc.F("Feed_Finished", seconds, card.Steps.Count);
            card.IsRunning = false;
            card.IsExpanded = false;
            chat.RunningTurnCards.Remove(member);
        }

        if (IsActiveSession(chat))
        {
            ScrollChatToEnd();
        }
    }

    /// <summary>The member's running turn card, created on demand (e.g. the chat opened mid-turn).</summary>
    private MemberTurnCardBlock TurnCardFor(WorkspaceSession chat, string member)
    {
        if (chat.RunningTurnCards.TryGetValue(member, out var card))
        {
            return card;
        }

        card = new MemberTurnCardBlock { Member = member, StartedAt = DateTimeOffset.Now };
        card.StatusText = Loc.F("Feed_Working", 0);
        chat.RunningTurnCards[member] = card;
        chat.Conversation.Add(card);
        EnsureFeedTimer();
        return card;
    }

    /// <summary>One shared 1s ticker keeps every running card's "working · Ns" live; it stops itself
    /// on the first tick with no running cards anywhere.</summary>
    private void EnsureFeedTimer()
    {
        if (_feedTimer is not null)
        {
            return;
        }

        _feedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _feedTimer.Tick += (_, _) =>
        {
            var any = false;
            foreach (var chat in _vm.Sessions.Sessions.Where(s => s.Kind == WorkspaceSession.EcosystemKind))
            {
                foreach (var card in chat.RunningTurnCards.Values)
                {
                    card.StatusText = Loc.F("Feed_Working", (int)(DateTimeOffset.Now - card.StartedAt).TotalSeconds);
                    any = true;
                }
            }

            if (!any && _feedTimer is not null)
            {
                _feedTimer.Stop();
                _feedTimer = null;
            }
        };
        _feedTimer.Start();
    }

    private static void AddFeedBlock(WorkspaceSession? chat, MemberActivityBlock block)
        => chat?.Conversation.Add(block);

    private static MemberActivityBlock ReportBlock(string member, string message) => new()
    {
        Member = member,
        Kind = "report",
        Summary = "reported",
        Detail = message,
        TimeText = DateTimeOffset.Now.ToString("HH:mm"),
    };

    /// <summary>Split assistant text into ECOSYSTEM_REPORT lines (CLI members' reporting channel)
    /// and the remaining prose.</summary>
    private static (List<string> Reports, string Remainder) SplitReports(string text)
    {
        var reports = new List<string>();
        var remaining = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(ReportMarker, StringComparison.OrdinalIgnoreCase))
            {
                var message = trimmed[ReportMarker.Length..].Trim();
                if (message.Length > 0)
                {
                    reports.Add(message);
                }
            }
            else
            {
                remaining.Add(line);
            }
        }

        return (reports, string.Join('\n', remaining));
    }

    /// <summary>Extract the message from a report_to_ecosystem tool call's raw JSON arguments.</summary>
    private static string ReportMessage(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString()!.Trim();
            }
        }
        catch
        {
            // Malformed arguments — fall back to the condensed raw JSON below.
        }

        return Condense(argumentsJson, 200);
    }

    /// <summary>Icons.xaml geometry key for a tool line (design spec §2 tool-glyph table).</summary>
    private static string ToolGlyph(string toolName) => toolName switch
    {
        "edit_file" or "write_file" or "read_file" or "create_directory" or "delete_path" => "IconFile",
        "run_shell" => "IconTerminal",
        "grep" or "glob" or "find_symbol" or "file_deps" or "list_directory" => "IconSearch",
        "web_fetch" or "web_search" => "IconGlobe",
        _ => "IconCode",
    };

    /// <summary>Feed attribution = the member's handle (leaf folder name) — the same identity used
    /// by @mentions and dispatch, and stable across title changes (chips never truncate).</summary>
    private static string MemberDisplayName(WorkspaceSession source)
        => LeafName(source.ProjectRoot);

    private static string Condense(string text, int max)
    {
        var s = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}

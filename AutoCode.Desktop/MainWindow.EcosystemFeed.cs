using System.Text.Json;
using AutoCode.Desktop.Misc;
using AutoCode.Desktop.ViewModels;
using AutoCode.Engine.Agent;

namespace AutoCode.Desktop;

// Ecosystem feed: tee a member session's agent events into the open ecosystem chat as attributed
// activity blocks. Milestone reports (the report_to_ecosystem tool for the builtin agent, or
// "ECOSYSTEM_REPORT:" output lines for CLI agents) additionally persist to the manifest repo's
// reports.md — even when the ecosystem chat is closed. Every method here runs on the Dispatcher
// (called from EmitAsync / the submit path), so reading _ecosystemByRoot and mutating the ecosystem
// chat's Conversation is UI-thread-safe. The feed itself is live-only (persistence = backlog).
public partial class MainWindow
{
    private const string ReportMarker = "ECOSYSTEM_REPORT:";

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

    /// <summary>Tee one member event: persist any milestone reports to reports.md (always), and mirror
    /// the activity into the ecosystem chat's feed (only if that chat is open).</summary>
    private void TeeToEcosystemFeed(WorkspaceSession source, AgentEvent evt)
    {
        var eco = EcosystemForMember(source);
        if (eco is null)
        {
            return;
        }

        var blocks = MakeFeedBlocks(source, evt);
        if (blocks.Count == 0)
        {
            return;
        }

        foreach (var report in blocks.Where(b => b.Kind == "report"))
        {
            _ = EcosystemManifestService.AppendReportAsync(eco, report.Member, report.Detail ?? "");
        }

        var chat = FindEcosystemChat(eco.Id);
        if (chat is null)
        {
            return;
        }

        foreach (var block in blocks)
        {
            chat.Conversation.Add(block);
        }

        if (IsActiveSession(chat))
        {
            ScrollChatToEnd();
        }
    }

    /// <summary>A member turn boundary ("started"/"finished") as a feed entry.</summary>
    private void NoteMemberTurnBoundary(WorkspaceSession source, bool starting)
    {
        var eco = EcosystemForMember(source);
        var chat = eco is null ? null : FindEcosystemChat(eco.Id);
        if (chat is null)
        {
            return;
        }

        chat.Conversation.Add(new MemberActivityBlock
        {
            Member = MemberDisplayName(source),
            Summary = starting ? "started a turn" : "finished a turn",
            Kind = starting ? "start" : "finish",
        });
        if (IsActiveSession(chat))
        {
            ScrollChatToEnd();
        }
    }

    /// <summary>Translate a member's agent event into attributed feed blocks (rich). Empty = don't surface.</summary>
    private List<MemberActivityBlock> MakeFeedBlocks(WorkspaceSession source, AgentEvent evt)
    {
        var member = MemberDisplayName(source);
        return evt switch
        {
            ChatEvent { Role: "user" } => [],
            ChatEvent chat when !string.IsNullOrWhiteSpace(chat.Text) => ChatBlocks(member, chat.Text),
            ToolCallEvent { ToolName: "report_to_ecosystem" } report
                => [new MemberActivityBlock { Member = member, Kind = "report", Summary = "reported", Detail = ReportMessage(report.ArgumentsJson) }],
            ToolCallEvent call
                => [new MemberActivityBlock { Member = member, Kind = "tool", Summary = $"ran {call.ToolName}", Detail = Condense(call.ArgumentsJson, 120) }],
            ToolResultEvent { IsError: true } err
                => [new MemberActivityBlock { Member = member, Kind = "error", Summary = $"error in {err.ToolName}", Detail = Condense(err.Summary, 200) }],
            VerificationEvent verification
                => [new MemberActivityBlock { Member = member, Kind = "tool", Summary = "verified", Detail = verification.Passed == true ? "passed" : "failed" }],
            _ => [],
        };
    }

    /// <summary>Assistant text → feed blocks: lines starting with the report marker become report rows
    /// (CLI agents' reporting channel); whatever remains renders as one condensed "said" row.</summary>
    private static List<MemberActivityBlock> ChatBlocks(string member, string text)
    {
        var blocks = new List<MemberActivityBlock>();
        var remaining = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(ReportMarker, StringComparison.OrdinalIgnoreCase))
            {
                var message = trimmed[ReportMarker.Length..].Trim();
                if (message.Length > 0)
                {
                    blocks.Add(new MemberActivityBlock { Member = member, Kind = "report", Summary = "reported", Detail = message });
                }
            }
            else
            {
                remaining.Add(line);
            }
        }

        var rest = string.Join('\n', remaining);
        if (!string.IsNullOrWhiteSpace(rest))
        {
            blocks.Add(new MemberActivityBlock { Member = member, Kind = "message", Summary = "said", Detail = Condense(rest, 200) });
        }

        return blocks;
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

    private string MemberDisplayName(WorkspaceSession source)
        => !string.IsNullOrWhiteSpace(source.ChatTitle) && source.ChatTitle != "New session"
            ? source.ChatTitle
            : LeafName(source.ProjectRoot);

    private static string Condense(string text, int max)
    {
        var s = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}

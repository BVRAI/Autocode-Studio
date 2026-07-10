using AutoCode.Desktop.Misc;
using AutoCode.Desktop.ViewModels;
using AutoCode.Engine.Agent;

namespace AutoCode.Desktop;

// Ecosystem feed: tee a member session's agent events into the open ecosystem chat as attributed
// activity blocks. Every method here runs on the Dispatcher (called from EmitAsync / the submit path),
// so reading _ecosystemByRoot and mutating the ecosystem chat's Conversation is UI-thread-safe.
// Live-only: activity before the ecosystem chat is opened isn't backfilled (feed persistence = backlog).
public partial class MainWindow
{
    /// <summary>The live ecosystem chat hosting the given ecosystem, if one is open.</summary>
    private WorkspaceSession? FindEcosystemChat(string ecosystemId)
        => _vm.Sessions.Sessions.FirstOrDefault(
            s => s.Kind == WorkspaceSession.EcosystemKind && s.EcosystemId == ecosystemId);

    /// <summary>The open ecosystem chat for the ecosystem a member session belongs to (or null).</summary>
    private WorkspaceSession? EcosystemChatForMember(WorkspaceSession source)
    {
        // The ecosystem chat never feeds itself; and roots map via the real ProjectRoot, not a worktree.
        if (source.Kind == WorkspaceSession.EcosystemKind || string.IsNullOrEmpty(source.ProjectRoot))
        {
            return null;
        }

        var root = EcosystemIndex.NormalizeRoot(source.ProjectRoot);
        return _ecosystemByRoot.TryGetValue(root, out var eco) ? FindEcosystemChat(eco.Id) : null;
    }

    /// <summary>Tee one member event into its ecosystem chat's feed (no-op if that chat isn't open).</summary>
    private void TeeToEcosystemFeed(WorkspaceSession source, AgentEvent evt)
    {
        var chat = EcosystemChatForMember(source);
        if (chat is null)
        {
            return;
        }

        var block = MakeFeedBlock(source, evt);
        if (block is null)
        {
            return;
        }

        chat.Conversation.Add(block);
        if (IsActiveSession(chat))
        {
            ScrollChatToEnd();
        }
    }

    /// <summary>A member turn boundary ("started"/"finished") as a feed entry.</summary>
    private void NoteMemberTurnBoundary(WorkspaceSession source, bool starting)
    {
        var chat = EcosystemChatForMember(source);
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

    /// <summary>Translate a member's agent event into an attributed feed block (rich). Null = don't surface.</summary>
    private MemberActivityBlock? MakeFeedBlock(WorkspaceSession source, AgentEvent evt)
    {
        var member = MemberDisplayName(source);
        return evt switch
        {
            ChatEvent { Role: "user" } => null,
            ChatEvent chat when !string.IsNullOrWhiteSpace(chat.Text)
                => new MemberActivityBlock { Member = member, Kind = "message", Summary = "said", Detail = Condense(chat.Text, 200) },
            ToolCallEvent call
                => new MemberActivityBlock { Member = member, Kind = "tool", Summary = $"ran {call.ToolName}", Detail = Condense(call.ArgumentsJson, 120) },
            ToolResultEvent { IsError: true } err
                => new MemberActivityBlock { Member = member, Kind = "error", Summary = $"error in {err.ToolName}", Detail = Condense(err.Summary, 200) },
            VerificationEvent verification
                => new MemberActivityBlock { Member = member, Kind = "tool", Summary = "verified", Detail = verification.Passed == true ? "passed" : "failed" },
            _ => null,
        };
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

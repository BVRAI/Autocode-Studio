using System.IO;
using System.Text.RegularExpressions;
using AutoCode.Desktop.Misc;
using AutoCode.Desktop.ViewModels;
using AutoCode.Engine.Agent;

namespace AutoCode.Desktop;

// @mention routing: from an ecosystem chat, a leading "@member ..." routes the prompt to that member's
// own session (opening one if needed) instead of the ecosystem agent, so the user can dispatch work
// without leaving the ecosystem chat. Members are the active ecosystem's MemberRoots; a handle is the
// member project's leaf folder name. All of this runs on the UI thread (called from the send path).
public partial class MainWindow
{
    private static readonly Regex MentionToken = new(@"^@([A-Za-z0-9._\-]+)\s*", RegexOptions.Compiled);

    /// <summary>Parse leading @mentions against the active ecosystem's members. Returns the resolved member
    /// roots (in order, de-duped) and the remaining prompt, or null when there's nothing to route.</summary>
    private (List<string> Roots, string Prompt)? ParseMentions(WorkspaceSession active, string input)
    {
        if (active.Kind != WorkspaceSession.EcosystemKind || active.EcosystemId is null)
        {
            return null;
        }

        var eco = _ecosystems.FirstOrDefault(e => e.Id == active.EcosystemId);
        if (eco is null || eco.MemberRoots.Count == 0)
        {
            return null;
        }

        var byHandle = MemberRootsByHandle(eco);

        var roots = new List<string>();
        var rest = input.TrimStart();
        while (rest.StartsWith('@'))
        {
            var match = MentionToken.Match(rest);
            if (!match.Success || !byHandle.TryGetValue(match.Groups[1].Value, out var root))
            {
                break;   // unknown handle → stop; the text falls through as a normal prompt
            }

            if (!roots.Any(r => r.Equals(root, StringComparison.OrdinalIgnoreCase)))
            {
                roots.Add(root);
            }

            rest = rest[match.Length..];
        }

        return roots.Count == 0 ? null : (roots, rest.Trim());
    }

    /// <summary>If the input starts with member @mentions, route it and return true; else return false so
    /// the caller submits normally to the active session.</summary>
    private bool TryRouteMentions(WorkspaceSession active, string input)
    {
        var parsed = ParseMentions(active, input);
        if (parsed is null)
        {
            return false;
        }

        var (roots, prompt) = parsed.Value;

        // Echo the full typed line in the ecosystem chat so the user sees what they sent.
        active.Conversation.Add(new UserBubbleBlock { Text = input });
        if (IsActiveSession(active)) { ScrollChatToEnd(); }

        if (prompt.Length == 0)
        {
            // A bare @mention just jumps to that member's tab.
            _ = FocusMemberAsync(roots[0]);
            return true;
        }

        foreach (var root in roots)
        {
            _ = DispatchToMemberAsync(active, root, prompt);
        }

        return true;
    }

    /// <summary>Send a routed prompt to a member: run it now, or queue it if the member is mid-turn.</summary>
    private async Task DispatchToMemberAsync(WorkspaceSession ecosystemChat, string root, string prompt)
    {
        var member = await FocusOrCreateMemberAsync(root);
        if (member.IsWorking)
        {
            member.PendingPrompts.Enqueue(prompt);
            ecosystemChat.Conversation.Add(new MemberActivityBlock
            {
                Member = MemberDisplayName(member),
                Summary = $"busy — queued ({member.PendingPrompts.Count})",
                Kind = "queued",
            });
            if (IsActiveSession(ecosystemChat)) { ScrollChatToEnd(); }
            return;
        }

        await SubmitPromptAsync(member, prompt);
    }

    /// <summary>Find a live member workspace for a root, or create one WITHOUT activating it (so the user
    /// stays in the ecosystem chat). Honors auto-worktree isolation on newly-created members.</summary>
    private async Task<WorkspaceSession> FocusOrCreateMemberAsync(string root)
    {
        var live = LiveMemberSession(root);
        if (live is not null)
        {
            return live;
        }

        var agentId = string.IsNullOrWhiteSpace(_config.DefaultAgentId) ? "builtin" : _config.DefaultAgentId;
        var id = SessionIds.NewId();
        var member = CreateSession(id, SessionIndex.SessionDir(id), Path.GetFullPath(root), agentId);
        member.Status = "ready";
        member.ChatTitle = "New session";
        _vm.Sessions.Add(member);   // add to the live set, but do NOT Activate
        RebuildSidebar(_vm.Active?.Id);
        RefreshUsage(member);
        RefreshFiles(member);
        if (_config.AutoWorktree)
        {
            await PrepareWorktreeAsync(member);
        }

        return member;
    }

    /// <summary>Jump to a member's tab: focus a live one, else open a new workspace there.</summary>
    private async Task FocusMemberAsync(string root)
    {
        var live = LiveMemberSession(root);
        if (live is not null)
        {
            ActivateWorkspace(live);
            return;
        }

        await StartNewSession(root);
    }

    /// <summary>Handle → member-root map for an ecosystem. A handle is the member project's leaf
    /// folder name (duplicate leaf names collide, last wins — rare, accepted). Shared by @mention
    /// parsing and the manager's dispatch tool.</summary>
    private static Dictionary<string, string> MemberRootsByHandle(EcosystemRecord eco)
    {
        var byHandle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in eco.MemberRoots)
        {
            byHandle[LeafName(root)] = root;
        }

        return byHandle;
    }

    private WorkspaceSession? LiveMemberSession(string root)
    {
        var norm = EcosystemIndex.NormalizeRoot(root);
        return _vm.Sessions.Sessions.FirstOrDefault(
            s => s.Kind == WorkspaceSession.ProjectKind
                 && EcosystemIndex.NormalizeRoot(s.ProjectRoot).Equals(norm, StringComparison.OrdinalIgnoreCase));
    }
}

using AutoCode.Desktop.Orchestration;
using AutoCode.Desktop.ViewModels;
using AutoCode.Engine.Agent;

namespace AutoCode.Desktop;

// Manager orchestration: the delegates behind the ecosystem chat's injected dispatch_to_member /
// list_members tools (wired in WireLoop for builtin-driven ecosystem chats only). Tool ExecuteAsync
// runs on a thread-pool thread — the agent loop is ConfigureAwait(false) throughout — so every
// touch of UI-thread state here marshals through Dispatcher.InvokeAsync, and the member's turn is
// awaited async-all-the-way (the Dispatcher pump stays free; no deadlock). Semantics: sync await;
// reject-if-busy (the manager replans — only user @mentions queue); the member's turn runs in
// Autocode mode (the approved dispatch IS the approval) under a CTS linked to the manager's token,
// so Stop on the manager cancels dispatched work too.
public partial class MainWindow
{
    private const int DispatchOutcomeMaxChars = 4_000;

    /// <summary>Route a manager dispatch to a member session and await its full turn.</summary>
    private async Task<DispatchOutcome> DispatchForManagerAsync(WorkspaceSession manager, string memberHandle, string task, CancellationToken cancellationToken)
    {
        // Resolve + open the target on the UI thread (registry, live sessions, worktree prep).
        var (member, error) = await await Dispatcher.InvokeAsync(async () =>
        {
            var eco = _ecosystems.FirstOrDefault(e => e.Id == manager.EcosystemId);
            if (eco is null)
            {
                return ((WorkspaceSession?)null, "This chat's ecosystem no longer exists in the registry.");
            }

            var byHandle = MemberRootsByHandle(eco);
            if (!byHandle.TryGetValue(memberHandle.Trim(), out var root))
            {
                var valid = string.Join(", ", byHandle.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
                return ((WorkspaceSession?)null, $"Unknown member \"{memberHandle}\". Valid members: {valid}.");
            }

            var target = await FocusOrCreateMemberAsync(root);
            return target.IsWorking
                ? ((WorkspaceSession?)null, $"\"{memberHandle}\" is busy with another turn. Check list_members and retry later, or adjust your plan.")
                : (target, "");
        });

        if (member is null)
        {
            return new DispatchOutcome(false, error);
        }

        // The member executes autonomously (Autocode): the user's approval of this dispatch was the
        // approval — a background member raising its own approval card would hijack the active tab.
        await await Dispatcher.InvokeAsync(() => SubmitPromptAsync(member, task, AgentMode.Autocode, cancellationToken));

        return await Dispatcher.InvokeAsync(() =>
        {
            var text = member.Conversation.OfType<AssistantBlock>().LastOrDefault()?.Text ?? "(the member produced no final message)";
            var ok = member.Status is not ("error" or "cancelled");
            return new DispatchOutcome(ok, TruncateOutcome(ok ? text : $"Turn ended with status \"{member.Status}\".\n{text}"));
        });
    }

    /// <summary>Live roster for the manager: each member's handle + busy/idle status.</summary>
    private async Task<string> ListMembersForManagerAsync(WorkspaceSession manager)
    {
        return await Dispatcher.InvokeAsync(() =>
        {
            var eco = _ecosystems.FirstOrDefault(e => e.Id == manager.EcosystemId);
            if (eco is null)
            {
                return "This chat's ecosystem no longer exists in the registry.";
            }

            if (eco.MemberRoots.Count == 0)
            {
                return "This ecosystem has no member projects yet.";
            }

            var lines = eco.MemberRoots.Select(root =>
            {
                var live = LiveMemberSession(root);
                var status = live is null ? "idle (no open workspace — dispatch opens one)"
                    : live.IsWorking ? "busy"
                    : "idle";
                return $"- {LeafName(root)} — {status}";
            });
            return string.Join('\n', lines);
        });
    }

    /// <summary>Cap a dispatch outcome so a verbose member can't flood the manager's context window.</summary>
    private static string TruncateOutcome(string text)
        => text.Length <= DispatchOutcomeMaxChars ? text : text[..DispatchOutcomeMaxChars] + "\n…(outcome truncated)";
}

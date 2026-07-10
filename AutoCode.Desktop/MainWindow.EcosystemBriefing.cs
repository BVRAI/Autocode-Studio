using System.Text;
using AutoCode.Desktop.Misc;
using AutoCode.Desktop.ViewModels;

namespace AutoCode.Desktop;

// Ecosystem briefing: the Desktop-composed text injected into a session's system prompt (via the
// engine's generic SessionContext.SystemAppendix seam) so agents know their place in an ecosystem.
// Members learn who their siblings are + where the shared manifest files live + how to report
// milestones; the ecosystem chat's agent learns it is the coordinator. Computed live per turn in
// SubmitPromptAsync, so membership changes apply on the next turn. Returns null for sessions in no
// ecosystem — non-ecosystem users see zero change (dormant-until-opt-in).
public partial class MainWindow
{
    private string? BuildEcosystemBriefing(WorkspaceSession session)
    {
        if (session.Kind == WorkspaceSession.EcosystemKind)
        {
            var eco = _ecosystems.FirstOrDefault(e => e.Id == session.EcosystemId);
            return eco is null ? null : CoordinatorBriefing(eco, session);
        }

        if (string.IsNullOrEmpty(session.ProjectRoot)
            || !_ecosystemByRoot.TryGetValue(EcosystemIndex.NormalizeRoot(session.ProjectRoot), out var memberEco))
        {
            return null;
        }

        return MemberBriefing(memberEco, session);
    }

    private string MemberBriefing(EcosystemRecord eco, WorkspaceSession session)
    {
        var self = LeafName(session.ProjectRoot);
        var siblings = eco.MemberRoots
            .Select(LeafName)
            .Where(n => !n.Equals(self, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"This project (`{self}`) is a member of the \"{eco.Name}\" ecosystem — a group of projects built as one product.");
        sb.AppendLine(siblings.Count > 0
            ? $"Other member projects: {string.Join(", ", siblings.Select(s => $"`{s}`"))}."
            : "It is currently the only member project.");
        sb.AppendLine($"Shared source of truth (read when your work touches shared shapes or cross-project concerns): {eco.ManifestRoot}");
        sb.AppendLine("- manifest.json (membership) · checklist.md (component checklist) · contract/data-contract.md (shared data contract) · design-tokens/tokens.json (shared design tokens)");
        sb.AppendLine("Keep your changes compatible with the data contract; propose contract changes there first.");
        sb.AppendLine(session.AgentId == "builtin"
            ? "When you complete a milestone, fix a cross-cutting bug, or change a shared interface, call the `report_to_ecosystem` tool with a one-line message."
            : "When you complete a milestone, fix a cross-cutting bug, or change a shared interface, output a line starting exactly with `ECOSYSTEM_REPORT: ` followed by a one-line message.");
        return sb.ToString().TrimEnd();
    }

    private string CoordinatorBriefing(EcosystemRecord eco, WorkspaceSession session)
    {
        var members = eco.MemberRoots.Select(LeafName).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"You are the coordination agent for the \"{eco.Name}\" ecosystem, working inside its manifest repo (the group's shared source of truth).");
        sb.AppendLine(members.Count > 0
            ? $"Member projects: {string.Join(", ", members.Select(m => $"`{m}`"))}."
            : "No member projects have been added yet.");
        sb.AppendLine("Your files: manifest.json (membership — maintained by the app), checklist.md (component checklist), contract/data-contract.md (shared data contract), design-tokens/tokens.json, reports.md (milestones reported by member agents — append-only log).");
        sb.AppendLine("Maintain the checklist, contract, and tokens as the product evolves. Member agents' live activity is relayed to the user separately; you don't need to restate it.");

        // Only the builtin engine can receive the injected orchestration tools (WireLoop).
        if (session.AgentId == "builtin" && members.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("You can also manage the members directly:");
            sb.AppendLine("- `list_members` — each member's handle and whether its agent is busy or idle.");
            sb.AppendLine("- `dispatch_to_member` — send one member a task and wait for its outcome. The member cannot see this conversation, so write each task as a complete, self-contained instruction (goal, relevant paths, what the shared contract requires, what done looks like). Dispatch one task at a time; review the outcome (and reports.md / the checklist) before the next.");
            sb.AppendLine("Dispatches may require the user's approval — if one is declined or revised, adapt your plan rather than re-sending it unchanged. If a member is busy, do other useful work or tell the user; don't retry in a loop. Update the checklist when dispatched work completes.");
        }

        return sb.ToString().TrimEnd();
    }
}

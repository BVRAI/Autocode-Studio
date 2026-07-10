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
            return eco is null ? null : CoordinatorBriefing(eco);
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

    private string CoordinatorBriefing(EcosystemRecord eco)
    {
        var members = eco.MemberRoots.Select(LeafName).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"You are the coordination agent for the \"{eco.Name}\" ecosystem, working inside its manifest repo (the group's shared source of truth).");
        sb.AppendLine(members.Count > 0
            ? $"Member projects: {string.Join(", ", members.Select(m => $"`{m}`"))}."
            : "No member projects have been added yet.");
        sb.AppendLine("Your files: manifest.json (membership — maintained by the app), checklist.md (component checklist), contract/data-contract.md (shared data contract), design-tokens/tokens.json, reports.md (milestones reported by member agents — append-only log).");
        sb.AppendLine("Maintain the checklist, contract, and tokens as the product evolves. Member agents' live activity is relayed to the user separately; you don't need to restate it.");
        return sb.ToString().TrimEnd();
    }
}

using System.Collections.Generic;
using System.Linq;
using AutoCode.Desktop.ViewModels;
using AutoCode.Engine.Agent;

namespace AutoCode.Desktop;

// Engine events: translate a session's AgentLoop events into its conversation/timeline/plan on the
// dispatcher. Handlers take the originating WorkspaceSession (concurrency-ready); UI-only side effects
// (scroll, auto-open the Plan panel) fire only when that session is the active one.
public partial class MainWindow
{
    private static readonly HashSet<string> MutatingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "edit_file", "write_file", "create_directory", "delete_path", "run_shell",
    };

    private bool IsActiveSession(WorkspaceSession session) => ReferenceEquals(_vm.Active, session);

    private async Task EmitAsync(WorkspaceSession session, AgentEvent evt)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            switch (evt)
            {
                case ChatEvent { Role: "user" } userChat:
                    // Engine echoes the user turn; we already added a bubble on submit. Skip duplicates.
                    if (session.Conversation.LastOrDefault() is not UserBubbleBlock)
                    {
                        session.Conversation.Add(new UserBubbleBlock { Text = userChat.Text });
                    }
                    break;

                case ChatEvent chat:
                    FinalizeWorked(session);
                    session.Conversation.Add(new AssistantBlock { Text = chat.Text });
                    session.PendingDiff = null;
                    if (IsActiveSession(session)) { ScrollChatToEnd(); }
                    break;

                case StatusEvent status:
                    session.Status = status.Text;
                    break;

                case ToolCallEvent call:
                    OnToolCall(session, call);
                    break;

                case ToolResultEvent result:
                    OnToolResult(session, result);
                    RefreshUsage(session);
                    break;

                case VerificationEvent verification:
                    OnVerification(session, verification);
                    break;

                case PlanEvent plan:
                    SetPlan(session, plan.Items);
                    break;
            }
        });
    }

    // Rebuild the live plan/todo checklist for a session. Reveals the Plan tab the first time a plan
    // appears for the ACTIVE session (only if the panel is closed, so we never yank the user away).
    private void SetPlan(WorkspaceSession session, IReadOnlyList<PlanItem> items)
    {
        session.Plan.Clear();
        foreach (var item in items)
        {
            session.Plan.Add(new PlanItemVM { Text = item.Text, Status = item.Status });
        }

        var had = session.HasPlan;
        session.HasPlan = session.Plan.Count > 0;
        if (!IsActiveSession(session))
        {
            return;
        }

        if (session.HasPlan && !had && !_vm.PanelOpen)
        {
            OpenPanel("plan");
        }
        else if (!session.HasPlan && _vm.PanelTab == "plan")
        {
            _vm.PanelTab = "workspace";
        }
    }

    private void ClearPlan(WorkspaceSession session)
    {
        session.Plan.Clear();
        session.HasPlan = false;
        if (IsActiveSession(session) && _vm.PanelTab == "plan")
        {
            _vm.PanelTab = "workspace";
        }
    }

    private void OnToolCall(WorkspaceSession session, ToolCallEvent call)
    {
        EnsureWorkedGroup(session);
        var step = new WorkedStep { Tool = call.ToolName, Status = "running" };
        session.CurrentWorked!.Steps.Add(step);

        var item = new TimelineItemVM { ToolName = call.ToolName, Status = "running" };
        session.Timeline.Insert(0, item);
        session.RunningTools.Enqueue((step, item));

        if (MutatingTools.Contains(call.ToolName)
            && session.Context?.Mode is AgentMode.Autocode or AgentMode.Admin)
        {
            session.ResolvedStatus = "Auto-approved";
        }

        if (IsActiveSession(session)) { ScrollChatToEnd(); }
    }

    private void OnToolResult(WorkspaceSession session, ToolResultEvent result)
    {
        var status = result.IsError ? "error" : "done";
        if (session.RunningTools.Count > 0)
        {
            var (step, item) = session.RunningTools.Dequeue();
            step.Status = status;
            step.Detail = result.Summary;
            item.Status = status;
            item.Summary = result.Summary;
            item.DurationMs = result.DurationMs;
        }
        else if (session.CurrentWorked is not null)
        {
            session.CurrentWorked.Steps.Add(new WorkedStep { Tool = result.ToolName, Status = status, Detail = result.Summary });
        }

        if (!result.IsError && MutatingTools.Contains(result.ToolName) && result.ToolName != "run_shell")
        {
            AccumulateDiff(session, result);
        }

        if (result.IsError)
        {
            session.Conversation.Add(new NoticeBlock { Title = $"{result.ToolName} failed", Detail = result.Content });
        }

        if (IsActiveSession(session)) { ScrollChatToEnd(); }
    }

    private void OnVerification(WorkspaceSession session, VerificationEvent verification)
    {
        var status = verification.Passed == false ? "error" : "done";
        var label = verification.Passed is null ? "verification" : verification.Passed.Value ? "verification passed" : "verification failed";
        session.Timeline.Insert(0, new TimelineItemVM { ToolName = label, Status = status, Summary = verification.Command });
        if (verification.Passed == false && !string.IsNullOrWhiteSpace(verification.Output))
        {
            session.Conversation.Add(new NoticeBlock { Title = "Verification failed", Detail = verification.Output });
        }
    }

    private void EnsureWorkedGroup(WorkspaceSession session)
    {
        if (session.CurrentWorked is not null)
        {
            return;
        }

        // Collapse the previous group; keep the newest expanded.
        foreach (var block in session.Conversation.OfType<WorkedForBlock>())
        {
            block.IsExpanded = false;
        }

        session.CurrentWorked = new WorkedForBlock { Label = "Working…", IsExpanded = true, StartedAt = DateTimeOffset.Now };
        session.Conversation.Add(session.CurrentWorked);
    }

    private void FinalizeWorked(WorkspaceSession session)
    {
        if (session.CurrentWorked is null)
        {
            return;
        }

        var elapsed = DateTimeOffset.Now - session.CurrentWorked.StartedAt;
        session.CurrentWorked.Label = $"Worked for {FormatElapsed(elapsed)}";
        session.CurrentWorked = null;
        session.RunningTools.Clear();
    }

    private void AccumulateDiff(WorkspaceSession session, ToolResultEvent result)
    {
        var (adds, dels) = ParseAddsDels(result.Summary);
        var path = ParsePath(result.Summary) ?? result.ToolName;

        if (session.PendingDiff is null)
        {
            session.PendingDiff = new DiffCardBlock();
            session.Conversation.Add(session.PendingDiff);
        }

        session.PendingDiff.Files.Add(new DiffFileRow { Path = path, Adds = adds, Dels = dels });
        session.PendingDiff.Refresh();

        var rel = TryRelative(session, path);
        if (rel is not null)
        {
            session.ModifiedFiles.Add(rel);
        }
    }

    private void ResetTurnState(WorkspaceSession session)
    {
        session.CurrentWorked = null;
        session.PendingDiff = null;
        session.RunningTools.Clear();
        session.ResolvedStatus = "";
    }
}

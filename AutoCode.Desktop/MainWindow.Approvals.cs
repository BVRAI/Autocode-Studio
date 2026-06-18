using System.Collections.Generic;
using System.Windows;
using AutoCode.Desktop.ViewModels;
using AutoCode.Engine.Agent;
using AutoCode.Engine.Tools;
using MessageBox = System.Windows.MessageBox;

namespace AutoCode.Desktop;

// Approvals: bridge a session's approve/confirm/choose callbacks to UI prompts. The approval lives on
// the requesting session; the Accept/Decline/Revise buttons act on the active session's pending approval.
public partial class MainWindow
{
    private async Task<ApprovalDecision> ApproveToolAsync(WorkspaceSession session, ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.InvokeAsync(() =>
        {
            session.ApprovalCompletion = completion;
            session.ResolvedStatus = "";
            session.Approval = BuildApproval(request);
            // A pending approval must be visible to be actioned — focus the requesting session.
            if (!ReferenceEquals(_vm.Active, session))
            {
                _vm.Sessions.Activate(session);
                RebuildSidebar(session.Id);
            }

            OpenPanel("run");
        });

        await using var _ = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task.ConfigureAwait(false);
    }

    private static ApprovalVM BuildApproval(ToolApprovalRequest request)
    {
        var target = "";
        if (request.Input.TryGetValue("path", out var p) && p is not null)
        {
            target = Convert.ToString(p) ?? "";
        }
        else if (request.Input.TryGetValue("command", out var c) && c is not null)
        {
            target = Convert.ToString(c) ?? "";
        }

        var vm = new ApprovalVM { ToolName = request.ToolName, Target = target };
        foreach (var line in (request.Preview ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var kind = "ctx";
            if (line.StartsWith('+')) { kind = "add"; }
            else if (line.StartsWith('-')) { kind = "del"; }
            vm.PreviewLines.Add(new PreviewLine { Text = line, Kind = kind });
        }

        return vm;
    }

    private void AcceptApproval() => CompleteApproval(_vm.Active, ApprovalDecision.Accept(), "Approved");

    private void DeclineApproval() => CompleteApproval(_vm.Active, ApprovalDecision.Decline(), "Declined");

    private void ReviseApproval() => CompleteApproval(_vm.Active, ApprovalDecision.Revise(_vm.RevisionText), "Revision requested");

    private void CompleteApproval(WorkspaceSession? session, ApprovalDecision decision, string resolved)
    {
        if (session is null)
        {
            return;
        }

        session.Approval = null;
        session.ResolvedStatus = resolved;
        _vm.RevisionText = "";
        session.ApprovalCompletion?.TrySetResult(decision);
        session.ApprovalCompletion = null;
    }

    private async Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken)
    {
        var result = await Dispatcher.InvokeAsync(() =>
            MessageBox.Show(this, prompt, "Confirm command", MessageBoxButton.YesNo, MessageBoxImage.Warning));
        return result == MessageBoxResult.Yes;
    }

    private async Task<IReadOnlyList<int>> ChooseAsync(AskUserRequest request, CancellationToken cancellationToken)
    {
        return await Dispatcher.InvokeAsync(() =>
        {
            var dialog = new ChoiceDialog(request) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.SelectedIndexes : (IReadOnlyList<int>)[];
        });
    }
}

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AutoCode.Desktop.Misc;
using AutoCode.Desktop.ViewModels;

namespace AutoCode.Desktop;

/// <summary>Inline session rename: double-click the title in the top bar, a live WORKSPACES row,
/// or a saved PROJECTS row to edit it in place (Enter commits, Escape cancels, clicking away
/// commits). Each location has its own editor, editing flag, and event handlers — two editors
/// bound to one flag open together and their focus grabs cancel each other, and a shared
/// LostFocus handler lets a stale editor's focus-loss close a freshly opened edit.</summary>
public partial class MainWindow
{
    // ---- begin editing (double-click; not handled while already editing so the TextBox keeps
    //      its native double-click word selection) ----

    private void HeaderTitle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var session = _vm.Active;
        if (e.ClickCount != 2 || session is null || session.IsEditingTitleHeader)
        {
            return;
        }

        CommitSidebarTitleEdit(session);
        session.EditableTitle = string.IsNullOrWhiteSpace(session.ChatTitle) ? "New session" : session.ChatTitle;
        session.IsEditingTitleHeader = true;
        e.Handled = true;
    }

    private void WorkspaceTitle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || (sender as FrameworkElement)?.DataContext is not WorkspaceSession session || session.IsEditingTitleSidebar)
        {
            return;
        }

        CommitHeaderTitleEdit(session);
        session.EditableTitle = string.IsNullOrWhiteSpace(session.ChatTitle) ? "New session" : session.ChatTitle;
        session.IsEditingTitleSidebar = true;
        e.Handled = true;
    }

    private void SavedSessionTitle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || (sender as FrameworkElement)?.DataContext is not SessionNode node || node.IsEditingTitle)
        {
            return;
        }

        // The double-click's first click already opened this session. Cold-opening adds a
        // WORKSPACES row above, shifting every PROJECTS row down — so the second click can land
        // on a *different* row. Only rename the row that is now the active session; swallow the
        // stray click on a shifted row so it doesn't open yet another session.
        if (node.IsActive)
        {
            node.EditableTitle = string.IsNullOrWhiteSpace(node.Title) ? "Session" : node.Title;
            node.IsEditingTitle = true;
        }

        e.Handled = true;
    }

    // ---- editor events, one set per location ----

    private void HeaderTitleEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WorkspaceSession session)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitHeaderTitleEdit(session);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelHeaderTitleEdit(session);
            e.Handled = true;
        }
    }

    private void HeaderTitleEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WorkspaceSession session)
        {
            CommitHeaderTitleEdit(session);
        }
    }

    private void WorkspaceTitleEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WorkspaceSession session)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitSidebarTitleEdit(session);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelSidebarTitleEdit(session);
            e.Handled = true;
        }
    }

    private void WorkspaceTitleEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WorkspaceSession session)
        {
            CommitSidebarTitleEdit(session);
        }
    }

    private void SavedTitleEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionNode node)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitSavedSessionTitleEdit(node);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelSavedSessionTitleEdit(node);
            e.Handled = true;
        }
    }

    private void SavedTitleEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionNode node)
        {
            CommitSavedSessionTitleEdit(node);
        }
    }

    private void TitleEditor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not TextBox box)
        {
            return;
        }

        box.Dispatcher.BeginInvoke(new Action(() =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
        }));
    }

    // ---- commit / cancel ----

    private void CommitHeaderTitleEdit(WorkspaceSession session)
    {
        if (!session.IsEditingTitleHeader)
        {
            return;
        }

        session.IsEditingTitleHeader = false;
        ApplyWorkspaceTitle(session);
    }

    private void CommitSidebarTitleEdit(WorkspaceSession session)
    {
        if (!session.IsEditingTitleSidebar)
        {
            return;
        }

        session.IsEditingTitleSidebar = false;
        ApplyWorkspaceTitle(session);
    }

    private void ApplyWorkspaceTitle(WorkspaceSession session)
    {
        var title = NormalizeTitle(session.EditableTitle, "New session");
        session.ChatTitle = title;
        session.EditableTitle = title;
        WriteSidecar(session);
        RebuildSidebar(session.Id);
    }

    private static void CancelHeaderTitleEdit(WorkspaceSession session)
    {
        session.IsEditingTitleHeader = false;
        session.EditableTitle = string.IsNullOrWhiteSpace(session.ChatTitle) ? "New session" : session.ChatTitle;
    }

    private static void CancelSidebarTitleEdit(WorkspaceSession session)
    {
        session.IsEditingTitleSidebar = false;
        session.EditableTitle = string.IsNullOrWhiteSpace(session.ChatTitle) ? "New session" : session.ChatTitle;
    }

    private void CommitSavedSessionTitleEdit(SessionNode node)
    {
        if (!node.IsEditingTitle)
        {
            return;
        }

        node.IsEditingTitle = false;
        var title = NormalizeTitle(node.EditableTitle, "Session");
        node.Title = title;
        node.EditableTitle = title;

        var live = _vm.Sessions.FindById(node.Id);
        if (live is not null)
        {
            live.ChatTitle = title;
            live.EditableTitle = title;
            WriteSidecar(live);
        }
        else
        {
            SessionIndex.Write(node.SessionDir, new SessionSidecar(
                node.Id,
                title,
                node.ProjectRoot,
                node.Model,
                node.StartedAt,
                node.GitBranch,
                node.GitWorktreePath,
                node.GitBaseBranch,
                node.AgentId,
                node.ExternalResumeId));
        }

        RebuildSidebar(_vm.Active?.Id);
    }

    private static void CancelSavedSessionTitleEdit(SessionNode node)
    {
        node.IsEditingTitle = false;
        node.EditableTitle = string.IsNullOrWhiteSpace(node.Title) ? "Session" : node.Title;
    }

    private static string NormalizeTitle(string? value, string fallback)
    {
        var title = (value ?? "").Trim();
        return string.IsNullOrWhiteSpace(title) ? fallback : title;
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AutoCode.Desktop.Misc;
using AutoCode.Desktop.ViewModels;
using AutoCode.Engine.Agent;

namespace AutoCode.Desktop;

// Ecosystems: the grouping level above projects. Phase 1 = registry + manifest git repo +
// right-click create/assign/move/remove + a bare sidebar list. The registry (EcosystemIndex)
// is the source of truth; manifest repo writes are best-effort and always happen LAST in a
// mutation (registry -> cache -> sidebar -> manifest), so the UI never waits on git.
public partial class MainWindow
{
    private List<EcosystemRecord> _ecosystems = [];
    private readonly Dictionary<string, EcosystemRecord> _ecosystemByRoot = new(StringComparer.OrdinalIgnoreCase);
    private object? _projectMenuTarget;

    /// <summary>Load the registry + build the root→ecosystem cache. Runs in Window_Loaded after the
    /// config block and before the first StartNewSession (which triggers the first RebuildSidebar).</summary>
    private void InitEcosystems()
    {
        _ecosystems = EcosystemIndex.LoadAll();
        RebuildEcosystemCache();
    }

    private void RebuildEcosystemCache()
    {
        _ecosystemByRoot.Clear();
        foreach (var eco in _ecosystems)
        {
            foreach (var root in eco.MemberRoots)
            {
                _ecosystemByRoot[root] = eco;
            }
        }
    }

    /// <summary>Refill the sidebar's bare ECOSYSTEMS list. Called from RebuildSidebar.</summary>
    private void RebuildEcosystemRows()
    {
        _vm.Ecosystems.Clear();
        foreach (var eco in _ecosystems)
        {
            _vm.Ecosystems.Add(new EcosystemNode
            {
                Id = eco.Id,
                Name = eco.Name,
                MemberCountText = Loc.F("EcosystemMembers", eco.MemberRoots.Count),
            });
        }

        _vm.HasEcosystems = _ecosystems.Count > 0;
    }

    /// <summary>Take the freshly-built project rows and populate the sidebar for the active mode.
    /// Flat (default): bare ECOSYSTEMS list + all projects under PROJECTS. Grouped: each ecosystem
    /// is an expandable header with its member projects nested; projects in no ecosystem stay under
    /// PROJECTS. Called by RebuildSidebar. Grouped mode requires the preference on AND ecosystems to exist.</summary>
    private void ApplySidebarGrouping(IReadOnlyList<ProjectNode> projects)
    {
        _vm.HasEcosystems = _ecosystems.Count > 0;
        var grouped = _vm.GroupByEcosystem && _vm.HasEcosystems;
        _vm.ShowGrouped = grouped;
        _vm.ShowBareEcosystems = _vm.HasEcosystems && !grouped;

        _vm.Projects.Clear();
        _vm.Ecosystems.Clear();
        _vm.EcosystemGroups.Clear();

        if (!grouped)
        {
            RebuildEcosystemRows();
            foreach (var p in projects)
            {
                _vm.Projects.Add(p);
            }
        }
        else
        {
            var groupById = new Dictionary<string, EcosystemNode>();
            foreach (var eco in _ecosystems)
            {
                var node = new EcosystemNode
                {
                    Id = eco.Id,
                    Name = eco.Name,
                    MemberCountText = Loc.F("EcosystemMembers", eco.MemberRoots.Count),
                    HasProjects = eco.MemberRoots.Count > 0,
                    EmptyHint = Loc.F("EcoNoProjectsHint", eco.Name),
                    // Empty ecosystems never auto-expand (design spec §5); non-empty open showing members.
                    IsExpanded = eco.MemberRoots.Count > 0,
                };
                var captured = node;
                node.ToggleCommand = new RelayCommand(_ => captured.IsExpanded = !captured.IsExpanded);
                groupById[eco.Id] = node;
                _vm.EcosystemGroups.Add(node);
            }

            foreach (var p in projects)
            {
                var root = EcosystemIndex.NormalizeRoot(p.Path);
                if (_ecosystemByRoot.TryGetValue(root, out var eco) && groupById.TryGetValue(eco.Id, out var gnode))
                {
                    gnode.Projects.Add(p);
                }
                else
                {
                    _vm.Projects.Add(p);
                }
            }
        }

        _vm.HasProjects = _vm.Projects.Count > 0;
        RefreshEcosystemChatMeta();   // roster subtitles track membership; this runs on every rebuild
    }

    /// <summary>Flip the "group projects by ecosystem" preference, persist it, and rebuild the sidebar.</summary>
    private void GroupByEcosystemToggle_Click(object sender, RoutedEventArgs e)
    {
        _config.GroupByEcosystem = !_config.GroupByEcosystem;
        _vm.GroupByEcosystem = _config.GroupByEcosystem;
        _configStore.Save(_config);
        RebuildSidebar(_vm.Active?.Id);
    }

    // ---- ecosystem chat: a session rooted at the manifest repo (Kind=ecosystem, no worktree) ----

    /// <summary>Header roster line for an ecosystem chat, e.g. "3 members — web · api · mobile".</summary>
    private static string EcosystemRosterSubtitle(EcosystemRecord eco)
        => eco.MemberRoots.Count == 0
            ? ""
            : Loc.F("EcoMembersSubtitle", eco.MemberRoots.Count, string.Join(" · ", eco.MemberRoots.Select(LeafName)));

    /// <summary>Refresh the roster subtitle on any live ecosystem chats (called after membership changes).</summary>
    private void RefreshEcosystemChatMeta()
    {
        foreach (var chat in _vm.Sessions.Sessions.Where(s => s.Kind == WorkspaceSession.EcosystemKind))
        {
            var eco = _ecosystems.FirstOrDefault(e => e.Id == chat.EcosystemId);
            if (eco is not null)
            {
                chat.ChatSubtitle = EcosystemRosterSubtitle(eco);
            }
        }
    }

    /// <summary>Double-click on a grouped ecosystem header opens its chat (single click still toggles).</summary>
    private void EcosystemHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is EcosystemNode node)
        {
            OpenEcosystemChatFromNode(node);
            e.Handled = true;
        }
    }

    private void OpenEcosystemChatFromNode(EcosystemNode node)
    {
        var eco = _ecosystems.FirstOrDefault(x => x.Id == node.Id);
        if (eco is not null)
        {
            _ = OpenEcosystemChatAsync(eco);
        }
    }

    /// <summary>Open (or focus) the ecosystem's chat. One per ecosystem: focus a live one, else cold-open a
    /// persisted one, else create fresh rooted at the manifest repo. Never gets a git worktree.</summary>
    private async Task OpenEcosystemChatAsync(EcosystemRecord eco)
    {
        var live = _vm.Sessions.Sessions.FirstOrDefault(
            s => s.Kind == WorkspaceSession.EcosystemKind && s.EcosystemId == eco.Id);
        if (live is not null)
        {
            ActivateWorkspace(live);
            return;
        }

        var sc = SessionIndex.LoadAll().FirstOrDefault(
            s => s.Kind == WorkspaceSession.EcosystemKind && s.EcosystemId == eco.Id);
        if (sc is not null)
        {
            var reopened = CreateSession(sc.Id, SessionIndex.SessionDir(sc.Id), sc.ProjectRoot,
                string.IsNullOrEmpty(sc.AgentId) ? "builtin" : sc.AgentId,
                kind: WorkspaceSession.EcosystemKind, ecosystemId: eco.Id);
            reopened.ChatTitle = string.IsNullOrWhiteSpace(sc.Title) ? eco.Name : sc.Title;
            reopened.Status = "ready";
            RehydrateTranscript(reopened);
            _vm.Sessions.Activate(reopened);
            RebuildSidebar(reopened.Id);
            RefreshUsage(reopened);
            RefreshFiles(reopened);
            return;
        }

        if (!Directory.Exists(eco.ManifestRoot))
        {
            await EcosystemManifestService.EnsureRepoAsync(eco);
        }

        var agentId = string.IsNullOrWhiteSpace(_config.DefaultAgentId) ? "builtin" : _config.DefaultAgentId;
        var sessionId = SessionIds.NewId();
        var session = CreateSession(sessionId, SessionIndex.SessionDir(sessionId), eco.ManifestRoot, agentId,
            kind: WorkspaceSession.EcosystemKind, ecosystemId: eco.Id);
        session.ChatTitle = eco.Name;
        session.Status = "ready";
        _vm.Sessions.Activate(session);
        RebuildSidebar(sessionId);
        RefreshUsage(session);
        RefreshFiles(session);
    }

    // ---- mutations (registry -> cache -> sidebar -> manifest, manifest last + best-effort) ----

    /// <summary>Add a root to <paramref name="target"/>; a root already in another ecosystem is
    /// moved (at most one ecosystem per root). Rewrites the manifests of every affected ecosystem.</summary>
    private async Task AssignRootToEcosystemAsync(string root, EcosystemRecord target)
    {
        root = EcosystemIndex.NormalizeRoot(root);
        var affected = new List<EcosystemRecord>();
        if (_ecosystemByRoot.TryGetValue(root, out var current))
        {
            if (ReferenceEquals(current, target))
            {
                return;
            }

            current.MemberRoots.RemoveAll(r => r.Equals(root, StringComparison.OrdinalIgnoreCase));
            affected.Add(current);
        }

        target.MemberRoots.Add(root);
        affected.Add(target);

        EcosystemIndex.SaveAll(_ecosystems);
        RebuildEcosystemCache();
        RebuildSidebar(_vm.Active?.Id);
        foreach (var eco in affected)
        {
            await EcosystemManifestService.EnsureRepoAsync(eco);
        }
    }

    private async Task RemoveRootFromEcosystemAsync(string root)
    {
        root = EcosystemIndex.NormalizeRoot(root);
        if (!_ecosystemByRoot.TryGetValue(root, out var eco))
        {
            return;
        }

        eco.MemberRoots.RemoveAll(r => r.Equals(root, StringComparison.OrdinalIgnoreCase));
        EcosystemIndex.SaveAll(_ecosystems);
        RebuildEcosystemCache();
        RebuildSidebar(_vm.Active?.Id);
        await EcosystemManifestService.EnsureRepoAsync(eco);
    }

    /// <summary>Registry-only delete: the manifest repo and member projects stay on disk untouched.</summary>
    private void DeleteEcosystem(EcosystemRecord eco)
    {
        var answer = MessageBox.Show(
            this,
            Loc.F("DeleteEcosystemBody", eco.Name),
            Loc.T("Menu_DeleteEcosystem"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _ecosystems.Remove(eco);
        EcosystemIndex.SaveAll(_ecosystems);
        RebuildEcosystemCache();
        RebuildSidebar(_vm.Active?.Id);
    }

    private async Task CreateEcosystemAsync(string? preselectRoot)
    {
        // Candidates: every known project root — saved sessions + live workspaces. Always
        // session.ProjectRoot (the real folder), never Context.ProjectRoot (worktree path).
        var candidates = SessionIndex.LoadAll().Where(s => s.Kind != WorkspaceSession.EcosystemKind).Select(s => s.ProjectRoot)
            .Concat(_vm.Sessions.Sessions.Where(s => s.Kind != WorkspaceSession.EcosystemKind).Select(s => s.ProjectRoot))
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(EcosystemIndex.NormalizeRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rootToEcoName = _ecosystemByRoot.ToDictionary(kv => kv.Key, kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase);

        var dialog = new CreateEcosystemDialog(candidates, preselectRoot, rootToEcoName) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var eco = new EcosystemRecord(
            Guid.NewGuid().ToString("N")[..12],
            dialog.EcosystemName,
            EcosystemIndex.NormalizeRoot(dialog.ManifestRoot),
            [],
            DateTimeOffset.Now);
        _ecosystems.Add(eco);
        EcosystemIndex.SaveAll(_ecosystems);
        RebuildEcosystemCache();
        RebuildSidebar(_vm.Active?.Id);
        await EcosystemManifestService.EnsureRepoAsync(eco);

        foreach (var root in dialog.SelectedRoots)
        {
            await AssignRootToEcosystemAsync(root, eco);
        }
    }

    // ---- right-click menu (the app's Popup idiom; no WPF ContextMenu styling exists) ----

    private void ProjectRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProjectNode node)
        {
            return;
        }

        _projectMenuTarget = node;
        BuildProjectMenu();
        ProjectMenuPopup.IsOpen = true;
        e.Handled = true;
    }

    private void EcosystemRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EcosystemNode node)
        {
            return;
        }

        _projectMenuTarget = node;
        BuildProjectMenu();
        ProjectMenuPopup.IsOpen = true;
        e.Handled = true;
    }

    private void BuildProjectMenu()
    {
        ProjectMenuPanel.Children.Clear();

        if (_projectMenuTarget is EcosystemNode ecoNode)
        {
            var eco = _ecosystems.FirstOrDefault(x => x.Id == ecoNode.Id);
            if (eco is not null)
            {
                AddMenuItem(Loc.T("Menu_DeleteEcosystem"), () => DeleteEcosystem(eco));
            }

            return;
        }

        if (_projectMenuTarget is not ProjectNode project)
        {
            return;
        }

        var root = EcosystemIndex.NormalizeRoot(project.Path);
        _ecosystemByRoot.TryGetValue(root, out var currentEco);

        AddMenuItem(Loc.T("Menu_NewEcosystem"), () => _ = CreateEcosystemAsync(root));
        foreach (var eco in _ecosystems)
        {
            if (ReferenceEquals(eco, currentEco))
            {
                continue;
            }

            var captured = eco;
            AddMenuItem(
                Loc.F(currentEco is null ? "Menu_AddToEcosystem" : "Menu_MoveToEcosystem", eco.Name),
                () => _ = AssignRootToEcosystemAsync(root, captured));
        }

        if (currentEco is not null)
        {
            AddMenuItem(Loc.F("Menu_RemoveFromEcosystem", currentEco.Name), () => _ = RemoveRootFromEcosystemAsync(root));
        }
    }

    private void AddMenuItem(string label, Action action)
    {
        var button = new Button { Style = (Style)FindResource("MenuItemButtonStyle"), Content = label };
        button.Click += (_, _) =>
        {
            ProjectMenuPopup.IsOpen = false;
            action();
        };
        ProjectMenuPanel.Children.Add(button);
    }
}

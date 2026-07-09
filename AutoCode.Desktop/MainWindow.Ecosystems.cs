using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AutoCode.Desktop.Misc;
using AutoCode.Desktop.ViewModels;

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
        var candidates = SessionIndex.LoadAll().Select(s => s.ProjectRoot)
            .Concat(_vm.Sessions.Sessions.Select(s => s.ProjectRoot))
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

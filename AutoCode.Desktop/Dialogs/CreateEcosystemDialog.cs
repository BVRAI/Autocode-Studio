using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AutoCode.Desktop.Misc;

namespace AutoCode.Desktop;

/// <summary>
/// Create an ecosystem: name, member projects (checkbox list of known roots), and the manifest
/// repo location. The location auto-fills from the checked members (deepest common ancestor +
/// "&lt;name&gt;-ecosystem") until the user edits or browses it. Validation keeps the dialog open
/// with an inline error: the manifest repo must not sit inside a member project, and no member
/// may sit inside the manifest repo.
/// </summary>
public sealed class CreateEcosystemDialog : ModalWindow
{
    private readonly TextBox _nameBox;
    private readonly TextBox _locationBox;
    private readonly TextBlock _errorText;
    private readonly List<(string Root, CheckBox Box)> _members = [];
    private bool _locationIsAuto = true;
    private bool _settingLocation;

    public CreateEcosystemDialog(
        IReadOnlyList<string> candidateRoots,
        string? preselectRoot,
        IReadOnlyDictionary<string, string> rootToEcoName)
        : base("New ecosystem", "Group related projects under one shared manifest: a small git repo holding the member list, component checklist, data contract, and design tokens.", 520)
    {
        _nameBox = MakeTextBox("");
        _nameBox.TextChanged += (_, _) => RecomputeLocation();
        AddField("Name", null, _nameBox);

        var memberPanel = new StackPanel();
        foreach (var root in candidateRoots)
        {
            var label = LeafName(root);
            if (rootToEcoName.TryGetValue(root, out var eco))
            {
                label += $"  (in {eco})";
            }

            var check = new CheckBox
            {
                Content = label,
                ToolTip = root,
                Margin = new Thickness(0, 4, 0, 4),
                Foreground = Res<Brush>("TextBrush"),
                IsChecked = preselectRoot is not null && EcosystemIndex.NormalizeRoot(preselectRoot).Equals(root, StringComparison.OrdinalIgnoreCase),
            };
            check.Checked += (_, _) => RecomputeLocation();
            check.Unchecked += (_, _) => RecomputeLocation();
            _members.Add((root, check));
            memberPanel.Children.Add(check);
        }

        if (_members.Count == 0)
        {
            memberPanel.Children.Add(new TextBlock
            {
                Text = "No known projects yet — you can add projects to the ecosystem later.",
                Foreground = Res<Brush>("Text3Brush"),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        AddField("Projects", null, memberPanel);

        var locationGrid = new Grid();
        locationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        locationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _locationBox = MakeTextBox("");
        _locationBox.TextChanged += (_, _) =>
        {
            if (!_settingLocation)
            {
                _locationIsAuto = false; // user took over
            }
        };
        var browse = new Button
        {
            Content = "Browse…",
            Style = Res<Style>("SmallGhostButtonStyle"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        browse.Click += Browse_Click;
        Grid.SetColumn(browse, 1);
        locationGrid.Children.Add(_locationBox);
        locationGrid.Children.Add(browse);
        AddField("Manifest location", null, locationGrid);

        _errorText = new TextBlock
        {
            Foreground = Res<Brush>("RedBrush"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Body.Children.Add(_errorText);

        AddFooterButton("Cancel", primary: false).Click += (_, _) => { DialogResult = false; Close(); };
        AddFooterButton("Create", primary: true).Click += Create_Click;

        RecomputeLocation();
        Loaded += (_, _) => _nameBox.Focus();
    }

    public string EcosystemName => _nameBox.Text.Trim();

    public string ManifestRoot => _locationBox.Text.Trim();

    public IReadOnlyList<string> SelectedRoots
        => _members.Where(m => m.Box.IsChecked == true).Select(m => m.Root).ToList();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose manifest repo location",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) == true)
        {
            _locationIsAuto = false;
            _locationBox.Text = dialog.FolderName;
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var error = Validate();
        if (error is not null)
        {
            _errorText.Text = error;
            _errorText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
        Close();
    }

    private string? Validate()
    {
        if (EcosystemName.Length == 0)
        {
            return "Give the ecosystem a name.";
        }

        if (ManifestRoot.Length == 0)
        {
            return "Choose a manifest location.";
        }

        string location;
        try
        {
            location = EcosystemIndex.NormalizeRoot(ManifestRoot);
        }
        catch
        {
            return "The manifest location isn't a valid path.";
        }

        foreach (var root in SelectedRoots)
        {
            if (EcosystemIndex.IsInside(location, root))
            {
                return $"The manifest location is inside the project \"{LeafName(root)}\" — put it outside your project folders.";
            }

            if (EcosystemIndex.IsInside(root, location))
            {
                return $"The project \"{LeafName(root)}\" is inside the manifest location — choose a location that doesn't contain member projects.";
            }
        }

        if (Directory.Exists(location) && Directory.EnumerateFileSystemEntries(location).Any())
        {
            return "That folder already has files in it — choose an empty or new folder.";
        }

        return null;
    }

    /// <summary>Auto-fill the location from the checked members while the user hasn't edited it.</summary>
    private void RecomputeLocation()
    {
        if (!_locationIsAuto)
        {
            return;
        }

        var selected = SelectedRoots;
        var parent = CommonAncestor(selected);

        // Step out of any member (single-member case: the ancestor IS the member root).
        while (parent is not null && selected.Any(r => EcosystemIndex.IsInside(parent, r)))
        {
            parent = Path.GetDirectoryName(parent);
        }

        parent ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AutoCode Ecosystems");

        var folder = SanitizeName(EcosystemName) + "-ecosystem";
        var candidate = Path.Combine(parent, folder);
        for (var i = 2; Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any() && i < 10; i++)
        {
            candidate = Path.Combine(parent, $"{folder}-{i}");
        }

        _settingLocation = true;
        _locationBox.Text = candidate;
        _settingLocation = false;
    }

    private static string? CommonAncestor(IReadOnlyList<string> roots)
    {
        if (roots.Count == 0)
        {
            return null;
        }

        var split = roots.Select(r => r.Split(Path.DirectorySeparatorChar)).ToList();
        var common = new List<string>();
        for (var i = 0; ; i++)
        {
            if (split.Any(p => p.Length <= i))
            {
                break;
            }

            var segment = split[0][i];
            if (split.Any(p => !p[i].Equals(segment, StringComparison.OrdinalIgnoreCase)))
            {
                break;
            }

            common.Add(segment);
        }

        if (common.Count == 0)
        {
            return null; // different drives
        }

        var path = string.Join(Path.DirectorySeparatorChar, common);
        return common.Count == 1 ? path + Path.DirectorySeparatorChar : path;
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.ToLowerInvariant()
            .Select(c => c == ' ' ? '-' : c)
            .Where(c => !invalid.Contains(c))
            .ToArray()).Trim('-');
        return cleaned.Length == 0 ? "ecosystem" : cleaned;
    }

    private static string LeafName(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var leaf = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
    }
}

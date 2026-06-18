using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AutoCode.Desktop.Controls;
using AutoCode.Engine.Agent;
using AutoCode.Engine.Llm;

namespace AutoCode.Desktop;

// Menus & popups: settings, session, search, mode/provider/model pickers.
// The model catalog lives in the engine (ModelCatalog) — do not hardcode one here.
public partial class MainWindow
{
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        UseProxyCheck.Visibility = _vm.UseProxy ? Visibility.Visible : Visibility.Hidden;
        AutoBranchCheck.Visibility = _config.AutoWorktree ? Visibility.Visible : Visibility.Hidden;
        SettingsPopup.IsOpen = true;
    }

    private void AutoBranchToggle_Click(object sender, RoutedEventArgs e)
    {
        _config.AutoWorktree = !_config.AutoWorktree;
        _configStore.Save(_config);
        AutoBranchCheck.Visibility = _config.AutoWorktree ? Visibility.Visible : Visibility.Hidden;
    }

    private void SessionMenu_Click(object sender, RoutedEventArgs e) => SessionPopup.IsOpen = true;

    private void SearchToggle_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Visibility = SearchBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SearchBox.Visibility == Visibility.Visible)
        {
            SearchBox.Focus();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        foreach (var project in _vm.Projects)
        {
            var any = false;
            foreach (var s in project.Sessions)
            {
                any = true;
                _ = s;
            }

            if (any && q.Length > 0)
            {
                project.IsExpanded = true;
            }
        }
        // Lightweight filter: expand all when searching; full filtering arrives with the session index.
    }

    private void ByokMenu_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        var dialog = new ByokDialog(_config) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            dialog.ApplyTo(_config);
            _configStore.Save(_config);
            // AuthResolver reads _config live per request, so new keys take effect on the next
            // turn for every open session — no need to rebuild loops.
        }
    }

    private void ProxyMenu_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        var dialog = new ProxyDialog(_config) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _config.ProxyToken = string.IsNullOrWhiteSpace(dialog.ProxyToken) ? null : dialog.ProxyToken;
            _config.ProxyBaseUrl = string.IsNullOrWhiteSpace(dialog.ProxyBaseUrl) ? null : dialog.ProxyBaseUrl.Trim();
            _configStore.Save(_config);
            // AuthResolver reads _config live per request — new proxy settings apply on the next turn.
        }
    }

    private void AboutMenu_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        new AboutDialog(_configStore.ConfigPath) { Owner = this }.ShowDialog();
    }

    private void ModePill_Click(object sender, RoutedEventArgs e) => ModePopup.IsOpen = true;

    private void ModeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string wire)
        {
            _vm.Mode = AgentModeExtensions.Parse(wire);
            var session = _vm.Active;
            if (session?.Context is not null)
            {
                session.Context = session.Context.WithMode(_vm.Mode);
                UpdateSessionMeta(session);
            }
        }

        ModePopup.IsOpen = false;
    }

    private void ModelPill_Click(object sender, RoutedEventArgs e)
    {
        BuildModelMenu();
        ModelPopup.IsOpen = true;
    }

    private void ProviderMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string provider)
        {
            _vm.Provider = provider;
            _vm.Model = ModelCatalog.DefaultModelFor(provider) ?? _vm.Model;
            SaveModelToConfig();
            BuildModelMenu();
        }
    }

    private void BuildModelMenu()
    {
        ModelMenuLabel.Text = $"{Loc.T("Model")} · {_vm.Provider}";
        ModelListPanel.Children.Clear();
        foreach (var model in ModelCatalog.ModelsFor(_vm.Provider))
        {
            var b = new Button { Style = (Style)FindResource("MenuItemButtonStyle"), Content = model.Label, Tag = model.Id };
            b.Click += ModelMenu_Click;
            ModelListPanel.Children.Add(b);
        }

        var custom = new Button { Style = (Style)FindResource("MenuItemButtonStyle"), Content = "Custom…" };
        custom.Click += CustomModel_Click;
        ModelListPanel.Children.Add(custom);
    }

    private void ModelMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string model)
        {
            _vm.Model = model;
            SaveModelToConfig();
        }

        ModelPopup.IsOpen = false;
    }

    private void CustomModel_Click(object sender, RoutedEventArgs e)
    {
        ModelPopup.IsOpen = false;
        var dialog = new InputDialog("Custom model", $"Model id for provider '{_vm.Provider}':", _vm.Model) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Value))
        {
            _vm.Model = dialog.Value.Trim();
            SaveModelToConfig();
        }
    }

    private void SaveModelToConfig()
    {
        _config.DefaultProvider = _vm.Provider;
        _config.DefaultModel = _vm.Model;
        _configStore.Save(_config);
    }

    // ---- language (i18n) ----

    private void LanguageMenu_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        BuildLanguageMenu();
        LanguagePopup.IsOpen = true;
    }

    private void BuildLanguageMenu()
    {
        LanguageListPanel.Children.Clear();
        foreach (var (code, name) in LocalizationService.Available)
        {
            var button = new Button
            {
                Style = (Style)FindResource("MenuItemButtonStyle"),
                Tag = code,
                Content = BuildLanguageItemContent(name, isSelected: code == LocalizationService.CurrentLanguage),
            };
            button.Click += LanguageOption_Click;
            LanguageListPanel.Children.Add(button);
        }
    }

    private static StackPanel BuildLanguageItemContent(string label, bool isSelected)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new IconGlyph
        {
            Geometry = (Geometry)Application.Current.FindResource("IconCheck"),
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 9, 0),
            Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
            Visibility = isSelected ? Visibility.Visible : Visibility.Hidden,
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private void LanguageOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string code)
        {
            _config.Language = code;
            _configStore.Save(_config);
            LocalizationService.SetLanguage(code);
            BuildLanguageMenu();
        }

        LanguagePopup.IsOpen = false;
    }
}

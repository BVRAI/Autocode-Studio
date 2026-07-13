using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using AutoCode.Desktop.Controls;

namespace AutoCode.Desktop;

// Layout: sidebar/panel toggles + animation, theme apply, text scale, keep-awake.
public partial class MainWindow
{
    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        _vm.SidebarCollapsed = !_vm.SidebarCollapsed;
        AnimateColumn(SidebarCol, _vm.SidebarCollapsed ? 0 : 262, (Duration)FindResource("SidebarDuration"));
    }

    private void WorkspaceToggle_Click(object sender, RoutedEventArgs e) => TogglePanel("workspace");

    private void RunToggle_Click(object sender, RoutedEventArgs e) => TogglePanel("run");

    private void PlanToggle_Click(object sender, RoutedEventArgs e) => TogglePanel("plan");

    private void ClosePanel_Click(object sender, RoutedEventArgs e) => ClosePanel();

    private void WorkspaceTab_Click(object sender, RoutedEventArgs e) => _vm.PanelTab = "workspace";

    private void RunTab_Click(object sender, RoutedEventArgs e) => _vm.PanelTab = "run";

    private void PlanTab_Click(object sender, RoutedEventArgs e) => _vm.PanelTab = "plan";

    private void ChangesTab_Click(object sender, RoutedEventArgs e) => _vm.PanelTab = "changes";

    private void TogglePanel(string tab)
    {
        if (_vm.PanelOpen && _vm.PanelTab == tab)
        {
            ClosePanel();
        }
        else
        {
            OpenPanel(tab);
        }
    }

    private void OpenPanel(string tab)
    {
        _vm.PanelTab = tab;
        if (!_vm.PanelOpen)
        {
            _vm.PanelOpen = true;
            AnimateColumn(PanelCol, 372, (Duration)FindResource("PanelDuration"));
        }

        UpdateToggleStates();
    }

    private void ClosePanel()
    {
        if (_vm.PanelOpen)
        {
            _vm.PanelOpen = false;
            AnimateColumn(PanelCol, 0, (Duration)FindResource("PanelDuration"));
        }

        UpdateToggleStates();
    }

    private void UpdateToggleStates()
    {
        WorkspaceToggle.Tag = _vm.PanelOpen && _vm.PanelTab == "workspace";
        PlanToggle.Tag = _vm.PanelOpen && _vm.PanelTab == "plan";
    }

    private void AnimateColumn(ColumnDefinition col, double to, Duration duration)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            col.BeginAnimation(ColumnDefinition.WidthProperty, null);
            col.Width = new GridLength(to);
            return;
        }

        var anim = new GridLengthAnimation
        {
            From = col.ActualWidth,
            To = to,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        col.BeginAnimation(ColumnDefinition.WidthProperty, anim);
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e) => ToggleTheme();

    private void ToggleTheme()
    {
        _vm.Theme = _vm.IsDark ? "light" : "dark";
        _config.Theme = _vm.Theme;
        ApplyTheme(_vm.Theme);
        _configStore.Save(_config);
    }

    private void ApplyTheme(string? theme)
    {
        ThemeManager.Apply(theme);
        _vm.Theme = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
    }

    private void KeepAwakeToggle_Click(object sender, RoutedEventArgs e)
    {
        _config.KeepAwakeEnabled = !_config.KeepAwakeEnabled;
        ApplyKeepAwake(_config.KeepAwakeEnabled);
        _vm.IsKeepAwake = _config.KeepAwakeEnabled;
        _configStore.Save(_config);
    }

    private void FontSizeToggle_Click(object sender, RoutedEventArgs e)
    {
        SetTextScale(_config.TextScale > 1.0 ? 1.0 : 1.15);
        _vm.IsLargeFont = _config.TextScale > 1.0;
    }

    private void SetTextScale(double value)
    {
        _config.TextScale = Math.Clamp(Math.Round(value, 2), 0.85, 1.35);
        ApplyTextScale(_config.TextScale);
        _configStore.Save(_config);
    }

    private void ApplyTextScale(double scale)
    {
        scale = Math.Clamp(scale <= 0 ? 1.0 : scale, 0.85, 1.35);
        // App-level so {DynamicResource BodyFontSize} resolves inside merged Conversation templates.
        Application.Current.Resources["BodyFontSize"] = 14.5 * scale;
    }

    private static void ApplyKeepAwake(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var flags = enabled ? (ExecutionState.EsContinuous | ExecutionState.EsSystemRequired) : ExecutionState.EsContinuous;
        SetThreadExecutionState(flags);
    }
}

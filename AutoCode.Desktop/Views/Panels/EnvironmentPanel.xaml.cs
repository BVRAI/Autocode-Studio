using System;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AutoCode.Desktop.ViewModels;

namespace AutoCode.Desktop.Views.Panels;

public partial class EnvironmentPanel : UserControl
{
    public EnvironmentPanel() => InitializeComponent();

    // Grip drag → app-level offset on the façade (bound to the panel's TranslateTransform).
    // Clamped so the panel can't be dragged fully out of reach.
    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        vm.EnvPanelOffsetX = Math.Clamp(vm.EnvPanelOffsetX + e.HorizontalChange, -520, 20);
        vm.EnvPanelOffsetY = Math.Clamp(vm.EnvPanelOffsetY + e.VerticalChange, -4, 620);
    }
}

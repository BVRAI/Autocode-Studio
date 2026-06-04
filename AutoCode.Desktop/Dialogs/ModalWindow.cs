using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AutoCode.Desktop.Controls;

namespace AutoCode.Desktop;

/// <summary>
/// Borderless modal: a dimmed scrim over the owner with a centered themed card
/// (header title/subtitle + close, scrollable body, footer with right-aligned buttons).
/// </summary>
public abstract class ModalWindow : Window
{
    protected StackPanel Body { get; } = new() { Margin = new Thickness(18) };
    protected StackPanel Footer { get; } = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

    private readonly Border _card;

    protected ModalWindow(string title, string? subtitle, double cardWidth)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = title;

        var scrim = new Border { Background = Res<Brush>("ScrimBrush") };

        var header = new Grid { Margin = new Thickness(18, 18, 18, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock { Text = title, FontFamily = Res<FontFamily>("UiFontFamily"), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("TextBrush") });
        if (!string.IsNullOrEmpty(subtitle))
        {
            titleStack.Children.Add(new TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontSize = 12.5, Foreground = Res<Brush>("Text3Brush") });
        }

        header.Children.Add(titleStack);
        var close = new Button { Style = Res<Style>("IconButtonStyle"), VerticalAlignment = VerticalAlignment.Top };
        close.Content = new IconGlyph { Geometry = Res<Geometry>("IconX"), Width = 16, Height = 16 };
        close.Click += (_, _) => { DialogResult = false; Close(); };
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        var headerBorder = new Border { BorderBrush = Res<Brush>("BorderBrush2"), BorderThickness = new Thickness(0, 0, 0, 1), Child = header };

        var bodyScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Body };

        var footerBorder = new Border
        {
            Background = Res<Brush>("Surface2Brush"),
            BorderBrush = Res<Brush>("BorderBrush2"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(18, 14, 18, 14),
            Child = Footer,
        };

        var dock = new DockPanel();
        DockPanel.SetDock(headerBorder, Dock.Top);
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        dock.Children.Add(headerBorder);
        dock.Children.Add(footerBorder);
        dock.Children.Add(bodyScroll);

        _card = new Border
        {
            Width = cardWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Res<Brush>("SurfaceBrush"),
            BorderBrush = Res<Brush>("BorderStrongBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Effect = Res<System.Windows.Media.Effects.Effect>("ModalShadow"),
            Child = dock,
            ClipToBounds = true,
        };

        var root = new Grid();
        root.Children.Add(scrim);
        root.Children.Add(_card);
        Content = root;

        Loaded += OnLoadedPosition;
    }

    private void OnLoadedPosition(object? sender, RoutedEventArgs e)
    {
        if (Owner is { } owner)
        {
            Rect bounds;
            if (owner.WindowState == WindowState.Maximized)
            {
                bounds = SystemParameters.WorkArea;
            }
            else
            {
                bounds = new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
            }

            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
        }

        _card.MaxHeight = Math.Max(240, Height - 48);
        _card.MaxWidth = Math.Max(320, Width - 48);
    }

    protected void AddField(string label, string? envHint, FrameworkElement input)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(new TextBlock { Text = label, FontFamily = Res<FontFamily>("UiFontFamily"), FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Res<Brush>("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(envHint))
        {
            head.Children.Add(new TextBlock { Text = envHint, Margin = new Thickness(8, 0, 0, 0), FontFamily = Res<FontFamily>("MonoFontFamily"), FontSize = 11, Foreground = Res<Brush>("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center });
        }

        var field = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        field.Children.Add(head);
        field.Children.Add(input);
        Body.Children.Add(field);
    }

    protected void AddBodyText(string text)
        => Body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontFamily = Res<FontFamily>("UiFontFamily"), FontSize = 13, Foreground = Res<Brush>("Text2Brush"), Margin = new Thickness(0, 0, 0, 12), LineHeight = 20 });

    protected Button AddFooterButton(string text, bool primary, bool danger = false)
    {
        var styleKey = primary ? "PrimaryButtonStyle" : danger ? "DangerButtonStyle" : "SmallGhostButtonStyle";
        var b = new Button { Content = text, Style = Res<Style>(styleKey), MinWidth = 84, Margin = new Thickness(Footer.Children.Count == 0 ? 0 : 8, 0, 0, 0) };
        Footer.Children.Add(b);
        return b;
    }

    protected TextBox MakeTextBox(string? value, string? placeholder = null)
        => new() { Text = value ?? "", Style = Res<Style>("DialogTextBoxStyle"), Tag = placeholder };

    protected PasswordBox MakePasswordBox(string? value)
    {
        var box = new PasswordBox { Style = Res<Style>("DialogPasswordStyle") };
        if (!string.IsNullOrEmpty(value)) { box.Password = value; }
        return box;
    }

    protected static T Res<T>(string key) => (T)Application.Current.FindResource(key);
}

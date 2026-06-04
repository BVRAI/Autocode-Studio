using System.Windows;
using System.Windows.Controls;

namespace AutoCode.Desktop.Misc;

/// <summary>
/// Attached property that fills a <see cref="TextBlock"/>'s Inlines from a markup string
/// (TextBlock.Inlines is not directly bindable). Used for assistant paragraph segments.
/// </summary>
public static class InlineBehavior
{
    public static readonly DependencyProperty MarkupProperty =
        DependencyProperty.RegisterAttached(
            "Markup",
            typeof(string),
            typeof(InlineBehavior),
            new PropertyMetadata(null, OnMarkupChanged));

    public static string? GetMarkup(DependencyObject obj) => (string?)obj.GetValue(MarkupProperty);

    public static void SetMarkup(DependencyObject obj, string? value) => obj.SetValue(MarkupProperty, value);

    private static void OnMarkupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb)
        {
            return;
        }

        tb.Inlines.Clear();
        if (e.NewValue is string markup && markup.Length > 0)
        {
            foreach (var inline in InlineParser.BuildInlines(markup))
            {
                tb.Inlines.Add(inline);
            }
        }
    }
}

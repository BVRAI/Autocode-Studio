using System.Windows;

namespace AutoCode.Desktop;

/// <summary>
/// Runtime light/dark theming by swapping the merged theme <see cref="ResourceDictionary"/>.
/// All themed brushes are referenced via DynamicResource, so a swap re-resolves them with
/// no mutation of any in-use (frozen) brush — the cause of the historical SetBrush crash.
/// </summary>
public static class ThemeManager
{
    // A key that exists only in the theme dictionaries (Light.xaml / Dark.xaml),
    // used to locate and remove the currently merged theme dictionary.
    private const string SentinelKey = "TitlebarBrush";

    public static bool IsDark { get; private set; }

    public static void Apply(string? theme)
    {
        var dark = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase);
        IsDark = dark;

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var source = new Uri(
            dark
                ? "pack://application:,,,/AutoCode.Desktop;component/Themes/Dark.xaml"
                : "pack://application:,,,/AutoCode.Desktop;component/Themes/Light.xaml",
            UriKind.Absolute);

        var next = new ResourceDictionary { Source = source };

        var dicts = app.Resources.MergedDictionaries;
        for (var i = dicts.Count - 1; i >= 0; i--)
        {
            if (dicts[i].Contains(SentinelKey))
            {
                dicts.RemoveAt(i);
            }
        }

        dicts.Add(next);
    }
}

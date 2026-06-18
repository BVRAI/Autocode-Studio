using System.Windows;

namespace AutoCode.Desktop;

/// <summary>
/// Code-side translation facade. Mirrors XAML's {DynamicResource L_Key}. Returns the key
/// itself if no resource is found (visible-but-safe fallback). See LocalizationService.
/// </summary>
public static class Loc
{
    public static string T(string key)
        => Application.Current?.TryFindResource("L_" + key) as string ?? key;

    public static string F(string key, params object[] args)
        => string.Format(T(key), args);
}

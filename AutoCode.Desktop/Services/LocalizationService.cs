using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace AutoCode.Desktop;

/// <summary>
/// Runtime i18n (mirrors Automax V6). String tables live in Strings/{code}.json (flat
/// "Key": "value"). en.json is the baseline; the selected language overlays it, with missing
/// keys falling back to English. Merges a ResourceDictionary of "L_{key}" entries into the
/// app resources so XAML uses {DynamicResource L_Key} and code uses Loc.T(...). Switching
/// language is live (no restart) because DynamicResource re-resolves when the dictionary swaps.
/// Only user-facing UI strings belong here — strings the engine sends to the LLM stay English.
/// </summary>
public static class LocalizationService
{
    public static event Action? LanguageChanged;

    /// <summary>The 11 ecosystem languages (code, native display name).</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Available =
    [
        ("en", "English"),
        ("fr", "Français"),
        ("es", "Español"),
        ("de", "Deutsch"),
        ("ja", "日本語"),
        ("ko", "한국어"),
        ("zh-Hans", "简体中文"),
        ("zh-Hant", "繁體中文"),
        ("yue", "粵語"),
        ("hi", "हिन्दी"),
        ("pa", "ਪੰਜਾਬੀ"),
    ];

    public static string CurrentLanguage { get; private set; } = "en";

    private static Dictionary<string, string> _baseline = new(StringComparer.Ordinal);
    private static ResourceDictionary? _merged;

    /// <summary>Load the baseline + the preferred (or OS-derived) language and merge it in.</summary>
    public static void Initialize(string? preferred)
    {
        _baseline = Load("en");
        ApplyInternal(Resolve(preferred));
    }

    /// <summary>Switch language at runtime and notify listeners.</summary>
    public static void SetLanguage(string code)
    {
        ApplyInternal(Resolve(code));
        LanguageChanged?.Invoke();
    }

    private static string Resolve(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code) && Available.Any(a => a.Code == code))
        {
            return code!;
        }

        var ui = CultureInfo.CurrentUICulture;
        foreach (var (c, _) in Available)
        {
            if (string.Equals(c, ui.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c, ui.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
        }

        return "en";
    }

    private static void ApplyInternal(string code)
    {
        CurrentLanguage = code;
        var overlay = code == "en" ? _baseline : Load(code);

        var dict = new ResourceDictionary();
        foreach (var (k, v) in _baseline)
        {
            dict["L_" + k] = v;
        }

        foreach (var (k, v) in overlay)
        {
            dict["L_" + k] = v;
        }

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (_merged is not null)
        {
            app.Resources.MergedDictionaries.Remove(_merged);
        }

        app.Resources.MergedDictionaries.Add(dict);
        _merged = dict;
    }

    private static Dictionary<string, string> Load(string code)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Strings", code + ".json");
            if (!File.Exists(path))
            {
                return new(StringComparer.Ordinal);
            }

            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                ?? new(StringComparer.Ordinal);
        }
        catch
        {
            return new(StringComparer.Ordinal);
        }
    }
}

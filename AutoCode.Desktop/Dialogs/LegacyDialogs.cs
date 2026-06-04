using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AutoCode.Engine.Agent;
using AutoCode.Engine.Auth;
using AutoCode.Engine.Tools;

namespace AutoCode.Desktop;

// Styled XAML-equivalent modal dialogs (built on ModalWindow), preserving the original public surface.

public sealed class ByokDialog : ModalWindow
{
    private readonly PasswordBox _anthropic;
    private readonly PasswordBox _openAi;
    private readonly PasswordBox _xai;
    private readonly PasswordBox _openRouter;
    private readonly PasswordBox _brave;

    public ByokDialog(AutocodeConfig config)
        : base("Bring your own keys", "Environment variables take precedence. Saved keys are written to ~/.autocode-gui/config.json.", 500)
    {
        _anthropic = MakePasswordBox(config.ApiKeys.TryGetValue("anthropic", out var a) ? a : null);
        _openAi = MakePasswordBox(config.ApiKeys.TryGetValue("openai", out var o) ? o : null);
        _xai = MakePasswordBox(config.ApiKeys.TryGetValue("xai", out var x) ? x : null);
        _openRouter = MakePasswordBox(config.ApiKeys.TryGetValue("openrouter", out var r) ? r : null);
        _brave = MakePasswordBox(config.ApiKeys.TryGetValue("brave", out var b) ? b : null);

        AddField("Anthropic", "ANTHROPIC_API_KEY", _anthropic);
        AddField("OpenAI", "OPENAI_API_KEY", _openAi);
        AddField("xAI", "XAI_API_KEY", _xai);
        AddField("OpenRouter", "OPENROUTER_API_KEY", _openRouter);
        AddField("Brave Search", "BRAVE_API_KEY", _brave);

        AddFooterButton("Cancel", primary: false).Click += (_, _) => { DialogResult = false; Close(); };
        AddFooterButton("Save keys", primary: true).Click += (_, _) => { DialogResult = true; Close(); };
    }

    public void ApplyTo(AutocodeConfig config)
    {
        SetKey(config, "anthropic", _anthropic.Password);
        SetKey(config, "openai", _openAi.Password);
        SetKey(config, "xai", _xai.Password);
        SetKey(config, "openrouter", _openRouter.Password);
        SetKey(config, "brave", _brave.Password);
    }

    private static void SetKey(AutocodeConfig config, string provider, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            config.ApiKeys.Remove(provider);
        }
        else
        {
            config.ApiKeys[provider] = value;
        }
    }
}

public sealed class ProxyDialog : ModalWindow
{
    private readonly PasswordBox _token;
    private readonly TextBox _url;

    public ProxyDialog(AutocodeConfig config)
        : base("Proxy access", "Route provider calls through a configurable proxy, falling back to AUTOCODE_GUI_PROXY_* env vars. Environment variables still take precedence.", 500)
    {
        _url = MakeTextBox(config.ProxyBaseUrl);
        _token = MakePasswordBox(config.ProxyToken);
        AddField("Proxy URL", "AUTOCODE_GUI_PROXY_URL", _url);
        AddField("Proxy token", "AUTOCODE_GUI_PROXY_TOKEN", _token);
        AddBodyText("Requests route to {proxyBaseUrl}/v1/{provider} — e.g. /v1/anthropic/messages.");

        AddFooterButton("Cancel", primary: false).Click += (_, _) => { DialogResult = false; Close(); };
        AddFooterButton("Save", primary: true).Click += (_, _) => { DialogResult = true; Close(); };
    }

    public string ProxyToken => _token.Password;

    public string ProxyBaseUrl => _url.Text;
}

public sealed class AboutDialog : ModalWindow
{
    public AboutDialog(string configPath)
        : base("AutoCode Studio", "Automax agentic code shell", 460)
    {
        AddBodyText("An independent C# desktop agent shell with project-scoped tools, approvals, BYOK/proxy routing, and an agentic coding workflow.");

        var meta = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        meta.Children.Add(MetaRow("version", "0.4.0"));
        meta.Children.Add(MetaRow("engine", "AutoCode.Engine"));
        meta.Children.Add(MetaRow("runtime", ".NET 8 · WPF"));
        meta.Children.Add(MetaRow("config", configPath));
        Body.Children.Add(meta);

        AddFooterButton("Close", primary: true).Click += (_, _) => { DialogResult = true; Close(); };
    }

    private static FrameworkElement MetaRow(string key, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var k = new TextBlock { Text = key, FontFamily = Res<FontFamily>("MonoFontFamily"), FontSize = 12, Foreground = Res<Brush>("TextFaintBrush") };
        var v = new TextBlock { Text = value, FontFamily = Res<FontFamily>("MonoFontFamily"), FontSize = 12, Foreground = Res<Brush>("Text2Brush"), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(v, 1);
        grid.Children.Add(k);
        grid.Children.Add(v);
        return grid;
    }
}

public sealed class InputDialog : ModalWindow
{
    private readonly TextBox _box;

    public InputDialog(string title, string prompt, string initial)
        : base(title, prompt, 420)
    {
        _box = MakeTextBox(initial);
        Body.Children.Add(_box);
        AddFooterButton("Cancel", primary: false).Click += (_, _) => { DialogResult = false; Close(); };
        AddFooterButton("OK", primary: true).Click += (_, _) => { DialogResult = true; Close(); };
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public string Value => _box.Text;
}

public sealed class ChoiceDialog : ModalWindow
{
    private readonly AskUserRequest _request;
    private readonly List<CheckBox> _checks = [];
    private readonly List<RadioButton> _radios = [];

    public ChoiceDialog(AskUserRequest request)
        : base("Question", request.Question, 460)
    {
        _request = request;
        var options = new StackPanel();
        for (var i = 0; i < request.Options.Count; i++)
        {
            if (request.MultiSelect)
            {
                var check = new CheckBox { Content = request.Options[i], Margin = new Thickness(0, 5, 0, 5), Foreground = Res<Brush>("TextBrush") };
                _checks.Add(check);
                options.Children.Add(check);
            }
            else
            {
                var radio = new RadioButton { Content = request.Options[i], Margin = new Thickness(0, 5, 0, 5), IsChecked = i == 0, Foreground = Res<Brush>("TextBrush") };
                _radios.Add(radio);
                options.Children.Add(radio);
            }
        }

        Body.Children.Add(options);
        AddFooterButton("Cancel", primary: false).Click += (_, _) => { DialogResult = false; Close(); };
        AddFooterButton("OK", primary: true).Click += (_, _) => { DialogResult = true; Close(); };
    }

    public IReadOnlyList<int> SelectedIndexes
    {
        get
        {
            if (_request.MultiSelect)
            {
                return _checks.Select((c, i) => c.IsChecked == true ? i : -1).Where(i => i >= 0).ToList();
            }

            var idx = _radios.FindIndex(r => r.IsChecked == true);
            return idx >= 0 ? [idx] : [];
        }
    }
}

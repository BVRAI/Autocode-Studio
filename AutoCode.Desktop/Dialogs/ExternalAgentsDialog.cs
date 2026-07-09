using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AutoCode.Engine.Auth;
using AutoCode.Engine.Backends;

namespace AutoCode.Desktop;

/// <summary>
/// Settings dialog for how external CLI agents (Claude Code / Codex) authenticate: the CLI's own
/// subscription login (default — the child env is scrubbed of API keys) or a per-agent API key
/// injected into the child env. Mirrors the ByokDialog pattern.
/// </summary>
public sealed class ExternalAgentsDialog : ModalWindow
{
    private readonly AgentAuthSection _claude;
    private readonly AgentAuthSection _codex;

    public ExternalAgentsDialog(AutocodeConfig config)
        : base("External agent auth",
               "How the Claude Code and Codex CLIs authenticate. Subscription uses the CLI's own login (claude login / codex login) — usually the cheaper option.",
               500)
    {
        _claude = AddAgentSection(config, "claude-code", "Claude Code", "ANTHROPIC_API_KEY");
        _codex = AddAgentSection(config, "codex", "Codex", "OPENAI_API_KEY");

        AddFooterButton("Cancel", primary: false).Click += (_, _) => { DialogResult = false; Close(); };
        AddFooterButton("Save", primary: true).Click += (_, _) => { DialogResult = true; Close(); };
    }

    public void ApplyTo(AutocodeConfig config)
    {
        Apply(config, "claude-code", _claude);
        Apply(config, "codex", _codex);
    }

    private static void Apply(AutocodeConfig config, string agentId, AgentAuthSection section)
    {
        var apiKeyMode = section.ApiKeyRadio.IsChecked == true;
        var key = section.KeyBox.Password;
        if (!apiKeyMode || string.IsNullOrWhiteSpace(key))
        {
            // Subscription is the default; no entry needed.
            config.ExternalAgents.Remove(agentId);
            return;
        }

        config.ExternalAgents[agentId] = new ExternalAgentAuthConfig
        {
            Mode = ExternalAgentAuth.ApiKeyMode,
            ApiKey = key,
        };
    }

    private AgentAuthSection AddAgentSection(AutocodeConfig config, string agentId, string label, string envName)
    {
        config.ExternalAgents.TryGetValue(agentId, out var cfg);
        var apiKeyMode = cfg?.Mode == ExternalAgentAuth.ApiKeyMode && !string.IsNullOrWhiteSpace(cfg.ApiKey);

        var subscription = new RadioButton
        {
            Content = "Subscription (CLI login)",
            GroupName = agentId,
            IsChecked = !apiKeyMode,
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = Res<Brush>("TextBrush"),
        };
        var apiKey = new RadioButton
        {
            Content = "API key",
            GroupName = agentId,
            IsChecked = apiKeyMode,
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = Res<Brush>("TextBrush"),
        };
        var keyBox = MakePasswordBox(cfg?.ApiKey);
        keyBox.IsEnabled = apiKeyMode;
        subscription.Checked += (_, _) => keyBox.IsEnabled = false;
        apiKey.Checked += (_, _) => keyBox.IsEnabled = true;

        var section = new StackPanel();
        section.Children.Add(subscription);
        section.Children.Add(apiKey);
        section.Children.Add(keyBox);
        AddField(label, envName, section);

        return new AgentAuthSection(apiKey, keyBox);
    }

    private sealed record AgentAuthSection(RadioButton ApiKeyRadio, PasswordBox KeyBox);
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AutoCode.Desktop.Controls;
using AutoCode.Desktop.ViewModels;
using AutoCode.Desktop.Voice;
using AutoCode.Engine.Auth;

namespace AutoCode.Desktop;

// Voice / dictation: mic recording lifecycle, transcription routing, provider picker.
public partial class MainWindow
{
    private AudioRecorder? _recorder;
    private CancellationTokenSource? _voiceCts;
    private const int MinWavBytes = 8000; // ~0.25s of 16kHz/16-bit/mono + header; ignore shorter clips

    private async void MicButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_vm.Voice)
        {
            case VoiceState.Recording:
                await StopAndTranscribeAsync();
                break;
            case VoiceState.Transcribing:
                break; // busy — ignore re-entry
            default:
                StartRecording();
                break;
        }
    }

    private void StartRecording()
    {
        try
        {
            _recorder = new AudioRecorder();
            _recorder.Start();
            _vm.Voice = VoiceState.Recording;
        }
        catch (Exception ex)
        {
            _recorder?.Dispose();
            _recorder = null;
            _vm.Voice = VoiceState.Idle;
            ShowVoiceError(ex);
        }
    }

    private async Task StopAndTranscribeAsync()
    {
        if (_recorder is null)
        {
            _vm.Voice = VoiceState.Idle;
            return;
        }

        byte[] wav;
        try
        {
            wav = await _recorder.StopAsync();
        }
        catch (Exception ex)
        {
            ShowVoiceError(ex);
            _vm.Voice = VoiceState.Idle;
            return;
        }
        finally
        {
            _recorder.Dispose();
            _recorder = null;
        }

        if (wav.Length < MinWavBytes)
        {
            _vm.Voice = VoiceState.Idle; // silent / too short
            return;
        }

        _vm.Voice = VoiceState.Transcribing;
        _voiceCts = new CancellationTokenSource();
        var router = new TranscriptionRouter(new AuthResolver(_config, ProxyTokenProvider));
        var option = router.ResolveSelection(_config.TranscriptionProvider);

        IProgress<string>? progress = option.Streaming
            ? new Progress<string>(delta => PromptBox.AppendText(delta))
            : null;

        try
        {
            var text = await router.TranscribeAsync(option, wav, progress, _voiceCts.Token);
            await FinishTranscriptAsync(text, streamedAlready: option.Streaming);
        }
        catch (OperationCanceledException)
        {
            // cancelled (e.g. app closing) — nothing to surface
        }
        catch (Exception ex)
        {
            ShowVoiceError(ex);
        }
        finally
        {
            _vm.Voice = VoiceState.Idle;
            _voiceCts?.Dispose();
            _voiceCts = null;
        }
    }

    private async Task FinishTranscriptAsync(string? text, bool streamedAlready)
    {
        text = (text ?? "").Trim();

        if (_config.AutoSubmitVoice && IsPhantomTranscript(text))
        {
            if (streamedAlready)
            {
                PromptBox.Clear(); // drop the streamed hallucination
            }

            return;
        }

        if (text.Length == 0)
        {
            return;
        }

        if (_config.AutoSubmitVoice)
        {
            if (!streamedAlready)
            {
                PromptBox.Text = text;
            }

            await SendPromptAsync();
            return;
        }

        // Manual mode: leave the text in the box for review.
        if (!streamedAlready)
        {
            if (PromptBox.Text.Length > 0 && !PromptBox.Text.EndsWith(' '))
            {
                PromptBox.AppendText(" ");
            }

            PromptBox.AppendText(text);
        }

        PromptBox.CaretIndex = PromptBox.Text.Length;
        PromptBox.Focus();
    }

    private void VoiceCaret_Click(object sender, RoutedEventArgs e)
    {
        BuildVoiceMenu();
        VoicePopup.IsOpen = true;
    }

    private void BuildVoiceMenu()
    {
        var router = new TranscriptionRouter(new AuthResolver(_config, ProxyTokenProvider));
        var selected = router.ResolveSelection(_config.TranscriptionProvider);

        VoiceListPanel.Children.Clear();
        foreach (var option in router.AllOptions)
        {
            var available = router.IsAvailable(option);
            var button = new Button
            {
                Style = (Style)FindResource("MenuItemButtonStyle"),
                Tag = option.Id,
                IsEnabled = available,
                Opacity = available ? 1.0 : 0.45,
                ToolTip = available ? null : "Add an API key (Settings ▸ Bring your own keys) or enable the proxy",
                Content = BuildVoiceItemContent(option.DisplayName, isSelected: available && option.Id == selected.Id),
            };
            button.Click += VoiceOption_Click;
            VoiceListPanel.Children.Add(button);
        }

        AutoSubmitCheck.Visibility = _config.AutoSubmitVoice ? Visibility.Visible : Visibility.Hidden;
    }

    private static StackPanel BuildVoiceItemContent(string label, bool isSelected)
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
        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private void VoiceOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string id)
        {
            _config.TranscriptionProvider = id;
            _configStore.Save(_config);
            BuildVoiceMenu();
        }

        VoicePopup.IsOpen = false;
    }

    private void AutoSubmitToggle_Click(object sender, RoutedEventArgs e)
    {
        _config.AutoSubmitVoice = !_config.AutoSubmitVoice;
        _configStore.Save(_config);
        AutoSubmitCheck.Visibility = _config.AutoSubmitVoice ? Visibility.Visible : Visibility.Hidden;
    }

    private void ShowVoiceError(Exception ex)
    {
        var detail = ex is InvalidOperationException ? ex.Message : $"Voice failed: {ex.Message}";
        _vm.Conversation.Add(new NoticeBlock { Title = "Voice", Detail = detail });
        ScrollChatToEnd();
    }

    private static readonly string[] PhantomPhrases =
    {
        "thank you", "thanks", "thanks for watching", "thank you for watching",
        "please subscribe", "you", "bye", "okay", "ok",
    };

    private static bool IsPhantomTranscript(string text)
    {
        var normalized = text.Trim().TrimEnd('.', '!', '?', ' ').ToLowerInvariant();
        return normalized.Length == 0 || PhantomPhrases.Contains(normalized);
    }
}

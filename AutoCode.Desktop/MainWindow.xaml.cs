using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AutoCode.Desktop.Auth;
using AutoCode.Desktop.Controls;
using AutoCode.Desktop.Misc;
using AutoCode.Desktop.ViewModels;
using AutoCode.Desktop.Voice;
using AutoCode.Engine.Agent;
using AutoCode.Engine.Auth;
using AutoCode.Engine.Session;
using AutoCode.Engine.Tools;
using MessageBox = System.Windows.MessageBox;

namespace AutoCode.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly ConfigStore _configStore = new();
    private readonly FirebaseAuthService _firebase = new();
    private readonly SubscriptionService _subscriptions = new();
    private AutocodeConfig _config = new();
    private SessionContext? _context;
    private AgentLoop? _loop;
    private CancellationTokenSource? _runCts;
    private TaskCompletionSource<ApprovalDecision>? _approvalCompletion;

    // voice / dictation
    private AudioRecorder? _recorder;
    private CancellationTokenSource? _voiceCts;
    private const int MinWavBytes = 8000; // ~0.25s of 16kHz/16-bit/mono + header; ignore shorter clips

    // conversation turn state
    private WorkedForBlock? _currentWorked;
    private DiffCardBlock? _pendingDiff;
    private readonly Queue<(WorkedStep Step, TimelineItemVM Item)> _runningTools = new();
    private readonly HashSet<string> _modifiedFiles = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> MutatingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "edit_file", "write_file", "create_directory", "delete_path", "run_shell",
    };

    private static readonly Dictionary<string, string[]> ProviderModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] = ["claude-sonnet-4.5", "claude-opus-4.1", "claude-haiku-4", "claude-opus-4-7"],
        ["openai"] = ["gpt-5.2", "gpt-5.2-mini", "o4"],
        ["xai"] = ["grok-code-fast-1", "grok-4", "grok-4-mini"],
        ["openrouter"] = ["auto", "qwen3-coder", "deepseek-v3.2"],
    };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.CopyCommand = new RelayCommand(p => CopyToClipboard(p as string));
        _vm.ReviewCommand = new RelayCommand(_ => OpenPanel("run"));
        _vm.OpenFileCommand = new RelayCommand(p => OpenFileNode(p as FileNode));
        InlineParser.FileRefRequested += OnFileRefRequested;
        _firebase.StateChanged += () => Dispatcher.Invoke(RefreshAccountUi);
    }

    // ============================= lifecycle =============================

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _config = _configStore.Load();
        _vm.Provider = _config.DefaultProvider ?? "anthropic";
        _vm.Model = _config.DefaultModel ?? "claude-opus-4-7";
        _vm.Mode = AgentMode.Default;
        _vm.Theme = string.Equals(_config.Theme, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
        _vm.UseProxy = _config.UseProxy;
        ApplyTheme(_vm.Theme);
        ApplyTextScale(_config.TextScale);
        _vm.IsLargeFont = _config.TextScale > 1.0;
        ApplyKeepAwake(_config.KeepAwakeEnabled);
        _vm.IsKeepAwake = _config.KeepAwakeEnabled;
        _vm.ProjectRoot = DefaultProjectRoot();

        try { await _firebase.TryRestoreSessionAsync(); } catch { /* offline / corrupt session */ }
        RefreshAccountUi();

        StartNewSession(clearChat: true);
        SendButton.IsEnabled = false;

        // Optional sign-in prompt on first launch (skippable -> guest). Shown once.
        if (!_firebase.IsAuthenticated && !_config.LoginPromptSeen)
        {
            ShowLoginDialog();
        }
    }

    private void ShowLoginDialog()
    {
        new LoginDialog(_firebase) { Owner = this }.ShowDialog();
        _config.LoginPromptSeen = true;
        _configStore.Save(_config);
        RefreshAccountUi();
    }

    // ============================= account =============================

    private void RefreshAccountUi()
    {
        _vm.IsSignedIn = _firebase.IsAuthenticated;
        _vm.AccountEmail = _firebase.CurrentEmail ?? "";
        _vm.AccountPhotoUrl = string.IsNullOrEmpty(_firebase.CurrentPhotoUrl) ? null : _firebase.CurrentPhotoUrl;

        _config.AccountEmail = _firebase.CurrentEmail;
        _config.AccountDisplayName = _firebase.CurrentDisplayName;
        _config.AccountPhotoUrl = _firebase.CurrentPhotoUrl;
        _configStore.Save(_config);

        if (_firebase.IsAuthenticated)
        {
            _ = RefreshSubscriptionAsync();
        }
        else
        {
            _vm.IsSubscriber = false;
        }
    }

    private async Task RefreshSubscriptionAsync()
    {
        try
        {
            var token = await _firebase.GetIdTokenAsync();
            var sub = await _subscriptions.GetStatusAsync(_firebase.CurrentUid, token);
            await Dispatcher.InvokeAsync(() => _vm.IsSubscriber = sub.IsActive);
        }
        catch
        {
            // Leave IsSubscriber as-is on transient failure.
        }
    }

    private string? ProxyTokenProvider()
        => _vm.UseProxy && _firebase.IsAuthenticated && _vm.IsSubscriber ? _firebase.CurrentIdToken : null;

    private void SignIn_Click(object sender, RoutedEventArgs e) => ShowLoginDialog();

    private void UseProxyToggle_Click(object sender, RoutedEventArgs e)
    {
        _vm.UseProxy = !_vm.UseProxy;
        _config.UseProxy = _vm.UseProxy;
        _configStore.Save(_config);
        UseProxyCheck.Visibility = _vm.UseProxy ? Visibility.Visible : Visibility.Hidden;
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        await _firebase.SignOutAsync();
        RefreshAccountUi();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _runCts?.Cancel();
        _voiceCts?.Cancel();
        _recorder?.Dispose();
        ApplyKeepAwake(false);
        RunShellTool.StopBackgroundProcesses();
    }

    // ============================= window chrome =============================

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        MaxIcon.Geometry = (Geometry)FindResource(maximized ? "IconRestore" : "IconMax");
    }

    // Constrain the maximized borderless window to the monitor work area, so it does not
    // cover (and get clipped behind) the taskbar. Without this, WPF maximizes a
    // WindowStyle=None window to the full monitor + ~7px overflow on every edge.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        const int MONITOR_DEFAULTTONEAREST = 0x00000002;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var work = info.rcWork;       // physical px (app is PerMonitorV2)
                var bounds = info.rcMonitor;  // physical px

                // Maximized rect = work area, positioned relative to the monitor origin.
                mmi.ptMaxPosition.X = work.Left - bounds.Left;
                mmi.ptMaxPosition.Y = work.Top - bounds.Top;
                mmi.ptMaxSize.X = work.Right - work.Left;
                mmi.ptMaxSize.Y = work.Bottom - work.Top;

                // handled=true suppresses WPF's own MinWidth/MinHeight enforcement, so we must
                // re-apply it here. Convert DIP minimums to physical px for THIS monitor's DPI;
                // recomputed every message so dragging across monitors of differing DPI stays correct.
                var scale = GetDpiForWindow(hwnd) / 96.0;
                mmi.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * scale);
                mmi.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * scale);
            }
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    // ============================= layout toggles =============================

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        _vm.SidebarCollapsed = !_vm.SidebarCollapsed;
        AnimateColumn(SidebarCol, _vm.SidebarCollapsed ? 0 : 262, (Duration)FindResource("SidebarDuration"));
    }

    private void WorkspaceToggle_Click(object sender, RoutedEventArgs e) => TogglePanel("workspace");

    private void RunToggle_Click(object sender, RoutedEventArgs e) => TogglePanel("run");

    private void ClosePanel_Click(object sender, RoutedEventArgs e) => ClosePanel();

    private void WorkspaceTab_Click(object sender, RoutedEventArgs e) => _vm.PanelTab = "workspace";

    private void RunTab_Click(object sender, RoutedEventArgs e) => _vm.PanelTab = "run";

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
        RunToggle.Tag = _vm.PanelOpen && _vm.PanelTab == "run";
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

    // ============================= theme =============================

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

    // ============================= settings / session popups =============================

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        UseProxyCheck.Visibility = _vm.UseProxy ? Visibility.Visible : Visibility.Hidden;
        SettingsPopup.IsOpen = true;
    }

    private void SessionMenu_Click(object sender, RoutedEventArgs e) => SessionPopup.IsOpen = true;

    private void SearchToggle_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Visibility = SearchBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SearchBox.Visibility == Visibility.Visible)
        {
            SearchBox.Focus();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        foreach (var project in _vm.Projects)
        {
            var any = false;
            foreach (var s in project.Sessions)
            {
                any = true;
                _ = s;
            }

            if (any && q.Length > 0)
            {
                project.IsExpanded = true;
            }
        }
        // Lightweight filter: expand all when searching; full filtering arrives with the session index.
    }

    private void ByokMenu_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        var dialog = new ByokDialog(_config) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            dialog.ApplyTo(_config);
            _configStore.Save(_config);
            StartNewSession(clearChat: false);
        }
    }

    private void ProxyMenu_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        var dialog = new ProxyDialog(_config) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _config.ProxyToken = string.IsNullOrWhiteSpace(dialog.ProxyToken) ? null : dialog.ProxyToken;
            _config.ProxyBaseUrl = string.IsNullOrWhiteSpace(dialog.ProxyBaseUrl) ? null : dialog.ProxyBaseUrl.Trim();
            _configStore.Save(_config);
            StartNewSession(clearChat: false);
        }
    }

    private void AboutMenu_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        new AboutDialog(_configStore.ConfigPath) { Owner = this }.ShowDialog();
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

    // ============================= mode / model menus =============================

    private void ModePill_Click(object sender, RoutedEventArgs e) => ModePopup.IsOpen = true;

    private void ModeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string wire)
        {
            _vm.Mode = AgentModeExtensions.Parse(wire);
            _context = _context?.WithMode(_vm.Mode);
            UpdateSessionMeta();
        }

        ModePopup.IsOpen = false;
    }

    private void ModelPill_Click(object sender, RoutedEventArgs e)
    {
        BuildModelMenu();
        ModelPopup.IsOpen = true;
    }

    private void ProviderMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string provider)
        {
            _vm.Provider = provider;
            _vm.Model = ProviderModels.TryGetValue(provider, out var models) && models.Length > 0 ? models[0] : _vm.Model;
            SaveModelToConfig();
            BuildModelMenu();
        }
    }

    private void BuildModelMenu()
    {
        ModelMenuLabel.Text = $"MODEL · {_vm.Provider}";
        ModelListPanel.Children.Clear();
        var models = ProviderModels.TryGetValue(_vm.Provider, out var list) ? list : [];
        foreach (var model in models)
        {
            var b = new Button { Style = (Style)FindResource("MenuItemButtonStyle"), Content = model, Tag = model };
            b.Click += ModelMenu_Click;
            ModelListPanel.Children.Add(b);
        }

        var custom = new Button { Style = (Style)FindResource("MenuItemButtonStyle"), Content = "Custom…" };
        custom.Click += CustomModel_Click;
        ModelListPanel.Children.Add(custom);
    }

    private void ModelMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string model)
        {
            _vm.Model = model;
            SaveModelToConfig();
        }

        ModelPopup.IsOpen = false;
    }

    private void CustomModel_Click(object sender, RoutedEventArgs e)
    {
        ModelPopup.IsOpen = false;
        var dialog = new InputDialog("Custom model", $"Model id for provider '{_vm.Provider}':", _vm.Model) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Value))
        {
            _vm.Model = dialog.Value.Trim();
            SaveModelToConfig();
        }
    }

    private void SaveModelToConfig()
    {
        _config.DefaultProvider = _vm.Provider;
        _config.DefaultModel = _vm.Model;
        _configStore.Save(_config);
    }

    // ============================= project / session =============================

    private void ChooseProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose project root",
            InitialDirectory = Directory.Exists(_vm.ProjectRoot) ? _vm.ProjectRoot : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        if (dialog.ShowDialog(this) == true)
        {
            _vm.ProjectRoot = dialog.FolderName;
            StartNewSession(clearChat: true);
        }
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        SessionPopup.IsOpen = false;
        StartNewSession(clearChat: true);
    }

    private void ClearConversation_Click(object sender, RoutedEventArgs e)
    {
        SessionPopup.IsOpen = false;
        _vm.Conversation.Clear();
        _vm.Timeline.Clear();
        _loop?.ClearConversation();
        ResetTurnState();
    }

    // ============================= composer =============================

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendPromptAsync();

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _loop?.Cancel();
        _runCts?.Cancel();
        _vm.Status = "stopping";
    }

    // ============================= voice / dictation =============================

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

    private async void PromptBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            await SendPromptAsync();
        }
    }

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e)
        => SendButton.IsEnabled = PromptBox.Text.Trim().Length > 0;

    private async Task SendPromptAsync()
    {
        if (_loop is null || _context is null)
        {
            StartNewSession(clearChat: false);
        }

        if (_loop is null || _context is null)
        {
            return;
        }

        var prompt = PromptBox.Text.Trim();
        if (prompt.Length == 0)
        {
            return;
        }

        PromptBox.Clear();
        SaveModelToConfig();
        _context = _context.WithMode(_vm.Mode).WithModel(new ModelConfig(_vm.Provider, _vm.Model));
        UpdateSessionMeta();
        UpdateActiveSessionTitle(prompt);

        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        ResetTurnState();
        _vm.Conversation.Add(new UserBubbleBlock { Text = prompt });
        ScrollChatToEnd();
        _vm.IsWorking = true;
        _vm.Status = "working";

        try
        {
            await _loop.SubmitAsync(prompt, _context, _runCts.Token);
        }
        catch (OperationCanceledException)
        {
            _vm.Status = "cancelled";
        }
        catch (Exception ex)
        {
            _vm.Conversation.Add(new NoticeBlock { Title = "Error", Detail = ex.Message });
            _vm.Status = "error";
        }
        finally
        {
            _vm.IsWorking = false;
            FinalizeWorked();
            if (_vm.Status == "working")
            {
                _vm.Status = "ready";
            }

            RefreshUsage();
            RefreshFiles();
        }
    }

    // ============================= session lifecycle =============================

    private void StartNewSession(bool clearChat)
    {
        if (!Directory.Exists(_vm.ProjectRoot))
        {
            return;
        }

        _runCts?.Cancel();
        if (clearChat)
        {
            _vm.Conversation.Clear();
            _vm.Timeline.Clear();
            ResetTurnState();
        }

        SaveModelToConfig();
        var sessionId = SessionIds.NewId();
        var sessionDir = SessionIndex.SessionDir(sessionId);
        BuildLoop(sessionId, sessionDir, Path.GetFullPath(_vm.ProjectRoot));
        _vm.Status = "ready";
        _vm.ChatTitle = "New session";
        _vm.ChatSubtitle = "";
        SessionIndex.Write(sessionDir, new SessionSidecar(sessionId, "New session", _context!.ProjectRoot, $"{_vm.Provider}/{_vm.Model}", DateTimeOffset.Now));
        RebuildSidebar(sessionId);
        RefreshUsage();
        RefreshFiles();
    }

    private void BuildLoop(string sessionId, string sessionDir, string projectRoot)
    {
        Directory.CreateDirectory(sessionDir);
        var dataDir = Engine.Auth.Paths.DataDirectory();
        _context = new SessionContext(
            sessionId,
            projectRoot,
            dataDir,
            sessionDir,
            new ModelConfig(_vm.Provider, _vm.Model),
            DateTimeOffset.Now,
            _vm.Mode);

        var store = new TranscriptStore(sessionDir);
        var checkpoints = new CheckpointStore(sessionDir);
        var router = new AutoCode.Engine.Llm.LlmRouter(new AuthResolver(_config, ProxyTokenProvider));
        var registry = new ToolRegistry(_config);
        _loop = new AgentLoop(_config, store, checkpoints, router, registry, EmitAsync, ApproveToolAsync, ConfirmAsync, ChooseAsync);
        UpdateSessionMeta();
    }

    private void UpdateSessionMeta()
    {
        if (_context is null)
        {
            return;
        }

        _vm.SessionId = _context.SessionId;
        _vm.SessionModel = $"{_context.Model.Provider}/{_context.Model.Model}";
        _vm.SessionRoot = _vm.ProjectRootShort;
    }

    // ============================= engine event handling =============================

    private async Task EmitAsync(AgentEvent evt)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            switch (evt)
            {
                case ChatEvent { Role: "user" } userChat:
                    // Engine echoes the user turn; we already added a bubble on submit. Skip duplicates.
                    if (_vm.Conversation.LastOrDefault() is not UserBubbleBlock)
                    {
                        _vm.Conversation.Add(new UserBubbleBlock { Text = userChat.Text });
                    }
                    break;

                case ChatEvent chat:
                    FinalizeWorked();
                    _vm.Conversation.Add(new AssistantBlock { Text = chat.Text });
                    _pendingDiff = null;
                    ScrollChatToEnd();
                    break;

                case StatusEvent status:
                    _vm.Status = status.Text;
                    break;

                case ToolCallEvent call:
                    OnToolCall(call);
                    break;

                case ToolResultEvent result:
                    OnToolResult(result);
                    RefreshUsage();
                    break;

                case VerificationEvent verification:
                    OnVerification(verification);
                    break;
            }
        });
    }

    private void OnToolCall(ToolCallEvent call)
    {
        EnsureWorkedGroup();
        var step = new WorkedStep { Tool = call.ToolName, Status = "running" };
        _currentWorked!.Steps.Add(step);

        var item = new TimelineItemVM { ToolName = call.ToolName, Status = "running" };
        _vm.Timeline.Insert(0, item);
        _runningTools.Enqueue((step, item));

        if (MutatingTools.Contains(call.ToolName) && (_vm.Mode == AgentMode.Autocode || _vm.Mode == AgentMode.Admin))
        {
            _vm.ResolvedStatus = "Auto-approved";
        }

        ScrollChatToEnd();
    }

    private void OnToolResult(ToolResultEvent result)
    {
        var status = result.IsError ? "error" : "done";
        if (_runningTools.Count > 0)
        {
            var (step, item) = _runningTools.Dequeue();
            step.Status = status;
            step.Detail = result.Summary;
            item.Status = status;
            item.Summary = result.Summary;
            item.DurationMs = result.DurationMs;
        }
        else if (_currentWorked is not null)
        {
            _currentWorked.Steps.Add(new WorkedStep { Tool = result.ToolName, Status = status, Detail = result.Summary });
        }

        if (!result.IsError && MutatingTools.Contains(result.ToolName) && result.ToolName != "run_shell")
        {
            AccumulateDiff(result);
        }

        if (result.IsError)
        {
            _vm.Conversation.Add(new NoticeBlock { Title = $"{result.ToolName} failed", Detail = result.Content });
        }

        ScrollChatToEnd();
    }

    private void OnVerification(VerificationEvent verification)
    {
        var status = verification.Passed == false ? "error" : "done";
        var label = verification.Passed is null ? "verification" : verification.Passed.Value ? "verification passed" : "verification failed";
        _vm.Timeline.Insert(0, new TimelineItemVM { ToolName = label, Status = status, Summary = verification.Command });
        if (verification.Passed == false && !string.IsNullOrWhiteSpace(verification.Output))
        {
            _vm.Conversation.Add(new NoticeBlock { Title = "Verification failed", Detail = verification.Output });
        }
    }

    private void EnsureWorkedGroup()
    {
        if (_currentWorked is not null)
        {
            return;
        }

        // Collapse the previous group; keep the newest expanded.
        foreach (var block in _vm.Conversation.OfType<WorkedForBlock>())
        {
            block.IsExpanded = false;
        }

        _currentWorked = new WorkedForBlock { Label = "Working…", IsExpanded = true, StartedAt = DateTimeOffset.Now };
        _vm.Conversation.Add(_currentWorked);
    }

    private void FinalizeWorked()
    {
        if (_currentWorked is null)
        {
            return;
        }

        var elapsed = DateTimeOffset.Now - _currentWorked.StartedAt;
        _currentWorked.Label = $"Worked for {FormatElapsed(elapsed)}";
        _currentWorked = null;
        _runningTools.Clear();
    }

    private void AccumulateDiff(ToolResultEvent result)
    {
        var (adds, dels) = ParseAddsDels(result.Summary);
        var path = ParsePath(result.Summary) ?? result.ToolName;

        if (_pendingDiff is null)
        {
            _pendingDiff = new DiffCardBlock();
            _vm.Conversation.Add(_pendingDiff);
        }

        _pendingDiff.Files.Add(new DiffFileRow { Path = path, Adds = adds, Dels = dels });
        _pendingDiff.Refresh();

        var rel = TryRelative(path);
        if (rel is not null)
        {
            _modifiedFiles.Add(rel);
        }
    }

    private void ResetTurnState()
    {
        _currentWorked = null;
        _pendingDiff = null;
        _runningTools.Clear();
        _vm.ResolvedStatus = "";
    }

    // ============================= approvals =============================

    private async Task<ApprovalDecision> ApproveToolAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.InvokeAsync(() =>
        {
            _approvalCompletion = completion;
            _vm.ResolvedStatus = "";
            _vm.Approval = BuildApproval(request);
            OpenPanel("run");
        });

        await using var _ = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task.ConfigureAwait(false);
    }

    private static ApprovalVM BuildApproval(ToolApprovalRequest request)
    {
        var target = "";
        if (request.Input.TryGetValue("path", out var p) && p is not null)
        {
            target = Convert.ToString(p) ?? "";
        }
        else if (request.Input.TryGetValue("command", out var c) && c is not null)
        {
            target = Convert.ToString(c) ?? "";
        }

        var vm = new ApprovalVM { ToolName = request.ToolName, Target = target };
        foreach (var line in (request.Preview ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var kind = "ctx";
            if (line.StartsWith('+')) { kind = "add"; }
            else if (line.StartsWith('-')) { kind = "del"; }
            vm.PreviewLines.Add(new PreviewLine { Text = line, Kind = kind });
        }

        return vm;
    }

    private void AcceptApproval_Click(object sender, RoutedEventArgs e) => CompleteApproval(ApprovalDecision.Accept(), "Approved");

    private void DeclineApproval_Click(object sender, RoutedEventArgs e) => CompleteApproval(ApprovalDecision.Decline(), "Declined");

    private void ReviseApproval_Click(object sender, RoutedEventArgs e) => CompleteApproval(ApprovalDecision.Revise(RevisionBox.Text), "Revision requested");

    private void CompleteApproval(ApprovalDecision decision, string resolved)
    {
        _vm.Approval = null;
        _vm.ResolvedStatus = resolved;
        RevisionBox.Clear();
        _approvalCompletion?.TrySetResult(decision);
        _approvalCompletion = null;
    }

    private async Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken)
    {
        var result = await Dispatcher.InvokeAsync(() =>
            MessageBox.Show(this, prompt, "Confirm command", MessageBoxButton.YesNo, MessageBoxImage.Warning));
        return result == MessageBoxResult.Yes;
    }

    private async Task<IReadOnlyList<int>> ChooseAsync(AskUserRequest request, CancellationToken cancellationToken)
    {
        return await Dispatcher.InvokeAsync(() =>
        {
            var dialog = new ChoiceDialog(request) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.SelectedIndexes : (IReadOnlyList<int>)[];
        });
    }

    // ============================= sidebar (sessions) =============================

    private void RebuildSidebar(string? activeId)
    {
        _vm.Projects.Clear();
        var now = DateTimeOffset.Now;
        var groups = SessionIndex.LoadAll()
            .GroupBy(s => s.ProjectRoot, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Root = g.Key, Items = g.OrderByDescending(s => s.StartedAt).ToList(), Latest = g.Max(s => s.StartedAt) })
            .OrderByDescending(g => g.Latest)
            .ToList();

        foreach (var g in groups)
        {
            var project = new ProjectNode { Name = LeafName(g.Root), Path = g.Root };
            project.ToggleCommand = new RelayCommand(_ => project.IsExpanded = !project.IsExpanded);
            var hasActive = false;
            foreach (var s in g.Items)
            {
                var node = new SessionNode
                {
                    Id = s.Id,
                    Title = string.IsNullOrWhiteSpace(s.Title) ? "Session" : s.Title,
                    ProjectRoot = s.ProjectRoot,
                    SessionDir = SessionIndex.SessionDir(s.Id),
                    RelativeTime = Converters.RelativeTimeConverter.Format(s.StartedAt, now),
                    IsActive = s.Id == activeId,
                };
                if (node.IsActive) { hasActive = true; }
                var captured = node;
                node.OpenCommand = new RelayCommand(_ => OpenSession(captured));
                project.Sessions.Add(node);
            }

            project.IsExpanded = hasActive;
            _vm.Projects.Add(project);
        }
    }

    private void UpdateActiveSessionTitle(string prompt)
    {
        if (_context is null || (_vm.ChatTitle != "New session" && !string.IsNullOrEmpty(_vm.ChatTitle)))
        {
            return;
        }

        var title = prompt.Length > 48 ? prompt[..48] + "…" : prompt;
        _vm.ChatTitle = title;
        SessionIndex.Write(_context.SessionDir, new SessionSidecar(
            _context.SessionId, title, _context.ProjectRoot, $"{_context.Model.Provider}/{_context.Model.Model}", _context.StartedAt));
        RebuildSidebar(_context.SessionId);
    }

    private void OpenSession(SessionNode node)
    {
        if (_context is not null && string.Equals(_context.SessionId, node.Id, StringComparison.Ordinal))
        {
            return;
        }

        _runCts?.Cancel();
        _vm.ProjectRoot = node.ProjectRoot;
        BuildLoop(node.Id, node.SessionDir, node.ProjectRoot);
        _vm.ChatTitle = string.IsNullOrWhiteSpace(node.Title) ? "Session" : node.Title;
        _vm.ChatSubtitle = "";
        _vm.Conversation.Clear();
        _vm.Timeline.Clear();
        ResetTurnState();

        // True resume: rehydrate the engine's context from the transcript so follow-ups continue the thread.
        var history = SessionIndex.LoadTranscript(node.SessionDir);
        _loop?.LoadHistory(history);

        foreach (var (role, text) in history)
        {
            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            {
                _vm.Conversation.Add(new UserBubbleBlock { Text = text });
            }
            else
            {
                _vm.Conversation.Add(new AssistantBlock { Text = text });
            }
        }

        RebuildSidebar(node.Id);
        _vm.Status = "ready";
        RefreshUsage();
        RefreshFiles();
        ScrollChatToEnd();
    }

    // ============================= files / usage =============================

    private void RefreshFiles()
    {
        _vm.Files.Clear();
        if (_context is null || !Directory.Exists(_context.ProjectRoot))
        {
            return;
        }

        try
        {
            foreach (var row in EnumerateFileNodes(_context.ProjectRoot, 0).Take(300))
            {
                _vm.Files.Add(row);
            }
        }
        catch
        {
            // Ignore file tree refresh failures.
        }
    }

    private IEnumerable<FileNode> EnumerateFileNodes(string root, int depth)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root)
                     .OrderBy(e => Directory.Exists(e) ? 0 : 1)
                     .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                if (ToolConstants.NoiseDirectories.Contains(name))
                {
                    continue;
                }

                yield return new FileNode { Name = name, Depth = depth, IsDirectory = true };
                if (depth < 3)
                {
                    foreach (var child in EnumerateFileNodes(entry, depth + 1).Take(80))
                    {
                        yield return child;
                    }
                }
            }
            else
            {
                var rel = TryRelative(entry);
                yield return new FileNode
                {
                    Name = name,
                    Depth = depth,
                    IsDirectory = false,
                    IsModified = rel is not null && _modifiedFiles.Contains(rel),
                };
            }
        }
    }

    private void RefreshUsage()
    {
        if (_loop is null)
        {
            _vm.SetUsage(0, 0, ContextWindowFor(_vm.Model));
            return;
        }

        var usage = _loop.CumulativeUsage;
        _vm.SetUsage(usage.InputTokens, usage.OutputTokens, ContextWindowFor(_vm.Model));
    }

    private static int ContextWindowFor(string model)
        => model.Contains("haiku", StringComparison.OrdinalIgnoreCase) ? 200_000
            : model.Contains("gpt", StringComparison.OrdinalIgnoreCase) ? 256_000
            : 200_000;

    // ============================= text scale / keep awake =============================

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

    // ============================= helpers =============================

    private void CopyToClipboard(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            try { Clipboard.SetText(text); } catch { /* clipboard may be locked */ }
        }
    }

    private void OpenFileNode(FileNode? node)
    {
        if (node is null || node.IsDirectory)
        {
            return;
        }

        OpenPanel("workspace");
    }

    private void OnFileRefRequested(string path, int? line)
    {
        OpenPanel("workspace");
        _ = line;
        _ = path;
    }

    private void ScrollChatToEnd() => ChatScroll.ScrollToEnd();

    private string? TryRelative(string absoluteOrName)
    {
        if (_context is null)
        {
            return null;
        }

        try
        {
            var full = Path.IsPathRooted(absoluteOrName) ? absoluteOrName : Path.Combine(_context.ProjectRoot, absoluteOrName);
            var rel = Path.GetRelativePath(_context.ProjectRoot, full).Replace('\\', '/');
            return rel.StartsWith("..", StringComparison.Ordinal) ? Path.GetFileName(absoluteOrName) : rel;
        }
        catch
        {
            return null;
        }
    }

    private static (int Adds, int Dels) ParseAddsDels(string summary)
    {
        var adds = 0;
        var dels = 0;
        var m = Regex.Match(summary, @"\+(\d+)");
        if (m.Success) { adds = int.Parse(m.Groups[1].Value); }
        var d = Regex.Match(summary, @"[−-](\d+)");
        if (d.Success) { dels = int.Parse(d.Groups[1].Value); }
        return (adds, dels);
    }

    private static string? ParsePath(string summary)
    {
        var m = Regex.Match(summary, @"[A-Za-z0-9_./\\-]+\.[A-Za-z0-9]+");
        return m.Success ? m.Value : null;
    }

    private static string FormatElapsed(TimeSpan span)
    {
        if (span.TotalSeconds < 60) { return $"{Math.Max(1, (int)span.TotalSeconds)}s"; }
        var m = (int)span.TotalMinutes;
        var s = span.Seconds;
        return s > 0 ? $"{m}m {s}s" : $"{m}m";
    }

    private static string LeafName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private static string DefaultProjectRoot()
    {
        var autocode = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Desktop", "automax", "autocode");
        return Directory.Exists(autocode) ? autocode : Environment.CurrentDirectory;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}

[Flags]
internal enum ExecutionState : uint
{
    EsContinuous = 0x80000000,
    EsSystemRequired = 0x00000001,
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int X; public int Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

[StructLayout(LayoutKind.Sequential)]
internal struct MINMAXINFO
{
    public POINT ptReserved;
    public POINT ptMaxSize;
    public POINT ptMaxPosition;
    public POINT ptMinTrackSize;
    public POINT ptMaxTrackSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AutoCode.Desktop.Misc;
using AutoCode.Desktop.ViewModels;
using AutoCode.Engine.Agent;
using AutoCode.Engine.Auth;
using AutoCode.Engine.Session;
using AutoCode.Engine.Tools;

namespace AutoCode.Desktop;

// MainWindow is split into partial classes by concern (see CLAUDE.md → "Don't grow
// MainWindow.xaml.cs"). This core file holds construction, window lifecycle, the composer,
// and small shared helpers. Other concerns live in MainWindow.<Concern>.cs:
//   Account, WindowChrome, Layout, Menus, Sessions, EngineEvents, Approvals, Voice.
//
// Per-session state (engine loop + conversation/timeline/plan/usage/approval/turn-state) lives on
// WorkspaceSession; SessionManager owns the live set + the Active one. The handlers here operate on a
// given session; the UI binds to MainViewModel's façade over the active session.
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly ConfigStore _configStore = new();
    private readonly Auth.FirebaseAuthService _firebase = new();
    private readonly Auth.SubscriptionService _subscriptions = new();
    private AutocodeConfig _config = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.CopyCommand = new RelayCommand(p => CopyToClipboard(p as string));
        _vm.ReviewCommand = new RelayCommand(_ => OpenPanel("run"));
        _vm.OpenFileCommand = new RelayCommand(p => OpenFileNode(p as FileNode));
        _vm.MergeCommand = new RelayCommand(_ => MergeActive());
        _vm.AcceptApprovalCommand = new RelayCommand(_ => AcceptApproval());
        _vm.DeclineApprovalCommand = new RelayCommand(_ => DeclineApproval());
        _vm.ReviseApprovalCommand = new RelayCommand(_ => ReviseApproval());
        _vm.ActivateSessionCommand = new RelayCommand(p => { if (p is WorkspaceSession s) ActivateWorkspace(s); });
        _vm.CloseSessionCommand = new RelayCommand(p => { if (p is WorkspaceSession s) CloseWorkspace(s); });
        _vm.OpenEcosystemChatCommand = new RelayCommand(p => { if (p is EcosystemNode n) OpenEcosystemChatFromNode(n); });
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
        _vm.GroupByEcosystem = _config.GroupByEcosystem;

        InitEcosystems();

        try { await _firebase.TryRestoreSessionAsync(); } catch { /* offline / corrupt session */ }
        RefreshAccountUi();

        // Open any projects passed on the command line (`--project <path>`, repeatable) so the app can
        // launch straight into one or more workspaces; otherwise start one session at the default root.
        var startupRoots = StartupProjectRoots();
        var startupAgent = StartupAgentId();
        if (startupRoots.Count > 0)
        {
            foreach (var root in startupRoots)
            {
                await StartNewSession(root, startupAgent);
            }
        }
        else
        {
            await StartNewSession(DefaultProjectRoot());
        }

        SendButton.IsEnabled = false;

        // Optional sign-in prompt on first launch (skippable -> guest). Shown once.
        if (!_firebase.IsAuthenticated && !_config.LoginPromptSeen)
        {
            ShowLoginDialog();
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        foreach (var session in _vm.Sessions.Sessions)
        {
            session.RunCts?.Cancel();
        }

        _voiceCts?.Cancel();
        _recorder?.Dispose();
        ApplyKeepAwake(false);
        RunShellTool.StopBackgroundProcesses();
    }

    // ============================= composer =============================

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendPromptAsync();

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        var session = _vm.Active;
        if (session is null)
        {
            return;
        }

        session.Backend?.Cancel();
        session.RunCts?.Cancel();
        session.Status = "stopping";
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
        // Don't submit while a session's git worktree is still being created.
        if (_vm.Active?.IsPreparing == true)
        {
            return;
        }

        var session = _vm.Active;
        if (session?.Backend is null || session.Context is null)
        {
            await StartNewSession(_vm.Active?.ProjectRoot ?? DefaultProjectRoot());
            session = _vm.Active;
        }

        if (session?.Backend is null || session.Context is null)
        {
            return;
        }

        var input = PromptBox.Text.Trim();
        if (input.Length == 0)
        {
            return;
        }

        PromptBox.Clear();

        // In an ecosystem chat, "@member ..." routes to member sessions instead of the ecosystem agent.
        if (TryRouteMentions(session, input))
        {
            return;
        }

        await SubmitPromptAsync(session, input);
    }

    /// <summary>Run one turn for a specific session: apply the composer model/mode, echo the user bubble,
    /// submit to its backend, then finalize (usage, files, resume-sidecar, worktree commit, changes). Shared
    /// by the active chat and by @mention-routed member sessions; drains this session's queued prompts after.</summary>
    private async Task SubmitPromptAsync(WorkspaceSession session, string prompt)
    {
        if (session.Backend is null || session.Context is null)
        {
            return;
        }

        SaveModelToConfig();
        session.Context = session.Context.WithMode(_vm.Mode).WithModel(new ModelConfig(_vm.Provider, _vm.Model));
        UpdateSessionMeta(session);
        UpdateActiveSessionTitle(session, prompt);

        session.RunCts?.Cancel();
        session.RunCts = new CancellationTokenSource();
        ResetTurnState(session);
        session.Conversation.Add(new UserBubbleBlock { Text = prompt });
        if (IsActiveSession(session)) { ScrollChatToEnd(); }
        session.IsWorking = true;
        session.Status = "working";
        NoteMemberTurnBoundary(session, starting: true);

        try
        {
            await session.Backend.SubmitAsync(prompt, session.Context, session.RunCts.Token);
        }
        catch (OperationCanceledException)
        {
            session.Status = "cancelled";
        }
        catch (Exception ex)
        {
            session.Conversation.Add(new NoticeBlock { Title = "Error", Detail = ex.Message });
            session.Status = "error";
        }
        finally
        {
            session.IsWorking = false;
            NoteMemberTurnBoundary(session, starting: false);
            FinalizeWorked(session);
            if (session.Status == "working")
            {
                session.Status = "ready";
            }

            RefreshUsage(session);
            RefreshFiles(session);

            // External CLI agents get their continuity handle (Claude session / Codex thread id)
            // during the first turn — after the sidecar was written. Re-persist so reopen resumes.
            if (session.Backend?.ResumeId is not null
                && System.IO.File.Exists(System.IO.Path.Combine(session.SessionDir, "session.json")))
            {
                WriteSidecar(session);
            }

            // In auto-branch mode, snapshot this turn's edits as a commit on the session's branch.
            if (session.WorktreePath is not null)
            {
                try { await GitService.CommitAllAsync(session.WorktreePath, session.ChatTitle); } catch { /* commit is best-effort */ }
            }

            // Update the review surface (changed files) for this session.
            await RefreshChangesAsync(session);
        }

        // Drain the next queued (@mention-routed) prompt for this session, if any.
        if (session.PendingPrompts.Count > 0)
        {
            await SubmitPromptAsync(session, session.PendingPrompts.Dequeue());
        }
    }

    // ============================= shared helpers =============================

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

    private static string? TryRelative(WorkspaceSession session, string absoluteOrName)
    {
        if (session.Context is null)
        {
            return null;
        }

        try
        {
            var root = session.Context.ProjectRoot;
            var full = System.IO.Path.IsPathRooted(absoluteOrName) ? absoluteOrName : System.IO.Path.Combine(root, absoluteOrName);
            var rel = System.IO.Path.GetRelativePath(root, full).Replace('\\', '/');
            return rel.StartsWith("..", StringComparison.Ordinal) ? System.IO.Path.GetFileName(absoluteOrName) : rel;
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
        var m = System.Text.RegularExpressions.Regex.Match(summary, @"\+(\d+)");
        if (m.Success) { adds = int.Parse(m.Groups[1].Value); }
        var d = System.Text.RegularExpressions.Regex.Match(summary, @"[−-](\d+)");
        if (d.Success) { dels = int.Parse(d.Groups[1].Value); }
        return (adds, dels);
    }

    private static string? ParsePath(string summary)
    {
        var m = System.Text.RegularExpressions.Regex.Match(summary, @"[A-Za-z0-9_./\\-]+\.[A-Za-z0-9]+");
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
        var trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    /// <summary>
    /// Project roots requested on the command line via <c>--project &lt;path&gt;</c> (repeatable).
    /// Each existing directory becomes its own workspace at startup. Useful for "open in folder"
    /// launches and for driving multiple isolated workspaces deterministically.
    /// </summary>
    private static List<string> StartupProjectRoots()
    {
        var roots = new List<string>();
        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--project", StringComparison.OrdinalIgnoreCase) || i + 1 >= args.Length)
            {
                continue;
            }

            var path = args[++i];
            if (System.IO.Directory.Exists(path))
            {
                roots.Add(System.IO.Path.GetFullPath(path));
            }
        }

        return roots;
    }

    /// <summary>Agent backend requested on the command line via <c>--agent &lt;id&gt;</c>
    /// ("builtin" | "claude-code" | "codex"), applied to startup workspaces. Null when absent
    /// (StartNewSession then falls back to the configured default).</summary>
    private static string? StartupAgentId()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--agent", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static string DefaultProjectRoot()
    {
        var autocode = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Desktop", "automax", "autocode");
        return System.IO.Directory.Exists(autocode) ? autocode : Environment.CurrentDirectory;
    }
}

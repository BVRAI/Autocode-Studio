using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using AutoCode.Engine.Agent;

namespace AutoCode.Desktop.ViewModels;

/// <summary>Lifecycle of a voice-dictation turn.</summary>
public enum VoiceState
{
    Idle,
    Recording,
    Transcribing,
}

/// <summary>
/// App-level window state (theme, voice, account, sidebar, layout, the model picker) PLUS a façade
/// over the active <see cref="WorkspaceSession"/>: per-session bound properties (Conversation, title,
/// timeline, plan, usage, approval, …) forward to <see cref="Sessions"/>.Active, so the same XAML
/// bindings follow whichever workspace is active. Switching active sessions is instant and preserves
/// each session's live state.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private static readonly ObservableCollection<ConversationBlock> EmptyConversation = [];
    private static readonly ObservableCollection<TimelineItemVM> EmptyTimeline = [];
    private static readonly ObservableCollection<PlanItemVM> EmptyPlan = [];
    private static readonly ObservableCollection<FileNode> EmptyFiles = [];
    private static readonly ObservableCollection<ChangeItem> EmptyChanges = [];

    private WorkspaceSession? _relayHooked;

    public MainViewModel()
    {
        Sessions.ActiveChanged += OnActiveChanged;
    }

    // ---- Commands wired by code-behind, reached from templates/panels via DataContext.* ----
    public RelayCommand? CopyCommand { get; set; }
    public RelayCommand? ReviewCommand { get; set; }
    public RelayCommand? OpenFileCommand { get; set; }
    public RelayCommand? MergeCommand { get; set; }
    public RelayCommand? AcceptApprovalCommand { get; set; }
    public RelayCommand? DeclineApprovalCommand { get; set; }
    public RelayCommand? ReviseApprovalCommand { get; set; }
    public RelayCommand? ActivateSessionCommand { get; set; }
    public RelayCommand? CloseSessionCommand { get; set; }

    // Transient text for the approval "revise" box (bound two-way from the Run panel).
    private string _revisionText = "";
    public string RevisionText
    {
        get => _revisionText;
        set => Set(ref _revisionText, value);
    }

    // ---- Sessions ----
    public SessionManager Sessions { get; } = new();

    public WorkspaceSession? Active => Sessions.Active;

    private void OnActiveChanged(WorkspaceSession? session)
    {
        if (_relayHooked is not null)
        {
            _relayHooked.PropertyChanged -= OnActivePropertyChanged;
        }

        _relayHooked = session;
        if (_relayHooked is not null)
        {
            _relayHooked.PropertyChanged += OnActivePropertyChanged;
        }

        // Refresh every façade binding to read the newly-active session.
        Raise(string.Empty);
    }

    private void OnActivePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            Raise(string.Empty);
            return;
        }

        Raise(e.PropertyName);
        if (e.PropertyName == nameof(WorkspaceSession.HasApproval))
        {
            Raise(nameof(ShowRunBadgeTopbar));
        }

        if (e.PropertyName == nameof(WorkspaceSession.AgentId))
        {
            Raise(nameof(AgentLabel));
        }
    }

    // ---- Per-session façade (forwards to Active) ----
    public ObservableCollection<ConversationBlock> Conversation => Active?.Conversation ?? EmptyConversation;
    public ObservableCollection<TimelineItemVM> Timeline => Active?.Timeline ?? EmptyTimeline;
    public ObservableCollection<PlanItemVM> Plan => Active?.Plan ?? EmptyPlan;
    public ObservableCollection<FileNode> Files => Active?.Files ?? EmptyFiles;
    public ObservableCollection<ChangeItem> Changes => Active?.Changes ?? EmptyChanges;

    public string AgentLabel => AgentDisplayName(Active?.AgentId);

    /// <summary>Display name for an agent id ("builtin" | "claude-code" | "codex").</summary>
    public static string AgentDisplayName(string? agentId) => agentId switch
    {
        "claude-code" => "Claude Code",
        "codex" => "Codex",
        _ => "AutoCode",
    };

    public string ChatTitle => Active?.ChatTitle ?? "New session";
    public string ChatSubtitle => Active?.ChatSubtitle ?? "";
    public string ProjectRoot => Active?.ProjectRoot ?? "";
    public string ProjectRootShort => Active?.ProjectRootShort ?? "";
    public string SessionId => Active?.SessionId ?? "";
    public string SessionModel => Active?.SessionModel ?? "";
    public string SessionRoot => Active?.SessionRoot ?? "";
    public bool IsWorking => Active?.IsWorking ?? false;
    public string Status => Active?.Status ?? "ready";
    public ApprovalVM? Approval => Active?.Approval;
    public bool HasApproval => Active?.HasApproval ?? false;
    public string ResolvedStatus => Active?.ResolvedStatus ?? "";
    public bool HasResolvedStatus => Active?.HasResolvedStatus ?? false;
    public bool HasPlan => Active?.HasPlan ?? false;
    public bool HasChanges => Active?.HasChanges ?? false;
    public string? Branch => Active?.Branch;
    public string? BaseBranch => Active?.BaseBranch;
    public bool HasWorktree => Active?.HasWorktree ?? false;
    public bool IsPreparing => Active?.IsPreparing ?? false;
    public string UsagePercentText => Active?.UsagePercentText ?? "0% used";
    public string UsageInText => Active?.UsageInText ?? "0";
    public string UsageOutText => Active?.UsageOutText ?? "0";
    public GridLength UsageFill => Active?.UsageFill ?? new GridLength(0.0001, GridUnitType.Star);
    public GridLength UsageRest => Active?.UsageRest ?? new GridLength(1, GridUnitType.Star);

    // ---- Sidebar projects ----
    public ObservableCollection<ProjectNode> Projects { get; } = [];

    // ---- Sidebar ecosystems (bare list in Phase 1; grouping in Phase 2) ----
    public ObservableCollection<EcosystemNode> Ecosystems { get; } = [];

    private bool _hasEcosystems;
    public bool HasEcosystems
    {
        get => _hasEcosystems;
        set => Set(ref _hasEcosystems, value);
    }

    // ---- Layout state ----
    private bool _sidebarCollapsed;
    public bool SidebarCollapsed
    {
        get => _sidebarCollapsed;
        set => Set(ref _sidebarCollapsed, value);
    }

    private bool _panelOpen;
    public bool PanelOpen
    {
        get => _panelOpen;
        set { if (Set(ref _panelOpen, value)) { Raise(nameof(ShowRunBadgeTopbar)); } }
    }

    private string _panelTab = "workspace";
    public string PanelTab
    {
        get => _panelTab;
        set { if (Set(ref _panelTab, value)) { Raise(nameof(ShowRunBadgeTopbar)); } }
    }

    public bool ShowRunBadgeTopbar => (Active?.HasApproval ?? false) && !(PanelOpen && PanelTab == "run");

    // ---- Mode (picker; applied to the active session per turn) ----
    private AgentMode _mode = AgentMode.Default;
    public AgentMode Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
            {
                Raise(nameof(ModeWire));
                Raise(nameof(ModeLabel));
                Raise(nameof(ModeGlyph));
            }
        }
    }

    public string ModeWire => _mode.WireName();

    public string ModeLabel => _mode switch
    {
        AgentMode.Autocode => "Full access",
        AgentMode.Planning => "Plan only",
        AgentMode.Admin => "Admin",
        _ => "Default",
    };

    public Geometry? ModeGlyph => ResourceGeometry(_mode switch
    {
        AgentMode.Autocode => "IconBolt",
        AgentMode.Planning => "IconPlan",
        AgentMode.Admin => "IconCrown",
        _ => "IconShieldCheck",
    });

    // ---- Provider / model (picker) ----
    private string _provider = "anthropic";
    public string Provider
    {
        get => _provider;
        set { if (Set(ref _provider, value)) { Raise(nameof(ModelLabel)); } }
    }

    private string _model = "claude-opus-4-7";
    public string Model
    {
        get => _model;
        set { if (Set(ref _model, value)) { Raise(nameof(ModelLabel)); } }
    }

    public string ModelLabel => $"{_provider} · {_model}";

    // ---- Theme ----
    private string _theme = "light";
    public string Theme
    {
        get => _theme;
        set { if (Set(ref _theme, value)) { Raise(nameof(IsDark)); } }
    }

    public bool IsDark => string.Equals(_theme, "dark", StringComparison.OrdinalIgnoreCase);

    // ---- Keep awake ----
    private bool _isKeepAwake;
    public bool IsKeepAwake
    {
        get => _isKeepAwake;
        set => Set(ref _isKeepAwake, value);
    }

    // ---- Font size ----
    private bool _isLargeFont;
    public bool IsLargeFont
    {
        get => _isLargeFont;
        set => Set(ref _isLargeFont, value);
    }

    // ---- Voice (dictation) ----
    private VoiceState _voice = VoiceState.Idle;
    public VoiceState Voice
    {
        get => _voice;
        set { if (Set(ref _voice, value)) { Raise(nameof(IsRecording)); Raise(nameof(VoiceTooltip)); } }
    }

    public bool IsRecording => _voice == VoiceState.Recording;

    public string VoiceTooltip => _voice switch
    {
        VoiceState.Recording => "Stop & transcribe",
        VoiceState.Transcribing => "Transcribing…",
        _ => "Dictate (voice to text)",
    };

    // ---- Account ----
    private bool _isSignedIn;
    public bool IsSignedIn
    {
        get => _isSignedIn;
        set { if (Set(ref _isSignedIn, value)) { Raise(nameof(IsSignedOut)); Raise(nameof(ProxyToggleEnabled)); Raise(nameof(SubscriptionLabel)); } }
    }

    public bool IsSignedOut => !_isSignedIn;

    private string _accountEmail = "";
    public string AccountEmail
    {
        get => _accountEmail;
        set { if (Set(ref _accountEmail, value)) { Raise(nameof(AccountInitial)); } }
    }

    private string? _accountPhotoUrl;
    public string? AccountPhotoUrl
    {
        get => _accountPhotoUrl;
        set => Set(ref _accountPhotoUrl, value);
    }

    public string AccountInitial => string.IsNullOrEmpty(_accountEmail) ? "?" : _accountEmail[..1].ToUpperInvariant();

    private bool _isSubscriber;
    public bool IsSubscriber
    {
        get => _isSubscriber;
        set { if (Set(ref _isSubscriber, value)) { Raise(nameof(ProxyToggleEnabled)); Raise(nameof(SubscriptionLabel)); } }
    }

    private bool _useProxy = true;
    public bool UseProxy
    {
        get => _useProxy;
        set => Set(ref _useProxy, value);
    }

    public bool ProxyToggleEnabled => _isSignedIn && _isSubscriber;

    public string SubscriptionLabel => !_isSignedIn ? "" : _isSubscriber ? "Subscriber" : "Not subscribed";

    // ---- helpers ----
    private static Geometry? ResourceGeometry(string key)
        => Application.Current?.TryFindResource(key) as Geometry;
}

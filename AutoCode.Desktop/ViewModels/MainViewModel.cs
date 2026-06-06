using System.Collections.ObjectModel;
using System.IO;
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

/// <summary>Bindable UI state for the main window. Engine interaction stays in code-behind.</summary>
public sealed class MainViewModel : ObservableObject
{
    // ---- Commands wired by code-behind, reached from templates via DataContext.* ----
    public RelayCommand? CopyCommand { get; set; }
    public RelayCommand? ReviewCommand { get; set; }
    public RelayCommand? OpenFileCommand { get; set; }

    // ---- Conversation / timeline / files / sessions ----
    public ObservableCollection<ConversationBlock> Conversation { get; } = [];
    public ObservableCollection<TimelineItemVM> Timeline { get; } = [];
    public ObservableCollection<FileNode> Files { get; } = [];
    public ObservableCollection<ProjectNode> Projects { get; } = [];

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

    // ---- Mode ----
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

    // ---- Provider / model ----
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

    // ---- Status ----
    private bool _isWorking;
    public bool IsWorking
    {
        get => _isWorking;
        set => Set(ref _isWorking, value);
    }

    private string _status = "ready";
    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

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

    // ---- Title / project ----
    private string _chatTitle = "New session";
    public string ChatTitle
    {
        get => _chatTitle;
        set => Set(ref _chatTitle, value);
    }

    private string _chatSubtitle = "";
    public string ChatSubtitle
    {
        get => _chatSubtitle;
        set => Set(ref _chatSubtitle, value);
    }

    private string _projectRoot = "";
    public string ProjectRoot
    {
        get => _projectRoot;
        set { if (Set(ref _projectRoot, value)) { Raise(nameof(ProjectRootShort)); } }
    }

    public string ProjectRootShort => ShortenPath(_projectRoot);

    // ---- Session meta (workspace tab) ----
    private string _sessionId = "";
    public string SessionId { get => _sessionId; set => Set(ref _sessionId, value); }

    private string _sessionModel = "";
    public string SessionModel { get => _sessionModel; set => Set(ref _sessionModel, value); }

    private string _sessionRoot = "";
    public string SessionRoot { get => _sessionRoot; set => Set(ref _sessionRoot, value); }

    // ---- Usage ----
    private int _inputTokens;
    private int _outputTokens;
    private int _contextWindow = 200_000;

    public void SetUsage(int input, int output, int contextWindow)
    {
        _inputTokens = input;
        _outputTokens = output;
        _contextWindow = contextWindow <= 0 ? 200_000 : contextWindow;
        Raise(nameof(UsagePercentText));
        Raise(nameof(UsageInText));
        Raise(nameof(UsageOutText));
        Raise(nameof(UsageFill));
        Raise(nameof(UsageRest));
    }

    private double Pct => Math.Clamp((_inputTokens + _outputTokens) / (double)_contextWindow, 0, 1);

    public string UsagePercentText => $"{(int)Math.Round(Pct * 100)}% used";
    public string UsageInText => FormatK(_inputTokens);
    public string UsageOutText => FormatK(_outputTokens);
    public GridLength UsageFill => new(Math.Max(Pct, 0.0001), GridUnitType.Star);
    public GridLength UsageRest => new(Math.Max(1 - Pct, 0.0001), GridUnitType.Star);

    // ---- Approval ----
    private ApprovalVM? _approval;
    public ApprovalVM? Approval
    {
        get => _approval;
        set
        {
            if (Set(ref _approval, value))
            {
                Raise(nameof(HasApproval));
                Raise(nameof(ShowRunBadgeTopbar));
            }
        }
    }

    public bool HasApproval => _approval is not null;

    private string _resolvedStatus = "";
    public string ResolvedStatus
    {
        get => _resolvedStatus;
        set { if (Set(ref _resolvedStatus, value)) { Raise(nameof(HasResolvedStatus)); } }
    }

    public bool HasResolvedStatus => !string.IsNullOrEmpty(_resolvedStatus);

    public bool ShowRunBadgeTopbar => HasApproval && !(PanelOpen && PanelTab == "run");

    // ---- helpers ----
    private static Geometry? ResourceGeometry(string key)
        => Application.Current?.TryFindResource(key) as Geometry;

    private static string FormatK(int n)
        => n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();

    private static string ShortenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var parts = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length <= 2)
        {
            return path;
        }

        return "…\\" + string.Join('\\', parts[^2..]);
    }
}

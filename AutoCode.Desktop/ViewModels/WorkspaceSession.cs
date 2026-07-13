using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using AutoCode.Engine.Agent;
using AutoCode.Engine.Backends;

namespace AutoCode.Desktop.ViewModels;

/// <summary>
/// One workspace/tab: its engine (context + loop + cancellation) plus all of its bindable view-state
/// and per-turn state. Multiple of these live in memory at once (one per open session); the UI binds
/// to whichever is <see cref="SessionManager.Active"/>. Behaviour/logic stays in the MainWindow
/// handlers, which operate on a given WorkspaceSession — this type is state only.
/// </summary>
public sealed class WorkspaceSession : ObservableObject
{
    // ---- identity / engine ----
    public const string ProjectKind = "project";
    public const string EcosystemKind = "ecosystem";

    public string Id { get; init; } = "";
    public string SessionDir { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Session role: <see cref="ProjectKind"/> (a normal project session) or
    /// <see cref="EcosystemKind"/> (the ecosystem chat, rooted at the manifest repo).</summary>
    public string Kind { get; set; } = ProjectKind;

    /// <summary>For an ecosystem chat (<see cref="EcosystemKind"/>), the id of the ecosystem it hosts.</summary>
    public string? EcosystemId { get; set; }

    /// <summary>Binding-friendly Kind check (drives the cube marker on ecosystem-chat rows).</summary>
    public bool IsEcosystemChat => Kind == EcosystemKind;

    /// <summary>Mutable session config (model/mode applied per turn). Set in BuildLoop.</summary>
    public SessionContext? Context { get; set; }

    /// <summary>Which agent drives this workspace: "builtin" | "claude-code" | "codex". Set at create
    /// time or via the composer's agent picker; WireLoop builds the matching <see cref="IAgentBackend"/>.</summary>
    private string _agentId = "builtin";
    public string AgentId
    {
        get => _agentId;
        set => Set(ref _agentId, value);
    }

    /// <summary>Per-session mode wire (see AgentCatalog.ModesFor for this session's harness):
    /// builtin "planning|default|autocode|admin"; claude-code "plan|accept-edits|auto"; codex
    /// "read-only|auto|full-access". The composer pill follows the active session.</summary>
    private string _modeWire = "default";
    public string ModeWire
    {
        get => _modeWire;
        set => Set(ref _modeWire, value);
    }

    /// <summary>LLM provider for builtin sessions; the agent id for external ones (so backends can
    /// tell whether the model choice was made for their harness).</summary>
    private string _provider = "anthropic";
    public string Provider
    {
        get => _provider;
        set => Set(ref _provider, value);
    }

    /// <summary>Model id (builtin: catalog id; external: "default" = the CLI's own setting, or an
    /// alias/custom id passed via the CLI's --model flag).</summary>
    private string _modelId = "claude-opus-4-7";
    public string ModelId
    {
        get => _modelId;
        set => Set(ref _modelId, value);
    }

    /// <summary>The agent driving this workspace — built-in engine or an external CLI (Claude Code / Codex).</summary>
    public IAgentBackend? Backend { get; set; }

    public CancellationTokenSource? RunCts { get; set; }

    // ---- git worktree isolation (auto-branch mode) ----
    private string? _branch;
    public string? Branch
    {
        get => _branch;
        set { if (Set(ref _branch, value)) { Raise(nameof(HasWorktree)); } }
    }

    private string? _baseBranch;
    public string? BaseBranch
    {
        get => _baseBranch;
        set => Set(ref _baseBranch, value);
    }

    /// <summary>Filesystem path of this session's git worktree (also its Context.ProjectRoot when set).</summary>
    public string? WorktreePath { get; set; }

    /// <summary>True while the worktree is being created (the composer is gated until ready).</summary>
    private bool _isPreparing;
    public bool IsPreparing
    {
        get => _isPreparing;
        set => Set(ref _isPreparing, value);
    }

    public bool HasWorktree => !string.IsNullOrEmpty(_branch);

    private bool _showEnvironmentPanel = true;
    /// <summary>Whether the floating Environment popover is shown for this session.</summary>
    public bool ShowEnvironmentPanel
    {
        get => _showEnvironmentPanel;
        set => Set(ref _showEnvironmentPanel, value);
    }

    // ---- conversation / timeline / files / plan ----
    public ObservableCollection<ConversationBlock> Conversation { get; } = [];
    public ObservableCollection<TimelineItemVM> Timeline { get; } = [];
    public ObservableCollection<FileNode> Files { get; } = [];
    public ObservableCollection<PlanItemVM> Plan { get; } = [];

    /// <summary>Changed files (the review surface). Refreshed after each turn and on activate.</summary>
    public ObservableCollection<ChangeItem> Changes { get; } = [];

    private bool _hasChanges;
    public bool HasChanges
    {
        get => _hasChanges;
        set => Set(ref _hasChanges, value);
    }

    // ---- per-turn working state (mutated by the engine-event handlers) ----
    public WorkedForBlock? CurrentWorked { get; set; }
    public DiffCardBlock? PendingDiff { get; set; }
    public Queue<(WorkedStep Step, TimelineItemVM Item)> RunningTools { get; } = new();
    public HashSet<string> ModifiedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public TaskCompletionSource<ApprovalDecision>? ApprovalCompletion { get; set; }

    /// <summary>Routed (@mention) prompts waiting for this session to finish its current turn; drained
    /// one at a time when a turn completes. Only populated for members that were busy when dispatched.</summary>
    public Queue<string> PendingPrompts { get; } = new();

    /// <summary>For an ecosystem chat: the currently-running member turn cards in its feed, keyed by
    /// member display name (the tee appends steps to these; removed when the turn finishes).</summary>
    public Dictionary<string, MemberTurnCardBlock> RunningTurnCards { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ---- status ----
    /// <summary>True for the one session currently shown in the main view (set by SessionManager).</summary>
    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

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

    // ---- title / project ----
    private string _chatTitle = "New session";
    public string ChatTitle
    {
        get => _chatTitle;
        set
        {
            if (Set(ref _chatTitle, value) && !_isEditingTitleHeader && !_isEditingTitleSidebar)
            {
                EditableTitle = value;
            }
        }
    }

    // The top-bar editor and the sidebar WORKSPACES-row editor need separate flags: one shared
    // flag opens both TextBoxes at once and their focus grabs cancel each other (the loser's
    // LostKeyboardFocus commits and closes the edit ~20ms after it opens).
    private bool _isEditingTitleHeader;
    public bool IsEditingTitleHeader
    {
        get => _isEditingTitleHeader;
        set => Set(ref _isEditingTitleHeader, value);
    }

    private bool _isEditingTitleSidebar;
    public bool IsEditingTitleSidebar
    {
        get => _isEditingTitleSidebar;
        set => Set(ref _isEditingTitleSidebar, value);
    }

    private string _editableTitle = "New session";
    public string EditableTitle
    {
        get => _editableTitle;
        set => Set(ref _editableTitle, value);
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

    // ---- session meta (workspace tab) ----
    private string _sessionId = "";
    public string SessionId { get => _sessionId; set => Set(ref _sessionId, value); }

    private string _sessionModel = "";
    public string SessionModel { get => _sessionModel; set => Set(ref _sessionModel, value); }

    private string _sessionRoot = "";
    public string SessionRoot { get => _sessionRoot; set => Set(ref _sessionRoot, value); }

    // ---- usage ----
    private int _inputTokens;
    private int _outputTokens;
    private int _contextWindow = 200_000;

    // Usage carried over from prior process runs, restored on reopen. The backend's CumulativeUsage only
    // counts THIS process's turns, so the true running total (shown + persisted) is baseline + it.
    public int RestoredInputBaseline { get; set; }
    public int RestoredOutputBaseline { get; set; }
    public int TotalInputTokens => RestoredInputBaseline + (Backend?.CumulativeUsage.InputTokens ?? 0);
    public int TotalOutputTokens => RestoredOutputBaseline + (Backend?.CumulativeUsage.OutputTokens ?? 0);

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

    // ---- approval ----
    private ApprovalVM? _approval;
    public ApprovalVM? Approval
    {
        get => _approval;
        set { if (Set(ref _approval, value)) { Raise(nameof(HasApproval)); } }
    }

    public bool HasApproval => _approval is not null;

    private string _resolvedStatus = "";
    public string ResolvedStatus
    {
        get => _resolvedStatus;
        set { if (Set(ref _resolvedStatus, value)) { Raise(nameof(HasResolvedStatus)); } }
    }

    public bool HasResolvedStatus => !string.IsNullOrEmpty(_resolvedStatus);

    // ---- plan / todo checklist ----
    private bool _hasPlan;
    public bool HasPlan
    {
        get => _hasPlan;
        set => Set(ref _hasPlan, value);
    }

    // ---- helpers ----
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

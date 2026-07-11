using System.Collections.ObjectModel;
using System.Windows.Input;

namespace AutoCode.Desktop.ViewModels;

/// <summary>An ecosystem row in the sidebar. Serves two renderings, both rebuilt wholesale on
/// registry changes: the bare flat list (Phase 1) uses only Id/Name/MemberCountText; the grouped
/// view (Phase 2) additionally nests its member projects and can expand/collapse.</summary>
public sealed class EcosystemNode : ObservableObject
{
    private bool _isExpanded = true;

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string MemberCountText { get; init; } = "";

    /// <summary>Whether the ecosystem has member projects (drives the grouped empty-state hint).</summary>
    public bool HasProjects { get; init; } = true;

    /// <summary>Localized hint shown inside an expanded empty group ("Right-click any project → Add to …").</summary>
    public string EmptyHint { get; init; } = "";

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>Member projects nested under this ecosystem in the grouped view (empty in the bare list).</summary>
    public ObservableCollection<ProjectNode> Projects { get; } = [];

    public ICommand? ToggleCommand { get; set; }
}

/// <summary>A project row in the sidebar that groups its sessions.</summary>
public sealed class ProjectNode : ObservableObject
{
    private bool _isExpanded;

    public string Name { get; init; } = "";
    public string Path { get; init; } = "";

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public ObservableCollection<SessionNode> Sessions { get; } = [];

    public ICommand? ToggleCommand { get; set; }
}

/// <summary>A session row nested under a project.</summary>
public sealed class SessionNode : ObservableObject
{
    private bool _isActive;
    private bool _isEditingTitle;
    private string _relativeTime = "";
    private string _title = "";
    private string _editableTitle = "";

    public string Id { get; init; } = "";

    public string Title
    {
        get => _title;
        set
        {
            if (Set(ref _title, value) && !_isEditingTitle)
            {
                EditableTitle = value;
            }
        }
    }

    public string EditableTitle
    {
        get => _editableTitle;
        set => Set(ref _editableTitle, value);
    }

    public bool IsEditingTitle
    {
        get => _isEditingTitle;
        set => Set(ref _isEditingTitle, value);
    }

    public string ProjectRoot { get; init; } = "";
    public string SessionDir { get; init; } = "";
    public string Model { get; init; } = "";
    public string AgentId { get; init; } = "builtin";
    public string? ExternalResumeId { get; init; }
    public string? ModeWire { get; init; }
    public DateTimeOffset StartedAt { get; init; }

    public string? GitBranch { get; init; }
    public string? GitWorktreePath { get; init; }
    public string? GitBaseBranch { get; init; }

    public string RelativeTime
    {
        get => _relativeTime;
        set => Set(ref _relativeTime, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public ICommand? OpenCommand { get; set; }
}

/// <summary>A tool-timeline card on the Run tab.</summary>
public sealed class TimelineItemVM : ObservableObject
{
    private string _status = "running";
    private string _summary = "";
    private long _durationMs;

    public string ToolName { get; init; } = "";

    /// <summary>"running" | "done" | "error".</summary>
    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string Summary
    {
        get => _summary;
        set => Set(ref _summary, value);
    }

    public long DurationMs
    {
        get => _durationMs;
        set => Set(ref _durationMs, value);
    }
}

/// <summary>The current pending approval shown on the Run tab.</summary>
public sealed class ApprovalVM : ObservableObject
{
    public string ToolName { get; init; } = "";
    public string Target { get; init; } = "";
    public ObservableCollection<PreviewLine> PreviewLines { get; } = [];

    /// <summary>dispatch_to_member approvals swap the raw preview for a "Send task to ‹member›" body.</summary>
    public bool IsDispatch { get; init; }
    public string DispatchMember { get; init; } = "";
    public string DispatchTask { get; init; } = "";
    public string DispatchContext { get; init; } = "";
}

/// <summary>A single line of an approval preview, classified for diff colouring.</summary>
public sealed class PreviewLine
{
    public string Text { get; init; } = "";

    /// <summary>"add" | "del" | "ctx".</summary>
    public string Kind { get; init; } = "ctx";
}

/// <summary>A checklist row on the Plan tab. Status: pending | in_progress | completed | interrupted.</summary>
public sealed class PlanItemVM
{
    public string Text { get; init; } = "";

    public string Status { get; init; } = "pending";
}

/// <summary>A changed file on the Changes (review) tab. Status: A | M | D | R | C.</summary>
public sealed class ChangeItem
{
    public string Status { get; init; } = "M";

    public string Path { get; init; } = "";
}

/// <summary>A row in the Workspace file tree.</summary>
public sealed class FileNode
{
    public string Name { get; init; } = "";
    public int Depth { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsModified { get; init; }

    public double Indent => Depth * 16 + 6;
}

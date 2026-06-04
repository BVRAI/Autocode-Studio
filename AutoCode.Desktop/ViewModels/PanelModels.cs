using System.Collections.ObjectModel;
using System.Windows.Input;

namespace AutoCode.Desktop.ViewModels;

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
    private string _relativeTime = "";

    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string ProjectRoot { get; init; } = "";
    public string SessionDir { get; init; } = "";

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
}

/// <summary>A single line of an approval preview, classified for diff colouring.</summary>
public sealed class PreviewLine
{
    public string Text { get; init; } = "";

    /// <summary>"add" | "del" | "ctx".</summary>
    public string Kind { get; init; } = "ctx";
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

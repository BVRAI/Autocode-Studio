using System.Collections.ObjectModel;

namespace AutoCode.Desktop.ViewModels;

/// <summary>Base for a rendered conversation block (selected via ConversationTemplateSelector).</summary>
public abstract class ConversationBlock : ObservableObject
{
}

/// <summary>Right-aligned user message bubble.</summary>
public sealed class UserBubbleBlock : ConversationBlock
{
    public string Text { get; init; } = "";
}

/// <summary>Assistant prose; Text is split into paragraph / path-block segments for rendering.</summary>
public sealed class AssistantBlock : ConversationBlock
{
    private string _text = "";

    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value))
            {
                RebuildSegments();
            }
        }
    }

    public ObservableCollection<object> Segments { get; } = [];

    private void RebuildSegments()
    {
        Segments.Clear();
        foreach (var seg in Misc.InlineParser.SplitSegments(_text))
        {
            Segments.Add(seg);
        }
    }
}

/// <summary>A single tool step inside a "Worked for…" group.</summary>
public sealed class WorkedStep : ObservableObject
{
    private string _status = "running";
    private string _detail = "";

    public string Tool { get; init; } = "";

    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    /// <summary>"running" | "done" | "error".</summary>
    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }
}

/// <summary>Collapsible group summarising the tools run between assistant turns.</summary>
public sealed class WorkedForBlock : ConversationBlock
{
    private string _label = "Working…";
    private bool _isExpanded = true;

    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public ObservableCollection<WorkedStep> Steps { get; } = [];

    /// <summary>Wall-clock start, used to synthesize the "Worked for {elapsed}" label.</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>One file row inside a diff card.</summary>
public sealed class DiffFileRow
{
    public string Path { get; init; } = "";
    public int Adds { get; init; }
    public int Dels { get; init; }
    public string CountsText => $"+{Adds} −{Dels}";
}

/// <summary>"Edited N files" diff summary card.</summary>
public sealed class DiffCardBlock : ConversationBlock
{
    public ObservableCollection<DiffFileRow> Files { get; } = [];

    public string Title => Files.Count == 1 ? "Edited 1 file" : $"Edited {Files.Count} files";

    public int Adds => Files.Sum(f => f.Adds);

    public int Dels => Files.Sum(f => f.Dels);

    public string AddsText => $"+{Adds}";

    public string DelsText => $"−{Dels}";

    public void Refresh()
    {
        Raise(nameof(Title));
        Raise(nameof(Adds));
        Raise(nameof(Dels));
        Raise(nameof(AddsText));
        Raise(nameof(DelsText));
    }
}

/// <summary>Amber/info notice banner (e.g. verification failure, tool error).</summary>
public sealed class NoticeBlock : ConversationBlock
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>Transient "Working…" spinner row.</summary>
public sealed class WorkingBlock : ConversationBlock
{
}

/// <summary>An attributed entry in an ecosystem chat's feed: activity from a member session, teed in.
/// <see cref="Kind"/> is start | tool | message | finish | error (drives the template accent).</summary>
public sealed class MemberActivityBlock : ConversationBlock
{
    public string Member { get; init; } = "";
    public string Summary { get; init; } = "";
    public string? Detail { get; init; }
    public string Kind { get; init; } = "activity";
}

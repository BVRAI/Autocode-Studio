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

/// <summary>A standalone attributed entry in an ecosystem chat's feed. <see cref="Kind"/> is
/// report (milestone — accent card) | error (amber card) | queued (ghost row). Routine turn
/// activity folds into <see cref="MemberTurnCardBlock"/> instead (design spec §2).</summary>
public sealed class MemberActivityBlock : ConversationBlock
{
    public string Member { get; init; } = "";
    public string Summary { get; init; } = "";
    public string? Detail { get; init; }
    public string Kind { get; init; } = "activity";
    public string TimeText { get; init; } = "";
}

/// <summary>One member turn folded into a single feed card: header = member chip + live status
/// ("working · Ns" → "finished · Ns · N steps"); body = tool/message steps, newest 3 while running
/// with an "N earlier steps" expander; auto-collapses to the header when the turn finishes.</summary>
public sealed class MemberTurnCardBlock : ConversationBlock
{
    private bool _isRunning = true;
    private string _statusText = "";
    private bool _isExpanded;
    private int _earlierCount;

    public string Member { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    public bool IsRunning
    {
        get => _isRunning;
        set => Set(ref _isRunning, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    /// <summary>Expanded shows every step; a finished card rests collapsed to its 30px header.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>Steps hidden behind the "N earlier steps" line while running.</summary>
    public int EarlierCount
    {
        get => _earlierCount;
        set { if (Set(ref _earlierCount, value)) { Raise(nameof(HasEarlier)); Raise(nameof(EarlierText)); } }
    }

    public bool HasEarlier => _earlierCount > 0;

    public string EarlierText => Loc.F("Feed_EarlierSteps", _earlierCount);

    public ObservableCollection<MemberTurnStep> Steps { get; } = [];

    /// <summary>The newest ≤3 steps (the running view); maintained by <see cref="AddStep"/>.</summary>
    public ObservableCollection<MemberTurnStep> VisibleSteps { get; } = [];

    public void AddStep(MemberTurnStep step)
    {
        Steps.Add(step);
        VisibleSteps.Add(step);
        while (VisibleSteps.Count > 3)
        {
            VisibleSteps.RemoveAt(0);
        }

        EarlierCount = Steps.Count - VisibleSteps.Count;
    }
}

/// <summary>A single line inside a member turn card: a tool call (glyph + name + detail) or a
/// spoken message (dot + text). GlyphKey names an Icons.xaml geometry resource.</summary>
public sealed class MemberTurnStep
{
    public bool IsMessage { get; init; }
    public string GlyphKey { get; init; } = "IconCode";
    public string ToolName { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Text { get; init; } = "";
}

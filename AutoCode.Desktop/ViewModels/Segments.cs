namespace AutoCode.Desktop.ViewModels;

/// <summary>A wrapped prose paragraph inside an assistant message (rendered with rich inlines).</summary>
public sealed class ParagraphSegment
{
    public string Markup { get; init; } = "";
}

/// <summary>A standalone full-width monospace path / fenced block inside an assistant message.</summary>
public sealed class PathBlockSegment
{
    public string Text { get; init; } = "";
}

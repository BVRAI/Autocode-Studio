using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AutoCode.Desktop.Controls;
using AutoCode.Desktop.ViewModels;

namespace AutoCode.Desktop.Misc;

/// <summary>
/// Splits assistant text into paragraph / path-block segments, and builds rich WPF inlines
/// (code chips, file-reference links) for a paragraph. No full markdown — prose + code spans.
/// </summary>
public static partial class InlineParser
{
    /// <summary>Raised when a file-reference link is clicked (path, optional 1-based line).</summary>
    public static event Action<string, int?>? FileRefRequested;

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"([A-Za-z0-9_./\\-]+\.(?:cs|xaml|csproj|sln|json|md|yml|yaml|txt|js|ts|tsx|jsx|py|cpp|hpp|h|html|css|sh|ps1|xml|toml|ini|log))(\s*\(line\s*(\d+)\))?", RegexOptions.IgnoreCase)]
    private static partial Regex FileRef();

    public static IEnumerable<object> SplitSegments(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var para = new List<string>();
        var fence = new List<string>();
        var inFence = false;

        ParagraphSegment? FlushPara()
        {
            if (para.Count == 0)
            {
                return null;
            }

            var seg = new ParagraphSegment { Markup = string.Join(' ', para) };
            para.Clear();
            return seg;
        }

        foreach (var raw in lines)
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inFence)
                {
                    yield return new PathBlockSegment { Text = string.Join('\n', fence) };
                    fence.Clear();
                    inFence = false;
                }
                else
                {
                    if (FlushPara() is { } p) { yield return p; }
                    inFence = true;
                }

                continue;
            }

            if (inFence)
            {
                fence.Add(raw);
                continue;
            }

            var t = raw.Trim();
            if (t.Length == 0)
            {
                if (FlushPara() is { } p) { yield return p; }
                continue;
            }

            if (IsStandalonePath(t))
            {
                if (FlushPara() is { } p) { yield return p; }
                yield return new PathBlockSegment { Text = t };
                continue;
            }

            para.Add(t);
        }

        if (FlushPara() is { } last) { yield return last; }
        if (inFence && fence.Count > 0) { yield return new PathBlockSegment { Text = string.Join('\n', fence) }; }
    }

    private static bool IsStandalonePath(string t)
    {
        if (t.Length > 200 || t.Contains(' '))
        {
            return false;
        }

        return t.StartsWith('%')
            || t.Contains('\\')
            || (t.Contains('/') && t.Contains('.'))
            || DriveRooted().IsMatch(t);
    }

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex DriveRooted();

    /// <summary>Build inlines for a single paragraph's markup.</summary>
    public static IEnumerable<Inline> BuildInlines(string markup)
    {
        // First split on code spans, then resolve file refs in the non-code parts.
        var pos = 0;
        foreach (Match m in CodeSpan().Matches(markup))
        {
            if (m.Index > pos)
            {
                foreach (var inl in NonCode(markup[pos..m.Index]))
                {
                    yield return inl;
                }
            }

            yield return CodeChip(m.Groups[1].Value);
            pos = m.Index + m.Length;
        }

        if (pos < markup.Length)
        {
            foreach (var inl in NonCode(markup[pos..]))
            {
                yield return inl;
            }
        }
    }

    private static IEnumerable<Inline> NonCode(string text)
    {
        var pos = 0;
        foreach (Match m in FileRef().Matches(text))
        {
            if (m.Index > pos)
            {
                yield return new Run(text[pos..m.Index]);
            }

            var path = m.Groups[1].Value;
            int? line = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null;
            yield return FileLink(m.Value, path, line);
            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
        {
            yield return new Run(text[pos..]);
        }
    }

    private static Inline CodeChip(string code)
    {
        var border = new Border
        {
            Background = Brush("CodeBgBrush"),
            BorderBrush = Brush("BorderBrush2"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(1, 0, 1, 0),
            Child = new TextBlock
            {
                Text = code,
                FontFamily = Mono(),
                FontSize = 12.5,
                Foreground = Brush("CodeTextBrush"),
            },
        };

        return new InlineUIContainer(border) { BaselineAlignment = BaselineAlignment.Center };
    }

    private static Inline FileLink(string display, string path, int? line)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(new IconGlyph
        {
            Geometry = App.Current?.TryFindResource("IconCode") as Geometry,
            Width = 14,
            Height = 14,
            Foreground = Brush("AccentBrush"),
            Margin = new Thickness(0, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.8,
        });
        stack.Children.Add(new TextBlock
        {
            Text = display,
            Foreground = Brush("AccentBrush"),
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var container = new InlineUIContainer(new Button
        {
            Content = stack,
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Style = App.Current?.TryFindResource("LinkButtonStyle") as Style,
        })
        { BaselineAlignment = BaselineAlignment.Center };

        if (container.Child is Button b)
        {
            b.Click += (_, _) => FileRefRequested?.Invoke(path, line);
        }

        return container;
    }

    private static Brush Brush(string key) => App.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    private static FontFamily Mono() => App.Current?.TryFindResource("MonoFontFamily") as FontFamily ?? new FontFamily("Consolas");
}

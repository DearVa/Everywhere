using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat.Plugins;

namespace Everywhere.Views;

/// <summary>
/// Represents one virtualizable row in a unified text-difference view.
/// </summary>
public abstract class TextDifferenceLine;

/// <summary>
/// Represents a row that displays text content.
/// </summary>
public abstract class TextDifferenceContentLine(string text) : TextDifferenceLine
{
    public string Text { get; } = text;
}

/// <summary>
/// Represents an unchanged source row with matching old and new line numbers.
/// </summary>
public sealed class TextDifferenceContextLine(
    int oldLineNumber,
    int newLineNumber,
    string text
) : TextDifferenceContentLine(text)
{
    public int OldLineNumber { get; } = oldLineNumber;

    public int NewLineNumber { get; } = newLineNumber;
}

/// <summary>
/// Represents a row belonging to one reviewable text change.
/// </summary>
public abstract class TextDifferenceChangedLine(
    string text,
    TextChange change
) : TextDifferenceContentLine(text)
{
    public TextChange Change { get; } = change;
}

/// <summary>
/// Represents a row added to the proposed content.
/// </summary>
public sealed class TextDifferenceAddedLine(
    int newLineNumber,
    string text,
    TextChange change
) : TextDifferenceChangedLine(text, change)
{
    public int NewLineNumber { get; } = newLineNumber;
}

/// <summary>
/// Represents a row removed from the original content.
/// </summary>
public sealed class TextDifferenceRemovedLine(
    int oldLineNumber,
    string text,
    TextChange change
) : TextDifferenceChangedLine(text, change)
{
    public int OldLineNumber { get; } = oldLineNumber;
}

/// <summary>
/// Represents a folded range of unchanged source rows that can be expanded on demand.
/// </summary>
[ObservableObject]
public sealed partial class TextDifferenceOmittedLine(
    int lineCount,
    int startLineIndex,
    int endLineIndex,
    int oldLineNumber,
    int newLineNumber
) : TextDifferenceLine
{
    public int LineCount { get; } = lineCount;

    internal int StartLineIndex { get; } = startLineIndex;

    internal int EndLineIndex { get; } = endLineIndex;

    internal int OldLineNumber { get; } = oldLineNumber;

    internal int NewLineNumber { get; } = newLineNumber;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }
}

/// <summary>
/// Describes one reviewable change in fixed display-row coordinates.
/// </summary>
/// <remarks>
/// The visual range may overlap adjacent regions to include nearby context. The interaction range
/// is partitioned between adjacent changes so pointer hit testing remains deterministic.
/// </remarks>
public sealed partial class TextDifferenceRegionItem(
    TextChange change,
    int visualStartRow,
    int visualEndRow,
    int interactionStartRow,
    int interactionEndRow
) : ObservableObject
{
    public TextChange Change { get; } = change;

    public int VisualStartRow { get; } = visualStartRow;

    public int VisualEndRow { get; } = visualEndRow;

    public int VisualRowSpan => VisualEndRow - VisualStartRow;

    public int InteractionStartRow { get; } = interactionStartRow;

    public int InteractionEndRow { get; } = interactionEndRow;

    [ObservableProperty]
    public partial Thickness InteractionMargin { get; internal set; }

    [ObservableProperty]
    public partial double StickyActionOffset { get; internal set; }

    internal void UpdateLayoutMetrics(double lineHeight, double viewportTop, double actionHeight, double topInset)
    {
        var top = (InteractionStartRow - VisualStartRow) * lineHeight;
        var bottom = (VisualEndRow - InteractionEndRow) * lineHeight;
        InteractionMargin = new Thickness(0, top, 0, bottom);

        var regionTop = VisualStartRow * lineHeight;
        var regionHeight = VisualRowSpan * lineHeight;
        var maximumOffset = Math.Max(0, regionHeight - actionHeight);
        StickyActionOffset = Math.Clamp(viewportTop + topInset - regionTop, 0, maximumOffset);
    }
}
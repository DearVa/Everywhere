using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffPlex;
using DiffPlex.Chunkers;
using Everywhere.Collections;
using Everywhere.Common;
using MessagePack;

namespace Everywhere.Chat.Plugins;

public enum TextChangeKind
{
    Insert = 0,
    Delete = 1,
    Replace = 2
}

/// <summary>
/// Describes the file operation represented by a text-difference review.
/// </summary>
public enum TextDifferenceReviewKind
{
    Update = 0,
    Create = 1,
    Delete = 2,
    MoveAndUpdate = 3,
    Move = 4
}

/// <summary>
/// A half-open character range over the original text \[Start, End).
/// Offsets are 0-based and refer to the original file content.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true)]
public readonly partial record struct TextRange
{
    [Key(0)]
    public int Start { get; }

    [Key(1)]
    public int Length { get; }

    public int End => Start + Length;

    [SerializationConstructor]
    public TextRange(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Start = start;
        Length = length;
    }

    public static TextRange FromBounds(int start, int end)
    {
        if (end < start) throw new ArgumentOutOfRangeException(nameof(end));
        return new TextRange(start, end - start);
    }

    public void EnsureInside(string original)
    {
        if (Start > original.Length || End > original.Length)
            throw new ArgumentOutOfRangeException($"Range [{Start},{End}) is outside original length {original.Length}.");
    }

    public override string ToString() => $"[{Start},{End})";
}

/// <summary>
/// A single edit on the original text. Offsets refer to the original content.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class TextChange : ObservableObject
{
    [Key(0)]
    public string Id { get; private set; } = Guid.CreateVersion7().ToString("N");

    [Key(1)]
    public TextChangeKind Kind { get; private set; }

    [Key(2)]
    public TextRange Range { get; private set; }

    /// <summary>
    /// Replacement text for Insert/Replace; null for Delete.
    /// </summary>
    [Key(3)]
    public string? NewText { get; private set; }

    /// <summary>
    /// Preserves the previous nullable MessagePack field.
    /// A legacy null value is normalized to accepted during deserialization and is never exposed as active review state.
    /// </summary>
    [Key(4)]
    private bool? SerializedAcceptance
    {
        get => IsAccepted;
        set => IsAccepted = value ?? true;
    }

    /// <summary>
    /// Gets or sets whether the change will be applied.
    /// </summary>
    [IgnoreMember]
    [ObservableProperty]
    public partial bool IsAccepted { get; set; } = true;

    /// <summary>
    /// Gets or sets the optional user comment associated with this change.
    /// </summary>
    [Key(5)]
    [ObservableProperty]
    public partial string? ReviewComment { get; set; }

    public static TextChange Insert(int at, string? text) => new()
    {
        Kind = TextChangeKind.Insert,
        Range = new TextRange(at, 0),
        NewText = text
    };

    public static TextChange Delete(int start, int length) => new()
    {
        Kind = TextChangeKind.Delete,
        Range = new TextRange(start, length)
    };

    public static TextChange Replace(int start, int length, string? newText) => new()
    {
        Kind = TextChangeKind.Replace,
        Range = new TextRange(start, length),
        NewText = newText
    };

    public string GetOriginalSlice(string original)
    {
        Range.EnsureInside(original);
        return original.Substring(Range.Start, Range.Length);
    }

    public override string ToString() => $"{Kind} id={Id} range={Range} accepted={IsAccepted.ToString().ToLowerInvariant()}";
}

/// <summary>
/// Defines a text difference between two versions of text.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class TextDifference : ObservableObject, IDisposable
{
    [Key(0)]
    public string FilePath { get; }

    /// <summary>
    /// A read-only, dispatcher-bound projection of changes for UI binding.
    /// Synchronous consumers should use the methods on this type, which read stable source-list snapshots.
    /// </summary>
    [IgnoreMember]
    public IReadOnlyBindableList<TextChange> Changes { get; }

    /// <summary>
    /// For serialization purposes only.
    /// </summary>
    [Key(1)]
    private IEnumerable<TextChange> SerializableChanges
    {
        get => _changesSource.Items;
        set => _changesSource.Reset(value);
    }

    public int TotalChangesCount => _changesSource.Count;

    public int AcceptedChangesCount => _changesSource.Count(static change => change.IsAccepted);

    public int RejectedChangesCount => TotalChangesCount - AcceptedChangesCount;

    public int CommentedChangesCount => _changesSource.Count(static change => !string.IsNullOrWhiteSpace(change.ReviewComment));

    [IgnoreMember] private readonly CompositeDisposable _disposables = new(4);
    [IgnoreMember] private readonly SourceList<TextChange> _changesSource = new();

    public TextDifference(string filePath)
    {
        FilePath = filePath;

        _changesSource.Connect()
            .WhenPropertyChanged(x => x.IsAccepted)
            .Subscribe(_ => NotifyChangesPropertiesChanged())
            .AddTo(_disposables);

        _changesSource.Connect()
            .WhenPropertyChanged(x => x.ReviewComment)
            .Subscribe(_ => NotifyChangesPropertiesChanged())
            .AddTo(_disposables);

        Changes = _changesSource.Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);

        _disposables.Add(_changesSource);
    }

    private void NotifyChangesPropertiesChanged()
    {
        OnPropertyChanged(nameof(TotalChangesCount));
        OnPropertyChanged(nameof(AcceptedChangesCount));
        OnPropertyChanged(nameof(RejectedChangesCount));
        OnPropertyChanged(nameof(CommentedChangesCount));
    }

    public void AddRange(params IEnumerable<TextChange> changes)
    {
        _changesSource.AddRange(changes);
    }

    public void AcceptAll() => SetAll(true);

    public void RejectAll() => SetAll(false);

    /// <summary>
    /// Get changes filtered according to the given options.
    /// </summary>
    /// <param name="onlyAccepted"></param>
    /// <returns></returns>
    public IEnumerable<TextChange> GetFilteredChanges(bool onlyAccepted)
    {
        IEnumerable<TextChange> q = _changesSource.Items;
        if (onlyAccepted) q = q.Where(static change => change.IsAccepted);
        return q.OrderBy(c => c.Range.Start);
    }

    public void ValidateAgainst(string original)
    {
        foreach (var c in _changesSource.Items) c.Range.EnsureInside(original);
        var ordered = _changesSource.Items.AsValueEnumerable().OrderBy(c => c.Range.Start).ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            var prev = ordered[i - 1];
            var curr = ordered[i];
            if (prev.Range.End > curr.Range.Start)
                throw new InvalidOperationException($"Overlapping changes: {prev.Id} {prev.Range} and {curr.Id} {curr.Range}");
        }
    }

    public string Apply(string original, Func<TextChange, bool>? selector = null, bool validate = true)
    {
        if (validate) ValidateAgainst(original);
        var selected = _changesSource.Items
            .AsValueEnumerable()
            .Where(c => selector?.Invoke(c) ?? c.IsAccepted)
            .OrderBy(c => c.Range.Start)
            .ToArray();

        var sb = new StringBuilder();
        var cursor = 0;
        foreach (var c in selected)
        {
            sb.Append(original, cursor, c.Range.Start - cursor);
            sb.Append(c.NewText);
            cursor = c.Range.End;
        }
        sb.Append(original, cursor, original.Length - cursor);
        return sb.ToString();
    }

    public static int CountLines(string? s) => s.IsNullOrEmpty() ? 0 : TakeLines(s, -1).Count();

    private static IEnumerable<string> TakeLines(string s, int maxLines)
    {
        using var reader = new StringReader(s);
        while (maxLines-- != 0) // use != to allow -1 (unlimited)
        {
            var line = reader.ReadLine();
            if (line is null) yield break;
            yield return line;
        }
    }

    private void SetAll(bool accepted)
    {
        _changesSource.Edit(list =>
        {
            foreach (var c in list) c.IsAccepted = accepted;
        });
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}

/// <summary>
/// Builds reviewable, original-relative text changes from two complete text versions.
/// </summary>
public static class TextDifferenceBuilder
{
    /// <summary>
    /// Adds one <see cref="TextChange"/> for each contiguous line-level difference.
    /// Line endings participate in comparison and are preserved in replacement text.
    /// </summary>
    public static void BuildLineDiff(TextDifference diff, string original, string updated)
    {
        diff.AddRange(BuildLineChanges(original, updated));
        diff.ValidateAgainst(original);
    }

    /// <summary>
    /// Builds a complete synchronous snapshot of the contiguous line-level changes between two text versions.
    /// </summary>
    internal static TextChange[] BuildLineChanges(string original, string updated)
    {
        var result = Differ.Instance.CreateDiffs(
            original,
            updated,
            ignoreWhiteSpace: false,
            ignoreCase: false,
            LineEndingsPreservingChunker.Instance);
        var originalOffsets = BuildOffsets(result.PiecesOld);
        var changes = new TextChange[result.DiffBlocks.Count];

        for (var index = 0; index < result.DiffBlocks.Count; index++)
        {
            var block = result.DiffBlocks[index];
            var start = originalOffsets[block.DeleteStartA];
            var end = originalOffsets[block.DeleteStartA + block.DeleteCountA];
            var newText = string.Concat(
                result.PiecesNew.Skip(block.InsertStartB).Take(block.InsertCountB));

            if (block.DeleteCountA == 0)
            {
                changes[index] = TextChange.Insert(start, newText);
            }
            else if (block.InsertCountB == 0)
            {
                changes[index] = TextChange.Delete(start, end - start);
            }
            else
            {
                changes[index] = TextChange.Replace(start, end - start, newText);
            }
        }

        return changes;
    }

    private static int[] BuildOffsets(IReadOnlyList<string> pieces)
    {
        var offsets = new int[pieces.Count + 1];
        for (var index = 0; index < pieces.Count; index++)
        {
            offsets[index + 1] = offsets[index] + pieces[index].Length;
        }

        return offsets;
    }
}

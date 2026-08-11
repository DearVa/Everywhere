using System.Buffers;
using Everywhere.Chat.Plugins;
using Everywhere.Views;
using MessagePack;

namespace Everywhere.Core.Tests.Chat;

public class TextDifferenceTests
{
    [Test]
    public void Add_NewChange_DefaultsToAccepted()
    {
        using var difference = new TextDifference("file.txt");
        difference.AddRange(TextChange.Replace(0, 3, "updated"));

        Assert.Multiple(() =>
        {
            Assert.That(difference.AcceptedChangesCount, Is.EqualTo(1));
            Assert.That(difference.RejectedChangesCount, Is.Zero);
            Assert.That(difference.GetFilteredChanges(default).Single().IsAccepted, Is.True);
        });
    }

    [Test]
    public void ReviewComment_IsStoredOnItsTextChange()
    {
        using var difference = new TextDifference("file.txt");
        var change = TextChange.Replace(0, 3, "updated");
        difference.AddRange(change);

        change.ReviewComment = "Keep the old API name.";

        Assert.Multiple(() =>
        {
            Assert.That(difference.CommentedChangesCount, Is.EqualTo(1));
            Assert.That(difference.GetFilteredChanges(default).Single().ReviewComment, Is.EqualTo("Keep the old API name."));
        });
    }

    [Test]
    public void FileLevelCommands_UpdateSelectionWithoutSubmittingUntilConfirmed()
    {
        using var difference = new TextDifference("file.txt");
        difference.AddRange(TextChange.Replace(0, 3, "new"));
        var confirmations = 0;
        var editor = new TextDifferenceEditor
        {
            TextDifference = difference,
            OriginalText = "old"
        };
        editor.ReviewConfirmed += (_, _) => confirmations++;

        editor.RejectAllCommand.Execute(null);
        Assert.Multiple(() =>
        {
            Assert.That(difference.AcceptedChangesCount, Is.Zero);
            Assert.That(confirmations, Is.Zero);
        });

        editor.AcceptAllCommand.Execute(null);
        editor.ConfirmCommand.Execute(null);

        Assert.Multiple(() =>
        {
            Assert.That(difference.AcceptedChangesCount, Is.EqualTo(1));
            Assert.That(confirmations, Is.EqualTo(1));
        });
    }

    [Test]
    public void BuildRegions_WhenVisualRangesOverlap_PartitionsInteractionAtNearestBoundary()
    {
        var first = TextChange.Replace(0, 1, "first");
        var second = TextChange.Replace(2, 1, "second");
        var rows = new TextDifferenceLine[]
        {
            new TextDifferenceContextLine(1, 1, "before"),
            new TextDifferenceRemovedLine(2, "old first", first),
            new TextDifferenceAddedLine(2, "new first", first),
            new TextDifferenceContextLine(3, 3, "between"),
            new TextDifferenceRemovedLine(4, "old second", second),
            new TextDifferenceAddedLine(4, "new second", second),
            new TextDifferenceContextLine(5, 5, "after")
        };

        var regions = TextDifferenceProjectionBuilder.BuildRegions(rows);

        Assert.Multiple(() =>
        {
            Assert.That(regions, Has.Count.EqualTo(2));
            Assert.That((regions[0].VisualStartRow, regions[0].VisualEndRow), Is.EqualTo((0, 5)));
            Assert.That((regions[1].VisualStartRow, regions[1].VisualEndRow), Is.EqualTo((2, 7)));
            Assert.That((regions[0].InteractionStartRow, regions[0].InteractionEndRow), Is.EqualTo((0, 4)));
            Assert.That((regions[1].InteractionStartRow, regions[1].InteractionEndRow), Is.EqualTo((4, 7)));
        });
    }

    [TestCase(20, 40, 164)]
    [TestCase(19.5, 39, 159)]
    public void UpdateLayoutMetrics_WhenRegionTopScrollsAway_ClampsStickyActionToRegionBottom(
        double lineHeight,
        double expectedInteractionMargin,
        double expectedStickyOffset)
    {
        var region = new TextDifferenceRegionItem(TextChange.Replace(0, 1, "new"), 10, 20, 12, 18);

        region.UpdateLayoutMetrics(lineHeight, viewportTop: 400, actionHeight: 36, topInset: 4);

        Assert.Multiple(() =>
        {
            Assert.That(region.InteractionMargin, Is.EqualTo(new Avalonia.Thickness(0, expectedInteractionMargin)));
            Assert.That(region.StickyActionOffset, Is.EqualTo(expectedStickyOffset));
        });
    }

    [Test]
    public void Apply_WithPartialSelection_AppliesOnlyAcceptedChanges()
    {
        using var difference = new TextDifference("file.txt");
        var accepted = TextChange.Replace(0, 3, "ONE");
        var rejected = TextChange.Replace(8, 3, "TWO");
        rejected.IsAccepted = false;
        rejected.ReviewComment = "Keep the second value.";
        difference.AddRange(accepted, rejected);

        var result = difference.Apply("one --- two");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo("ONE --- two"));
            Assert.That(difference.AcceptedChangesCount, Is.EqualTo(1));
            Assert.That(difference.RejectedChangesCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void FileDifferenceDisplayBlock_RoundTrip_PreservesOnlyCompletedSummary()
    {
        using var difference = new TextDifference("destination.txt");
        var change = TextChange.Replace(0, 3, "new");
        change.ReviewComment = "Retain the compatibility branch.";
        change.IsAccepted = false;
        difference.AddRange(change);
        var sourceBlock = new ChatPluginFileDifferenceDisplayBlock(
            difference,
            "old",
            TextDifferenceReviewKind.MoveAndUpdate,
            "source.txt");
        sourceBlock.CompleteReview();
        ChatPluginDisplayBlock source = sourceBlock;

        var bytes = MessagePackSerializer.Serialize(source);
        var restored = (ChatPluginFileDifferenceDisplayBlock)MessagePackSerializer.Deserialize<ChatPluginDisplayBlock>(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(restored.FilePath, Is.EqualTo("destination.txt"));
            Assert.That(restored.OriginalText, Is.Null);
            Assert.That(restored.Difference, Is.Null);
            Assert.That(restored.CanReview, Is.False);
            Assert.That(restored.ReviewKind, Is.EqualTo(TextDifferenceReviewKind.MoveAndUpdate));
            Assert.That(restored.SourcePath, Is.EqualTo("source.txt"));
            Assert.That(restored.AddedLineCount, Is.EqualTo(1));
            Assert.That(restored.RemovedLineCount, Is.EqualTo(1));
            Assert.That(restored.IsWaitingForUserInput, Is.False);
        });
    }

    [Test]
    public void TextChange_LegacyNullAcceptance_DeserializesAsAccepted()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(5);
        writer.Write("legacy-change");
        writer.Write((int)TextChangeKind.Replace);
        writer.WriteArrayHeader(2);
        writer.Write(0);
        writer.Write(3);
        writer.Write("new");
        writer.WriteNil();
        writer.Flush();

        var change = MessagePackSerializer.Deserialize<TextChange>(buffer.WrittenMemory);

        Assert.That(change.IsAccepted, Is.True);
    }

    [Test]
    public void BuildLineDiff_ReplaceLine_ApplyReturnsUpdatedContent()
    {
        using var difference = new TextDifference("file.txt");
        TextDifferenceBuilder.BuildLineDiff(difference, "old\n", "new\n");
        difference.AcceptAll();

        Assert.That(difference.Apply("old\n"), Is.EqualTo("new\n"));
    }

    [Test]
    public void BuildLineDiff_MultipleSeparatedReplacements_CreatesIndependentChangesAndRoundTrips()
    {
        var unchangedPrefix = Enumerable.Range(1, 56).Select(static index => $"line {index}");
        var original = string.Join(
            '\n',
            unchangedPrefix.Concat([
                "old declaration",
                "context 1",
                "old initialization",
                "context 2",
                "old step",
                "context 3",
                "old key 1",
                "old key 2",
                "old key 3",
                "old key 4"
            ])) + "\n";
        var updated = string.Join(
            '\n',
            unchangedPrefix.Concat([
                "new declaration",
                "context 1",
                "new initialization",
                "context 2",
                "new step",
                "context 3",
                "new key prelude",
                "new key 1",
                "new key 2",
                "new key 3",
                "new key 4"
            ])) + "\n";
        using var difference = new TextDifference("file.txt");

        TextDifferenceBuilder.BuildLineDiff(difference, original, updated);
        var changes = difference.GetFilteredChanges(default).ToArray();
        var projection = TextDifferenceProjectionBuilder.Build(
            original,
            changes,
            CancellationToken.None);
        var changedRows = projection.Rows.OfType<TextDifferenceChangedLine>().ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(changes, Has.Length.EqualTo(4));
            Assert.That(changes[0].Range.Start, Is.GreaterThan(0));
            Assert.That(changes.Sum(static change => TextDifference.CountLines(change.NewText)), Is.EqualTo(8));
            Assert.That(
                changes.Sum(change => TextDifference.CountLines(change.GetOriginalSlice(original))),
                Is.EqualTo(7));
            Assert.That(difference.Apply(original), Is.EqualTo(updated));
            Assert.That(
                projection.Rows.OfType<TextDifferenceOmittedLine>().First().LineCount,
                Is.EqualTo(53));
            Assert.That(
                changedRows.OfType<TextDifferenceRemovedLine>().First().OldLineNumber,
                Is.EqualTo(57));
            Assert.That(changedRows.OfType<TextDifferenceAddedLine>().Count(), Is.EqualTo(8));
            Assert.That(changedRows.OfType<TextDifferenceRemovedLine>().Count(), Is.EqualTo(7));
        });
    }

    [TestCase("", "new\n")]
    [TestCase("old\n", "")]
    [TestCase("one\r\ntwo\r\n", "one\r\nTWO\r\n")]
    [TestCase("same\nduplicate\nduplicate\ntail\n", "same\nduplicate\nchanged\ntail\n")]
    public void BuildLineDiff_CommonEdgeCases_RoundTrip(string original, string updated)
    {
        using var difference = new TextDifference("file.txt");

        TextDifferenceBuilder.BuildLineDiff(difference, original, updated);

        Assert.That(difference.Apply(original), Is.EqualTo(updated));
    }

    [Test]
    public void CountLines_IncludesEmptyPhysicalLines()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TextDifference.CountLines("\n"), Is.EqualTo(1));
            Assert.That(TextDifference.CountLines("a\n\n"), Is.EqualTo(2));
            Assert.That(TextDifference.CountLines("a\nb"), Is.EqualTo(2));
        });
    }

    [Test]
    public void ProjectionBuilder_LongUnchangedRanges_FoldsContextAndPreservesLineNumbers()
    {
        var original = string.Join('\n', Enumerable.Range(1, 30).Select(static index => $"line {index}")) + "\n";
        var updated = original.Replace("line 15\n", "changed 15\n", StringComparison.Ordinal);
        using var difference = new TextDifference("file.txt");
        TextDifferenceBuilder.BuildLineDiff(difference, original, updated);

        var projection = TextDifferenceProjectionBuilder.Build(
            original,
            difference.GetFilteredChanges(default).ToArray(),
            CancellationToken.None);
        var omittedRows = projection.Rows.OfType<TextDifferenceOmittedLine>().ToArray();
        var changedRows = projection.Rows.OfType<TextDifferenceChangedLine>().ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(omittedRows.Select(static row => row.LineCount), Is.EqualTo(new[] { 11, 12 }));
            Assert.That(changedRows, Has.Length.EqualTo(2));
            Assert.That(changedRows[0], Is.TypeOf<TextDifferenceRemovedLine>());
            Assert.That(changedRows[1], Is.TypeOf<TextDifferenceAddedLine>());
            Assert.That(((TextDifferenceRemovedLine)changedRows[0]).OldLineNumber, Is.EqualTo(15));
            Assert.That(((TextDifferenceAddedLine)changedRows[1]).NewLineNumber, Is.EqualTo(15));
        });
    }

    [Test]
    public void ProjectionBuilder_ExpandOmittedContext_ReturnsStoredSourceRange()
    {
        var original = string.Join('\n', Enumerable.Range(1, 20).Select(static index => $"line {index}")) + "\n";
        var updated = original.Replace("line 20\n", "changed 20\n", StringComparison.Ordinal);
        using var difference = new TextDifference("file.txt");
        TextDifferenceBuilder.BuildLineDiff(difference, original, updated);
        var projection = TextDifferenceProjectionBuilder.Build(
            original,
            difference.GetFilteredChanges(default).ToArray(),
            CancellationToken.None);
        var omitted = projection.Rows.OfType<TextDifferenceOmittedLine>().Single();

        var expanded = TextDifferenceProjectionBuilder.ExpandContext(projection, omitted, CancellationToken.None);
        var contextRows = expanded.OfType<TextDifferenceContextLine>().ToArray();
        var expandedProjectionRows = projection.Rows.ToList();
        var omittedIndex = expandedProjectionRows.IndexOf(omitted);
        expandedProjectionRows.RemoveAt(omittedIndex);
        expandedProjectionRows.InsertRange(omittedIndex, expanded);
        var expandedRegions = TextDifferenceProjectionBuilder.BuildRegions(expandedProjectionRows);

        Assert.Multiple(() =>
        {
            Assert.That(expanded, Has.Count.EqualTo(16));
            Assert.That(contextRows, Has.Length.EqualTo(16));
            Assert.That(contextRows[0].OldLineNumber, Is.EqualTo(1));
            Assert.That(contextRows[^1].OldLineNumber, Is.EqualTo(16));
            Assert.That(contextRows[0].Text, Is.EqualTo("line 1"));
            Assert.That(contextRows[^1].Text, Is.EqualTo("line 16"));
            Assert.That(
                expandedRegions.Single().VisualStartRow,
                Is.EqualTo(projection.Regions.Single().VisualStartRow + expanded.Count - 1));
        });
    }
}

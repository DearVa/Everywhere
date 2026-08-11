using Everywhere.Chat.Plugins.BuiltIn.FileSystem.Patching;

namespace Everywhere.Core.Tests.Chat;

public class FilePatchParserTests
{
    [Test]
    public void Parse_UpdateOperation_ParsesHunksAndLines()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: src/example.txt
            @@ section one
             before
            -old
            +new
            @@
             after
            -remove
            +keep
            *** End Patch
            """;

        var document = PatchParser.Parse(patch);
        var operation = document.Operations.Single();
        Assert.That(operation, Is.TypeOf<PatchFileOperation.Update>());
        var update = (PatchFileOperation.Update)operation;

        Assert.Multiple(() =>
        {
            Assert.That(update.Path, Is.EqualTo("src/example.txt"));
            Assert.That(update.Hunks, Has.Count.EqualTo(2));
            Assert.That(update.Hunks[0].Anchor, Is.EqualTo(new PatchHunkAnchor.Context("section one")));
            Assert.That(update.Hunks[0].Lines[1], Is.EqualTo(new PatchLine(PatchLineKind.Remove, "old")));
            Assert.That(update.Hunks[0].Lines[2], Is.EqualTo(new PatchLine(PatchLineKind.Add, "new")));
        });
    }

    [Test]
    public void Parse_ContextHunk_StoresLiteralContextAnchor()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: src/example.cs
            @@ void Execute()
            -old
            +new
            *** End Patch
            """;

        var operation = PatchParser.Parse(patch).Operations.Single();
        var hunk = ((PatchFileOperation.Update)operation).Hunks.Single();

        Assert.That(hunk.Anchor, Is.EqualTo(new PatchHunkAnchor.Context("void Execute()")));
    }

    [Test]
    public void Parse_MultipleHunksWithBlankSeparators_ParsesEveryHunk()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@
            -old 1
            +new 1

            @@
            -old 2
            +new 2

            @@
            -old 3
            +new 3

            @@
            -old 4
            +new 4

            @@
            -old 5
            +new 5

            @@
            -old 6
            +new 6
            *** End Patch
            """;

        var operation = (PatchFileOperation.Update)PatchParser.Parse(patch).Operations.Single();

        Assert.Multiple(() =>
        {
            Assert.That(operation.Hunks, Has.Count.EqualTo(6));
            Assert.That(operation.Hunks.Select(static hunk => hunk.HeaderLineNumber), Is.EqualTo([3, 7, 11, 15, 19, 23]));
        });
    }

    [Test]
    public void Parse_PrefixedEmptyLine_TreatsItAsHunkContent()
    {
        const string patch = "*** Begin Patch\n*** Update File: file.txt\n@@\n-old\n+new\n \n*** End Patch";

        var hunk = GetUpdateHunk(patch);

        Assert.That(hunk.Lines[^1], Is.EqualTo(new PatchLine(PatchLineKind.Context, string.Empty)));
    }

    [Test]
    public void Parse_EmptyHunk_ReportsFileHunkAndHeaderLine()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: src/example.txt
            @@ first anchor
             context only

            @@ second anchor
            -old
            +new
            *** End Patch
            """;

        var exception = Assert.Throws<PatchParseException>(() => PatchParser.Parse(patch));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.LineNumber, Is.EqualTo(3));
            Assert.That(exception.Message, Does.Contain("Update File 'src/example.txt'"));
            Assert.That(exception.Message, Does.Contain("hunk #1"));
            Assert.That(exception.Message, Does.Contain("header line 3"));
            Assert.That(exception.Message, Does.Contain("contains no '+' or '-'").And.Contain("line 6"));
        });
    }

    [Test]
    public void Parse_BlankLineInsideHunk_ExplainsRequiredPrefix()
    {
        const string patch = "*** Begin Patch\n*** Update File: file.txt\n@@\n-old\n\n+new\n*** End Patch";

        var exception = Assert.Throws<PatchParseException>(() => PatchParser.Parse(patch));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.LineNumber, Is.EqualTo(5));
            Assert.That(exception.Message, Does.Contain("hunk #1").And.Contain("header line 3"));
            Assert.That(exception.Message, Does.Contain("Prefix a semantic empty line"));
        });
    }

    [Test]
    public void Parse_AddDeleteAndMoveOperations_ParsesAllOperations()
    {
        const string patch = """
            *** Begin Patch
            *** Add File: added.txt
            +first
            +second
            *** Delete File: removed.txt
            *** Update File: old.txt
            *** Move to: new.txt
            @@
            -old
            +new
            *** End Patch
            """;

        var document = PatchParser.Parse(patch);

        Assert.Multiple(() =>
        {
            Assert.That(document.Operations, Has.Count.EqualTo(3));
            Assert.That(document.Operations[0], Is.TypeOf<PatchFileOperation.Add>());
            Assert.That(((PatchFileOperation.Add)document.Operations[0]).Hunks.Single().Lines, Has.Count.EqualTo(2));
            Assert.That(document.Operations[1], Is.TypeOf<PatchFileOperation.Delete>());
            Assert.That(document.Operations[2], Is.TypeOf<PatchFileOperation.Move>());
            Assert.That(((PatchFileOperation.Move)document.Operations[2]).DestinationPath, Is.EqualTo("new.txt"));
        });
    }

    [Test]
    public void Parse_CrlfInput_NormalizesEnvelopeAndLineContent()
    {
        const string patch = "*** Begin Patch\r\n*** Add File: file.txt\r\n+line\r\n*** End Patch\r\n";

        var operation = PatchParser.Parse(patch).Operations.Single();
        Assert.That(operation, Is.TypeOf<PatchFileOperation.Add>());
        var add = (PatchFileOperation.Add)operation;

        Assert.That(add.Hunks.Single().Lines.Single().Text, Is.EqualTo("line"));
    }

    [TestCase("text")]
    [TestCase("*** Begin Patch\n*** Update File: file.txt\n@@\n context\n*** End Patch")]
    [TestCase("*** Begin Patch\n*** Update File: file.txt\n@@ \n-old\n+new\n*** End Patch")]
    [TestCase("*** Begin Patch\n*** Update File: file.txt\n@@method\n-old\n+new\n*** End Patch")]
    [TestCase("*** Begin Patch\n*** Add File: file.txt\n-invalid\n*** End Patch")]
    [TestCase("*** Begin Patch\n*** Delete File: file.txt\n+content\n*** End Patch")]
    public void Parse_InvalidPatch_ThrowsWithLineNumber(string patch)
    {
        var exception = Assert.Throws<PatchParseException>(() => PatchParser.Parse(patch));

        Assert.That(exception!.LineNumber, Is.GreaterThan(0));
    }

    [Test]
    public void Parse_DuplicatePaths_RejectsAmbiguousPlan()
    {
        const string patch = """
            *** Begin Patch
            *** Delete File: duplicate.txt
            *** Add File: duplicate.txt
            +content
            *** End Patch
            """;

        var exception = Assert.Throws<PatchParseException>(() => PatchParser.Parse(patch));

        Assert.That(exception!.Message, Does.Contain("occurs more than once"));
    }

    [Test]
    public void Parse_UnifiedDiffRangeHeader_RejectsWithProtocolGuidance()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@ -1,1 +1,2 @@
            -old
            +new
            *** End Patch
            """;

        var exception = Assert.Throws<PatchParseException>(() => PatchParser.Parse(patch));

        Assert.That(
            exception!.Message,
            Does.Contain("unified-diff range headers").And.Contain("bare '@@'").And.Contain("literal source line"));
    }

    [Test]
    public void Parse_MoveWithoutHunk_AllowsPureRename()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: old.txt
            *** Move to: new.txt
            *** End Patch
            """;

        var operation = PatchParser.Parse(patch).Operations.Single();
        Assert.That(operation, Is.TypeOf<PatchFileOperation.Move>());
        var move = (PatchFileOperation.Move)operation;

        Assert.Multiple(() =>
        {
            Assert.That(move.Hunks, Is.Empty);
        });
    }

    [Test]
    public void LocateHunk_WithUniqueContext_ReturnsExactRange()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@
             before
            -old
            +new
             after
            *** End Patch
            """;
        var hunk = GetUpdateHunk(patch);

        var match = PatchHunkMatcher.Locate(["before", "old", "after"], hunk);

        Assert.Multiple(() =>
        {
            Assert.That(match.StartIndex, Is.EqualTo(0));
            Assert.That(match.EndIndex, Is.EqualTo(3));
            Assert.That(match.Kind, Is.EqualTo(PatchMatchKind.Exact));
        });
    }

    [Test]
    public void LocateHunk_BareHeader_SelectsNextMatchingSequence()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@
             repeated
            -old
            +new
            *** End Patch
            """;
        var hunk = GetUpdateHunk(patch);

        var match = PatchHunkMatcher.Locate(["repeated", "old", "repeated", "old"], hunk);

        Assert.Multiple(() =>
        {
            Assert.That(match.StartIndex, Is.EqualTo(0));
            Assert.That(match.EndIndex, Is.EqualTo(2));
        });
    }

    [Test]
    public void LocateHunk_WithTrailingWhitespaceDifference_ReportsFallback()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@
             before
            -old
            +new
            *** End Patch
            """;
        var hunk = GetUpdateHunk(patch);

        var match = PatchHunkMatcher.Locate(["before ", "old\t"], hunk);

        Assert.That(match.Kind, Is.EqualTo(PatchMatchKind.TrailingWhitespaceFallback));
    }

    [Test]
    public void LocateHunk_WithLeadingWhitespaceDifference_ReportsOuterWhitespaceFallback()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@
            -old
            +new
            *** End Patch
            """;
        var hunk = GetUpdateHunk(patch);

        var match = PatchHunkMatcher.Locate(["    old"], hunk);

        Assert.That(match.Kind, Is.EqualTo(PatchMatchKind.OuterWhitespaceFallback));
    }

    [Test]
    public void LocateHunk_WithUnicodePunctuationDifference_ReportsCompatibilityFallback()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@
            -import asyncio  # local import - avoids top-level dep
            +import asyncio  # replacement
            *** End Patch
            """;
        var hunk = GetUpdateHunk(patch);

        var match = PatchHunkMatcher.Locate(["import asyncio  # local import – avoids top‑level dep"], hunk);

        Assert.That(match.Kind, Is.EqualTo(PatchMatchKind.UnicodeCompatibilityFallback));
    }

    [Test]
    public void LocateHunk_WithUnicodeQuotesAndSpace_ReportsCompatibilityFallback()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@
            -message = "hello world"
            +message = "replacement"
            *** End Patch
            """;
        var hunk = GetUpdateHunk(patch);

        var match = PatchHunkMatcher.Locate(["message = “hello\u00A0world”"], hunk);

        Assert.That(match.Kind, Is.EqualTo(PatchMatchKind.UnicodeCompatibilityFallback));
    }

    [Test]
    public void LocateHunk_WithLaterExactAndEarlierFuzzyMatch_PrefersExactMatch()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@
            -old
            +new
            *** End Patch
            """;
        var hunk = GetUpdateHunk(patch);

        var match = PatchHunkMatcher.Locate(["    old", "old"], hunk);

        Assert.That(match.StartIndex, Is.EqualTo(1));
    }

    [Test]
    public void LocateHunk_WithRepeatedFuzzyMatch_SelectsFirstMatch()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@
            -old
            +new
            *** End Patch
            """;
        var hunk = GetUpdateHunk(patch);

        var match = PatchHunkMatcher.Locate(["    old", "  old"], hunk);

        Assert.That(match.StartIndex, Is.EqualTo(0));
    }

    [Test]
    public void LocateHunk_WithMissingContext_FailsClosed()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@
             before
            -old
            +new
            *** End Patch
            """;
        var hunk = GetUpdateHunk(patch);

        var exception = Assert.Throws<PatchMatchException>(() =>
            PatchHunkMatcher.Locate(["before", "different"], hunk));

        Assert.That(exception!.Message, Does.Contain("does not match"));
    }

    [Test]
    public void Parse_BareInsertionWithoutEndMarker_RejectsMissingLocation()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@
            +inserted
            *** End Patch
            """;
        var exception = Assert.Throws<PatchParseException>(() => PatchParser.Parse(patch));

        Assert.That(
            exception!.Message,
            Does.Contain("hunk #1").And.Contain("insertion-only").And.Contain("*** End of File"));
    }

    [Test]
    public void LocateHunk_BareInsertionWithEndMarker_AppendsAtEndOfFile()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@
            +inserted
            *** End of File
            *** End Patch
            """;
        var hunk = GetUpdateHunk(patch);

        var match = PatchHunkMatcher.Locate(["one", "two"], hunk);

        Assert.Multiple(() =>
        {
            Assert.That(match.StartIndex, Is.EqualTo(2));
            Assert.That(match.EndIndex, Is.EqualTo(2));
        });
    }

    [Test]
    public void LocateHunk_ContextInsertion_InsertsImmediatelyAfterAnchor()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@ method B
            +inserted
            *** End Patch
            """;
        var hunk = GetUpdateHunk(patch);

        var match = PatchHunkMatcher.Locate(["method A", "method B", "after"], hunk);

        Assert.Multiple(() =>
        {
            Assert.That(match.StartIndex, Is.EqualTo(2));
            Assert.That(match.EndIndex, Is.EqualTo(2));
            Assert.That(match.Kind, Is.EqualTo(PatchMatchKind.Context));
        });
    }

    [Test]
    public void LocateHunk_ContextAnchor_SearchesOnlyAfterAnchor()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@ method B
            -old
            +new
            *** End Patch
            """;
        var hunk = GetUpdateHunk(patch);

        var match = PatchHunkMatcher.Locate(["method A", "old", "method B", "old", "old"], hunk);

        Assert.Multiple(() =>
        {
            Assert.That(match.StartIndex, Is.EqualTo(3));
            Assert.That(match.EndIndex, Is.EqualTo(4));
            Assert.That(match.Kind, Is.EqualTo(PatchMatchKind.Context));
        });
    }

    [Test]
    public void LocateHunk_ContextAnchor_UsesNarrowWhitespaceFallback()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@ method B
            -old
            +new
            *** End Patch
            """;
        var hunk = GetUpdateHunk(patch);

        var match = PatchHunkMatcher.Locate(["method B ", "old\t"], hunk);

        Assert.That(match.Kind, Is.EqualTo(PatchMatchKind.ContextTrailingWhitespaceFallback));
    }

    [Test]
    public void LocateHunk_RepeatedContextAnchor_SelectsFirstMatch()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@ method
            -old
            +new
            *** End Patch
            """;
        var hunk = GetUpdateHunk(patch);

        var match = PatchHunkMatcher.Locate(["method", "old", "method", "old"], hunk);

        Assert.Multiple(() =>
        {
            Assert.That(match.StartIndex, Is.EqualTo(1));
            Assert.That(match.EndIndex, Is.EqualTo(2));
            Assert.That(match.Kind, Is.EqualTo(PatchMatchKind.Context));
        });
    }

    [Test]
    public void LocateHunk_EndOfFileMarkerRequiresEndOfFile()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: file.txt
            @@
             last
            -old
            +new
            *** End of File
            *** End Patch
            """;
        var hunk = GetUpdateHunk(patch);

        var exception = Assert.Throws<PatchMatchException>(() =>
            PatchHunkMatcher.Locate(["last", "old", "tail"], hunk));

        Assert.That(exception!.Message, Does.Contain("end of the file"));
    }

    private static PatchHunk GetUpdateHunk(string patch)
    {
        var operation = PatchParser.Parse(patch).Operations.Single();
        Assert.That(operation, Is.TypeOf<PatchFileOperation.Update>());
        return ((PatchFileOperation.Update)operation).Hunks.Single();
    }
}

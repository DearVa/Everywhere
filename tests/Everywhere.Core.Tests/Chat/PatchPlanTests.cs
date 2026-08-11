using System.Text;
using Everywhere.Chat.Plugins.BuiltIn.FileSystem.Patching;

namespace Everywhere.Core.Tests.Chat;

public class PatchPlanTests
{
    [Test]
    public async Task BuildAsync_Update_PreservesEncodingAndLineEndings()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await File.WriteAllBytesAsync(
            path,
            encoding.GetPreamble().Concat(encoding.GetBytes("before\r\nold\r\nafter\r\n")).ToArray());

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@
                 before
                -old
                +new
                 after
                *** End Patch
                """,
                root);
            var file = plan.Files.Single();
            Assert.That(file, Is.TypeOf<PatchUpdatePlanFile>());
            var update = (PatchUpdatePlanFile)file;

            Assert.Multiple(() =>
            {
                Assert.That(file.ProposedContent, Is.EqualTo("before\r\nnew\r\nafter\r\n"));
                Assert.That(file.MatchDiagnostics, Is.Empty);
                Assert.That(update.ProposedBytes[..encoding.GetPreamble().Length], Is.EqualTo(encoding.GetPreamble()));
                Assert.That(file.Original.Preamble, Is.EqualTo(encoding.GetPreamble()));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_RepeatedHunkContext_UpdatesFirstMatch()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "same\nold\nsame\nold\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@ same
                -old
                +new
                *** End Patch
                """,
                root);

            var file = plan.Files.Single();
            Assert.Multiple(() =>
            {
                Assert.That(file.ProposedContent, Is.EqualTo("same\nnew\nsame\nold\n"));
                Assert.That(file.MatchDiagnostics, Is.Empty);
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_UnicodeCompatibilityFallback_AppliesReplacement()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "import asyncio  # local import – avoids top‑level dep\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@
                -import asyncio  # local import - avoids top-level dep
                +import asyncio  # replacement
                *** End Patch
                """,
                root);

            var file = plan.Files.Single();
            Assert.Multiple(() =>
            {
                Assert.That(file.ProposedContent, Is.EqualTo("import asyncio  # replacement\n"));
                Assert.That(file.MatchDiagnostics, Has.Count.EqualTo(1));
                Assert.That(file.MatchDiagnostics[0].HunkNumber, Is.EqualTo(1));
                Assert.That(file.MatchDiagnostics[0].HeaderLineNumber, Is.EqualTo(3));
                Assert.That(file.MatchDiagnostics[0].Kind, Is.EqualTo(PatchMatchKind.UnicodeCompatibilityFallback));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_OuterWhitespaceFallback_WritesAdditionExactly()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "    old\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@
                -old
                +  new
                *** End Patch
                """,
                root);

            var file = plan.Files.Single();
            Assert.Multiple(() =>
            {
                Assert.That(file.ProposedContent, Is.EqualTo("  new\n"));
                Assert.That(file.MatchDiagnostics, Has.Count.EqualTo(1));
                Assert.That(file.MatchDiagnostics[0].Kind, Is.EqualTo(PatchMatchKind.OuterWhitespaceFallback));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_TrailingWhitespaceFallback_RecordsDiagnostic()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "old  \n");

        try
        {
            var plan = await BuildAsync(
                "*** Begin Patch\n*** Update File: file.txt\n@@\n-old\t\n+new\n*** End Patch",
                root);

            Assert.That(
                plan.Files.Single().MatchDiagnostics.Single().Kind,
                Is.EqualTo(PatchMatchKind.TrailingWhitespaceFallback));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_AnchoredWhitespaceFallback_RecordsContextDiagnostic()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "    section\nold\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@ section
                -old
                +new
                *** End Patch
                """,
                root);

            Assert.That(
                plan.Files.Single().MatchDiagnostics.Single().Kind,
                Is.EqualTo(PatchMatchKind.ContextOuterWhitespaceFallback));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_OverlappingHunks_FailsBeforeCreatingOutput()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "a\nb\nc\n");

        try
        {
            var exception = Assert.ThrowsAsync<PatchMatchException>(async () =>
                await BuildAsync(
                    """
                    *** Begin Patch
                    *** Update File: file.txt
                    @@
                     a
                    -b
                    +B
                    @@
                     b
                    -c
                    +C
                    *** End Patch
                    """,
                    root));

            Assert.That(exception!.Message, Does.Contain("overlap"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_OutOfOrderHunks_FailsClosed()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "first\nsecond\nthird\n");

        try
        {
            var exception = Assert.ThrowsAsync<PatchMatchException>(async () =>
                await BuildAsync(
                    """
                    *** Begin Patch
                    *** Update File: file.txt
                    @@
                    -third
                    +THIRD
                    @@
                    -second
                    +SECOND
                    *** End Patch
                    """,
                    root));

            Assert.That(exception!.Message, Does.Contain("out of order"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_BareHunks_LocateNextMatchInPatchOrder()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "section A\nold\nsection B\nold\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@
                -old
                +new A

                @@
                -old
                +new B
                *** End Patch
                """,
                root);

            Assert.That(plan.Files.Single().ProposedContent, Is.EqualTo("section A\nnew A\nsection B\nnew B\n"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_ContextInsertion_InsertsImmediatelyAfterAnchor()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "method A\nmethod B\nafter\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@ method B
                +inserted
                *** End Patch
                """,
                root);

            Assert.That(plan.Files.Single().ProposedContent, Is.EqualTo("method A\nmethod B\ninserted\nafter\n"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public void BuildAsync_MissingHunkTarget_ReportsFileHunkAndPatchLine()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        File.WriteAllText(path, "actual\n");

        try
        {
            var exception = Assert.ThrowsAsync<PatchMatchException>(async () =>
                await BuildAsync(
                    """
                    *** Begin Patch
                    *** Update File: file.txt
                    @@
                    -missing
                    +new
                    *** End Patch
                    """,
                    root));

            Assert.Multiple(() =>
            {
                Assert.That(exception!.Message, Does.Contain($"Patch target '{path}'"));
                Assert.That(exception.Message, Does.Contain("hunk #1").And.Contain("patch header line 3"));
                Assert.That(exception.Message, Does.Contain("does not match"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_AddDeleteAndMove_ProducesFileLevelPlans()
    {
        var root = CreateTemporaryDirectory();
        var oldPath = Path.Combine(root, "old.txt");
        var deletedPath = Path.Combine(root, "deleted.txt");
        await File.WriteAllTextAsync(oldPath, "old\n");
        await File.WriteAllTextAsync(deletedPath, "delete\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Add File: added.txt
                +added
                *** Delete File: deleted.txt
                *** Update File: old.txt
                *** Move to: moved.txt
                @@
                -old
                +new
                *** End Patch
                """,
                root);

            Assert.Multiple(() =>
            {
                Assert.That(plan.Files, Has.Count.EqualTo(3));
                Assert.That(plan.Files[0], Is.TypeOf<PatchAddPlanFile>());
                Assert.That(plan.Files[0].ProposedContent, Is.EqualTo("added"));
                Assert.That(plan.Files[1], Is.TypeOf<PatchDeletePlanFile>());
                Assert.That(plan.Files[2], Is.TypeOf<PatchMovePlanFile>());
                Assert.That(((PatchMovePlanFile)plan.Files[2]).DestinationPath, Is.EqualTo(Path.Combine(root, "moved.txt")));
                Assert.That(plan.Files[2].ProposedContent, Is.EqualTo("new\n"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task CreateDifference_AcceptAll_ProducesPlannedContent()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "old\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@
                -old
                +new
                *** End Patch
                """,
                root);
            var file = plan.Files.Single();
            using var difference = file.CreateDifference();
            difference.AcceptAll();

            Assert.That(difference.Apply(file.Original.Content), Is.EqualTo(file.ProposedContent));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public void EnsureOutputBudget_MultipleSeparatedChanges_CountsCompleteSynchronousDiff()
    {
        const string original = "old one\ncontext\nold two\n";
        const string proposed = "new one\ncontext\nnew two\n";
        var limits = PatchLimits.Default with { MaxChangedLines = 1 };

        var exception = Assert.Throws<PatchPlanException>(() => PatchPlanBuilder.EnsureOutputBudget(
            "file.txt",
            original,
            proposed,
            Encoding.UTF8.GetByteCount(proposed),
            limits));

        Assert.That(exception!.Message, Does.Contain("changes 2 lines"));
    }

    private static async Task<PatchPlan> BuildAsync(string patch, string root)
    {
        var document = PatchParser.Parse(patch);
        return await PatchPlanBuilder.BuildAsync(document, root, PatchLimits.Default, CancellationToken.None);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "everywhere-patch-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}

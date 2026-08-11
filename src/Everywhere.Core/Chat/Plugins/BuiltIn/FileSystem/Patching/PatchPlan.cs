using Everywhere.Common;

namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem.Patching;

/// <summary>
/// Bounds planning and output growth before any filesystem mutation is allowed.
/// </summary>
/// <param name="MaxFiles">Maximum number of file operations.</param>
/// <param name="MaxHunks">Maximum number of update hunks.</param>
/// <param name="MaxFileBytes">Maximum source file size loaded into memory.</param>
/// <param name="MaxOutputBytes">Maximum encoded output size for one file.</param>
/// <param name="MaxGrowthRatio">Maximum output-to-input character ratio for existing files.</param>
/// <param name="MaxChangedLines">Maximum estimated changed logical lines.</param>
internal readonly record struct PatchLimits(
    int MaxFiles,
    int MaxHunks,
    long MaxFileBytes,
    long MaxOutputBytes,
    double MaxGrowthRatio,
    int MaxChangedLines
)
{
    public static PatchLimits Default => new(
        MaxFiles: 32,
        MaxHunks: 256,
        MaxFileBytes: 50L * 1024 * 1024,
        MaxOutputBytes: 50L * 1024 * 1024,
        MaxGrowthRatio: 10,
        MaxChangedLines: 100_000);
}

/// <summary>
/// Contains immutable per-file patch plans produced from one parsed document.
/// </summary>
internal sealed class PatchPlan(IReadOnlyList<PatchPlanFile> files)
{
    public IReadOnlyList<PatchPlanFile> Files { get; } = files;
}

/// <summary>
/// Describes one hunk that required tolerant text matching while building a patch plan.
/// </summary>
internal sealed record PatchMatchDiagnostic(int HunkNumber, int HeaderLineNumber, PatchMatchKind Kind);

/// <summary>
/// Contains the original snapshot and proposed logical content for one planned operation.
/// </summary>
internal abstract class PatchPlanFile
{
    public required string SourcePath { get; init; }

    public required PatchTextFileSnapshot Original { get; init; }

    public required string ProposedContent { get; init; }

    public IReadOnlyList<PatchMatchDiagnostic> MatchDiagnostics { get; init; } = [];

    public abstract string ReviewPath { get; }

    public bool HasContentChange => !string.Equals(Original.Content, ProposedContent, StringComparison.Ordinal);

    /// <summary>
    /// Converts the planned change into the existing review model.
    /// </summary>
    public abstract TextDifference CreateDifference();
}

/// <summary>
/// Contains a planned new-file operation and its encoded content.
/// </summary>
internal sealed class PatchAddPlanFile : PatchPlanFile
{
    public required byte[] ProposedBytes { get; init; }

    public override string ReviewPath => SourcePath;

    public override TextDifference CreateDifference()
    {
        var difference = new TextDifference(ReviewPath);
        if (ProposedContent.Length > 0)
        {
            difference.AddRange(TextChange.Insert(0, ProposedContent));
        }

        return difference;
    }
}

/// <summary>
/// Contains a planned update operation and its encoded content.
/// </summary>
internal sealed class PatchUpdatePlanFile : PatchPlanFile
{
    public required byte[] ProposedBytes { get; init; }

    public override string ReviewPath => SourcePath;

    public override TextDifference CreateDifference()
    {
        var difference = new TextDifference(ReviewPath);
        TextDifferenceBuilder.BuildLineDiff(difference, Original.Content, ProposedContent);
        return difference;
    }
}

/// <summary>
/// Contains a planned delete operation.
/// </summary>
internal sealed class PatchDeletePlanFile : PatchPlanFile
{
    public override string ReviewPath => SourcePath;

    public override TextDifference CreateDifference()
    {
        var difference = new TextDifference(ReviewPath);
        if (Original.Content.Length > 0)
        {
            difference.AddRange(TextChange.Delete(0, Original.Content.Length));
        }

        return difference;
    }
}

/// <summary>
/// Contains a planned move operation and its encoded destination content.
/// </summary>
internal sealed class PatchMovePlanFile : PatchPlanFile
{
    public required string DestinationPath { get; init; }

    public required byte[] ProposedBytes { get; init; }

    public override string ReviewPath => DestinationPath;

    public override TextDifference CreateDifference()
    {
        var difference = new TextDifference(ReviewPath);
        if (HasContentChange)
        {
            TextDifferenceBuilder.BuildLineDiff(difference, Original.Content, ProposedContent);
        }

        return difference;
    }
}

/// <summary>
/// Resolves all paths, snapshots all sources, locates hunks, and builds a mutation-free plan.
/// </summary>
internal static class PatchPlanBuilder
{
    /// <summary>
    /// Builds a complete multi-file plan before any target is written.
    /// </summary>
    /// <param name="document">The parsed patch document.</param>
    /// <param name="workingDirectory">The base directory for relative patch paths.</param>
    /// <param name="limits">Safety limits for the planning operation.</param>
    /// <param name="cancellationToken">Cancels planning and file reads.</param>
    /// <returns>A complete immutable patch plan.</returns>
    public static async ValueTask<PatchPlan> BuildAsync(
        PatchDocument document,
        string workingDirectory,
        PatchLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new PatchPlanException("The patch working directory cannot be empty.");
        }

        if (document.Operations.Count == 0 || document.Operations.Count > limits.MaxFiles)
        {
            throw new PatchPlanException($"The patch contains {document.Operations.Count} files; the maximum is {limits.MaxFiles}.");
        }

        var totalHunks = document.Operations.AsValueEnumerable().Sum(operation => operation switch
        {
            PatchFileOperation.Add add => add.Hunks.Count,
            PatchFileOperation.Update update => update.Hunks.Count,
            PatchFileOperation.Delete => 0,
            PatchFileOperation.Move move => move.Hunks.Count,
            _ => throw new PatchPlanException($"The patch operation type '{operation.GetType().Name}' is not supported.")
        });
        if (totalHunks > limits.MaxHunks)
        {
            throw new PatchPlanException($"The patch contains {totalHunks} hunks; the maximum is {limits.MaxHunks}.");
        }

        var paths = new HashSet<string>(PathContainment.SystemPathComparer);
        var plannedFiles = new List<PatchPlanFile>(document.Operations.Count);

        foreach (var operation in document.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = ResolvePath(workingDirectory, operation.Path);
            EnsureNoReparseComponents(sourcePath);
            AddPath(sourcePath, paths);

            switch (operation)
            {
                case PatchFileOperation.Add add:
                    EnsureParentDirectory(sourcePath, sourcePath);
                    EnsureMissingFile(sourcePath);
                    plannedFiles.Add(PlanAdd(add, sourcePath, limits));
                    break;
                case PatchFileOperation.Update update:
                    EnsureParentDirectory(sourcePath, sourcePath);
                    plannedFiles.Add(await PlanUpdateAsync(update, sourcePath, limits, cancellationToken));
                    break;
                case PatchFileOperation.Delete:
                    EnsureParentDirectory(sourcePath, sourcePath);
                    plannedFiles.Add(await PlanDeleteAsync(sourcePath, limits, cancellationToken));
                    break;
                case PatchFileOperation.Move move:
                    var destinationPath = ResolvePath(workingDirectory, move.DestinationPath);
                    EnsureNoReparseComponents(destinationPath);
                    AddPath(destinationPath, paths);
                    EnsureParentDirectory(sourcePath, destinationPath);
                    EnsureMissingFile(destinationPath);
                    plannedFiles.Add(await PlanMoveAsync(move, sourcePath, destinationPath, limits, cancellationToken));
                    break;
                default:
                    throw new PatchPlanException($"The patch operation type '{operation.GetType().Name}' is not supported.");
            }
        }

        return new PatchPlan(plannedFiles);
    }

    private static PatchAddPlanFile PlanAdd(PatchFileOperation.Add operation, string path, PatchLimits limits)
    {
        var original = PatchTextFileSnapshot.CreateNew(path);
        var lines = operation.Hunks.Count == 0 ?
            Array.Empty<PatchSourceLine>() :
            operation.Hunks.AsValueEnumerable().Single().Lines.Select(line => new PatchSourceLine(line.Text, string.Empty)).ToArray();
        var proposedContent = PatchTextFileSnapshot.RenderLines(
            NormalizeLineEndings(lines, original, original.EndsWithLineEnding));
        var proposedBytes = original.Encode(proposedContent);
        EnsureOutputBudget(path, original.Content, proposedContent, proposedBytes.Length, limits);

        return new PatchAddPlanFile
        {
            SourcePath = path,
            Original = original,
            ProposedContent = proposedContent,
            ProposedBytes = proposedBytes
        };
    }

    private static async ValueTask<PatchUpdatePlanFile> PlanUpdateAsync(
        PatchFileOperation.Update operation,
        string sourcePath,
        PatchLimits limits,
        CancellationToken cancellationToken)
    {
        var original = await PatchTextFileSnapshot.ReadAsync(sourcePath, limits.MaxFileBytes, cancellationToken);
        var application = operation.Hunks.Count == 0 ? new PatchHunkApplication(original.Content, []) : ApplyHunks(original, operation.Hunks);
        var proposedBytes = original.Encode(application.Content);
        EnsureOutputBudget(sourcePath, original.Content, application.Content, proposedBytes.Length, limits);

        return new PatchUpdatePlanFile
        {
            SourcePath = sourcePath,
            Original = original,
            ProposedContent = application.Content,
            ProposedBytes = proposedBytes,
            MatchDiagnostics = application.Diagnostics
        };
    }

    private static async ValueTask<PatchMovePlanFile> PlanMoveAsync(
        PatchFileOperation.Move operation,
        string sourcePath,
        string destinationPath,
        PatchLimits limits,
        CancellationToken cancellationToken)
    {
        var original = await PatchTextFileSnapshot.ReadAsync(sourcePath, limits.MaxFileBytes, cancellationToken);
        var application = operation.Hunks.Count == 0 ? new PatchHunkApplication(original.Content, []) : ApplyHunks(original, operation.Hunks);
        var proposedBytes = original.Encode(application.Content);
        EnsureOutputBudget(sourcePath, original.Content, application.Content, proposedBytes.Length, limits);

        return new PatchMovePlanFile
        {
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            Original = original,
            ProposedContent = application.Content,
            ProposedBytes = proposedBytes,
            MatchDiagnostics = application.Diagnostics
        };
    }

    private static async ValueTask<PatchDeletePlanFile> PlanDeleteAsync(string path, PatchLimits limits, CancellationToken cancellationToken)
    {
        var original = await PatchTextFileSnapshot.ReadAsync(path, limits.MaxFileBytes, cancellationToken);
        return new PatchDeletePlanFile
        {
            SourcePath = path,
            Original = original,
            ProposedContent = string.Empty,
        };
    }

    private static PatchHunkApplication ApplyHunks(PatchTextFileSnapshot original, IReadOnlyList<PatchHunk> hunks)
    {
        var originalLines = original.Lines.AsValueEnumerable().Select(static line => line.Text).ToArray();
        var locatedMatches = new List<(PatchHunk Hunk, PatchHunkMatch Match)>(hunks.Count);
        var searchStartIndex = 0;
        PatchHunkMatch? previousMatch = null;
        for (var hunkIndex = 0; hunkIndex < hunks.Count; hunkIndex++)
        {
            var hunk = hunks[hunkIndex];
            var match = LocateHunkInOrder(
                original.Path,
                originalLines,
                hunk,
                hunkIndex + 1,
                searchStartIndex,
                previousMatch);
            locatedMatches.Add((hunk, match));
            searchStartIndex = match.EndIndex;
            previousMatch = match;
        }

        EnsurePatchOrder(locatedMatches);
        var matches = locatedMatches.AsValueEnumerable().OrderBy(item => item.Match.StartIndex).ToArray();
        EnsureNonOverlapping(matches);

        var lines = original.Lines.AsValueEnumerable().ToList();
        foreach (var item in matches.AsValueEnumerable().Reverse())
        {
            var replacement = BuildReplacement(original, item.Hunk, item.Match.StartIndex, item.Match.EndIndex);
            lines.RemoveRange(item.Match.StartIndex, item.Match.EndIndex - item.Match.StartIndex);
            lines.InsertRange(item.Match.StartIndex, replacement);
        }

        var diagnostics = locatedMatches
            .AsValueEnumerable()
            .Select((item, index) => new PatchMatchDiagnostic(index + 1, item.Hunk.HeaderLineNumber, item.Match.Kind))
            .Where(static diagnostic => diagnostic.Kind is not PatchMatchKind.Exact and not PatchMatchKind.Context)
            .ToArray();
        var content = PatchTextFileSnapshot.RenderLines(
            NormalizeLineEndings(lines, original, original.EndsWithLineEnding));
        return new PatchHunkApplication(content, diagnostics);
    }

    private static PatchHunkMatch LocateHunkInOrder(
        string path,
        IReadOnlyList<string> originalLines,
        PatchHunk hunk,
        int hunkNumber,
        int searchStartIndex,
        PatchHunkMatch? previousMatch)
    {
        try
        {
            return PatchHunkMatcher.Locate(originalLines, hunk, searchStartIndex);
        }
        catch (PatchMatchException exception)
        {
            var earlierMatch = searchStartIndex > 0 ? TryLocateFromStart(originalLines, hunk) : null;
            if (earlierMatch is { } earlier && earlier.StartIndex < searchStartIndex)
            {
                var message = previousMatch is { } previous && earlier.EndIndex > previous.StartIndex ?
                    "overlaps a previous hunk." :
                    "is out of order; its target occurs before the previous hunk ended.";
                throw CreateHunkMatchException(path, hunk, hunkNumber, message, exception);
            }

            throw CreateHunkMatchException(path, hunk, hunkNumber, exception.Message, exception);
        }
    }

    private static PatchHunkMatch? TryLocateFromStart(IReadOnlyList<string> originalLines, PatchHunk hunk)
    {
        try
        {
            return PatchHunkMatcher.Locate(originalLines, hunk);
        }
        catch (PatchMatchException)
        {
            return null;
        }
    }

    private static PatchMatchException CreateHunkMatchException(
        string path,
        PatchHunk hunk,
        int hunkNumber,
        string message,
        Exception innerException) =>
        new($"Patch target '{path}', hunk #{hunkNumber} (patch header line {hunk.HeaderLineNumber}): {message}", innerException);

    private static void EnsurePatchOrder(List<(PatchHunk Hunk, PatchHunkMatch Match)> matches)
    {
        for (var index = 1; index < matches.Count; index++)
        {
            if (matches[index - 1].Match.StartIndex > matches[index].Match.StartIndex)
            {
                throw new PatchMatchException("Patch hunks are out of order; their locations must follow patch order.");
            }
        }
    }

    private static List<PatchSourceLine> BuildReplacement(PatchTextFileSnapshot original, PatchHunk hunk, int startIndex, int endIndex)
    {
        var sourceLines = original.Lines.AsValueEnumerable().Skip(startIndex).Take(endIndex - startIndex).ToArray();
        var sourceIndex = 0;
        var replacement = new List<PatchSourceLine>(hunk.Lines.Count);
        foreach (var line in hunk.Lines.AsValueEnumerable())
        {
            if (line.Kind is PatchLineKind.Add)
            {
                replacement.Add(new PatchSourceLine(line.Text, string.Empty));
                continue;
            }

            if (sourceIndex >= sourceLines.Length)
            {
                throw new PatchMatchException("The hunk match changed while constructing the replacement.");
            }

            if (line.Kind is PatchLineKind.Context) replacement.Add(sourceLines[sourceIndex]);
            sourceIndex++;
        }

        if (sourceIndex != sourceLines.Length)
        {
            throw new PatchMatchException("The hunk match did not consume the complete matched range.");
        }

        return replacement;
    }

    private sealed record PatchHunkApplication(string Content, IReadOnlyList<PatchMatchDiagnostic> Diagnostics);

    private static PatchSourceLine[] NormalizeLineEndings(
        IReadOnlyList<PatchSourceLine> lines,
        PatchTextFileSnapshot original,
        bool originalEndsWithLineEnding)
    {
        if (lines.Count == 0) return [];

        var normalized = lines.ToArray();
        for (var index = 0; index < normalized.Length - 1; index++)
        {
            if (normalized[index].LineEnding.Length == 0)
            {
                normalized[index] = normalized[index] with { LineEnding = original.DefaultLineEnding };
            }
        }

        var last = normalized[^1];
        if (last.LineEnding.Length == 0 && originalEndsWithLineEnding)
        {
            normalized[^1] = last with { LineEnding = original.DefaultLineEnding };
        }

        return normalized;
    }

    private static void EnsureNonOverlapping((PatchHunk Hunk, PatchHunkMatch Match)[] matches)
    {
        for (var index = 1; index < matches.Length; index++)
        {
            var previous = matches[index - 1].Match;
            var current = matches[index].Match;
            var overlaps = previous.EndIndex > current.StartIndex ||
                previous.StartIndex == previous.EndIndex && current.StartIndex == current.EndIndex &&
                previous.StartIndex == current.StartIndex;
            if (overlaps)
            {
                throw new PatchMatchException("Patch hunks overlap or target the same insertion position.");
            }
        }
    }

    private static string ResolvePath(string workingDirectory, string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile)
            {
                throw new PatchPlanException($"The patch path '{path}' uses an unsupported URI scheme.");
            }

            path = uri.LocalPath;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path), workingDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PatchPlanException($"The patch path '{path}' is invalid: {ex.Message}");
        }
    }

    private static void AddPath(string path, HashSet<string> paths)
    {
        if (!paths.Add(path))
        {
            throw new PatchPlanException($"The patch resolves more than one operation to '{path}'.");
        }
    }

    private static void EnsureParentDirectory(string sourcePath, string targetPath)
    {
        var parent = Path.GetDirectoryName(targetPath) ?? Path.GetDirectoryName(sourcePath);
        if (parent is null || !Directory.Exists(parent))
        {
            throw new PatchPlanException($"The parent directory for '{targetPath}' does not exist.");
        }
    }

    private static void EnsureMissingFile(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new PatchPlanException($"The patch destination '{path}' already exists.");
        }

        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes != (FileAttributes)(-1))
            {
                throw new PatchPlanException($"The patch destination '{path}' already exists or is a reparse point.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void EnsureNoReparseComponents(string path)
    {
        if (!PathContainment.TryResolvePath(path, out var resolvedPath) ||
            !string.Equals(path, resolvedPath, PathContainment.SystemPathComparison))
        {
            throw new PatchPlanException($"The patch path '{path}' contains a symbolic link, junction, or other reparse point.");
        }
    }

    /// <summary>
    /// Validates the encoded size, growth ratio, and estimated line changes of proposed content.
    /// </summary>
    internal static void EnsureOutputBudget(
        string path,
        string originalContent,
        string proposedContent,
        int proposedBytes,
        PatchLimits limits)
    {
        if (proposedBytes > limits.MaxOutputBytes)
        {
            throw new PatchPlanException($"The patched file '{path}' exceeds the output size limit of {limits.MaxOutputBytes} bytes.");
        }

        var growthRatio = originalContent.Length == 0 ? 1 : (double)proposedContent.Length / originalContent.Length;
        if (growthRatio > limits.MaxGrowthRatio)
        {
            throw new PatchPlanException($"The patched file '{path}' exceeds the maximum growth ratio of {limits.MaxGrowthRatio}.");
        }

        var changedLines = CountChangedLines(originalContent, proposedContent);
        if (changedLines > limits.MaxChangedLines)
        {
            throw new PatchPlanException($"The patch changes {changedLines} lines in '{path}'; the maximum is {limits.MaxChangedLines}.");
        }
    }

    private static int CountChangedLines(string original, string proposed)
    {
        var changes = TextDifferenceBuilder.BuildLineChanges(original, proposed);
        return changes.Sum(change =>
            change.Kind is TextChangeKind.Replace ?
                Math.Max(
                    TextDifference.CountLines(change.GetOriginalSlice(original)),
                    TextDifference.CountLines(change.NewText ?? string.Empty)) :
                TextDifference.CountLines(
                    change.Kind is TextChangeKind.Delete ? change.GetOriginalSlice(original) : change.NewText ?? string.Empty));
    }
}

/// <summary>
/// Reports a patch that cannot be safely planned from the requested paths or contents.
/// </summary>
internal sealed class PatchPlanException(string message) : InvalidOperationException(message);
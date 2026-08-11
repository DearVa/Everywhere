using Everywhere.Common;

namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem.Patching;

/// <summary>
/// Describes one reviewed text change, its line impact, and its optional user comment.
/// </summary>
internal sealed record PatchChangeDecision(
    string Id,
    bool Accepted,
    int AddedLineCount,
    int RemovedLineCount,
    string? Comment
);

/// <summary>
/// Carries one explicit review outcome for a planned file.
/// </summary>
internal abstract record PatchFileDecision(string SourcePath, IReadOnlyList<PatchChangeDecision> Changes);

/// <summary>
/// Accepts a content-bearing add, update, or move operation.
/// </summary>
internal sealed record PatchContentFileDecision(
    string SourcePath,
    string Content,
    IReadOnlyList<PatchChangeDecision> Changes
) : PatchFileDecision(SourcePath, Changes);

/// <summary>
/// Accepts a delete operation.
/// </summary>
internal sealed record PatchDeleteFileDecision(
    string SourcePath,
    IReadOnlyList<PatchChangeDecision> Changes
) : PatchFileDecision(SourcePath, Changes);

/// <summary>
/// Records an operation rejected by the user, including an optional reason.
/// </summary>
internal sealed record PatchRejectedFileDecision(
    string SourcePath,
    string? Reason,
    IReadOnlyList<PatchChangeDecision> Changes
) : PatchFileDecision(SourcePath, Changes);

/// <summary>
/// Records a planned update whose proposed content already matches the source.
/// </summary>
internal sealed record PatchNoChangesFileDecision(string SourcePath) : PatchFileDecision(SourcePath, []);

/// <summary>
/// Describes the outcome for one planned file operation.
/// </summary>
internal enum PatchCommitStatus
{
    Committed,
    NoChanges,
    RejectedByUser,
    Conflict,
    Failed,
    NotAttempted,
}

/// <summary>
/// Reports the outcome for one planned path.
/// </summary>
internal sealed record PatchCommitFileResult(
    string Path,
    PatchCommitStatus Status,
    PatchFileDecision Decision,
    string? Error = null
);

/// <summary>
/// Reports the result of applying reviewed patch operations.
/// </summary>
internal sealed record PatchCommitResult(
    bool Succeeded,
    IReadOnlyList<PatchCommitFileResult> Files,
    string? Error = null
);

/// <summary>
/// Applies reviewed patch results after rechecking their raw source snapshots.
/// </summary>
/// <remarks>
/// Planning and conflict verification happen before the first mutation. The commit itself is
/// deliberately sequential and does not create disk backups or attempt rollback. If an operation
/// fails after an earlier operation committed, the result reports the committed, failed, and
/// not-attempted operations so the caller can explain the partial outcome accurately.
/// </remarks>
internal static class PatchCommitter
{
    /// <summary>
    /// Applies accepted decisions after rechecking every source snapshot.
    /// </summary>
    /// <param name="plan">The mutation-free plan to commit.</param>
    /// <param name="decisions">One decision for every planned source path.</param>
    /// <param name="limits">Safety limits used for final conflict reads.</param>
    /// <param name="cancellationToken">Cancels before or between file operations.</param>
    /// <returns>A detailed commit, skip, conflict, or partial-failure report.</returns>
    public static async ValueTask<PatchCommitResult> CommitAsync(
        PatchPlan plan,
        IReadOnlyList<PatchFileDecision> decisions,
        PatchLimits limits,
        CancellationToken cancellationToken)
    {
        var decisionMap = new Dictionary<string, PatchFileDecision>(PathContainment.SystemPathComparer);
        foreach (var decision in decisions)
        {
            if (!decisionMap.TryAdd(decision.SourcePath, decision))
            {
                throw new PatchCommitException($"The patch contains more than one review decision for '{decision.SourcePath}'.");
            }
        }

        if (decisionMap.Count != plan.Files.Count)
        {
            throw new PatchCommitException("The patch review returned a decision for an unknown file.");
        }

        var commitItems = new List<CommitItem>();
        var resultItems = new List<PatchCommitFileResult>(plan.Files.Count);
        foreach (var file in plan.Files)
        {
            if (!decisionMap.TryGetValue(file.SourcePath, out var decision))
            {
                throw new PatchCommitException($"The patch review did not return a decision for '{file.SourcePath}'.");
            }

            if (decision is PatchRejectedFileDecision)
            {
                resultItems.Add(new PatchCommitFileResult(file.ReviewPath, PatchCommitStatus.RejectedByUser, decision));
                continue;
            }

            if (decision is PatchNoChangesFileDecision)
            {
                resultItems.Add(new PatchCommitFileResult(file.ReviewPath, PatchCommitStatus.NoChanges, decision));
                continue;
            }

            switch (file)
            {
                case PatchAddPlanFile add when decision is PatchContentFileDecision contentDecision:
                    commitItems.Add(
                        new AddCommitItem(
                            add,
                            contentDecision,
                            EncodeAcceptedContent(add, contentDecision.Content, limits)));
                    break;
                case PatchUpdatePlanFile update when decision is PatchContentFileDecision contentDecision:
                    if (string.Equals(contentDecision.Content, update.Original.Content, StringComparison.Ordinal))
                    {
                        resultItems.Add(new PatchCommitFileResult(file.ReviewPath, PatchCommitStatus.NoChanges, decision));
                        break;
                    }

                    commitItems.Add(
                        new UpdateCommitItem(
                            update,
                            contentDecision,
                            EncodeAcceptedContent(update, contentDecision.Content, limits)));
                    break;
                case PatchDeletePlanFile delete when decision is PatchDeleteFileDecision deleteDecision:
                    commitItems.Add(new DeleteCommitItem(delete, deleteDecision));
                    break;
                case PatchMovePlanFile move when decision is PatchContentFileDecision contentDecision:
                    commitItems.Add(
                        new MoveCommitItem(
                            move,
                            contentDecision,
                            EncodeAcceptedContent(move, contentDecision.Content, limits),
                            !string.Equals(contentDecision.Content, move.Original.Content, StringComparison.Ordinal)));
                    break;
                case PatchAddPlanFile or PatchUpdatePlanFile or PatchMovePlanFile:
                    throw new PatchCommitException(
                        $"The accepted review for '{file.ReviewPath}' did not provide file content.");
                case PatchDeletePlanFile:
                    throw new PatchCommitException(
                        $"The accepted review for '{file.ReviewPath}' did not provide a delete decision.");
                default:
                    throw new PatchCommitException($"The patch plan type '{file.GetType().Name}' is not supported.");
            }
        }

        if (commitItems.Count == 0)
        {
            return new PatchCommitResult(true, OrderResults(plan, resultItems));
        }

        var conflict = await VerifyCurrentStateAsync(commitItems, limits, cancellationToken);
        if (conflict is not null)
        {
            resultItems.AddRange(
                commitItems.Select(item => new PatchCommitFileResult(
                    item.Plan.ReviewPath,
                    PatchCommitStatus.Conflict,
                    item.Decision,
                    conflict)));
            return new PatchCommitResult(false, OrderResults(plan, resultItems), conflict);
        }

        for (var index = 0; index < commitItems.Count; index++)
        {
            var item = commitItems[index];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ApplyCommitItemAsync(item, cancellationToken);
                resultItems.Add(new PatchCommitFileResult(item.Plan.ReviewPath, PatchCommitStatus.Committed, item.Decision));
            }
            catch (Exception ex)
            {
                var error = $"Failed to apply '{item.Plan.ReviewPath}': {ex.Message}";
                resultItems.Add(new PatchCommitFileResult(item.Plan.ReviewPath, PatchCommitStatus.Failed, item.Decision, error));
                for (var remaining = index + 1; remaining < commitItems.Count; remaining++)
                {
                    resultItems.Add(
                        new PatchCommitFileResult(
                            commitItems[remaining].Plan.ReviewPath,
                            PatchCommitStatus.NotAttempted,
                            commitItems[remaining].Decision,
                            "Not attempted because a previous file operation failed."));
                }

                return new PatchCommitResult(false, OrderResults(plan, resultItems), error);
            }
        }

        return new PatchCommitResult(true, OrderResults(plan, resultItems));
    }

    private static PatchCommitFileResult[] OrderResults(PatchPlan plan, IEnumerable<PatchCommitFileResult> results)
    {
        var order = plan.Files
            .Select((file, index) => (file.ReviewPath, Index: index))
            .ToDictionary(static item => item.ReviewPath, static item => item.Index, PathContainment.SystemPathComparer);
        return results
            .AsValueEnumerable()
            .OrderBy(result => order.TryGetValue(result.Path, out var index) ? index : int.MaxValue)
            .ToArray();
    }

    private static byte[] EncodeAcceptedContent(PatchPlanFile file, string content, PatchLimits limits)
    {
        var contentBytes = file.Original.Encode(content);
        PatchPlanBuilder.EnsureOutputBudget(
            file.ReviewPath,
            file.Original.Content,
            content,
            contentBytes.Length,
            limits);
        return contentBytes;
    }

    private static async ValueTask<string?> VerifyCurrentStateAsync(
        IReadOnlyList<CommitItem> items,
        PatchLimits limits,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is AddCommitItem add)
            {
                if (!IsStablePath(add.Add.SourcePath))
                {
                    return $"The patch destination '{add.Add.SourcePath}' became a symbolic link or reparse point while awaiting approval.";
                }

                if (File.Exists(add.Add.SourcePath) || Directory.Exists(add.Add.SourcePath))
                {
                    return $"The patch destination '{add.Add.SourcePath}' was created or changed while awaiting approval.";
                }

                continue;
            }

            var sourcePath = item.Plan.SourcePath;
            if (!IsStablePath(sourcePath))
            {
                return $"The patch source '{sourcePath}' became a symbolic link or reparse point while awaiting approval.";
            }

            if (!File.Exists(sourcePath))
            {
                return $"The patch source '{sourcePath}' was removed or changed while awaiting approval.";
            }

            try
            {
                if (File.GetAttributes(sourcePath).HasFlag(FileAttributes.ReparsePoint))
                {
                    return $"The patch source '{sourcePath}' became a symbolic link or reparse point while awaiting approval.";
                }

                var currentBytes = await ReadRawBytesAsync(sourcePath, limits.MaxFileBytes, cancellationToken);
                if (!item.Plan.Original.HasSameFingerprint(currentBytes))
                {
                    return $"The patch source '{sourcePath}' changed while awaiting approval. No changes were written.";
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return $"The patch source '{sourcePath}' was removed or changed while awaiting approval.";
            }
            catch (IOException ex)
            {
                return $"The patch source '{sourcePath}' could not be verified: {ex.Message}";
            }

            if (item is MoveCommitItem move)
            {
                var destination = move.Move.DestinationPath;
                if (!IsStablePath(destination))
                {
                    return $"The patch destination '{destination}' became a symbolic link or reparse point while awaiting approval.";
                }

                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    return $"The patch destination '{destination}' was created while awaiting approval. No changes were written.";
                }
            }
        }

        return null;
    }

    private static bool IsStablePath(string path) =>
        PathContainment.TryResolvePath(path, out var resolvedPath) &&
        string.Equals(path, resolvedPath, PathContainment.SystemPathComparison);

    private static async ValueTask ApplyCommitItemAsync(CommitItem item, CancellationToken cancellationToken)
    {
        switch (item)
        {
            case UpdateCommitItem update:
                await OverwriteExistingFileAsync(update.Update.SourcePath, update.ContentBytes, cancellationToken);
                File.SetAttributes(update.Update.SourcePath, update.Update.Original.Attributes);
                break;
            case AddCommitItem add:
                await CreateNewFileAsync(add.Add.SourcePath, add.ContentBytes, cancellationToken);
                break;
            case DeleteCommitItem delete:
                File.Delete(delete.Delete.SourcePath);
                break;
            case MoveCommitItem move:
                if (!move.ContentChanged)
                {
                    File.Move(move.Move.SourcePath, move.Move.DestinationPath);
                    break;
                }

                await OverwriteExistingFileAsync(move.Move.SourcePath, move.ContentBytes, cancellationToken);
                File.SetAttributes(move.Move.SourcePath, move.Move.Original.Attributes);
                File.Move(move.Move.SourcePath, move.Move.DestinationPath);
                break;
            default:
                throw new PatchCommitException($"The patch commit type '{item.GetType().Name}' is not supported.");
        }
    }

    private static async ValueTask OverwriteExistingFileAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous);
        stream.SetLength(0);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask CreateNewFileAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask<byte[]> ReadRawBytesAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        if (stream.Length > maxBytes || stream.Length > int.MaxValue)
        {
            throw new PatchCommitException($"The file '{path}' exceeds the patch size limit.");
        }

        var bytes = new byte[(int)stream.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
            if (read == 0) break;
            offset += read;
        }

        if (offset != bytes.Length) Array.Resize(ref bytes, offset);
        return bytes;
    }

    private abstract class CommitItem(PatchPlanFile plan, PatchFileDecision decision)
    {
        public PatchPlanFile Plan { get; } = plan;

        public PatchFileDecision Decision { get; } = decision;
    }

    private sealed class AddCommitItem(
        PatchAddPlanFile add,
        PatchContentFileDecision decision,
        byte[] contentBytes
    ) : CommitItem(add, decision)
    {
        public PatchAddPlanFile Add { get; } = add;

        public byte[] ContentBytes { get; } = contentBytes;
    }

    private sealed class UpdateCommitItem(
        PatchUpdatePlanFile update,
        PatchContentFileDecision decision,
        byte[] contentBytes
    ) : CommitItem(update, decision)
    {
        public PatchUpdatePlanFile Update { get; } = update;

        public byte[] ContentBytes { get; } = contentBytes;
    }

    private sealed class DeleteCommitItem(
        PatchDeletePlanFile delete,
        PatchDeleteFileDecision decision
    ) : CommitItem(delete, decision)
    {
        public PatchDeletePlanFile Delete { get; } = delete;
    }

    private sealed class MoveCommitItem(
        PatchMovePlanFile move,
        PatchContentFileDecision decision,
        byte[] contentBytes,
        bool contentChanged
    ) : CommitItem(move, decision)
    {
        public PatchMovePlanFile Move { get; } = move;

        public byte[] ContentBytes { get; } = contentBytes;

        public bool ContentChanged { get; } = contentChanged;
    }
}

/// <summary>
/// Reports an invalid or incomplete commit decision set.
/// </summary>
internal sealed class PatchCommitException(string message) : InvalidOperationException(message);
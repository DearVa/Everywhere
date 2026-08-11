namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem.Patching;

/// <summary>
/// Represents a fully parsed multi-file patch document.
/// </summary>
/// <param name="Operations">The file operations in their source order.</param>
internal sealed record PatchDocument(IReadOnlyList<PatchFileOperation> Operations);

/// <summary>
/// Describes one file operation in the parsed patch.
/// </summary>
/// <param name="Path">The source or target path from the patch.</param>
internal abstract record PatchFileOperation(string Path)
{
    /// <summary>
    /// Describes an update to an existing file.
    /// </summary>
    /// <param name="Path">The source path from the patch.</param>
    /// <param name="Hunks">The content hunks to apply.</param>
    internal sealed record Update(string Path, IReadOnlyList<PatchHunk> Hunks) : PatchFileOperation(Path);

    /// <summary>
    /// Describes creation of a new file.
    /// </summary>
    /// <param name="Path">The target path from the patch.</param>
    /// <param name="Hunks">The added-file content represented as one optional hunk.</param>
    internal sealed record Add(string Path, IReadOnlyList<PatchHunk> Hunks) : PatchFileOperation(Path);

    /// <summary>
    /// Describes deletion of an existing file.
    /// </summary>
    /// <param name="Path">The source path from the patch.</param>
    internal sealed record Delete(string Path) : PatchFileOperation(Path);

    /// <summary>
    /// Describes moving an existing file, optionally while changing its content.
    /// </summary>
    /// <param name="Path">The source path from the patch.</param>
    /// <param name="DestinationPath">The destination path from the patch.</param>
    /// <param name="Hunks">The content hunks to apply before moving the file.</param>
    internal sealed record Move(
        string Path,
        string DestinationPath,
        IReadOnlyList<PatchHunk> Hunks
    ) : PatchFileOperation(Path);
}

/// <summary>
/// Represents one update hunk and its explicit source-location anchor.
/// </summary>
/// <param name="Anchor">The location selector encoded by the hunk header.</param>
/// <param name="Lines">The context, addition, and removal lines.</param>
/// <param name="EndOfFile">Whether the hunk explicitly anchors at end of file.</param>
/// <param name="HeaderLineNumber">The one-based patch line containing the hunk header.</param>
internal sealed record PatchHunk(
    PatchHunkAnchor Anchor,
    IReadOnlyList<PatchLine> Lines,
    bool EndOfFile,
    int HeaderLineNumber
);

/// <summary>
/// Selects the source location for a patch hunk.
/// </summary>
internal abstract record PatchHunkAnchor
{
    private PatchHunkAnchor()
    {
    }

    /// <summary>
    /// Represents a bare <c>@@</c> header with no explicit anchor.
    /// </summary>
    internal sealed record Unanchored : PatchHunkAnchor
    {
        public static Unanchored Instance { get; } = new();
    }

    /// <summary>
    /// Represents a literal source line from an <c>@@ &lt;context&gt;</c> header.
    /// </summary>
    /// <param name="Text">The literal anchor text without the header marker.</param>
    internal sealed record Context(string Text) : PatchHunkAnchor;
}

/// <summary>
/// Identifies how a line in a hunk contributes to the target content.
/// </summary>
internal enum PatchLineKind
{
    Context,
    Add,
    Remove,
}

/// <summary>
/// Represents one context, addition, or removal line from a patch hunk.
/// </summary>
/// <param name="Kind">The line operation.</param>
/// <param name="Text">The line text without its patch prefix.</param>
internal sealed record PatchLine(PatchLineKind Kind, string Text);
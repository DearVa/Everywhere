namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem;

/// <summary>
/// Contains structured content matches produced by a file handler.
/// </summary>
public sealed record FileContentSearchResult(
    IReadOnlyList<FileContentMatch> Matches,
    bool LimitHit = false
);

/// <summary>
/// Describes one regex occurrence and the logical line used to preview it.
/// </summary>
public sealed record FileContentMatch(
    string Path,
    string Preview,
    int Line,
    int Column,
    int Length,
    int? PageNumber = null
);
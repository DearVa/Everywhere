using System.Text.RegularExpressions;
using Everywhere.Chat;

namespace Everywhere.StrategyEngine.Conditions;

/// <summary>
/// Condition that matches <see cref="FileAttachment"/>.
/// </summary>
public sealed class FileCondition : AttachmentConditionBase<FileAttachment>
{
    public override AttachmentType TargetType => AttachmentType.File;

    /// <summary>
    /// File extensions to match (e.g., ".pdf", ".docx").
    /// Case-insensitive.
    /// </summary>
    public IReadOnlyList<string>? Extensions { get; init; }

    /// <summary>
    /// Regex pattern to match against the full file path.
    /// </summary>
    public Regex? PathPattern { get; init; }

    /// <summary>
    /// Strings that must appear in the file path (any match).
    /// </summary>
    public IReadOnlyList<string>? PathContains { get; init; }

    /// <summary>
    /// Minimum file size in bytes.
    /// </summary>
    public long? MinSize { get; init; }

    /// <summary>
    /// Maximum file size in bytes.
    /// </summary>
    public long? MaxSize { get; init; }

    protected override bool MatchesAttachment(FileAttachment attachment)
    {
        var filePath = attachment.FilePath;

        // Check extension
        if (Extensions is { Count: > 0 })
        {
            var ext = Path.GetExtension(filePath);
            if (!Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        // Check path pattern
        if (PathPattern is not null && !PathPattern.IsMatch(filePath))
        {
            return false;
        }

        // Check path contains
        if (PathContains is { Count: > 0 })
        {
            var hasMatch = PathContains.Any(s =>
                filePath.Contains(s, StringComparison.OrdinalIgnoreCase));
            if (!hasMatch)
            {
                return false;
            }
        }

        // Check file size (only if file exists)
        if ((MinSize.HasValue || MaxSize.HasValue) && File.Exists(filePath))
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var size = fileInfo.Length;

                if (MinSize.HasValue && size < MinSize.Value)
                {
                    return false;
                }

                if (MaxSize.HasValue && size > MaxSize.Value)
                {
                    return false;
                }
            }
            catch
            {
                // If we can't read file info, skip size check
            }
        }

        return true;
    }
}

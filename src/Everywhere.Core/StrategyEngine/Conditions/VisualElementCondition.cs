using System.Diagnostics;
using System.Text.RegularExpressions;
using Everywhere.Chat;
using Everywhere.Interop;
using ZLinq;

namespace Everywhere.StrategyEngine.Conditions;

/// <summary>
/// Base class for conditions that match specific attachment types.
/// </summary>
public abstract class AttachmentConditionBase<T> : IAttachmentCondition where T : ChatAttachment
{
    public abstract AttachmentType TargetType { get; }

    /// <summary>
    /// If true, at least one matching attachment must be primary.
    /// </summary>
    public bool IsPrimaryRequired { get; init; }

    /// <summary>
    /// Minimum number of matching attachments required.
    /// </summary>
    public int MinCount { get; init; } = 1;

    /// <summary>
    /// Maximum number of matching attachments allowed (-1 for unlimited).
    /// </summary>
    public int MaxCount { get; init; } = -1;

    public bool Evaluate(StrategyContext context)
    {
        var matchingAttachments = context.Attachments.AsValueEnumerable().OfType<T>()
            .Where(MatchesAttachment)
            .ToList();

        if (matchingAttachments.Count < MinCount)
        {
            return false;
        }

        if (MaxCount >= 0 && matchingAttachments.Count > MaxCount)
        {
            return false;
        }

        if (IsPrimaryRequired && !matchingAttachments.Any(a => a.IsPrimary))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Override to implement type-specific matching logic.
    /// </summary>
    protected abstract bool MatchesAttachment(T attachment);
}

/// <summary>
/// Condition that matches <see cref="VisualElementAttachment"/>.
/// </summary>
public sealed class VisualElementCondition : AttachmentConditionBase<VisualElementAttachment>
{
    public override AttachmentType TargetType => AttachmentType.VisualElement;

    /// <summary>
    /// Required element types (matches if element is any of these types).
    /// </summary>
    public IReadOnlyList<VisualElementType>? ElementTypes { get; init; }

    /// <summary>
    /// Required element states (all specified states must be present).
    /// </summary>
    public VisualElementStates? RequiredStates { get; init; }

    /// <summary>
    /// Regex pattern to match against element Name.
    /// </summary>
    public Regex? NamePattern { get; init; }

    /// <summary>
    /// Regex pattern to match against element GetText().
    /// </summary>
    public Regex? TextPattern { get; init; }

    /// <summary>
    /// Maximum text length to retrieve for matching (performance optimization).
    /// </summary>
    public int TextMaxLength { get; init; } = 1000;

    /// <summary>
    /// Process names to match (case-insensitive).
    /// </summary>
    public IReadOnlyList<string>? ProcessNames { get; init; }

    /// <summary>
    /// Ancestor query to verify element is within a specific container.
    /// </summary>
    public string? AncestorQuery { get; init; }

    /// <summary>
    /// Cross-path probe queries to check other branches of the visual tree.
    /// </summary>
    public IReadOnlyList<ProbeQuery>? ProbeQueries { get; init; }

    protected override bool MatchesAttachment(VisualElementAttachment attachment)
    {
        var element = attachment.Element?.Target;
        if (element is null)
        {
            return false;
        }

        // Check element type
        if (ElementTypes is { Count: > 0 } && !ElementTypes.Contains(element.Type))
        {
            return false;
        }

        // Check required states
        if (RequiredStates is { } states && (element.States & states) != states)
        {
            return false;
        }

        // Check name pattern
        if (NamePattern is not null)
        {
            var name = element.Name ?? string.Empty;
            if (!NamePattern.IsMatch(name))
            {
                return false;
            }
        }

        // Check text pattern
        if (TextPattern is not null)
        {
            var text = element.GetText(TextMaxLength) ?? string.Empty;
            if (!TextPattern.IsMatch(text))
            {
                return false;
            }
        }

        // Check process name
        if (ProcessNames is { Count: > 0 })
        {
            var processName = GetProcessName(element);
            if (processName is null ||
                !ProcessNames.Any(p => p.Equals(processName, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        // TODO: Implement ancestor query and probe queries in Query engine
        // For now, skip these advanced checks

        return true;
    }

    private static string? GetProcessName(IVisualElement element)
    {
        var processId = element.ProcessId;
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            return Process.GetProcessById(processId).ProcessName;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// A probe query for cross-path visual tree matching.
/// </summary>
public sealed record ProbeQuery
{
    /// <summary>
    /// The query string in visual element query language.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// If true, the probe must find at least one result for the condition to match.
    /// </summary>
    public bool Required { get; init; } = true;

    /// <summary>
    /// Optional name for this probe (for debugging).
    /// </summary>
    public string? Name { get; init; }
}

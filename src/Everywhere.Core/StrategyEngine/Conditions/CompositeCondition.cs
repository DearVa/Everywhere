using Everywhere.Chat;

namespace Everywhere.StrategyEngine.Conditions;

/// <summary>
/// Combines multiple conditions with AND or OR logic.
/// </summary>
public sealed class CompositeCondition : IStrategyCondition
{
    /// <summary>
    /// The logic operator for combining conditions.
    /// </summary>
    public CompositeLogic Logic { get; init; } = CompositeLogic.And;

    /// <summary>
    /// The conditions to combine.
    /// </summary>
    public required IReadOnlyList<IStrategyCondition> Conditions { get; init; }

    public bool Evaluate(StrategyContext context)
    {
        if (Conditions.Count == 0)
        {
            return true;
        }

        return Logic switch
        {
            CompositeLogic.And => Conditions.All(c => c.Evaluate(context)),
            CompositeLogic.Or => Conditions.Any(c => c.Evaluate(context)),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    /// <summary>
    /// Creates an AND composite condition.
    /// </summary>
    public static CompositeCondition And(params IStrategyCondition[] conditions) =>
        new() { Logic = CompositeLogic.And, Conditions = conditions };

    /// <summary>
    /// Creates an OR composite condition.
    /// </summary>
    public static CompositeCondition Or(params IStrategyCondition[] conditions) =>
        new() { Logic = CompositeLogic.Or, Conditions = conditions };
}

/// <summary>
/// Logic operators for combining conditions.
/// </summary>
public enum CompositeLogic
{
    /// <summary>
    /// All conditions must match.
    /// </summary>
    And,

    /// <summary>
    /// Any condition can match.
    /// </summary>
    Or
}

/// <summary>
/// Condition groups with OR logic between groups and AND logic within groups.
/// This matches the YAML configuration model.
/// </summary>
public sealed class ConditionGroups : IStrategyCondition
{
    /// <summary>
    /// Groups of conditions. Strategy matches if ANY group matches.
    /// Within a group, ALL conditions must match.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<IStrategyCondition>> Groups { get; init; }

    public bool Evaluate(StrategyContext context)
    {
        if (Groups.Count == 0)
        {
            return true;
        }

        // OR between groups
        foreach (var group in Groups)
        {
            // AND within group
            if (group.All(c => c.Evaluate(context)))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A condition that always matches. Useful for global strategies.
/// </summary>
public sealed class AlwaysTrueCondition : IStrategyCondition
{
    public static readonly AlwaysTrueCondition Instance = new();

    public bool Evaluate(StrategyContext context) => true;
}

/// <summary>
/// A condition that never matches. Useful for disabled strategies.
/// </summary>
public sealed class AlwaysFalseCondition : IStrategyCondition
{
    public static readonly AlwaysFalseCondition Instance = new();

    public bool Evaluate(StrategyContext context) => false;
}

/// <summary>
/// Negates another condition.
/// </summary>
public sealed class NotCondition : IStrategyCondition
{
    public required IStrategyCondition Inner { get; init; }

    public bool Evaluate(StrategyContext context) => !Inner.Evaluate(context);
}

/// <summary>
/// Checks if the context has any attachments.
/// </summary>
public sealed class HasAttachmentsCondition : IStrategyCondition
{
    /// <summary>
    /// Minimum number of attachments required.
    /// </summary>
    public int MinCount { get; init; } = 1;

    /// <summary>
    /// Optional type filter.
    /// </summary>
    public AttachmentType? Type { get; init; }

    public bool Evaluate(StrategyContext context)
    {
        var attachments = Type switch
        {
            AttachmentType.VisualElement => context.Attachments.OfType<VisualElementAttachment>().Cast<ChatAttachment>(),
            AttachmentType.TextSelection => context.Attachments.OfType<TextSelectionAttachment>(),
            AttachmentType.Text => context.Attachments.OfType<TextAttachment>(),
            AttachmentType.File => context.Attachments.OfType<FileAttachment>(),
            _ => context.Attachments
        };

        return attachments.Count() >= MinCount;
    }
}

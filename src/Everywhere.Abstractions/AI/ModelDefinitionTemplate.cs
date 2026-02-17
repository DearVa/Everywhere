namespace Everywhere.AI;

/// <summary>
/// Defines the properties of an AI model.
/// </summary>
public sealed record ModelDefinitionTemplate : IModelDefinition
{
    public required string ModelId { get; init; }

    public required string? Name { get; init; }

    public required bool SupportsReasoning { get; init; }

    public required bool SupportsToolCall { get; init; }

    public required Modalities InputModalities { get; init; }

    public required Modalities OutputModalities { get; init; }

    public required int ContextLimit { get; init; }

    /// <summary>
    /// Gets or sets the default model in a model provider.
    /// This indicates the best (powerful but economical) model in the provider.
    /// </summary>
    public bool IsDefault { get; init; }

    public bool Equals(ModelDefinitionTemplate? other) => ModelId == other?.ModelId;

    public override int GetHashCode() => ModelId.GetHashCode();

    public override string ToString() => Name ?? ModelId;
}
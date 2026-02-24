using MessagePack;

namespace Everywhere.AI;

/// <summary>
/// Defines the properties of an AI model.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true)]
public sealed partial record ModelDefinitionTemplate : IModelDefinition
{
    [Key(0)]
    public required string ModelId { get; init; }

    [Key(1)]
    public required string? Name { get; init; }

    [Key(2)]
    public required bool SupportsReasoning { get; init; }

    [Key(3)]
    public required bool SupportsToolCall { get; init; }

    [Key(4)]
    public DateOnly? KnowledgeCutoff { get; init; }

    [Key(5)]
    public DateOnly? ReleaseDate { get; init; }

    [Key(6)]
    public DateOnly? DeprecationDate { get; init; }

    [Key(7)]
    public required Modalities InputModalities { get; init; }

    [Key(8)]
    public required Modalities OutputModalities { get; init; }

    [Key(9)]
    public required int ContextLimit { get; init; }

    [Key(10)]
    public required int OutputLimit { get; init; }

    /// <summary>
    /// Gets or sets the default model in a model provider.
    /// This indicates the best (powerful but economical) model in the provider.
    /// </summary>
    public bool IsDefault { get; init; }

    public bool Equals(ModelDefinitionTemplate? other) => ModelId == other?.ModelId;

    public override int GetHashCode() => ModelId.GetHashCode();

    public override string ToString() => Name ?? ModelId;
}
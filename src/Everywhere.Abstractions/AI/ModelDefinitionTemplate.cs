using System.Text.Json.Serialization;

namespace Everywhere.AI;

/// <summary>
/// Defines the properties of an AI model.
/// </summary>
public record ModelDefinitionTemplate
{
    /// <summary>
    /// Unique identifier for the model definition.
    /// This also serves as the model ID used in API requests.
    /// e.g., "gpt-4", "gpt-3.5-turbo".
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Model id for API calling.
    /// This is typically the same as <see cref="Id"/>, but can be customized
    /// to use a different identifier for API requests.
    /// </summary>
    public required string ModelId { get; set; }

    /// <summary>
    /// Display name of the model, used for UI.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Indicates whether the model supports image input capabilities.
    /// </summary>
    public bool IsImageInputSupported { get; set; }

    /// <summary>
    /// Indicates whether the model supports function calling capabilities.
    /// </summary>
    public bool IsFunctionCallingSupported { get; set; }

    /// <summary>
    /// Indicates whether the model supports tool calls.
    /// </summary>
    public bool IsDeepThinkingSupported { get; set; }

    /// <summary>
    /// Maximum number of tokens that the model can process in a single request.
    /// aka, the maximum context length.
    /// </summary>
    public int MaxTokens { get; set; }

    /// <summary>
    /// Gets or sets the default model in a model provider.
    /// This indicates the best (powerful but economical) model in the provider.
    /// </summary>
    [JsonIgnore]
    public bool IsDefault { get; set; }

    public virtual bool Equals(ModelDefinitionTemplate? other) => Id == other?.Id;

    public override int GetHashCode() => Id.GetHashCode();

    public override string ToString() => DisplayName ?? Id;
}
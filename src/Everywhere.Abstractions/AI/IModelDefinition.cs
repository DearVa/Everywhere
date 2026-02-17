namespace Everywhere.AI;

public interface IModelDefinition
{
    /// <summary>
    /// Unique identifier for the model definition.
    /// This also serves as the model ID used in API requests.
    /// </summary>
    string? ModelId { get; }

    /// <summary>
    /// Display name of the model, used for UI.
    /// </summary>
    string? Name { get; }

    /// <summary>
    /// Whether the model is capable of reasoning (deep thinking).
    /// </summary>
    bool SupportsReasoning { get; }

    /// <summary>
    /// Whether the model supports function/tool calling.
    /// </summary>
    bool SupportsToolCall { get; }

    /// <summary>
    /// Modalities supported by the model for input.
    /// </summary>
    Modalities InputModalities { get; }

    /// <summary>
    /// Modalities supported by the model for output. This is used to determine the type of content the model can generate.
    /// </summary>
    Modalities OutputModalities { get; }

    /// <summary>
    /// Maximum number of tokens that the model can process in a single request.
    /// </summary>
    int ContextLimit { get; }
}
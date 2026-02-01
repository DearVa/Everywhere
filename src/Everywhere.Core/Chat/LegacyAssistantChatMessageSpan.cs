using MessagePack;

namespace Everywhere.Chat;

/// <summary>
/// Represents a span of content in an assistant chat message.
/// A span can contain markdown content and associated function calls.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class LegacyAssistantChatMessageSpan
{
    [Key(0)]
    public string? Content { get; init; }

    [Key(1)]
    public IReadOnlyList<FunctionCallChatMessage>? FunctionCalls { get; init; }

    [Key(2)]
    public DateTimeOffset CreatedAt { get; init; }

    [Key(3)]
    public DateTimeOffset FinishedAt { get; init; }

    [Key(4)]
    public string? ReasoningOutput { get; init; }

    [Key(5)]
    public DateTimeOffset? ReasoningFinishedAt { get; init; }
}
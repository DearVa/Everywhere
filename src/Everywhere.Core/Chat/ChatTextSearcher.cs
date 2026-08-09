using LiveMarkdown.Avalonia;

namespace Everywhere.Chat;

/// <summary>
/// Defines the user-visible text that participates in chat search. Tool calls, function arguments,
/// results and plugin display blocks deliberately stay outside this projection.
/// </summary>
internal static class ChatTextSearcher
{
    /// <summary>
    /// An instance of MarkdownPipeline is immutable, thread-safe, and should be reused when parsing multiple inputs.
    /// </summary>
    public static MarkdownTextProjector SharedMarkdownTextProjector { get; } = new();

    public static bool Contains(
        ChatContext context,
        TextSearchPattern pattern,
        MarkdownTextProjector markdownProjector,
        CancellationToken cancellationToken)
    {
        // Keep the DynamicData edit lock limited to capturing stable message references.
        // Markdown projection can be expensive and must run after the collection lock is released.
        var messages = context.Read(items =>
        {
            var result = new ChatMessage[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                result[i] = items[i].Message;
            }

            return result;
        });

        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Contains(message, pattern, markdownProjector, cancellationToken)) return true;
        }

        return false;
    }

    private static bool Contains(
        ChatMessage message,
        TextSearchPattern pattern,
        MarkdownTextProjector markdownProjector,
        CancellationToken cancellationToken) => message switch
    {
        UserChatMessage { Content: { Length: > 0 } content } => pattern.FindRanges(content).Any(),
        AssistantChatMessage assistant => Contains(assistant, pattern, markdownProjector, cancellationToken),
        _ => false
    };

    private static bool Contains(
        AssistantChatMessage message,
        TextSearchPattern pattern,
        MarkdownTextProjector markdownProjector,
        CancellationToken cancellationToken)
    {
        var sources = new List<string>();
        message.Edit(spans =>
        {
            foreach (var span in spans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (span is not AssistantChatMessageTextSpan textSpan || textSpan.ContentMarkdownBuilder.Length == 0)
                {
                    continue;
                }

                sources.Add(textSpan.ContentMarkdownBuilder.ToString());
            }
        });

        if (sources.Count == 0) return false;

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Historical matching consumes this immutable snapshot immediately and does not
            // reconcile it with a live renderer, so no persistent source version is required.
            var projection = markdownProjector.Project(new ObservableStringBuilderSnapshot(source, 0), cancellationToken);
            if (projection.Buffers.AsValueEnumerable().Any(buffer => pattern.FindRanges(buffer.Text).AsValueEnumerable().Any())) return true;
        }

        return false;
    }
}
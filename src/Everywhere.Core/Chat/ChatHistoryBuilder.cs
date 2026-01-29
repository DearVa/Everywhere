using Everywhere.Common;
using Everywhere.Utilities;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Serilog;
using ZLinq;

namespace Everywhere.Chat;

/// <summary>
/// Builds ChatHistory (SK) from ChatMessages (Everywhere).
/// </summary>
public static class ChatHistoryBuilder
{
    public static async ValueTask<ChatHistory> BuildChatHistoryAsync(
        IEnumerable<ChatMessage> chatMessages,
        CancellationToken cancellationToken = default)
    {
        var chatHistory = new ChatHistory();
        foreach (var chatMessage in chatMessages)
        {
            await foreach (var chatMessageContent in CreateChatMessageContentsAsync(chatMessage, cancellationToken))
            {
                chatHistory.Add(chatMessageContent);
            }
        }

        return chatHistory;
    }

    /// <summary>
    /// Creates chat message contents from a chat message.
    /// </summary>
    /// <param name="chatMessage"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private static async IAsyncEnumerable<ChatMessageContent> CreateChatMessageContentsAsync(
        ChatMessage chatMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        switch (chatMessage)
        {
            case SystemChatMessage system:
            {
                yield return new ChatMessageContent(AuthorRole.System, system.SystemPrompt);
                break;
            }
            case AssistantChatMessage assistant:
            {
                // ReSharper disable once ForCanBeConvertedToForeach
                // foreach would create an enumerator object, which will cause thread lock issues.
                for (var spanIndex = 0; spanIndex < assistant.Spans.Count; spanIndex++)
                {
                    var span = assistant.Spans[spanIndex];
                    if (span.MarkdownBuilder.Length > 0)
                    {
                        var metadata = span.ReasoningOutput.IsNullOrEmpty() ?
                            null :
                            new Dictionary<string, object?>
                            {
                                { "reasoning_content", span.ReasoningOutput }
                            };
                        yield return new ChatMessageContent(AuthorRole.Assistant, span.MarkdownBuilder.ToString(), metadata: metadata);
                    }

                    // ReSharper disable once ForCanBeConvertedToForeach
                    // foreach would create an enumerator object, which will cause thread lock issues.
                    for (var callIndex = 0; callIndex < span.FunctionCalls.Count; callIndex++)
                    {
                        var functionCallChatMessage = span.FunctionCalls[callIndex];
                        await foreach (var actionChatMessageContent in CreateChatMessageContentsAsync(functionCallChatMessage, cancellationToken))
                        {
                            yield return actionChatMessageContent;
                        }
                    }
                }
                break;
            }
            case UserChatMessage user:
            {
                var items = new ChatMessageContentItemCollection();
                foreach (var chatAttachment in user.Attachments.AsValueEnumerable().ToList())
                {
                    await PopulateKernelContentsAsync(chatAttachment, items, cancellationToken);
                }

                if (items.Count > 0)
                {
                    // If there are attachments, add the user content as a separate item.
                    items.Add(
                        new TextContent(
                            $"""
                             <UserRequestStart/>
                             {user.Content}
                             """));
                }
                else
                {
                    // No attachments, just add the content directly.
                    items.Add(new TextContent(user.Content));
                }

                yield return new ChatMessageContent(AuthorRole.User, items);
                break;
            }
            case FunctionCallChatMessage functionCall:
            {
                var functionCallMessage = new ChatMessageContent(AuthorRole.Assistant, content: null);
                functionCallMessage.Items.AddRange(functionCall.Calls);
                yield return functionCallMessage;

                // ReSharper disable once ForCanBeConvertedToForeach
                // foreach would create an enumerator object, which will cause thread lock issues.
                for (var callIndex = 0; callIndex < functionCall.Calls.Count; callIndex++)
                {
                    var callId = functionCall.Calls[callIndex].Id;
                    if (callId.IsNullOrEmpty())
                    {
                        throw new InvalidOperationException("Function call ID cannot be null or empty when creating chat message contents.");
                    }

                    var resultContent = functionCall.Results.AsValueEnumerable().FirstOrDefault(r => r.CallId == callId);
                    yield return resultContent?.ToChatMessage() ?? new ChatMessageContent(
                        AuthorRole.Tool,
                        [
                            new FunctionResultContent(
                                functionCall.Calls[callIndex],
                                $"Error: No result found for function call ID '{callId}'. " +
                                $"This may caused by an error during function execution or user cancellation.")
                        ]);

                    if (await TryCreateExtraToolCallResultsContentAsync(
                            resultContent,
                            cancellationToken) is { } extraToolCallResultsContent)
                    {
                        yield return extraToolCallResultsContent;
                    }
                }

                break;
            }
            case { Role.Label: "system" or "user" or "developer" or "tool" }:
            {
                yield return new ChatMessageContent(chatMessage.Role, chatMessage.ToString());
                break;
            }
        }
    }

    /// <summary>
    /// Creates a special ChatMessageContent to hold the extra tool call results if there are any attachments in the function call chat message.
    /// This is a workaround to include additional tool call results that are not part of the standard function call results. e.g. images, audio, etc.
    /// </summary>
    /// <param name="resultContent"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async ValueTask<ChatMessageContent?> TryCreateExtraToolCallResultsContentAsync(
        FunctionResultContent? resultContent,
        CancellationToken cancellationToken)
    {
        if (resultContent?.Result is not ChatAttachment chatAttachment) return null;

        var items = new ChatMessageContentItemCollection { new TextContent("<ExtraToolCallResultAttachments>") };
        await PopulateKernelContentsAsync(chatAttachment, items, cancellationToken);

        if (items.Count == 1) // No valid attachment added
        {
            return null;
        }

        items.Add(new TextContent("</ExtraToolCallResultAttachments>"));
        return new ChatMessageContent(AuthorRole.User, items);
    }

    /// <summary>
    /// Creates KernelContent from a chat attachment, and adds them to the contents list.
    /// </summary>
    /// <param name="chatAttachment"></param>
    /// <param name="contents"></param>
    /// <param name="cancellationToken"></param>
    private static async ValueTask PopulateKernelContentsAsync(
        ChatAttachment chatAttachment,
        ChatMessageContentItemCollection contents,
        CancellationToken cancellationToken)
    {
        switch (chatAttachment)
        {
            case ChatTextSelectionAttachment textSelection:
            {
                contents.Add(
                    new TextContent(
                        $"""
                         <Attachment type="text-selection">
                         <Text>
                         {textSelection.Text}
                         </Text>
                         <AssociatedElement>
                         {textSelection.Content ?? "omitted due to duplicate"}
                         </AssociatedElement>
                         </Attachment>
                         """));
                break;
            }
            case ChatVisualElementAttachment visualElement:
            {
                contents.Add(
                    new TextContent(
                        $"""
                         <Attachment type="visual-element">
                         {visualElement.Content ?? "omitted due to duplicate"}
                         </Attachment>
                         """));
                break;
            }
            case ChatTextAttachment text:
            {
                contents.Add(
                    new TextContent(
                        $"""
                         <Attachment type="text">
                         {text}
                         </Attachment>
                         """));
                break;
            }
            case ChatFileAttachment file:
            {
                var fileInfo = new FileInfo(file.FilePath);
                if (!fileInfo.Exists || fileInfo.Length <= 0 || fileInfo.Length > 25 * 1024 * 1024) // TODO: Configurable max file size?
                {
                    return;
                }

                byte[] data;
                try
                {
                    data = await File.ReadAllBytesAsync(file.FilePath, cancellationToken);
                }
                catch (Exception ex)
                {
                    // If we fail to read the file, just skip it.
                    // The file might be deleted or moved.
                    // We don't want to fail the whole message because of one attachment.
                    // Just log the error and continue.
                    ex = HandledSystemException.Handle(ex, true); // treat all as expected
                    Log.ForContext(typeof(ChatHistoryBuilder)).Warning(ex, "Failed to read attachment file '{FilePath}'", file.FilePath);
                    return;
                }

                contents.Add(
                    new TextContent(
                        $"""
                         <Attachment type="file" path="{file.FilePath}" mimeType="{file.MimeType}" description="{file.Description}">
                         """));
                contents.Add(
                    FileUtilities.GetCategory(file.MimeType) switch
                    {
                        FileTypeCategory.Audio => new AudioContent(data, file.MimeType),
                        FileTypeCategory.Image => new ImageContent(data, file.MimeType),
                        _ => new BinaryContent(data, file.MimeType)
                    });
                contents.Add(new TextContent("</Attachment>"));
                break;
            }
        }
    }
}
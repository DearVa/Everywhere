using Everywhere.AI;
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
        string systemPrompt,
        IEnumerable<ChatMessage> chatMessages,
        Modalities supportedModalities,
        CancellationToken cancellationToken = default)
    {
        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(systemPrompt);

        foreach (var chatMessage in chatMessages)
        {
            await foreach (var chatMessageContent in CreateChatMessageContentsAsync(chatMessage, supportedModalities, cancellationToken))
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
    /// <param name="supportedModalities"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private static async IAsyncEnumerable<ChatMessageContent> CreateChatMessageContentsAsync(
        ChatMessage chatMessage,
        Modalities supportedModalities,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        switch (chatMessage)
        {
            case AssistantChatMessage assistant:
            {
                var items = new ChatMessageContentItemCollection();
                var metadata = new MetadataDictionary(1);
                foreach (var span in assistant.Items)
                {
                    switch (span)
                    {
                        case AssistantChatMessageTextSpan { Content: { Length: > 0 } content }:
                        {
                            items.Add(new TextContent(content, metadata: span.Metadata));
                            break;
                        }
                        case AssistantChatMessageFunctionCallSpan { Items: { Count: > 0 } functionCalls }:
                        {
                            // 1. Add all function calls as content items.
                            items.AddRange(functionCalls.SelectMany(f => f.Calls));

                            // 2. Yield the assistant message with function call items first
                            yield return new ChatMessageContent(AuthorRole.Assistant, items, metadata: metadata);
                            items = [];

                            // 3. Yield the function call results as separate tool messages
                            var extraToolCallResults = new List<ChatAttachment>();
                            foreach (var functionCall in functionCalls)
                            {
                                foreach (var call in functionCall.Calls)
                                {
                                    var callId = call.Id;
                                    if (callId.IsNullOrEmpty())
                                    {
                                        throw new InvalidOperationException("Function CallId cannot be null or empty.");
                                    }

                                    var resultContent = functionCall.Results.AsValueEnumerable().FirstOrDefault(r => r.CallId == callId);
                                    yield return resultContent?.ToChatMessage() ?? new ChatMessageContent(
                                        AuthorRole.Tool,
                                        [
                                            new FunctionResultContent(
                                                call,
                                                $"Error: No result found for function call ID '{callId}'. " +
                                                $"This may caused by an error during function execution or user cancellation.")
                                        ]);

                                    // If the function call result is a ChatAttachment, add it as extra attachment message(s).
                                    if (resultContent?.Result is ChatAttachment extraToolCallResult)
                                    {
                                        extraToolCallResults.Add(extraToolCallResult);
                                    }
                                }
                            }

                            // 4. Workaround for any function call results that are ChatAttachments
                            // We put them as user message because tool message doesn't support attachments
                            if (extraToolCallResults.Count > 0)
                            {
                                var attachmentItems = new ChatMessageContentItemCollection { new TextContent("<ExtraToolCallResultAttachments>") };
                                foreach (var extraToolCallResult in extraToolCallResults)
                                {
                                    await PopulateKernelContentsAsync(extraToolCallResult, attachmentItems, cancellationToken);
                                }

                                // No valid attachment added, do nothing
                                if (attachmentItems.Count == 1) break;

                                attachmentItems.Add(new TextContent("</ExtraToolCallResultAttachments>"));
                                yield return new ChatMessageContent(AuthorRole.User, attachmentItems);
                            }

                            break;
                        }
                        case AssistantChatMessageReasoningSpan { ReasoningOutput: { Length: > 0 } reasoningOutput }:
                        {
                            metadata["reasoning_content"] = reasoningOutput;
                            break;
                        }
                        case AssistantChatMessageImageSpan { ImageOutput: { } imageOutput }:
                        {
                            try
                            {
                                var imageData = await File.ReadAllBytesAsync(imageOutput.FilePath, cancellationToken);
                                items.Add(
                                    new ImageContent(imageData, imageOutput.MimeType)
                                    {
                                        Metadata = span.Metadata
                                    });
                            }
                            catch
                            {
                                items.Add(new TextContent("The image is generated but failed to be read from disk.", metadata: span.Metadata));
                            }
                            break;
                        }
                    }
                }

                if (items.Count > 0)
                {
                    yield return new ChatMessageContent(AuthorRole.Assistant, items, metadata: metadata);
                }
                break;
            }
            case UserChatMessage user:
            {
                var items = new ChatMessageContentItemCollection();
                foreach (var chatAttachment in user.Attachments.AsValueEnumerable().ToList())
                {
                    await PopulateKernelContentsAsync(chatAttachment, items, supportedModalities, cancellationToken);
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
            case { Role.Label: "system" or "user" or "developer" or "tool" }:
            {
                yield return new ChatMessageContent(chatMessage.Role, chatMessage.ToString());
                break;
            }
        }
    }

    /// <summary>
    /// Creates KernelContent from a chat attachment, and adds them to the contents list.
    /// </summary>
    /// <param name="chatAttachment"></param>
    /// <param name="contents"></param>
    /// <param name="supportedModalities"></param>
    /// <param name="cancellationToken"></param>
    private static async ValueTask PopulateKernelContentsAsync(
        ChatAttachment chatAttachment,
        ChatMessageContentItemCollection contents,
        Modalities supportedModalities,
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
                    break;
                }

                byte[] data;
                try
                {
                    await using var stream = fileInfo.OpenRead();
                    var extension = Path.GetExtension(file.FilePath).ToLowerInvariant();
                    if (!FileUtilities.KnownMimeTypes.TryGetValue(extension, out var mimeType))
                    {
                        mimeType = await FileUtilities.DetectMimeTypeAsync(stream, cancellationToken);
                    }

                    if (!supportedModalities.SupportsMimeType(mimeType))
                    {
                        break;
                    }

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
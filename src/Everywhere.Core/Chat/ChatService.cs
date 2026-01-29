using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Storage;
using Everywhere.Utilities;
using LiveMarkdown.Avalonia;
using Lucide.Avalonia;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenAI.Chat;
using ZLinq;
using ChatFunction = Everywhere.Chat.Plugins.ChatFunction;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;
using FunctionCallContent = Microsoft.SemanticKernel.FunctionCallContent;
using FunctionResultContent = Microsoft.SemanticKernel.FunctionResultContent;

namespace Everywhere.Chat;

public sealed partial class ChatService(
    IChatContextManager chatContextManager,
    IChatPluginManager chatPluginManager,
    IKernelMixinFactory kernelMixinFactory,
    IBlobStorage blobStorage,
    Settings settings,
    PersistentState persistentState,
    ILogger<ChatService> logger
) : IChatService, IChatPluginUserInterface
{
    /// <summary>
    /// Context for function call invocations.
    /// </summary>
    private record FunctionCallContext(
        Kernel Kernel,
        ChatContext ChatContext,
        ChatPlugin Plugin,
        ChatFunction Function,
        FunctionCallChatMessage ChatMessage
    )
    {
        public string PermissionKey => $"{Plugin.Key}.{Function.KernelFunction.Name}";
    }

    private readonly ActivitySource _activitySource = new(typeof(ChatService).FullName.NotNull());
    private readonly Stack<FunctionCallContext> _functionCallContextStack = new();
    private FunctionCallContext? _currentFunctionCallContext;

    public async Task SendMessageAsync(UserChatMessage message, CancellationToken cancellationToken)
    {
        var customAssistant = settings.Model.SelectedCustomAssistant;
        if (customAssistant is null) return;

        using var activity = _activitySource.StartActivity();

        var chatContext = chatContextManager.Current;
        activity?.SetTag("chat.context.id", chatContext.Metadata.Id);
        chatContext.Add(message);

        await ProcessUserChatMessageAsync(chatContext, customAssistant, message, cancellationToken);

        var assistantChatMessage = new AssistantChatMessage { IsBusy = true };
        chatContext.Add(assistantChatMessage);

        await RunGenerateAsync(chatContext, customAssistant, assistantChatMessage, cancellationToken);
    }

    public async Task RetryAsync(ChatMessageNode node, CancellationToken cancellationToken)
    {
        var customAssistant = settings.Model.SelectedCustomAssistant;
        if (customAssistant is null) return;

        using var activity = _activitySource.StartActivity();
        activity?.SetTag("chat.context.id", node.Context.Metadata.Id);

        if (node.Message.Role != AuthorRole.Assistant)
        {
            throw new InvalidOperationException("Only assistant messages can be retried.");
        }

        var assistantChatMessage = new AssistantChatMessage { IsBusy = true };
        node.Context.CreateBranchOn(node, assistantChatMessage);

        await RunGenerateAsync(node.Context, customAssistant, assistantChatMessage, cancellationToken);
    }

    public async Task EditAsync(ChatMessageNode originalNode, UserChatMessage newMessage, CancellationToken cancellationToken)
    {
        var customAssistant = settings.Model.SelectedCustomAssistant;
        if (customAssistant is null) return;

        using var activity = _activitySource.StartActivity();

        var chatContext = chatContextManager.Current;
        activity?.SetTag("chat.context.id", chatContext.Metadata.Id);

        if (originalNode.Message.Role != AuthorRole.User)
        {
            throw new InvalidOperationException("Only user messages can be retried.");
        }

        chatContext.CreateBranchOn(originalNode, newMessage);

        await ProcessUserChatMessageAsync(chatContext, customAssistant, newMessage, cancellationToken);

        var assistantChatMessage = new AssistantChatMessage { IsBusy = true };
        chatContext.Add(assistantChatMessage);

        await RunGenerateAsync(chatContext, customAssistant, assistantChatMessage, cancellationToken);
    }

    private async Task ProcessUserChatMessageAsync(
        ChatContext chatContext,
        CustomAssistant customAssistant,
        UserChatMessage userChatMessage,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity();
        activity?.SetTag("chat.context.id", chatContext.Metadata.Id);

        // All ChatVisualElementAttachment should be strongly referenced here.
        // So we have to need to check alive status before building visual tree XML.
        var visualElementAttachments = userChatMessage
            .Attachments
            .AsValueEnumerable()
            .OfType<ChatVisualElementAttachment>()
            .ToList();

        if (visualElementAttachments.Count == 0) return;

        var analyzingContextMessage = new ActionChatMessage(
            new AuthorRole("action"),
            LucideIconKind.TextSearch,
            LocaleKey.ActionChatMessage_Header_AnalyzingContext)
        {
            IsBusy = true
        };

        try
        {
            chatContext.Add(analyzingContextMessage);

            // Building the visual tree XML includes the following steps:
            // 1. Gather required parameters, such as max tokens, detail level, etc.
            // 2. Group the visual elements and build the XML in separate tasks.
            // 3. Populate result into ChatVisualElementAttachment.Xml

            var maxTokens = Math.Max(customAssistant.MaxTokens, 4096);
            var approximateTokenLimit = Math.Min(persistentState.VisualTreeTokenLimit, maxTokens / 10);
            var detailLevel = settings.ChatWindow.VisualTreeDetailLevel;

            await Task.Run(
                () =>
                {
                    // Build and populate the XML for visual elements.
                    var builtVisualElements = VisualTreeBuilder.BuildAndPopulate(
                        visualElementAttachments,
                        approximateTokenLimit,
                        chatContext.VisualElements.Count + 1,
                        detailLevel,
                        cancellationToken);

                    // Adds the visual elements to the chat context for future reference.
                    chatContext.VisualElements.AddRange(builtVisualElements);

                    // Then deactivate all the references, making them weak references.
                    foreach (var reference in userChatMessage
                                 .Attachments
                                 .AsValueEnumerable()
                                 .OfType<ChatVisualElementAttachment>()
                                 .Select(a => a.Element)
                                 .OfType<ResilientReference<IVisualElement>>())
                    {
                        reference.IsActive = false;
                    }

                    // After this, only the chat context holds strong references to the visual elements.
                },
                cancellationToken);
        }
        catch (Exception e)
        {
            e = HandledChatException.Handle(e);
            activity?.SetStatus(ActivityStatusCode.Error, e.Message.Trim());
            analyzingContextMessage.ErrorMessageKey = e.GetFriendlyMessage();
            logger.LogError(e, "Error analyzing visual tree");
        }
        finally
        {
            analyzingContextMessage.FinishedAt = DateTimeOffset.UtcNow;
            analyzingContextMessage.IsBusy = false;
        }
    }

    private IKernelMixin CreateKernelMixin(CustomAssistant customAssistant)
    {
        using var activity = _activitySource.StartActivity();

        try
        {
            var kernelMixin = kernelMixinFactory.GetOrCreate(customAssistant);
            activity?.SetTag("llm.model.id", customAssistant.ModelId);
            activity?.SetTag("llm.model.max_embedding", customAssistant.MaxTokens);
            return kernelMixin;
        }
        catch (Exception e)
        {
            // This method may throw if the model settings are invalid.
            activity?.SetStatus(ActivityStatusCode.Error, e.Message.Trim());
            throw;
        }
    }

    /// <summary>
    /// Kernel is very cheap to create, so we can create a new kernel for each request.
    /// This method builds the kernel based on the current settings.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="NotSupportedException"></exception>
    private async Task<Kernel> BuildKernelAsync(
        IKernelMixin kernelMixin,
        ChatContext chatContext,
        CustomAssistant customAssistant,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity();

        var builder = Kernel.CreateBuilder();

        builder.Services.AddSingleton(this);
        builder.Services.AddSingleton(kernelMixin.ChatCompletionService);
        builder.Services.AddSingleton(chatContextManager);
        builder.Services.AddSingleton(chatContext);
        builder.Services.AddSingleton(customAssistant);
        builder.Services.AddSingleton<IChatPluginUserInterface>(this);

        if (kernelMixin.IsFunctionCallingSupported && persistentState.IsToolCallEnabled)
        {
            var needToStartMcp = chatPluginManager.McpPlugins.AsValueEnumerable().Any(p => p is { IsEnabled: true, IsRunning: false });
            using var _ = needToStartMcp ? chatContext.SetBusyMessage(new DynamicResourceKey(LocaleKey.ChatContext_BusyMessage_StartingMcp)) : null;

            var chatPluginScope = await chatPluginManager.CreateScopeAsync(cancellationToken);
            builder.Services.AddSingleton(chatPluginScope);
            activity?.SetTag("plugins.count", chatPluginScope.Plugins.AsValueEnumerable().Count());

            foreach (var plugin in chatPluginScope.Plugins)
            {
                builder.Plugins.Add(plugin);
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Runs the GenerateAsync method in a separate task.
    /// This will clear the function call context stack before running.
    /// Means a fresh generation.
    /// </summary>
    /// <param name="chatContext"></param>
    /// <param name="customAssistant"></param>
    /// <param name="assistantChatMessage"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private Task RunGenerateAsync(
        ChatContext chatContext,
        CustomAssistant customAssistant,
        AssistantChatMessage assistantChatMessage,
        CancellationToken cancellationToken)
    {
        // Clear the function call context stack.
        _functionCallContextStack.Clear();

        return Task.Run(() => GenerateAsync(chatContext, customAssistant, assistantChatMessage, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Generates a response for the given chat context and assistant chat message.
    /// </summary>
    /// <param name="chatContext"></param>
    /// <param name="customAssistant"></param>
    /// <param name="assistantChatMessage"></param>
    /// <param name="cancellationToken"></param>
    public async Task GenerateAsync(
        ChatContext chatContext,
        CustomAssistant customAssistant,
        AssistantChatMessage assistantChatMessage,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity();
        activity?.SetTag("chat.context.id", chatContext.Metadata.Id);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var kernelMixin = CreateKernelMixin(customAssistant);
            var kernel = await BuildKernelAsync(kernelMixin, chatContext, customAssistant, cancellationToken);

            // Because the custom assistant maybe changed, we need to re-render the system prompt.
            chatContextManager.PopulateSystemPrompt(chatContext, customAssistant.SystemPrompt);

            var chatHistory = await ChatHistoryBuilder.BuildChatHistoryAsync(
                chatContext
                    .Items
                    .AsValueEnumerable()
                    .Select(n => n.Message)
                    .Where(m => !ReferenceEquals(m, assistantChatMessage)) // exclude the current assistant message
                    .Where(m => m.Role.Label is "system" or "assistant" or "user" or "tool")
                    .ToList(), // make a snapshot, otherwise async may cause thread deadlock
                cancellationToken);

            var toolCallCount = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chatSpan = new AssistantChatMessageSpan();
                assistantChatMessage.AddSpan(chatSpan);
                try
                {
                    var functionCallContents = await GetStreamingChatMessageContentsAsync(
                        kernel,
                        kernelMixin,
                        chatContext,
                        chatHistory,
                        customAssistant,
                        chatSpan,
                        assistantChatMessage,
                        cancellationToken);
                    if (functionCallContents.Count <= 0) break;

                    toolCallCount += functionCallContents.Count;

                    await InvokeFunctionsAsync(kernel, chatContext, chatHistory, chatSpan, functionCallContents, cancellationToken);
                }
                finally
                {
                    chatSpan.FinishedAt = DateTimeOffset.UtcNow;
                    chatSpan.ReasoningFinishedAt = DateTimeOffset.UtcNow;
                }
            }

            activity?.SetTag("tool_calls.count", toolCallCount);

            if (!chatContext.Metadata.IsTemporary && // Do not generate titles for temporary contexts.
                chatContext.Metadata.Topic.IsNullOrEmpty() &&
                chatHistory.Any(c => c.Role == AuthorRole.User) &&
                chatHistory.Any(c => c.Role == AuthorRole.Assistant) &&
                chatHistory.First(c => c.Role == AuthorRole.User).Content is { Length: > 0 } userMessage &&
                chatHistory.First(c => c.Role == AuthorRole.Assistant).Content is { Length: > 0 } assistantMessage)
            {
                // If the chat history only contains one user message and one assistant message,
                // we can generate a title for the chat context.
                GenerateTitleAsync(
                    kernelMixin,
                    userMessage,
                    assistantMessage,
                    chatContext.Metadata,
                    cancellationToken).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
            }
        }
        catch (Exception e)
        {
            e = HandledChatException.Handle(e);
            activity?.SetStatus(ActivityStatusCode.Error, e.Message.Trim());
            assistantChatMessage.ErrorMessageKey = e.GetFriendlyMessage();
            logger.LogError(e, "Error generating chat response");
        }
        finally
        {
            assistantChatMessage.FinishedAt = DateTimeOffset.UtcNow;
            assistantChatMessage.IsBusy = false;
        }
    }

    /// <summary>
    /// Gets streaming chat message contents from the chat completion service.
    /// </summary>
    /// <param name="kernel"></param>
    /// <param name="kernelMixin"></param>
    /// <param name="chatContext"></param>
    /// <param name="chatHistory"></param>
    /// <param name="customAssistant"></param>
    /// <param name="chatSpan"></param>
    /// <param name="assistantChatMessage"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<IReadOnlyList<FunctionCallContent>> GetStreamingChatMessageContentsAsync(
        Kernel kernel,
        IKernelMixin kernelMixin,
        ChatContext chatContext,
        ChatHistory chatHistory,
        CustomAssistant customAssistant,
        AssistantChatMessageSpan chatSpan,
        AssistantChatMessage assistantChatMessage,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity();

        var inputTokenCount = 0L;
        var outputTokenCount = 0L;
        var totalTokenCount = 0L;

        AuthorRole? authorRole = null;
        var assistantContentBuilder = new StringBuilder();
        var functionCallContentBuilder = new FunctionCallContentBuilder();
        var promptExecutionSettings = kernelMixin.GetPromptExecutionSettings(
            kernelMixin.IsFunctionCallingSupported && persistentState.IsToolCallEnabled ?
                FunctionChoiceBehavior.Auto(autoInvoke: false) :
                null);

        activity?.SetTag("llm.model.id", customAssistant.ModelId);
        activity?.SetTag("llm.model.max_embedding", customAssistant.MaxTokens);

        IDisposable? callingToolsBusyMessage = null;

        try
        {
            await foreach (var streamingContent in kernelMixin.ChatCompletionService.GetStreamingChatMessageContentsAsync(
                               // They absolutely must modify this ChatHistory internally.
                               // I can neither alter it nor inherit it.
                               // Let's copy the chat history to avoid modifying the original one.
                               new ChatHistory(chatHistory),
                               promptExecutionSettings,
                               kernel,
                               cancellationToken))
            {
                if (streamingContent.Metadata?.TryGetValue("Usage", out var usage) is true && usage is not null)
                {
                    switch (usage)
                    {
                        case UsageContent usageContent:
                        {
                            inputTokenCount = Math.Max(inputTokenCount, usageContent.Details.InputTokenCount ?? 0);
                            outputTokenCount = Math.Max(outputTokenCount, usageContent.Details.OutputTokenCount ?? 0);
                            totalTokenCount = Math.Max(totalTokenCount, usageContent.Details.TotalTokenCount ?? 0);
                            break;
                        }
                        case UsageDetails usageDetails:
                        {
                            inputTokenCount = Math.Max(inputTokenCount, usageDetails.InputTokenCount ?? 0);
                            outputTokenCount = Math.Max(outputTokenCount, usageDetails.OutputTokenCount ?? 0);
                            totalTokenCount = Math.Max(totalTokenCount, usageDetails.TotalTokenCount ?? 0);
                            break;
                        }
                        case ChatTokenUsage openAIUsage:
                        {
                            inputTokenCount = Math.Max(inputTokenCount, openAIUsage.InputTokenCount);
                            outputTokenCount = Math.Max(outputTokenCount, openAIUsage.OutputTokenCount);
                            totalTokenCount = Math.Max(totalTokenCount, openAIUsage.TotalTokenCount);
                            break;
                        }
                    }
                }

                foreach (var item in streamingContent.Items)
                {
                    switch (item)
                    {
                        case StreamingChatMessageContent { Content.Length: > 0 } chatMessageContent:
                        {
                            if (IsReasoningContent(chatMessageContent)) await HandleReasoningMessageAsync(chatMessageContent.Content);
                            else await HandleTextMessageAsync(chatMessageContent.Content);
                            break;
                        }
                        case StreamingTextContent { Text.Length: > 0 } textContent:
                        {
                            if (IsReasoningContent(textContent)) await HandleReasoningMessageAsync(textContent.Text);
                            else await HandleTextMessageAsync(textContent.Text);
                            break;
                        }
                        case StreamingReasoningContent reasoningContent:
                        {
                            await HandleReasoningMessageAsync(reasoningContent.Text);
                            break;
                        }
                    }

                    // Handle binary content separately.
                    if (item.InnerContent is BinaryContent { Data: not null, MimeType: not null } binaryContent &&
                        FileUtilities.IsOfCategory(binaryContent.MimeType, FileTypeCategory.Image) &&
                        (binaryContent.Metadata?.TryGetValue("thumbnail", out var isThumbnail) is not true || isThumbnail is false))
                    {
                        using var memoryStream = new MemoryStream(binaryContent.Data.Value.ToArray());
                        var blob = await blobStorage.StorageBlobAsync(memoryStream, binaryContent.MimeType, cancellationToken);
                        chatSpan.ImageOutput = new ChatFileAttachment(
                            new DynamicResourceKey(string.Empty),
                            blob.LocalPath,
                            blob.Sha256,
                            blob.MimeType);
                    }

                    bool IsReasoningContent(StreamingKernelContent content) =>
                        streamingContent.Metadata?.TryGetValue("reasoning", out var reasoning) is true && reasoning is true ||
                        content.Metadata?.TryGetValue("reasoning", out reasoning) is true && reasoning is true;

                    DispatcherOperation<ObservableStringBuilder> HandleTextMessageAsync(string text)
                    {
                        // Mark the reasoning as finished when we receive the first content chunk.
                        if (chatSpan.ReasoningOutput is not null && chatSpan.ReasoningFinishedAt is null)
                        {
                            chatSpan.ReasoningFinishedAt = DateTimeOffset.UtcNow;
                        }

                        assistantContentBuilder.Append(text);
                        return Dispatcher.UIThread.InvokeAsync(() => chatSpan.MarkdownBuilder.Append(text));
                    }

                    DispatcherOperation<ObservableStringBuilder> HandleReasoningMessageAsync(string text)
                    {
                        return Dispatcher.UIThread.InvokeAsync(() => chatSpan.ReasoningMarkdownBuilder.Append(text));
                    }
                }

                authorRole ??= streamingContent.Role;
                functionCallContentBuilder.Append(streamingContent);

                if (callingToolsBusyMessage is null && functionCallContentBuilder.Count > 0)
                {
                    callingToolsBusyMessage = chatContext.SetBusyMessage(new DynamicResourceKey(LocaleKey.ChatContext_BusyMessage_CallingTools));
                }
            }
        }
        finally
        {
            callingToolsBusyMessage?.Dispose();
        }

        // Mark the reasoning as finished if we have any reasoning output.
        if (!chatSpan.ReasoningOutput.IsNullOrEmpty() && chatSpan.ReasoningFinishedAt is null)
        {
            chatSpan.ReasoningFinishedAt = DateTimeOffset.UtcNow;
        }

        // Finally, add the assistant message to the chat history.
        if (assistantContentBuilder.Length > 0)
        {
            // Don't forget to add reasoning content metadata.
            chatHistory.AddMessage(AuthorRole.Assistant, assistantContentBuilder.ToString(), metadata: TryCreateReasoningMetadata(chatSpan));
        }

        assistantChatMessage.InputTokenCount = inputTokenCount;
        assistantChatMessage.OutputTokenCount = outputTokenCount;
        assistantChatMessage.TotalTokenCount = totalTokenCount;

        var functionCallContents = functionCallContentBuilder.Build();

        activity?.SetTag("chat.history.count", chatHistory.Count);
        activity?.SetTag("chat.embedding.input", inputTokenCount);
        activity?.SetTag("chat.embedding.output", outputTokenCount);
        activity?.SetTag("chat.embedding.total", totalTokenCount);
        activity?.SetTag("chat.response.length", assistantContentBuilder.Length);
        activity?.SetTag("chat.response.tool_call.count", functionCallContents.Count);

        return functionCallContents;
    }

    /// <summary>
    /// Invokes the functions specified in the function call contents.
    /// This will group the function calls by plugin and function, and invoke them sequentially.
    /// </summary>
    /// <param name="kernel"></param>
    /// <param name="chatContext"></param>
    /// <param name="chatHistory"></param>
    /// <param name="chatSpan"></param>
    /// <param name="functionCallContents"></param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="InvalidOperationException"></exception>
    private async Task InvokeFunctionsAsync(
        Kernel kernel,
        ChatContext chatContext,
        ChatHistory chatHistory,
        AssistantChatMessageSpan chatSpan,
        IReadOnlyList<FunctionCallContent> functionCallContents,
        CancellationToken cancellationToken)
    {
        // Group function calls by plugin name, and create ActionChatMessages for each group.
        // For example:
        // AI calls multiple functions at once:
        // {
        //   "function_calls": [
        //     { "function_name": "Function1", "parameters": { ... } },
        //     { "function_name": "Function1", "parameters": { ... } },
        //     { "function_name": "Function2", "parameters": { ... } }
        //   ]
        // }
        //
        // So we group them into:
        // - Function1
        //   - Call1
        //   - Call2
        // - Function2
        //   - Call1
        //
        // And invoke them one by one.
        // TODO: parallel invoke?
        var chatPluginScope = kernel.GetRequiredService<IChatPluginScope>();
        foreach (var functionCallContentGroup in functionCallContents.GroupBy(f => f.FunctionName))
        {
            // 1. Grouped by function name.
            // After grouping, we need to find the corresponding plugin and function.
            // For example, in the above example,
            // 1st functionCallContentGroup: Key = "Function1", Values = [Call1, Call2]
            // 2nd functionCallContentGroup: Key = "Function2", Values = [Call1]

            cancellationToken.ThrowIfCancellationRequested();

            // functionCallContentGroup.Key is the function name.
            if (!chatPluginScope.TryGetPluginAndFunction(
                    functionCallContentGroup.Key,
                    out var chatPlugin,
                    out var chatFunction,
                    out var similarFunctionNames))
            {
                // Not found the function, tell AI.

                var errorMessageBuilder = new StringBuilder();
                errorMessageBuilder.Append("Function '").Append(functionCallContentGroup.Key).Append("' is not available.");

                if (similarFunctionNames.Count > 0)
                {
                    errorMessageBuilder.Append("Did you mean: ");
                    foreach (var similarFunctionName in similarFunctionNames)
                    {
                        errorMessageBuilder.Append(' ').AppendLine(similarFunctionName);
                    }
                }

                // Display error in the chat span (UI).
                var missingFunctionMessage = new FunctionCallChatMessage(
                    LucideIconKind.X,
                    new DirectResourceKey(functionCallContentGroup.Key));
                chatSpan.AddFunctionCall(missingFunctionMessage);

                // Add call message to the chat history.
                var missingFunctionCallMessage = new ChatMessageContent(
                    AuthorRole.Assistant,
                    content: null,
                    metadata: TryCreateReasoningMetadata(chatSpan));
                missingFunctionCallMessage.Items.AddRange(functionCallContentGroup);
                chatHistory.Add(missingFunctionCallMessage);

                // Iterate through the function call contents in the group.
                // Add the error message for each function call.
                foreach (var functionCallContent in functionCallContentGroup)
                {
                    // Add the function call content to the missing function chat message for DB storage.
                    missingFunctionMessage.Calls.Add(functionCallContent);

                    // Create the corresponding function result content with the error message.
                    var missingFunctionResultContent = new FunctionResultContent(functionCallContent, errorMessageBuilder.ToString());

                    // Add the function result content to the missing function chat message for DB storage.
                    missingFunctionMessage.Results.Add(missingFunctionResultContent);

                    // Add the function result content to the chat history.
                    chatHistory.Add(new ChatMessageContent(AuthorRole.Tool, [missingFunctionResultContent]));
                }

                missingFunctionMessage.ErrorMessageKey = new FormattedDynamicResourceKey(
                    LocaleKey.HandledFunctionInvokingException_FunctionNotFound,
                    new DirectResourceKey(functionCallContentGroup.Key));

                continue;
            }

            var functionCallChatMessage = new FunctionCallChatMessage(
                chatFunction.Icon ?? chatPlugin.Icon ?? LucideIconKind.Hammer,
                chatFunction.HeaderKey)
            {
                IsBusy = true,
            };

            // Set the current function call context.
            // Push the previous context to the stack, allowing nested function calls.
            if (_currentFunctionCallContext is not null)
            {
                _functionCallContextStack.Push(_currentFunctionCallContext);
            }

            _currentFunctionCallContext = new FunctionCallContext(
                kernel,
                chatContext,
                chatPlugin,
                chatFunction,
                functionCallChatMessage);

            chatSpan.AddFunctionCall(functionCallChatMessage);

            // Add call message to the chat history.
            var functionCallMessage = new ChatMessageContent(
                AuthorRole.Assistant,
                content: null,
                metadata: TryCreateReasoningMetadata(chatSpan));
            chatHistory.Add(functionCallMessage);

            try
            {
                // Iterate through the function call contents in the group.
                foreach (var functionCallContent in functionCallContentGroup)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // This should be processed in KernelMixin.
                    // All function calls must have an ID (returned from the LLM, or generated by us).
                    if (functionCallContent.Id.IsNullOrEmpty())
                    {
                        // This should never happen.
                        throw new InvalidOperationException("Function call content must have an ID");
                    }

                    // Add the function call content to the function call chat message.
                    // This will record the function call in the database.
                    functionCallChatMessage.Calls.Add(functionCallContent);

                    // Also add a display block for the function call content.
                    // This will allow the UI to display the function call content.
                    var friendlyContent = chatFunction.GetFriendlyCallContent(functionCallContent);
                    if (friendlyContent is not null) functionCallChatMessage.DisplaySink.AppendBlock(friendlyContent);

                    // Add the function call content to the chat history.
                    // This will allow the LLM to see the function call in the chat history.
                    functionCallMessage.Items.Add(functionCallContent);

                    var resultContent = await InvokeFunctionAsync(
                        functionCallContent,
                        _currentFunctionCallContext,
                        friendlyContent,
                        cancellationToken);

                    // Try to cancel if requested immediately after function invocation (a long-time await).
                    cancellationToken.ThrowIfCancellationRequested();

                    // dd the function result content to the function call chat message.
                    // This will record the function result in the database.
                    functionCallChatMessage.Results.Add(resultContent);

                    // Add the function result content to the chat history.
                    // This will allow the LLM to see the function result in the chat history.
                    chatHistory.Add(new ChatMessageContent(AuthorRole.Tool, [resultContent]));

                    // Some functions may return attachments (e.g., images, audio, files).
                    // We need to add them to the function call chat message as well.
                    // This is a workaround to include additional tool call results that are not part of the standard function call results.
                    if (await ChatHistoryBuilder.TryCreateExtraToolCallResultsContentAsync(
                            resultContent,
                            cancellationToken) is { } extraToolCallResultsContent)
                    {
                        chatHistory.Add(extraToolCallResultsContent);
                    }

                    if (resultContent.InnerContent is Exception ex)
                    {
                        functionCallChatMessage.ErrorMessageKey = ex.GetFriendlyMessage();
                        break; // If an error occurs, we stop processing further function calls.
                    }
                }
            }
            finally
            {
                functionCallChatMessage.FinishedAt = DateTimeOffset.UtcNow;
                functionCallChatMessage.IsBusy = false;

                // Restore the previous function call context.
                if (_functionCallContextStack.Count > 0)
                {
                    _currentFunctionCallContext = _functionCallContextStack.Pop();
                }
                else
                {
                    _currentFunctionCallContext = null;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    functionCallChatMessage.ErrorMessageKey ??= new DynamicResourceKey(LocaleKey.FriendlyExceptionMessage_OperationCanceled);
                }
            }
        }
    }

    private async Task<FunctionResultContent> InvokeFunctionAsync(
        FunctionCallContent content,
        FunctionCallContext context,
        ChatPluginDisplayBlock? friendlyContent,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity();
        activity?.SetTag("tool.plugin_name", content.PluginName);
        activity?.SetTag("tool.function_name", content.FunctionName);

        FunctionResultContent resultContent;
        try
        {
            if (!IsPermissionGranted())
            {
                // The function requires permissions that are not granted.
                var promise = new TaskCompletionSource<ConsentDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

                FormattedDynamicResourceKey headerKey;
                if (context.Function.Permissions.HasFlag(ChatFunctionPermissions.MCP))
                {
                    headerKey = new FormattedDynamicResourceKey(
                        LocaleKey.ChatPluginConsentRequest_MCP_Header,
                        context.Function.HeaderKey);
                }
                else
                {
                    headerKey = new FormattedDynamicResourceKey(
                        LocaleKey.ChatPluginConsentRequest_Common_Header,
                        context.Function.HeaderKey,
                        new DirectResourceKey(context.Function.Permissions.I18N(LocaleResolver.Common_Comma, true)));
                }

                WeakReferenceMessenger.Default.Send(
                    new ChatPluginConsentRequest(
                        promise,
                        headerKey,
                        friendlyContent,
                        true,
                        cancellationToken));

                var consentDecision = await promise.Task;
                switch (consentDecision)
                {
                    case ConsentDecision.AlwaysAllow:
                    {
                        settings.Plugin.GrantedPermissions.TryGetValue(context.PermissionKey, out var grantedGlobalPermissions);
                        settings.Plugin.GrantedPermissions[context.PermissionKey] = grantedGlobalPermissions | context.Function.Permissions;
                        break;
                    }
                    case ConsentDecision.AllowSession:
                    {
                        if (!context.ChatContext.GrantedPermissions.TryGetValue(context.PermissionKey, out var grantedSessionPermissions))
                        {
                            grantedSessionPermissions = ChatFunctionPermissions.None;
                        }

                        grantedSessionPermissions |= context.Function.Permissions;
                        context.ChatContext.GrantedPermissions[context.PermissionKey] = grantedSessionPermissions;
                        break;
                    }
                    case ConsentDecision.Deny:
                    {
                        return new FunctionResultContent(content, "Error: Function execution denied by user.");
                    }
                }
            }

            resultContent = await content.InvokeAsync(context.Kernel, cancellationToken);

            bool IsPermissionGranted()
            {
                var requiredPermissions = context.Function.Permissions;
                if (requiredPermissions < ChatFunctionPermissions.FileAccess) return true;

                var grantedPermissions = ChatFunctionPermissions.None;
                if (settings.Plugin.GrantedPermissions.TryGetValue(context.PermissionKey, out var grantedGlobalPermissions))
                {
                    grantedPermissions |= grantedGlobalPermissions;
                }
                if (context.ChatContext.GrantedPermissions.TryGetValue(context.PermissionKey, out var grantedSessionPermissions))
                {
                    grantedPermissions |= grantedSessionPermissions;
                }

                return (grantedPermissions & requiredPermissions) == requiredPermissions;
            }
        }
        catch (Exception ex)
        {
            ex = HandledFunctionInvokingException.Handle(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "Error invoking function '{FunctionName}'", content.FunctionName);

            resultContent = new FunctionResultContent(content, $"Error: {ex.Message}") { InnerContent = ex };
        }

        return resultContent;
    }

    /// <summary>
    /// Creates reasoning metadata if the reasoning content is not null or empty.
    /// </summary>
    /// <param name="span"></param>
    /// <returns></returns>
    private static Dictionary<string, object?>? TryCreateReasoningMetadata(AssistantChatMessageSpan span) =>
        span.ReasoningOutput.IsNullOrEmpty() ? null : new Dictionary<string, object?> { { "reasoning_content", span.ReasoningOutput } };

    private async Task GenerateTitleAsync(
        IKernelMixin kernelMixin,
        string userMessage,
        string assistantMessage,
        ChatContextMetadata metadata,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity();

        try
        {
            var language = settings.Common.Language.ToEnglishName();

            activity?.SetTag("chat.context.id", metadata.Id);
            activity?.SetTag("user_message.length", userMessage.Length);
            activity?.SetTag("assistant_message.length", assistantMessage.Length);
            activity?.SetTag("system_language", language);

            var chatHistory = new ChatHistory
            {
                new ChatMessageContent(
                    AuthorRole.System,
                    Prompts.TitleGeneratorSystemPrompt),
                new ChatMessageContent(
                    AuthorRole.User,
                    Prompts.RenderPrompt(
                        Prompts.TitleGeneratorUserPrompt,
                        new Dictionary<string, Func<string>>
                        {
                            { "UserMessage", () => userMessage.SafeSubstring(0, 2048) },
                            { "AssistantMessage", () => assistantMessage.SafeSubstring(0, 2048) },
                            { "SystemLanguage", () => language }
                        })),
            };
            var chatMessageContent = await kernelMixin.ChatCompletionService.GetChatMessageContentAsync(
                chatHistory,
                kernelMixin.GetPromptExecutionSettings(),
                cancellationToken: cancellationToken);

            Span<char> punctuationChars = ['.', ',', '!', '?', '。', '，', '！', '？'];
            metadata.Topic = chatMessageContent.Content?.Trim().Trim(punctuationChars).Trim().SafeSlice(0, 50).ToString();

            activity?.SetTag("topic.length", metadata.Topic?.Length ?? 0);
        }
        catch (Exception e)
        {
            e = HandledChatException.Handle(e);
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            logger.LogError(e, "Failed to generate chat title");
        }
    }

    public IChatPluginDisplaySink DisplaySink =>
        _currentFunctionCallContext?.ChatMessage.DisplaySink ?? throw new InvalidOperationException("No active function call to display sink for");

    public async Task<bool> RequestConsentAsync(
        string? id,
        DynamicResourceKeyBase headerKey,
        ChatPluginDisplayBlock? content = null,
        CancellationToken cancellationToken = default)
    {
        if (_currentFunctionCallContext is null)
        {
            throw new InvalidOperationException("No active function call to request consent for");
        }

        string? permissionKey = null;
        if (!id.IsNullOrWhiteSpace())
        {
            // Check if the permission is already granted
            var grantedPermissions = ChatFunctionPermissions.None;
            permissionKey = $"{_currentFunctionCallContext.PermissionKey}.{id}";
            if (settings.Plugin.GrantedPermissions.TryGetValue(permissionKey, out var extra))
            {
                grantedPermissions |= extra;
            }
            if (_currentFunctionCallContext.ChatContext.GrantedPermissions.TryGetValue(permissionKey, out var session))
            {
                grantedPermissions |= session;
            }
            if ((grantedPermissions & _currentFunctionCallContext.Function.Permissions) == _currentFunctionCallContext.Function.Permissions)
            {
                return true;
            }
        }

        var promise = new TaskCompletionSource<ConsentDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        WeakReferenceMessenger.Default.Send(
            new ChatPluginConsentRequest(
                promise,
                headerKey,
                content,
                permissionKey is not null,
                cancellationToken));

        var consentDecision = await promise.Task;

        if (permissionKey is null)
        {
            // no id provided, so we cannot remember the decision
            return consentDecision switch
            {
                ConsentDecision.AllowOnce => true,
                _ => false,
            };
        }

        switch (consentDecision)
        {
            case ConsentDecision.AlwaysAllow:
            {
                settings.Plugin.GrantedPermissions.TryGetValue(permissionKey, out var grantedGlobalPermissions);
                settings.Plugin.GrantedPermissions[permissionKey] = grantedGlobalPermissions | _currentFunctionCallContext.Function.Permissions;
                return true;
            }
            case ConsentDecision.AllowSession:
            {
                if (!_currentFunctionCallContext.ChatContext.GrantedPermissions.TryGetValue(permissionKey, out var grantedSessionPermissions))
                {
                    grantedSessionPermissions = ChatFunctionPermissions.None;
                }

                grantedSessionPermissions |= _currentFunctionCallContext.Function.Permissions;
                _currentFunctionCallContext.ChatContext.GrantedPermissions[permissionKey] = grantedSessionPermissions;
                return true;
            }
            case ConsentDecision.AllowOnce:
            {
                return true;
            }
            case ConsentDecision.Deny:
            default:
            {
                return false;
            }
        }
    }

    public Task<string> RequestInputAsync(DynamicResourceKeyBase message, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
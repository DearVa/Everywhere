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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
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
        using var activity = StartChatActivity("chat", customAssistant);
        activity?.SetTag("id", chatContext.Metadata.Id);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var kernelMixin = CreateKernelMixin(customAssistant);
            var kernel = await BuildKernelAsync(kernelMixin, chatContext, customAssistant, cancellationToken);

            // Because the custom assistant maybe changed, we need to re-render the system prompt.
            chatContextManager.PopulateSystemPrompt(chatContext, customAssistant.SystemPrompt);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Build the chat history for the current generation.
                var chatHistory = await ChatHistoryBuilder.BuildChatHistoryAsync(
                    chatContext
                        .Items
                        .AsValueEnumerable()
                        .Select(n => n.Message)
                        .Where(m => m.Role.Label is "system" or "assistant" or "user" or "tool")
                        .ToList(),
                    cancellationToken);

                if (!chatContext.Metadata.IsTemporary && // Do not generate titles for temporary contexts.
                    chatContext.Metadata.Topic.IsNullOrEmpty() &&
                    chatHistory.Count(c => c.Role == AuthorRole.User) == 1 && // Only try when there's one user message.
                    chatHistory.FirstOrDefault(c => c.Role == AuthorRole.User)?.Content is { Length: > 0 } userMessage)
                {
                    // If the chat history only contains one user message and one assistant message,
                    // we can generate a title for the chat context.
                    GenerateTitleAsync(
                        customAssistant,
                        kernelMixin,
                        userMessage,
                        chatContext.Metadata,
                        cancellationToken).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
                }

                // Process streaming chat message contents (thinking, text, function calls, etc.)
                // It will return the function call contents for further processing.
                var functionCallContents = await GetStreamingChatMessageContentsAsync(
                    customAssistant,
                    kernel,
                    kernelMixin,
                    chatContext,
                    chatHistory,
                    assistantChatMessage,
                    cancellationToken);
                if (functionCallContents.Count <= 0) break; // No more function calls, exit the loop.

                // Invoke the functions specified in the function call contents.
                await InvokeFunctionsAsync(
                    customAssistant,
                    kernel,
                    chatContext,
                    assistantChatMessage,
                    functionCallContents,
                    cancellationToken);
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
            SetChatUsageTags(activity, assistantChatMessage.UsageDetails);
            assistantChatMessage.FinishedAt = DateTimeOffset.UtcNow;
            assistantChatMessage.IsBusy = false;
        }
    }

    /// <summary>
    /// Gets streaming chat message contents from the chat completion service.
    /// </summary>
    /// <param name="customAssistant"></param>
    /// <param name="kernel"></param>
    /// <param name="kernelMixin"></param>
    /// <param name="chatContext"></param>
    /// <param name="chatHistory"></param>
    /// <param name="assistantChatMessage"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<IReadOnlyList<FunctionCallContent>> GetStreamingChatMessageContentsAsync(
        CustomAssistant customAssistant,
        Kernel kernel,
        IKernelMixin kernelMixin,
        ChatContext chatContext,
        ChatHistory chatHistory,
        AssistantChatMessage assistantChatMessage,
        CancellationToken cancellationToken)
    {
        using var activity = StartChatActivity("invoke_agent", customAssistant);
        activity?.SetTag("gen_ai.messages.count", chatHistory.Count);

        AuthorRole? authorRole = null;
        IDisposable? callingToolsBusyMessage = null;
        AssistantChatMessageSpan? span = null;

        var usage = new ChatUsageDetails(); // Each generation has its own usage details.
        var functionCallContentBuilder = new FunctionCallContentBuilder();
        var promptExecutionSettings = kernelMixin.GetPromptExecutionSettings(
            kernelMixin.IsFunctionCallingSupported && persistentState.IsToolCallEnabled ?
                FunctionChoiceBehavior.Auto(autoInvoke: false) :
                null);

        try
        {
            await foreach (var streamingContent in kernelMixin.ChatCompletionService.GetStreamingChatMessageContentsAsync(
                               chatHistory,
                               promptExecutionSettings,
                               kernel,
                               cancellationToken))
            {
                usage.Update(streamingContent);

                // Add persistent message-level metadata to the assistant chat message.
                if (streamingContent.Metadata is not null)
                {
                    foreach (var (key, value) in streamingContent.Metadata
                                 .AsValueEnumerable()
                                 .Where(kv => kernelMixin.IsPersistentMessageMetadataKey(kv.Key)))
                    {
                        assistantChatMessage.Metadata ??= new MetadataDictionary();
                        assistantChatMessage.Metadata[key] = value;
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
                        EnsureSpan<AssistantChatMessageImageSpan>(true).ImageOutput = new ChatFileAttachment(
                            new DynamicResourceKey(string.Empty),
                            blob.LocalPath,
                            blob.Sha256,
                            blob.MimeType);
                    }

                    if (item.Metadata is not null && span is not null)
                    {
                        foreach (var (key, value) in item.Metadata
                                     .AsValueEnumerable()
                                     .Where(kv => kernelMixin.IsPersistentSpanMetadataKey(kv.Key)))
                        {
                            span.Metadata ??= new MetadataDictionary();
                            span.Metadata[key] = value;
                        }
                    }

                    bool IsReasoningContent(StreamingKernelContent content) =>
                        streamingContent.Metadata?.TryGetValue("reasoning", out var reasoning) is true && reasoning is true ||
                        content.Metadata?.TryGetValue("reasoning", out reasoning) is true && reasoning is true;

                    DispatcherOperation<ObservableStringBuilder> HandleTextMessageAsync(string text) => Dispatcher.UIThread.InvokeAsync(() =>
                        EnsureSpan<AssistantChatMessageTextSpan>(false).ContentMarkdownBuilder.Append(text));

                    DispatcherOperation<ObservableStringBuilder> HandleReasoningMessageAsync(string text) => Dispatcher.UIThread.InvokeAsync(() =>
                        EnsureSpan<AssistantChatMessageReasoningSpan>(false).ReasoningMarkdownBuilder.Append(text));
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
            assistantChatMessage.UsageDetails.Accumulate(usage); // Accumulate usage details.
            SetChatUsageTags(activity, usage);

            span?.FinishedAt ??= DateTimeOffset.UtcNow;
            callingToolsBusyMessage?.Dispose();
        }

        var functionCallContents = functionCallContentBuilder.Build();
        activity?.SetTag("gen_ai.tool.count", functionCallContents.Count);
        return functionCallContents;

        TSpan EnsureSpan<TSpan>(bool createNew) where TSpan : AssistantChatMessageSpan, new()
        {
            // Handle existing span.
            if (span is not null)
            {
                // If the existing span is of the requested type and we don't need to create a new one, return it.
                if (!createNew && span is TSpan existingSpan)
                {
                    return existingSpan;
                }

                // Finish the existing span.
                span.FinishedAt = DateTimeOffset.UtcNow;
            }

            // Create a new span of the requested type.
            TSpan newSpan;
            span = newSpan = new TSpan();
            assistantChatMessage.AddSpan(span);
            return newSpan;
        }
    }

    /// <summary>
    /// Invokes the functions specified in the function call contents.
    /// This will group the function calls by plugin and function, and invoke them sequentially.
    /// </summary>
    /// <param name="customAssistant"></param>
    /// <param name="kernel"></param>
    /// <param name="chatContext"></param>
    /// <param name="assistantChatMessage"></param>
    /// <param name="functionCallContents"></param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="InvalidOperationException"></exception>
    private async Task InvokeFunctionsAsync(
        CustomAssistant customAssistant,
        Kernel kernel,
        ChatContext chatContext,
        AssistantChatMessage assistantChatMessage,
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
        var functionCallSpan = new AssistantChatMessageFunctionCallSpan();
        assistantChatMessage.AddSpan(functionCallSpan);

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
                functionCallSpan.Add(missingFunctionMessage);

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
            functionCallSpan.Add(functionCallChatMessage);

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

                    var resultContent = await InvokeFunctionAsync(
                        customAssistant,
                        functionCallContent,
                        _currentFunctionCallContext,
                        friendlyContent,
                        cancellationToken);

                    // Try to cancel if requested immediately after function invocation (a long-time await).
                    cancellationToken.ThrowIfCancellationRequested();

                    // dd the function result content to the function call chat message.
                    // This will record the function result in the database.
                    functionCallChatMessage.Results.Add(resultContent);

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
        CustomAssistant customAssistant,
        FunctionCallContent content,
        FunctionCallContext context,
        ChatPluginDisplayBlock? friendlyContent,
        CancellationToken cancellationToken)
    {
        using var activity = StartChatActivity("execute_tool", customAssistant);
        activity?.SetTag("gen_ai.tool.plugin", content.PluginName);
        activity?.SetTag("gen_ai.tool.name", content.FunctionName);
        activity?.SetTag("gen_ai.tool.input", content.Arguments?.ToString());

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

    private async Task GenerateTitleAsync(
        CustomAssistant customAssistant,
        IKernelMixin kernelMixin,
        string userMessage,
        ChatContextMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (metadata.IsGeneratingTopic) return;
        metadata.IsGeneratingTopic = true;

        using var activity = StartChatActivity("invoke_agent", customAssistant);

        try
        {
            var language = settings.Common.Language.ToEnglishName();
            activity?.SetTag("id", metadata.Id);
            activity?.SetTag("user_message.length", userMessage.Length);
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
                            { "SystemLanguage", () => language }
                        })),
            };
            var chatMessageContent = await kernelMixin.ChatCompletionService.GetChatMessageContentAsync(
                chatHistory,
                kernelMixin.GetPromptExecutionSettings(reasoningEffortLevel: ReasoningEffortLevel.Minimal),
                cancellationToken: cancellationToken);

            var usage = new ChatUsageDetails();
            usage.Update(chatMessageContent);
            SetChatUsageTags(activity, usage);

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
        finally
        {
            metadata.IsGeneratingTopic = false;
        }
    }

    /// <summary>
    /// Starts a chat activity for telemetry.
    /// </summary>
    /// <param name="operationName"></param>
    /// <param name="customAssistant"></param>
    /// <param name="displayName"></param>
    /// <returns></returns>
    private Activity? StartChatActivity(string operationName, CustomAssistant? customAssistant, [CallerMemberName] string displayName = "")
    {
        IEnumerable<KeyValuePair<string, object?>> tags = customAssistant is null ?
            [
                new KeyValuePair<string, object?>("gen_ai.operation.name", operationName)
            ] :
            [
                new KeyValuePair<string, object?>("gen_ai.operation.name", operationName),
                new KeyValuePair<string, object?>("gen_ai.request.model", customAssistant.ModelId),
                new KeyValuePair<string, object?>("gen_ai.request.supports_image", customAssistant.IsImageInputSupported),
                new KeyValuePair<string, object?>("gen_ai.request.supports_tool", customAssistant.IsFunctionCallingSupported),
                new KeyValuePair<string, object?>("gen_ai.request.supports_reasoning", customAssistant.IsDeepThinkingSupported),
                new KeyValuePair<string, object?>("gen_ai.request.max_tokens", customAssistant.MaxTokens),
                new KeyValuePair<string, object?>("gen_ai.request.temperature", customAssistant.Temperature),
                new KeyValuePair<string, object?>("gen_ai.request.top_p", customAssistant.TopP)
            ];
        return _activitySource.StartActivity(
            $"gen_ai.{operationName}",
            ActivityKind.Client,
            null,
            tags).With(x => x?.DisplayName = displayName);
    }

    /// <summary>
    /// Sets chat usage tags to the activity.
    /// </summary>
    /// <param name="activity"></param>
    /// <param name="usage"></param>
    private static void SetChatUsageTags(Activity? activity, ChatUsageDetails usage)
    {
        activity?.SetTag("gen_ai.usage.input_tokens", usage.InputTokenCount);
        activity?.SetTag("gen_ai.usage.cached_input_tokens", usage.CachedInputTokenCount);
        activity?.SetTag("gen_ai.usage.output_tokens", usage.OutputTokenCount);
        activity?.SetTag("gen_ai.usage.reasoning_tokens", usage.ReasoningTokenCount);
        activity?.SetTag("gen_ai.usage.total_tokens", usage.TotalTokenCount);
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
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Anthropic.SDK.Messaging;
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
using Serilog;
using ZLinq;
using ChatFunction = Everywhere.Chat.Plugins.ChatFunction;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;
using FunctionCallContent = Microsoft.SemanticKernel.FunctionCallContent;
using FunctionResultContent = Microsoft.SemanticKernel.FunctionResultContent;
using ImageContent = Microsoft.SemanticKernel.ImageContent;
using TextContent = Microsoft.SemanticKernel.TextContent;

namespace Everywhere.Chat;

public class ChatService(
    IChatContextManager chatContextManager,
    IChatPluginManager chatPluginManager,
    IKernelMixinFactory kernelMixinFactory,
    Settings settings,
    ILogger<ChatService> logger
) : IChatService, IChatPluginUserInterface
{
    /// <summary>
    /// Context for function call invocations.
    /// </summary>
    protected record FunctionCallContext(
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

        await Task.Run(() => GenerateAsync(chatContext, customAssistant, assistantChatMessage, cancellationToken), cancellationToken);
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

        await Task.Run(() => GenerateAsync(node.Context, customAssistant, assistantChatMessage, cancellationToken), cancellationToken);
    }

    public Task EditAsync(ChatMessageNode node, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    private async Task ProcessUserChatMessageAsync(
        ChatContext chatContext,
        CustomAssistant customAssistant,
        UserChatMessage userChatMessage,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity();
        activity?.SetTag("chat.context.id", chatContext.Metadata.Id);

        var attachmentTagValues = activity is null ? null : new List<object>();
        var validVisualElements = new List<IVisualElement>();
        foreach (var attachment in userChatMessage.Attachments.AsValueEnumerable().Take(10))
        {
            switch (attachment)
            {
                case ChatTextAttachment textAttachment:
                {
                    attachmentTagValues?.Add(
                        new
                        {
                            type = "text",
                            length = textAttachment.Text.Length
                        });
                    break;
                }
                case ChatFileAttachment fileAttachment:
                {
                    attachmentTagValues?.Add(
                        new
                        {
                            type = "file",
                            mime_type = fileAttachment.MimeType,
                            file_size = GetFileSize(fileAttachment.FilePath)
                        });
                    break;
                }
                case ChatVisualElementAttachment visualElement:
                {
                    var element = visualElement.Element?.Target;
                    if (element is not null) validVisualElements.Add(element);

                    attachmentTagValues?.Add(
                        new
                        {
                            type = "element",
                            element_type = element?.Type.ToString() ?? "invalid",
                        });

                    break;
                }
            }
        }

        activity?.SetTag("attachments", JsonSerializer.Serialize(attachmentTagValues));

        if (validVisualElements.Count > 0)
        {
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

                var maxTokens = customAssistant.MaxTokens.ActualValue;
                var approximateTokenLimit = Math.Min(settings.Internal.VisualTreeTokenLimit, maxTokens / 2);
                var detailLevel = settings.ChatWindow.VisualTreeDetailLevel;
                var xmlBuilder = new VisualTreeXmlBuilder(
                    validVisualElements,
                    approximateTokenLimit,
                    chatContext.VisualElements.Count + 1,
                    detailLevel);
                var renderedVisualTreePrompt = await Task.Run(
                    () =>
                    {
                        // ReSharper disable once ExplicitCallerInfoArgument
                        using var builderActivity = _activitySource.StartActivity("BuildVisualTreeXml");

                        var xml = xmlBuilder.BuildXml(cancellationToken);
                        var builtVisualElements = xmlBuilder.BuiltVisualElements;
                        builderActivity?.SetTag("xml.length", xml.Length);
                        builderActivity?.SetTag("xml.detail_level", detailLevel);
                        builderActivity?.SetTag("xml.length_limit", approximateTokenLimit);
                        builderActivity?.SetTag("xml.built_visual_elements.count", builtVisualElements.Count);

                        string focusedElementIdString;
                        if (builtVisualElements.FirstOrDefault(kv => ReferenceEquals(kv.Value, validVisualElements[0])) is
                            { Key: var focusedElementId, Value: not null })
                        {
                            focusedElementIdString = focusedElementId.ToString();
                        }
                        else
                        {
                            focusedElementIdString = "null";
                        }

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

                        return Prompts.RenderPrompt(
                            Prompts.VisualTreePrompt,
                            new Dictionary<string, Func<string>>
                            {
                                { "VisualTree", () => xml },
                                { "FocusedElementId", () => focusedElementIdString },
                            });
                    },
                    cancellationToken);
                userChatMessage.UserPrompt = renderedVisualTreePrompt + "\n\n" + userChatMessage.UserPrompt;
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

        // Append tool use prompt if enabled.
        // if (settings.Internal.IsToolCallEnabled)
        // {
        //     userChatMessage.UserPrompt += Prompts.TryUseToolUserPrompt;
        // }

        static long GetFileSize(string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                return fileInfo.Exists ? fileInfo.Length : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    private IKernelMixin CreateKernelMixin(CustomAssistant customAssistant)
    {
        using var activity = _activitySource.StartActivity();

        try
        {
            var kernelMixin = kernelMixinFactory.GetOrCreate(customAssistant);
            activity?.SetTag("llm.provider.id", customAssistant.ModelProviderTemplateId);
            activity?.SetTag("llm.model.id", customAssistant.ModelDefinitionTemplateId);
            activity?.SetTag("llm.model.actual_id", customAssistant.ModelId.ActualValue);
            activity?.SetTag("llm.model.max_embedding", customAssistant.MaxTokens.ActualValue);
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
    private Kernel BuildKernel(IKernelMixin kernelMixin, ChatContext chatContext, CustomAssistant customAssistant)
    {
        using var activity = _activitySource.StartActivity();

        var builder = Kernel.CreateBuilder();

        builder.Services.AddSingleton(kernelMixin.ChatCompletionService);
        builder.Services.AddSingleton(chatContext);
        builder.Services.AddSingleton<IChatPluginUserInterface>(this);

        if (kernelMixin.IsFunctionCallingSupported && settings.Internal.IsToolCallEnabled)
        {
            var chatPluginScope = chatPluginManager.CreateScope(chatContext, customAssistant);
            builder.Services.AddSingleton(chatPluginScope);
            activity?.SetTag("plugins.count", chatPluginScope.Plugins.AsValueEnumerable().Count());

            foreach (var plugin in chatPluginScope.Plugins)
            {
                builder.Plugins.Add(plugin);
            }
        }

        return builder.Build();
    }

    private async Task GenerateAsync(
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
            var kernel = BuildKernel(kernelMixin, chatContext, customAssistant);

            var chatHistory = new ChatHistory();
            // Because the custom assistant maybe changed, we need to re-render the system prompt.
            chatContext.SystemPrompt = Prompts.RenderPrompt(customAssistant.SystemPrompt.ActualValue, chatContextManager.SystemPromptVariables);

            // Build the chat history from the chat context.
            foreach (var chatMessage in chatContext
                         .Select(n => n.Message)
                         .Where(m => !ReferenceEquals(m, assistantChatMessage)) // exclude the current assistant message
                         .Where(m => m.Role.Label is "system" or "assistant" or "user" or "tool")
                         .ToList()) // make a snapshot, otherwise async may cause thread deadlock
            {
                await foreach (var chatMessageContent in CreateChatMessageContentsAsync(chatMessage, cancellationToken))
                {
                    chatHistory.Add(chatMessageContent);
                }
            }

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
    /// <param name="customAssistant"></param>
    /// <param name="chatHistory"></param>
    /// <param name="chatSpan"></param>
    /// <param name="assistantChatMessage"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<IReadOnlyList<FunctionCallContent>> GetStreamingChatMessageContentsAsync(
        Kernel kernel,
        IKernelMixin kernelMixin,
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
            kernelMixin.IsFunctionCallingSupported && settings.Internal.IsToolCallEnabled ?
                FunctionChoiceBehavior.Auto(autoInvoke: false) :
                null);

        activity?.SetTag("llm.provider.id", customAssistant.ModelProviderTemplateId);
        activity?.SetTag("llm.model.id", customAssistant.ModelDefinitionTemplateId);
        activity?.SetTag("llm.model.actual_id", customAssistant.ModelId.ActualValue);
        activity?.SetTag("llm.model.max_embedding", customAssistant.MaxTokens.ActualValue);

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
                    case Usage anthropicUsage:
                    {
                        inputTokenCount = Math.Max(inputTokenCount, anthropicUsage.InputTokens);
                        outputTokenCount = Math.Max(outputTokenCount, anthropicUsage.OutputTokens);
                        totalTokenCount = Math.Max(totalTokenCount, anthropicUsage.InputTokens + anthropicUsage.OutputTokens);
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
                        if (IsReasoningContent(chatMessageContent))
                        {
                            HandleReasoningMessage(chatMessageContent.Content);
                        }
                        else
                        {
                            await HandleTextMessage(chatMessageContent.Content);
                        }
                        break;
                    }
                    case StreamingTextContent { Text.Length: > 0 } textContent:
                    {
                        if (IsReasoningContent(textContent))
                        {
                            HandleReasoningMessage(textContent.Text);
                        }
                        else
                        {
                            await HandleTextMessage(textContent.Text);
                        }
                        break;
                    }
                    case StreamingReasoningContent reasoningContent:
                    {
                        HandleReasoningMessage(reasoningContent.Text);
                        break;
                    }
                }

                bool IsReasoningContent(StreamingKernelContent content) =>
                    streamingContent.Metadata?.TryGetValue("reasoning", out var reasoning) is true && reasoning is true ||
                    content.Metadata?.TryGetValue("reasoning", out reasoning) is true && reasoning is true;

                DispatcherOperation<ObservableStringBuilder> HandleTextMessage(string text)
                {
                    // Mark the reasoning as finished when we receive the first content chunk.
                    if (chatSpan.ReasoningOutput is not null && chatSpan.ReasoningFinishedAt is null)
                    {
                        chatSpan.ReasoningOutput = chatSpan.ReasoningOutput.TrimEnd();
                        chatSpan.ReasoningFinishedAt = DateTimeOffset.UtcNow;
                    }

                    assistantContentBuilder.Append(text);
                    return Dispatcher.UIThread.InvokeAsync(() => chatSpan.MarkdownBuilder.Append(text));
                }

                void HandleReasoningMessage(string text)
                {
                    if (chatSpan.ReasoningOutput is null) chatSpan.ReasoningOutput = text;
                    else chatSpan.ReasoningOutput += text;
                }
            }

            authorRole ??= streamingContent.Role;
            functionCallContentBuilder.Append(streamingContent);
        }

        // Mark the reasoning as finished if we have any reasoning output.
        if (chatSpan.ReasoningOutput is not null && chatSpan.ReasoningFinishedAt is null)
        {
            chatSpan.ReasoningFinishedAt = DateTimeOffset.UtcNow;
        }

        // Finally, add the assistant message to the chat history.
        if (assistantContentBuilder.Length > 0) chatHistory.AddAssistantMessage(assistantContentBuilder.ToString());

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
        var chatPluginScope = kernel.GetRequiredService<IChatPluginScope>();
        foreach (var functionCallContentGroup in functionCallContents.GroupBy(f => f.FunctionName))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!chatPluginScope.TryGetPluginAndFunction(
                    functionCallContentGroup.Key,
                    out var chatPlugin,
                    out var chatFunction))
            {
                throw new InvalidOperationException($"Function '{functionCallContentGroup.Key}' is not available");
            }

            var functionCallChatMessage = new FunctionCallChatMessage(
                chatFunction.Icon ?? chatPlugin.Icon ?? LucideIconKind.Hammer,
                chatFunction.HeaderKey)
            {
                IsBusy = true,
            };
            _currentFunctionCallContext = new FunctionCallContext(
                kernel,
                chatContext,
                chatPlugin,
                chatFunction,
                functionCallChatMessage);

            chatSpan.AddFunctionCall(functionCallChatMessage);

            // Add call message to the chat history.
            var functionCallMessage = new ChatMessageContent(AuthorRole.Assistant, content: null);
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
                    if (await TryCreateExtraToolCallResultsContentAsync(resultContent, cancellationToken) is { } extraToolCallResultsContent)
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
                _currentFunctionCallContext = null;

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
                WeakReferenceMessenger.Default.Send(
                    new ChatPluginConsentRequest(
                        promise,
                        new FormattedDynamicResourceKey(
                            LocaleKey.ChatPluginConsentRequest_Common_Header,
                            context.Function.HeaderKey,
                            new DirectResourceKey(context.Function.Permissions.I18N(LocaleKey.Common_Comma.I18N(), true))),
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
            ex = HandledSystemException.Handle(ex, true); // treat all as expected
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "Error invoking function '{FunctionName}'", content.FunctionName);

            resultContent = new FunctionResultContent(content, $"Error: {ex.Message}") { InnerContent = ex };
        }

        return resultContent;
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
                yield return new ChatMessageContent(chatMessage.Role, system.SystemPrompt);
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
                        yield return new ChatMessageContent(chatMessage.Role, span.MarkdownBuilder.ToString());
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
                var content = new ChatMessageContent(chatMessage.Role, user.UserPrompt);

                // snapshot the attachments to avoid thread issues.
                foreach (var chatAttachment in user.Attachments.ToList())
                {
                    await AddAttachmentToChatMessageContentAsync(chatAttachment, content, cancellationToken);
                }

                yield return content;
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

                    var result = functionCall.Results.AsValueEnumerable().FirstOrDefault(r => r.CallId == callId);
                    yield return result?.ToChatMessage() ?? new ChatMessageContent(
                        AuthorRole.Tool,
                        [
                            new FunctionResultContent(
                                functionCall.Calls[callIndex],
                                $"Error: No result found for function call ID '{callId}'. " +
                                $"This may caused by an error during function execution or user cancellation.")
                        ]);

                    if (result is not null &&
                        await TryCreateExtraToolCallResultsContentAsync(result, cancellationToken) is { } extraToolCallResultsContent)
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
    /// Creates extra tool call results content if there are any attachments in the function call chat message.
    /// This is a workaround to include additional tool call results that are not part of the standard function call results. e.g. images, audio, etc.
    /// </summary>
    /// <param name="functionResultContent"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private static async ValueTask<ChatMessageContent?> TryCreateExtraToolCallResultsContentAsync(
        FunctionResultContent functionResultContent,
        CancellationToken cancellationToken)
    {
        if (functionResultContent.Result is not ChatAttachment chatAttachment) return null;

        var content = new ChatMessageContent(AuthorRole.User, "Extra tool call results in order");
        await AddAttachmentToChatMessageContentAsync(chatAttachment, content, cancellationToken);
        return content;
    }

    /// <summary>
    /// Adds attachment to the chat message content. This method supports up to 10 attachments and will load file attachments from disk.
    /// </summary>
    /// <param name="attachment"></param>
    /// <param name="content"></param>
    /// <param name="cancellationToken"></param>
    private static async ValueTask AddAttachmentToChatMessageContentAsync(
        ChatAttachment attachment,
        ChatMessageContent content,
        CancellationToken cancellationToken)
    {
        switch (attachment)
        {
            case ChatTextAttachment text:
            {
                content.Items.Add(new TextContent(text.Text));
                break;
            }
            case ChatFileAttachment file:
            {
                byte[] data;

                var fileInfo = new FileInfo(file.FilePath);
                if (!fileInfo.Exists || fileInfo.Length <= 0 || fileInfo.Length > 25 * 1024 * 1024) // TODO: Configurable max file size?
                {
                    return;
                }

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
                    Log.ForContext<ChatService>().Warning(ex, "Failed to read attachment file '{FilePath}'", file.FilePath);
                    return;
                }

                if (MimeTypeUtilities.IsAudio(file.MimeType))
                {
                    content.Items.Add(new AudioContent(data, file.MimeType));
                }
                else if (MimeTypeUtilities.IsImage(file.MimeType))
                {
                    content.Items.Add(new ImageContent(data, file.MimeType));
                }
                else
                {
                    content.Items.Add(new BinaryContent(data, file.MimeType));
                }

                break;
            }
        }
    }

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
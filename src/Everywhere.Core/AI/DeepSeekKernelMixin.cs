using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ZLinq;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="IKernelMixin"/> for DeepSeek models via Chat Completions.
/// It extends <see cref="OpenAIKernelMixin"/> to add custom behavior for DeepSeek (e.g., adding reasoning content).
/// </summary>
/// <param name="customAssistant"></param>
/// <param name="httpClient"></param>
/// <param name="loggerFactory"></param>
public class DeepSeekKernelMixin(
    CustomAssistant customAssistant,
    HttpClient httpClient,
    ILoggerFactory loggerFactory
) : OpenAIKernelMixin(
    customAssistant,
    httpClient,
    loggerFactory
)
{
    /// <summary>
    /// Apply reasoning content patch before sending the streaming chat request.
    /// </summary>
    /// <remarks>
    /// As the official documentation states, we need set the "reasoningContent" field in the assistant messages after **last** user message.
    /// https://api-docs.deepseek.com/zh-cn/guides/thinking_mode
    /// </remarks>
    /// <param name="messages"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    protected override Task BeforeStreamingRequestAsync(IList<ChatMessage> messages, ref ChatOptions? options)
    {
        if (!_customAssistant.IsDeepThinkingSupported) return Task.CompletedTask;

        options ??= new ChatOptions();
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.AdditionalProperties["thinking"] = new { type = "enabled" };

        var lastUserMessageIndex = messages.AsValueEnumerable()
            .Select((m, i) => new { Message = m, Index = i })
            .Where(x => x.Message.Role == ChatRole.User)
            .Select(x => x.Index)
            .LastOrDefault(-1);
        if (lastUserMessageIndex == -1 || lastUserMessageIndex == messages.Count - 1) return Task.CompletedTask;

        foreach (var assistantMessage in messages.AsValueEnumerable().Skip(lastUserMessageIndex + 1).Where(m => m.Role == ChatRole.Assistant))
        {
            if (assistantMessage.RawRepresentation is not OpenAI.Chat.ChatMessage chatMessage ||
                assistantMessage.AdditionalProperties?.TryGetValue("reasoning_content", out var reasoningObj) is not true ||
                reasoningObj is not string { Length: > 0 } reasoningContent) continue;

            var patch = new JsonPatch();
            patch.Set("$.reasoning_content"u8.ToArray(), reasoningContent);
            chatMessage.Patch = patch;
        }

        return Task.CompletedTask;
    }
}
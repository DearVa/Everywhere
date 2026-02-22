using Everywhere.AI;

namespace Everywhere.Chat;

public interface IChatService
{
    /// <summary>
    /// Send a message to the chat service.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task SendMessageAsync(UserChatMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Retry sending a message that previously failed. This will create a branch in the chat history.
    /// </summary>
    /// <param name="node"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task RetryAsync(ChatMessageNode node, CancellationToken cancellationToken);

    /// <summary>
    /// Edit a previously sent user message. This will create a branch in the chat history.
    /// </summary>
    /// <param name="originalNode"></param>
    /// <param name="newMessage"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task EditAsync(ChatMessageNode originalNode, UserChatMessage newMessage, CancellationToken cancellationToken);

    /// <summary>
    /// Run a sub-agent within the context of the current chat. The sub-agent will have access to the chat history and can send messages back to the main agent.
    /// </summary>
    /// <param name="chatContext"></param>
    /// <param name="customAssistant"></param>
    /// <param name="assistantChatMessage"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task RunSubagentAsync(
        ChatContext chatContext,
        CustomAssistant customAssistant,
        AssistantChatMessage assistantChatMessage,
        CancellationToken cancellationToken);
}
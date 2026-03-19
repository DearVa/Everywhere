using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

/// <summary>
/// Builds a round-based chat history window for LLM calls.
/// </summary>
public static class ChatHistoryRoundWindowBuilder
{
    public readonly record struct BuildResult(ChatHistory ChatHistory, int StartIndex);

    public static BuildResult Build(ChatHistory fullChatHistory, int maxContextRounds, int? fixedStartIndex = null)
    {
        if (fullChatHistory.Count <= 0)
        {
            return new BuildResult(fullChatHistory, 0);
        }

        if (maxContextRounds < -1)
        {
            maxContextRounds = -1;
        }

        if (fixedStartIndex is { } fixedIndex)
        {
            var clampedFixedIndex = Math.Clamp(fixedIndex, 0, fullChatHistory.Count - 1);
            return new BuildResult(CreateHistoryWithSystemPromptPreserved(fullChatHistory, clampedFixedIndex), clampedFixedIndex);
        }

        if (maxContextRounds == -1)
        {
            return new BuildResult(fullChatHistory, 0);
        }

        var computedStartIndex = ResolveStartIndex(fullChatHistory, maxContextRounds);
        return new BuildResult(CreateHistoryWithSystemPromptPreserved(fullChatHistory, computedStartIndex), computedStartIndex);
    }

    private static int ResolveStartIndex(ChatHistory fullChatHistory, int maxContextRounds)
    {
        var roundWindowStart = ResolveRoundWindowStart(fullChatHistory, maxContextRounds);

        // The start must be the first user message inside the round window.
        var firstUserInWindow = FindFirstRoleIndex(fullChatHistory, AuthorRole.User, roundWindowStart, fullChatHistory.Count - 1);
        if (firstUserInWindow >= 0)
        {
            return firstUserInWindow;
        }

        // If no user message exists in the window, ignore the round window and use the latest user message.
        var latestUser = FindLastRoleIndex(fullChatHistory, AuthorRole.User);
        return latestUser >= 0 ? latestUser : 0;
    }

    private static int ResolveRoundWindowStart(ChatHistory fullChatHistory, int maxContextRounds)
    {
        if (maxContextRounds <= 0)
        {
            // Empty round window. Caller will fallback to latest user message.
            return fullChatHistory.Count;
        }

        var assistantMessageIndices = new List<int>();
        for (var i = 0; i < fullChatHistory.Count; i++)
        {
            if (fullChatHistory[i].Role == AuthorRole.Assistant)
            {
                assistantMessageIndices.Add(i);
            }
        }

        if (assistantMessageIndices.Count <= maxContextRounds)
        {
            return 0;
        }

        return assistantMessageIndices[assistantMessageIndices.Count - maxContextRounds];
    }

    private static int FindFirstRoleIndex(ChatHistory chatHistory, AuthorRole role, int startIndex, int endIndex)
    {
        if (startIndex < 0 || endIndex < startIndex || chatHistory.Count <= 0)
        {
            return -1;
        }

        var safeEnd = Math.Min(endIndex, chatHistory.Count - 1);
        for (var i = startIndex; i <= safeEnd; i++)
        {
            if (chatHistory[i].Role == role)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindLastRoleIndex(ChatHistory chatHistory, AuthorRole role)
    {
        for (var i = chatHistory.Count - 1; i >= 0; i--)
        {
            if (chatHistory[i].Role == role)
            {
                return i;
            }
        }

        return -1;
    }

    private static ChatHistory CreateHistoryWithSystemPromptPreserved(ChatHistory fullChatHistory, int startIndex)
    {
        startIndex = Math.Clamp(startIndex, 0, fullChatHistory.Count - 1);

        var sliced = new ChatHistory();

        // Keep system prompt(s) that appear before the start index.
        for (var i = 0; i < startIndex; i++)
        {
            if (fullChatHistory[i].Role == AuthorRole.System)
            {
                sliced.Add(fullChatHistory[i]);
            }
        }

        for (var i = startIndex; i < fullChatHistory.Count; i++)
        {
            sliced.Add(fullChatHistory[i]);
        }

        return sliced;
    }
}

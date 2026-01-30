using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using Everywhere.AI;
using Everywhere.Chat.Permissions;
using Everywhere.Common;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Provides essential functionalities for chat interactions.
/// e.g., run_subagent, manage_todo_list, etc.
/// </summary>
public class EssentialPlugin : BuiltInChatPlugin
{
    public override DynamicResourceKeyBase HeaderKey { get; } = new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_Essential_Header);
    public override DynamicResourceKeyBase DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_Essential_Description);
    public override LucideIconKind? Icon => LucideIconKind.ToolCase;
    public override bool IsDefaultEnabled => true;

    private readonly ILogger<EssentialPlugin> _logger;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum TodoAction
    {
        Reset,
        Read
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum TodoStatus
    {
        NotStarted,
        InProgress,
        Completed
    }

    [Serializable]
    public class TodoItem
    {
        [Description("1-based unique identifier for the todo item.")]
        public required int Id { get; set; }

        [Description("Concise action-oriented todo label displayed in UI.")]
        public required string Title { get; set; }

        [Description("(Optional) Detailed context, requirements, or implementation notes.")]
        public string? Description { get; set; }

        public TodoStatus Status { get; set; } = TodoStatus.NotStarted;
    }

    /// <summary>
    /// Stores to-do lists for different chat contexts.
    /// </summary>
    private readonly ConditionalWeakTable<ChatContext, List<TodoItem>> _todoLists = new();

    public EssentialPlugin(ILogger<EssentialPlugin> logger) : base("essential")
    {
        _logger = logger;

        _functionsSource.Edit(list =>
        {
            list.Add(
                new NativeChatFunction(
                    RunSubagentAsync,
                    ChatFunctionPermissions.None));
            list.Add(
                new NativeChatFunction(
                    ManageTodoList,
                    ChatFunctionPermissions.None));
        });
    }

    [KernelFunction("run_subagent")]
    [Description(
        """
        Launch a new agent to handle complex, multi-step tasks autonomously, which is good for complex tasks that require decision-making and planning.
        The agent can access tools as you can. After started, you will wait for the subagent to complete and return the final result as string.
        Each agent invocation is stateless, so make sure to provide all necessary context and instructions for the subagent to perform its task effectively.
        """)]
    [DynamicResourceKey(LocaleKey.BuiltInChatPlugin_Essential_RunSubagent_Header, LocaleKey.BuiltInChatPlugin_Essential_RunSubagent_Description)]
    private async Task<string> RunSubagentAsync(
        [FromKernelServices] ChatService chatService,
        [FromKernelServices] CustomAssistant customAssistant,
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [Description("A detailed description of the task for the agent to perform")] string prompt,
        [Description("A concise title for the agent's task. Should in system language.")] string title,
        CancellationToken cancellationToken)
    {
        userInterface.DisplaySink.AppendDynamicResourceKey(
            new FormattedDynamicResourceKey(
                LocaleKey.BuiltInChatPlugin_Essential_RunSubagent_Title,
                new DirectResourceKey(title)),
            "Large");

        // Create a temporary chat context for the subagent
        var chatContext = new ChatContext { Metadata = { IsTemporary = true } };
        chatContext.Add(new UserChatMessage(prompt, []));
        var assistantChatMessage = new AssistantChatMessage();

        await chatService.GenerateAsync(chatContext, customAssistant, assistantChatMessage, cancellationToken);

        if (assistantChatMessage.Count < 1)
        {
            _logger.LogWarning("Subagent did not return any messages for task '{Title}'", title);
            return "The subagent did not return any response.";
        }

        var result = (assistantChatMessage.Items[^1] as AssistantChatMessageTextSpan)?.Content;
        userInterface.DisplaySink.AppendMarkdown().Append(result);
        userInterface.DisplaySink.AppendDynamicResourceKey(
            new FormattedDynamicResourceKey(
                LocaleKey.BuiltInChatPlugin_Essential_RunSubagent_TokenCount,
                new DirectResourceKey(assistantChatMessage.UsageDetails.InputTokenCount),
                new DirectResourceKey(assistantChatMessage.UsageDetails.OutputTokenCount),
                new DirectResourceKey(assistantChatMessage.UsageDetails.TotalTokenCount)),
            "Small Muted");
        return result ?? string.Empty;
    }

    [KernelFunction("manage_todo_list")]
    [Description(
        "Manage a structured todo list to track progress and plan tasks. Use this tool VERY frequently to ensure task visibility and proper planning.")]
    [DynamicResourceKey(
        LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Header,
        LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Description)]
    private string ManageTodoList(
        [FromKernelServices] ChatContext chatContext,
        [FromKernelServices] IChatPluginUserInterface userInterface,
        TodoAction action,
        [Description(
            "Complete array of all todo items (required for reset, optional for read). ALWAYS provide complete list when rewriting - partial updates not supported. This MUST be a JSON array instead of a stringified JSON.") ]
        List<TodoItem>? items)
    {
        var currentList = _todoLists.GetOrCreateValue(chatContext);

        switch (action)
        {
            case TodoAction.Reset when items == null:
            {
                throw new HandledFunctionInvokingException(
                    HandledFunctionInvokingExceptionType.ArgumentMissing,
                    nameof(items),
                    new ArgumentException("items is required for reset action.", nameof(items)));
            }
            case TodoAction.Reset:
            {
                currentList.Clear();
                currentList.AddRange(items);
                AppendDisplayBlock();
                return "Todo list reset successfully.";
            }
            case TodoAction.Read when currentList.Count == 0:
            {
                AppendDisplayBlock();
                return "Todo list is empty.";
            }
            case TodoAction.Read:
            {
                // Display the current list to the user
                AppendDisplayBlock();

                var sb = new StringBuilder();
                sb.AppendLine("Current Todo List:");
                foreach (var item in currentList)
                {
                    sb.AppendLine($"- ID: {item.Id}, Status: {item.Status}, Title: {item.Title}");
                    if (!string.IsNullOrWhiteSpace(item.Description))
                    {
                        sb.AppendLine($"  Description: {item.Description}");
                    }
                }
                return sb.ToString();
            }
            default:
            {
                throw new HandledFunctionInvokingException(
                    HandledFunctionInvokingExceptionType.ArgumentError,
                    nameof(action),
                    new ArgumentException("Invalid action.", nameof(action)));
            }
        }

        void AppendDisplayBlock()
        {
            if (currentList.Count == 0)
            {
                userInterface.DisplaySink.AppendDynamicResourceKey(
                    new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Empty));
                return;
            }

            var stringBuilder = new StringBuilder();
            foreach (var item in currentList)
            {
                var statusIcon = item.Status switch
                {
                    TodoStatus.NotStarted => "🔳",
                    TodoStatus.InProgress => "🚧",
                    TodoStatus.Completed => "✅",
                    _ => "🔳"
                };
                stringBuilder.AppendLine($"{statusIcon} {item.Title}");
            }
            userInterface.DisplaySink.AppendText(stringBuilder.TrimEnd().ToString());
        }
    }
}
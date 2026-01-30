using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Text;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicData;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Lucide.Avalonia;
using MessagePack;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ZLinq;

namespace Everywhere.Chat;

[MessagePackObject(OnlyIncludeKeyedMembers = true)]
[Union(0, typeof(SystemChatMessage))]
[Union(1, typeof(AssistantChatMessage))]
[Union(2, typeof(UserChatMessage))]
[Union(3, typeof(ActionChatMessage))]
[Union(4, typeof(FunctionCallChatMessage))]
public abstract partial class ChatMessage : ObservableObject
{
    public abstract AuthorRole Role { get; }

    [IgnoreMember]
    [JsonIgnore]
    [ObservableProperty]
    public partial bool IsBusy { get; set; }
}

public interface IHaveChatAttachments
{
    IEnumerable<ChatAttachment> Attachments { get; }
}

[MessagePackObject(OnlyIncludeKeyedMembers = true)]
public partial class SystemChatMessage(string systemPrompt) : ChatMessage
{
    public override AuthorRole Role => AuthorRole.System;

    [Key(0)]
    [ObservableProperty]
    public partial string SystemPrompt { get; set; } = systemPrompt;

    public override string ToString() => SystemPrompt;
}

[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class AssistantChatMessage :
    ChatMessage,
    IHaveChatAttachments,
    ISourceList<AssistantChatMessageSpan>
{
    public override AuthorRole Role => AuthorRole.Assistant;

    [Key(0)]
    private string? Content
    {
        get => null; // for forward compatibility
        init
        {
            if (!value.IsNullOrEmpty())
            {
                _spansSource.Edit(list => list.Add(new AssistantChatMessageTextSpan(value)));
            }
        }
    }

    [Key(1)]
    [ObservableProperty]
    public partial DynamicResourceKeyBase? ErrorMessageKey { get; set; }

    [Key(2)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedSeconds))]
    public partial DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Key(3)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedSeconds))]
    public partial DateTimeOffset FinishedAt { get; set; }

    [IgnoreMember]
    [JsonIgnore]
    public double ElapsedSeconds => Math.Max((FinishedAt - CreatedAt).TotalSeconds, 0);

    [Key(4)]
    private IList<FunctionCallChatMessage>? FunctionCalls
    {
        get => null; // for forward compatibility
        init
        {
            if (value is { Count: > 0 })
            {
                _spansSource.Edit(list => list.Add(new AssistantChatMessageFunctionCallSpan(value)));
            }
        }
    }

    [Key(5)]
    [Obsolete]
    private IEnumerable<LegacyAssistantChatMessageSpan>? LegacySerializableSpans
    {
        get => null; // For forward compatibility
        init
        {
            if (value is null) return;
            _spansSource.Edit(list =>
            {
                list.Clear();
                foreach (var legacySpan in value)
                {
                    if (legacySpan.ReasoningOutput is { Length: > 0 } reasoningOutput)
                    {
                        list.Add(
                            new AssistantChatMessageReasoningSpan(reasoningOutput)
                            {
                                CreatedAt = legacySpan.CreatedAt,
                                FinishedAt = legacySpan.ReasoningFinishedAt ?? legacySpan.FinishedAt
                            });
                    }

                    if (legacySpan.FunctionCalls is { Count: > 0 } functionCalls)
                    {
                        list.Add(
                            new AssistantChatMessageFunctionCallSpan(functionCalls)
                            {
                                CreatedAt = legacySpan.CreatedAt,
                                FinishedAt = legacySpan.FinishedAt
                            });
                    }

                    if (legacySpan.Content is { Length: > 0 } content)
                    {
                        list.Add(
                            new AssistantChatMessageTextSpan(content)
                            {
                                CreatedAt = legacySpan.CreatedAt,
                                FinishedAt = legacySpan.FinishedAt
                            });
                    }
                }
            });
        }
    }

    /// <summary>
    /// Each span represents a part of the message content and function calls.
    /// </summary>
    [IgnoreMember]
    public ReadOnlyObservableCollection<AssistantChatMessageSpan> Spans { get; }

    [Key(9)]
    [ObservableProperty]
    public partial MetadataDictionary? Metadata { get; set; }

    [Key(10)]
    private IEnumerable<AssistantChatMessageSpan>? SerializableSpans
    {
        get => _spansSource.Items;
        set
        {
            if (value is null) return;
            _spansSource.Edit(list =>
            {
                list.Clear();
                list.AddRange(value);
            });
        }
    }

    [Key(11)]
    [ObservableProperty]
    public partial ChatUsageDetails UsageDetails { get; private set; } = new();

    [IgnoreMember]
    public IEnumerable<ChatAttachment> Attachments => _spansSource.Items.OfType<IHaveChatAttachments>().SelectMany(s => s.Attachments);

    /// <summary>
    /// The private source for function calls.
    /// </summary>
    [IgnoreMember] private readonly SourceList<AssistantChatMessageSpan> _spansSource = new();
    [IgnoreMember] private readonly IDisposable _spansConnection;

    public AssistantChatMessage()
    {
        Spans = _spansSource
            .Connect()
            .ObserveOnDispatcher()
            .DisposeMany()
            .BindEx(out _spansConnection);
    }

    public void AddSpan(AssistantChatMessageSpan span)
    {
        _spansSource.Add(span);
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var span in Items.AsValueEnumerable().OfType<AssistantChatMessageTextSpan>().Where(s => s.ContentMarkdownBuilder.Length > 0))
        {
            builder.AppendLine(span.ContentMarkdownBuilder.ToString());
        }

        return builder.TrimEnd().ToString();
    }

    public void Dispose()
    {
        _spansSource.Dispose();
        _spansConnection.Dispose();
    }

    #region ISourceList<AssistantChatMessageSpan> Implementation

    public int Count => _spansSource.Count;
    public IObservable<int> CountChanged => _spansSource.CountChanged;
    public IReadOnlyList<AssistantChatMessageSpan> Items => _spansSource.Items;

    public IObservable<IChangeSet<AssistantChatMessageSpan>> Connect(Func<AssistantChatMessageSpan, bool>? predicate = null)
    {
        return _spansSource.Connect(predicate);
    }

    public IObservable<IChangeSet<AssistantChatMessageSpan>> Preview(Func<AssistantChatMessageSpan, bool>? predicate = null)
    {
        return _spansSource.Preview(predicate);
    }

    public void Edit(Action<IExtendedList<AssistantChatMessageSpan>> updateAction)
    {
        _spansSource.Edit(updateAction);
    }

    #endregion

}

[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public partial class UserChatMessage(string content, IEnumerable<ChatAttachment> attachments) : ChatMessage, IHaveChatAttachments
{
    public override AuthorRole Role => AuthorRole.User;

    /// <summary>
    /// The actual prompt that sends to the LLM.
    /// Including attachments converted prompts that are invisible to the user.
    /// </summary>
    [Key(0)]
    [ObservableProperty]
    public partial string Content { get; set; } = content;

    [Key(1)]
    public IEnumerable<ChatAttachment> Attachments { get; set; } = attachments;

    [Key(3)]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public override string ToString() => Content;
}

/// <summary>
/// Represents an action message in the chat.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public partial class ActionChatMessage : ChatMessage
{
    [Key(0)]
    public override AuthorRole Role { get; }

    [Key(1)]
    [ObservableProperty]
    public partial LucideIconKind Icon { get; set; }

    [Key(2)]
    [ObservableProperty]
    public partial DynamicResourceKey? HeaderKey { get; set; }

    [Key(3)]
    [ObservableProperty]
    public partial string? Content { get; set; }

    [Key(4)]
    [ObservableProperty]
    public partial DynamicResourceKeyBase? ErrorMessageKey { get; set; }

    [Key(5)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedSeconds))]
    public partial DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Key(6)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedSeconds))]
    public partial DateTimeOffset FinishedAt { get; set; }

    [IgnoreMember]
    [JsonIgnore]
    public double ElapsedSeconds => Math.Max((FinishedAt - CreatedAt).TotalSeconds, 0);

    [SerializationConstructor]
    private ActionChatMessage() { }

    public ActionChatMessage(AuthorRole role, LucideIconKind icon, DynamicResourceKey? headerKey)
    {
        Role = role;
        Icon = icon;
        HeaderKey = headerKey;
    }
}

/// <summary>
/// Represents a function call action message in the chat.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class FunctionCallChatMessage : ChatMessage, IHaveChatAttachments, IDisposable
{
    [Key(0)]
    public override AuthorRole Role => AuthorRole.Tool;

    [Key(1)]
    [ObservableProperty]
    public partial LucideIconKind Icon { get; set; }

    /// <summary>
    /// Obsolete: Use HeaderKey instead.
    /// </summary>
    [Key(2)]
    private DynamicResourceKey? ObsoleteHeaderKey
    {
        get => null; // for forward compatibility
        set => HeaderKey = value;
    }

    [Key(3)]
    public string? Content { get; set; }

    [Key(4)]
    [ObservableProperty]
    public partial DynamicResourceKeyBase? ErrorMessageKey { get; set; }

    [Key(5)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedSeconds))]
    public partial DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Key(6)]
    public List<FunctionCallContent> Calls { get; set; } = [];

    [Key(7)]
    public List<FunctionResultContent> Results { get; set; } = [];

    [Key(8)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedSeconds))]
    public partial DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.UtcNow;

    [IgnoreMember]
    [JsonIgnore]
    public double ElapsedSeconds => Math.Max((FinishedAt - CreatedAt).TotalSeconds, 0);

    [Key(9)]
    [ObservableProperty]
    public partial DynamicResourceKeyBase? HeaderKey { get; set; }

    [Key(10)]
    private IEnumerable<ChatPluginDisplayBlock> SerializableDisplayBlocks
    {
        get => _displaySink.Items;
        set => _displaySink.Edit(list =>
        {
            list.Clear();
            list.AddRange(value);
        });
    }

    /// <summary>
    /// The display blocks that make up the content of this function call message,
    /// which can include text, markdown, progress indicators, file references, and function call/result displays.
    /// These blocks are rendered in the chat UI to present the function call information to the user.
    /// And can be serialized for persistence or transmission.
    /// </summary>
    /// <remarks>
    /// The reason why we need to populate the Content property of function call/result display blocks
    /// is that during deserialization, the references to the actual FunctionCallContent and FunctionResultContent
    /// objects are not automatically restored. Therefore, we need to manually link them back
    /// based on their IDs after deserialization. This ensures that the display blocks have access
    /// to the full details of the function calls and results they are meant to represent.
    /// </remarks>
    [IgnoreMember]
    public ReadOnlyObservableCollection<ChatPluginDisplayBlock> DisplayBlocks { get; }

    /// <summary>
    /// The display sink that holds the display blocks for this function call message.
    /// </summary>
    [IgnoreMember]
    public IChatPluginDisplaySink DisplaySink => _displaySink;

    [Key(11)]
    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    [IgnoreMember]
    [JsonIgnore]
    public bool IsWaitingForUserInput => _displaySink.Any(db => db.IsWaitingForUserInput);

    /// <summary>
    /// Attachments associated with this action message. Used to provide additional context of a tool call result.
    /// </summary>
    [IgnoreMember]
    public IEnumerable<ChatAttachment> Attachments => Results.Select(r => r.Result).OfType<ChatAttachment>();

    [IgnoreMember] private readonly ChatPluginDisplaySink _displaySink = new();
    [IgnoreMember] private readonly CompositeDisposable _disposables = new(3);

    [SerializationConstructor]
    private FunctionCallChatMessage() : this(default, null)
    {
        // This constructor is for the deserializer.
        // The pipeline is set up in the primary constructor.
    }

    public FunctionCallChatMessage(LucideIconKind icon, DynamicResourceKeyBase? headerKey)
    {
        Icon = icon;
        HeaderKey = headerKey;

        // Set up the DynamicData pipeline
        DisplayBlocks = _displaySink
            .Connect()
            .ObserveOnDispatcher()
            .DisposeMany()
            .BindEx(_disposables);

        // Monitor IsWaitingForUserInput changes
        _disposables.Add(
            _displaySink
                .Connect()
                .WhenAnyPropertyChanged(nameof(ChatPluginDisplayBlock.IsWaitingForUserInput))
                .Subscribe(_ => OnPropertyChanged(nameof(IsWaitingForUserInput))));

        _disposables.Add(_displaySink);
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
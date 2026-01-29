using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables;
using System.Text;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicData;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using LiveMarkdown.Avalonia;
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
    IReadOnlyList<AssistantChatMessageSpan>,
    IDisposable
{
    public override AuthorRole Role => AuthorRole.Assistant;

    [Key(0)]
    private string? Content
    {
        get => null; // for forward compatibility
        init
        {
            if (!value.IsNullOrEmpty()) EnsureInitialSpan().MarkdownBuilder.Append(value);
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
            if (value is { Count: > 0 }) EnsureInitialSpan().FunctionCalls.AddRange(value);
        }
    }

    [Key(5)]
    private IEnumerable<AssistantChatMessageSpan> SerializableSpans
    {
        get => _spansSource.Items;
        set => _spansSource.Edit(list =>
        {
            list.Clear();
            list.AddRange(value);
        });
    }

    /// <summary>
    /// Each span represents a part of the message content and function calls.
    /// </summary>
    [IgnoreMember]
    public ReadOnlyObservableCollection<AssistantChatMessageSpan> Spans { get; }

    [Key(6)]
    [ObservableProperty]
    public partial long InputTokenCount { get; set; }

    [Key(7)]
    [ObservableProperty]
    public partial long OutputTokenCount { get; set; }

    [Key(8)]
    [ObservableProperty]
    public partial double TotalTokenCount { get; set; }

    [IgnoreMember]
    public IEnumerable<ChatAttachment> Attachments => _spansSource.Items.SelectMany(s => s.Attachments);

    [IgnoreMember]
    public int Count => _spansSource.Items.Count;

    [IgnoreMember]
    public AssistantChatMessageSpan this[int index] => _spansSource.Items[index];

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

    private AssistantChatMessageSpan EnsureInitialSpan()
    {
        _spansSource.Edit(list =>
        {
            if (list.Count == 0) list.Add(new AssistantChatMessageSpan());
        });
        return _spansSource.Items[^1];
    }

    public void AddSpan(AssistantChatMessageSpan span)
    {
        _spansSource.Add(span);
    }

    public IEnumerator<AssistantChatMessageSpan> GetEnumerator()
    {
        return _spansSource.Items.GetEnumerator();
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var span in Spans.AsValueEnumerable().Where(s => s.MarkdownBuilder.Length > 0))
        {
            builder.AppendLine(span.MarkdownBuilder.ToString());
        }

        return builder.TrimEnd().ToString();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return ((IEnumerable)_spansSource.Items).GetEnumerator();
    }

    public void Dispose()
    {
        _spansSource.Dispose();
        _spansConnection.Dispose();
    }
}

/// <summary>
/// Represents a span of content in an assistant chat message.
/// A span can contain markdown content and associated function calls.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class AssistantChatMessageSpan : ObservableObject, IHaveChatAttachments, IDisposable
{
    [IgnoreMember]
    public ObservableStringBuilder MarkdownBuilder { get; }

    [Key(0)]
    public string Content
    {
        get => MarkdownBuilder.ToString();
        init => MarkdownBuilder.Append(value);
    }

    [Key(1)]
    private IEnumerable<FunctionCallChatMessage> SerializableFunctionCalls
    {
        get => _functionCallsSource.Items;
        set => _functionCallsSource.Edit(list =>
        {
            list.Clear();
            list.AddRange(value);
        });
    }

    [IgnoreMember]
    public ReadOnlyObservableCollection<FunctionCallChatMessage> FunctionCalls { get; }

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

    [IgnoreMember]
    public ObservableStringBuilder ReasoningMarkdownBuilder => EnsureReasoningMarkdownBuilder();

    /// <summary>
    /// The reasoning output in Markdown format for serialization.
    /// </summary>
    [Key(4)]
    public string? ReasoningOutput
    {
        get => _reasoningMarkdownBuilder?.ToString();
        init
        {
            if (value is not { Length: > 0 }) return;

            EnsureReasoningMarkdownBuilder().Append(value);
        }
    }

    [Key(5)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasoningElapsedSeconds))]
    public partial DateTimeOffset? ReasoningFinishedAt { get; set; }

    [IgnoreMember]
    [JsonIgnore]
    public double ReasoningElapsedSeconds => Math.Max((ReasoningFinishedAt.GetValueOrDefault() - CreatedAt).TotalSeconds, 0);

    /// <summary>
    /// Additional metadata associated with this message span. e.g. thoughtSignature for Gemini
    /// </summary>
    [Key(6)]
    public Dictionary<string, object?> Metadata { get; set; } = new();

    [Key(7)]
    [ObservableProperty]
    public partial ChatFileAttachment? ImageOutput { get; set; }

    [IgnoreMember]
    public IEnumerable<ChatAttachment> Attachments
    {
        get
        {
            foreach (var functionCallChatMessage in _functionCallsSource.Items)
            {
                foreach (var attachment in functionCallChatMessage.Attachments)
                {
                    yield return attachment;
                }
            }

            if (ImageOutput is not null)
            {
                yield return ImageOutput;
            }
        }
    }

    [IgnoreMember] private readonly SourceList<FunctionCallChatMessage> _functionCallsSource = new();
    [IgnoreMember] private readonly IDisposable _functionCallsConnection;

    [IgnoreMember] private ObservableStringBuilder? _reasoningMarkdownBuilder;

    public AssistantChatMessageSpan()
    {
        MarkdownBuilder = new ObservableStringBuilder();
        MarkdownBuilder.Changed += HandleMarkdownBuilderChanged;

        FunctionCalls = _functionCallsSource
            .Connect()
            .ObserveOnDispatcher()
            .DisposeMany()
            .BindEx(out _functionCallsConnection);
    }

    public void AddFunctionCall(FunctionCallChatMessage functionCall)
    {
        _functionCallsSource.Add(functionCall);
    }

    private void HandleMarkdownBuilderChanged(in ObservableStringBuilderChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Content));
    }

    [MemberNotNull(nameof(_reasoningMarkdownBuilder))]
    private ObservableStringBuilder EnsureReasoningMarkdownBuilder()
    {
        if (_reasoningMarkdownBuilder != null) return _reasoningMarkdownBuilder;
        _reasoningMarkdownBuilder = new ObservableStringBuilder();
        _reasoningMarkdownBuilder.Changed += HandleReasoningMarkdownBuilderChanged;
        return _reasoningMarkdownBuilder;
    }

    private void HandleReasoningMarkdownBuilderChanged(in ObservableStringBuilderChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ReasoningOutput));
    }

    public void Dispose()
    {
        MarkdownBuilder.Changed -= HandleMarkdownBuilderChanged;
        _functionCallsSource.Dispose();
        _functionCallsConnection.Dispose();
        if (_reasoningMarkdownBuilder != null) _reasoningMarkdownBuilder.Changed -= HandleReasoningMarkdownBuilderChanged;
    }
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
        _disposables.Add(_displaySink
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
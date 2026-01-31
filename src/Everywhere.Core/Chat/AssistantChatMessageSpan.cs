using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicData;
using Everywhere.Common;
using Everywhere.Serialization;
using LiveMarkdown.Avalonia;
using MessagePack;

namespace Everywhere.Chat;

/// <summary>
/// Represents a span of content in an assistant chat message.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
[Union(0, typeof(AssistantChatMessageTextSpan))]
[Union(1, typeof(AssistantChatMessageFunctionCallSpan))]
[Union(2, typeof(AssistantChatMessageReasoningSpan))]
[Union(3, typeof(AssistantChatMessageImageSpan))]
public abstract partial class AssistantChatMessageSpan : ObservableObject
{
    [Key(0)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedSeconds))]
    public partial DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Key(1)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedSeconds))]
    public partial DateTimeOffset? FinishedAt { get; set; }

    [IgnoreMember]
    [JsonIgnore]
    public double ElapsedSeconds => FinishedAt.HasValue ? Math.Max((FinishedAt.Value - CreatedAt).TotalSeconds, 0) : 0;

    [Key(2)]
    [ObservableProperty]
    public partial MetadataDictionary? Metadata { get; set; }
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class AssistantChatMessageTextSpan : AssistantChatMessageSpan, IDisposable
{
    [IgnoreMember]
    [JsonIgnore]
    public ObservableStringBuilder ContentMarkdownBuilder => EnsureContentMarkdownBuilder();

    [Key(3)]
    public string? Content
    {
        get => _contentMarkdownBuilder?.ToString();
        init
        {
            if (value is not { Length: > 0 }) return;

            EnsureContentMarkdownBuilder().Append(value);
        }
    }

    [IgnoreMember] private ObservableStringBuilder? _contentMarkdownBuilder;

    [MemberNotNull(nameof(_contentMarkdownBuilder))]
    private ObservableStringBuilder EnsureContentMarkdownBuilder()
    {
        if (_contentMarkdownBuilder != null) return _contentMarkdownBuilder;
        _contentMarkdownBuilder = new ObservableStringBuilder();
        _contentMarkdownBuilder.Changed += HandleContentMarkdownBuilderChanged;
        return _contentMarkdownBuilder;
    }

    private void HandleContentMarkdownBuilderChanged(in ObservableStringBuilderChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Content));
    }

    public void Dispose()
    {
        _contentMarkdownBuilder?.Changed -= HandleContentMarkdownBuilderChanged;
    }

    [SerializationConstructor]
    public AssistantChatMessageTextSpan() { }

    public AssistantChatMessageTextSpan(string initialContent)
    {
        EnsureContentMarkdownBuilder().Append(initialContent);
    }
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class AssistantChatMessageFunctionCallSpan :
    AssistantChatMessageSpan,
    IHaveChatAttachments,
    ISourceList<FunctionCallChatMessage>
{
    [IgnoreMember]
    [JsonIgnore]
    public ReadOnlyObservableCollection<FunctionCallChatMessage> FunctionCalls { get; }

    [IgnoreMember]
    [JsonIgnore]
    public IEnumerable<ChatAttachment> Attachments =>
        _functionCallsSource.Items.SelectMany(functionCallChatMessage => functionCallChatMessage.Attachments);

    [Key(3)]
    private IEnumerable<FunctionCallChatMessage> SerializableFunctionCalls
    {
        get => _functionCallsSource.Items;
        set => _functionCallsSource.Edit(list =>
        {
            list.Clear();
            list.AddRange(value);
        });
    }

    [IgnoreMember] private readonly SourceList<FunctionCallChatMessage> _functionCallsSource = new();
    [IgnoreMember] private readonly IDisposable _functionCallsConnection;

    [SerializationConstructor]
    public AssistantChatMessageFunctionCallSpan()
    {
        FunctionCalls = _functionCallsSource
            .Connect()
            .ObserveOnDispatcher()
            .DisposeMany()
            .BindEx(out _functionCallsConnection);
    }

    public AssistantChatMessageFunctionCallSpan(FunctionCallChatMessage initialFunctionCall) : this()
    {
        _functionCallsSource.Add(initialFunctionCall);
    }

    public AssistantChatMessageFunctionCallSpan(IEnumerable<FunctionCallChatMessage> initialFunctionCalls) : this()
    {
        _functionCallsSource.AddRange(initialFunctionCalls);
    }

    public void Add(FunctionCallChatMessage functionCall)
    {
        _functionCallsSource.Add(functionCall);
    }

    public void AddRange(IEnumerable<FunctionCallChatMessage> functionCalls)
    {
        _functionCallsSource.AddRange(functionCalls);
    }

    public void Dispose()
    {
        _functionCallsSource.Dispose();
        _functionCallsConnection.Dispose();
    }

    #region ISourceList<FunctionCallChatMessage> Implementation

    public int Count => _functionCallsSource.Count;
    public IObservable<int> CountChanged => _functionCallsSource.CountChanged;
    public IReadOnlyList<FunctionCallChatMessage> Items => _functionCallsSource.Items;

    public IObservable<IChangeSet<FunctionCallChatMessage>> Connect(Func<FunctionCallChatMessage, bool>? predicate = null)
    {
        return _functionCallsSource.Connect(predicate);
    }

    public IObservable<IChangeSet<FunctionCallChatMessage>> Preview(Func<FunctionCallChatMessage, bool>? predicate = null)
    {
        return _functionCallsSource.Preview(predicate);
    }

    public void Edit(Action<IExtendedList<FunctionCallChatMessage>> updateAction)
    {
        _functionCallsSource.Edit(updateAction);
    }

    #endregion
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class AssistantChatMessageReasoningSpan : AssistantChatMessageSpan, IDisposable
{
    [IgnoreMember]
    public ObservableStringBuilder ReasoningMarkdownBuilder => EnsureReasoningMarkdownBuilder();

    /// <summary>
    /// The reasoning output in Markdown format for serialization.
    /// </summary>
    [Key(3)]
    public string? ReasoningOutput
    {
        get => _reasoningMarkdownBuilder?.ToString();
        init
        {
            if (value is not { Length: > 0 }) return;

            EnsureReasoningMarkdownBuilder().Append(value);
        }
    }

    [IgnoreMember] private ObservableStringBuilder? _reasoningMarkdownBuilder;

    [SerializationConstructor]
    public AssistantChatMessageReasoningSpan() { }

    public AssistantChatMessageReasoningSpan(string initialReasoning)
    {
        EnsureReasoningMarkdownBuilder().Append(initialReasoning);
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
        _reasoningMarkdownBuilder?.Changed -= HandleReasoningMarkdownBuilderChanged;
    }
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public partial class AssistantChatMessageImageSpan : AssistantChatMessageSpan, IHaveChatAttachments
{
    [Key(3)]
    [ObservableProperty]
    public partial ChatFileAttachment? ImageOutput { get; set; }

    [IgnoreMember]
    [JsonIgnore]
    public IEnumerable<ChatAttachment> Attachments =>
        ImageOutput is not null ? new[] { ImageOutput } : Array.Empty<ChatAttachment>();
}
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicData;
using MessagePack;

namespace Everywhere.Chat;

/// <summary>Tree node in the chat history. The current branch is resolved by ChoiceIndex per node.</summary>
[MessagePackObject(AllowPrivate = true)]
public sealed partial class ChatMessageNode : ObservableObject, IDisposable
{
    [Key(0)]
    public Guid Id { get; }

    [Key(1)]
    public ChatMessage Message { get; }

    [Key(2)]
    public IReadOnlyList<Guid> Children => _children.Items;

    /// <summary>
    /// Index of the chosen child in <see cref="Children"/> (-1 when none).
    /// When persisted, it should be mapped to the child's ID (ChoiceChildId) to avoid index drift under concurrent inserts.
    /// </summary>
    [Key(3)]
    public int ChoiceIndex
    {
        get => Math.Min(field, Children.Count - 1);
        set => SetProperty(ref field, Math.Clamp(value, -1, Children.Count - 1));
    }

    [IgnoreMember]
    public int ChoiceCount => Children.Count;

    [IgnoreMember]
    public ChatMessageNode? Parent { get; internal set; }

    [IgnoreMember]
    [field: AllowNull, MaybeNull]
    public ChatContext Context
    {
        get => field ?? throw new InvalidOperationException("This node is not attached to a ChatContext.");
        internal set;
    }

    [IgnoreMember] private readonly SourceList<Guid> _children = new();
    [IgnoreMember] private readonly IDisposable _childrenCountChangedSubscription;

    public ChatMessageNode(Guid id, ChatMessage message)
    {
        Id = id;
        Message = message ?? throw new ArgumentNullException(nameof(message)); // messagepack may pass null here so we guard against it
        message.PropertyChanged += HandleMessagePropertyChanged;
        _childrenCountChangedSubscription = _children.CountChanged.Subscribe(_ => OnPropertyChanged(nameof(ChoiceCount)));
    }

    [SerializationConstructor]
    private ChatMessageNode(Guid id, ChatMessage message, IReadOnlyList<Guid> children, int choiceIndex) : this(id, message)
    {
        _children.AddRange(children);
        ChoiceIndex = choiceIndex;
    }

    private void HandleMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Message));
    }

    public ChatMessageNode(ChatMessage message) : this(Guid.CreateVersion7(), message) { }

    public void Add(Guid childId) => _children.Add(childId);

    public void AddRange(IEnumerable<Guid> childIds) => _children.AddRange(childIds);

    public void Clear() => _children.Clear();

    internal static ChatMessageNode CreateRootNode(string systemPrompt) => new(Guid.Empty, new SystemChatMessage(systemPrompt));

    public void Dispose()
    {
        Message.PropertyChanged -= HandleMessagePropertyChanged;
        _childrenCountChangedSubscription.Dispose();
        _children.Dispose();
    }
}
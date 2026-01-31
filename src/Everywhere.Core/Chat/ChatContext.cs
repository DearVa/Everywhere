using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive.Disposables;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using DynamicData;
using Everywhere.Chat.Permissions;
using Everywhere.Interop;
using Everywhere.Utilities;
using MessagePack;

namespace Everywhere.Chat;

/// <summary>
/// Message sent when chat context metadata changes.
/// </summary>
/// <param name="context">The chat context whose metadata has changed. Null if the context has been released.</param>
/// <param name="metadata">The metadata that has changed.</param>
/// <param name="propertyName">
/// DateModified -> indicates the context has been modified. Need to save.
/// Topic -> indicates the topic has changed. Need to save.
/// IsSelected -> indicates selection state has changed.
/// </param>
public class ChatContextMetadataChangedMessage(ChatContext? context, ChatContextMetadata metadata, string? propertyName)
{
    public ChatContext? Context { get; set; } = context;

    public ChatContextMetadata Metadata { get; set; } = metadata;

    public string? PropertyName { get; set; } = propertyName;
}

/// <summary>
/// Maintains the context of the chat, including a tree of <see cref="ChatMessageNode"/> and other metadata.
/// The current branch is derived by following each node's <see cref="ChatMessageNode.ChoiceIndex"/>.
/// </summary>
[MessagePackObject(AllowPrivate = true)]
public sealed partial class ChatContext : ObservableObject, IObservableList<ChatMessageNode>
{
    [Key(0)]
    public ChatContextMetadata Metadata { get; }

    [IgnoreMember]
    public string SystemPrompt
    {
        get => _rootNode.Message.To<SystemChatMessage>().SystemPrompt;
        set => _rootNode.Message.To<SystemChatMessage>().SystemPrompt = value;
    }

    /// <summary>
    /// Items in the current branch, excluding the root system prompt node. Used for UI bindings.
    /// </summary>
    [IgnoreMember]
    public ReadOnlyObservableCollection<ChatMessageNode> DisplayItems { get; }

    /// <summary>
    /// Messages in the current branch.
    /// </summary>
    [IgnoreMember]
    public int Count => _branchNodes.Count;

    [IgnoreMember]
    public IObservable<int> CountChanged => _branchNodes.CountChanged;

    [IgnoreMember]
    public IReadOnlyList<ChatMessageNode> Items => _branchNodes.Items;

    /// <summary>
    /// Key: VisualElement.Id
    /// Value: VisualElement.
    /// VisualElement is dynamically created and not serialized, so we keep a map here to track them.
    /// This is also not serialized.
    /// </summary>
    [IgnoreMember]
    public ResilientCache<int, IVisualElement> VisualElements { get; } = new();

    /// <summary>
    /// A map of granted permissions for plugin functions in this chat context (session).
    /// Key: PluginName.FunctionName.id
    /// Value: Granted permissions for the function.
    /// </summary>
    [IgnoreMember]
    public Dictionary<string, ChatFunctionPermissions> GrantedPermissions { get; } = new();

    /// <summary>
    /// Resource key for the busy message to show when waiting for a response.
    /// This can be set temporarily using <see cref="SetBusyMessage(DynamicResourceKeyBase?)"/>.
    /// </summary>
    [IgnoreMember]
    [ObservableProperty]
    public partial DynamicResourceKeyBase? BusyMessage { get; private set; }

    /// <summary>
    /// Backing store for MessagePack (de)serialization: nodes are persisted as a collection, and linked by Ids.
    /// </summary>
    [Key(1)]
    private ICollection<ChatMessageNode> MessageNodes => _messageNodeMap.Values;

    /// <summary>
    /// Root node (Guid.Empty) containing the System Prompt.
    /// </summary>
    [Key(2)]
    private readonly ChatMessageNode _rootNode;

    /// <summary>
    /// Map of all message nodes by their ID. This allows for quick access to any node in the context.
    /// NOTE that this map does not include the root node, which is always at Id = Guid.Empty.
    /// </summary>
    [IgnoreMember] private readonly Dictionary<Guid, ChatMessageNode> _messageNodeMap = new();

    /// <summary>
    /// Nodes on the currently selected branch. [0] is always the root node.
    /// </summary>
    [IgnoreMember] private readonly SourceList<ChatMessageNode> _branchNodes = new();

    [IgnoreMember] private readonly IDisposable _branchNodesWithoutSystemSubscription;

    /// <summary>
    /// Constructor for MessagePack deserialization and for creating a new chat context with existing nodes.
    /// </summary>
    /// <param name="metadata"></param>
    /// <param name="messageNodes"></param>
    /// <param name="rootNode"></param>
    [SerializationConstructor]
    public ChatContext(ChatContextMetadata metadata, ICollection<ChatMessageNode> messageNodes, ChatMessageNode rootNode)
    {
        Metadata = metadata;
        _messageNodeMap.AddRange(messageNodes.Select(v => new KeyValuePair<Guid, ChatMessageNode>(v.Id, v)));
        _rootNode = rootNode;
        _branchNodes.Add(rootNode);

        foreach (var node in messageNodes.Append(rootNode))
        {
            node.Context = this;
            node.PropertyChanged += HandleNodePropertyChanged;
            foreach (var childId in node.Children) _messageNodeMap[childId].Parent = node;
        }

        if (_messageNodeMap.ContainsKey(Guid.Empty))
            throw new InvalidOperationException("Root node (Guid.Empty) must not be in the messageNodeMap.");

        UpdateBranchAfter(0, rootNode);

        DisplayItems = _branchNodes
            .Connect()
            .Filter(node => node != _rootNode)
            .BindEx(out _branchNodesWithoutSystemSubscription);
    }

    /// <summary>
    /// Creates a new chat context with the given system prompt. A new Guid v7 ID is assigned.
    /// </summary>
    public ChatContext()
    {
        Metadata = new ChatContextMetadata(Guid.CreateVersion7(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        _rootNode = ChatMessageNode.CreateRootNode("You are a helpful assistant.");
        _rootNode.PropertyChanged += HandleNodePropertyChanged;
        _branchNodes.Add(_rootNode);

        _branchNodesWithoutSystemSubscription = _branchNodes
            .Connect()
            .Filter(node => node != _rootNode)
            .Bind(out var branchNodesWithoutSystem)
            .Subscribe();
        DisplayItems = branchNodesWithoutSystem;
    }

    /// <summary>
    /// Create a new branch on the specified sibling node by inserting a new message at that position.
    /// </summary>
    public void CreateBranchOn(ChatMessageNode siblingNode, ChatMessage chatMessage)
    {
        var index = _branchNodes.Items.IndexOf(siblingNode);
        var afterNode = index switch
        {
            < 0 => throw new ArgumentException("The specified node is not in the current branch.", nameof(siblingNode)),
            0 => _rootNode,
            _ => _branchNodes.Items[index - 1]
        };

        var newNode = new ChatMessageNode(chatMessage)
        {
            Context = this,
            Parent = afterNode,
        };
        newNode.PropertyChanged += HandleNodePropertyChanged;
        _messageNodeMap[newNode.Id] = newNode;

        afterNode.Add(newNode.Id);
        afterNode.ChoiceIndex = afterNode.Children.Count - 1;

        UpdateBranchAfter(index - 1, afterNode);
    }

    public void Insert(int index, ChatMessage chatMessage) => Insert(index, new ChatMessageNode(chatMessage) { Context = this });

    /// <summary>
    /// Adds a message at the end of the current branch.
    /// </summary>
    public void Add(ChatMessage message)
    {
        Insert(_branchNodes.Count, new ChatMessageNode(message) { Context = this });
    }

    /// <summary>
    /// Gets all nodes in the chat context in all branches, including the root node.
    /// </summary>
    /// <returns></returns>
    public IEnumerable<ChatMessageNode> GetAllNodes()
    {
        yield return _rootNode;
        foreach (var node in _messageNodeMap.Values)
        {
            yield return node;
        }
    }

    /// <summary>
    /// Sets the busy message resource key for the duration of the returned IDisposable.
    /// </summary>
    /// <param name="busyMessage"></param>
    /// <returns></returns>
    public IDisposable SetBusyMessage(DynamicResourceKeyBase? busyMessage)
    {
        var previous = BusyMessage;
        BusyMessage = busyMessage;
        return Disposable.Create(() => BusyMessage = previous);
    }

    public IObservable<IChangeSet<ChatMessageNode>> Connect(Func<ChatMessageNode, bool>? predicate = null) => _branchNodes.Connect(predicate);

    public IObservable<IChangeSet<ChatMessageNode>> Preview(Func<ChatMessageNode, bool>? predicate = null) => _branchNodes.Preview(predicate);

    private void HandleNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessageNode.ChoiceIndex))
        {
            UpdateBranchAfterNode(sender.NotNull<ChatMessageNode>());
        }

        Metadata.DateModified = DateTimeOffset.UtcNow;
        WeakReferenceMessenger.Default.Send(new ChatContextMetadataChangedMessage(this, Metadata, nameof(Metadata.DateModified)));
    }

    /// <summary>
    /// Rebuilds the current branch from the specified node forward.
    /// </summary>
    private void UpdateBranchAfterNode(ChatMessageNode node) => UpdateBranchAfter(_branchNodes.Items.IndexOf(node), node);

    private void UpdateBranchAfter(int index, ChatMessageNode node)
    {
        if (index == -1)
            throw new ArgumentOutOfRangeException(nameof(index), "Node is not in the branch nodes.");

        for (var i = _branchNodes.Count - 1; i > index; i--) _branchNodes.RemoveAt(i);

        // Follow ChoiceIndex down the tree.
        while (true)
        {
            if (node.ChoiceIndex < 0 || node.ChoiceIndex >= node.Children.Count) break;
            _branchNodes.Add(node = _messageNodeMap[node.Children[node.ChoiceIndex]]);
        }
    }

    private void Insert(int index, ChatMessageNode newNode)
    {
        if (newNode.Id == Guid.Empty)
            throw new ArgumentException("New node must have a non-empty ID.", nameof(newNode));

        _messageNodeMap[newNode.Id] = newNode;
        newNode.PropertyChanged += HandleNodePropertyChanged;

        var afterNode = index switch
        {
            0 => _rootNode,
            _ => _branchNodes.Items[index - 1]
        };

        if (afterNode.Children.Count > 0)
        {
            newNode.AddRange(afterNode.Children);
            newNode.ChoiceIndex = afterNode.ChoiceIndex;
            foreach (var afterNodeChildId in afterNode.Children)
            {
                _messageNodeMap[afterNodeChildId].Parent = newNode;
            }

            afterNode.Clear();
        }

        newNode.Parent = afterNode;
        afterNode.Add(newNode.Id);

        UpdateBranchAfter(index - 1, afterNode);
    }

    public void Dispose()
    {
        foreach (var node in _messageNodeMap.Values)
        {
            node.PropertyChanged -= HandleNodePropertyChanged;
        }

        _branchNodesWithoutSystemSubscription.Dispose();
        _branchNodes.Dispose();
    }
}
using Everywhere.Chat;
using Everywhere.Common;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Views;

/// <summary>
/// Keeps a lightweight, UI-thread-owned index of user turns in the selected branch, independent
/// of the materialized message window. Entries retain identity across incremental branch edits.
/// </summary>
public sealed class ChatTurnNavigationIndex : IDisposable
{
    /// <summary>
    /// Gets the ordered user turns, including user actions.
    /// </summary>
    public IReadOnlyList<Entry> Turns => _turns;

    /// <summary>
    /// Raised after a branch change has been applied on the UI thread.
    /// </summary>
    public event Action? Changed;

    private readonly List<ChatMessageNode> _nodes = [];
    private readonly List<Entry> _turns = [];
    private readonly IDisposable _subscription;

    /// <summary>
    /// Observes the visible selected branch of the supplied context.
    /// </summary>
    public ChatTurnNavigationIndex(ChatContext context)
    {
        _subscription = context
            .ConnectDisplayItems()
            .ObserveOnAvaloniaDispatcher()
            .Clone(_nodes)
            .Subscribe(_ =>
            {
                Reconcile();
                Changed?.Invoke();
            });
    }

    private void Reconcile()
    {
        // Only structural branch notifications visit the full index; rendering and preview reads
        // never scan history. Reuse entries by node identity when edits replace a branch suffix.
        var existing = _turns.ToDictionary(turn => turn.Node);
        var position = 0;
        for (var i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            if (node.Message.Role != AuthorRole.User) continue;

            if (position > 0) _turns[position - 1].End = i;
            if (!existing.TryGetValue(node, out var entry)) entry = new Entry(node);
            entry.Start = i;
            entry.End = _nodes.Count;
            if (position < _turns.Count) _turns[position] = entry;
            else _turns.Add(entry);
            position++;
        }
        if (position < _turns.Count) _turns.RemoveRange(position, _turns.Count - position);
    }

    /// <summary>
    /// Enumerates only the messages belonging to this indexed turn.
    /// </summary>
    public IEnumerable<ChatMessage> GetMessages(Entry entry)
    {
        for (var i = entry.Start; i < entry.End; i++) yield return _nodes[i].Message;
    }

    /// <inheritdoc />
    public void Dispose() => _subscription.Dispose();

    /// <summary>
    /// A stable user-node identity and its current range in the visible branch.
    /// </summary>
    public sealed class Entry
    {
        /// <summary>
        /// Gets the user message to reveal when the turn is activated.
        /// </summary>
        public ChatMessageNode Node { get; }

        internal int Start { get; set; }
        internal int End { get; set; }

        internal Entry(ChatMessageNode node) => Node = node;
    }
}
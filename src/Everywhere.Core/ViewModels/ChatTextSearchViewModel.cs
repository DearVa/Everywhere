using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat;
using Everywhere.Collections;
using Everywhere.Views;
using LiveMarkdown.Avalonia;
using Serilog;

namespace Everywhere.ViewModels;

/// <summary>
/// Coordinates text search across the stable presentation rows of the current conversation.
/// Markdown parsing is cached by source identity and committed content version,
/// independently from the active query, so changing a query never reparses unchanged messages.
/// Parsing and range matching run on immutable snapshots away from the UI thread.
/// </summary>
public sealed partial class ChatTextSearchViewModel : ObservableObject, IDisposable
{
    public TextSearchPattern? ActivePattern { get; private set; }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string? Query { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMatches))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchResultCountText))]
    public partial int CurrentIndex { get; private set; } = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchResultCountText))]
    [NotifyPropertyChangedFor(nameof(HasMatches))]
    public partial int MatchCount { get; private set; }

    public string SearchResultCountText => MatchCount == 0 ? "0/0" : $"{CurrentIndex + 1}/{MatchCount}";

    public bool HasMatches => !IsBusy && MatchCount > 0;

    /// <summary>
    /// Raised when realized surfaces must apply or clear the active search pattern.
    /// </summary>
    public event EventHandler? VisualStateChanged;

    /// <summary>
    /// Raised when realized surfaces only need to move the current-match highlight.
    /// </summary>
    internal event EventHandler? CurrentMatchChanged;

    /// <summary>
    /// Raised when the selected match should be brought into the viewport.
    /// </summary>
    public event EventHandler? NavigationRequested;

    /// <summary>
    /// Raised when the search input should receive keyboard focus.
    /// </summary>
    public event EventHandler? FocusRequested;

    private readonly IChatContextManager chatContextManager;
    private readonly List<RowState?> rowStates = [];
    private readonly Dictionary<ChatPresentationRow, RowState> statesByRow = new(ReferenceEqualityComparer.Instance);
    private readonly List<ChatTextSearchMatch> matches = [];

    private IReadOnlyBindableList<ChatPresentationRow>? _rows;
    private CancellationTokenSource? _projectionCancellation;
    private CancellationTokenSource? _matchCancellation;
    private long _searchGeneration;
    private long _projectionOperation;
    private long _matchingGeneration = -1;
    private long _publishedGeneration = -1;
    private bool _projectionRefreshRunning;
    private bool _isDisposed;

    public ChatTextSearchViewModel(IChatContextManager chatContextManager)
    {
        this.chatContextManager = chatContextManager;
        chatContextManager.PropertyChanged += HandleChatContextManagerPropertyChanged;
        AttachContext(chatContextManager.Current);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        chatContextManager.PropertyChanged -= HandleChatContextManagerPropertyChanged;
        DetachRows();
        CancelProjectionRefresh();
        CancelMatch();
    }

    /// <summary>
    /// Accepts a projection produced by an attached renderer when it represents the current source
    /// object and committed version for the row.
    /// </summary>
    internal void AcceptRenderedProjection(ChatPresentationRow row, ObservableStringBuilder source, MarkdownTextProjection projection)
    {
        Dispatcher.UIThread.VerifyAccess();

        if (!statesByRow.TryGetValue(row, out var state) || !state.AcceptRenderedProjection(source, projection)) return;
        RestartMatchingForProjectionChange();
    }

    internal int GetCurrentLocalIndex(ChatPresentationRow row)
    {
        var current = GetCurrentMatch();
        return current is { } match && ReferenceEquals(match.Row, row) ? match.LocalIndex : -1;
    }

    internal ChatTextSearchMatch? GetCurrentMatch() =>
        !IsBusy && CurrentIndex >= 0 && CurrentIndex < matches.Count ? matches[CurrentIndex] : null;

    [RelayCommand]
    private void OpenSearch()
    {
        IsOpen = true;
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CloseSearch() => IsOpen = false;

    [RelayCommand]
    private void PreviousResult() => MoveCurrent(-1);

    [RelayCommand]
    private void NextResult() => MoveCurrent(1);

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            StartRefresh(clearMatches: true);
            return;
        }

        CancelProjectionRefresh();
        CancelMatch();
        _searchGeneration++;
        ActivePattern = null;
        IsBusy = false;
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
        ReplaceMatches([]);
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnQueryChanged(string? value)
    {
        if (!IsOpen) return;
        StartRefresh(clearMatches: true);
    }

    private void HandleChatContextManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IChatContextManager.Current))
        {
            var context = chatContextManager.Current;
            Dispatcher.UIThread.PostOnDemand(() =>
            {
                if (ReferenceEquals(context, chatContextManager.Current))
                {
                    AttachContext(context);
                }
            });
        }
    }

    private void AttachContext(ChatContext? context)
    {
        Dispatcher.UIThread.VerifyAccess();
        CancelProjectionRefresh();
        CancelMatch();
        DetachRows();

        if (context is not null)
        {
            _rows = context.Presentation.Rows;
            _rows.CollectionChanged += HandleRowsCollectionChanged;
            ReconcileRows();
        }

        StartRefresh(clearMatches: true);
    }

    private void DetachRows()
    {
        if (_rows is not null)
        {
            _rows.CollectionChanged -= HandleRowsCollectionChanged;
            _rows = null;
        }

        foreach (var state in statesByRow.Values)
        {
            state.Dispose();
        }

        statesByRow.Clear();
        rowStates.Clear();
    }

    private void HandleRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ReconcileRows();
        StartRefresh(clearMatches: false);
    }

    private void ReconcileRows()
    {
        if (_rows is null) return;

        var retained = new HashSet<ChatPresentationRow>(ReferenceEqualityComparer.Instance);
        var nextStates = new List<RowState?>(_rows.Count);
        foreach (var row in _rows)
        {
            retained.Add(row);
            if (!statesByRow.TryGetValue(row, out var state))
            {
                state = RowState.Create(row, HandleRowContentChanged);
                if (state is not null)
                {
                    statesByRow.Add(row, state);
                }
            }

            nextStates.Add(state);
        }

        foreach (var (row, state) in statesByRow.ToArray())
        {
            if (retained.Contains(row)) continue;
            statesByRow.Remove(row);
            state.Dispose();
        }

        rowStates.Clear();
        rowStates.AddRange(nextStates);
    }

    private void HandleRowContentChanged(RowState state)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!statesByRow.TryGetValue(state.Row, out var current) || !ReferenceEquals(current, state)) return;
        StartRefresh(clearMatches: false);
    }

    private void StartRefresh(bool clearMatches)
    {
        CancelMatch();
        _searchGeneration++;

        if (!IsOpen || string.IsNullOrEmpty(Query))
        {
            CancelProjectionRefresh();
            ActivePattern = null;
            IsBusy = false;
            VisualStateChanged?.Invoke(this, EventArgs.Empty);
            ReplaceMatches([]);
            return;
        }

        ActivePattern = new TextSearchPattern(Query);
        IsBusy = true;
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
        if (clearMatches)
        {
            ReplaceMatches([]);
        }

        StartMatchingCurrentSearch();
    }

    /// <summary>
    /// Starts one query-independent projection pass for every source version that is not cached.
    /// Query changes reuse the running pass; only source lifetime changes cancel it.
    /// </summary>
    private void EnsureProjections()
    {
        if (_projectionRefreshRunning || !IsOpen || ActivePattern is null) return;

        var work = new List<ProjectionWork>();
        foreach (var state in rowStates)
        {
            if (state?.TryCreateProjectionWork() is { } item)
            {
                work.Add(item);
            }
        }

        if (work.Count == 0)
        {
            StartMatchingCurrentSearch();
            return;
        }

        _projectionRefreshRunning = true;
        var operation = ++_projectionOperation;
        _projectionCancellation = new CancellationTokenSource();
        RefreshProjectionsAsync(work, operation, _projectionCancellation.Token).Detach();
    }

    private async Task RefreshProjectionsAsync(
        IReadOnlyList<ProjectionWork> work,
        long operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var projections = await Task.Run(
                () =>
                {
                    // A canceled operation may briefly overlap its replacement while Markdig
                    // finishes parsing. Keeping the projector local avoids sharing parser state.
                    var results = new MarkdownTextProjection[work.Count];
                    for (var i = 0; i < work.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        results[i] = ChatTextSearcher.SharedMarkdownTextProjector.Project(work[i].Snapshot, cancellationToken);
                    }

                    return results;
                },
                cancellationToken);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (operation != _projectionOperation || cancellationToken.IsCancellationRequested) return;

                for (var i = 0; i < work.Count; i++)
                {
                    work[i].State.AcceptOffscreenProjection(work[i].Source, projections[i]);
                }

                CompleteProjectionRefresh(operation);
                EnsureProjections();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to build Markdown text projections for chat search.");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (operation != _projectionOperation) return;
                CompleteProjectionRefresh(operation);
                IsBusy = false;
                ReplaceMatches([]);
            });
        }
    }

    private void StartMatchingCurrentSearch()
    {
        if (!IsOpen || ActivePattern is not { } pattern) return;

        foreach (var state in rowStates)
        {
            if (state is { IsProjectionCurrent: false })
            {
                EnsureProjections();
                return;
            }
        }

        var generation = _searchGeneration;
        if (_matchingGeneration == generation || _publishedGeneration == generation) return;

        CancelMatch();
        _matchingGeneration = generation;

        var snapshots = new List<RowSearchSnapshot>();
        foreach (var state in rowStates)
        {
            if (state?.CreateSearchSnapshot() is { } snapshot)
            {
                snapshots.Add(snapshot);
            }
        }

        if (snapshots.Count == 0)
        {
            _matchingGeneration = -1;
            _publishedGeneration = generation;
            IsBusy = false;
            ReplaceMatches([]);
            return;
        }

        _matchCancellation = new CancellationTokenSource();
        MatchAsync(pattern, snapshots, generation, _matchCancellation.Token).Detach();
    }

    private async Task MatchAsync(
        TextSearchPattern pattern,
        IReadOnlyList<RowSearchSnapshot> snapshots,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var nextMatches = await Task.Run(
                () => BuildMatches(pattern, snapshots, cancellationToken),
                cancellationToken);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _searchGeneration || cancellationToken.IsCancellationRequested) return;

                CompleteMatch(generation);
                _publishedGeneration = generation;
                IsBusy = false;
                ReplaceMatches(nextMatches);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to match projected chat text.");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _searchGeneration) return;
                CompleteMatch(generation);
                _publishedGeneration = generation;
                IsBusy = false;
                ReplaceMatches([]);
            });
        }
    }

    private static List<ChatTextSearchMatch> BuildMatches(
        TextSearchPattern pattern,
        IReadOnlyList<RowSearchSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var nextMatches = new List<ChatTextSearchMatch>();
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localIndex = 0;
            if (snapshot.PlainText is { } text)
            {
                foreach (var range in pattern.FindRanges(text))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    nextMatches.Add(new ChatTextSearchMatch(
                        snapshot.Row,
                        0,
                        localIndex++,
                        new TextHighlightRange(
                            range.Start + snapshot.PlainTextOffset,
                            range.Length)));
                }

                continue;
            }

            if (snapshot.Projection is not { } projection) continue;
            for (var bufferIndex = 0; bufferIndex < projection.Buffers.Count; bufferIndex++)
            {
                foreach (var range in pattern.FindRanges(projection.Buffers[bufferIndex].Text))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    nextMatches.Add(new ChatTextSearchMatch(
                        snapshot.Row,
                        bufferIndex,
                        localIndex++,
                        range));
                }
            }
        }

        return nextMatches;
    }

    private void RestartMatchingForProjectionChange()
    {
        if (!IsOpen || ActivePattern is null) return;

        CancelMatch();
        _searchGeneration++;
        IsBusy = true;
        StartMatchingCurrentSearch();
    }

    private void ReplaceMatches(IReadOnlyList<ChatTextSearchMatch> nextMatches)
    {
        var previous = GetCurrentMatch();
        matches.Clear();
        matches.AddRange(nextMatches);
        MatchCount = matches.Count;

        var nextIndex = -1;
        if (matches.Count > 0)
        {
            nextIndex = previous is { } previousMatch ? matches.IndexOf(previousMatch) : 0;
            if (nextIndex < 0) nextIndex = Math.Min(CurrentIndex, matches.Count - 1);
            if (nextIndex < 0) nextIndex = 0;
        }

        CurrentIndex = nextIndex;
        CurrentMatchChanged?.Invoke(this, EventArgs.Empty);
        if (nextIndex >= 0 && (previous is null || !matches[nextIndex].Equals(previous.Value)))
        {
            NavigationRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void MoveCurrent(int delta)
    {
        if (IsBusy || matches.Count == 0) return;

        CurrentIndex = (CurrentIndex + delta + matches.Count) % matches.Count;
        CurrentMatchChanged?.Invoke(this, EventArgs.Empty);
        NavigationRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteProjectionRefresh(long operation)
    {
        if (operation != _projectionOperation) return;
        _projectionRefreshRunning = false;
        _projectionCancellation?.Dispose();
        _projectionCancellation = null;
    }

    private void CancelProjectionRefresh()
    {
        _projectionOperation++;
        _projectionRefreshRunning = false;
        _projectionCancellation?.Cancel();
        _projectionCancellation?.Dispose();
        _projectionCancellation = null;
    }

    private void CompleteMatch(long generation)
    {
        if (_matchingGeneration != generation) return;
        _matchingGeneration = -1;
        _matchCancellation?.Dispose();
        _matchCancellation = null;
    }

    private void CancelMatch()
    {
        _matchingGeneration = -1;
        _matchCancellation?.Cancel();
        _matchCancellation?.Dispose();
        _matchCancellation = null;
    }

    internal readonly record struct ChatTextSearchMatch(
        ChatPresentationRow Row,
        int BufferIndex,
        int LocalIndex,
        TextHighlightRange Range
    );

    private readonly record struct ProjectionWork(
        RowState State,
        ObservableStringBuilder Source,
        ObservableStringBuilderSnapshot Snapshot
    );

    /// <summary>
    /// Captures immutable row search input on the UI thread for background matching.
    /// </summary>
    private readonly record struct RowSearchSnapshot(
        ChatPresentationRow Row,
        string? PlainText,
        int PlainTextOffset,
        MarkdownTextProjection? Projection
    );

    private sealed class RowState : IDisposable
    {
        public ChatPresentationRow Row { get; }

        public bool IsProjectionCurrent => _markdownSource is null || _projection?.SourceVersion == _markdownSource.Version;

        private readonly Action<RowState> _contentChanged;
        private readonly UserChatMessage? _userMessage;
        private readonly ObservableStringBuilder? _markdownSource;
        private MarkdownTextProjection? _projection;

        private RowState(
            ChatPresentationRow row,
            Action<RowState> contentChanged,
            UserChatMessage? userMessage,
            ObservableStringBuilder? markdownSource)
        {
            Row = row;
            _contentChanged = contentChanged;
            _userMessage = userMessage;
            _markdownSource = markdownSource;

            if (userMessage is not null)
            {
                userMessage.PropertyChanged += HandleUserMessagePropertyChanged;
            }

            if (markdownSource is not null)
            {
                markdownSource.Changed += HandleMarkdownSourceChanged;
            }
        }

        public static RowState? Create(ChatPresentationRow row, Action<RowState> contentChanged) => row switch
        {
            ChatMessagePresentationRow { Node.Message: UserChatMessage userMessage } =>
                new RowState(row, contentChanged, userMessage, null),
            AssistantOutputPresentationRow { Span: AssistantChatMessageTextSpan textSpan } =>
                new RowState(row, contentChanged, null, textSpan.ContentMarkdownBuilder),
            _ => null,
        };

        public ProjectionWork? TryCreateProjectionWork()
        {
            if (_markdownSource is null || IsProjectionCurrent) return null;
            return new ProjectionWork(this, _markdownSource, _markdownSource.CaptureSnapshot());
        }

        public bool AcceptRenderedProjection(ObservableStringBuilder source, MarkdownTextProjection value)
        {
            if (!ReferenceEquals(_markdownSource, source) || source.Version != value.SourceVersion) return false;
            if (ReferenceEquals(_projection, value)) return false;
            _projection = value;
            return true;
        }

        public void AcceptOffscreenProjection(ObservableStringBuilder source, MarkdownTextProjection value)
        {
            if (!ReferenceEquals(_markdownSource, source) || source.Version != value.SourceVersion) return;

            // A projection published by a realized renderer is authoritative for custom nodes.
            // Never replace any already-current projection with the conservative off-screen one.
            if (IsProjectionCurrent) return;

            _projection = value;
        }

        public RowSearchSnapshot? CreateSearchSnapshot()
        {
            if (_userMessage is not null)
            {
                return new RowSearchSnapshot(
                    Row,
                    _userMessage.Content,
                    _userMessage is UserStrategyChatMessage ? 1 : 0,
                    null);
            }

            return IsProjectionCurrent && _projection is not null
                ? new RowSearchSnapshot(Row, null, 0, _projection)
                : null;
        }

        public void Dispose()
        {
            if (_userMessage is not null)
            {
                _userMessage.PropertyChanged -= HandleUserMessagePropertyChanged;
            }

            if (_markdownSource is not null)
            {
                _markdownSource.Changed -= HandleMarkdownSourceChanged;
            }
        }

        private void HandleUserMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(UserChatMessage.Content))
            {
                _contentChanged(this);
            }
        }

        private void HandleMarkdownSourceChanged(in ObservableStringBuilderChangedEventArgs e) => _contentChanged(this);
    }
}
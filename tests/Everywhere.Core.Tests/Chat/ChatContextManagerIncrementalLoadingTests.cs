using System.Runtime.CompilerServices;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Everywhere.Chat;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Storage;
using LiveMarkdown.Avalonia;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.Chat;

[TestFixture]
public sealed class ChatContextManagerIncrementalLoadingTests
{
    [AvaloniaTest]
    public async Task LoadMoreAsync_WithTitleFilter_ReturnsRequestedFinalMatchCount()
    {
        var storage = new TestChatContextStorage(
        [
            Metadata("skip one", 1),
            Metadata("match one", 2),
            Metadata("skip two", 3),
            Metadata("match two", 4),
            Metadata("match three", 5)
        ]);
        using var manager = CreateManager(storage);
        manager.HistorySearchQuery = "match";

        using var session = manager.BeginLoadSession();
        var result = await session.LoadMoreAsync(2);
        await PumpDispatcherAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.AddedItemCount, Is.EqualTo(2));
            Assert.That(result.HasMoreItems, Is.True);
            Assert.That(Flatten(manager).Select(metadata => metadata.Topic),
                Is.EqualTo(new[] { "match one", "match two" }));
        });
    }

    [AvaloniaTest]
    public async Task LoadMoreAsync_WithContentSearch_SkipsToolContentAndStopsAfterPageIsFull()
    {
        var toolOnly = Metadata("tool", 1);
        var userMatch = Metadata("user", 2);
        var laterMatch = Metadata("later", 3);
        var storage = new TestChatContextStorage([toolOnly, userMatch, laterMatch]);
        storage.Contexts[toolOnly.Id] = Context(
            new FunctionCallChatMessage(LucideIconKind.Hammer, new DirectLocaleKey("Tool"))
            {
                Content = "needle"
            });
        storage.Contexts[userMatch.Id] = Context(new UserChatMessage("contains needle", []));
        storage.Contexts[laterMatch.Id] = Context(new UserChatMessage("also contains needle", []));
        using var manager = CreateManager(storage);
        manager.HistorySearchIncludesContent = true;
        manager.HistorySearchQuery = "needle";

        using var session = manager.BeginLoadSession();
        var result = await session.LoadMoreAsync(1);
        await PumpDispatcherAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.AddedItemCount, Is.EqualTo(1));
            Assert.That(Flatten(manager), Is.EqualTo(new[] { userMatch }));
            Assert.That(storage.LoadedContextIds, Is.EqualTo(new[] { toolOnly.Id, userMatch.Id }));
        });
    }

    [AvaloniaTest]
    public async Task HistorySearchQuery_WhenChanged_ClearsResultsAndRetargetsSameSession()
    {
        var alpha = Metadata("alpha", 1);
        var beta = Metadata("beta", 2);
        var storage = new TestChatContextStorage([alpha, beta]);
        using var manager = CreateManager(storage);
        manager.HistorySearchQuery = "alpha";
        using var session = manager.BeginLoadSession();

        await session.LoadMoreAsync(1);
        manager.HistorySearchQuery = "beta";
        await PumpDispatcherAsync();
        Assert.That(Flatten(manager), Is.Empty);

        await session.LoadMoreAsync(1);
        await PumpDispatcherAsync();

        Assert.That(Flatten(manager), Is.EqualTo(new[] { beta }));
    }

    [AvaloniaTest]
    public async Task HistorySearchQuery_WhenChangedDuringLoad_RetargetsInFlightRequest()
    {
        var first = Metadata("unrelated one", 1);
        var second = Metadata("unrelated two", 2);
        var storage = new TestChatContextStorage([first, second]);
        storage.Contexts[first.Id] = Context(new UserChatMessage("alpha", []));
        storage.Contexts[second.Id] = Context(new UserChatMessage("beta", []));
        var firstLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAttempt = 0;
        storage.ContextLoader = async (id, cancellationToken) =>
        {
            if (id == first.Id && Interlocked.Increment(ref firstAttempt) == 1)
            {
                firstLoadStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return storage.Contexts[id];
        };
        using var manager = CreateManager(storage);
        manager.HistorySearchIncludesContent = true;
        manager.HistorySearchQuery = "alpha";
        using var session = manager.BeginLoadSession();

        var load = session.LoadMoreAsync(1).AsTask();
        await firstLoadStarted.Task;
        manager.HistorySearchQuery = "beta";
        var result = await load;
        await PumpDispatcherAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.AddedItemCount, Is.EqualTo(1));
            Assert.That(Flatten(manager), Is.EqualTo(new[] { second }));
        });
    }

    [AvaloniaTest]
    public async Task LoadSession_AcrossMultipleRequests_KeepsManagerBusyUntilDisposed()
    {
        var storage = new TestChatContextStorage([Metadata("one", 1), Metadata("two", 2)]);
        using var manager = CreateManager(storage);
        var session = manager.BeginLoadSession();

        Assert.That(manager.IsBusy, Is.True);
        await session.LoadMoreAsync(1);
        await session.LoadMoreAsync(1);
        Assert.That(manager.IsBusy, Is.True);

        session.Dispose();
        Assert.That(manager.IsBusy, Is.False);
    }

    [Test]
    public void Contains_WhenOnlyToolContentMatches_ReturnsFalse()
    {
        using var context = Context(
            new FunctionCallChatMessage(LucideIconKind.Hammer, new DirectLocaleKey("Tool"))
            {
                Content = "needle"
            });

        Assert.That(
            ChatTextSearcher.Contains(
                context,
                new TextSearchPattern("needle"),
                new MarkdownTextProjector(),
                CancellationToken.None),
            Is.False);
    }

    [Test]
    public void Contains_WhenAssistantTextIsSplitByMarkdownFormatting_MatchesVisualText()
    {
        using var context = Context(Assistant("Hel**lo**"));

        Assert.That(
            ChatTextSearcher.Contains(
                context,
                new TextSearchPattern("Hello"),
                new MarkdownTextProjector(),
                CancellationToken.None),
            Is.True);
    }

    [Test]
    public void Contains_WhenOnlyMarkdownLinkDestinationMatches_ReturnsFalse()
    {
        using var context = Context(Assistant("[visible](https://hidden.example)"));

        Assert.That(
            ChatTextSearcher.Contains(
                context,
                new TextSearchPattern("hidden.example"),
                new MarkdownTextProjector(),
                CancellationToken.None),
            Is.False);
    }

    private static ChatContextManager CreateManager(IChatContextStorage storage) =>
        new(
            new Settings(new ServiceCollection().BuildServiceProvider()),
            storage,
            NullLogger<ChatContextManager>.Instance);

    private static ChatContextMetadata[] Flatten(ChatContextManager manager) =>
        manager.AllHistory.SelectMany(group => group.MetadataList).ToArray();

    private static ChatContextMetadata Metadata(string topic, int minutesAgo)
    {
        var modified = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);
        return new ChatContextMetadata(Guid.CreateVersion7(), modified, modified, topic);
    }

    private static ChatContext Context(ChatMessage message)
    {
        var context = new ChatContext();
        context.Add(message);
        return context;
    }

    private static AssistantChatMessage Assistant(string markdown)
    {
        var message = new AssistantChatMessage();
        message.AddSpan(new AssistantChatMessageTextSpan(markdown));
        return message;
    }

    private static async Task PumpDispatcherAsync()
    {
        await Task.Yield();
        await Dispatcher.UIThread.InvokeAsync(static () => { });
    }

    private sealed class TestChatContextStorage(IReadOnlyList<ChatContextMetadata> metadata) : IChatContextStorage
    {
        public Dictionary<Guid, ChatContext> Contexts { get; } = [];

        public List<Guid> LoadedContextIds { get; } = [];

        public Func<Guid, CancellationToken, Task<ChatContext>>? ContextLoader { get; set; }

        public Task DeleteChatContextsAsync(
            IEnumerable<Guid> chatContextIds,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RestoreChatContextsAsync(
            IEnumerable<Guid> chatContextIds,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<ChatContextMetadata> QueryChatContextsAsync(
            int take,
            ChatContextOrderBy orderBy,
            bool descending,
            Guid? startAfterId = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var startIndex = startAfterId is { } cursor
                ? metadata.Select(item => item.Id).ToList().IndexOf(cursor) + 1
                : 0;
            foreach (var item in metadata.Skip(startIndex).Take(take))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }

        public Task<ChatContext> GetChatContextAsync(
            Guid chatContextId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadedContextIds.Add(chatContextId);
            return ContextLoader?.Invoke(chatContextId, cancellationToken) ??
                   Task.FromResult(Contexts[chatContextId]);
        }

        public Task SaveChatContextAsync(
            ChatContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveChatContextMetadataAsync(
            ChatContextMetadata metadata,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

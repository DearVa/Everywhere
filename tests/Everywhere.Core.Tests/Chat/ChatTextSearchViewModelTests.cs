using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Everywhere.Chat;
using Everywhere.ViewModels;
using Everywhere.Views;
using LiveMarkdown.Avalonia;
using NSubstitute;

namespace Everywhere.Core.Tests.Chat;

[TestFixture]
public sealed class ChatTextSearchViewModelTests
{
    [AvaloniaTest]
    public async Task Search_WhenAssistantFormattingSplitsVisibleText_CountsOffscreenMatch()
    {
        using var context = new ChatContext();
        var assistant = new AssistantChatMessage();
        assistant.AddSpan(new AssistantChatMessageTextSpan("Hel**lo**"));
        context.Add(assistant);
        var manager = CreateManager(context);
        using var viewModel = new ChatTextSearchViewModel(manager) { Query = "Hello" };

        viewModel.OpenSearchCommand.Execute(null);
        await WaitForSearchAsync(viewModel);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.MatchCount, Is.EqualTo(1));
            Assert.That(viewModel.CurrentIndex, Is.EqualTo(0));
            Assert.That(viewModel.GetCurrentMatch()?.Span, Is.TypeOf<AssistantChatMessageTextSpan>());
        });
    }

    [AvaloniaTest]
    public async Task QueryChange_WhileBackgroundMatchIsPending_DoesNotExposePreviousNavigation()
    {
        using var context = new ChatContext();
        context.Add(new UserChatMessage("alpha beta", []));
        var manager = CreateManager(context);
        using var viewModel = new ChatTextSearchViewModel(manager) { Query = "alpha" };
        viewModel.OpenSearchCommand.Execute(null);
        await WaitForSearchAsync(viewModel);
        Assert.That(viewModel.HasMatches, Is.True);

        viewModel.Query = "beta";

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsBusy, Is.True);
            Assert.That(viewModel.HasMatches, Is.False);
            Assert.That(viewModel.MatchCount, Is.Zero);
            Assert.That(viewModel.GetCurrentMatch(), Is.Null);
        });

        await WaitForSearchAsync(viewModel);
        Assert.That(viewModel.MatchCount, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task RenderedProjection_WhenOffscreenWorkIsPending_RemainsAuthoritative()
    {
        using var context = new ChatContext();
        var assistant = new AssistantChatMessage();
        var span = new AssistantChatMessageTextSpan("offscreen text");
        assistant.AddSpan(span);
        context.Add(assistant);
        var row = context.Presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single();
        var manager = CreateManager(context);
        using var viewModel = new ChatTextSearchViewModel(manager) { Query = "rendered-only" };

        viewModel.OpenSearchCommand.Execute(null);
        var source = span.ContentMarkdownBuilder;
        var renderedProjection = new MarkdownTextProjector().Project(
            new ObservableStringBuilderSnapshot("rendered-only", source.Version));
        viewModel.AcceptRenderedProjection(row, source, renderedProjection);
        await WaitForSearchAsync(viewModel);

        Assert.That(viewModel.MatchCount, Is.EqualTo(1));

        var navigationRequests = 0;
        viewModel.NavigationRequested += (_, _) => navigationRequests++;
        var equivalentProjection = new MarkdownTextProjector().Project(
            new ObservableStringBuilderSnapshot("rendered-only", source.Version));
        viewModel.AcceptRenderedProjection(row, source, equivalentProjection);
        await WaitForSearchAsync(viewModel);

        Assert.That(navigationRequests, Is.Zero);
    }

    [AvaloniaTest]
    public async Task Search_WhenMatchPrecedesPresentationWindow_ReturnsLogicalModelTarget()
    {
        using var context = new ChatContext();
        ChatMessageNode? earliestNode = null;
        for (var index = 0; index < ChatPresentation.TurnBatchSize + 3; index++)
        {
            context.Add(new UserChatMessage(index == 0 ? "needle in old history" : $"Question {index}", []));
            earliestNode ??= context.Items[^1];
            var assistant = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
            assistant.AddSpan(new AssistantChatMessageTextSpan($"Answer {index}") { FinishedAt = DateTimeOffset.UtcNow });
            context.Add(assistant);
        }

        Assert.That(
            context.Presentation.Rows.OfType<ChatMessagePresentationRow>().Any(row => ReferenceEquals(row.Node, earliestNode)),
            Is.False);

        var manager = CreateManager(context);
        using var viewModel = new ChatTextSearchViewModel(manager) { Query = "needle" };
        viewModel.OpenSearchCommand.Execute(null);
        await WaitForSearchAsync(viewModel);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.MatchCount, Is.EqualTo(1));
            Assert.That(viewModel.GetCurrentMatch()?.Node, Is.SameAs(earliestNode));
            Assert.That(viewModel.GetCurrentMatch()?.Span, Is.Null);
        });
    }

    [AvaloniaTest]
    public async Task Search_WhenAssistantAddsTextSpan_TracksNewLogicalSource()
    {
        using var context = new ChatContext();
        var assistant = new AssistantChatMessage { IsBusy = true };
        context.Add(assistant);
        var manager = CreateManager(context);
        using var viewModel = new ChatTextSearchViewModel(manager) { Query = "new output" };
        viewModel.OpenSearchCommand.Execute(null);
        await WaitForSearchAsync(viewModel);
        Assert.That(viewModel.MatchCount, Is.Zero);

        var span = new AssistantChatMessageTextSpan("new output");
        assistant.AddSpan(span);
        await WaitForSearchAsync(viewModel);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.MatchCount, Is.EqualTo(1));
            Assert.That(viewModel.GetCurrentMatch()?.Span, Is.SameAs(span));
        });
    }

    [AvaloniaTest]
    public async Task NextResult_WhenOnlyOneMatch_RequestsNavigationWithoutRepublishingCurrentMatch()
    {
        using var context = new ChatContext();
        context.Add(new UserChatMessage("only needle", []));
        var manager = CreateManager(context);
        using var viewModel = new ChatTextSearchViewModel(manager) { Query = "needle" };
        viewModel.OpenSearchCommand.Execute(null);
        await WaitForSearchAsync(viewModel);

        var currentMatchChanges = 0;
        var navigationRequests = 0;
        viewModel.CurrentMatchChanged += (_, _) => currentMatchChanges++;
        viewModel.NavigationRequested += (_, _) => navigationRequests++;

        viewModel.NextResultCommand.Execute(null);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.CurrentIndex, Is.Zero);
            Assert.That(currentMatchChanges, Is.Zero);
            Assert.That(navigationRequests, Is.EqualTo(1));
        });
    }

    private static IChatContextManager CreateManager(ChatContext context)
    {
        var manager = Substitute.For<IChatContextManager>();
        manager.Current.Returns(context);
        return manager;
    }

    private static async Task WaitForSearchAsync(ChatTextSearchViewModel viewModel)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(10);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            if (!viewModel.IsBusy) return;
        }

        Assert.Fail("The chat text search did not complete in time.");
    }
}

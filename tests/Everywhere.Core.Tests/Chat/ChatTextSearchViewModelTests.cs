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
            Assert.That(viewModel.GetCurrentMatch()?.Row, Is.TypeOf<AssistantOutputPresentationRow>());
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
        var row = context.Presentation.Rows.OfType<AssistantOutputPresentationRow>().Single();
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

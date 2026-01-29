using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Everywhere.Common;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.Logging;

namespace Everywhere.Views;

public partial class ChangeLogView : ReactiveUserControl<ChangeLogViewModel>
{
    public static readonly DirectProperty<ChangeLogView, ObservableStringBuilder> MarkdownBuilderProperty =
        AvaloniaProperty.RegisterDirect<ChangeLogView, ObservableStringBuilder>(
            nameof(MarkdownBuilder),
            o => o.MarkdownBuilder);

    public ObservableStringBuilder MarkdownBuilder { get; } = new();

    public ChangeLogView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        try
        {
            MarkdownBuilder.Clear();

            using var changeLogReader = new StreamReader(AssetLoader.Open(new Uri("avares://Everywhere.Core/Assets/CHANGELOG.md", UriKind.Absolute)));

            var maxLines = 100;
            while (changeLogReader.ReadLine() is { } line && maxLines-- > 0)
            {
                MarkdownBuilder.AppendLine(line);
            }

            if (maxLines == 0)
            {
                MarkdownBuilder.AppendLine("... (truncated)");
            }
        }
        catch (Exception ex)
        {
            ServiceLocator.Resolve<ILogger<ChangeLogView>>().LogError(ex, "Failed to load changelog.");
        }
    }

    private void HandleMarkdownRendererLinkClick(object? sender, LinkClickedEventArgs e)
    {
        if (e.HRef is not { IsAbsoluteUri: true, Scheme: "https" or "http" } href) return;

        TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(href);
    }
}
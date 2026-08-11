using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;

namespace Everywhere.Views;

/// <summary>
/// Summarizes one file operation and opens detailed review while hosted by a consent card.
/// </summary>
public partial class TextDifferenceSummaryView : TemplatedControl
{
    public static readonly StyledProperty<TextDifference?> TextDifferenceProperty =
        AvaloniaProperty.Register<TextDifferenceSummaryView, TextDifference?>(nameof(TextDifference));

    public TextDifference? TextDifference
    {
        get => GetValue(TextDifferenceProperty);
        set => SetValue(TextDifferenceProperty, value);
    }

    public static readonly StyledProperty<string?> OriginalTextProperty =
        AvaloniaProperty.Register<TextDifferenceSummaryView, string?>(nameof(OriginalText));

    public string? OriginalText
    {
        get => GetValue(OriginalTextProperty);
        set => SetValue(OriginalTextProperty, value);
    }

    public static readonly StyledProperty<string?> FilePathProperty =
        AvaloniaProperty.Register<TextDifferenceSummaryView, string?>(nameof(FilePath));

    public string? FilePath
    {
        get => GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    public static readonly StyledProperty<TextDifferenceReviewKind> ReviewKindProperty =
        AvaloniaProperty.Register<TextDifferenceSummaryView, TextDifferenceReviewKind>(nameof(ReviewKind));

    public TextDifferenceReviewKind ReviewKind
    {
        get => GetValue(ReviewKindProperty);
        set => SetValue(ReviewKindProperty, value);
    }

    public static readonly StyledProperty<string?> SourcePathProperty =
        AvaloniaProperty.Register<TextDifferenceSummaryView, string?>(nameof(SourcePath));

    public string? SourcePath
    {
        get => GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public static readonly StyledProperty<int> AddedLineCountProperty =
        AvaloniaProperty.Register<TextDifferenceSummaryView, int>(nameof(AddedLineCount));

    public int AddedLineCount
    {
        get => GetValue(AddedLineCountProperty);
        set => SetValue(AddedLineCountProperty, value);
    }

    public static readonly StyledProperty<int> RemovedLineCountProperty =
        AvaloniaProperty.Register<TextDifferenceSummaryView, int>(nameof(RemovedLineCount));

    public int RemovedLineCount
    {
        get => GetValue(RemovedLineCountProperty);
        set => SetValue(RemovedLineCountProperty, value);
    }

    public static readonly StyledProperty<bool> HasLineChangesProperty =
        AvaloniaProperty.Register<TextDifferenceSummaryView, bool>(nameof(HasLineChanges));

    public bool HasLineChanges
    {
        get => GetValue(HasLineChangesProperty);
        set => SetValue(HasLineChangesProperty, value);
    }

    public static readonly StyledProperty<bool> CanReviewProperty =
        AvaloniaProperty.Register<TextDifferenceSummaryView, bool>(nameof(CanReview));

    public bool CanReview
    {
        get => GetValue(CanReviewProperty);
        set => SetValue(CanReviewProperty, value);
    }

    [RelayCommand]
    private Task OpenFileAsync()
    {
        if (FilePath is not { Length: > 0 } filePath ||
            TopLevel.GetTopLevel(this) is not { Launcher: { } launcher } ||
            !Uri.TryCreate(filePath, UriKind.Absolute, out var uri))
        {
            return Task.CompletedTask;
        }

        return launcher.LaunchUriAsync(uri);
    }

    [RelayCommand]
    private void Edit()
    {
        if (!CanReview) return;
        if (TextDifference is not { } textDifference) return;
        if (OriginalText is not { } originalText) return;
        if (this.FindAncestorOfType<ConsentDecisionCard>()?.Command is not { } consentCommand) return;

        var editor = new TextDifferenceEditor
        {
            AddedLineCount = AddedLineCount,
            RemovedLineCount = RemovedLineCount,
            TextDifference = textDifference,
            OriginalText = originalText,
            ShowLineNumbers = true
        };
        var window = new TransientWindow
        {
            [!TransientWindow.TitleBarContentOverrideProperty] = new DynamicLocaleKey(LocaleKey.TextDifferenceEditor_WindowTitle).ToBinding(),
            Content = editor
        };
        editor.ReviewConfirmed += (_, _) =>
        {
            var decision = ConsentDecision.AllowOnce;
            window.Close();
            if (consentCommand.CanExecute(decision)) consentCommand.Execute(decision);
        };

        if (TopLevel.GetTopLevel(this) is Window owner) window.ShowDialog(owner);
        else window.Show();
    }
}
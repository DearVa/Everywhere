using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat.Plugins;
using ShadUI;

namespace Everywhere.Views;

public partial class AskQuestionCard : Card
{
    public sealed partial class OptionWrapper(ChatPluginQuestionOption option) : ObservableObject
    {
        public ChatPluginQuestionOption Option { get; } = option;

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        [RelayCommand]
        private void Select() => IsSelected = true;
    }

    public sealed partial class QuestionWrapper : ObservableObject
    {
        public ChatPluginQuestion Question { get; }
        public SelectionMode SelectionMode { get; }
        public IDynamicLocaleKey? MultiSelectHintKey => Question.MultiSelect ? new DynamicLocaleKey(LocaleKey.ChatPlugin_MultiSelectHint) : null;
        public List<OptionWrapper> OptionWrappers { get; } = [];
        [ObservableProperty] public partial string? FreeformText { get; set; }

        public QuestionWrapper(ChatPluginQuestion question)
        {
            Question = question;
            SelectionMode = question.MultiSelect ? SelectionMode.Multiple | SelectionMode.Toggle : SelectionMode.Single | SelectionMode.Toggle;

            var hasPreSelected = false;
            if (question.Options is not null)
            {
                foreach (var option in question.Options)
                {
                    var isSelected = option.Recommended && (question.MultiSelect || !hasPreSelected);
                    if (isSelected) hasPreSelected = true;

                    OptionWrappers.Add(new OptionWrapper(option) { IsSelected = isSelected });
                }
            }
        }
    }

    #region Properties

    public static readonly StyledProperty<IReadOnlyList<ChatPluginQuestion>?> QuestionsProperty =
        AvaloniaProperty.Register<AskQuestionCard, IReadOnlyList<ChatPluginQuestion>?>(nameof(Questions));

    public IReadOnlyList<ChatPluginQuestion>? Questions
    {
        get => GetValue(QuestionsProperty);
        set => SetValue(QuestionsProperty, value);
    }

    public static readonly StyledProperty<IRelayCommand<IReadOnlyList<ChatPluginQuestionAnswer>>?> CommandProperty =
        AvaloniaProperty.Register<AskQuestionCard, IRelayCommand<IReadOnlyList<ChatPluginQuestionAnswer>>?>(nameof(Command));

    public IRelayCommand<IReadOnlyList<ChatPluginQuestionAnswer>>? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public static readonly DirectProperty<AskQuestionCard, IReadOnlyList<QuestionWrapper>?> WrappedQuestionsProperty =
        AvaloniaProperty.RegisterDirect<AskQuestionCard, IReadOnlyList<QuestionWrapper>?>(
            nameof(WrappedQuestions),
            o => o.WrappedQuestions);

    public IReadOnlyList<QuestionWrapper>? WrappedQuestions
    {
        get;
        private set => SetAndRaise(WrappedQuestionsProperty, ref field, value);
    }

    public static readonly DirectProperty<AskQuestionCard, QuestionWrapper?> CurrentQuestionProperty =
        AvaloniaProperty.RegisterDirect<AskQuestionCard, QuestionWrapper?>(
            nameof(CurrentQuestion),
            o => o.CurrentQuestion);

    public QuestionWrapper? CurrentQuestion
    {
        get;
        private set => SetAndRaise(CurrentQuestionProperty, ref field, value);
    }

    public static readonly DirectProperty<AskQuestionCard, int> CurrentIndexProperty =
        AvaloniaProperty.RegisterDirect<AskQuestionCard, int>(
            nameof(CurrentIndex),
            o => o.CurrentIndex);

    public int CurrentIndex
    {
        get;
        private set => SetAndRaise(CurrentIndexProperty, ref field, value);
    }

    public static readonly DirectProperty<AskQuestionCard, bool> HasPreviousPageProperty =
        AvaloniaProperty.RegisterDirect<AskQuestionCard, bool>(
            nameof(HasPreviousPage),
            o => o.HasPreviousPage);

    public bool HasPreviousPage
    {
        get;
        private set => SetAndRaise(HasPreviousPageProperty, ref field, value);
    }

    public static readonly DirectProperty<AskQuestionCard, bool> HasNextPageProperty =
        AvaloniaProperty.RegisterDirect<AskQuestionCard, bool>(
            nameof(HasNextPage),
            o => o.HasNextPage);

    public bool HasNextPage
    {
        get;
        private set => SetAndRaise(HasNextPageProperty, ref field, value);
    }

    #endregion

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != QuestionsProperty) return;

        var questions = change.GetNewValue<IReadOnlyList<ChatPluginQuestion>?>();
        if (questions is null or { Count: 0 })
        {
            WrappedQuestions = null;
            CurrentQuestion = null;
            return;
        }

        WrappedQuestions = questions.AsValueEnumerable().Select(q => new QuestionWrapper(q)).ToArray();
        NavigateTo(0);
    }

    private void NavigateTo(int index)
    {
        if (WrappedQuestions is null) return;

        CurrentIndex = index;
        CurrentQuestion = WrappedQuestions[index];
        HasPreviousPage = CurrentIndex > 0;
        HasNextPage = CurrentIndex < WrappedQuestions.Count - 1;
        PreviousPageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasPreviousPage))]
    private void PreviousPage()
    {
        if (CurrentIndex > 0) NavigateTo(CurrentIndex - 1);
    }

    [RelayCommand]
    private void NextPage()
    {
        if (WrappedQuestions is null) return;

        if (CurrentIndex < WrappedQuestions.Count - 1)
        {
            NavigateTo(CurrentIndex + 1);
        }
        else if (Command is { } command)
        {
            var answers = new ChatPluginQuestionAnswer[WrappedQuestions.Count];
            for (var i = 0; i < WrappedQuestions.Count; i++)
            {
                var question = WrappedQuestions[i];
                var selected = question.OptionWrappers.AsValueEnumerable().Where(x => x.IsSelected).Select(x => x.Option.Content).ToArray();
                answers[i] = new ChatPluginQuestionAnswer(selected, question.FreeformText);
            }

            if (command.CanExecute(answers))
            {
                command.Execute(answers);
            }
        }
    }
}
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.ViewModels;

public sealed partial class WelcomeViewModel : BusyViewModelBase
{
    [ObservableProperty]
    public partial WelcomeViewModelStep? CurrentStep { get; private set; }

    [ObservableProperty]
    public partial bool IsPageTransitionReversed { get; private set; }

    /// <summary>
    /// Only allow skipping if there are more steps ahead.
    /// </summary>
    public bool CanSkipAll => _currentStepIndex < _steps.Count - 1;

    /// <summary>
    /// Only allow moving next if there are more steps ahead.
    /// </summary>
    public bool CanMoveNext => _currentStepIndex < _steps.Count - 1;

    /// <summary>
    /// Only allow moving previous if there are steps behind.
    /// </summary>
    public bool CanMovePrevious => _currentStepIndex > 0;

    public Settings Settings { get; }

    public CustomAssistant Assistant { get; }

    [ObservableProperty]
    public partial bool IsConnectivityChecked { get; set; }


    private readonly IReadOnlyList<WelcomeViewModelStep> _steps;
    private int _currentStepIndex;

    public WelcomeViewModel(IServiceProvider serviceProvider)
    {
        Settings = serviceProvider.GetRequiredService<Settings>();

        // Initialize a default custom assistant
        Assistant = new CustomAssistant
        {
            Name = LocaleResolver.CustomAssistant_Name_Default,
            ConfiguratorType = ModelProviderConfiguratorType.PresetBased
        };
        Assistant.PropertyChanged += delegate
        {
            // Reset connectivity check when assistant configuration changes
            if (IsNotBusy) IsConnectivityChecked = false;
        };

        _steps =
        [
            new WelcomeViewModelIntroStep(this),
            new WelcomeViewModelConfiguratorStep(this),
            new WelcomeViewModelAssistantStep(this, serviceProvider),
            new WelcomeViewModelShortcutStep(this),
            new WelcomeViewModelTelemetryStep(this)
        ];

        CurrentStep = _steps[0];
    }

    [RelayCommand(CanExecute = nameof(CanMoveNext))]
    private void MoveNext()
    {
        IsPageTransitionReversed = false;
        _currentStepIndex++;
        CurrentStep = _steps[_currentStepIndex];
    }

    [RelayCommand(CanExecute = nameof(CanMovePrevious))]
    private void MovePrevious()
    {
        IsPageTransitionReversed = true;
        _currentStepIndex--;
        CurrentStep = _steps[_currentStepIndex];
    }

    [RelayCommand]
    public void Close()
    {
        if (IsConnectivityChecked)
        {
            // Save the configured assistant
            Settings.Model.CustomAssistants.Add(Assistant);
        }

        CurrentStep?.CancellationTokenSource.Cancel();
        DialogManager.CloseAll();
    }
}

/// <summary>
/// The base class for welcome view model steps.
/// All step must inherit from this class.
/// When switching steps, the View layer will select the appropriate DataTemplate based on the step type.
/// </summary>
/// <param name="viewModel"></param>
public abstract class WelcomeViewModelStep(WelcomeViewModel viewModel) : BusyViewModelBase
{
    public WelcomeViewModel ViewModel { get; } = viewModel;

    public ReusableCancellationTokenSource CancellationTokenSource { get; } = new();
}

/// <summary>
/// The introduction step of the welcome view model.
/// </summary>
/// <param name="viewModel"></param>
public sealed class WelcomeViewModelIntroStep(WelcomeViewModel viewModel) : WelcomeViewModelStep(viewModel);

public sealed class WelcomeViewModelConfiguratorStep(WelcomeViewModel viewModel) : WelcomeViewModelStep(viewModel);

/// <summary>
/// Message to trigger confetti effect in the UI.
/// </summary>
public sealed record ShowConfettiEffectMessage;

public sealed partial class WelcomeViewModelAssistantStep(WelcomeViewModel viewModel, IServiceProvider serviceProvider)
    : WelcomeViewModelStep(viewModel)
{
    private readonly IKernelMixinFactory _kernelMixinFactory = serviceProvider.GetRequiredService<IKernelMixinFactory>();
    private readonly ILogger<WelcomeViewModelAssistantStep> _logger = serviceProvider.GetRequiredService<ILogger<WelcomeViewModelAssistantStep>>();

    [RelayCommand]
    private Task CheckConnectivityAsync()
    {
        ViewModel.IsConnectivityChecked = false;
        if (!ViewModel.Assistant.Configurator.Validate()) return Task.CompletedTask;

        return ExecuteBusyTaskAsync(
            async cancellationToken =>
            {
                try
                {
                    var kernelMixin = _kernelMixinFactory.GetOrCreate(ViewModel.Assistant);
                    await kernelMixin.CheckConnectivityAsync(cancellationToken);
                    StrongReferenceMessenger.Default.Send(new ShowConfettiEffectMessage());
                    ViewModel.IsConnectivityChecked = true;
                }
                catch (Exception ex)
                {
                    ex = HandledChatException.Handle(ex);
                    _logger.LogError(ex, "Failed to validate assistant connectivity");
                    ToastManager
                        .CreateToast(LocaleResolver.WelcomeViewModel_ValidateApiKey_FailedToast_Title)
                        .WithContent(ex.GetFriendlyMessage().ToTextBlock())
                        .DismissOnClick()
                        .ShowError();
                }
            },
            cancellationToken: CancellationTokenSource.Token);
    }
}

public sealed class WelcomeViewModelShortcutStep(WelcomeViewModel viewModel) : WelcomeViewModelStep(viewModel);

public sealed partial class WelcomeViewModelTelemetryStep(WelcomeViewModel viewModel) : WelcomeViewModelStep(viewModel)
{
    [RelayCommand]
    private void SendOnlyNecessaryTelemetry()
    {
        ViewModel.Settings.Common.DiagnosticData = false;
        ViewModel.Close();
    }
}
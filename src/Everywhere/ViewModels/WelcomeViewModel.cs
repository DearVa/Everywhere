using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.Logging;
using ShadUI;
using ZLinq;

namespace Everywhere.ViewModels;

public partial class WelcomeViewModel : BusyViewModelBase
{
    public enum Step
    {
        Intro = 0,
        SelectProvider = 1,
        /// <summary>
        /// Enter API Key (optional: select model) and validate
        /// </summary>
        EnterApiKey = 2,
        /// <summary>
        /// Teach shortcut
        /// </summary>
        Shortcut = 3,
        /// <summary>
        /// Select telemetry preference
        /// </summary>
        Telemetry = 4
    }

    public record ModelProviderWrapper(ModelProviderTemplate ProviderTemplate)
    {
        public DynamicResourceKeyBase DescriptionKey => new DynamicResourceKey($"WelcomeView_ModelProviderWrapper_{ProviderTemplate.Id}_Description");

        public Uri OfficialWebsiteUri => new(ProviderTemplate.OfficialWebsiteUrl ?? "https://everywhere.sylinko.com", UriKind.Absolute);

        public Uri ApiKeyHelpUri => new($"https://everywhere.sylinko.com/model-provider/{ProviderTemplate.Id}", UriKind.Absolute);
    }

    [ObservableProperty]
    public partial Step CurrentStep { get; private set; }

    public IReadOnlyList<ModelProviderWrapper> ModelProviders { get; }

    public ModelProviderWrapper? SelectedModelProvider
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;

            ModelDefinitions = field?.ProviderTemplate.ModelDefinitions
                    .AsValueEnumerable()
                    .Where(m => m.Id != "custom")
                    .ToList()
                ?? [];
            SelectedModelDefinition = ModelDefinitions.FirstOrDefault(m => m.IsDefault) ?? ModelDefinitions.First();

            ApiKey = null;
            GoToEnterApiKeyStepCommand.NotifyCanExecuteChanged();
        }
    }

    [ObservableProperty]
    public partial IReadOnlyList<ModelDefinitionTemplate> ModelDefinitions { get; private set; } = [];

    [ObservableProperty]
    public partial ModelDefinitionTemplate? SelectedModelDefinition { get; set; }

    public string? ApiKey
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;

            ValidateApiKeyCommand.NotifyCanExecuteChanged();
            IsApiKeyValid = false;
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoToShortcutStepCommand))]
    public partial bool IsApiKeyValid { get; private set; }

    public event Action? ApiKeyValidated;

    public Settings Settings { get; }

    private readonly IKernelMixinFactory _kernelMixinFactory;
    private readonly ILogger<WelcomeViewModel> _logger;

    public WelcomeViewModel(
        IKernelMixinFactory kernelMixinFactory,
        Settings settings,
        ILogger<WelcomeViewModel> logger)
    {
        _kernelMixinFactory = kernelMixinFactory;
        Settings = settings;
        _logger = logger;

        ModelProviders = ModelProviderTemplate.SupportedTemplates
            .AsValueEnumerable()
            .Where(m => m.Id is not ModelProviderIds.Ollama)
            .Select(m => new ModelProviderWrapper(m))
            .ToList();
    }

    [RelayCommand]
    private void Close() => DialogManager.CloseAll();

    [RelayCommand]
    private void GoToSelectProviderStep() => CurrentStep = Step.SelectProvider;

    [RelayCommand]
    private void PreviousStep() => CurrentStep = (Step)Math.Max(0, (int)CurrentStep - 1);

    public bool CanGoToEnterApiKeyStep => SelectedModelProvider is not null;

    [RelayCommand(CanExecute = nameof(CanGoToEnterApiKeyStep))]
    private void GoToEnterApiKeyStep() => CurrentStep = Step.EnterApiKey;

    [MemberNotNullWhen(true, nameof(ApiKey), nameof(SelectedModelProvider), nameof(SelectedModelDefinition))]
    public bool CanValidateApiKey => !string.IsNullOrWhiteSpace(ApiKey) && SelectedModelProvider is not null && SelectedModelDefinition is not null;

    [RelayCommand(CanExecute = nameof(CanValidateApiKey))]
    private Task ValidateApiKey() => ExecuteBusyTaskAsync(async cancellationToken =>
    {
        if (!CanValidateApiKey) return;

        IsApiKeyValid = false;
        var customAssistant = new CustomAssistant
        {
            Name = LocaleKey.CustomAssistant_Name_Default.I18N(),
            Icon = new ColoredIcon(ColoredIconType.Text) { Text = "🥳" },
            ApiKey = ApiKey,
            ModelProviderTemplateId = SelectedModelProvider.ProviderTemplate.Id,
            ModelDefinitionTemplateId = SelectedModelDefinition.Id
        };

        try
        {
            var kernelMixin = _kernelMixinFactory.GetOrCreate(customAssistant);
            await kernelMixin.CheckConnectivityAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ex = HandledChatException.Handle(ex);
            _logger.LogError(
                ex,
                "Failed to validate API key for provider {ProviderId} and model {ModelId}",
                SelectedModelProvider.ProviderTemplate.Id,
                SelectedModelDefinition.Id);
            ToastManager
                .CreateToast(LocaleKey.WelcomeViewModel_ValidateApiKey_FailedToast_Title.I18N())
                .WithContent(ex.GetFriendlyMessage().ToTextBlock())
                .DismissOnClick()
                .ShowError();
            return;
        }

        IsApiKeyValid = true;
        ApiKeyValidated?.Invoke();

        // Apply settings
        Settings.Model.CustomAssistants.Add(customAssistant);
        Settings.Model.SelectedCustomAssistant = customAssistant;
    });

    [RelayCommand(CanExecute = nameof(IsApiKeyValid))]
    private void GoToShortcutStep() => CurrentStep = Step.Shortcut;

    [RelayCommand]
    private void GoToTelemetryStep() => CurrentStep = Step.Telemetry;

    [RelayCommand]
    private void SendOnlyNecessaryTelemetry()
    {
        Settings.Common.DiagnosticData = false;
        Close();
    }
}
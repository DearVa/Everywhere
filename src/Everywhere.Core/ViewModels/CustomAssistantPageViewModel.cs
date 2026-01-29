using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Configuration;
using Lucide.Avalonia;
using Serilog;
using ShadUI;

namespace Everywhere.ViewModels;

public partial class CustomAssistantPageViewModel(IKernelMixinFactory kernelMixinFactory, Settings settings) : ReactiveViewModelBase
{
    public ObservableCollection<CustomAssistant> CustomAssistants => settings.Model.CustomAssistants;

    [ObservableProperty]
    public partial CustomAssistant? SelectedCustomAssistant { get; set; }

    private static Color[] RandomAssistantIconBackgrounds { get; } =
    [
        Colors.MediumPurple,
        Colors.CadetBlue,
        Colors.Coral,
        Colors.CornflowerBlue,
        Colors.DarkCyan,
        Colors.DarkGoldenrod,
        Colors.DarkKhaki,
        Colors.DarkOrange,
        Colors.DarkSalmon,
        Colors.DarkSeaGreen,
        Colors.DarkTurquoise,
        Colors.DeepSkyBlue,
        Colors.DodgerBlue,
        Colors.ForestGreen,
        Colors.Goldenrod,
        Colors.IndianRed,
        Colors.LightCoral,
        Colors.LightSeaGreen,
        Colors.MediumSeaGreen,
        Colors.MediumSlateBlue,
        Colors.MediumTurquoise,
        Colors.OliveDrab,
        Colors.OrangeRed,
        Colors.RoyalBlue,
        Colors.SeaGreen,
        Colors.SteelBlue,
    ];

    [RelayCommand]
    private void CreateNewCustomAssistant()
    {
        var newAssistant = new CustomAssistant
        {
            Name = LocaleResolver.CustomAssistant_Name_Default,
            Icon = new ColoredIcon(
                ColoredIconType.Lucide,
                background: RandomAssistantIconBackgrounds[Random.Shared.Next(RandomAssistantIconBackgrounds.Length)])
            {
                Kind = LucideIconKind.Bot
            },
            ConfiguratorType = ModelProviderConfiguratorType.PresetBased
        };
        settings.Model.CustomAssistants.Add(newAssistant);
        SelectedCustomAssistant = newAssistant;
    }

    [RelayCommand]
    private void DuplicateCustomAssistant()
    {
        if (SelectedCustomAssistant is not { } customAssistant) return;

        var options = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            IgnoreReadOnlyProperties = true
        };
        var json = JsonSerializer.Serialize(customAssistant, options);
        var duplicatedAssistant = JsonSerializer.Deserialize<CustomAssistant>(json, options).NotNull();

        duplicatedAssistant.Id = Guid.CreateVersion7();
        duplicatedAssistant.Name += " - " + LocaleResolver.Common_Copy;
        settings.Model.CustomAssistants.Insert(settings.Model.CustomAssistants.IndexOf(customAssistant) + 1, duplicatedAssistant);
        SelectedCustomAssistant = duplicatedAssistant;
    }

    [RelayCommand]
    private async Task CheckConnectivityAsync(CancellationToken cancellationToken)
    {
        if (SelectedCustomAssistant is not { } customAssistant) return;
        if (!customAssistant.Configurator.Validate()) return;

        try
        {
            await kernelMixinFactory.GetOrCreate(customAssistant).CheckConnectivityAsync(cancellationToken);
            ToastManager
                .CreateToast(LocaleResolver.CustomAssistantPageViewModel_CheckConnectivity_SuccessToast_Title)
                .DismissOnClick()
                .ShowSuccess();
        }
        catch (Exception ex)
        {
            ex = HandledChatException.Handle(ex);
            Log.Logger.ForContext<CustomAssistantPageViewModel>().Error(
                ex,
                "Failed to check connectivity key for endpoint {ProviderId} and model {ModelId}",
                customAssistant.Endpoint,
                customAssistant.ModelId);
            ToastManager
                .CreateToast(LocaleResolver.CustomAssistantPageViewModel_CheckConnectivity_FailedToast_Title)
                .WithContent(ex.GetFriendlyMessage().ToTextBlock())
                .DismissOnClick()
                .ShowError();
        }
    }

    [RelayCommand]
    private async Task DeleteCustomAssistantAsync()
    {
        if (SelectedCustomAssistant is not { } customAssistant) return;
        var result = await DialogManager.CreateDialog(
                LocaleResolver.CustomAssistantPageViewModel_DeleteCustomAssistant_Dialog_Message.Format(customAssistant.Name),
                LocaleResolver.Common_Warning)
            .WithPrimaryButton(LocaleResolver.Common_Yes)
            .WithCancelButton(LocaleResolver.Common_No)
            .ShowAsync();
        if (result != DialogResult.Primary) return;

        settings.Model.CustomAssistants.Remove(customAssistant);
    }
}
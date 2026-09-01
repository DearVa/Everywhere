using CommunityToolkit.Mvvm.ComponentModel;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class TextSelectionToolbarSettings(IServiceProvider serviceProvider) : SettingsBase(serviceProvider), ISettingsCategory
{
    [SettingsItemIgnore]
    public int Index => 5;

    [SettingsItemIgnore]
    public LucideIconKind Icon => LucideIconKind.TextSelect;

    [SettingsItemIgnore]
    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_TextSelectionToolbar_Header);

    [SettingsItemIgnore]
    public IDynamicLocaleKey? DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_TextSelectionToolbar_Description);

    /// <summary>
    /// Master toggle. Disabled by default because the feature installs global input hooks,
    /// which the user should opt into explicitly.
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.TextSelectionToolbarSettings_IsEnabled_Header,
        LocaleKey.TextSelectionToolbarSettings_IsEnabled_Description)]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.TextSelectionToolbarSettings_MaxActionCount_Header,
        LocaleKey.TextSelectionToolbarSettings_MaxActionCount_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsEnabled), Group = "_")]
    [SettingsIntegerItem(Min = 1, Max = 8)]
    public partial int MaxActionCount { get; set; } = 5;

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.TextSelectionToolbarSettings_ShowActionLabels_Header,
        LocaleKey.TextSelectionToolbarSettings_ShowActionLabels_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsEnabled), Group = "_")]
    public partial bool ShowActionLabels { get; set; } = true;
}

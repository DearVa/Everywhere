using Avalonia.Controls;
using Everywhere.Configuration;
using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

/// <summary>
/// Represents a settings category page that displays a list of settings items.
/// It dynamically creates settings items based on the properties of a specified settings category.
/// </summary>
public partial class SettingsCategoryPage : UserControl, IMainViewPage
{
    public int Index { get; }

    public DynamicResourceKeyBase Title { get; }

    public LucideIconKind Icon { get; }

    public SettingsItems Items { get; }

    public SettingsCategoryPage(int index, ISettingsCategory settingsCategory)
    {
        Index = index;
        Title = settingsCategory.DisplayNameKey;
        Icon = settingsCategory.Icon;
        Items = settingsCategory.SettingsItems ?? [];

        InitializeComponent();
    }
}

public class SettingsCategoryPageFactory(Settings settings) : IMainViewPageFactory
{
    public IEnumerable<IMainViewPage> CreatePages() =>
    [
        new SettingsCategoryPage(0, settings.Common),
        new SettingsCategoryPage(0, settings.ChatWindow),
    ];
}
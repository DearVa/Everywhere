using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Configuration;

/// <summary>
/// Represents the application settings.
/// A singleton that holds all the settings categories.
/// And automatically saves the settings to a JSON file when any setting is changed.
/// </summary>
[Serializable]
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public class Settings : ObservableObject
{
    public CommonSettings Common { get; set; } = new();

    public NetworkSettings Network { get; set; } = new();

    [HiddenSettingsItem]
    public ModelSettings Model { get; set; } = new();

    public ChatWindowSettings ChatWindow { get; set; } = new();

    [HiddenSettingsItem]
    public PluginSettings Plugin { get; set; } = new();

    [HiddenSettingsItem]
    public InternalSettings Internal { get; set; } = new();
}
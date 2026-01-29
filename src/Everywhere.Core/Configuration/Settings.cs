using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Configuration;

/// <summary>
/// Represents the application settings.
/// A singleton that holds all the settings categories.
/// And automatically saves the settings to a JSON file when any setting is changed.
/// </summary>
[Serializable]
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public partial class Settings : ObservableObject
{
    [ObservableProperty]
    public partial string? Version { get; set; }

    public CommonSettings Common { get; set; } = new();

    public ModelSettings Model { get; set; } = new();

    public ChatWindowSettings ChatWindow { get; set; } = new();

    public PluginSettings Plugin { get; set; } = new();
}
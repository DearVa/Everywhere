using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Configuration;

/// <summary>
/// Represents the persistent state of the application.
/// </summary>
public class PersistentState(IKeyValueStorage storage) : ObservableObject
{
    /// <summary>
    /// Used to popup welcome dialog on first launch and update.
    /// </summary>
    public string? PreviousLaunchVersion
    {
        get => Get<string?>();
        set => Set(value);
    }

    /// <summary>
    /// Pop a tray notification when the application is launched for the first time.
    /// </summary>
    public bool IsHideToTrayIconNotificationShown
    {
        get => Get(true);
        set => Set(value);
    }

    public bool IsToolCallEnabled
    {
        get => Get<bool>();
        set => Set(value);
    }

    public int MaxChatAttachmentCount
    {
        get => Get(10);
        set => Set(value);
    }

    public bool IsMainViewSidebarExpanded
    {
        get => Get<bool>();
        set => Set(value);
    }

    public bool? IsChatWindowPinned
    {
        get => Get<bool?>();
        set => Set(value);
    }

    public string? ChatInputAreaText
    {
        get => Get<string?>();
        set => Set(value);
    }

    public int VisualTreeTokenLimit
    {
        get => Get(4096);
        set => Set(value);
    }

    private T? Get<T>(T? defaultValue = default, [CallerMemberName] string key = "")
    {
        return storage.Get(key, defaultValue);
    }

    private void Set<T>(T? value, [CallerMemberName] string key = "")
    {
        if (EqualityComparer<T>.Default.Equals(Get(default(T), key), value)) return;
        storage.Set(key, value);
        OnPropertyChanged(key);
    }
}

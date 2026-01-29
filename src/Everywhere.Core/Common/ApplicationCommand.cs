using MessagePack;

namespace Everywhere.Common;

/// <summary>
/// Represents an application command. It can be sent between different parts of the application or different processes.
/// </summary>
[MessagePackObject]
[Union(0, typeof(ShowWindowCommand))]
[Union(1, typeof(UrlProtocolCallbackCommand))]
public abstract partial class ApplicationCommand;

/// <summary>
/// Command to show the main application window.
/// </summary>
/// <param name="name">
/// The name of the ViewModel to be shown.
/// </param>
[MessagePackObject]
public partial class ShowWindowCommand(string name) : ApplicationCommand
{
    [Key(0)]
    public string Name { get; } = name;
}

/// <summary>
/// Command to handle when application is launched via URL protocol.
/// </summary>
[MessagePackObject]
public partial class UrlProtocolCallbackCommand(string url) : ApplicationCommand
{
    public const string Scheme = "sylinko-everywhere";

    [Key(0)]
    public string Url { get; } = url;
}

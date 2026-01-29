using MessagePack;

namespace Everywhere.Rpc;

[MessagePackObject]
[Union(0, typeof(RegisterSubprocessCommand))]
[Union(1, typeof(UnregisterSubprocessCommand))]
public abstract partial class WatchdogCommand;

/// <summary>
/// Command to register a process to be watched
/// </summary>
public abstract class ProcessCommand : WatchdogCommand
{
    [Key(0)]
    public required long ProcessId { get; init; }
}

/// <summary>
/// Register a subprocess to be watched, when host process exits, the subprocess will be killed.
/// </summary>
[MessagePackObject]
public partial class RegisterSubprocessCommand : ProcessCommand;

/// <summary>
/// Unregister a watched subprocess.
/// </summary>
[MessagePackObject]
public partial class UnregisterSubprocessCommand : ProcessCommand
{
    [Key(1)]
    public bool KillIfRunning { get; init; }
}
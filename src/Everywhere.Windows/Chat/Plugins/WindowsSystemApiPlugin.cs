using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.I18N;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Everywhere.Windows.Chat.Plugins;

/// <summary>
/// Simplified Windows system helper plugin that exposes a compact tool surface area.
/// </summary>
public class WindowsSystemApiPlugin : BuiltInChatPlugin
{
    public override DynamicResourceKeyBase HeaderKey { get; } = new DirectResourceKey(LocaleKey.NativeChatPlugin_WindowsSystemApi_Header);
    public override DynamicResourceKeyBase DescriptionKey { get; } = new DirectResourceKey(LocaleKey.NativeChatPlugin_WindowsSystemApi_Description);
    public override LucideIconKind? Icon => LucideIconKind.Monitor;
    public override string BeautifulIcon => "avares://Everywhere.Windows/Assets/Icons/Windows11.svg";

    private readonly ILogger<WindowsSystemApiPlugin> _logger;

    public WindowsSystemApiPlugin(ILogger<WindowsSystemApiPlugin> logger) : base("windows_system_api")
    {
        _logger = logger;

        _functions.Add(new AnonymousChatFunction(OpenControlPanelAsync, ChatFunctionPermissions.ProcessAccess));
    }

    private static readonly IReadOnlyDictionary<ControlPanelItem, string> ControlPanelArguments = new Dictionary<ControlPanelItem, string>
    {
        { ControlPanelItem.Home, string.Empty },
        { ControlPanelItem.NetworkConnections, "ncpa.cpl" },
        { ControlPanelItem.PowerOptions, "/name Microsoft.PowerOptions" },
        { ControlPanelItem.ProgramsAndFeatures, "appwiz.cpl" },
        { ControlPanelItem.System, "/name Microsoft.System" },
        { ControlPanelItem.DeviceManager, "hdwwiz.cpl" },
        { ControlPanelItem.Sound, "mmsys.cpl" },
        { ControlPanelItem.Display, "/name Microsoft.Display" },
        { ControlPanelItem.UserAccounts, "/name Microsoft.UserAccounts" },
        { ControlPanelItem.WindowsUpdate, "/name Microsoft.WindowsUpdate" },
        { ControlPanelItem.DateTime, "timedate.cpl" }
    };

    [KernelFunction("open_control_panel")]
    [Description("Launches Control Panel tasks the same way the control.exe command does. Useful for opening specific Windows settings panes.")]
    private Task<string> OpenControlPanelAsync(
        [Description("The Control Panel item to open. Matches control.exe canonical names.")] ControlPanelItem item,
        [Description("Optional override for the control.exe argument when you already know the exact command.")]
        string? argument = null)
    {
        _logger.LogDebug("Launching Control Panel item {Item} with override {Override}", item, argument);

        return Task.Run(() =>
        {
            var args = argument ?? ControlPanelArguments.GetValueOrDefault(item, string.Empty);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "control.exe"), // full path to avoid possible hijacking
                    Arguments = args,
                    UseShellExecute = true
                };

                Process.Start(psi);
                return "success";
            }
            catch (Exception ex)
            {
                var handledException = new HandledException(ex, new DirectResourceKey("Failed to launch Control Panel item"));
                _logger.LogError(ex, "Failed to launch Control Panel item {Item} with args {Args}", item, args);
                throw handledException;
            }
        });
    }









    public enum ControlPanelItem
    {
        Home,
        NetworkConnections,
        PowerOptions,
        ProgramsAndFeatures,
        System,
        DeviceManager,
        Sound,
        Display,
        UserAccounts,
        WindowsUpdate,
        DateTime
    }



}
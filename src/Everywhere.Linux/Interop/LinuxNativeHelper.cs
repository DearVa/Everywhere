using System.Diagnostics;
using Avalonia.Input;
using Everywhere.Common;
using Everywhere.Extensions;
using Everywhere.Interop;

namespace Everywhere.Linux.Interop;

public class LinuxNativeHelper : INativeHelper
{
    private readonly ILinuxEventHelper _eventHelper = ServiceLocator.Resolve<ILinuxEventHelper>();

    private static string SystemdServiceFile
    {
        get
        {
            string? home = Environment.GetEnvironmentVariable("HOME");
            return string.IsNullOrEmpty(home) ?
                throw new InvalidOperationException("HOME environment variable is not set.") :
                Path.Combine(home, ".config/systemd/user/Everywhere.service");
        }
    }

    public bool IsInstalled => false; // implement proper detection if needed

    public bool IsAdministrator => false; // Linux elevation detection omitted

    // About Startup setting:
    // Most modern Linux Use Systemd to manage this feature.
    // For easy use, we attach a service config file in Installation dir.
    // Thus write systemd service file in user mode and enable the service.
    public bool IsUserStartupEnabled
    {
        get
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = "--user is-enabled Everywhere.service",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var p = Process.Start(startInfo);
            p?.WaitForExit();
            return p?.ExitCode == 0;
        }
        set
        {
            const string serviceFileContent =
                """
                [Unit]
                Description=Everywhere
                After=graphical-session.target

                [Service]
                ExecStart=/usr/bin/Everywhere

                [Install]
                WantedBy=graphical-session.target
                """;
            if (!File.Exists(SystemdServiceFile) || value)
            {
                File.WriteAllText(SystemdServiceFile, serviceFileContent);
            }
            var action = value ? "enable" : "disable";
            Process.Start(new ProcessStartInfo("systemctl", $"--user {action} Everywhere.service"))?.WaitForExit();
        }

    }

    public bool IsAdministratorStartupEnabled { get; set; }

    public void RestartAsAdministrator()
    {
        // No-op on Linux by default. Could re-exec with sudo if desired.
        ShowDesktopNotificationAsync("Administrator is usually unnecessary on Linux. if needed, Please re-exec with sudo.");
    }

    public bool GetKeyState(KeyModifiers keyModifiers)
    {
        return _eventHelper.GetKeyState(keyModifiers);
    }

    public Task<bool> ShowDesktopNotificationAsync(string message, string? title = null)
    {
        // Try to use libnotify via command line as a best-effort notification
        var args = $"-u normal \"{title ?? "Everywhere"}\" \"{message}\"";
        Process.Start("notify-send", args);
        return Task.FromResult(false);
    }

    public void OpenFileLocation(string fullPath)
    {
        if (fullPath.IsNullOrWhiteSpace()) return;
        var args = $"\"{fullPath}\"";
        Process.Start(new ProcessStartInfo("xdg-open", args) { UseShellExecute = true });
    }
}
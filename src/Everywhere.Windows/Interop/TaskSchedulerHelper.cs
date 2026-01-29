using System.Diagnostics;
using System.Security.Principal;
using Windows.Win32;

namespace Everywhere.Windows.Interop;

public static class TaskSchedulerHelper
{
    public static bool IsTaskScheduled(string taskName)
    {
        using var process = Process.Start(
            new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{taskName}\"")
            {
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
        if (process is null) return false;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    public static void CreateScheduledTask(string taskName, string appPath)
    {
        var userId = WindowsIdentity.GetCurrent().Name;

        // Split command line into executable and arguments
        var exePath = appPath;
        var arguments = string.Empty;
        unsafe
        {
            var args = PInvoke.CommandLineToArgv(appPath, out var argCount);
            if (argCount > 1)
            {
                exePath = (*args).ToString();
                var argList = new string[argCount - 1];
                for (var i = 1; i < argCount; i++)
                {
                    argList[i - 1] = (*(args + i)).ToString();
                }
                arguments = string.Join(' ', argList);
            }
        }

        var xmlContent =
            $"""
             <?xml version="1.0" encoding="UTF-16"?>
             <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
               <RegistrationInfo>
                 <Description>Auto-start task for {taskName}</Description>
                 <URI>\{taskName}</URI>
               </RegistrationInfo>
               <Triggers>
                 <LogonTrigger>
                   <Enabled>true</Enabled>
                   <UserId>{userId}</UserId>
                   <Delay>PT3S</Delay>
                 </LogonTrigger>
               </Triggers>
               <Principals>
                 <Principal id="Author">
                   <UserId>{userId}</UserId>
                   <LogonType>InteractiveToken</LogonType>
                   <RunLevel>HighestAvailable</RunLevel>
                 </Principal>
               </Principals>
               <Settings>
                 <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                 <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                 <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                 <AllowHardTerminate>true</AllowHardTerminate>
                 <StartWhenAvailable>false</StartWhenAvailable>
                 <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                 <IdleSettings>
                   <StopOnIdleEnd>true</StopOnIdleEnd>
                   <RestartOnIdle>false</RestartOnIdle>
                 </IdleSettings>
                 <AllowStartOnDemand>true</AllowStartOnDemand>
                 <Enabled>true</Enabled>
                 <Hidden>false</Hidden>
                 <RunOnlyIfIdle>false</RunOnlyIfIdle>
                 <WakeToRun>false</WakeToRun>
                 <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                 <Priority>7</Priority>
               </Settings>
               <Actions Context="Author">
                 <Exec>
                   <Command>{exePath}</Command>
                   <Arguments>{arguments}</Arguments>
                 </Exec>
               </Actions>
             </Task>
             """;

        var tempXmlPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tempXmlPath, xmlContent);

            Process.Start(
                new ProcessStartInfo("schtasks.exe", $"/Create /TN \"{taskName}\" /XML \"{tempXmlPath}\" /F")
                {
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                })?.WaitForExit();
        }
        finally
        {
            if (File.Exists(tempXmlPath))
            {
                File.Delete(tempXmlPath);
            }
        }
    }

    public static void DeleteScheduledTask(string taskName)
    {
        Process.Start(
            new ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{taskName}\" /F")
            {
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })?.WaitForExit();
    }
}
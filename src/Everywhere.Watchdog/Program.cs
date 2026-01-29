using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Everywhere.Rpc;
using MessagePack;

namespace Everywhere.Watchdog;

public static class Program
{
    private static readonly ConcurrentDictionary<long, Process> MonitoredProcesses = new();

    public static async Task Main(string[] args)
    {
        // Ensure we use UTF-8 for all I/O to avoid encoding issues (e.g. ??? in output)
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            await Console.Error.WriteLineAsync("No arguments provided. Exiting.");
            Environment.Exit(1);
        }

        var pipeName = args[0];
        Console.WriteLine($"Started. Waiting for main application to connect with pipe name: {pipeName}");

        await using var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.In,
            PipeOptions.Asynchronous);

        try
        {
            await clientStream.ConnectAsync(5000).ConfigureAwait(false);
            Console.WriteLine("Main application connected. Listening for commands...");

            var lengthBuffer = new byte[4];
            while (clientStream.IsConnected)
            {
                var bytesRead = await clientStream.ReadAsync(lengthBuffer.AsMemory(0, 4));
                if (bytesRead < 4) break;

                var messageLength = BitConverter.ToInt32(lengthBuffer, 0);
                var messageBuffer = new byte[messageLength];
                await clientStream.ReadExactlyAsync(messageBuffer, 0, messageLength);

                var command = MessagePackSerializer.Deserialize<WatchdogCommand>(messageBuffer);
                ProcessCommand(command);
            }
        }
        catch (TimeoutException)
        {
            await Console.Error.WriteLineAsync("Timeout waiting for main application to connect. Exiting...");
        }
        catch (IOException)
        {
            await Console.Error.WriteLineAsync("Connection lost. Main application has likely exited.");
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"An unexpected error occurred: {ex.Message}");
        }
        finally
        {
            TerminateAllSubprocesses();
            Console.WriteLine("Job finished. Exiting...");
        }
    }

    private static void ProcessCommand(WatchdogCommand? command)
    {
        switch (command)
        {
            case RegisterSubprocessCommand registerCmd:
            {
                try
                {
                    var process = Process.GetProcessById((int)registerCmd.ProcessId);
                    MonitoredProcesses.TryAdd(process.Id, process);
                    Console.WriteLine($"Registered process '{process.ProcessName}' (ID: {process.Id}).");
                }
                catch (ArgumentException)
                {
                    Console.WriteLine($"Process with ID {registerCmd.ProcessId} not found.");
                }
                break;
            }
            case UnregisterSubprocessCommand unregisterCmd:
            {
                if (MonitoredProcesses.TryRemove(unregisterCmd.ProcessId, out var p))
                {
                    Console.WriteLine($"Unregistered process '{p.ProcessName}' (ID: {p.Id}, KillIfRunning: {unregisterCmd.KillIfRunning}).");

                    if (unregisterCmd.KillIfRunning && !p.HasExited)
                    {
                        Console.WriteLine($"Killing process '{p.ProcessName}' (ID: {p.Id}).");
                        TerminateProcessTree(p.Id);
                    }
                }
                break;
            }
        }
    }

    private static void TerminateAllSubprocesses()
    {
        Console.WriteLine($"Terminating {MonitoredProcesses.Count} monitored process(es)...");
        foreach (var pair in MonitoredProcesses)
        {
            try
            {
                if (pair.Value.HasExited) continue;

                Console.WriteLine($"Killing process '{pair.Value.ProcessName}' (ID: {pair.Key}).");
                TerminateProcessTree(pair.Value.Id);
                pair.Value.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to terminate process {pair.Key}: {ex.Message}");
            }
        }

        MonitoredProcesses.Clear();
    }

    private static void TerminateProcessTree(int pid)
    {
#if WINDOWS
            // Use taskkill to terminate the process tree on Windows
            // /T: terminate child processes, /F: force termination
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {pid} /T /F",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                })?.WaitForExit();
#elif LINUX || MACOS
        TerminateUnixProcessTree(pid);
#else
        #error Unsupported platform
#endif
    }

    private static void TerminateUnixProcessTree(int pid)
    {
        try
        {
            // 1. Try to find all children using ps
            var processMap = GetUnixProcessMap();
            var processesToKill = new HashSet<int> { pid };
            var queue = new Queue<int>();
            queue.Enqueue(pid);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!processMap.TryGetValue(current, out var children)) continue;
                foreach (var child in children.Where(child => processesToKill.Add(child)))
                {
                    queue.Enqueue(child);
                }
            }
            Console.WriteLine($"Terminating process tree for PID {pid}: {string.Join(", ", processesToKill)}");

            // 2. Kill them all
            foreach (var p in processesToKill)
            {
                try
                {
                    Process.GetProcessById(p).Kill();
                }
                catch (Exception)
                {
                    // Fallback to kill command
                    Console.Error.WriteLine($"Failed to terminate process with ID {p}, falling back to 'kill -9'.");
                    Process.Start(
                        new ProcessStartInfo
                        {
                            FileName = "kill",
                            Arguments = $"-9 {p}",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        })?.WaitForExit();
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to terminate process tree for {pid}: {ex.Message}");
            // Fallback to PGID kill if the above failed catastrophically
            try
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = "/bin/sh",
                        Arguments = $"-c \"kill -9 -$(ps -o pgid= -p {pid} | tr -d ' ')\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit();
            }
            catch (Exception ex2)
            {
                Console.Error.WriteLine($"Failed to terminate process group for {pid}: {ex2.Message}");
            }
        }
    }

    private static Dictionary<int, List<int>> GetUnixProcessMap()
    {
        var map = new Dictionary<int, List<int>>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ps",
                Arguments = "-A -o ppid,pid",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                while (!proc.StandardOutput.EndOfStream)
                {
                    var line = proc.StandardOutput.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[0], out var ppid) && int.TryParse(parts[1], out var childPid))
                    {
                        if (!map.TryGetValue(ppid, out var list))
                        {
                            list = new List<int>();
                            map[ppid] = list;
                        }
                        list.Add(childPid);
                    }
                }
                proc.WaitForExit();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to get process map: {ex.Message}");
        }
        return map;
    }
}
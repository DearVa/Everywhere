using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.PowerShell;

namespace Everywhere.Windows.PowerShell;

public static partial class Program
{
    public static async Task Main()
    {
        // Ensure we use UTF-8 for all I/O to avoid encoding issues (e.g. ??? in output)
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        var path = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
        var modulesPath = Path.Combine(path ?? ".", "runtimes", "win", "lib", "net9.0", "Modules");

        var modulesPathBuilder = new StringBuilder();
        modulesPathBuilder.Append(Path.GetFullPath(modulesPath)).Append(';');

        if (FindPowerShellExecutable() is { } pwshExe && Path.GetDirectoryName(pwshExe) is { Length: > 0 } pwshDir)
        {
            var pwshModulesPath = Path.Combine(pwshDir, "Modules");
            if (Directory.Exists(pwshModulesPath))
            {
                modulesPathBuilder.Append(Path.GetFullPath(pwshModulesPath)).Append(';');
            }
        }

        Environment.SetEnvironmentVariable(
            "PSModulePath",
            modulesPathBuilder.Append(Environment.GetEnvironmentVariable("PSModulePath")).ToString());

        var iss = InitialSessionState.CreateDefault2();
        iss.ExecutionPolicy = ExecutionPolicy.Bypass;

        // Create a new runspace and PowerShell instance for each execution
        // using the cached InitialSessionState. This ensures context isolation.
        using var runspace = RunspaceFactory.CreateRunspace(iss);
        // Disable ReSharper warning as OpenAsync() cannot be awaited
        // ReSharper disable once MethodHasAsyncOverload
        runspace.Open();

        var scriptBuilder = new StringBuilder();
        while (Console.ReadLine() is { } line)
        {
            scriptBuilder.AppendLine(line);
        }

        using var powerShell = System.Management.Automation.PowerShell.Create(runspace);
        
        // Use *>&1 to redirect all streams (Error, Warning, Verbose, Debug, Information) to the Success stream.
        // Then pipe to Out-String to format everything as text (e.g. "WARNING: ...").
        powerShell.AddScript($"& {{ {scriptBuilder} }} *>&1 | Out-String");

        var results = await powerShell.InvokeAsync();
        
        // Since we redirected Error stream to Success stream, HadErrors might be false, 
        // or even if true, the error details are already in 'results'.
        // We only check for infrastructure errors here if needed, but generally we want to return everything to the caller.
        if (powerShell.HadErrors && results.Count == 0)
        {
            // Fallback if something went wrong and wasn't captured in output
            var errorMessage = string.Join(Environment.NewLine, powerShell.Streams.Error.Select(e => e.ToString()));
            await Console.Error.WriteAsync(errorMessage);
            Environment.Exit(1);
        }

        var result = results.FirstOrDefault()?.ToString() ?? string.Empty;
        if (result.EndsWith(Environment.NewLine)) result = result[..^Environment.NewLine.Length]; // Trim trailing new line
        Console.Write(result);
    }

    /// <summary>
    /// Search for PowerShell executable in the system.
    /// </summary>
    private static string? FindPowerShellExecutable()
    {
        // 1. Use PATH first
        var pwshInPath = FindInPath("pwsh.exe");
        if (!string.IsNullOrEmpty(pwshInPath)) return pwshInPath;

        // 2. Search in Program Files
        var bestProgramFilesVersion = FindBestVersionInProgramFiles();
        if (!string.IsNullOrEmpty(bestProgramFilesVersion)) return bestProgramFilesVersion;

        return null;
    }

    /// <summary>
    /// Search for the best PowerShell version installed in Program Files directories.
    /// </summary>
    private static string FindBestVersionInProgramFiles()
    {
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        var foundExes = new List<(Version Ver, string Path)>();

        foreach (var psRoot in roots
                     .Where(root => !string.IsNullOrEmpty(root))
                     .Select(root => Path.Combine(root, "PowerShell"))
                     .Where(Directory.Exists))
        {
            try
            {
                // Get all subdirectories (versions)
                var dirs = Directory.GetDirectories(psRoot);

                foreach (var dir in dirs)
                {
                    var folderName = Path.GetFileName(dir);
                    var exePath = Path.Combine(dir, "pwsh.exe");

                    if (!File.Exists(exePath)) continue;

                    // Try parse version
                    // Use regex to extract leading numeric part to handle "7-preview" etc.
                    // Match "7", "7.1", "7.2.3" etc.
                    var match = VersionRegex().Match(folderName);

                    if (match.Success && Version.TryParse(match.Value, out var v))
                    {
                        foundExes.Add((v, exePath));
                    }
                }
            }
            catch
            {
                // Ignore
            }
        }

        // 7.2 > 7.1 > 7.0 > 6.0
        var bestMatch = foundExes.OrderByDescending(x => x.Ver).FirstOrDefault();
        return bestMatch.Path;
    }

    /// <summary>
    /// Find a file in the system PATH environment variable.
    /// </summary>
    /// <param name="fileName"></param>
    /// <returns></returns>
    private static string? FindInPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var paths = pathEnv.Split(Path.PathSeparator);
        foreach (var path in paths)
        {
            try
            {
                var fullPath = Path.Combine(path.Trim(), fileName);
                if (File.Exists(fullPath)) return fullPath;
            }
            catch
            {
                // Ignore
            }
        }

        return null;
    }

    [GeneratedRegex(@"^(\d+(\.\d+)*)")]
    private static partial Regex VersionRegex();
}
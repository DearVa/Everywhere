using System.Text;
using Serilog;

namespace Everywhere.Interop;

public static class EnvironmentVariableUtilities
{
    /// <summary>
    /// Gets the latest PATH environment variable by merging Machine, User and Process level PATHs.
    /// </summary>
    /// <returns></returns>
    public static string? GetLatestPathVariable()
    {
        try
        {
            var machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? string.Empty;
            var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;

            var sb = new StringBuilder();
            var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AppendPaths(string sourcePath)
            {
                if (string.IsNullOrEmpty(sourcePath)) return;
                var parts = sourcePath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var part in parts)
                {
                    if (existingPaths.Add(part))
                    {
                        if (sb.Length > 0) sb.Append(Path.PathSeparator);
                        sb.Append(part);
                    }
                }
            }

            AppendPaths(machinePath);
            AppendPaths(userPath);

            var currentProcessPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Process) ?? string.Empty;
            AppendPaths(currentProcessPath);

            return sb.ToString();
        }
        catch (Exception ex)
        {
            Log.ForContext(typeof(EnvironmentVariableUtilities)).Error(ex, "Failed to get latest PATH environment variable.");

            // Ignore errors when trying to refresh environment
            return null;
        }
    }
}
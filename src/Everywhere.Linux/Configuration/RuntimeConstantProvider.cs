using Everywhere.Configuration;

namespace Everywhere.Linux.Configuration;

public class RuntimeConstantProvider : IRuntimeConstantProvider
{
    public object? this[RuntimeConstantType type] => type switch
    {
        RuntimeConstantType.WritableDataPath => EnsureDirectory(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Everywhere")),
        _ => null
    };

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}

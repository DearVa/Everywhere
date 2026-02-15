namespace Everywhere.Configuration;

/// <summary>
/// Provides runtime constants and utility methods for managing writable data paths and database paths.
/// </summary>
public static class RuntimeConstants
{
    /// <summary>
    /// Get a unique device identifier for the current machine with length of 36 characters (GUID format).
    /// The device ID is generated once and stored in the writable data path, ensuring that it remains consistent across application sessions on the same machine.
    /// This can be used for telemetry, analytics, or any scenario where a consistent identifier for the device is needed.
    /// </summary>
    public static string DeviceId { get; }

    /// <summary>
    /// Gets the writable data path for the application, ensuring that the directory exists.
    /// This path is typically used for storing application data, such as databases, logs, and other files that need to be persisted across sessions.
    /// </summary>
    public static string WritableDataPath { get; }

    static RuntimeConstants()
    {
        DeviceId = EnsureDeviceId();
        WritableDataPath = EnsureDirectory(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Everywhere"));
    }

    /// <summary>
    /// Ensures that the specified relative path within the writable data folder exists and returns the full path.
    /// </summary>
    /// <param name="relativePath"></param>
    /// <returns></returns>
    public static string EnsureWritableDataFolderPath(string relativePath)
    {
        return EnsureDirectory(Path.GetFullPath(Path.Combine(WritableDataPath, relativePath)));
    }

    /// <summary>
    /// Gets the full path for a database file with the specified name within the writable data folder, ensuring that the directory exists.
    /// </summary>
    /// <param name="dbName"></param>
    /// <returns></returns>
    public static string GetDatabasePath(string dbName)
    {
        var folderPath = EnsureWritableDataFolderPath("db");
        return Path.Combine(folderPath, dbName);
    }

    private static string EnsureDeviceId()
    {
        // Store the device ID in a file within the local application data folder to ensure it persists across sessions on the same machine
        var baseFolderPath =
            EnsureDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everywhere"));
        var deviceIdFilePath = Path.Combine(baseFolderPath, ".device_id");

        var fileInfo = new FileInfo(deviceIdFilePath);
        if (fileInfo is { Exists: true, Length: 36 }) // GUID format length
        {
            using var reader = new StreamReader(deviceIdFilePath);
            var existingDeviceId = reader.ReadToEnd().Trim();
            if (Guid.TryParse(existingDeviceId, out _))
            {
                return existingDeviceId;
            }
        }

        // If the file does not exist or is invalid, generate a new device ID, save it to the file, and return it
        var newDeviceId = Guid.CreateVersion7().ToString();
        File.WriteAllText(deviceIdFilePath, newDeviceId);
        return newDeviceId;
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
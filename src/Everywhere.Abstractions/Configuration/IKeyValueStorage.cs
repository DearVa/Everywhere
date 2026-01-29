namespace Everywhere.Configuration;

/// <summary>
/// Represents a high-performance key-value storage.
/// </summary>
public interface IKeyValueStorage
{
    /// <summary>
    /// Gets the value associated with the specified key.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="key">The key.</param>
    /// <param name="defaultValue">The default value if the key is not found.</param>
    /// <returns>The value associated with the specified key, or the default value if the key is not found.</returns>
    T? Get<T>(string key, T? defaultValue = default);

    /// <summary>
    /// Sets the value associated with the specified key.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    void Set<T>(string key, T? value);

    /// <summary>
    /// Determines whether the storage contains the specified key.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns><c>true</c> if the storage contains the specified key; otherwise, <c>false</c>.</returns>
    bool Contains(string key);

    /// <summary>
    /// Removes the value associated with the specified key.
    /// </summary>
    /// <param name="key">The key.</param>
    void Remove(string key);
}

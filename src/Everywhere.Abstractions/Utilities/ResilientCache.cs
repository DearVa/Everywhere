using System.Diagnostics.CodeAnalysis;

namespace Everywhere.Utilities;

/// <summary>
/// A thread-safe dictionary-like collection that can switch between holding strong and weak references to its values.
/// This is useful for managing memory for expensive objects that can be temporarily deactivated and potentially reclaimed by the GC,
/// but can be restored to a strongly-referenced state if they are still alive.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the cache.</typeparam>
/// <typeparam name="TValue">The type of the values in the cache. Must be a reference type.</typeparam>
public class ResilientCache<TKey, TValue> : IDictionary<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    private readonly Lock _lock = new();
    private Dictionary<TKey, TValue> _strongReferences = new();
    private Dictionary<TKey, WeakReference<TValue>> _weakReferences = new();
    private bool _isActive = true;

    /// <summary>
    /// Gets or sets a value indicating whether the cache is active.
    /// When set to <c>true</c> (active), the cache switches to holding strong references, restoring any live objects.
    /// When set to <c>false</c> (inactive), the cache switches to holding weak references, allowing values to be garbage collected.
    /// The operation is thread-safe.
    /// </summary>
    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _isActive;
            }
        }
        set
        {
            lock (_lock)
            {
                if (_isActive == value) return;

                if (value) // Switching to Active (Strong)
                {
                    _strongReferences = new Dictionary<TKey, TValue>(_weakReferences.Count);
                    foreach (var kvp in _weakReferences)
                    {
                        if (kvp.Value.TryGetTarget(out var target))
                        {
                            _strongReferences[kvp.Key] = target;
                        }
                    }
                    _weakReferences.Clear();
                }
                else // Switching to Inactive (Weak)
                {
                    _weakReferences = new Dictionary<TKey, WeakReference<TValue>>(_strongReferences.Count);
                    foreach (var kvp in _strongReferences)
                    {
                        _weakReferences[kvp.Key] = new WeakReference<TValue>(kvp.Value);
                    }
                    _strongReferences.Clear();
                }

                _isActive = value;
            }
        }
    }

    /// <summary>
    /// Removes all key-value pairs where the value has been garbage-collected.
    /// This method is only effective when the cache is inactive (<see cref="IsActive"/> is <c>false</c>).
    /// This operation is thread-safe.
    /// </summary>
    public void Prune()
    {
        lock (_lock)
        {
            if (_isActive) return;

            var keysToRemove = new List<TKey>();
            foreach (var kvp in _weakReferences)
            {
                if (!kvp.Value.TryGetTarget(out _))
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _weakReferences.Remove(key);
            }
        }
    }

    #region IDictionary<TKey, TValue> Implementation

    public TValue this[TKey key]
    {
        get
        {
            if (TryGetValue(key, out var value))
            {
                return value;
            }

            throw new KeyNotFoundException($"The given key '{key}' was not present in the dictionary.");
        }
        set
        {
            lock (_lock)
            {
                if (_isActive)
                {
                    _strongReferences[key] = value;
                }
                else
                {
                    _weakReferences[key] = new WeakReference<TValue>(value);
                }
            }
        }
    }

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        lock (_lock)
        {
            if (_isActive)
            {
                return _strongReferences.TryGetValue(key, out value);
            }

            if (_weakReferences.TryGetValue(key, out var weakRef) && weakRef.TryGetTarget(out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    public void Add(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_isActive)
            {
                _strongReferences.Add(key, value);
            }
            else
            {
                // In weak mode, a key might exist but its value could have been collected.
                // `ContainsKey` correctly checks for liveness.
                if (ContainsKey(key))
                {
                    throw new ArgumentException("An item with the same key has already been added and is alive.");
                }

                _weakReferences[key] = new WeakReference<TValue>(value);
            }
        }
    }

    /// <summary>
    /// Adds multiple key-value pairs to the cache with a single lock acquisition.
    /// </summary>
    /// <param name="items"></param>
    public void AddRange(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        lock (_lock)
        {
            foreach (var kvp in items)
            {
                if (_isActive)
                {
                    _strongReferences[kvp.Key] = kvp.Value;
                }
                else
                {
                    _weakReferences[kvp.Key] = new WeakReference<TValue>(kvp.Value);
                }
            }
        }
    }

    public bool Remove(TKey key)
    {
        lock (_lock)
        {
            return _isActive ? _strongReferences.Remove(key) : _weakReferences.Remove(key);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _strongReferences.Clear();
            _weakReferences.Clear();
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                if (_isActive)
                {
                    return _strongReferences.Count;
                }

                // In weak mode, we count only the live references for accuracy.
                var count = 0;
                foreach (var weakRef in _weakReferences.Values)
                {
                    if (weakRef.TryGetTarget(out _))
                    {
                        count++;
                    }
                }
                return count;
            }
        }
    }

    public ICollection<TKey> Keys
    {
        get
        {
            lock (_lock)
            {
                if (_isActive)
                {
                    return _strongReferences.Keys.ToList(); // Return a snapshot
                }

                var aliveKeys = new List<TKey>(_weakReferences.Count);
                foreach (var kvp in _weakReferences)
                {
                    if (kvp.Value.TryGetTarget(out _))
                    {
                        aliveKeys.Add(kvp.Key);
                    }
                }
                return aliveKeys;
            }
        }
    }

    public ICollection<TValue> Values
    {
        get
        {
            lock (_lock)
            {
                if (_isActive)
                {
                    return _strongReferences.Values.ToList(); // Return a snapshot
                }

                var aliveValues = new List<TValue>(_weakReferences.Count);
                foreach (var weakRef in _weakReferences.Values)
                {
                    if (weakRef.TryGetTarget(out var value))
                    {
                        aliveValues.Add(value);
                    }
                }
                return aliveValues;
            }
        }
    }

    public bool ContainsKey(TKey key)
    {
        lock (_lock)
        {
            if (_isActive)
            {
                return _strongReferences.ContainsKey(key);
            }

            // In weak mode, a key is considered contained only if its value is still alive.
            return _weakReferences.TryGetValue(key, out var weakRef) && weakRef.TryGetTarget(out _);
        }
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        // Return an enumerator over a snapshot of the live items to ensure thread safety.
        var snapshot = new List<KeyValuePair<TKey, TValue>>();
        lock (_lock)
        {
            if (_isActive)
            {
                snapshot.AddRange(_strongReferences);
            }
            else
            {
                foreach (var kvp in _weakReferences)
                {
                    if (kvp.Value.TryGetTarget(out var value))
                    {
                        snapshot.Add(new KeyValuePair<TKey, TValue>(kvp.Key, value));
                    }
                }
            }
        }
        return snapshot.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

    public bool Contains(KeyValuePair<TKey, TValue> item)
    {
        return TryGetValue(item.Key, out var value) && EqualityComparer<TValue>.Default.Equals(value, item.Value);
    }

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);

        // The GetEnumerator implementation already creates a safe, live-only snapshot.
        var snapshot = this.ToList();

        if (array.Length - arrayIndex < snapshot.Count)
        {
            throw new ArgumentException("The destination array is not large enough to hold the collection's items.");
        }

        snapshot.CopyTo(array, arrayIndex);
    }

    public bool Remove(KeyValuePair<TKey, TValue> item)
    {
        return Contains(item) && Remove(item.Key);
    }

    public bool IsReadOnly => false;

    #endregion
}
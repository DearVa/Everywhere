using System.Collections.Concurrent;
using GObj = GObject.Object;
using Microsoft.Extensions.Logging;

namespace Everywhere.Linux.Interop.AtspiBackend.Core;

public sealed class AtspiCacheManager
{
    private readonly ConcurrentDictionary<GObj, object> _cache = new();
    private readonly ILogger _logger;

    public AtspiCacheManager(ILogger logger)
    {
        _logger = logger;
    }
    public T GetOrAdd<T>(GObj gobject, Func<GObj, T> factory) where T : class
    {
        if (gobject == null)
        {
            throw new ArgumentNullException(nameof(gobject));
        }

        var result = _cache.GetOrAdd(gobject, key =>
        {
            var element = factory(key);
            _logger.LogTrace("Created new cached element for GObject {Hash}", key.GetHashCode());
            return element;
        });

        return (T)result;
    }
    public bool TryGet<T>(GObj gobject, out T? element) where T : class
    {
        element = null;

        if (gobject == null)
        {
            return false;
        }

        if (_cache.TryGetValue(gobject, out var cached))
        {
            element = cached as T;
            return element != null;
        }

        return false;
    }
    public bool Remove(GObj gobject)
    {
        if (gobject == null)
        {
            return false;
        }

        if (_cache.TryRemove(gobject, out _))
        {
            _logger.LogDebug("Removed defunct element from cache: GObject {Hash}", gobject.GetHashCode());
            return true;
        }

        return false;
    }
    public void Clear()
    {
        var count = _cache.Count;
        _cache.Clear();
        _logger.LogInformation("Cleared cache: {Count} elements removed", count);
    }
    public int Count => _cache.Count;
    public bool Contains(GObj gobject)
    {
        return gobject != null && _cache.ContainsKey(gobject);
    }
}
using System.Runtime.InteropServices;
using Everywhere.Interop;
using Everywhere.Linux.Interop.AtspiBackend.Core;
using Everywhere.Linux.Interop.AtspiBackend.Native;
using GObj = GObject.Object;
using Microsoft.Extensions.Logging;

namespace Everywhere.Linux.Interop.AtspiBackend.Elements;

public sealed class AtspiElementFactory
{
    private readonly AtspiCacheManager _cache;
    private readonly IWindowBackend _windowBackend;
    private readonly ILogger _logger;

    public AtspiElementFactory(
        AtspiCacheManager cache,
        IWindowBackend windowBackend,
        ILogger logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _windowBackend = windowBackend ?? throw new ArgumentNullException(nameof(windowBackend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    public AtspiElement? GetOrCreateElement(GObj? gobject)
    {
        if (gobject == null)
        {
            return null;
        }

        try
        {
            return _cache.GetOrAdd(gobject, CreateElement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create element for GObject {Hash}", gobject.GetHashCode());
            return null;
        }
    }
    public AtspiElement? GetOrCreateElement(Func<GObj?> provider)
    {
        if (provider == null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        try
        {
            var gobject = provider();
            return GetOrCreateElement(gobject);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get element from provider");
            return null;
        }
    }
    public void RemoveElement(GObj gobject)
    {
        _cache.Remove(gobject);
    }
    public GObj? TryGetRelationTarget(GObj gobject, bool up)
    {
        if (gobject == null)
        {
            return null;
        }

        try
        {
            var relationSetPtr = AtspiNative.atspi_accessible_get_relation_set(
                gobject.Handle,
                IntPtr.Zero);

            if (relationSetPtr == IntPtr.Zero)
            {
                return null;
            }

            using var relationArray = new GObjectArray(relationSetPtr);

            foreach (var relation in relationArray.Iterate())
            {
                var relationType = (AtspiRelationType)AtspiNative.atspi_relation_get_relation_type(
                    relation.Handle);

                var targetCount = AtspiNative.atspi_relation_get_n_targets(relation.Handle);

                // Check if relation type matches direction
                bool isMatch = up
                    ? relationType is AtspiRelationType.EmbeddedBy or AtspiRelationType.SubwindowOf
                    : relationType is AtspiRelationType.Embeds;

                if (!isMatch || targetCount < 1)
                {
                    continue;
                }

                // Get first target
                var targetPtr = AtspiNative.atspi_relation_get_target(relation.Handle, 0);
                if (targetPtr == IntPtr.Zero)
                {
                    continue;
                }

                var target = GObjectWrapper.WrapOwned(targetPtr);

                // Check if target is visible
                if (target != null && IsElementVisibleForRelation(target))
                {
                    return target;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get relation target");
            return null;
        }
    }
    private AtspiElement CreateElement(GObj gobject)
    {
        return new AtspiElement(gobject, this, _windowBackend, _logger);
    }
    private bool IsElementVisibleForRelation(GObj? gobject)
    {
        if (gobject == null)
        {
            return false;
        }

        try
        {
            var stateSetPtr = AtspiNative.atspi_accessible_get_state_set(gobject.Handle);
            if (stateSetPtr == IntPtr.Zero)
            {
                return false;
            }

            var stateSet = GObjectWrapper.WrapOwned(stateSetPtr);
            if (stateSet == null)
            {
                return false;
            }

            var isVisible = AtspiNative.atspi_state_set_contains(
                stateSet.Handle,
                (int)AtspiState.Visible) != 0;

            var isShowing = AtspiNative.atspi_state_set_contains(
                stateSet.Handle,
                (int)AtspiState.Showing) != 0;

            if (!isVisible || !isShowing)
            {
                return false;
            }

            if (AtspiNative.atspi_accessible_is_component(gobject.Handle) != 0)
            {
                var rectPtr = AtspiNative.atspi_component_get_extents(
                    gobject.Handle,
                    (int)AtspiCoordType.Screen,
                    IntPtr.Zero);

                if (rectPtr != IntPtr.Zero)
                {
                    var rect = Marshal.PtrToStructure<AtspiNative.AtspiRect>(rectPtr);
                    AtspiNative.g_free(rectPtr);
                    
                    return rect.width > 0 && rect.height > 0;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class GObjectArray : IDisposable
{
    private readonly IntPtr _arrayPtr;
    private readonly AtspiNative.NativeGArray _array;
    private bool _disposed;

    public GObjectArray(IntPtr arrayPtr)
    {
        _arrayPtr = arrayPtr;
        
        if (_arrayPtr != IntPtr.Zero)
        {
            _array = Marshal.PtrToStructure<AtspiNative.NativeGArray>(_arrayPtr);
        }
    }

    public uint Length => _array.Length;

    public IEnumerable<GObj> Iterate()
    {
        if (_arrayPtr == IntPtr.Zero || _array.Data == IntPtr.Zero)
        {
            yield break;
        }

        for (var i = 0; i < Length; i++)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(GObjectArray));
            }

            var elementPtr = Marshal.ReadIntPtr(_array.Data, i * IntPtr.Size);
            
            if (elementPtr == IntPtr.Zero)
            {
                continue;
            }

            var wrapped = GObjectWrapper.WrapOwned(elementPtr);
            if (wrapped != null)
            {
                yield return wrapped;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed || _arrayPtr == IntPtr.Zero)
        {
            return;
        }

        _disposed = true;
        AtspiNative.g_free(_arrayPtr);
    }
}
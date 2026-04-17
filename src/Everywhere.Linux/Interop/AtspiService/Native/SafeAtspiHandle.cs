using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Everywhere.Linux.Interop.AtspiBackend.Native;

public sealed class SafeAtspiHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly bool _isSingleton;

    private SafeAtspiHandle(bool ownsHandle, bool isSingleton) : base(ownsHandle)
    {
        _isSingleton = isSingleton;
    }

    public static SafeAtspiHandle CreateOwned(IntPtr handle)
    {
        var safeHandle = new SafeAtspiHandle(ownsHandle: true, isSingleton: false);
        safeHandle.SetHandle(handle);
        return safeHandle;
    }

    public static SafeAtspiHandle CreateSingleton(IntPtr handle)
    {
        var safeHandle = new SafeAtspiHandle(ownsHandle: false, isSingleton: true);
        safeHandle.SetHandle(handle);
        return safeHandle;
    }

    public static SafeAtspiHandle CreateNull()
    {
        return new SafeAtspiHandle(ownsHandle: false, isSingleton: false);
    }

    protected override bool ReleaseHandle()
    {
        if (_isSingleton || handle == IntPtr.Zero)
        {
            return true;
        }

        try
        {
            // Call g_object_unref to decrement reference count
            g_object_unref(handle);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("libgobject-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_object_unref(IntPtr obj);
}
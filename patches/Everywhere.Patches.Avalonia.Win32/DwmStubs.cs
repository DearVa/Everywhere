using MonoMod;

// Avalonia's DWM enum is internal. This declaration supplies its compile-time shape and is
// relinked to the target assembly's enum when MonoMod weaves the patch.
// ReSharper disable once CheckNamespace
namespace Avalonia.Win32.Interop;

[MonoModIgnore]
internal static class UnmanagedMethods
{
    [MonoModIgnore]
    internal enum DwmNCRenderingPolicy : uint
    {
        DWMNCRP_USEWINDOWSTYLE,
        DWMNCRP_DISABLED,
        DWMNCRP_ENABLED
    }
}

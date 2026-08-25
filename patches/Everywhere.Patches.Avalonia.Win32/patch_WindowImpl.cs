using Everywhere.Patches.Contracts.Interop;
using MonoMod;

namespace Everywhere.Patches.Avalonia.Win32;

[MonoModPatch("Avalonia.Win32.WindowImpl")]
internal class patch_WindowImpl : IWindowCornerRadiusFeature
{
    private long _everywhereCornerRadiusBits;
    private int _everywhereCornerRadiusConfigured;
    private int _everywhereCornerRadiusSuppressed;
    private int _everywhereNativeFrameRenderingSuppressed;

    public void SetCornerRadius(double radius)
    {
        if (!double.IsFinite(radius) || radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The corner radius must be finite and non-negative.");
        }

        radius = Math.Min(radius, 4096);
        Volatile.Write(ref _everywhereCornerRadiusBits, BitConverter.DoubleToInt64Bits(radius));
        Volatile.Write(ref _everywhereCornerRadiusConfigured, 1);
    }

    public void SetNativeFrameRenderingSuppressed(bool suppressed) =>
        Volatile.Write(ref _everywhereNativeFrameRenderingSuppressed, suppressed ? 1 : 0);

    public void SetCornerRadiusSuppressed(bool suppressed) =>
        Volatile.Write(ref _everywhereCornerRadiusSuppressed, suppressed ? 1 : 0);

    public bool TryGetEffectiveCornerRadius(out double radius)
    {
        if (Volatile.Read(ref _everywhereCornerRadiusConfigured) == 0)
        {
            radius = 0;
            return false;
        }

        if (Volatile.Read(ref _everywhereCornerRadiusSuppressed) != 0)
        {
            radius = 0;
            return true;
        }

        var bits = Volatile.Read(ref _everywhereCornerRadiusBits);
        radius = BitConverter.Int64BitsToDouble(bits);
        return true;
    }

    // Avalonia reapplies its preferred policy whenever it extends the client area or changes the
    // window state. Preserve that behavior for ordinary windows, but keep native rendering disabled
    // after Everywhere has successfully installed the complete custom frame for ChatWindow.
    private extern void orig_SetNCRenderingPolicy(
        global::Avalonia.Win32.Interop.UnmanagedMethods.DwmNCRenderingPolicy value);

    private void SetNCRenderingPolicy(
        global::Avalonia.Win32.Interop.UnmanagedMethods.DwmNCRenderingPolicy value)
    {
        if (Volatile.Read(ref _everywhereNativeFrameRenderingSuppressed) != 0)
        {
            value = global::Avalonia.Win32.Interop.UnmanagedMethods.DwmNCRenderingPolicy.DWMNCRP_DISABLED;
        }

        orig_SetNCRenderingPolicy(value);
    }
}

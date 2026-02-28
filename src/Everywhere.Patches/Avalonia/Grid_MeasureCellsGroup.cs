// resharper disable InconsistentNaming
// resharper disable UnusedParameter.Local

using System.Runtime.CompilerServices;
using Avalonia.Controls;
using HarmonyLib;

namespace Everywhere.Patches.Avalonia;

/// <summary>
/// Fixes random crashes in Grid.MeasureCellsGroup due to ArgumentOutOfRangeException when accessing the cells list.
/// </summary>
/// <remarks>
/// System.ArgumentOutOfRangeException: Index was out of range. Must be non-negative and less than the size of the collection. (Parameter 'index')
/// at void Grid.MeasureCellsGroup(int cellsHead, bool ignoreDesiredSizeU, bool forceInfinityV, out bool hasDesiredSizeUChanged)()
/// at Size Grid.MeasureOverride(Size constraint)()
/// at Size Layoutable.MeasureCore(Size availableSize)()
/// at void Layoutable.Measure(Size availableSize)()
/// at bool LayoutManager.Measure(Layoutable control)()
/// at void LayoutManager.ExecuteLayoutPass()()
/// at void MediaContext.FireInvokeOnRenderCallbacks()()
/// at void MediaContext.RenderCore()()
/// at void DispatcherOperation.InvokeCore()()
/// </remarks>
internal static class Grid_MeasureCellsGroup
{
    public static void Patch(Harmony harmony)
    {
        var target = AccessTools.Method(
            typeof(Grid),
            "MeasureCellsGroup",
            [typeof(int), typeof(bool), typeof(bool), typeof(bool).MakeByRefType()]);
        harmony.CreateReversePatcher(target, new HarmonyMethod(MeasureCellsGroup_Original)).Patch();
        harmony.Patch(target, new HarmonyMethod(Prefix));
    }

    // Original method signature:
    // private void MeasureCellsGroup(
    //     int cellsHead,
    //     bool ignoreDesiredSizeU,
    //     bool forceInfinityV,
    //     out bool hasDesiredSizeUChanged)

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MeasureCellsGroup_Original(
        Grid instance,
        int cellsHead,
        bool ignoreDesiredSizeU,
        bool forceInfinityV,
        out bool hasDesiredSizeUChanged) => throw new NotSupportedException("It's a stub");

    private static bool Prefix(
        Grid __instance,
        ref int cellsHead,
        ref bool ignoreDesiredSizeU,
        ref bool forceInfinityV,
        ref bool hasDesiredSizeUChanged)
    {
        try
        {
            MeasureCellsGroup_Original(
                __instance,
                cellsHead,
                ignoreDesiredSizeU,
                forceInfinityV,
                out hasDesiredSizeUChanged);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Ignored
#if DEBUG
            System.Diagnostics.Debugger.Break();
#endif
        }

        return false;
    }
}
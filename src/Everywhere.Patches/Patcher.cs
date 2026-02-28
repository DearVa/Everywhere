using Everywhere.Patches.Avalonia;
using HarmonyLib;

namespace Everywhere.Patches;

public static class Patcher
{
    public static void PatchAll()
    {
        var harmony = new Harmony("com.sylinko.everywhere");
        TextLeadingPrefixCharacterEllipsis_Collapse.Patch(harmony);
        Grid_MeasureCellsGroup.Patch(harmony);
    }
}
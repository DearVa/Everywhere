using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Platform.Storage;
using HarmonyLib;

namespace Everywhere.Mac.Patches;

/// <summary>
/// Patch for Avalonia.Platform.Storage.FileIO.BclLauncher.Exec on macOS to handle URLs or file paths with spaces or special characters correctly.
/// </summary>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public static class BclLauncherExecPatch
{
    public static void Patch(Harmony harmony)
    {
#pragma warning disable IL2026 // This is safe because Avalonia is a known dependency
        var bclLauncherType = typeof(ILauncher).Assembly.GetType("Avalonia.Platform.Storage.FileIO.BclLauncher");
#pragma warning restore IL2026
        var execMethod = AccessTools.Method(bclLauncherType, "Exec");
        harmony.Patch(execMethod, new HarmonyMethod(PatchedMethod));
    }

    /// <summary>
    /// The implementation of BclLauncher.Exec for macOS does not work well when urlOrFile contains spaces or special characters.
    /// This patch fixes the issue by properly escaping the urlOrFile before passing it to the system command.
    /// </summary>
    /// <param name="urlOrFile"></param>
    /// <param name="__result"></param>
    /// <returns></returns>
    // ReSharper disable once InconsistentNaming
    // ReSharper disable once RedundantAssignment
    private static bool PatchedMethod(ref string urlOrFile, ref bool __result)
    {
        using var process = Process.Start(
            new ProcessStartInfo
            {
                FileName = "open",
                ArgumentList = { urlOrFile }, // Use ArgumentList to avoid issues with spaces/special characters
                CreateNoWindow = true,
            });
        __result = true;
        return false;
    }
}
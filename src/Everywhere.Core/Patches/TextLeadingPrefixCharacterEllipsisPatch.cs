using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using HarmonyLib;

namespace Everywhere.Patches;

/// <summary>
/// This fixes https://github.com/DearVa/Everywhere/issues/313
/// Since TextLeadingPrefixCharacterEllipsis.Collapse is not virtual, we have to patch the method body to change the behavior of the collapsing logic.
/// It also calls internal class and methods that we cannot access directly.
/// </summary>
public static class TextLeadingPrefixCharacterEllipsisPatch
{
    public static void Patch(Harmony harmony)
    {
        var original = AccessTools.Method(typeof(TextLeadingPrefixCharacterEllipsis), nameof(TextLeadingPrefixCharacterEllipsis.Collapse));
        harmony.Patch(original, new HarmonyMethod(Collapse_Patch));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_prefixLength")]
    private static extern ref int GetPrefixLength(TextLeadingPrefixCharacterEllipsis @this);

    // resharper disable InconsistentNaming
    private static bool Collapse_Patch(TextLeadingPrefixCharacterEllipsis __instance, ref TextLine textLine, ref TextRun[]? __result)
    {
        var shapedSymbol = TextFormatter.CreateSymbol(__instance.Symbol, FlowDirection.LeftToRight);

        if (__instance.Width < shapedSymbol.GlyphRun.Bounds.Width)
        {
            __result = [];
            return false;
        }

        var textRunEnumerator = new LogicalTextRunEnumerator(textLine);
        var availableWidth = __instance.Width - shapedSymbol.Size.Width;
        while (textRunEnumerator.MoveNext(out var run))
        {
            if (run is not DrawableTextRun drawableTextRun) continue;
            availableWidth -= drawableTextRun.Size.Width;

            if (!(availableWidth < 0)) continue;
            var objectPool = FormattingObjectPool.Instance;

            // meaningful order when this line contains RTL / reversed textRuns
            var innerTextRunEnumerator = new LogicalTextRunEnumerator(textLine);
            var textRuns = objectPool.TextRunLists.Rent();

            while (innerTextRunEnumerator.MoveNext(out var innerRun))
                textRuns.Add(innerRun);

            var collapsedRuns = objectPool.TextRunLists.Rent();
            FormattingObjectPool.RentedList<TextRun>? rentedPreSplitRuns = null;
            FormattingObjectPool.RentedList<TextRun>? rentedPostSplitRuns = null;

            try
            {
                FormattingObjectPool.RentedList<TextRun>? effectivePostSplitRuns;
                var availableSuffixWidth = __instance.Width - shapedSymbol.Size.Width;

                // prepare the prefix
                if (GetPrefixLength(__instance) > 0)
                {
                    (rentedPreSplitRuns, rentedPostSplitRuns) = TextFormatterImpl.SplitTextRuns(textRuns, GetPrefixLength(__instance), objectPool);
                    effectivePostSplitRuns = rentedPostSplitRuns;
                    if (rentedPreSplitRuns != null)
                    {
                        foreach (var preSplitRun in rentedPreSplitRuns)
                        {
                            collapsedRuns.Add(preSplitRun);
                            if (preSplitRun is DrawableTextRun innerDrawableTextRun)
                            {
                                availableSuffixWidth -= innerDrawableTextRun.Size.Width;
                            }
                        }
                    }
                }
                else
                {
                    effectivePostSplitRuns = textRuns;
                }

                // add Ellipsis symbol
                collapsedRuns.Add(shapedSymbol);

                if (effectivePostSplitRuns is null || availableSuffixWidth <= 0)
                {
                    __result = collapsedRuns.ToArray();
                    return false;
                }

                var suffixStartIndex = collapsedRuns.Count;

                // append the suffix backwards until it gets trimmed
                for (var i = effectivePostSplitRuns.Count - 1; i >= 0; i--)
                {
                    var innerRun = effectivePostSplitRuns[i];

                    if (innerRun is ShapedTextRun endShapedRun)
                    {
                        if (endShapedRun.TryMeasureCharactersBackwards(
                                availableSuffixWidth,
                                out var suffixCount,
                                out var suffixWidth))
                        {
                            if (endShapedRun.IsReversed)
                                endShapedRun.Reverse();

                            availableSuffixWidth -= suffixWidth;

                            if (suffixCount >= innerRun.Length)
                            {
                                collapsedRuns.Insert(suffixStartIndex, innerRun);
                            }
                            else
                            {
                                var splitSuffix = endShapedRun.Split(innerRun.Length - suffixCount);
                                collapsedRuns.Insert(suffixStartIndex, splitSuffix.Second!);
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                    else if (innerRun is DrawableTextRun innerDrawableTextRun)
                    {
                        availableSuffixWidth -= innerDrawableTextRun.Size.Width;

                        // entire run must fit
                        if (availableSuffixWidth >= 0)
                            collapsedRuns.Insert(suffixStartIndex, innerRun);
                        else
                            break;
                    }
                }

                __result = collapsedRuns.ToArray();
                return false;
            }
            finally
            {
                objectPool.TextRunLists.Return(ref rentedPreSplitRuns);
                objectPool.TextRunLists.Return(ref rentedPostSplitRuns);
                objectPool.TextRunLists.Return(ref collapsedRuns);
                objectPool.TextRunLists.Return(ref textRuns);
            }
        }

        __result = null;
        return false;
    }
    // resharper restore InconsistentNaming
}
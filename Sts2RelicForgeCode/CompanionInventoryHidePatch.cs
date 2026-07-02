using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace Sts2RelicForge;

/// <summary>
/// Keep hidden companion relics out of the top-bar relic inventory, WITHOUT desyncing the
/// icon list from player.Relics indices.
///
/// Two jobs, both in NRelicInventory.Add(relic, startsShown, index):
///  1. Companion relic → return false, so no icon node is created for it (the effect still
///     fires — it's in player.Relics — only the UI icon is suppressed).
///  2. Non-companion relic → the game passes index = player.Relics.IndexOf(relic), which
///     COUNTS the hidden companions that live in player.Relics but were skipped in step 1.
///     That index can exceed _relicNodes.Count and make Insert() throw (ArgumentOutOfRange),
///     which silently drops the host relic's icon. Companions are always appended to
///     player.Relics, so an inflated index is always "too big" — clamp it to the node count
///     (append position) to land the host at the correct visual slot.
/// </summary>
[HarmonyPatch(typeof(NRelicInventory), "Add")]
internal static class CompanionInventoryHidePatch
{
    private static bool Prefix(NRelicInventory __instance, RelicModel relic, ref int index)
    {
        try
        {
            if (RelicForgeService.IsCompanion(relic)) return false; // no icon for a companion

            // Correct the index for hidden companions that inflate player.Relics.IndexOf.
            // Only positive indices are affected; -1 already means "append".
            int nodeCount = __instance.RelicNodes.Count;
            if (index > nodeCount) index = nodeCount;
            return true;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] companion icon-hide failed: {e.Message}");
            return true; // on error, fall back to normal behavior rather than dropping a relic
        }
    }
}

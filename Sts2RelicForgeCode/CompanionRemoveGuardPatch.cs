using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace Sts2RelicForge;

/// <summary>
/// A hidden companion has no icon node (CompanionInventoryHidePatch skips its Add), so if the
/// game ever removes it (RelicCmd.Remove → RelicRemoved event → NRelicInventory.Remove), the
/// Remove method's `_relicNodes.First(n => n.Relic.Model == relic)` would throw
/// (InvalidOperationException: no matching node). Skip Remove for companion instances — there
/// is nothing to un-render.
/// </summary>
[HarmonyPatch(typeof(NRelicInventory), "Remove")]
internal static class CompanionRemoveGuardPatch
{
    private static bool Prefix(RelicModel relic)
    {
        try
        {
            return !RelicForgeService.IsCompanion(relic); // false = skip (no node to remove)
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] companion remove-guard failed: {e.Message}");
            return true;
        }
    }
}

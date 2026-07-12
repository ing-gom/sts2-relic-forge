using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Clean up a forged relic's grafted companion when the HOST leaves the inventory. Player.RemoveRelicInternal
/// is the single choke point for ALL relic removal (RelicCmd.Remove → RemoveRelicInternal, and a foreign
/// "Sell" mod funnels through it too), mirroring how RelicObtainPatch hooks the RelicCmd.Obtain choke point.
///
/// Bug: selling a relic whose prefix grafted a hidden companion (e.g. a graft-family prefix) left that
/// companion in player.Relics, so its effect kept firing after the visible relic was gone ("词缀效果依然在
/// 后台生效"). Numeric prefixes and enemy-rider / self-curses need no cleanup — they read only from relics
/// still owned — but the hidden companion is itself an owned relic, so it must be un-grafted here.
///
/// Gated to NON-silent removals: our own companion un-graft (RemoveCompanions) removes with silent:true,
/// so this never re-enters on that path — only genuine sells/removes (which fire RelicRemoved for the UI)
/// trigger the cleanup. The extra IsCompanion guard keeps a stray non-silent companion removal from being
/// mistaken for a host. Deterministic + scan-based (see RelicForgeService.OnHostRemoved), so co-op peers
/// all converge — the same design that keeps reforge un-grafts from desyncing.
/// </summary>
[HarmonyPatch(typeof(Player), nameof(Player.RemoveRelicInternal))]
internal static class HostRemoveCleanupPatch
{
    private static void Postfix(Player __instance, RelicModel relic, bool silent)
    {
        try
        {
            if (silent) return;                                  // internal shuffle / our own companion un-graft
            if (RelicForgeService.IsCompanion(relic)) return;    // a companion has no companions of its own
            RelicForgeService.OnHostRemoved(relic, __instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] host-remove cleanup failed: {e.Message}");
        }
    }
}

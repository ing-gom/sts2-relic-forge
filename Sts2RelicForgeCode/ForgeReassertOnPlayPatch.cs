using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;     // CardPlay
using MegaCrit.Sts2.Core.Entities.Players;   // Player
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;             // RelicModel

namespace Sts2RelicForge;

/// <summary>
/// Re-assert forged BASE VALUES after each card play — the intra-combat companion to
/// <see cref="CombatStartForgeHealPatch"/> (which only fires at turn start). A foreign MID-COMBAT state
/// restore — most notably the Rewind mod's in-combat turn rewind — reconstructs the run through
/// NGame.LoadRun (so <see cref="RunLoadReforgePatch"/> restores the forge RECORD), but can leave the live
/// DynamicVars at their canonical values until something re-applies them. The turn-start heal then only
/// catches it on the NEXT turn, so the boost looks "lost" for the rest of the current turn (the reported
/// Rewind incompatibility: "rewind from turn 4 to turn 2 and the forge effect disappears").
///
/// This runs on every card play and re-applies the stored deltas via
/// <see cref="RelicForgeService.ReassertForgeVars"/> — idempotent (a no-op unless a var was reset to
/// canonical), numeric-only (no companion graft / no seed re-derive), synchronous (no networked dispatch)
/// and deterministic, so it is cheap in normal play and co-op-safe. A record-less fresh instance still
/// relies on the full turn-start re-derive; this only restores relics whose record survived the rebuild
/// (which the LoadRun-based rewind produces).
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
internal static class ForgeReassertOnPlayPatch
{
    private static void Postfix(CardPlay cardPlay)
    {
        try
        {
            if (cardPlay?.Card?.Owner is not Player player) return;
            foreach (var relic in new List<RelicModel>(player.Relics))
                RelicForgeService.ReassertForgeVars(relic);
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] forge reassert on play failed: {e.Message}"); }
    }
}

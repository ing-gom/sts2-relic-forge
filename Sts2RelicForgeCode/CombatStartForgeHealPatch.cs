using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Makes forge state survive a combat restart done by ANOTHER mod. The grade lives only on the
/// in-memory DynamicVarSet + the per-instance record table (never serialized), and is re-derived by
/// just two hooks — pickup and NGame.LoadRun. A foreign "restart/retry combat" feature (e.g. Better
/// Spire 2's double-R) restores state WITHOUT calling LoadRun, so the affix silently changes or
/// vanishes (a Save &amp; Quit -> reload keeps it fine because that DOES go through LoadRun).
///
/// We re-derive at combat start instead of depending on that one narrow hook. This is a PREFIX on the
/// same combat-start choke the fallback / combat-affix effects use (<see cref="FallbackBuffPatch"/>,
/// <see cref="ForgeCombatAffixPatch"/>) — running BEFORE those postfixes so the records they read are
/// already restored. <see cref="RelicForgeService.HealForge"/> is idempotent (a no-op when the forge is
/// intact) and deterministic on every peer, so a normal combat pays nothing and co-op stays in sync.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
internal static class CombatStartForgeHealPatch
{
    private static void Prefix(Player player)
    {
        try
        {
            if (player == null) return;
            // Every player turn start (turn >= 1), not just turn 1: a foreign "reset round" (BetterSpire2
            // double-tap R) reconstructs the combat WITHOUT calling NGame.LoadRun, and may restore mid-
            // combat rather than at turn 1 — so gating to turn 1 could miss it. Healing is idempotent (a
            // no-op once the forge is intact and the re-derive fires at most once), so running every turn
            // costs nothing in normal play while catching a restore at any turn. `turn <= 0` skips setup.
            if ((player.PlayerCombatState?.TurnNumber ?? 0) <= 0) return;

            // Snapshot: a re-derive can graft a hidden companion (mutates player.Relics), so iterate a copy.
            foreach (var relic in new List<RelicModel>(player.Relics))
                RelicForgeService.HealForge(relic, player);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] combat-start forge heal failed: {e.Message}");
        }
    }
}

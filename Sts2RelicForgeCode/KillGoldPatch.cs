using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Plundering (강탈의) — gain gold each time an enemy is killed (Prefix.KillGold), fired from
/// <see cref="Hook.AfterDeath"/>. Gold is HOST-AUTHORITATIVE and REPLICATED (the game's own gold gain pairs
/// PlayerCmd.GainGold with RewardSynchronizer.SyncLocalObtainedGold, and the mod's shop path likewise needs
/// SyncLocalGoldLost), so — like the Overclocked Max-HP fix — this applies to the LOCAL player only and calls
/// SyncLocalObtainedGold, then chains AWAITED onto the hook's Task. Each peer credits its own player; the sync
/// carries it to the other peer, so no double-count and no desync.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDeath))]
internal static class KillGoldPatch
{
    private static void Postfix(ref Task __result, Creature creature, bool wasRemovalPrevented)
        => __result = After(__result, creature, wasRemovalPrevented);

    private static async Task After(Task original, Creature creature, bool wasRemovalPrevented)
    {
        await original;
        try
        {
            if (wasRemovalPrevented) return;                            // death was prevented — not a kill
            if (creature == null || creature.Player != null) return;   // only ENEMY deaths pay out
            var run = RunManager.Instance;
            var players = run?.State?.Players;
            if (players == null) return;
            bool sp = run.IsSingleplayerOrFakeMultiplayer;
            foreach (var player in players)
            {
                if (!(sp || LocalContext.IsMe(player))) continue;      // LOCAL player only (host-authoritative gold)
                int gold = 0;
                foreach (var relic in new List<RelicModel>(player.Relics))
                {
                    if (RelicForgeService.IsForgeEffectSuppressed(relic)) continue;
                    var rec = RelicForgeService.RecordFor(relic);
                    if (rec == null || rec.Prefix.Length == 0) continue;
                    var pfx = PrefixTable.ByName(rec.Prefix);
                    if (pfx != null && pfx.KillGold > 0) gold += pfx.KillGold;
                }
                if (gold <= 0) continue;
                await PlayerCmd.GainGold(gold, player, false);
                run.RewardSynchronizer?.SyncLocalObtainedGold(gold);   // networked-safe, same as the game's own gold gain
                MainFile.Logger.Info($"[{MainFile.ModId}] Plundering: +{gold} gold on kill ({creature.Name}).");
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] kill-gold hook failed: {e.Message}"); }
    }
}

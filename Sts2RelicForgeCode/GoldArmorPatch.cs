using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Bribing (매수의) — pay gold to shave incoming enemy hits: each enemy hit you take deals 1 LESS damage, and each
/// such reduction spends <see cref="Prefix.GoldArmorCost"/> gold. When you can't afford it, the hit lands in full.
///
/// Split across the same two chokepoints the mod already trusts, so the co-op-hard gold state is never mutated in a
/// calc path:
///   • <see cref="Hook.ModifyDamage"/> (PURE calc, like Calloused's <see cref="EnemyDamageReductionPatch"/>) subtracts
///     1 from an enemy→player hit WHEN the owner can afford the cost. Gold is host-authoritative but REPLICATED, so
///     both peers read the same balance and compute the identical reduced number — and it flows into the enemy-intent
///     PREVIEW too. No spend here (ModifyDamage runs speculatively / multiple times).
///   • <see cref="Hook.AfterDamageReceived"/> (AWAITED, chained) spends the gold ONCE per HP-damaging hit, LOCAL player
///     only + RewardSynchronizer.SyncLocalGoldLost — the exact host-authoritative gold pattern of KillGoldPatch / the
///     shop reforge. A fully-BLOCKED hit (UnblockedDamage 0) shaved a point of block for free; only hits that reach HP
///     charge, matching "pay to protect your HP".
/// The reduce-gate and the spend-gate both test gold >= cost, and gold can't change between them within one lockstep
/// step, so every reduction that reaches HP is charged and vice-versa (the sole slack is the block-only freebie above).
/// </summary>
internal static class GoldArmor
{
    /// <summary>The highest Bribing cost among the player's LIVE forged relics (0 = none carried).</summary>
    internal static int CostFor(Player player)
    {
        int cost = 0;
        foreach (var relic in new List<RelicModel>(player.Relics))
        {
            if (RelicForgeService.IsForgeEffectSuppressed(relic)) continue;
            var rec = RelicForgeService.RecordFor(relic);
            if (rec == null || rec.Prefix.Length == 0) continue;
            var pfx = PrefixTable.ByName(rec.Prefix);
            if (pfx != null && pfx.GoldArmorCost > cost) cost = pfx.GoldArmorCost;
        }
        return cost;
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
internal static class GoldArmorReducePatch
{
    private static void Postfix(ref decimal __result, Creature? target, Creature? dealer, ModifyDamageHookType modifyDamageHookType)
    {
        try
        {
            if (__result <= 0m) return;
            if (!modifyDamageHookType.HasFlag(ModifyDamageHookType.Additive)) return;   // apply once, on the full pass
            if (target?.Player == null) return;                                          // hits on a PLAYER only
            if (dealer == null || dealer.Player != null) return;                         // dealt by an ENEMY only
            int cost = GoldArmor.CostFor(target.Player);
            if (cost <= 0) return;                                                        // no live Bribing relic
            if ((int)target.Player.Gold < cost) return;                                   // broke → no reduction
            __result -= 1m;
            if (__result < 0m) __result = 0m;
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] gold-armor reduce failed: {e.Message}"); }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageReceived))]
internal static class GoldArmorSpendPatch
{
    private static void Postfix(ref Task __result, Creature target, DamageResult result, Creature? dealer)
        => __result = After(__result, target, result, dealer);

    private static async Task After(Task original, Creature target, DamageResult result, Creature? dealer)
    {
        await original;
        try
        {
            if (result.UnblockedDamage <= 0) return;                 // block held → nothing reached HP, nothing to pay
            if (target?.Player == null) return;                      // the PLAYER took it
            if (dealer == null || dealer.Player != null) return;     // from an ENEMY
            var player = target.Player;
            int cost = GoldArmor.CostFor(player);
            if (cost <= 0) return;
            var run = RunManager.Instance;
            bool sp = run?.IsSingleplayerOrFakeMultiplayer ?? true;
            if (!(sp || LocalContext.IsMe(player))) return;          // LOCAL player only (host-authoritative gold)
            if ((int)player.Gold < cost) return;                     // same gate as the reduction — broke ⇒ wasn't reduced
            await PlayerCmd.LoseGold(cost, player, GoldLossType.Spent);
            run?.RewardSynchronizer?.SyncLocalGoldLost(cost);        // networked-safe, same as the shop reforge charge
            MainFile.Logger.Info($"[{MainFile.ModId}] Bribing: -{cost} gold to shave a hit ({dealer.Name}).");
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] gold-armor spend failed: {e.Message}"); }
    }
}

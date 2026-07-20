using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Executing (처단의) — deal <see cref="Prefix.ExecutePct"/>% MORE damage to an enemy at or below 20% max HP,
/// shifting target priority toward finishing the wounded. A Harmony postfix on <see cref="Hook.ModifyDamage"/>
/// (like Calloused's <see cref="EnemyDamageReductionPatch"/> but the mirror side): a pure deterministic scale of
/// a player→enemy hit, no state mutation, ownership + the target's HP read from replicated state — so both peers
/// compute the identical number and it flows into the damage PREVIEW too. Co-op-safe by construction.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
internal static class ExecutePatch
{
    private static void Postfix(ref decimal __result, Creature? target, Creature? dealer, ModifyDamageHookType modifyDamageHookType)
    {
        try
        {
            if (__result <= 0m) return;
            if (!modifyDamageHookType.HasFlag(ModifyDamageHookType.Additive)) return;   // apply once, on the full pass
            if (target == null || target.Player != null) return;      // hits on an ENEMY only
            if (dealer?.Player == null) return;                        // dealt BY a player
            if (target.MaxHp <= 0) return;
            if (target.CurrentHp * 5 > target.MaxHp) return;           // strictly above 20% max HP → no bonus
            int pct = ExecutePctFor(dealer.Player);
            if (pct <= 0) return;
            __result = __result * (100 + pct) / 100m;
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] execute hook failed: {e.Message}"); }
    }

    /// <summary>The strongest Executing bonus among the dealer's live forged relics (0 = none carried).</summary>
    private static int ExecutePctFor(Player player)
    {
        int pct = 0;
        foreach (var relic in new List<RelicModel>(player.Relics))
        {
            if (RelicForgeService.IsForgeEffectSuppressed(relic)) continue;
            var rec = RelicForgeService.RecordFor(relic);
            if (rec == null || rec.Prefix.Length == 0) continue;
            var pfx = PrefixTable.ByName(rec.Prefix);
            if (pfx != null && pfx.ExecutePct > pct) pct = pfx.ExecutePct;
        }
        return pct;
    }
}

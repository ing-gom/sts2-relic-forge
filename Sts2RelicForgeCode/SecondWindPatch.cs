using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2RelicForge;

/// <summary>
/// Renewing (재기의) — a once-per-combat comeback: the FIRST time the owner's HP drops to 50% max HP or below in a
/// fight, they gain Block (<see cref="Prefix.SecondWindBlock"/>). Rides <see cref="Hook.AfterDamageReceived"/>
/// (chained, awaited) and fires on the hit that CROSSES the threshold (UnblockedDamage &gt; 0 → HP actually fell).
/// Block is deterministic combat state applied on both peers, so it converges in co-op; the once-per-combat guard is
/// a per-peer tracker keyed on the live combat-state instance (auto-resets when a new combat begins) plus the
/// player's NetId, and both peers cross the threshold on the same synchronized hit, so they fire together. Pairs
/// with Cornered (the low-HP extra draw) as the mod's low-HP synergy pair.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageReceived))]
internal static class SecondWindPatch
{
    private static ICombatState? _combat;
    private static readonly HashSet<ulong> _fired = new();

    private static void Postfix(ref Task __result, Creature target, DamageResult result)
        => __result = After(__result, target, result);

    private static async Task After(Task original, Creature target, DamageResult result)
    {
        await original;
        try
        {
            if (result.UnblockedDamage <= 0) return;             // no HP lost → the threshold can't be freshly crossed
            var player = target?.Player;
            if (player == null) return;                          // the PLAYER took it
            var self = target!;
            if (self.CurrentHp * 2 > self.MaxHp) return;         // still above 50% max HP

            int block = 0;
            foreach (var relic in new List<RelicModel>(player.Relics))
            {
                if (RelicForgeService.IsForgeEffectSuppressed(relic)) continue;
                var rec = RelicForgeService.RecordFor(relic);
                if (rec == null || rec.Prefix.Length == 0) continue;
                var pfx = PrefixTable.ByName(rec.Prefix);
                if (pfx != null && pfx.SecondWindBlock > block) block = pfx.SecondWindBlock;
            }
            if (block <= 0) return;                              // no Renewing relic carried

            var cs = self.CombatState;
            if (!ReferenceEquals(cs, _combat)) { _combat = cs; _fired.Clear(); }   // new combat → reset the guard
            if (!_fired.Add(player.NetId)) return;               // already fired this combat

            await CreatureCmd.GainBlock(self, block, ValueProp.Unpowered, null);
            MainFile.Logger.Info($"[{MainFile.ModId}] Renewing: +{block} Block on first drop to <=50% HP.");
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] renewing hook failed: {e.Message}"); }
    }
}

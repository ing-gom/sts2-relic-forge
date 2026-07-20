using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;

namespace Sts2RelicForge;

/// <summary>
/// Adrenal (투지의) — a hit-reactive BOON prefix. Each time the OWNER takes UNBLOCKED damage FROM AN ENEMY
/// (block failed → real HP lost), a per-hit roll of <see cref="Prefix.HitEnergyPercent"/> may grant 1 bonus
/// Energy. "Pain fuels you": the payoff only comes when you're actually being hit, so it rewards an
/// aggressive / low-block style rather than being a free upgrade.
///
/// Mirrors <see cref="UnblockedHitPenaltyPatch"/> (the self-curse side of the same hook): patches the awaited
/// <see cref="Hook.AfterDamageReceived"/> and CHAINS onto <c>__result</c>, because that hook fires once PER
/// damage instance inside CreatureCmd.Damage's multi-hit loop — a detached GainEnergy would race the
/// remaining hits and the kill resolution and desync co-op (the Cursefed class). The roll is stateless and
/// deterministic: seeded from the POST-hit CurrentHp (which strictly decreases per unblocked hit, so
/// consecutive hits roll independently) plus turn / damage / relic id — every input is host-authoritative
/// and reproduces on reload and on every peer, with no per-hit counter to serialize.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageReceived))]
internal static class HitEnergyAffixPatch
{
    private static void Postfix(ref Task __result, PlayerChoiceContext choiceContext, Creature target, DamageResult result, Creature? dealer)
        => __result = After(__result, target, result, dealer);

    private static async Task After(Task original, Creature target, DamageResult result, Creature? dealer)
    {
        await original;
        try
        {
            if (result.UnblockedDamage <= 0) return;             // block held → no HP lost, no fuel
            Player? player = target?.Player;
            if (player == null) return;                          // only the player's own creature
            if (dealer != null && dealer.Player != null) return; // enemy-sourced only (ignore self/ally HP loss)

            uint seed = player.RunState?.Rng.Seed ?? 0;
            int turn = player.PlayerCombatState?.TurnNumber ?? 0;
            int hp = player.Creature?.CurrentHp ?? 0;            // post-hit HP → distinct per unblocked hit

            foreach (var relic in new List<RelicModel>(player.Relics))
            {
                if (RelicForgeService.IsForgeEffectSuppressed(relic)) continue;   // dead relic → no forge affix
                var rec = RelicForgeService.RecordFor(relic);
                if (rec == null || rec.Prefix.Length == 0) continue;
                var pfx = PrefixTable.ByName(rec.Prefix);
                if (pfx == null || pfx.HitEnergyPercent <= 0) continue;

                var rng = new Rng((uint)((int)seed + turn * 48611 + hp * 769 + result.UnblockedDamage * 919
                                         + StringHelper.GetDeterministicHashCode(relic.Id.Entry)));
                if (rng.NextFloat() * 100f >= MetaAffix.ChancePct(pfx.HitEnergyPercent, player)) continue;   // Catalytic aura doubles it

                relic.Flash();
                await PlayerCmd.GainEnergy(1m, player);
                MainFile.Logger.Info($"[{MainFile.ModId}] Adrenal: +1 energy on a {result.UnblockedDamage}-HP hit (turn {turn}, hp {hp}) ({relic.Id.Entry}).");
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] hit-energy affix failed: {e.Message}"); }
    }
}

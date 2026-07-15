using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2RelicForge;

/// <summary>
/// FALLBACK prefixes (Honed / Bulwarked / Nimble / Barbed). A magnitude prefix that scaled NOTHING on
/// a relic is replaced (in RelicForgeService.Forge) by one of these — a host-independent, chance-gated
/// minor buff. This applies it: at combat start, a per-combat roll of the record's stored chance
/// (ForgeRecord.FallbackPercent, derived from the fizzled tier) either grants the stat or doesn't, so
/// the relic reliably does SOMETHING across a run while its odds are honestly shown on the tooltip.
///
/// Fires from Hook.AfterPlayerTurnStart (turn 1 only) — the same setup-end choke point the delayed /
/// combat-affix prefixes use (a co-op-verified safe spot for a fire-and-forget grant). The roll is
/// seeded from (run seed, TotalFloor, relic id): deterministic and identical on every peer, and
/// TotalFloor (which increments each room) makes it vary per combat rather than being fixed for the run.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
internal static class FallbackBuffPatch
{
    private static void Postfix(ICombatState combatState, PlayerChoiceContext choiceContext, Player player)
    {
        try
        {
            int turn = player.PlayerCombatState?.TurnNumber ?? 0;
            if (turn != 1) return;                         // combat start only (fires once per combat)
            var creature = player.Creature;
            if (creature == null) return;

            uint runSeed = player.RunState.Rng.Seed;
            int floor = player.RunState.TotalFloor;        // per-combat nonce (increments each room)

            // Snapshot: applying a power won't change player.Relics, but be safe.
            foreach (var relic in new List<RelicModel>(player.Relics))
            {
                if (RelicForgeService.IsForgeEffectSuppressed(relic)) continue;   // dead relic (spent/disabled/saturated) — no forge buff
                var rec = RelicForgeService.RecordFor(relic);
                if (rec == null || rec.FallbackPercent <= 0 || rec.FallbackStat.Length == 0) continue;

                // Deterministic per-(relic, combat) roll — matches ForgeCombatAffixPatch's idiom but
                // keyed on TotalFloor (not turn) so it re-rolls each combat instead of every turn. The
                // test command can force a relic to always fire WITHOUT touching the displayed chance.
                if (!RelicForgeService.IsForceFire(relic))
                {
                    var rng = new Rng((uint)((int)runSeed + floor * 68917
                                             + StringHelper.GetDeterministicHashCode(relic.Id.Entry)));
                    if (rng.NextFloat() * 100f >= rec.FallbackPercent) continue;   // didn't fire this combat
                }

                decimal amt = rec.FallbackAmount;
                relic.Flash();                              // pulse the host (prefixed) relic
                switch (rec.FallbackStat)
                {
                    case "Strength":
                        TaskHelper.RunSafely(PowerCmd.Apply<StrengthPower>(choiceContext, creature, amt, creature, null)); break;
                    case "Dexterity":
                        TaskHelper.RunSafely(PowerCmd.Apply<DexterityPower>(choiceContext, creature, amt, creature, null)); break;
                    case "Thorns":
                        TaskHelper.RunSafely(PowerCmd.Apply<ThornsPower>(choiceContext, creature, amt, creature, null)); break;
                    case "Block":
                        TaskHelper.RunSafely(CreatureCmd.GainBlock(creature, amt, ValueProp.Unpowered, null)); break;
                    // Penalty fallbacks — a self-debuff (applier = self, so it never reads as an enemy hit).
                    case "Weak":
                        TaskHelper.RunSafely(PowerCmd.Apply<WeakPower>(choiceContext, creature, amt, creature, null)); break;
                    case "Frail":
                        TaskHelper.RunSafely(PowerCmd.Apply<FrailPower>(choiceContext, creature, amt, creature, null)); break;
                    case "Vulnerable":
                        TaskHelper.RunSafely(PowerCmd.Apply<VulnerablePower>(choiceContext, creature, amt, creature, null)); break;
                    // Stat-down penalty fallbacks: NEGATE the amount so Strength/Dexterity are REDUCED
                    // (the mirror of Honed/Nimble). Applier = self, so it's a self-inflicted combat-long loss.
                    case "StrengthDown":
                        TaskHelper.RunSafely(PowerCmd.Apply<StrengthPower>(choiceContext, creature, -amt, creature, null)); break;
                    case "DexterityDown":
                        TaskHelper.RunSafely(PowerCmd.Apply<DexterityPower>(choiceContext, creature, -amt, creature, null)); break;
                    default:
                        continue;
                }
                MainFile.Logger.Info($"[{MainFile.ModId}] fallback fired ({rec.FallbackPercent}% {rec.FallbackStat} {rec.FallbackAmount:+#;-#;0}) on {relic.Id.Entry} [{rec.Prefix}].");
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] fallback buff apply failed: {e.Message}");
        }
    }
}

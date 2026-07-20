using System;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Random;

namespace Sts2RelicForge;

/// <summary>
/// REACTIVE enemy-rider curses (see <see cref="EnemyForge"/> / <see cref="RiderSuffix"/>): unlike the fixed
/// combat-start / per-turn enemy buffs, these fire off DAMAGE events on a forged enemy's <see cref="EnemyForgeTag"/>:
///   Fury / 격노 (Enraging)  — each time the PLAYER hits a forged enemy, that enemy gains Strength.
///   Cruelty / 가학 (Sadistic) — each time a forged enemy damages the player, that enemy gains Strength.
///   the Hex / 주술 (Hexing)   — when a forged enemy damages the player, 50% to apply Weak/Frail/Vulnerable to the player.
///
/// Mirrors <see cref="UnblockedHitPenaltyPatch"/>: patches the awaited <see cref="Hook.AfterDamageReceived"/> and
/// CHAINS onto <c>__result</c> so every effect runs AWAITED IN-ORDER inside the synchronized damage action — the
/// co-op-safe path proven this session (a detached apply races the checksum). Enemy Strength / a player debuff are
/// deterministically simulated on both peers (enemy state, like <see cref="ForgeCombatAffixPatch"/>'s enemy strip),
/// and the Hex roll is seeded from host-authoritative synced state, so every peer resolves it identically.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageReceived))]
internal static class EnemyReactiveCursePatch
{
    private static readonly string[] Debuffs = { "Weak", "Frail", "Vulnerable" };

    private static void Postfix(ref Task __result, PlayerChoiceContext choiceContext, Creature target, DamageResult result, Creature? dealer)
        => __result = After(__result, choiceContext, target, result, dealer);

    private static async Task After(Task original, PlayerChoiceContext ctx, Creature target, DamageResult result, Creature? dealer)
    {
        await original;
        try
        {
            if (result.UnblockedDamage <= 0) return;   // block held → nothing landed

            // Fury / Enraging: the player hit a forged ENEMY → it grows angrier (gains Strength).
            if (target != null && target.Player == null && dealer != null && dealer.Player != null)
            {
                var tag = EnemyForge.TagOf(target);
                if (tag != null && tag.OnHitStr > 0)
                    await PowerCmd.Apply<StrengthPower>(ctx, target, tag.OnHitStr, target, null);
                return;
            }

            // Cruelty / Sadistic + the Hex / Hexing: a forged ENEMY damaged the PLAYER.
            if (target?.Player != null && dealer != null && dealer.Player == null)
            {
                var tag = EnemyForge.TagOf(dealer);
                if (tag == null) return;

                if (tag.OnDealStr > 0)                                       // Sadistic: the enemy gains Strength
                    await PowerCmd.Apply<StrengthPower>(ctx, dealer, tag.OnDealStr, dealer, null);

                if (tag.OnDealHealPct > 0 && dealer.IsAlive)                 // Vampiric: the enemy heals for the damage it dealt
                {
                    int heal = result.UnblockedDamage * tag.OnDealHealPct / 100;
                    if (heal > 0) await CreatureCmd.Heal(dealer, heal, playAnim: false);
                }

                if (tag.OnDealCard)                                          // Fouling: a Wound clogs your discard
                {
                    var card = target.CombatState?.CreateCard<Wound>(target.Player);
                    if (card != null) await CardPileCmd.Add(card, PileType.Discard);
                }

                if (tag.OnDealDebuff)                                        // Hexing: 50% to debuff the player
                {
                    var self = target.Player.Creature;
                    if (self == null) return;
                    // Deterministic per-hit roll from synced state (post-hit player HP strictly decreases → distinct).
                    uint seed = target.Player.RunState?.Rng.Seed ?? 0;
                    int turn = target.Player.PlayerCombatState?.TurnNumber ?? 0;
                    var rng = new Rng((uint)((int)seed + turn * 40129 + target.CurrentHp * 769 + result.UnblockedDamage * 733
                                             + StringHelper.GetDeterministicHashCode(dealer.Name ?? "")));
                    if (rng.NextFloat() >= 0.5f) return;                    // 50% miss
                    int pick = (int)(rng.NextFloat() * Debuffs.Length);
                    switch (pick >= Debuffs.Length ? Debuffs.Length - 1 : pick)
                    {
                        case 0:  await PowerCmd.Apply<WeakPower>(ctx, self, 1m, self, null); break;
                        case 1:  await PowerCmd.Apply<FrailPower>(ctx, self, 1m, self, null); break;
                        default: await PowerCmd.Apply<VulnerablePower>(ctx, self, 1m, self, null); break;
                    }
                }
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] enemy reactive curse failed: {e.Message}"); }
    }
}

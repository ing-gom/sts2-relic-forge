using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2RelicForge;

/// <summary>
/// Harmony patches for the character-gated prefix family — each rides a per-event game Hook and calls
/// the matching handler in <see cref="CharAffix"/>. Same idiom as <see cref="ReactiveAffixPatches"/>:
/// the mod tags existing relics with a prefix, so it can't override relic virtuals; it reads the event
/// and dispatches networked commands. Multiple mod patches on one Hook (AfterPlayerTurnStart,
/// AfterCardPlayed, AfterPowerAmountChanged) stack — Harmony runs them all.
/// </summary>
internal static class CharAffixPatches
{
    /// <summary>Live-combat seam over ICombatState.CreateCard so the engine can build cards without
    /// taking a dependency on the combat-state type.</summary>
    private readonly struct CsAccess : ICombatStateAccess
    {
        private readonly ICombatState _cs;
        public CsAccess(ICombatState cs) { _cs = cs; }
        public CardModel CreateShiv(Player player) => _cs.CreateCard<Shiv>(player);
    }

    /// <summary>Poison / Doom applied to an enemy → +1 more (Envenomed / Dooming); permanent Focus
    /// self-gain → Focused's first-of-combat +1. Fires post-modify, so amount is the final delta.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPowerAmountChanged))]
    internal static class PowerPatch
    {
        private static void Postfix(PlayerChoiceContext choiceContext, PowerModel power, decimal amount, Creature? applier)
        {
            try
            {
                if (amount <= 0m) return;
                var owner = power.Owner;
                if (owner == null) return;
                Player? actor = applier?.Player ?? applier?.PetOwner;

                if (power is PoisonPower && applier?.Side == CombatSide.Player && owner.Side == CombatSide.Enemy && actor != null)
                    CharAffix.OnPoisonApplied(choiceContext, actor, owner);
                else if (power is DoomPower && applier?.Side == CombatSide.Player && owner.Side == CombatSide.Enemy && actor != null)
                    CharAffix.OnDoomApplied(choiceContext, actor, owner);
                else if (power is FocusPower)
                {
                    if (owner.Side == CombatSide.Player && applier?.Side == CombatSide.Player && owner.Player != null)
                        CharAffix.OnFocusGained(choiceContext, owner.Player);
                }
            }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] char power hook failed: {e.Message}"); }
        }
    }

    /// <summary>Dulled (curse) — clamp the finalized poison amount the player applies to an enemy so
    /// the enemy's total cannot exceed the cap. Same Hook as ReactiveAffixPatch's invert patch —
    /// Harmony stacks both postfixes.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyPowerAmountReceived))]
    internal static class PoisonCapPatch
    {
        private static void Postfix(PowerModel canonicalPower, Creature target, Creature? giver, ref decimal __result)
        {
            try
            {
                if (canonicalPower is PoisonPower && __result > 0m)
                    __result = CharAffix.ClampPoisonForDulled(giver, target, __result);
            }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] poison cap hook failed: {e.Message}"); }
        }
    }

    /// <summary>Levied (curse) — +1 Star to every card that HAS a star cost. Hook.ModifyStarCost
    /// early-returns negative costs (= the card doesn't use stars) before consulting listeners, but
    /// this postfix still runs on that path, so re-check the sign. X-cost star cards never reach
    /// this hook (GetStarCostWithModifiers returns the player's stars directly).</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyStarCost))]
    internal static class StarCostPatch
    {
        private static void Postfix(CardModel card, ref decimal __result)
        {
            try
            {
                if (__result < 0m) return;   // negative = no star cost on this card
                if (card?.Owner is Player player && CharAffix.HasLevied(player)) __result += 1m;
            }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] star cost hook failed: {e.Message}"); }
        }
    }

    /// <summary>Orb channeled → Amplified's bonus channel + Supercharged refresh.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterOrbChanneled))]
    internal static class ChannelPatch
    {
        private static void Postfix(PlayerChoiceContext choiceContext, Player player)
        {
            try { CharAffix.OnOrbChanneled(choiceContext, player); }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] char channel hook failed: {e.Message}"); }
        }
    }

    /// <summary>Orb evoked (a slot freed) → refresh Supercharged for every player in the combat (the
    /// evoke hook carries no Player; non-Defect players simply have no Supercharged relic). Reconcile
    /// itself skips evokes that are the transient first half of a channel-into-full-queue (see
    /// <see cref="ChannelBracketPatch"/>).</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterOrbEvoked))]
    internal static class EvokePatch
    {
        private static void Postfix(PlayerChoiceContext choiceContext, ICombatState combatState)
        {
            try
            {
                foreach (var pc in combatState.PlayerCreatures)
                    if (pc.Player != null) CharAffix.Reconcile(choiceContext, pc.Player);
            }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] char evoke hook failed: {e.Message}"); }
        }
    }

    /// <summary>Brackets the WHOLE channel operation for Supercharged. OrbCmd.Channel on a full queue
    /// runs "evoke oldest → enqueue new" (decompile-verified), so mid-channel the queue is transiently
    /// not-full and reacting to it flickers the Focus off/on. The prefix raises a depth counter that
    /// makes Reconcile a no-op; the postfix re-wraps the async task so the counter drops and ONE
    /// reconcile runs only after the operation (and any nested Amplified bonus channel) settles.
    /// Patches the 3-arg overload — the generic Channel&lt;T&gt; funnels into it.</summary>
    [HarmonyPatch(typeof(OrbCmd), nameof(OrbCmd.Channel),
        typeof(PlayerChoiceContext), typeof(OrbModel), typeof(Player))]
    internal static class ChannelBracketPatch
    {
        private static void Prefix() => CharAffix.BeginChannel();

        private static void Postfix(ref Task __result, PlayerChoiceContext choiceContext, Player player)
            => __result = SettleThenReconcile(__result, choiceContext, player);

        private static async Task SettleThenReconcile(Task original, PlayerChoiceContext ctx, Player player)
        {
            try { await original; }
            finally { CharAffix.EndChannel(); }
            try { CharAffix.Reconcile(ctx, player); }   // no-op if a bonus channel is still in flight
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] channel settle reconcile failed: {e.Message}"); }
        }
    }

    /// <summary>Card discarded → Cycling draws one. Chains onto the awaited hook Task (like
    /// CurseDrawAffixPatch) so the draw runs in-order: AfterCardDiscarded fires inside DiscardAndDraw's
    /// per-card loop, so a detached draw would race the remaining discards and desync co-op.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDiscarded))]
    internal static class DiscardPatch
    {
        private static void Postfix(ref Task __result, PlayerChoiceContext choiceContext, CardModel card)
        {
            __result = DrawAfter(__result, choiceContext, card);
        }

        private static async Task DrawAfter(Task original, PlayerChoiceContext choiceContext, CardModel card)
        {
            await original;
            try
            {
                if (card?.Owner != null) await CharAffix.OnCardDiscardedAsync(choiceContext, card.Owner);
            }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] char discard hook failed: {e.Message}"); }
        }
    }

    /// <summary>Shiv played → Flurrying's bonus Shiv; any card played → Echoing's Vulnerable+Frail cost
    /// if that card was this turn's replay-granted one.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
    internal static class CardPlayedPatch
    {
        private static void Postfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardPlay cardPlay)
        {
            try
            {
                var card = cardPlay.Card;
                if (card?.Owner is not Player player) return;
                if (card is Shiv)
                    CharAffix.OnShivPlayed(player, new CsAccess(combatState));
                CharAffix.OnCardPlayedEchoing(choiceContext, player, card);
            }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] char card-played hook failed: {e.Message}"); }
        }
    }

    /// <summary>Summon (Osty grew) → Necromantic's block.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterSummon))]
    internal static class SummonPatch
    {
        private static void Postfix(PlayerChoiceContext choiceContext, Player summoner)
        {
            try { CharAffix.OnSummon(choiceContext, summoner); }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] char summon hook failed: {e.Message}"); }
        }
    }

    /// <summary>A summon took UNBLOCKED damage → Sacrificial's self-debuff on its owner (once per
    /// turn). AfterDamageReceived fires even for fully-blocked hits, so gate on real HP loss
    /// (result.UnblockedDamage &gt; 0 — the same idiom UnblockedHitPenaltyPatch / EmotionChip use).</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageReceived))]
    internal static class DamagePatch
    {
        private static void Postfix(PlayerChoiceContext choiceContext, Creature target, DamageResult result)
        {
            try
            {
                if (result.UnblockedDamage <= 0) return;   // block held -> the summon wasn't hurt
                if (target.IsPet && target.PetOwner is Player owner)
                    CharAffix.OnSummonDamaged(choiceContext, owner);
            }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] char summon-damage hook failed: {e.Message}"); }
        }
    }

    /// <summary>Lethal-hit path for Sacrificial. The game does NOT raise AfterDamageReceived when the
    /// hit kills the target (decompile: the WasTargetKilled && IsDead branch diverts to Kill instead),
    /// so a summon dying to an unblocked hit never reaches <see cref="DamagePatch"/>. Wrap the CORE
    /// CreatureCmd.Damage overload (all others funnel into it): snapshot pet→owner up front (death
    /// unattaches the pet, so PetOwner may be gone afterwards), then when the damage task settles fire
    /// the handler for killed pets that lost real HP. The once-per-turn gate in OnSummonDamaged
    /// dedupes any overlap with DamagePatch (removal-prevented deaths raise both paths).</summary>
    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
        typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp),
        typeof(Creature), typeof(CardModel))]
    internal static class LethalSummonDamagePatch
    {
        private static void Prefix(IEnumerable<Creature> targets, out Dictionary<Creature, Player>? __state)
        {
            __state = null;
            try
            {
                foreach (var t in targets)
                    if (t != null && t.IsPet && t.PetOwner is Player owner)
                        (__state ??= new Dictionary<Creature, Player>())[t] = owner;
            }
            catch { __state = null; }
        }

        private static void Postfix(ref Task<IEnumerable<DamageResult>> __result,
            PlayerChoiceContext choiceContext, Dictionary<Creature, Player>? __state)
        {
            if (__state == null) return;   // no pets among the targets — leave the task untouched
            __result = Watch(__result, choiceContext, __state);
        }

        private static async Task<IEnumerable<DamageResult>> Watch(
            Task<IEnumerable<DamageResult>> original, PlayerChoiceContext ctx, Dictionary<Creature, Player> pets)
        {
            var results = await original;
            try
            {
                // Require IsDead too: a removal-prevented "kill" stays alive AND raises the normal
                // AfterDamageReceived path — without this check it would collect both 1 and 2 stacks.
                foreach (var r in results)
                    if (r.UnblockedDamage > 0 && r.WasTargetKilled && r.Receiver != null && r.Receiver.IsDead
                        && pets.TryGetValue(r.Receiver, out var owner))
                        CharAffix.OnSummonDamaged(ctx, owner, lethal: true);
            }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] lethal summon-damage check failed: {e.Message}"); }
            return results;
        }
    }

    /// <summary>Card forged in combat → Reforging's star.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterForge))]
    internal static class ForgePatch
    {
        private static void Postfix(Player forger)
        {
            try { CharAffix.OnForge(forger); }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] char forge hook failed: {e.Message}"); }
        }
    }

    /// <summary>Stars spent → Regal refund + Bankrupt cost. The amount log doubles as the Levied
    /// verification readout: with the curse, a card's spend shows base+1 here.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterStarsSpent))]
    internal static class StarsSpentPatch
    {
        private static void Postfix(ICombatState combatState, int amount, Player spender)
        {
            try
            {
                CharAffix.OnStarsSpent(spender, new CsAccess(combatState), amount);
            }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] char stars hook failed: {e.Message}"); }
        }
    }

    /// <summary>Player's turn ended (pre-flush, after the orb queue's own end-of-turn triggers) →
    /// Polarized's all-empty / all-full check, plus Echoing's single-turn Replay revert. BeforeFlush is
    /// per-player, so co-op attributes both to the right owner.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeFlush))]
    internal static class FlushPatch
    {
        private static void Postfix(Player player)
        {
            try { CharAffix.OnTurnEndOrbCheck(player); CharAffix.OnTurnEndEchoing(player); }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] char flush hook failed: {e.Message}"); }
        }
    }

    /// <summary>Combat start → bump the per-combat epoch (re-arms Focused, clears Supercharged grant).</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
    internal static class CombatStartPatch
    {
        private static void Postfix()
        {
            try { CharAffix.OnCombatStart(); }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] char combat-start hook failed: {e.Message}"); }
        }
    }

    /// <summary>Turn start → per-turn char effects (Bonebound summon, Starlit star), turn-1 curses
    /// (Toxic self-poison, Shorted focus-1), plus per-turn resets and a Supercharged refresh.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
    internal static class TurnStartPatch
    {
        private static void Postfix(ICombatState combatState, PlayerChoiceContext choiceContext, Player player)
        {
            try
            {
                int turn = player.PlayerCombatState?.TurnNumber ?? 0;
                if (turn <= 0) return;
                CharAffix.ResetTurn();
                foreach (var relic in new List<RelicModel>(player.Relics))
                {
                    var rec = RelicForgeService.RecordFor(relic);
                    if (rec == null || rec.Prefix.Length == 0) continue;
                    switch (rec.Prefix)
                    {
                        case "Bonebound":                    CharAffix.OnTurnBonebound(choiceContext, player, relic); break;
                        case "Starlit":                       CharAffix.OnTurnStarlit(player, relic); break;
                        case "Doombound":                     CharAffix.OnTurnDoombound(choiceContext, player, relic); break;
                        case "Polarized":                     CharAffix.OnTurnPolarized(player, relic); break;
                        case "Toxic"   when turn == 1:        CharAffix.OnCombatStartToxic(choiceContext, player, relic); break;
                        case "Shorted" when turn == 1:        CharAffix.OnCombatStartShorted(choiceContext, player, relic); break;
                        case "Echoing":                       CharAffix.OnTurnEchoing(player, relic, turn); break;
                    }
                }
                CharAffix.Reconcile(choiceContext, player);   // Supercharged re-eval each turn
            }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] char turn hook failed: {e.Message}"); }
        }
    }
}

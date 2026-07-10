using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Random;

namespace Sts2RelicForge;

/// <summary>
/// Turn-based penalty prefixes, applied to the OWNER (a pure downside):
///   Cursed     — Weak 1 to self on turn 1
///   Cumbersome — Dexterity -1 to self on turn 1
///   Fickle     — 25% each turn to take a random debuff (Weak / Vulnerable / Frail)
/// Fires from Hook.AfterPlayerTurnStart. Fickle's roll is deterministic per (runSeed, turn,
/// relic) so a reload reproduces it (no save-scumming a bad roll away) and it never touches the
/// shared run RNG stream.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
internal static class PenaltyTurnPatch
{
    private static readonly Func<PlayerChoiceContext, Player, Task>[] FickleDebuffs =
    {
        (ctx, p) => PowerCmd.Apply<WeakPower>(ctx, p.Creature, 1m, p.Creature, null),
        (ctx, p) => PowerCmd.Apply<VulnerablePower>(ctx, p.Creature, 1m, p.Creature, null),
        (ctx, p) => PowerCmd.Apply<FrailPower>(ctx, p.Creature, 1m, p.Creature, null),
    };

    // Card-insertion penalties: shove a status card into a combat pile. eachTurn=false fires
    // once (turn 1). Dazed is ethereal (self-cleans), so Tainted can recur every turn; the
    // lasting cards (Wound/Burn/Void) are one-time at combat start so they don't snowball.
    private static readonly Dictionary<string, (int count, PileType pile, bool eachTurn, Func<ICombatState, Player, CardModel> make)> CardPenalties = new()
    {
        ["Tainted"]    = (1, PileType.Draw,    true,  (cs, p) => cs.CreateCard<Dazed>(p)),
        ["Festering"]  = (2, PileType.Discard, false, (cs, p) => cs.CreateCard<Wound>(p)),
        ["Smoldering"] = (1, PileType.Draw,    false, (cs, p) => cs.CreateCard<Burn>(p)),
        ["Hollow"]     = (1, PileType.Draw,    false, (cs, p) => cs.CreateCard<MegaCrit.Sts2.Core.Models.Cards.Void>(p)),
    };

    private static void Postfix(ICombatState combatState, PlayerChoiceContext choiceContext, Player player)
    {
        try
        {
            int turn = player.PlayerCombatState?.TurnNumber ?? 0;
            if (turn <= 0) return;
            uint seed = player.RunState.Rng.Seed;
            foreach (var relic in new List<RelicModel>(player.Relics))
            {
                var rec = RelicForgeService.RecordFor(relic);
                if (rec == null) continue;
                // Penalty affixes were re-homed onto the curse slot, so dispatch off rec.SelfCurse (which
                // holds a penalty identity here, or an on-hit self-curse key that falls through to default
                // and is handled by UnblockedHitPenaltyPatch instead). Same effects, same triggers.
                string key = rec.SelfCurse;
                if (key.Length == 0) continue;
                switch (key)
                {
                    case "Cursed" when turn == 1:
                        Fire(relic, PowerCmd.Apply<WeakPower>(choiceContext, player.Creature, 1m, player.Creature, null));
                        break;
                    case "Cumbersome" when turn == 1:
                        Fire(relic, PowerCmd.Apply<DexterityPower>(choiceContext, player.Creature, -1m, player.Creature, null));
                        break;
                    case "Taxing" when turn == 1:
                    {
                        // Upkeep bleed: lose 1 gold per card in your deck, once at combat start. The
                        // bigger (more bloated) your deck, the more it costs — clamped to what you hold.
                        int deckCount = PileType.Deck.GetPile(player).Cards.Count;
                        int loss = Math.Min(deckCount, player.Gold);
                        if (loss > 0)
                        {
                            relic.Flash();
                            TaskHelper.RunSafely(PlayerCmd.LoseGold(loss, player));
                            MainFile.Logger.Info($"[{MainFile.ModId}] Taxing: -{loss} gold (deck {deckCount}) on turn 1 ({relic.Id.Entry}).");
                        }
                        break;
                    }
                    case "Fickle":
                        var rng = new Rng((uint)((int)seed + turn * 48611 + StringHelper.GetDeterministicHashCode(relic.Id.Entry)));
                        if (rng.NextFloat() < 0.25)
                        {
                            int idx = (int)(rng.NextFloat() * FickleDebuffs.Length);
                            if (idx >= FickleDebuffs.Length) idx = FickleDebuffs.Length - 1;
                            Fire(relic, FickleDebuffs[idx](choiceContext, player));
                        }
                        break;
                    default:
                        if (CardPenalties.TryGetValue(key, out var cp) && (cp.eachTurn || turn == 1))
                        {
                            relic.Flash();
                            for (int i = 0; i < cp.count; i++)
                                TaskHelper.RunSafely(CardPileCmd.Add(cp.make(combatState, player), cp.pile));
                        }
                        break;
                }
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] penalty turn apply failed: {e.Message}"); }
    }

    private static void Fire(RelicModel relic, Task effect)
    {
        relic.Flash();
        TaskHelper.RunSafely(effect);
    }
}

/// <summary>
/// Card-count penalty: Overloaded — playing 6 cards in a single turn applies Vulnerable 1 to
/// self, once per turn. AfterCardPlayed has no turn card-counter to read, so we track one per
/// host instance (reset when the turn changes).
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
internal static class PenaltyCardPatch
{
    private const int Threshold = 6;
    // per-host [turnNumber, cardsThisTurn, firedThisTurn]
    private static readonly ConditionalWeakTable<RelicModel, int[]> State = new();

    private static void Postfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        try
        {
            Player? player = cardPlay.Card.Owner;
            if (player == null) return;
            int turn = player.PlayerCombatState?.TurnNumber ?? 0;
            if (turn <= 0) return;

            foreach (var relic in new List<RelicModel>(player.Relics))
            {
                var rec = RelicForgeService.RecordFor(relic);
                if (rec == null || rec.SelfCurse != "Overloaded") continue;   // penalty re-homed onto the curse slot

                var s = State.GetValue(relic, _ => new int[3]);
                if (s[0] != turn) { s[0] = turn; s[1] = 0; s[2] = 0; }  // new turn -> reset
                s[1]++;
                if (s[1] >= Threshold && s[2] == 0)
                {
                    s[2] = 1;
                    relic.Flash();
                    TaskHelper.RunSafely(PowerCmd.Apply<VulnerablePower>(choiceContext, player.Creature, 1m, player.Creature, null));
                }
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] penalty card apply failed: {e.Message}"); }
    }
}

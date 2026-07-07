using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;        // CardType
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
/// Cursefed (저주먹은): the first time you DRAW a Curse card each turn, gain 1 Strength OR Dexterity —
/// turning the deck's curses, normally dead weight, into a scaling engine (and a payoff for the mod's
/// own curse riders). Rides Hook.AfterCardDrawn, which fires once per card for BOTH the opening hand and
/// every mid-combat draw. Capped at ONCE PER TURN per relic (same idiom as ReactiveAffix's 공명의 cap):
/// only the first curse drawn in a turn triggers, so a shuffle-heavy deck can't runaway-stack it. The
/// Str-vs-Dex pick is deterministic per (runSeed, turn, relic) so a reload reproduces it.
///
/// CO-OP: the grant must be part of the AWAITED hook chain, not fire-and-forget. AfterCardDrawn runs
/// inside the opening-hand draw loop of SetupPlayerTurn (fromHandDraw draws), i.e. mid combat-entry. A
/// detached PowerCmd.Apply (Strength/Dexterity are VISIBLE powers -> they await CustomScaledWait and write
/// History.PowerReceived) would interleave non-deterministically with the remaining draws and the following
/// AfterPlayerTurnStart, so host and client produce divergent lockstep checksums and the client fails to
/// enter the match. Because the static Hook.AfterCardDrawn is an awaited Task, we chain onto its __result
/// and AWAIT the grant in-order instead — both machines execute it at the same point.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDrawn))]
internal static class CurseDrawAffixPatch
{
    // Per-relic [lastTurn, firedThisTurn]: at most one trigger per distinct turn number, self-resetting
    // when the turn changes (no separate turn-start hook needed).
    private static readonly ConditionalWeakTable<RelicModel, int[]> FiredTurn = new();

    // Chain onto the awaited hook Task so the grant runs in-order (see class remarks): reassigning
    // __result makes the caller's `await Hook.AfterCardDrawn(...)` wait for our deterministic grant.
    private static void Postfix(ref Task __result, PlayerChoiceContext choiceContext, CardModel card)
    {
        __result = GrantAfter(__result, choiceContext, card);
    }

    private static async Task GrantAfter(Task original, PlayerChoiceContext choiceContext, CardModel card)
    {
        await original; // let every model listener's AfterCardDrawn run first; propagate its result faithfully
        try
        {
            if (card == null || card.Type != CardType.Curse) return;
            Player? player = card.Owner;
            if (player?.Creature == null) return;

            int turn = player.PlayerCombatState?.TurnNumber ?? 0;
            uint seed = player.RunState.Rng.Seed;

            foreach (var relic in new List<RelicModel>(player.Relics))
            {
                var rec = RelicForgeService.RecordFor(relic);
                if (rec == null || rec.Prefix.Length == 0) continue;
                var pfx = PrefixTable.ByName(rec.Prefix);
                if (pfx == null || !pfx.CurseDrawStrength) continue;

                // Once-per-turn gate.
                var box = FiredTurn.GetValue(relic, _ => new int[] { int.MinValue, 0 });
                if (box[0] != turn) { box[0] = turn; box[1] = 0; }
                if (box[1] > 0) continue;
                box[1] = 1;

                // Deterministic per (seed, turn, relic) coin flip: Strength or Dexterity.
                var rng = new Rng((uint)((int)seed + turn * 60961 + StringHelper.GetDeterministicHashCode(relic.Id.Entry)));
                bool dex = rng.NextFloat() < 0.5f;

                relic.Flash();
                if (dex)
                    await PowerCmd.Apply<DexterityPower>(choiceContext, player.Creature, 1m, player.Creature, null);
                else
                    await PowerCmd.Apply<StrengthPower>(choiceContext, player.Creature, 1m, player.Creature, null);
                MainFile.Logger.Info($"[{MainFile.ModId}] Cursefed: +1 {(dex ? "Dexterity" : "Strength")} on drawing {card.Id.Entry} (turn {turn}, {relic.Id.Entry}).");
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] Cursefed draw hook failed: {e.Message}"); }
    }
}

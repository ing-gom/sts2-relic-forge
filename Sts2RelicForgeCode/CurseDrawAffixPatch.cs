using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
/// Str-vs-Dex pick is deterministic per (runSeed, turn, relic) so a reload reproduces it. Grants via the
/// networked PowerCmd, so co-op stays consistent with no extra sync.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDrawn))]
internal static class CurseDrawAffixPatch
{
    // Per-relic [lastTurn, firedThisTurn]: at most one trigger per distinct turn number, self-resetting
    // when the turn changes (no separate turn-start hook needed).
    private static readonly ConditionalWeakTable<RelicModel, int[]> FiredTurn = new();

    private static void Postfix(PlayerChoiceContext choiceContext, CardModel card)
    {
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
                    TaskHelper.RunSafely(PowerCmd.Apply<DexterityPower>(choiceContext, player.Creature, 1m, player.Creature, null));
                else
                    TaskHelper.RunSafely(PowerCmd.Apply<StrengthPower>(choiceContext, player.Creature, 1m, player.Creature, null));
                MainFile.Logger.Info($"[{MainFile.ModId}] Cursefed: +1 {(dex ? "Dexterity" : "Strength")} on drawing {card.Id.Entry} (turn {turn}, {relic.Id.Entry}).");
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] Cursefed draw hook failed: {e.Message}"); }
    }
}

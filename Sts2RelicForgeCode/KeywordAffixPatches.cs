using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace Sts2RelicForge;

/// <summary>
/// KEYWORD prefix family — general-pool (character-agnostic) prefixes that grant/inflict card
/// keywords through the game's own APIs, so the card face shows the keyword and tooltip natively:
///   Retaining / 보존의 (bonus) — at turn end, one random un-retained card in hand gets single-turn
///       Retain (GiveSingleTurnRetain — the vanilla retain-power idiom; the flag self-clears in
///       EndOfTurnCleanup, so next turn rolls a fresh card).
///   Searing / 불사르는 (curse) — playing a card has a 25% chance to brand it with Exhaust for the
///       combat (CardCmd.ApplyKeyword — the GhostSeed idiom). The brand lands AFTER the play's
///       result pile was already chosen (decompile-verified), so THIS play still discards normally
///       and the card burns on its NEXT play — the player sees the mark and gets to choose.
/// Rolls ride CharAffix's deterministic per-(relic, turn, occurrence) Rng so all clients agree.
/// </summary>
internal static class KeywordAffixPatches
{
    /// <summary>Turn end → Retaining picks its card. The static Hook.BeforeFlush runs every model's
    /// BeforeFlush AND BeforeFlushLate before postfixes, so vanilla retain effects (retain powers,
    /// select screens) have already marked their cards — we only touch what is still flushing.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeFlush))]
    internal static class RetainingFlushPatch
    {
        private static void Postfix(Player player)
        {
            try
            {
                if (CombatManager.Instance.IsOverOrEnding) return;
                var cs = player.Creature?.CombatState;
                if (cs == null || !Hook.ShouldFlush(cs, player)) return;   // flush suppressed -> retain is moot
                int turn = player.PlayerCombatState?.TurnNumber ?? 0;
                if (turn <= 0) return;

                foreach (var relic in CharAffix.Owned(player, "Retaining"))
                {
                    var eligible = new List<CardModel>();
                    foreach (var c in PileType.Hand.GetPile(player).Cards)
                        if (!c.ShouldRetainThisTurn) eligible.Add(c);
                    if (eligible.Count == 0) continue;

                    int pick = (int)(CharAffix.Roll(player, relic, turn) * eligible.Count);
                    if (pick >= eligible.Count) pick = eligible.Count - 1;
                    relic.Flash();
                    eligible[pick].GiveSingleTurnRetain();
                }
            }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] retaining flush hook failed: {e.Message}"); }
        }
    }

    /// <summary>Card played → Searing's 25% Exhaust brand. Skips cards that already exhaust and
    /// Power cards (they never return to a pile, so a brand would be pure noise).</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
    internal static class SearingPlayPatch
    {
        private static void Postfix(CardPlay cardPlay)
        {
            try
            {
                var card = cardPlay.Card;
                if (card == null || card.Owner is not Player player) return;
                if (card.Type == CardType.Power || card.Keywords.Contains(CardKeyword.Exhaust)) return;
                int turn = player.PlayerCombatState?.TurnNumber ?? 0;
                if (turn <= 0) return;

                foreach (var relic in CharAffix.Owned(player, "Searing"))
                    if (CharAffix.Roll(player, relic, turn) < 0.25f)
                    {
                        relic.Flash();
                        CardCmd.ApplyKeyword(card, CardKeyword.Exhaust);
                        break;   // one brand is one brand — further Searing relics change nothing
                    }
            }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] searing play hook failed: {e.Message}"); }
        }
    }
}

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
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Universal card-play boon prefixes (see PrefixTable), fired from <see cref="Hook.AfterCardPlayed"/>:
///   Studious (학구의)    — the FIRST Power card played each combat → draw 2.
///   Overflowing (충만의) — playing a Power card → +1 Energy (once per turn).
///   Cunning (교활의)     — playing a Skill card → 25% chance to draw a card.
///
/// CHAINS onto the awaited AfterCardPlayed Task (fires inside the card-play / Replay series loop) so the
/// draw / energy lands IN-ORDER, not detached — a detached draw races the series and desyncs co-op (the same
/// reason the char-affix CardPlayedPatch chains). Draw / energy are deterministic commands; the Cunning roll
/// reuses <see cref="CharAffix.Roll"/> (seed-deterministic, and it picks up the Catalytic/Empowering/Priming
/// aura for free). Once-per-combat / once-per-turn gates are per-relic and keyed on synced floor/turn numbers.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
internal static class PrefixCardPlayPatch
{
    // per-relic [studiousCombatFloor+1, overflowingTurn+1]; 0 = not yet fired (floor/turn are >= 0).
    private static readonly ConditionalWeakTable<RelicModel, int[]> Gate = new();

    private static void Postfix(ref Task __result, PlayerChoiceContext choiceContext, CardPlay cardPlay)
        => __result = After(__result, choiceContext, cardPlay);

    private static async Task After(Task original, PlayerChoiceContext ctx, CardPlay cardPlay)
    {
        await original;
        try
        {
            var card = cardPlay.Card;
            if (card?.Owner is not Player player) return;
            var type = card.Type;
            if (type != CardType.Power && type != CardType.Skill) return;   // only Power/Skill triggers here
            int turn = player.PlayerCombatState?.TurnNumber ?? 0;
            int floor = player.RunState?.TotalFloor ?? 0;

            foreach (var relic in new List<RelicModel>(player.Relics))
            {
                if (RelicForgeService.IsForgeEffectSuppressed(relic)) continue;
                var rec = RelicForgeService.RecordFor(relic);
                if (rec == null || rec.Prefix.Length == 0) continue;
                var pfx = PrefixTable.ByName(rec.Prefix);
                if (pfx == null) continue;
                var box = Gate.GetValue(relic, _ => new int[2]);

                if (type == CardType.Power)
                {
                    if (pfx.PowerDrawFirst && box[0] != floor + 1)          // Studious — first Power this combat
                    {
                        box[0] = floor + 1;
                        relic.Flash();
                        await CardPileCmd.Draw(ctx, 2m, player);
                    }
                    if (pfx.PowerEnergy && box[1] != turn + 1)              // Overflowing — once per turn
                    {
                        box[1] = turn + 1;
                        relic.Flash();
                        await PlayerCmd.GainEnergy(1m, player);
                    }
                }
                else if (type == CardType.Skill && pfx.SkillDraw)          // Cunning — 25% per Skill
                {
                    if (CharAffix.Roll(player, relic, turn) < 0.25f)
                    {
                        relic.Flash();
                        await CardPileCmd.Draw(ctx, 1m, player);
                    }
                }
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] prefix card-play hook failed: {e.Message}"); }
    }
}

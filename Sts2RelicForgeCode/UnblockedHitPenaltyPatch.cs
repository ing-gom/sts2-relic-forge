using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
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
/// SELF-CURSE application (see <see cref="SelfCurseTable"/>): an independent player-side "저주" a forged
/// relic may carry. Each time the OWNER takes UNBLOCKED damage (block failed → real HP lost), the curse
/// punishes the player. FULLY PROPORTIONAL to the hit count — a 5-hit unblocked attack fires 5×, because
/// AfterDamageReceived is raised once per damage instance (the same idiom the game's own EmotionChip
/// relic uses: guard on <c>target</c> + <c>result.UnblockedDamage &gt; 0</c>). Its own roll dimension,
/// separate from the prefix pool and the enemy-rider slot (which strengthens enemies instead).
///   Enfeebling  — Weak 1 to self
///   Cracking    — Frail 1 to self
///   Vulnerating — Vulnerable 1 to self
///   Bewildering — a Dazed to the draw pile
///   Wretched    — a random one of Weak / Frail / Vulnerable to self
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageReceived))]
internal static class UnblockedHitPenaltyPatch
{
    private static void Postfix(PlayerChoiceContext choiceContext, Creature target, DamageResult result, Creature? dealer)
    {
        try
        {
            if (result.UnblockedDamage <= 0) return;             // block held → no penalty
            Player? player = target?.Player;
            if (player == null) return;                          // only the player's own creature
            if (dealer != null && dealer.Player != null) return; // ignore self/ally-sourced HP loss

            foreach (var relic in new List<RelicModel>(player.Relics))
            {
                var rec = RelicForgeService.RecordFor(relic);
                if (rec == null || rec.SelfCurse.Length == 0) continue;
                var def = SelfCurseTable.ByKey(rec.SelfCurse);
                if (def == null) continue;

                relic.Flash();
                if (def.OnHitCard)
                {
                    var card = target!.CombatState?.CreateCard<Dazed>(player);
                    if (card != null) TaskHelper.RunSafely(CardPileCmd.Add(card, PileType.Draw));
                    continue;
                }
                string power = def.OnHitRandom ? RandomDebuff(player, relic, result) : def.OnHitPower;
                Apply(choiceContext, player.Creature, power);
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] self-curse apply failed: {e.Message}"); }
    }

    private static readonly string[] Debuffs = { "Weak", "Frail", "Vulnerable" };

    // Stateless deterministic pick: the same hit (seed, turn, relic, unblocked amount) → the same
    // debuff, so reloading the fight reproduces it with no per-hit counter to serialize.
    private static string RandomDebuff(Player player, RelicModel relic, DamageResult result)
    {
        uint seed = player.RunState?.Rng.Seed ?? 0;
        int turn = player.PlayerCombatState?.TurnNumber ?? 0;
        var rng = new Rng((uint)((int)seed + turn * 40129 + result.UnblockedDamage * 733
                                  + StringHelper.GetDeterministicHashCode(relic.Id.Entry)));
        int i = (int)(rng.NextFloat() * Debuffs.Length);
        return Debuffs[i >= Debuffs.Length ? Debuffs.Length - 1 : i];
    }

    private static void Apply(PlayerChoiceContext ctx, Creature self, string power)
    {
        switch (power)
        {
            case "Weak":       TaskHelper.RunSafely(PowerCmd.Apply<WeakPower>(ctx, self, 1m, self, null)); break;
            case "Frail":      TaskHelper.RunSafely(PowerCmd.Apply<FrailPower>(ctx, self, 1m, self, null)); break;
            case "Vulnerable": TaskHelper.RunSafely(PowerCmd.Apply<VulnerablePower>(ctx, self, 1m, self, null)); break;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace Sts2RelicForge;

/// <summary>
/// Delayed companion prefixes (Mighty, Intimidating) don't graft a relic — they apply a fixed
/// min-1 effect on a set turn (DelayTurn), which is strictly weaker than the original relic's
/// combat-start trigger. Hook.AfterPlayerTurnStart fires once per player turn, so we check each
/// forged relic's prefix and fire its effect when the turn matches.
///
/// The effect table maps a prefix name to how it applies. Add an entry here when adding a new
/// delayed prefix in PrefixTable.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
internal static class DelayedCompanionPatch
{
    private static readonly Dictionary<string, Func<PlayerChoiceContext, Player, ICombatState, Task>> Effects = new()
    {
        ["Mighty"] = (ctx, p, cs) => PowerCmd.Apply<StrengthPower>(ctx, p.Creature, 1m, p.Creature, null),
        ["Intimidating"] = (ctx, p, cs) => PowerCmd.Apply<VulnerablePower>(ctx, cs.HittableEnemies, 1m, p.Creature, null),
    };

    private static void Postfix(ICombatState combatState, PlayerChoiceContext choiceContext, Player player)
    {
        try
        {
            int turn = player.PlayerCombatState?.TurnNumber ?? 0;
            if (turn <= 0) return;
            // Snapshot: applying a power won't change player.Relics, but be safe.
            foreach (var relic in new List<RelicModel>(player.Relics))
            {
                var rec = RelicForgeService.RecordFor(relic);
                if (rec == null || rec.Prefix.Length == 0) continue;
                var pfx = PrefixTable.ByName(rec.Prefix);
                if (pfx == null || pfx.DelayTurn != turn) continue;      // fires once, on the exact turn
                if (!Effects.TryGetValue(pfx.Name, out var apply)) continue;
                relic.Flash();                                          // pulse the host (prefixed) relic
                TaskHelper.RunSafely(apply(choiceContext, player, combatState));
                MainFile.Logger.Info($"[{MainFile.ModId}] delayed {pfx.Name} fired on turn {turn} ({relic.Id.Entry}).");
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] delayed companion apply failed: {e.Message}");
        }
    }
}

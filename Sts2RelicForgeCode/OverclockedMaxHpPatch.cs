using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Overclocked's permanent Max-HP cost (Prefix.StartMaxHpLoss). Max-HP is host-authoritative and REPLICATED,
/// so it must NOT be mutated by a DETACHED (TaskHelper.RunSafely) call from a per-peer hook — that creates an
/// out-of-band action that races the turn-start checksum and the host→client replication, and the client
/// double-applies (coop-verify caught it: player Max-HP 87 on host vs 86 on the client → StateDivergence drop).
///
/// The game's own <c>PaperCutsPower</c> reduces a player's Max HP in co-op by AWAITING
/// <see cref="CreatureCmd.LoseMaxHp"/> inside its power hook (part of the synchronized action flow). This
/// mirrors that: it CHAINS onto the awaited <see cref="Hook.AfterPlayerTurnStart"/> Task (which returns Task)
/// so the loss runs awaited-in-order INSIDE the synchronized turn-start action on every peer — deterministic,
/// no replication race, no double-apply. Separate from <see cref="ForgeCombatAffixPatch"/> (a void postfix)
/// precisely because only this cost needs the in-order Task chain; energy / current-HP damage are locally
/// simulated and stay on the detached path.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
internal static class OverclockedMaxHpPatch
{
    private static void Postfix(ref Task __result, PlayerChoiceContext choiceContext, Player player)
        => __result = After(__result, choiceContext, player);

    private static async Task After(Task original, PlayerChoiceContext ctx, Player player)
    {
        await original;
        try
        {
            int turn = player.PlayerCombatState?.TurnNumber ?? 0;
            if (turn != 1) return;                          // combat start only (fires once per combat)
            var creature = player.Creature;
            if (creature == null) return;

            // Snapshot: LoseMaxHp won't change player.Relics, but be safe.
            foreach (var relic in new List<RelicModel>(player.Relics))
            {
                if (RelicForgeService.IsForgeEffectSuppressed(relic)) continue;   // dead relic — no forge affix
                var rec = RelicForgeService.RecordFor(relic);
                if (rec == null || rec.Prefix.Length == 0) continue;
                var pfx = PrefixTable.ByName(rec.Prefix);
                if (pfx == null || pfx.StartMaxHpLoss <= 0) continue;

                relic.Flash();
                // AWAITED in-order (not RunSafely): runs inside the synchronized turn-start action, same as
                // PaperCutsPower's awaited LoseMaxHp — the co-op-safe path for a replicated Max-HP change.
                await CreatureCmd.LoseMaxHp(ctx, creature, pfx.StartMaxHpLoss, isFromCard: false);
                MainFile.Logger.Info($"[{MainFile.ModId}] {pfx.Name}: -{pfx.StartMaxHpLoss} Max HP (permanent, awaited) on turn 1 ({relic.Id.Entry}).");
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] Overclocked maxHp apply failed: {e.Message}"); }
    }
}

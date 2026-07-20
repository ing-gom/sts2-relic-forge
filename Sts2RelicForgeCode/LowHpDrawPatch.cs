using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Cornered (궁지의) — a low-HP comeback boon: while the owner is at or below HALF max HP, they draw ONE extra
/// card each turn. Rather than a detached draw command, this rides the game's own turn-start hand-draw COUNT via
/// <see cref="Hook.ModifyHandDraw"/> (the single point that computes how many cards the hand-draw pulls) and adds
/// 1 to the result. The extra card is therefore part of the normal synchronized draw — no separate action, no RNG
/// timing of our own — so it's co-op-safe by construction, the same pure-calc class as ModifyDamage/ModifyMerchantPrice:
/// both peers read the same replicated HP and relic list and return the same count. Preview/multiple calls are safe
/// because the postfix recomputes (never accumulates).
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyHandDraw))]
internal static class LowHpDrawPatch
{
    private static void Postfix(ref decimal __result, Player player)
    {
        try
        {
            if (player == null) return;
            var self = player.Creature;
            if (self == null) return;
            if (self.CurrentHp * 2 > self.MaxHp) return;   // strictly above 50% max HP → no bonus

            foreach (var relic in new List<RelicModel>(player.Relics))
            {
                if (RelicForgeService.IsForgeEffectSuppressed(relic)) continue;
                var rec = RelicForgeService.RecordFor(relic);
                if (rec == null || rec.Prefix.Length == 0) continue;
                var pfx = PrefixTable.ByName(rec.Prefix);
                if (pfx != null && pfx.LowHpDraw) { __result += 1m; return; }   // one extra card, once
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] low-hp draw hook failed: {e.Message}"); }
    }
}

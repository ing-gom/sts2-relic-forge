using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Covetous (탐욕의) curse — merchant prices are raised by <see cref="Prefix.ShopTaxPct"/>% while the player
/// carries it. The first NON-combat curse in the mod: a Harmony postfix on <see cref="Hook.ModifyMerchantPrice"/>
/// (the aggregator that returns an item's final price for a given player) scales the result up. A pure
/// deterministic calculation — no state mutation, ownership read from the synced relic list — so it's co-op-safe
/// by construction (same class as Calloused's damage-modifier), and it flows into the shop UI so the higher
/// price is what the player sees and pays.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyMerchantPrice))]
internal static class MerchantPricePatch
{
    private static void Postfix(ref decimal __result, Player player)
    {
        try
        {
            if (player == null || __result <= 0m) return;
            int tax = 0;
            foreach (var relic in new List<RelicModel>(player.Relics))
            {
                if (RelicForgeService.IsRelicSpent(relic)) continue;   // spent relic → its curse is off
                var rec = RelicForgeService.RecordFor(relic);
                if (rec == null) continue;
                // Covetous is a Penalty prefix → re-homed to rec.SelfCurse; also check the prefix slot to be safe.
                var pfx = PrefixTable.ByName(rec.SelfCurse.Length > 0 ? rec.SelfCurse : rec.Prefix);
                if (pfx != null && pfx.ShopTaxPct > tax) tax = pfx.ShopTaxPct;
            }
            if (tax > 0) __result = decimal.Round(__result * (100 + tax) / 100m, 0);
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] merchant price hook failed: {e.Message}"); }
    }
}

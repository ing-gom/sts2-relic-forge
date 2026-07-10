using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Surfaces a forged relic's prefix EFFECT and CURSE as their OWN hover panels — the way a card shows a
/// keyword sub-tooltip — instead of cramming that text into the relic's main description. The main tooltip
/// keeps only the numeric boosts (see <see cref="ForgeText.DescriptionBlock"/>) and the prefix name on the
/// title (<see cref="RelicTooltipPatch"/>); the effect note and the curse move here into up to two panels.
///
/// Hook: <c>RelicModel.get_HoverTips</c> — the plural list the relic holders (NRelicInventoryHolder /
/// NRelicBasicHolder) feed to NHoverTipSet. It is <c>[ main HoverTip ] + ExtraHoverTips</c>; appending here
/// runs downstream of every per-relic ExtraHoverTips override, and each HoverTip in the list renders as its
/// own stacked panel. (HoverTip has no child-tip field — multi-panel is purely a longer list.)
///
/// History-reconstructed records (DisplayOnly) can't route through this live path, so they keep the inline
/// block from DescriptionBlock and are skipped here.
/// </summary>
[HarmonyPatch(typeof(RelicModel), "get_HoverTips")]
internal static class RelicExtraPanelsPatch
{
    private static void Postfix(RelicModel __instance, ref IEnumerable<IHoverTip> __result)
    {
        try
        {
            ForgeRecord? rec = RelicForgeService.RecordForHover(__instance);
            if (rec is null || rec.DisplayOnly) return;

            string effect = ForgeText.PrefixEffectBody(rec);
            string curse = ForgeText.CurseBody(rec);
            if (effect.Length == 0 && curse.Length == 0) return;

            // __result is the game's List<IHoverTip> ([main] + extras); copy so we never mutate its backing.
            var list = new List<IHoverTip>(__result);
            if (effect.Length > 0)
                list.Add(MakePanel(ForgeText.PrefixEffectTitle(rec), effect, "sts2rf_prefix_" + rec.Prefix, debuff: false));
            if (curse.Length > 0)
                list.Add(MakePanel(ForgeText.CurseTitle(rec), curse,
                    "sts2rf_curse_" + (rec.SelfCurse.Length > 0 ? rec.SelfCurse : rec.EnemyRiderSuffix), debuff: true));
            __result = list;
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] extra hover panels failed: {e.Message}"); }
    }

    /// <summary>Build a plain title + rich-text-body panel. HoverTip's setters are reachable because
    /// ModKit publicizes sts2 (same as MerchantReforgeButton.MakeTip). A unique Id keeps RemoveDupes from
    /// dropping it; IsDebuff gives the curse panel the red debuff skin.</summary>
    private static HoverTip MakePanel(string title, string body, string id, bool debuff)
    {
        var t = new HoverTip();
        t.Title = title;
        t.Description = body;
        t.Id = id;
        t.IsDebuff = debuff;
        return t;
    }
}

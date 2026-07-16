using System;
using System.Collections.Generic;
using System.Reflection;
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
/// Hooks BOTH properties that surface a relic's ExtraHoverTips to the UI, because they feed DIFFERENT views:
///   - <c>RelicModel.get_HoverTips</c> — <c>[ main HoverTip ] + ExtraHoverTips</c>, the list the relic
///     holders (NRelicInventoryHolder / NRelicBasicHolder) feed to NHoverTipSet on HOVER.
///   - <c>RelicModel.get_HoverTipsExcludingRelic</c> — just <c>ExtraHoverTips</c>, the list the click-to-
///     ENLARGE inspect screen (NInspectRelicScreen.UpdateRelicDisplay) reads. That screen renders the main
///     description itself (from DynamicDescription, so forged NUMBERS already show) and pulls only the extra
///     panels from here. Without this second target the enlarged view showed the boosted numbers but no
///     prefix-effect / curse / gauge panels at all.
/// Both getters are non-virtual on RelicModel and call the virtual ExtraHoverTips through the vtable, so a
/// single Postfix on each runs downstream of every per-relic ExtraHoverTips override. (HoverTip has no
/// child-tip field — multi-panel is purely a longer list.)
///
/// History-reconstructed records (DisplayOnly) can't route through this live path, so they keep the inline
/// block from DescriptionBlock and are skipped here.
/// </summary>
[HarmonyPatch]
internal static class RelicExtraPanelsPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.PropertyGetter(typeof(RelicModel), "HoverTips");
        yield return AccessTools.PropertyGetter(typeof(RelicModel), "HoverTipsExcludingRelic");
    }

    private static void Postfix(RelicModel __instance, ref IEnumerable<IHoverTip> __result)
    {
        try
        {
            ForgeRecord? rec = RelicForgeService.RecordForHover(__instance);
            if (rec is null || rec.DisplayOnly) return;

            string effect = ForgeText.PrefixEffectBody(rec);
            string curse = ForgeText.CurseBody(rec);
            // Curse gauge: read off the live hovered instance (0 for offered/preview relics, which carry no
            // reforge count).
            int gauge = RelicForgeService.CurseGauge(__instance);
            bool saturated = RelicForgeService.IsGaugeSaturated(__instance);
            if (effect.Length == 0 && curse.Length == 0 && gauge == 0) return;

            // __result is the game's List<IHoverTip> ([main] + extras); copy so we never mutate its backing.
            var list = new List<IHoverTip>(__result);
            if (effect.Length > 0)
                list.Add(MakePanel(ForgeText.PrefixEffectTitle(rec), effect, "sts2rf_prefix_" + rec.Prefix, debuff: false));
            if (curse.Length > 0)
                list.Add(MakePanel(ForgeText.CurseTitle(rec), curse,
                    "sts2rf_curse_" + (rec.SelfCurse.Length > 0 ? rec.SelfCurse : rec.EnemyRiderSuffix), debuff: true));
            // Curse-gauge panel. A SATURATED relic shows a plain "burnt out — no longer works" note
            // EVERYWHERE, because its red icon persists off the forge location (see GaugeTintPatch) and that
            // note is what explains it. A non-saturated gauge is the actionable "curse risk N%" number, only
            // meaningful where you can reforge/cleanse — gate it to a forge location (rest site / shop), so
            // in combat / on the map it doesn't clutter the tooltip. Same Id keeps RemoveDupes happy.
            if (saturated)
                list.Add(MakePanel(ForgeText.SaturatedTitle(), ForgeText.SaturatedBody(), "sts2rf_gauge", debuff: true));
            else if (gauge > 0 && RelicForgeService.IsAtForgeLocation())
                list.Add(MakePanel(ForgeText.GaugeTitle(), ForgeText.GaugeBody(gauge), "sts2rf_gauge", debuff: true));
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

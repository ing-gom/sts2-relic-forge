using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;              // LocalContext
using MegaCrit.Sts2.Core.Entities.Players;     // Player
using MegaCrit.Sts2.Core.Helpers;              // AscensionHelper
using MegaCrit.Sts2.Core.HoverTips;            // HoverTip, IHoverTip
using MegaCrit.Sts2.Core.Nodes.HoverTips;      // NHoverTipSet
using MegaCrit.Sts2.Core.Runs;                 // RunManager

namespace Sts2RelicForge;

/// <summary>Shared: resolve the top-bar portrait-tooltip control type (both namespace casings seen in the
/// decompile), so a casing drift can't null the patch target (a null target breaks Harmony PatchAll).</summary>
internal static class PortraitTip
{
    public static Type? Type()
        => AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.TopBar.NTopBarPortraitTip")
           ?? AccessTools.TypeByName("MegaCrit.sts2.Core.Nodes.TopBar.NTopBarPortraitTip");
}

/// <summary>
/// Makes the character portrait hoverable even with NO ascension tooltip (ascension 0): the vanilla
/// Initialize sets FocusMode=None there, so OnFocus would never fire and the forge summary could not show.
/// We force FocusMode=All so the portrait always accepts hover. See <see cref="ForgeSummaryFocusPatch"/>.
/// </summary>
[HarmonyPatch]
internal static class ForgeSummaryInitPatch
{
    private static MethodBase? TargetMethod()
    {
        var t = PortraitTip.Type();
        return t == null ? null : AccessTools.Method(t, "Initialize");
    }

    private static void Postfix(object __instance)
    {
        try { if (__instance is Control c) c.FocusMode = Control.FocusModeEnum.All; }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] portrait tip focus-enable failed: {e.Message}"); }
    }
}

/// <summary>
/// Rides the character portrait's own hover tooltip and appends a FORGE SUMMARY (every forged relic's prefix
/// effect + curse, see <see cref="ForgeSummary"/>) under the run's ascension penalties — so hovering the
/// character shows ascension + forge info in ONE native panel, where the player already looks. Rebuilds the
/// tip LIVE each hover (ascension text via <see cref="AscensionHelper"/> + the current summary) and shows it
/// anchored under the portrait, exactly like the vanilla path, then returns false to replace the original
/// show. Pure local read-only UI → co-op safe. On any failure it falls back to the vanilla ascension tooltip.
/// </summary>
[HarmonyPatch]
internal static class ForgeSummaryFocusPatch
{
    private static MethodBase? TargetMethod()
    {
        var t = PortraitTip.Type();
        return t == null ? null : AccessTools.Method(t, "OnFocus");
    }

    private static bool Prefix(object __instance)
    {
        try
        {
            if (__instance is not Control ctrl) return true;
            var run = RunManager.Instance;
            var player = LocalContext.GetMe(run?.State?.Players ?? Enumerable.Empty<Player>());
            if (player == null) return true;

            int level = run!.State.AscensionLevel;
            bool locked = run.State.GameMode.AreAchievementsAndEpochsLocked();

            // Build SEPARATE, CHUNKED panels — ascension (if any) | prefix-effect panels | curse panels — so
            // they sit side by side and a long list spills into further COLUMNS (the game's flow container
            // wraps whole panels rightward) instead of one tall panel running off the bottom of the screen.
            // Each panel needs a UNIQUE Id or NHoverTipSet.Init's RemoveDupes collapses same-titled chunks.
            var tips = new List<IHoverTip>();
            if (level > 0 || locked)
                tips.Add(AscensionHelper.GetHoverTip(player.Character, level, locked));

            var pfxPanels = ForgeSummary.PrefixPanels(player);
            for (int i = 0; i < pfxPanels.Count; i++)
                tips.Add(Panel(ForgeLoc.Ui("SUMMARY_PREFIXES"), pfxPanels[i], "sts2rf_pfx_" + i));

            var cursePanels = ForgeSummary.CursePanels(player);
            for (int i = 0; i < cursePanels.Count; i++)
                tips.Add(Panel(ForgeLoc.Ui("SUMMARY_CURSES"), cursePanels[i], "sts2rf_curse_" + i));

            if (tips.Count == 0) return false;   // ascension 0 + nothing forged → show nothing

            var set = NHoverTipSet.CreateAndShow(ctrl, tips);
            if (set != null) set.GlobalPosition = ctrl.GlobalPosition + new Vector2(0f, ctrl.Size.Y + 20f);
            return false;   // replace the vanilla show with our chunked, side-by-side panels
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] portrait forge summary failed: {e.Message}");
            return true;    // fall back to the vanilla ascension tooltip
        }
    }

    /// <summary>A native tooltip panel with an explicit UNIQUE id (chunks share a title, so the id must
    /// differ or NHoverTipSet.Init's RemoveDupes drops all but the first). Setters are reachable because
    /// ModKit publicizes sts2 (same as NMerchantReforgeButton.MakeTip).</summary>
    private static IHoverTip Panel(string title, string body, string id)
    {
        var t = new HoverTip();
        t.Title = title;
        t.Description = body;
        t.Id = id;
        return t;
    }
}

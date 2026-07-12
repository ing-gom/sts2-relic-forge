using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;

namespace Sts2RelicForge;

/// <summary>
/// Prefixes the click-to-ENLARGE inspect screen's relic NAME with the forged prefix (and curse mark) and
/// tints it to the prefix's tier color — matching the hover tooltip title (<see cref="RelicTooltipPatch"/> +
/// <see cref="HoverTipTitleTintPatch"/>).
///
/// The inspect screen (NInspectRelicScreen.UpdateRelicDisplay) sets its name label straight from
/// <c>relicModel.Title</c>, bypassing the HoverTip whose title we already decorate — so without this the
/// enlarged view showed the plain relic name with no prefix. Unlike the hover path, the label here is a live
/// MegaLabel we own, so we set its text and Modulate directly instead of the invisible-marker handshake.
///
/// Gating: RecordForHover only yields a record for an owned, forged relic (PreviewOnHover returns null out of
/// run and for mutable/owned-unforged relics), and such a relic always rendered via the unlocked+seen branch
/// with its real Title — so a non-null record already implies a decoratable name. History-reconstructed
/// records (DisplayOnly) never reach the inspect screen, but are skipped for safety.
/// </summary>
[HarmonyPatch(typeof(NInspectRelicScreen), "UpdateRelicDisplay")]
internal static class InspectRelicNamePatch
{
    private static void Postfix(NInspectRelicScreen __instance)
    {
        try
        {
            var relics = __instance._relics;
            int idx = __instance._index;
            if (relics == null || idx < 0 || idx >= relics.Count) return;
            RelicModel relic = relics[idx];

            ForgeRecord? rec = RelicForgeService.RecordForHover(relic);
            if (rec is null || rec.DisplayOnly) return;

            string prefix = ForgeText.TitlePrefix(rec);
            string suffix = ForgeText.TitleSuffix(rec);
            if (prefix.Length == 0 && suffix.Length == 0) return;

            var label = __instance._nameLabel;
            if (label == null) return;

            // Rebuild from the relic's real title (idempotent): prepend the prefix, append the curse mark.
            label.SetTextAutoSize(prefix + relic.Title.GetFormattedText() + suffix);
            if (rec.Prefix.Length > 0)
                label.Modulate = Color.FromHtml(PrefixTable.ColorOf(rec.Prefix).TrimStart('#'));
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] inspect name decorate failed: {e.Message}");
        }
    }
}

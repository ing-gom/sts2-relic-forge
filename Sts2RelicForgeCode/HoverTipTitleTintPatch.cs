using System.Text.RegularExpressions;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace Sts2RelicForge;

/// <summary>
/// Tints a forged relic's whole tooltip NAME to its prefix tier color (Terraria/Diablo-style
/// rarity-colored item names). The tooltip title renders in a plain MegaLabel (Godot Label =
/// no BBCode), so the name can't be colored inline; instead RelicTooltipPatch prepends an
/// INVISIBLE color marker (Mark + 6 hex digits + Mark) to the title string. After
/// NHoverTipSet.Init builds the tip nodes, we find the title label, strip the marker, and set
/// its Modulate to the color. All relic tooltips funnel through Init, so the marker is always
/// consumed here and never shown (the marker char is zero-width regardless).
/// </summary>
[HarmonyPatch(typeof(NHoverTipSet), "Init")]
internal static class HoverTipTitleTintPatch
{
    /// <summary>U+2063 INVISIBLE SEPARATOR — brackets the color hex; unused in normal text.</summary>
    public const char Mark = '⁣';

    private static readonly Regex Marker =
        new("^" + Mark + "([0-9a-fA-F]{6})" + Mark, RegexOptions.Compiled);

    private static void Postfix(NHoverTipSet __instance)
    {
        // Runs on EVERY tooltip's Init (a UI render path): a freed label node mid-build would
        // otherwise throw into NHoverTipSet.Init and black-screen whenever a tooltip appears.
        try { Tint(__instance); }
        catch (System.Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] title tint failed: {e.Message}"); }
    }

    private static void Tint(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is MegaLabel label)
            {
                Match m = Marker.Match(label.Text);
                if (m.Success)
                {
                    label.Text = label.Text.Substring(m.Length);
                    label.Modulate = Color.FromHtml(m.Groups[1].Value);
                }
            }
            Tint(child);
        }
    }
}

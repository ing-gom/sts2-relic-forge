using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Redden a relic's icon EVERYWHERE — the top-of-screen inventory bar, the reforge picker, hover tooltips —
/// in proportion to its curse gauge. RelicModel.UpdateTexture(TextureRect) is the single place the game
/// styles a relic's icon (pulse / used / and, for a melted relic, SelfModulate = DarkRed), and BOTH the
/// inventory holder and the picker holder route their icon through it (via the shared NRelic node). So one
/// postfix here tints the icon consistently wherever it shows, using the same SelfModulate idiom the game
/// uses for melted relics — no per-screen node, no layout.
///
/// The tint runs White → dark crimson by gauge %, so an over-reforged relic clearly reads as burning out and
/// a saturated (100%) one is fully crimson = "dead" (its upside is disabled — see SaturatedRelicDisablePatch).
/// gauge 0 restores White so a cleansed relic (gauge reset) drops its tint. Melted relics are left to the
/// game's own DarkRed. Display-only (SelfModulate is visual), so no co-op / sim impact.
///
/// CONTEXT-GATED (see RelicForgeService.IsAtForgeLocation): a merely over-reforged relic (0 &lt; gauge &lt; 100)
/// only reddens at a FORGE LOCATION (rest site / shop) where its curse-risk is actionable — in combat and on
/// the map that red is just noise, so it stays White there. A SATURATED relic is the deliberate exception:
/// its effect is disabled EVERYWHERE, so it stays full-red wherever it shows (with the hover panel explaining
/// why — see RelicExtraPanelsPatch).
/// </summary>
[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.UpdateTexture))]
internal static class GaugeTintPatch
{
    // Full-gauge target: keep RED bright (R≈1) while killing green/blue, so a saturated relic reads as a
    // vivid RED — not the muddy dark crimson a low R gave (which looked gray on darker icons).
    private static readonly Color Red = new(1.0f, 0.14f, 0.14f);

    private static void Postfix(RelicModel __instance, TextureRect texture)
    {
        try
        {
            if (__instance == null || texture == null) return;
            if (__instance.IsMelted) return;                        // game already tinted it DarkRed
            // A SPENT relic (Status.Disabled — its curse is off too, so it's harmless) keeps the game's GRAY
            // "used up" look: clear any red tint and let the _isUsed shader gray it. Only SATURATION (curse
            // still active) reddens — the deliberate red-vs-gray split between "dangerous" and "safe" dead.
            if (RelicForgeService.IsRelicSpent(__instance)) { texture.SelfModulate = Colors.White; return; }
            int gauge = RelicForgeService.CurseGauge(__instance);   // memoized O(1)
            // A saturated (100%) relic is burnt out everywhere → keep it flagged everywhere. A merely
            // over-reforged relic only shows its curse-risk tint at a forge location; elsewhere it's noise.
            bool saturated = RelicForgeService.IsGaugeSaturated(__instance);
            if (!saturated && !RelicForgeService.IsAtForgeLocation()) { texture.SelfModulate = Colors.White; return; }
            // Always set (White at 0) so a previous tint clears after a cleanse resets the gauge. Ease the
            // curve (t²) so it stays near-white at low gauge and reddens sharply toward saturation.
            float t = gauge / 100f;
            texture.SelfModulate = gauge <= 0 ? Colors.White : Colors.White.Lerp(Red, t * t);
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] gauge tint failed: {e.Message}"); }
    }
}

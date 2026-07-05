using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;                      // EventOption
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens;               // NChooseARelicSelection
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Treasure-room relic previews. Unlike shop/reward relics (per-offer .ToMutable()),
/// treasure relics are shared CANONICAL instances pulled from the grab bag, so the
/// generic mutable preview hook skips them. When a holder is initialized we forge a
/// throwaway clone (holder gives us the runState for the deterministic seed + floor) and
/// cache it so the tooltip can show the enhanced numbers + prefix. The obtained relic is
/// still a fresh mutable of the same canonical at the same floor, so its grade matches.
/// </summary>
[HarmonyPatch(typeof(NTreasureRoomRelicHolder), "Initialize")]
internal static class TreasureRelicPreviewPatch
{
    private static void Postfix(RelicModel relic, IRunState runState)
    {
        try { RelicForgeService.OfferPreview(relic, runState.Rng.Seed, runState.TotalFloor); }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] treasure preview failed: {e.Message}"); }
    }
}

/// <summary>
/// Clear the offered-preview cache when the treasure closes, so the shared canonical
/// relics don't keep a forged tooltip in later collection/deck views.
/// </summary>
[HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), "EndRelicVoting")]
internal static class TreasureRelicClearPatch
{
    private static void Postfix() => RelicForgeService.ClearOffers();
}

/// <summary>
/// Event / reward relic CHOICE previews. The choose-a-relic screen (<see cref="NChooseARelicSelection"/>,
/// used by events and rewards) shows shared CANONICAL relics, so — like the treasure room — the generic
/// mutable preview hook skips them and the tooltip shows no prefix/curse decoration. Forge a throwaway
/// clone per offered relic and cache it so the tooltip previews the prefix + curse + enhanced numbers.
/// The obtained relic is a fresh mutable of the same canonical at the same floor, so its grade matches.
/// </summary>
[HarmonyPatch(typeof(NChooseARelicSelection), nameof(NChooseARelicSelection.ShowScreen))]
internal static class ChooseARelicPreviewPatch
{
    private static void Postfix(IReadOnlyList<RelicModel> relics)
    {
        try
        {
            var state = RunManager.Instance?.State;
            if (state == null || relics == null) return;
            foreach (var relic in relics)
                if (relic != null) RelicForgeService.OfferPreview(relic, state.Rng.Seed, state.TotalFloor);
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] choose-a-relic preview failed: {e.Message}"); }
    }
}

/// <summary>Clear the offered-preview cache when the choose-a-relic screen leaves the tree, so the
/// shared canonical relics don't keep a forged tooltip in later collection/deck views.</summary>
[HarmonyPatch(typeof(NChooseARelicSelection), "_ExitTree")]
internal static class ChooseARelicClearPatch
{
    private static void Postfix() => RelicForgeService.ClearOffers();
}

/// <summary>
/// EVENT relic options (a relic offered as an event choice, e.g. a chest event). These are shown as
/// TEXT options via <see cref="EventOption.FromRelic"/>, whose hover tips use <c>HoverTipsExcludingRelic</c>
/// — deliberately EXCLUDING the relic's main tooltip, so our forge decoration never appears. The offered
/// relic IS mutable (WithRelic asserts it), so we forge it for the preview and then swap the option's
/// hover tips to the FULL <c>HoverTips</c> (which include our decorated main tooltip), so hovering the
/// option shows the prefix + curse + enhanced numbers. Obtaining it later re-uses the same graded relic.
/// </summary>
[HarmonyPatch(typeof(EventOption), nameof(EventOption.FromRelic))]
internal static class EventRelicPreviewPatch
{
    private static void Postfix(RelicModel relic, EventOption __result)
    {
        try
        {
            if (__result == null || relic == null) return;
            RelicForgeService.TryForgePreview(relic);   // mutable per-offer relic -> forge for the preview
            if ((RelicForgeService.RecordFor(relic)?.Prefix.Length ?? 0) > 0)
                __result.HoverTips = relic.HoverTips;   // full (decorated) tooltip, not the relic-excluding one
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] event relic preview failed: {e.Message}"); }
    }
}

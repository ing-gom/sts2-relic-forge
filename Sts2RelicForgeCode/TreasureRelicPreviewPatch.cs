using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
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

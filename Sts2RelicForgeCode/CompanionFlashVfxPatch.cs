using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace Sts2RelicForge;

/// <summary>
/// When a grafted effect triggers, the game flashes the DONOR relic's icon above the
/// character (NRelicFlashVfx renders relic.Icon). But the donor is hidden — the player owns
/// the HOST relic (with the prefix), so the flash should show the host's icon instead.
///
/// NRelicFlashVfx.Create(RelicModel) is the single icon-building entry point (the
/// Create(relic, target) overload delegates to it), so swapping the argument here covers
/// every flash. Purely cosmetic: the effect already ran; this only changes which icon floats.
/// </summary>
[HarmonyPatch(typeof(NRelicFlashVfx), nameof(NRelicFlashVfx.Create), new[] { typeof(RelicModel) })]
internal static class CompanionFlashVfxPatch
{
    private static void Prefix(ref RelicModel relic)
    {
        try
        {
            var host = RelicForgeService.HostOf(relic);
            if (host != null) relic = host; // show the owned, prefixed relic instead of the donor
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] companion flash-swap failed: {e.Message}");
        }
    }
}

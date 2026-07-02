using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace Sts2RelicForge;

/// <summary>
/// Heal/Smith end the rest by clearing ALL options (RestSiteSynchronizer.ChooseOption →
/// options.Clear() when Hook.ShouldDisableRemainingRestSiteOptions is true). Reforge is a FREE
/// side-action, so it should NOT vanish with them — it stays available until the player proceeds.
///
/// The rest UI rebuilds its buttons from the option list via UpdateRestSiteOptions (called after a
/// selection, among others). Prefix it to re-add the tracked reforge option whenever it's gone but
/// still enabled (not ended by a penalty, and the player still has something to reforge). No-op when
/// it's already present or disabled, so Heal/Smith clearing themselves is unaffected.
/// </summary>
[HarmonyPatch(typeof(NRestSiteRoom), "UpdateRestSiteOptions")]
internal static class KeepReforgeOptionPatch
{
    private static void Prefix(NRestSiteRoom __instance)
    {
        try
        {
            var reforge = RestSiteReforgeSupport.Current;
            if (reforge == null || !reforge.IsEnabled) return;      // ended by penalty / nothing to reforge
            if (__instance.Options is not List<RestSiteOption> opts) return; // the synchronizer's live list
            if (!opts.Contains(reforge)) opts.Add(reforge);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] keep reforge option failed: {e.Message}");
        }
    }
}

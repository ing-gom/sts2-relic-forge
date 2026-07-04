using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Runs;                 // RunManager

namespace Sts2RelicForge;

/// <summary>
/// Adds the Reforge option to every rest site. RestSiteOption.Generate builds the Heal/Smith list
/// and returns it to the synchronizer, so appending here means our option is part of the synced,
/// index-selectable list. We also inject the option's loc keys + icon at this exact moment (rest
/// site build time), which keeps them correct across language changes without a separate hook.
///
/// SINGLE-PLAYER ONLY. Selecting the option opens <see cref="NReforgeRelicPicker"/> (a local,
/// un-synced UI) and calls <see cref="RelicForgeService.Reforge"/>, which mutates relic state
/// locally with no networked command — both would desync a co-op session. The merchant reforge
/// button is gated the same way (see MerchantReforgeButtonPatch), so we skip adding the campfire
/// option entirely in multiplayer instead of exposing a control that breaks the session.
/// </summary>
[HarmonyPatch(typeof(RestSiteOption), nameof(RestSiteOption.Generate))]
internal static class RestSiteReforgeOptionPatch
{
    private static void Postfix(Player player, List<RestSiteOption> __result)
    {
        try
        {
            if (!ReforgeNet.Available())
            {
                RestSiteReforgeSupport.Current = null; // ensure KeepReforgeOptionPatch never re-adds a stale option
                return;                                 // reforge is SP-only until the networked path lands (ReforgeNet)
            }
            RestSiteReforgeSupport.EnsureLoc();
            var option = new ReforgeRestSiteOption(player);
            RestSiteReforgeSupport.Current = option; // tracked so it can be re-added after Heal/Smith clears the list
            __result.Add(option);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] add reforge rest option failed: {e.Message}");
        }
    }
}

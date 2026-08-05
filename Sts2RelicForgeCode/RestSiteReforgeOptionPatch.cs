using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Runs;                 // RunManager

namespace Sts2RelicForge;

/// <summary>
/// Adds the Reforge AND Cleanse options to every rest site. RestSiteOption.Generate builds the
/// Heal/Smith list and returns it to the synchronizer, so appending here means our options are part
/// of the synced, index-selectable list (added in a fixed order — Reforge then Cleanse — so every
/// client's list matches). We also inject the options' loc keys + icons at this exact moment (rest
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
            // Host: push our forge settings to every client now (rest-site build time), so a client
            // that reforges here derives the SAME curse the host would — before the picker can be used.
            ForgeConfigBroadcaster.BroadcastIfHost();

            if (!ReforgeNet.Available())
            {
                RestSiteReforgeSupport.ByPlayer.Remove(player.NetId);        // don't let a stale option be re-added
                RestSiteReforgeSupport.CleanseByPlayer.Remove(player.NetId);
                return;                                               // reforge unavailable (e.g. no active run)
            }
            RestSiteReforgeSupport.EnsureLoc();

            // IDEMPOTENT ADD. Everything below appends to __result, so this postfix running twice over
            // the SAME list yields a second Reforge + Cleanse pair while Heal/Smith stay single — which
            // is exactly the "two sets of reforge/cleanse at the campfire" report. Two ways that happens:
            // the patch is applied twice (the mod installed a second time under a different id, so two
            // assemblies each patch Generate), or a game build calls Generate more than once over one
            // list. Drop any of ours already present before adding, so the list can hold at most one of
            // each no matter how many times we run. A no-op on a freshly generated list.
            RestSiteReforgeSupport.RemoveOurs(__result);

            var option = new ReforgeRestSiteOption(player);
            RestSiteReforgeSupport.ByPlayer[player.NetId] = option; // per-player, for the synced re-add after Heal/Smith
            __result.Add(option);

            // The cleanse sibling — one free cleanse per visit (see CleanseRestSiteOption). Added
            // whenever the (HOST-authoritative) campfire-cleanse toggle is on — never gated on local
            // config, so the co-op option lists stay identical on every peer; when on, it greys via
            // IsEnabled when there's nothing cursed. OFF = the hard-economy mode (workshop request):
            // the shop is the only cleanser.
            if (HostForgeConfig.CampfireCleanse)
            {
                var cleanse = new CleanseRestSiteOption(player);
                RestSiteReforgeSupport.CleanseByPlayer[player.NetId] = cleanse;
                __result.Add(cleanse);
            }
            else
                RestSiteReforgeSupport.CleanseByPlayer.Remove(player.NetId);   // no stale re-add after Heal/Smith
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] add reforge rest option failed: {e.Message}");
        }
    }
}

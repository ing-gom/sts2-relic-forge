using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;              // LocalContext
using MegaCrit.Sts2.Core.Entities.Players;     // Player
using MegaCrit.Sts2.Core.Entities.RestSite;    // RestSiteOption
using MegaCrit.Sts2.Core.Multiplayer.Game;     // RestSiteSynchronizer
using MegaCrit.Sts2.Core.Nodes.Rooms;          // NRestSiteRoom

namespace Sts2RelicForge;

/// <summary>
/// Keeps the reforge option available after Heal/Smith — CO-OP SAFELY.
///
/// Heal/Smith end the rest by clearing a player's whole option list inside
/// <c>RestSiteSynchronizer.ChooseOption</c> (<c>options.Clear()</c> when
/// <c>ShouldDisableRemainingRestSiteOptions</c> is true). Reforge is a free, repeatable side-action,
/// so it must survive that clear.
///
/// The previous approach re-added it from <c>NRestSiteRoom.UpdateRestSiteOptions</c> — a LOCAL-only
/// UI rebuild. But <c>NRestSiteRoom.Options</c> is <c>_synchronizer.GetLocalOptions()</c>, i.e. the
/// synchronizer's AUTHORITATIVE per-player option list (a live <see cref="List{T}"/>, not a copy). So
/// that re-add mutated the acting player's option list on ONE client only; the same player's list on
/// a peer stayed cleared, the lists diverged, and the next index-based selection threw out of range
/// (<c>optionIndex &gt;= options.Count</c>) → desync.
///
/// Instead, re-add on the SAME synced path the native options use: <c>ChooseOption</c> runs on EVERY
/// client for the acting player, so re-adding here keeps every client's copy of that player's list
/// identical. <c>ChooseOption</c> is async, so we chain the re-add onto its returned Task (after the
/// clear/remove has already run). The option instance is looked up per-player
/// (<see cref="RestSiteReforgeSupport.ByPlayer"/>) so its penalty-ended state is preserved and co-op
/// peers resolve the correct owner. List MEMBERSHIP keys only on <c>HasReforgeable</c> (relics are
/// replicated → identical on all clients); the penalty "ended" state only greys the button
/// (<c>IsEnabled</c>) and never removes it, so it can never diverge the list.
/// </summary>
[HarmonyPatch(typeof(RestSiteSynchronizer), "ChooseOption")]
internal static class ReaddReforgeAfterChoosePatch
{
    private static void Postfix(RestSiteSynchronizer __instance, Player player, ref Task<bool> __result)
    {
        __result = ReaddAfter(__instance, player, __result);
    }

    private static async Task<bool> ReaddAfter(RestSiteSynchronizer sync, Player player, Task<bool> inner)
    {
        bool result = await inner; // let ChooseOption finish (OnSelect + any Clear/RemoveAt) first
        try
        {
            if (player != null
                && RestSiteReforgeSupport.ByPlayer.TryGetValue(player.NetId, out var reforge)
                && RestSiteReforgeSupport.HasReforgeable(player)
                && sync.GetOptionsForPlayer(player) is List<RestSiteOption> opts
                && !opts.Contains(reforge))
            {
                // Runs on every client for this player, so all copies of the list stay identical
                // (index-based selection therefore stays consistent). Only refresh the button UI on
                // the client actually viewing this player's rest site.
                opts.Add(reforge);
                if (LocalContext.IsMe(player))
                    NRestSiteRoom.Instance?.CallDeferred(NRestSiteRoom.MethodName.UpdateRestSiteOptions);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] re-add reforge option failed: {e.Message}");
        }
        return result;
    }
}

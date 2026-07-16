using System;
using System.Collections.Generic;
using System.Threading;
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
        // Postfix wrapping the choose-option result: a synchronous throw here would propagate into
        // RestSiteSynchronizer.ChooseOption. Contain it and leave the original result untouched.
        try { __result = ReaddAfter(__instance, player, __result); }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] reforge re-add failed: {e.Message}"); }
    }

    // We wrap EVERY rest option's choose-task (Heal/Smith/any other mod's option). Chain the re-add
    // onto ChooseOption's OWN task via ContinueWith + Unwrap — NOT by awaiting it inside our own async
    // method. The returned task then MIRRORS `inner` exactly: if the chosen option's handling faults
    // (vanilla's index-guard InvalidOperationException, or another mod's rest-option OnSelect), that
    // fault propagates with its ORIGINAL stack trace and THIS patch never appears in it — so we are no
    // longer mis-blamed for a crash we merely observed. (An async `await inner` wrapper, by contrast,
    // splices this method's frame into that trace.) We still log the true origin, and behavior is
    // otherwise identical to the no-mod path. ExecuteSynchronously runs the continuation inline on the
    // thread that completed `inner` — in Godot that is the main thread (awaits resume on the game's
    // SynchronizationContext), so the node/list touch below stays on the main thread as before.
    private static Task<bool> ReaddAfter(RestSiteSynchronizer sync, Player player, Task<bool> inner)
    {
        return inner.ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion)
            {
                try
                {
                    if (player != null
                        && sync.GetOptionsForPlayer(player) is List<RestSiteOption> opts)
                    {
                        bool changed = false;

                        // Re-add REFORGE (free + repeatable). Membership keys on HasReforgeable (replicated
                        // → identical on all clients); its per-visit "ended" state only greys the button.
                        if (RestSiteReforgeSupport.ByPlayer.TryGetValue(player.NetId, out var reforge)
                            && RestSiteReforgeSupport.HasReforgeable(player)
                            && !opts.Contains(reforge))
                        {
                            opts.Add(reforge);
                            changed = true;
                        }

                        // Re-add CLEANSE (one per visit). Membership keys on HasCleansable (replicated); its
                        // per-visit "used" state only greys the button, so the lists can't diverge.
                        if (RestSiteReforgeSupport.CleanseByPlayer.TryGetValue(player.NetId, out var cleanse)
                            && RestSiteReforgeSupport.HasCleansable(player)
                            && !opts.Contains(cleanse))
                        {
                            opts.Add(cleanse);
                            changed = true;
                        }

                        // Runs on every client for this player, so all copies of the list stay identical
                        // (index-based selection therefore stays consistent). Only refresh the button UI
                        // on the client actually viewing this player's rest site.
                        if (changed && LocalContext.IsMe(player))
                            NRestSiteRoom.Instance?.CallDeferred(NRestSiteRoom.MethodName.UpdateRestSiteOptions);
                    }
                }
                catch (Exception e)
                {
                    // A fault HERE genuinely is our re-add (list mutation / room refresh).
                    MainFile.Logger.Warn(
                        $"[{MainFile.ModId}] re-add reforge option failed (our path): {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                }
            }
            else if (t.IsFaulted)
            {
                // The chosen option's own handling faulted — log the TRUE origin so this patch isn't
                // mis-attributed. We do not swallow it: Unwrap re-propagates `t` unchanged below.
                var e = t.Exception?.GetBaseException();
                MainFile.Logger.Warn(
                    $"[{MainFile.ModId}] rest-option select faulted BEFORE our re-add (origin is the chosen "
                    + $"option, not this patch): {e?.GetType().Name}: {e?.Message}\n{e?.StackTrace}");
            }
            return t; // Unwrap flattens to a Task<bool> that mirrors `inner` (result OR original fault)
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
    }
}

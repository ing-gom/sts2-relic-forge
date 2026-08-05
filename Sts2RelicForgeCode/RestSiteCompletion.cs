using System;
using System.Collections;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace Sts2RelicForge;

/// <summary>
/// Keeps a rest site OPEN while our free side-options (Reforge / Cleanse) are still on it — on game
/// builds that track rest-site completion.
///
/// ★WHY. v0.110 gave every player's rest site a completion gate that v0.107.1 did not have:
///
/// <code>
///   class PlayerRestSite { ...; public TaskCompletionSource completionTaskSource; }
///
///   ChooseOption:  if (completionTaskSource.Task.IsCompleted) throw    // "...already been completed!"
///                  ... options.Clear() ...
///                  if (options.Count == 0) completionTaskSource.SetResult();
///
///   RestSiteRoom.Exit -> BeforeLocalRestSiteExited:
///                  if (options.Count > 0) { options.Clear(); completionTaskSource.SetResult(); }
/// </code>
///
/// Heal/Smith clear the option list, which empties it and thereby COMPLETES the rest site. We then
/// re-add Reforge/Cleanse (they are free, repeatable side-actions — see ReaddReforgeAfterChoosePatch),
/// which on v0.110 lands the run in a state the game treats as impossible: a completed rest site that
/// still has options. Both of its exits from that state throw.
///   1. Click the re-added Reforge/Cleanse -> ChooseOption throws on the completion check.
///   2. Just walk away        -> BeforeLocalRestSiteExited sees options.Count > 0 and calls
///                               SetResult() a SECOND time on an already-completed source, which
///                               throws out of RestSiteRoom.Exit (SetResult, not TrySetResult).
///
/// So the honest fix is not to dedupe the buttons but to stop lying about the state: if we put options
/// back, the rest site is NOT finished, and the completion source has to say so. <see cref="Reopen"/>
/// swaps in a fresh, uncompleted source, restoring exactly the v0.107.1 semantics the mod was written
/// against — the campfire ends when the player leaves it, not the moment Heal empties the list.
///
/// Everything here is reflection over private members, and every failure degrades to "treat the rest
/// site as still open" — which is what v0.107.1 (no completion field at all) genuinely is. The same
/// DLL therefore serves both branches; see RngCompat for the same discipline on the RNG API.
/// </summary>
internal static class RestSiteCompletion
{
    private static readonly FieldInfo? RestSitesF = AccessTools.Field(typeof(RestSiteSynchronizer), "_restSites");
    private static readonly Type? PlayerRestSiteT = AccessTools.Inner(typeof(RestSiteSynchronizer), "PlayerRestSite");
    private static readonly FieldInfo? OptionsF = PlayerRestSiteT == null ? null : AccessTools.Field(PlayerRestSiteT, "options");
    private static readonly FieldInfo? CompletionF = PlayerRestSiteT == null ? null : AccessTools.Field(PlayerRestSiteT, "completionTaskSource");

    /// <summary>True on builds that gate rest sites on a completion source (v0.110+).</summary>
    public static bool Tracked => RestSitesF != null && OptionsF != null && CompletionF != null;

    /// <summary>
    /// If <paramref name="opts"/> belongs to a rest site the game already marked complete, replace its
    /// completion source with a fresh one so the site counts as open again. Returns false when the
    /// state could not be reached at all — the caller then leaves the options alone rather than
    /// re-adding into a site that would throw on the next click or on room exit.
    ///
    /// The owning entry is found by REFERENCE-matching its options list against the one we were handed
    /// (GetOptionsForPlayer returns the live list), so we never have to reproduce the synchronizer's
    /// player-slot indexing.
    /// </summary>
    public static bool Reopen(RestSiteSynchronizer sync, List<RestSiteOption> opts)
    {
        if (!Tracked) return true;                       // v0.107.1: nothing completes a rest site early

        try
        {
            if (RestSitesF!.GetValue(sync) is not IEnumerable entries) return false;
            foreach (var entry in entries)
            {
                if (!ReferenceEquals(OptionsF!.GetValue(entry), opts)) continue;
                if (CompletionF!.GetValue(entry) is not TaskCompletionSource tcs) return false;
                if (!tcs.Task.IsCompleted) return true;   // still open — nothing to do
                CompletionF.SetValue(entry, new TaskCompletionSource());
                return true;
            }
            return false;                                 // list isn't one of the synchronizer's
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] could not reopen the rest site: {e.Message}");
            return false;
        }
    }
}

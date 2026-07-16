using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Re-applies the top-of-screen inventory relics' curse-gauge TINT on every ROOM transition, so the
/// context-gated tint (see GaugeTintPatch / RelicForgeService.IsAtForgeLocation) flips correctly both ways:
/// a merely over-reforged relic reddens on entering a rest site / shop and drops back to White on leaving to
/// combat or the map. Without this the icon keeps its last SelfModulate — a relic reddened at a forge site
/// would linger red into combat (the exact noise the gating removes), and one whitened in combat would stay
/// white back at a rest site until its next flash. (A SATURATED relic re-paints red regardless of room, as
/// intended — its effect is dead everywhere.)
///
/// The game already fires <see cref="RunManager.RoomEntered"/> at the tail of a fully-entered room (after
/// State.CurrentRoom is the new room), so we subscribe to it once per RunManager instance from the prefix of
/// the room-enter chokepoint (EnterRoomInternal) and refresh every inventory holder's status there — which
/// re-runs RelicModel.UpdateTexture and thus our tint postfix. Display-only, per-client → no co-op / sim impact.
/// </summary>
[HarmonyPatch]
internal static class RoomTintRefreshPatch
{
    private static readonly MethodInfo? RefreshStatus =
        AccessTools.Method(typeof(NRelicInventoryHolder), "RefreshStatus");

    // The RunManager instance we've already hooked; re-subscribe only when it changes (new run).
    private static RunManager? _subscribed;

    private static MethodBase? TargetMethod() =>
        AccessTools.Method(typeof(RunManager), "EnterRoomInternal");

    private static void Prefix()
    {
        try
        {
            var rm = RunManager.Instance;
            if (rm == null || ReferenceEquals(rm, _subscribed)) return;
            if (_subscribed != null) _subscribed.RoomEntered -= OnRoomEntered;
            rm.RoomEntered += OnRoomEntered;
            _subscribed = rm;
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] room-tint subscribe failed: {e.Message}"); }
    }

    private static void OnRoomEntered()
    {
        try
        {
            var inv = NRun.Instance?.GlobalUi?.RelicInventory;
            if (inv == null || RefreshStatus == null) return;
            foreach (var holder in inv.RelicNodes)
            {
                try { RefreshStatus.Invoke(holder, null); } catch { /* one bad holder mustn't skip the rest */ }
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] room-tint refresh failed: {e.Message}"); }
    }
}

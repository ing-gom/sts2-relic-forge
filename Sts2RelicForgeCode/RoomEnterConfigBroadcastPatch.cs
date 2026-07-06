using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;   // RunManager

namespace Sts2RelicForge;

/// <summary>
/// Host: broadcast our forge settings on EVERY room entry, so every client has the host's config
/// cached BEFORE any relic is obtained/forged in that room — combat rewards, events, treasure — not
/// just at shops/rests. This closes the pickup gap: rewards arrive at combat END, long after the room
/// (and thus this broadcast) began, so the client already holds the host's curse settings when it
/// re-runs the obtain forge.
///
/// Runs on all clients; <see cref="ForgeConfigBroadcaster.BroadcastIfHost"/> is a no-op off the host.
/// <c>RunManager.EnterRoom</c> is the earliest reliable per-room choke point (the shop/rest broadcasts
/// remain as belt-and-suspenders for the debug <c>room</c> path, which enters via EnterRoomDebug).
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterRoom))]
internal static class RoomEnterConfigBroadcastPatch
{
    private static void Prefix()
    {
        try { ForgeConfigBroadcaster.BroadcastIfHost(); }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] room-enter config broadcast failed: {e.Message}");
        }
    }
}

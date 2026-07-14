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
        // Diagnostic kill-switch (co-op only): lets an affected player stop the per-room re-broadcast
        // if it turns out to be the "black screen on event/room entry" hang. No-op in single-player.
        if (!ForgeConfig.RoomBroadcastEnabled) return;
        try
        {
            // EVERY peer (not just the host): announce this peer's prefix/curse pool fingerprint once
            // per run, so the HOST also observes a client's sister-mod mismatch — the symmetric half
            // of ForgeSafeMode (the client-side half rides rf_config's fingerprint arg).
            ForgeSafeMode.AnnounceOncePerRun();

            ForgeConfigBroadcaster.BroadcastIfHost();
            // Also re-send per-relic reforge counts, so a client that rebuilt its relics on RECONNECT
            // (their counts can't cross the packet wire) reconciles at the next room boundary. Idempotent
            // on every synced peer — a cheap in-sync check in normal play. Shares the RoomBroadcastEnabled
            // kill-switch so an affected co-op player can disable both if a per-room enqueue ever hangs.
            ForgeConfigBroadcaster.BroadcastCountsIfHost();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] room-enter config broadcast failed: {e.Message}");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// CO-OP replica self-heal: every room transition runs CombatStateSynchronizer.StartSync/WaitForSync,
/// where each peer rebuilds every REMOTE player from a SerializablePlayer packet —
/// <c>Player.SyncWithSerializedPlayer</c> removes ALL relics and re-adds them from the wire. That wipe
/// destroys the mod's forge state on the replica: the packet serializer strips our custom SavedProperties
/// (see ReforgeKeyPacketGuardPatch), so the rebuilt relic instances carry no descriptor/count, and the
/// hidden companion relics (excluded from serialization to avoid save-load duplication) vanish outright.
///
/// The room-ENTRY rf_counts broadcast (ForgeConfigBroadcaster.BroadcastCountsIfHost) eventually
/// reconciles the replica back to the host's state — but that is one step too late for any CHECKSUM that
/// runs between the wipe and the reconcile: the peer that owns the relics still has the live companion +
/// forged vars while the replica just lost them → relic-count/props mismatch → state divergence → session
/// drop. Found by coop-verify's shop-phase test: the join's timeline showed the companion + descriptors
/// evaporating the moment the host changed rooms, followed by a StateDivergence disconnect.
///
/// Fix: wrap the rebuild. Prefix snapshots the replica's CURRENT forge state per (relicId, occurrence);
/// Postfix restores it onto the fresh instances SYNCHRONOUSLY (RestoreForged + companion re-graft), so
/// the replica never presents a wiped state to a checksum. This is self-consistent (it restores what THIS
/// peer already knew — no new derivation), and the per-room rf_counts reconcile still runs afterward as
/// the authoritative host correction for genuinely diverged clients.
/// </summary>
[HarmonyPatch(typeof(Player), nameof(Player.SyncWithSerializedPlayer))]
internal static class ForgeReplicaSyncPatch
{
    private sealed class Snap
    {
        public string Desc = "";
        public int Count;
        public bool Cleansed;
        public int Gred;
        /// <summary>The grafted companion's position in the pre-wipe relic list (-1 = none). The relic
        /// LIST ORDER is hashed by the checksum, so the re-graft must land at the owner's index — an
        /// appended re-graft desyncs on order alone (found by the coop-verify timeline: identical relic
        /// sets, different order → StateDivergence).</summary>
        public int CompanionIndex = -1;
    }

    private static void Prefix(Player __instance, out Dictionary<(string, int), Snap>? __state)
    {
        __state = null;
        try
        {
            var run = RunManager.Instance;
            if (run == null || run.IsSingleplayerOrFakeMultiplayer) return;   // replicas exist only in real co-op
            Dictionary<(string, int), Snap>? snaps = null;
            var occ = new Dictionary<string, int>();
            var relics = __instance.Relics;
            for (int idx = 0; idx < relics.Count; idx++)
            {
                var r = relics[idx];
                if (RelicForgeService.IsCompanion(r)) continue;   // re-grafted from its host relic's record
                string id = r.Id.Entry;
                occ.TryGetValue(id, out int i);
                occ[id] = i + 1;
                string? desc = RelicForgeService.DescriptorOf(r);
                int count = RelicForgeService.ReforgeCountOf(r);
                bool cleansed = RelicForgeService.IsCleansed(r);
                int gred = RelicForgeService.GaugeReductionOf(r);
                if (string.IsNullOrEmpty(desc) && count <= 0 && !cleansed && gred <= 0) continue;   // vanilla
                int compIdx = -1;
                for (int c = 0; c < relics.Count; c++)
                    if (RelicForgeService.IsCompanion(relics[c]) && RelicForgeService.HostOf(relics[c]) == r) { compIdx = c; break; }
                (snaps ??= new())[(id, i)] = new Snap
                { Desc = desc ?? "", Count = count, Cleansed = cleansed, Gred = gred, CompanionIndex = compIdx };
            }
            __state = snaps;
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] replica forge snapshot failed: {e.Message}"); }
    }

    private static void Postfix(Player __instance, Dictionary<(string, int), Snap>? __state)
    {
        if (__state == null) return;
        try
        {
            var occ = new Dictionary<string, int>();
            int restored = 0;
            var grafts = new List<(int index, MegaCrit.Sts2.Core.Models.RelicModel host)>();
            foreach (var r in __instance.Relics.ToList())   // grafting later mutates the list — iterate a copy
            {
                if (RelicForgeService.IsCompanion(r)) continue;
                string id = r.Id.Entry;
                occ.TryGetValue(id, out int i);
                occ[id] = i + 1;
                if (!__state.TryGetValue((id, i), out var s)) continue;

                if (!string.IsNullOrEmpty(s.Desc))
                {
                    // Authoritative restore: identity + curse verbatim from the descriptor, numeric deltas
                    // recomputed deterministically (rebuilt instances are fresh = no record, so this applies).
                    RelicForgeService.RestoreForged(r, s.Desc, __instance.RunState.Rng.Seed, r.FloorAddedToDeck,
                                                    s.Count, s.Cleansed, s.Gred, CharAffix.TitleOf(__instance));
                }
                else
                {
                    // No descriptor (count/cleanse-only state) — seed re-derive, same as the legacy
                    // ReconcileToHost branch.
                    RelicForgeService.Forge(r, __instance.RunState.Rng.Seed, r.FloorAddedToDeck,
                                            reforgeCount: s.Count, guaranteePrefix: s.Count > 0,
                                            character: CharAffix.TitleOf(__instance), gaugeReduction: s.Gred);
                    if (s.Cleansed) RelicForgeService.ApplyCleanse(r);
                }
                if (s.CompanionIndex >= 0) grafts.Add((s.CompanionIndex, r));
                else RelicForgeService.GrantCompanionIfAny(r, __instance);   // no known position — append
                restored++;
            }
            // Re-graft AT THE ORIGINAL POSITIONS, ascending, so each Insert reconstructs the pre-wipe
            // layout exactly (the checksum hashes the relic list in order).
            foreach (var (index, host) in grafts.OrderBy(g => g.index))
                RelicForgeService.GrantCompanionIfAny(host, __instance, index);
            if (restored > 0)
                MainFile.Logger.Info($"[{MainFile.ModId}] replica sync: re-applied forge state to {restored} relic(s) of player {__instance.NetId} ({grafts.Count} positioned graft(s)).");
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] replica forge restore failed: {e.Message}"); }
    }
}

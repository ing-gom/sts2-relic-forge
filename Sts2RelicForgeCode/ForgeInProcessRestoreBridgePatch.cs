using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// IN-PROCESS restore bridge — fixes the Rewind (皮皮倒带) mod losing forge state on a turn rewind.
///
/// Rewind snapshots the run through the game's PACKET serializer (PacketWriter → CombatReplay →
/// RunState.FromSerializable on rewind). The packet serializer THROWS on our custom SavedProperty keys,
/// so ReforgeKeyPacketGuardPatch strips "__rf_desc"/"__rf_count"/… from every packet-format write — which
/// means Rewind's snapshot never contains them, and the rewound run re-derives every reforged relic at
/// count 0: a DIFFERENT prefix ("Insightful" count 1 became "Mighty" count 0 in the repro test T11).
/// The v1.0.8 card-play reassert can't help: the rebuilt instances have no record to reassert.
///
/// The snapshot restore happens IN THE SAME PROCESS while the live run is still standing (Rewind calls
/// RunState.FromSerializable BEFORE RunManager.CleanUp) — so the live Records still hold the truth.
/// This postfix detects "deserializing the SAME run that is currently live" (equal run seed, different
/// state instance) and parks the live forge state (descriptor/count/cleansed/gauge) onto the freshly
/// deserialized relic instances via the same pending facility disk loads use — NGame.LoadRun
/// (RunLoadReforgePatch) then restores the enchantment verbatim, exactly like a save/load round-trip.
///
/// Guards: never fires for a genuine load (no live run, or a different seed = different run), and never
/// overrides a descriptor the serialized props actually carried (disk saves keep our keys — only the
/// packet-stripped path arrives empty). Also covers any other in-process restart tool that rides the
/// packet-serialized CombatReplay (the BetterSpire2 "reset round" class reads the DISK save, which
/// carries our keys — unaffected either way).
/// </summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.FromSerializable))]
internal static class ForgeInProcessRestoreBridgePatch
{
    private static void Postfix(RunState __result)
    {
        try
        {
            var live = RunManager.Instance?.State;
            if (live == null || __result == null || ReferenceEquals(live, __result)) return;
            if (live.Rng.Seed != __result.Rng.Seed) return;   // different run — a real load, don't bridge

            // A CHECKPOINT (map-node) rewind reads a JSON save, which PRESERVES our __rf_* keys — so
            // ReforgeLoadCapturePatch + RunLoadReforgePatch already restore the checkpoint's OWN forge state
            // (correctly UNDOING any reforge done after the checkpoint). This bridge exists only for the
            // packet-stripped TURN rewind, where those keys are gone. Running it during a checkpoint load
            // wrongly re-applies the LIVE (post-checkpoint) forge state onto a relic that was first reforged
            // AFTER the checkpoint — its pre-shop save carried no __rf_desc, so the HasPendingDesc guard below
            // never catches it (Bug: a shop reforge survived a map-node rewind to before the shop). Rewind
            // flags its checkpoint loads with CheckpointManager.IsLoading — skip when it is set.
            if (RewindCheckpointIsLoading()) return;

            int bridged = 0;
            foreach (var newPlayer in __result.Players)
            {
                Player? livePlayer = null;
                foreach (var p in live.Players)
                    if (p.NetId == newPlayer.NetId) { livePlayer = p; break; }
                if (livePlayer == null) continue;

                // Live counterparts by (relicId, occurrence) among NON-companions — companions are
                // never serialized, so the new list has none; they re-graft from their host's record.
                var liveByKey = new Dictionary<(string, int), RelicModel>();
                var liveOcc = new Dictionary<string, int>();
                foreach (var r in livePlayer.Relics)
                {
                    if (RelicForgeService.IsCompanion(r)) continue;
                    liveOcc.TryGetValue(r.Id.Entry, out int i);
                    liveOcc[r.Id.Entry] = i + 1;
                    liveByKey[(r.Id.Entry, i)] = r;
                }

                var newOcc = new Dictionary<string, int>();
                foreach (var nr in newPlayer.Relics)
                {
                    if (RelicForgeService.IsCompanion(nr)) continue;
                    newOcc.TryGetValue(nr.Id.Entry, out int i);
                    newOcc[nr.Id.Entry] = i + 1;
                    if (RelicForgeService.HasPendingDesc(nr)) continue;   // props carried it — trust them
                    if (!liveByKey.TryGetValue((nr.Id.Entry, i), out var lr)) continue;
                    var rec = RelicForgeService.RecordFor(lr);
                    if (rec == null) continue;
                    string? desc = RelicForgeService.EncodeDescriptor(rec);
                    if (!string.IsNullOrEmpty(desc)) RelicForgeService.SetPendingDesc(nr, desc!);
                    if (rec.ReforgeCount > 0) RelicForgeService.SetPendingReforgeCount(nr, rec.ReforgeCount);
                    if (rec.Cleansed) RelicForgeService.SetPendingCleansed(nr);
                    if (rec.GaugeReduction > 0) RelicForgeService.SetPendingGaugeReduction(nr, rec.GaugeReduction);
                    bridged++;
                }
            }
            if (bridged > 0)
                MainFile.Logger.Info($"[{MainFile.ModId}] in-process restore bridge: parked forge state for {bridged} relic(s) (packet-stripped snapshot of the live run).");
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] restore bridge failed: {e.Message}"); }
    }

    private static System.Reflection.PropertyInfo? _checkpointIsLoading;
    private static bool _checkpointProbed;

    /// <summary>True while the Rewind mod's CheckpointManager is loading a JSON checkpoint (map-node rewind).
    /// Reflection — Rewind may be absent (then false: the bridge behaves as before). Probe once, cache.</summary>
    private static bool RewindCheckpointIsLoading()
    {
        try
        {
            if (!_checkpointProbed)
            {
                _checkpointProbed = true;
                var t = System.Type.GetType("Rewind.Scripts.CheckpointManager, Rewind");
                _checkpointIsLoading = t?.GetProperty("IsLoading",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public);
            }
            return _checkpointIsLoading != null && (bool)_checkpointIsLoading.GetValue(null)!;
        }
        catch { return false; }   // Rewind absent / renamed → behave as before (bridge fires)
    }
}

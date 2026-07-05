using System;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Save/load persistence. Forge mutations live only on the in-memory DynamicVarSet and
/// are NOT serialized (DynamicVars isn't a [SavedProperty]), so a loaded relic comes back
/// at its canonical value. But the grade is a pure function of (runSeed, relicId,
/// FloorAddedToDeck) — all three survive the save — so re-deriving on load reproduces the
/// exact same enhancement. No side-table needed.
///
/// NGame.LoadRun(runState, ...) receives a fully-restored runState (players + their
/// relics), and runState.Rng.Seed is the run's fixed, serialized seed. We re-forge every
/// owned relic here, before combat reads any value. Freshly-obtained relics already went
/// through RelicCmd.Obtain and are guarded by the Records table, so this only touches
/// load-restored instances.
/// </summary>
[HarmonyPatch(typeof(NGame), "LoadRun")]
internal static class RunLoadReforgePatch
{
    private static void Prefix(RunState runState)
    {
        try
        {
            uint seed = runState.Rng.Seed;
            int count = 0;
            foreach (var player in runState.Players)
            {
                // Snapshot: companions aren't serialized, so player.Relics holds only hosts —
                // but GrantCompanionIfAny mutates the list, so iterate a copy. Re-forge first
                // (re-derives the same seed-deterministic prefix), then re-graft companions.
                var hosts = player.Relics.ToList();
                foreach (var relic in hosts)
                {
                    // A re-forged relic persisted a count>0; re-derive with the same count, and
                    // guarantee a prefix (reforge never lands "no prefix"), matching Reforge().
                    int rf = RelicForgeService.TakePendingReforgeCount(relic);
                    bool cleansed = RelicForgeService.TakePendingCleansed(relic);
                    if (RelicForgeService.Forge(relic, seed, relic.FloorAddedToDeck,
                            reforgeCount: rf, guaranteePrefix: rf > 0) != null)
                        count++;
                    // A shop-cleansed relic re-derived its curse above — strip it again so cleanse sticks.
                    if (cleansed) RelicForgeService.ApplyCleanse(relic);
                }
                foreach (var relic in hosts)
                    RelicForgeService.GrantCompanionIfAny(relic, player);
            }
            if (count > 0)
                MainFile.Logger.Info($"[{MainFile.ModId}] re-applied forge to {count} relic(s) on load.");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] load re-forge failed: {e.Message}");
        }
    }
}

using System;
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
                foreach (var relic in player.Relics)
                    if (RelicForgeService.Forge(relic, seed, relic.FloorAddedToDeck) != null)
                        count++;
            if (count > 0)
                MainFile.Logger.Info($"[{MainFile.ModId}] re-applied forge to {count} relic(s) on load.");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] load re-forge failed: {e.Message}");
        }
    }
}

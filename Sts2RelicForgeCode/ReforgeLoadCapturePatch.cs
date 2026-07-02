using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Sts2RelicForge;

/// <summary>
/// LOAD side of reforge persistence. RelicModel.FromSerializable rebuilds a fresh mutable relic
/// from its saved form; at that moment the serialized "__rf_count" (see ReforgeSaveInjectPatch)
/// is still readable on save.Props, but it is about to be dropped (the game's FillInternal has no
/// matching [SavedProperty] and ignores it). We grab it here and park it on the reconstructed
/// instance, so RunLoadReforgePatch can re-forge with the right count once LoadRun runs.
/// </summary>
[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.FromSerializable))]
internal static class ReforgeLoadCapturePatch
{
    private static void Postfix(SerializableRelic save, RelicModel __result)
    {
        try
        {
            var ints = save.Props?.ints;
            if (ints == null || __result == null) return;
            foreach (var p in ints)
            {
                if (p.name != RelicForgeService.RfCountKey) continue;
                if (p.value > 0) RelicForgeService.SetPendingReforgeCount(__result, p.value);
                break;
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] reforge count load-capture failed: {e.Message}");
        }
    }
}

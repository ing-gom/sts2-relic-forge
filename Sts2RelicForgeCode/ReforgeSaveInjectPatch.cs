using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Sts2RelicForge;

/// <summary>
/// SAVE side of reforge persistence. The forge grade itself is re-derived from
/// (runSeed, relicId, floor) and never serialized — but the reforge COUNT is player-driven, so
/// it is the one value that must ride along in the save. We piggyback it onto the relic's own
/// SavedProperties as an extra int named "__rf_count".
///
/// This is safe both ways: the disk save is name-based JSON (System.Text.Json), so an extra
/// entry round-trips fine, and on load SavedProperties.FillInternal does GetProperty(name)?.
/// SetValue — an unknown name resolves to null and is silently ignored, so vanilla (or a
/// mod-less client) never chokes on it. The value is instead captured by ReforgeLoadCapturePatch
/// before it is discarded.
/// </summary>
[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.ToSerializable))]
internal static class ReforgeSaveInjectPatch
{
    private static void Postfix(RelicModel __instance, SerializableRelic __result)
    {
        try
        {
            int n = RelicForgeService.ReforgeCountOf(__instance);
            if (n <= 0) return;                       // never re-forged -> nothing to persist
            __result.Props ??= new SavedProperties();
            __result.Props.ints ??= new List<SavedProperties.SavedProperty<int>>();
            __result.Props.ints.Add(new SavedProperties.SavedProperty<int>(RelicForgeService.RfCountKey, n));
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] reforge count save-inject failed: {e.Message}");
        }
    }
}

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
            if (n > 0)                                // re-forged -> persist the count
            {
                __result.Props ??= new SavedProperties();
                __result.Props.ints ??= new List<SavedProperties.SavedProperty<int>>();
                __result.Props.ints.Add(new SavedProperties.SavedProperty<int>(RelicForgeService.RfCountKey, n));
            }

            int gred = RelicForgeService.GaugeReductionOf(__instance);
            if (gred > 0)                            // cleansed -> persist the gauge reduction
            {
                __result.Props ??= new SavedProperties();
                __result.Props.ints ??= new List<SavedProperties.SavedProperty<int>>();
                __result.Props.ints.Add(new SavedProperties.SavedProperty<int>(RelicForgeService.RfReductionKey, gred));
            }

            // Compact forge summary ("prefix|rider|self") for the RUN-HISTORY view — history keeps only
            // the display seed string, so the grade can't be re-derived there; we carry a readable
            // summary instead. Only relics that actually rolled a prefix or curse get it.
            var rec = RelicForgeService.RecordFor(__instance);
            if (rec != null && (rec.Prefix.Length > 0 || rec.EnemyRider || rec.SelfCurse.Length > 0))
            {
                __result.Props ??= new SavedProperties();
                __result.Props.strings ??= new List<SavedProperties.SavedProperty<string>>();
                string desc = $"{rec.Prefix}|{(rec.EnemyRider ? rec.EnemyRiderSuffix : "")}|{rec.SelfCurse}";
                __result.Props.strings.Add(new SavedProperties.SavedProperty<string>(RelicForgeService.RfDescKey, desc));
            }

            // Persist the cleansed flag so a shop-cleansed curse doesn't re-derive from the seed on load.
            if (rec != null && rec.Cleansed)
            {
                __result.Props ??= new SavedProperties();
                __result.Props.ints ??= new List<SavedProperties.SavedProperty<int>>();
                __result.Props.ints.Add(new SavedProperties.SavedProperty<int>(RelicForgeService.RfCleansedKey, 1));
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] reforge count save-inject failed: {e.Message}");
        }
    }
}

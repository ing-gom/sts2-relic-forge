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

            // AUTHORITATIVE forge descriptor ("prefix|rider|self|fbStat|fbAmt|fbPct"). This is now the SOURCE
            // OF TRUTH for the enchantment on load: RunLoadReforgePatch RESTORES from it verbatim instead of
            // re-deriving the prefix from the seed (which drifted across save/load when any derivation input
            // shifted). Still doubles as the run-history summary (which reads only the leading fields). Only
            // relics that actually carry a prefix / curse / fallback buff get it.
            var rec = RelicForgeService.RecordFor(__instance);
            string? desc = rec != null ? RelicForgeService.EncodeDescriptor(rec) : null;
            if (desc != null)
            {
                __result.Props ??= new SavedProperties();
                __result.Props.strings ??= new List<SavedProperties.SavedProperty<string>>();
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

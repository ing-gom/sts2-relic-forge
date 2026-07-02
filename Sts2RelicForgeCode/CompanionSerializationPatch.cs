using System;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Keep hidden companion relics OUT of the save. A companion (granted by a companion prefix)
/// lives in player.Relics so its hooks fire, and Player.ToSerializable would otherwise
/// serialize it like any owned relic. That would double-grant on load — the host re-forges
/// and re-grafts a fresh companion on top of the persisted one.
///
/// Instead we mirror the affix design: companions are NEVER serialized; they are re-derived
/// on load (RunLoadReforgePatch re-forges each host with the same seed-deterministic prefix
/// and re-grafts). At serialize time the live Companions tag is available, so we rebuild the
/// serialized relic list from the non-companion relics only.
/// </summary>
[HarmonyPatch(typeof(Player), nameof(Player.ToSerializable))]
internal static class CompanionSerializationPatch
{
    private static void Postfix(Player __instance, SerializablePlayer __result)
    {
        try
        {
            // Only touch the list if this player actually has a companion (cheap common case).
            if (!__instance.Relics.Any(RelicForgeService.IsCompanion)) return;
            __result.Relics = __instance.Relics
                .Where(r => !RelicForgeService.IsCompanion(r))
                .Select(r => r.ToSerializable())
                .ToList();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] companion save-exclude failed: {e.Message}");
        }
    }
}

using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Multiplayer safety guard. Our reforge count rides on a relic's SavedProperties under a custom
/// name "__rf_count" (see ReforgeSaveInjectPatch). Disk saves are name-based JSON, so that is fine
/// — but the PACKET serializer (IPacketSerializable.Serialize, used only for multiplayer sync)
/// encodes each property NAME as a net-id via GetNetIdForPropertyName, which THROWS on any name
/// not registered from a real [SavedProperty] at startup. So a re-forged relic would crash MP sync.
///
/// The packet path never needs our value (peers re-derive the forge from the shared seed, and
/// cross-client reforge-count sync is out of scope), so we simply strip our key from the transient
/// SavedProperties right before it is packet-serialized. The disk JSON path does NOT go through
/// this method, so persistence is unaffected.
/// </summary>
[HarmonyPatch(typeof(SavedProperties), nameof(SavedProperties.Serialize))]
internal static class ReforgeKeyPacketGuardPatch
{
    private static void Prefix(SavedProperties __instance)
    {
        try
        {
            __instance.ints?.RemoveAll(p => p.name == RelicForgeService.RfCountKey || p.name == RelicForgeService.RfCleansedKey);
            __instance.strings?.RemoveAll(p => p.name == RelicForgeService.RfDescKey);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] reforge packet-guard failed: {e.Message}");
        }
    }
}

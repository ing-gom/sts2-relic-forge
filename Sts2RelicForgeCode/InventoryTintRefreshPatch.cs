using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace Sts2RelicForge;

/// <summary>
/// Keep the top-of-screen inventory relic's curse-gauge TINT (GaugeTintPatch on RelicModel.UpdateTexture)
/// up to date. The inventory holder only re-runs its RefreshStatus() — which calls UpdateTexture and thus
/// re-applies our SelfModulate tint — on a StatusChanged event; a reforge / cleanse changes the gauge but
/// NOT the relic's Status, so the bar icon kept its stale (usually White) tint until the next status change.
/// A reforge / cleanse DOES Flash() the relic, and the holder handles that in OnRelicFlashed (which normally
/// only plays the flash animation), so we postfix it to ALSO refresh the status → the tint updates at once.
///
/// Display-only (SelfModulate is visual); RefreshStatus is idempotent and cheap (our CurseGauge is memoized),
/// and firing it on any flash is harmless. Per-client, so no co-op impact.
/// </summary>
[HarmonyPatch(typeof(NRelicInventoryHolder), "OnRelicFlashed")]
internal static class InventoryTintRefreshPatch
{
    private static readonly MethodInfo? RefreshStatus =
        AccessTools.Method(typeof(NRelicInventoryHolder), "RefreshStatus");

    private static void Postfix(NRelicInventoryHolder __instance)
    {
        try { RefreshStatus?.Invoke(__instance, null); }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] inventory tint refresh failed: {e.Message}"); }
    }
}

using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Surfaces a grafted companion's counter (e.g. "attacks until next trigger", "turns until
/// energy") on the HOST relic's icon. The companion is hidden, so its own counter never shows;
/// but counting effects are confusing without the countdown. NRelicInventoryHolder.RefreshAmount
/// draws a single amount label from the model's ShowCounter/DisplayAmount, so we run after it and
/// override the label for hosts carrying a counting companion.
///
/// Combined label for the both-case: if the HOST relic also shows its own counter, the label
/// reads "host|companion" (host's native count first, grafted count second). Otherwise just the
/// companion's count. The host holder refreshes live because GrantCompanionIfAny forwards the
/// companion's DisplayAmountChanged to the host.
/// </summary>
[HarmonyPatch(typeof(NRelicInventoryHolder), "RefreshAmount")]
internal static class CompanionCounterPatch
{
    private static void Postfix(NRelicInventoryHolder __instance)
    {
        try
        {
            if (!RunManager.Instance.IsInProgress) return;
            RelicModel? host = __instance.Relic?.Model;
            if (host == null) return;

            RelicModel? comp = RelicForgeService.RecordFor(host)?.Companion;
            if (comp == null || !comp.ShowCounter) return;     // no counting companion

            // Combine with the host's own counter when it has one; else show just the companion's.
            string text = host.ShowCounter
                ? $"{host.DisplayAmount}|{comp.DisplayAmount}"
                : comp.DisplayAmount.ToString();
            __instance._amountLabel.Visible = true;
            __instance._amountLabel.SetTextAutoSize(text);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] companion counter overlay failed: {e.Message}");
        }
    }
}

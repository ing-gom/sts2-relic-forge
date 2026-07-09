using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Surfaces mod counters on a forged relic's icon — SINGLE writer for the amount label so the
/// overlays never fight each other:
///   · a grafted companion's counter (e.g. "attacks until next trigger") — the companion is hidden,
///     so its own counter never shows, yet counting effects are confusing without the countdown;
///   · Bankrupt's debt meter — stars left until the next asset evaporates.
/// NRelicInventoryHolder.RefreshAmount draws a single amount label from the model's
/// ShowCounter/DisplayAmount, so we run after it and override the label when the host carries any
/// extra counter. Multiple counters join as "host|companion|debt" (host's native count first).
/// Live refresh: companions forward DisplayAmountChanged to the host; the debt meter raises it via
/// CharAffix.NotifyCounter on every spend.
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
            var rec = RelicForgeService.RecordFor(host);
            if (rec == null) return;

            var pieces = new System.Collections.Generic.List<string>(3);
            if (host.ShowCounter) pieces.Add(host.DisplayAmount.ToString());

            RelicModel? comp = rec.Companion;
            if (comp != null && comp.ShowCounter) pieces.Add(comp.DisplayAmount.ToString());

            int debtLeft = CharAffix.BankruptRemaining(host, rec.SelfCurse);   // Bankrupt re-homed onto the curse slot
            if (debtLeft >= 0) pieces.Add(debtLeft.ToString());

            // Nothing beyond the host's own native counter -> leave the vanilla label untouched.
            if (pieces.Count <= (host.ShowCounter ? 1 : 0)) return;

            __instance._amountLabel.Visible = true;
            __instance._amountLabel.SetTextAutoSize(string.Join("|", pieces));
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] companion counter overlay failed: {e.Message}");
        }
    }
}

using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Decorates a forged relic's hover tooltip:
///   - prepends a ★-tier prefix to the Title (plain text — the title MegaLabel doesn't
///     render BBCode),
///   - appends a colored per-var change block to the Description (MegaRichTextLabel
///     renders BBCode, so [green]+N[/green] / [red]-N[/red] show up).
///
/// RelicModel.HoverTip's constructor has already called GetFormattedText(), so __result
/// carries final display strings — we edit them directly. HoverTip is a record struct in
/// sts2.dll; its private setters are reachable because ModKit publicizes sts2.
/// </summary>
[HarmonyPatch(typeof(RelicModel), "get_HoverTip")]
internal static class RelicTooltipPatch
{
    // Runs BEFORE the getter builds its description, so an unforged candidate relic
    // (shop offer, reward/event choice) gets enhanced first and the tooltip's numbers
    // reflect the forge. Owned relics are already forged (guarded); canonical templates
    // are skipped (IsMutable gate inside TryForgePreview).
    private static void Prefix(RelicModel __instance)
    {
        try { RelicForgeService.TryForgePreview(__instance); }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] preview forge failed: {e.Message}"); }
    }

    private static void Postfix(RelicModel __instance, ref HoverTip __result)
    {
        try
        {
            // Owned / shop / reward: __instance was forged in place, so its record is here
            // and its description already shows enhanced numbers — just append the block.
            ForgeRecord? rec = RelicForgeService.RecordFor(__instance);
            RelicModel? descSource = null;

            // Treasure: __instance is a shared canonical, forged only on a cached clone.
            // Read the record off the clone and rebuild the description from its enhanced
            // DynamicVars so the numbers preview correctly too.
            if (rec is null)
            {
                RelicModel? clone = RelicForgeService.PreviewCloneFor(__instance);
                if (clone != null) { rec = RelicForgeService.RecordFor(clone); descSource = clone; }
            }

            if (rec is null || rec.Prefix.Length == 0) return;

            // Bespoke reward relics (LostCoffer/NeowsTalisman): no DynamicVar, so the vanilla
            // description still shows the base counts. Rewrite its [blue]N[/blue] count tokens
            // inline to the real post-forge numbers so the tooltip reads "2 card rewards, 2
            // potions" instead of the base "1, 1". Key by class name (PascalCase), NOT Id.Entry
            // (UPPER_SNAKE) — the bespoke table is authored in PascalCase.
            int[]? counts = BespokeBonus.CountTokens(__instance.GetType().Name, rec.Percent, rec.Amplify);
            // Companion prefixes have no var changes and no bespoke counts, but still decorate
            // (title prefix + grafted-effect note), so don't bail on them.
            if (!rec.HasChanges && counts is null && rec.CompanionRelic is null) return;

            // Title prefix FIRST and on its own, so a description-side failure can never
            // stop the prefix from showing on the relic name. Also prepend an invisible color
            // marker (U+2063 + hex + U+2063) that HoverTipTitleTintPatch reads to tint the
            // whole name to the prefix's tier color (the title MegaLabel can't render BBCode).
            if (__result.Title != null)
            {
                string m = HoverTipTitleTintPatch.Mark.ToString();
                string mark = m + PrefixTable.ColorOf(rec.Prefix).TrimStart('#') + m;
                __result.Title = mark + ForgeText.TitlePrefix(rec) + __result.Title;
            }

            // Description block, isolated: any label/loc hiccup logs and leaves the base
            // description intact rather than dropping the whole decoration.
            try
            {
                string body = __result.Description ?? "";
                if (descSource != null)
                {
                    string enhanced = descSource.DynamicDescription.GetFormattedText();
                    if (!string.IsNullOrEmpty(enhanced)) body = enhanced;
                }
                string tail = ForgeText.DescriptionBlock(rec);
                if (counts != null)
                {
                    // Rewrite the inline counts (1 -> 2) AND list the per-count deltas under
                    // the ⚒ header, so bespoke relics match the numeric-relic tooltip format.
                    body = BespokeBonus.RewriteCounts(body, counts);
                    tail += BespokeBonus.DeltaLines(__instance.GetType().Name, counts);
                }
                __result.Description = body + tail;
            }
            catch (Exception de)
            {
                MainFile.Logger.Warn($"[{MainFile.ModId}] desc decorate failed for {__instance.Id.Entry}: {de}");
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] tooltip decorate failed: {e}");
        }
    }
}

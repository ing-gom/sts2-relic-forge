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
                // Cached offer preview (treasure/choose-a-relic), else an ON-DEMAND preview for any
                // canonical relic hovered during a run — covers all event/reward offer surfaces at once.
                // ISOLATED: forging an on-demand preview (ToMutable + Forge) is the ONLY game-touching
                // call in this outer block, so if a specific relic ever makes the engine throw (e.g. a
                // CanonicalModelException from some canonical-only access deep in the offer/clone path),
                // contain it here — the relic just shows undecorated instead of the whole tooltip
                // decoration silently failing. The full stack is logged with the relic id so the exact
                // frame is recoverable if it recurs.
                try
                {
                    RelicModel? clone = RelicForgeService.PreviewCloneFor(__instance)
                                        ?? RelicForgeService.PreviewOnHover(__instance);
                    if (clone != null) { rec = RelicForgeService.RecordFor(clone); descSource = clone; }
                }
                catch (Exception pe)
                {
                    MainFile.Logger.Warn($"[{MainFile.ModId}] preview resolve failed for {__instance.Id.Entry}: {pe}");
                    return;   // no forged record to show — leave the vanilla tooltip intact
                }
            }

            if (rec is null || rec.Prefix.Length == 0) return;

            // Run-history reconstruction (see HistoryForgeDisplayPatch): no var deltas were stored, so
            // just show the prefix name + curse. Bypasses the numeric/bespoke path below.
            if (rec.DisplayOnly)
            {
                if (__result.Title != null)
                {
                    string mk = HoverTipTitleTintPatch.Mark.ToString();
                    string tint = mk + PrefixTable.ColorOf(rec.Prefix).TrimStart('#') + mk;
                    __result.Title = tint + ForgeText.TitlePrefix(rec) + __result.Title + ForgeText.TitleSuffix(rec);
                }
                try { __result.Description = (__result.Description ?? "") + ForgeText.DescriptionBlock(rec); }
                catch (Exception hde) { MainFile.Logger.Warn($"[{MainFile.ModId}] history desc decorate failed: {hde.Message}"); }
                return;
            }

            // Bespoke reward relics (LostCoffer/NeowsTalisman): no DynamicVar, so the vanilla
            // description still shows the base counts. Rewrite its [blue]N[/blue] count tokens
            // inline to the real post-forge numbers so the tooltip reads "2 card rewards, 2
            // potions" instead of the base "1, 1". Key by class name (PascalCase), NOT Id.Entry
            // (UPPER_SNAKE) — the bespoke table is authored in PascalCase.
            int[]? counts = BespokeBonus.CountTokens(__instance.GetType().Name, rec.Percent, rec.Amplify);
            // Companion-family prefixes (grafted or delayed) have no var changes and no bespoke
            // counts, but still decorate (title prefix + effect note), so don't bail on them.
            bool companionFam = (PrefixTable.ByName(rec.Prefix)?.NoteDisplay.Length ?? 0) > 0;
            // A curse (enemy-rider or self-curse) also decorates even with no var change — common on
            // modded relics that expose no scalable DynamicVars, where the prefix scales nothing but
            // the curse still rides along. Without this, the curse line silently never renders.
            bool hasCurse = rec.EnemyRider || rec.SelfCurse.Length > 0;
            if (!rec.HasChanges && counts is null && !companionFam && !hasCurse) return;

            // A one-time reward relic (LostCoffer/NeowsTalisman) dispenses its bonus ONCE, at
            // AfterObtained. Reforging it at a campfire re-rolls the prefix but can't hand out the
            // reward again — yet counts above reflect the NEW prefix. So once the relic is OWNED
            // (its one-time effect has fired) AND it was re-forged (count > 0), the shown bonus is
            // stale: suppress the inflated numbers and show an "already granted" footnote instead.
            // count == 0 (never re-forged) still matches what was granted, and the not-yet-owned
            // preview must keep showing the real bonus you're about to get — both untouched.
            // Only OWNED relics are mutable; a canonical (offered) relic throws on .Owner access
            // (CanonicalModelException), which previously aborted the whole decoration — guard it.
            bool owned = __instance.IsMutable && (__instance.Owner?.Relics?.Contains(__instance) ?? false);
            bool staleOneTimeBonus = counts != null && rec.ReforgeCount > 0 && owned;

            // Title prefix FIRST and on its own, so a description-side failure can never
            // stop the prefix from showing on the relic name. Also prepend an invisible color
            // marker (U+2063 + hex + U+2063) that HoverTipTitleTintPatch reads to tint the
            // whole name to the prefix's tier color (the title MegaLabel can't render BBCode).
            if (__result.Title != null)
            {
                string m = HoverTipTitleTintPatch.Mark.ToString();
                string mark = m + PrefixTable.ColorOf(rec.Prefix).TrimStart('#') + m;
                __result.Title = mark + ForgeText.TitlePrefix(rec) + __result.Title + ForgeText.TitleSuffix(rec);
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
                if (counts != null && !staleOneTimeBonus)
                {
                    // Rewrite the inline counts (1 -> 2) AND list the per-count deltas under
                    // the ⚒ header, so bespoke relics match the numeric-relic tooltip format.
                    body = BespokeBonus.RewriteCounts(body, counts);
                    tail += BespokeBonus.DeltaLines(__instance.GetType().Name, counts);
                }
                if (staleOneTimeBonus)
                    // Re-forged after the one-time reward was already dispensed: don't advertise
                    // the new (uncollectable) numbers — just note that the effect already fired.
                    tail += BespokeBonus.SpentNote();
                __result.Description = body + tail;
            }
            catch (Exception de)
            {
                MainFile.Logger.Warn($"[{MainFile.ModId}] desc decorate failed for {__instance.Id.Entry}: {de}");
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] tooltip decorate failed for {__instance?.Id.Entry}: {e}");
        }
    }
}

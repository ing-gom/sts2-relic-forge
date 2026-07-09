using System.Text;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2RelicForge;

/// <summary>
/// Builds the tooltip strings from a ForgeRecord.
///   - Title prefix: PLAIN text only (relic title renders in a MegaLabel = Godot Label,
///     no BBCode). Uses the rarity-scaled descriptive adjective, e.g. "전설적인 ".
///   - Description block: rich text (MegaRichTextLabel = BBCode enabled). Uses the game's
///     own color tags [green]/[red] (same as StsTextUtilities.HighlightChangeText) so a
///     raised number reads green +N and a lowered number reads red -N, with magnitude.
/// </summary>
internal static class ForgeText
{
    /// <summary>Localized prefix + space, prepended to the relic title (plain text).</summary>
    public static string TitlePrefix(ForgeRecord rec) => rec.Prefix.Length == 0 ? "" : PrefixTable.Localize(rec.Prefix) + " ";

    /// <summary>
    /// Localized enemy-rider SUFFIX, appended to the relic title (plain text) — e.g. " of Wrath" /
    /// " 〈재앙〉". Empty unless the mechanic is on and the relic carries the rider curse.
    /// </summary>
    public static string TitleSuffix(ForgeRecord rec)
    {
        bool rider = HostForgeConfig.EnemyForgeEnabled && rec.EnemyRider && rec.EnemyRiderSuffix.Length > 0;
        bool self = rec.SelfCurse.Length > 0;
        if (!rider && !self) return "";
        // Just a simple "curse" mark on the name — the exact effect(s) are in the tooltip lines.
        return " " + ForgeLoc.Ui("CURSED_MARK");
    }

    /// <summary>
    /// Rich-text block appended under the relic description: the prefix (as the heading)
    /// followed by ONLY how each stat improved. No rarity/overall-% internals — just the
    /// prefix and the performance gains it granted.
    /// </summary>
    public static string DescriptionBlock(ForgeRecord rec)
    {
        var sb = new StringBuilder();
        // A curse-only relic (no prefix — just a curse) has no prefix header or note; skip straight to
        // the curse line(s) below. Otherwise render the tier-tinted header and any companion/penalty note.
        if (rec.Prefix.Length > 0)
        {
            // Prefix header tinted by tier (MegaRichTextLabel renders [color=#hex]).
            string headerColor = PrefixTable.ColorOf(rec.Prefix);
            sb.Append("\n\n[color=").Append(headerColor).Append("]⚒ ")
              .Append(PrefixTable.Localize(rec.Prefix)).Append("[/color]");
            // Companion-family prefix (grafted / delayed / penalty): no var deltas — show the effect
            // note instead. Boons are green; penalties (curses) are red. Only companion-family
            // prefixes carry a note, so a non-empty note is the signal.
            Prefix? pfx = PrefixTable.ByName(rec.Prefix);
            string note = pfx?.NoteDisplay ?? "";
            // Fallback prefixes carry a {0} chance placeholder — fill it with this relic's rolled odds
            // (derived from the fizzled tier). Guard the format so a translation that drops {0} can't throw.
            if (pfx is { IsFallback: true } && note.Contains("{0}"))
            {
                try { note = string.Format(note, rec.FallbackPercent); }
                catch { /* malformed placeholder in a translation — leave the raw note */ }
            }
            if (note.Length > 0)
            {
                if (pfx!.Mixed)
                    // gamble affix: neither pure boon nor curse — amber, via a hex color tag.
                    sb.Append("\n[color=#e0b64d]").Append(note).Append("[/color]");
                else
                {
                    string c = pfx.Penalty ? "red" : "green";
                    sb.Append('\n').Append('[').Append(c).Append(']').Append(note).Append("[/").Append(c).Append(']');
                }
            }
        }
        foreach (VarChange c in rec.Changes)
        {
            int d = (int)c.Delta;
            string color = c.IsBuff ? "green" : "red"; // colour by benefit, not numeric sign
            string sign = d >= 0 ? "+" : "";           // negative delta already carries '-'
            sb.Append('\n').Append(VarLabel.Of(c.VarName)).Append("  [")
              .Append(color).Append(']').Append(sign).Append(d)
              .Append("[/").Append(color).Append(']');
        }
        // Tier tie-break bonus: a positive prefix that rounded to the same delta as the tier below it
        // carries an extra combat-start chance-of-more (see RelicForgeService.ApplyTierTiebreak). Show it
        // as its own green line UNDER the numeric delta. The fizzle-SUBSTITUTION case (the prefix IS a
        // fallback) is already rendered by the note block above, so it's excluded here.
        if (rec.FallbackPercent > 0 && rec.FallbackStat.Length > 0
            && !(PrefixTable.ByName(rec.Prefix)?.IsFallback ?? false))
        {
            Prefix? fbp = PrefixTable.FallbackByStat(rec.FallbackStat);
            if (fbp != null)
            {
                string note = fbp.NoteDisplay;
                if (note.Contains("{0}")) { try { note = string.Format(note, rec.FallbackPercent); } catch { /* keep raw */ } }
                sb.Append("\n[green]").Append(note).Append("[/green]");
            }
        }
        // Enemy-rider curse: name the SPECIFIC buff this relic grants elites/bosses (only surfaced
        // when the mechanic is enabled). Amber warning so the trade-off is clear.
        if (rec.EnemyRider && HostForgeConfig.EnemyForgeEnabled)
        {
            string effect = RiderSuffix.EffectOf(rec.EnemyRiderSuffix);
            if (effect.Length > 0) sb.Append("\n[color=#e0554d]⚔ ").Append(effect).Append("[/color]");
        }
        // Self-curse: an independent player-side curse (punishes YOU). Distinct ☠ icon + red so it reads
        // apart from the enemy-rider ⚔ line above. The key is either an on-hit self-curse (EffectOf) or a
        // re-homed PENALTY identity — for the latter, fall back to that penalty prefix's own note (its
        // effect + original trigger, e.g. "Weak 1 to self at combat start").
        if (rec.SelfCurse.Length > 0)
        {
            string effect = SelfCurseTable.EffectOf(rec.SelfCurse);
            if (effect.Length == 0) effect = PrefixTable.ByName(rec.SelfCurse)?.NoteDisplay ?? "";
            if (effect.Length > 0) sb.Append("\n[color=#c0554d]☠ ").Append(effect).Append("[/color]");
        }
        return sb.ToString();
    }
}

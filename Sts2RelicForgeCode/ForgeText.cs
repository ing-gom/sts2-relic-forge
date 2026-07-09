using System.Text;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2RelicForge;

/// <summary>
/// Builds the tooltip strings from a ForgeRecord.
///   - Title prefix/suffix: PLAIN text (the relic title renders in a MegaLabel = Godot Label, no BBCode).
///   - Main description block (<see cref="DescriptionBlock"/>): ONLY the numeric stat deltas the forge
///     produced — rich text ([green]+N[/green] / [red]-N[/red] via MegaRichTextLabel).
///   - The prefix EFFECT and the CURSE are surfaced as SEPARATE hover panels (see RelicExtraPanelsPatch),
///     mirroring how a card shows keyword sub-tooltips, instead of being crammed into the main tooltip.
///     History-reconstructed records (DisplayOnly) can't spawn panels, so DescriptionBlock inlines the
///     full block for them (unchanged behavior).
/// </summary>
internal static class ForgeText
{
    /// <summary>Localized prefix + space, prepended to the relic title (plain text). "" for no prefix.</summary>
    public static string TitlePrefix(ForgeRecord rec) => rec.Prefix.Length == 0 ? "" : PrefixTable.Localize(rec.Prefix) + " ";

    /// <summary>Curse mark appended to the relic title (plain text). Empty unless the relic is cursed.</summary>
    public static string TitleSuffix(ForgeRecord rec)
    {
        bool rider = HostForgeConfig.EnemyForgeEnabled && rec.EnemyRider && rec.EnemyRiderSuffix.Length > 0;
        bool self = rec.SelfCurse.Length > 0;
        if (!rider && !self) return "";
        // Just a simple "curse" mark on the name — the exact effect(s) are in the curse panel.
        return " " + ForgeLoc.Ui("CURSED_MARK");
    }

    /// <summary>
    /// Main-tooltip block: ONLY the numeric stat changes (the forge's boosts), under a tier-tinted ⚒
    /// prefix header. The prefix effect and curse are shown in their own panels (RelicExtraPanelsPatch).
    /// A history-reconstructed record (DisplayOnly) can't spawn panels, so it inlines the full block —
    /// effect note, tie-break, and curse — exactly as before.
    /// </summary>
    public static string DescriptionBlock(ForgeRecord rec)
    {
        var sb = new StringBuilder();
        bool inlineAll = rec.DisplayOnly;   // history view: no panels available, so inline everything

        // ⚒ prefix header — grouping for the numbers below (or, for history, whenever there's a prefix
        // to name). Tier-tinted; MegaRichTextLabel renders [color=#hex].
        if (rec.Prefix.Length > 0 && (rec.HasChanges || inlineAll))
        {
            string headerColor = PrefixTable.ColorOf(rec.Prefix);
            sb.Append("\n\n[color=").Append(headerColor).Append("]⚒ ")
              .Append(PrefixTable.Localize(rec.Prefix)).Append("[/color]");
        }

        // History only: the prefix effect note inline (live relics get the panel instead).
        if (inlineAll)
        {
            string note = PrefixNote(rec);
            if (note.Length > 0) sb.Append('\n').Append(note);
        }

        // Numeric stat deltas — always in the main tooltip. Colour by benefit, not numeric sign.
        foreach (VarChange c in rec.Changes)
        {
            int d = (int)c.Delta;
            string color = c.IsBuff ? "green" : "red";
            string sign = d >= 0 ? "+" : "";           // negative delta already carries '-'
            sb.Append('\n').Append(VarLabel.Of(c.VarName)).Append("  [")
              .Append(color).Append(']').Append(sign).Append(d)
              .Append("[/").Append(color).Append(']');
        }

        // History only: tie-break bonus + curse lines inline.
        if (inlineAll)
        {
            string tb = TieBreakNote(rec);
            if (tb.Length > 0) sb.Append('\n').Append(tb);
            string curse = CurseBody(rec);
            if (curse.Length > 0) sb.Append('\n').Append(curse);
        }
        return sb.ToString();
    }

    // ---- Separate hover panels (built here, shown by RelicExtraPanelsPatch) ----

    /// <summary>Title of the prefix-effect panel: the localized prefix name (plain text, MegaLabel).</summary>
    public static string PrefixEffectTitle(ForgeRecord rec) => PrefixTable.Localize(rec.Prefix);

    /// <summary>Body of the prefix-effect panel: the companion/mixed/reactive effect note plus any
    /// tie-break bonus line. Empty when the prefix has no effect text (a pure numeric prefix — those
    /// only show their +N in the main tooltip, so no effect panel).</summary>
    public static string PrefixEffectBody(ForgeRecord rec)
    {
        string note = PrefixNote(rec);
        string tb = TieBreakNote(rec);
        if (note.Length == 0) return tb;
        return tb.Length == 0 ? note : note + "\n" + tb;
    }

    /// <summary>Title of the curse panel.</summary>
    public static string CurseTitle(ForgeRecord rec) => ForgeLoc.Ui("CURSE_TITLE");

    /// <summary>Body of the curse panel: the enemy-rider line and/or the self-curse line. The self-curse
    /// key is either an on-hit curse (EffectOf) or a re-homed penalty identity (its prefix note).</summary>
    public static string CurseBody(ForgeRecord rec)
    {
        var sb = new StringBuilder();
        if (rec.EnemyRider && HostForgeConfig.EnemyForgeEnabled)
        {
            string effect = RiderSuffix.EffectOf(rec.EnemyRiderSuffix);
            if (effect.Length > 0)
                sb.Append(sb.Length > 0 ? "\n" : "").Append("[color=#e0554d]⚔ ").Append(effect).Append("[/color]");
        }
        if (rec.SelfCurse.Length > 0)
        {
            string effect = SelfCurseTable.EffectOf(rec.SelfCurse);
            if (effect.Length == 0) effect = PrefixTable.ByName(rec.SelfCurse)?.NoteDisplay ?? "";
            if (effect.Length > 0)
                sb.Append(sb.Length > 0 ? "\n" : "").Append("[color=#c0554d]☠ ").Append(effect).Append("[/color]");
        }
        return sb.ToString();
    }

    // ---- shared note builders ----

    /// <summary>The prefix's companion/mixed/penalty effect note (colored), or "" for a pure numeric prefix.</summary>
    private static string PrefixNote(ForgeRecord rec)
    {
        Prefix? pfx = PrefixTable.ByName(rec.Prefix);
        string note = pfx?.NoteDisplay ?? "";
        if (note.Length == 0) return "";
        // Fallback prefixes carry a {0} chance placeholder — fill it with this relic's rolled odds.
        if (pfx is { IsFallback: true } && note.Contains("{0}"))
        {
            try { note = string.Format(note, rec.FallbackPercent); }
            catch { /* malformed placeholder in a translation — leave the raw note */ }
        }
        if (pfx!.Mixed) return "[color=#e0b64d]" + note + "[/color]";   // gamble affix: amber
        string c = pfx.Penalty ? "red" : "green";
        return "[" + c + "]" + note + "[/" + c + "]";
    }

    /// <summary>Tie-break bonus line (a positive prefix that rounded to the same delta as the tier below
    /// gains a combat-start chance-of-more), or "". Excludes the fizzle-SUBSTITUTION case (the prefix IS a
    /// fallback — that's already the note in <see cref="PrefixNote"/>).</summary>
    private static string TieBreakNote(ForgeRecord rec)
    {
        if (rec.FallbackPercent <= 0 || rec.FallbackStat.Length == 0
            || (PrefixTable.ByName(rec.Prefix)?.IsFallback ?? false)) return "";
        Prefix? fbp = PrefixTable.FallbackByStat(rec.FallbackStat);
        if (fbp == null) return "";
        string note = fbp.NoteDisplay;
        if (note.Contains("{0}")) { try { note = string.Format(note, rec.FallbackPercent); } catch { /* keep raw */ } }
        return "[green]" + note + "[/green]";
    }
}

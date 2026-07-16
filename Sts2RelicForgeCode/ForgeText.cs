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
    /// Main-tooltip block. For a LIVE relic this is now EMPTY: the forge's numeric boosts moved into the
    /// prefix panel (see <see cref="PrefixEffectBody"/> / RelicExtraPanelsPatch), joining the effect note
    /// and curse that were already there, so the relic's own tooltip stays vanilla. A history-reconstructed
    /// record (DisplayOnly) can't spawn panels, so it still inlines the full block — ⚒ header, effect note,
    /// numeric deltas, tie-break, and curse — exactly as before.
    /// </summary>
    public static string DescriptionBlock(ForgeRecord rec)
    {
        if (!rec.DisplayOnly) return "";   // live relics: everything is in the hover panels now

        // History view: no panels available, so inline the full block under a tier-tinted ⚒ header.
        var sb = new StringBuilder();
        sb.Append(PrefixHeader(rec));                       // "" when there's no prefix to name
        string note = PrefixNote(rec);
        if (note.Length > 0) sb.Append('\n').Append(note);
        string deltas = DeltaLines(rec);                    // "" — reconstructed records store no var deltas
        if (deltas.Length > 0) sb.Append('\n').Append(deltas);
        string tb = TieBreakNote(rec);
        if (tb.Length > 0) sb.Append('\n').Append(tb);
        string curse = CurseBody(rec);
        if (curse.Length > 0) sb.Append('\n').Append(curse);
        return sb.ToString();
    }

    /// <summary>Tier-tinted "⚒ &lt;prefix name&gt;" header line (with a leading blank line), or "" when the
    /// record has no prefix. Groups the deltas beneath it in the inline (history / bespoke) contexts.</summary>
    public static string PrefixHeader(ForgeRecord rec)
        => rec.Prefix.Length == 0
            ? ""
            : "\n\n[color=" + PrefixTable.ColorOf(rec.Prefix) + "]⚒ " + PrefixTable.Localize(rec.Prefix) + "[/color]";

    /// <summary>The per-var numeric stat deltas ([green]+N[/green] / [red]-N[/red]), one per line, no leading
    /// newline. Coloured by benefit, not numeric sign. "" when the forge changed no numeric var.</summary>
    private static string DeltaLines(ForgeRecord rec)
    {
        var sb = new StringBuilder();
        foreach (VarChange c in rec.Changes)
        {
            int d = (int)c.Delta;
            string color = c.IsBuff ? "green" : "red";
            string sign = d >= 0 ? "+" : "";               // negative delta already carries '-'
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(VarLabel.Of(c.VarName)).Append("  [")
              .Append(color).Append(']').Append(sign).Append(d)
              .Append("[/").Append(color).Append(']');
        }
        return sb.ToString();
    }

    // ---- Separate hover panels (built here, shown by RelicExtraPanelsPatch) ----

    /// <summary>Title of the prefix-effect panel: the localized prefix name (plain text, MegaLabel).</summary>
    public static string PrefixEffectTitle(ForgeRecord rec) => PrefixTable.Localize(rec.Prefix);

    /// <summary>Body of the prefix panel: the forge's numeric boosts ([green]+N[/green] / [red]-N[/red])
    /// first, then the companion/mixed/reactive effect note, then any tie-break bonus line. A pure numeric
    /// prefix (e.g. 신성한 +5) has no effect text but still fills this from its deltas, so it now gets its own
    /// panel too instead of inlining the numbers into the relic's main tooltip. Empty only when the forge
    /// produced neither a numeric change nor an effect note.</summary>
    public static string PrefixEffectBody(ForgeRecord rec)
    {
        var sb = new StringBuilder();
        void Add(string s) { if (s.Length == 0) return; if (sb.Length > 0) sb.Append('\n'); sb.Append(s); }
        Add(DeltaLines(rec));    // numeric boosts first — this is what turns a pure-numeric prefix into a panel
        Add(PrefixNote(rec));
        Add(TieBreakNote(rec));
        return sb.ToString();
    }

    /// <summary>The QUALITATIVE prefix note(s) only — companion/mixed/penalty note + tie-break — WITHOUT the
    /// numeric var deltas. Used by the consolidated <see cref="ForgeSummary"/>, which groups the numeric
    /// deltas by variable itself (into ranges) and groups these notes by identical text.</summary>
    public static string NotesOnly(ForgeRecord rec)
    {
        var sb = new StringBuilder();
        void Add(string s) { if (s.Length == 0) return; if (sb.Length > 0) sb.Append('\n'); sb.Append(s); }
        Add(PrefixNote(rec));
        Add(TieBreakNote(rec));
        return sb.ToString();
    }

    /// <summary>Title of the curse-gauge panel.</summary>
    public static string GaugeTitle() => ForgeLoc.Ui("GAUGE_TITLE");

    /// <summary>Title of the SATURATED panel (a relic whose curse-aura is full = its effect is dead).</summary>
    public static string SaturatedTitle() => ForgeLoc.Ui("SATURATED_TITLE");

    /// <summary>Body of the SATURATED panel: a plain "the curse-aura is full, so this relic no longer works"
    /// note. Shown EVERYWHERE (unlike the numeric gauge panel), because a saturated relic keeps its red icon
    /// off the forge location — this is what explains that red. See RelicExtraPanelsPatch.</summary>
    public static string SaturatedBody() => "[color=#c0554d]" + ForgeLoc.Ui("SATURATED_BODY") + "[/color]";

    /// <summary>Body of the curse-gauge panel: the fill percent + the escalating flavor band, in crimson.
    /// Empty at gauge 0 (never re-forged). See <see cref="RelicForgeService.CurseGauge"/> / GaugeBand.</summary>
    public static string GaugeBody(int gauge)
    {
        if (gauge <= 0) return "";
        string fill = ForgeLoc.Ui("GAUGE_FILL");
        try { fill = string.Format(fill, gauge); } catch { /* malformed {0} in a translation — show raw */ }
        return "[color=#c0554d]" + fill + "[/color]\n" + ForgeLoc.Ui("GAUGE_BAND" + RelicForgeService.GaugeBand(gauge));
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

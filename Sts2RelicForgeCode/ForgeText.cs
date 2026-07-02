using System.Text;

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
    public static string TitlePrefix(ForgeRecord rec) => PrefixTable.Localize(rec.Prefix) + " ";

    /// <summary>
    /// Rich-text block appended under the relic description: the prefix (as the heading)
    /// followed by ONLY how each stat improved. No rarity/overall-% internals — just the
    /// prefix and the performance gains it granted.
    /// </summary>
    public static string DescriptionBlock(ForgeRecord rec)
    {
        var sb = new StringBuilder();
        // Prefix header tinted by tier (MegaRichTextLabel renders [color=#hex]).
        string headerColor = PrefixTable.ColorOf(rec.Prefix);
        sb.Append("\n\n[color=").Append(headerColor).Append("]⚒ ")
          .Append(PrefixTable.Localize(rec.Prefix)).Append("[/color]");
        // Companion-family prefix (grafted or delayed): no var deltas — show the effect note
        // instead, tinted like a boon (green), e.g. "전투 시작 시 가시 2". Only companion
        // prefixes carry a note, so a non-empty note is the signal.
        {
            string note = PrefixTable.ByName(rec.Prefix)?.NoteDisplay ?? "";
            if (note.Length > 0) sb.Append('\n').Append("[green]").Append(note).Append("[/green]");
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
        return sb.ToString();
    }
}

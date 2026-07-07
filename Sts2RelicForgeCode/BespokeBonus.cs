using System;
using System.Text;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2RelicForge;

/// <summary>
/// Per-relic "bonus" effects for HARDCODED reward relics that expose no DynamicVar, so the
/// generic var-scaling can't touch them. The forge still assigns them a prefix (its pct is
/// stored on the record even with no var changes); a bespoke Harmony patch reads that pct
/// and grants extra effects. This class centralizes the pct→bonus mapping and the localized
/// tooltip note so the patch and the tooltip agree (and both survive save/load, since the
/// prefix is re-derived on load).
/// </summary>
internal static class BespokeBonus
{
    /// <summary>Extra (card rewards, potions) LostCoffer grants for a rolled prefix.</summary>
    public static (int cards, int potions) LostCoffer(double pct, bool amplify)
    {
        if (amplify) return (1, -1);    // Volatile: trade the potion for a 2nd card reward
        if (pct >= 0.50) return (1, 1); // Legendary: 2 card rewards + 2 potions
        if (pct >= 0.25) return (1, 0); // Godly/Demonic: 2 card rewards
        if (pct >= 0.10) return (0, 1); // Superior/Forceful: 2 potions
        if (pct > 0) return (0, 0);     // weak positive: prefix only
        return (0, -1);                 // negative: lose the potion
    }

    /// <summary>How many basic Strikes AND basic Defends NeowsTalisman upgrades (base 1 each).</summary>
    public static int NeowsTalismanCount(double pct)
    {
        if (pct >= 0.50) return 3; // Legendary
        if (pct >= 0.18) return 2; // Superior+
        if (pct > 0) return 1;     // base
        return 0;                  // negative: upgrade nothing
    }

    /// <summary>
    /// The enhanced values for the base description's count tokens, in document order, or
    /// null if this relic isn't a bespoke-reward relic. The vanilla loc wraps each count in
    /// [blue]N[/blue]; <see cref="RewriteCounts"/> swaps these in so the tooltip shows the
    /// real post-forge numbers inline (localization-independent — the markup is the same in
    /// every language). LostCoffer: (card rewards, potions). NeowsTalisman: (Strikes, Defends).
    /// </summary>
    public static int[]? CountTokens(string relicType, double pct, bool amplify)
    {
        if (relicType == "LostCoffer")
        {
            var (c, p) = LostCoffer(pct, amplify);
            return new[] { 1 + c, Math.Max(0, 1 + p) };
        }
        if (relicType == "NeowsTalisman")
        {
            int n = NeowsTalismanCount(pct);
            return new[] { n, n };
        }
        return null;
    }

    private static readonly Regex BlueCount = new(@"\[blue\](\d+)\[/blue\]", RegexOptions.Compiled);

    /// <summary>
    /// Replace the description's [blue]N[/blue] count tokens (in order) with the enhanced
    /// values, recoloring by benefit: [green] if raised, [red] if lowered, unchanged if equal.
    /// </summary>
    public static string RewriteCounts(string desc, int[] newVals)
    {
        int i = 0;
        return BlueCount.Replace(desc, m =>
        {
            if (i >= newVals.Length) return m.Value;
            int baseV = int.Parse(m.Groups[1].Value);
            int nv = newVals[i++];
            string color = nv > baseV ? "green" : nv < baseV ? "red" : "blue";
            return $"[{color}]{nv}[/{color}]";
        });
    }

    /// <summary>
    /// Colored per-count delta lines (vs the base of 1 each), appended under the ⚒ prefix
    /// header so bespoke relics read like numeric ones (e.g. "카드 보상  +1"). <paramref name="totals"/>
    /// is <see cref="CountTokens"/>'s result. Empty when nothing changed.
    /// </summary>
    public static string DeltaLines(string relicType, int[] totals)
    {
        string[] labels = LabelsFor(relicType);
        var sb = new StringBuilder();
        for (int i = 0; i < totals.Length && i < labels.Length; i++)
        {
            int d = totals[i] - 1; // every bespoke count has base 1
            if (d == 0) continue;
            string color = d > 0 ? "green" : "red";
            string sign = d > 0 ? "+" : "";
            sb.Append('\n').Append(labels[i]).Append("  [").Append(color).Append(']')
              .Append(sign).Append(d).Append("[/").Append(color).Append(']');
        }
        return sb.ToString();
    }

    private static string[] LabelsFor(string relicType)
    {
        if (relicType == "LostCoffer")
            return new[] { ForgeLoc.Ui("BESPOKE_CARD_REWARD"), ForgeLoc.Ui("BESPOKE_POTION") };
        if (relicType == "NeowsTalisman")
            return new[] { ForgeLoc.Ui("BESPOKE_STRIKE_UPGRADE"), ForgeLoc.Ui("BESPOKE_DEFEND_UPGRADE") };
        return Array.Empty<string>();
    }

    /// <summary>True for a hardcoded ONE-TIME reward relic (its bonus is dispensed once, at
    /// AfterObtained). Reforging such a relic can't retroactively change what was already granted.</summary>
    public static bool IsOneTimeReward(string relicType)
        => relicType == "LostCoffer" || relicType == "NeowsTalisman";

    /// <summary>The BBCode color marker unique to <see cref="SpentNote"/>, so tests / callers can
    /// detect the footnote in a built tooltip string without depending on the localized text.</summary>
    public const string SpentMarker = "#9a9a9a";

    /// <summary>
    /// Muted note shown under the ⚒ header for an already-obtained one-time reward relic whose
    /// prefix was re-rolled at a campfire: the card/potion/upgrade bonus was handed out at the
    /// pre-reforge prefix and can't be re-collected, so the tooltip must not advertise the new
    /// (uncollectable) numbers. Localization-independent gray so it reads as a footnote.
    /// </summary>
    public static string SpentNote()
        => $"\n[color={SpentMarker}]{ForgeLoc.Ui("BESPOKE_SPENT")}[/color]";
}

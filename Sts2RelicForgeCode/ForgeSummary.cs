using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Builds the CONSOLIDATED forge read-out shown on the character-portrait hover (see
/// <see cref="ForgeSummaryFocusPatch"/>). Rather than one line per relic (which grows without bound as you
/// forge more relics), effects are GROUPED by kind so the panel count stays small:
///   • numeric boosts are grouped per stat and shown as a RANGE (e.g. "블록 +5~8") — the values differ by
///     prefix tier / relic base, so a range compresses many relics into one line;
///   • qualitative notes (companion grafts, tie-breaks) and curses are grouped by identical text with a
///     "×N" count (curses stack in play, so the count is the total applied).
/// Long lists are still chunked into panels of <see cref="PerPanel"/> so they wrap into columns. Reuses
/// <see cref="ForgeText"/>/<see cref="VarLabel"/> so wording matches the relic tooltips. Read-only → co-op safe.
/// </summary>
internal static class ForgeSummary
{
    private const int PerPanel = 5;

    public static bool HasAny(Player player) => PrefixLines(player).Count > 0 || CurseLines(player).Count > 0;

    public static List<string> PrefixPanels(Player player) => Chunk(PrefixLines(player));
    public static List<string> CursePanels(Player player) => Chunk(CurseLines(player));

    private static List<string> PrefixLines(Player player)
    {
        // ONLY qualitative, TRIGGERED effects (companion grafts, on-hit / combat-start / conditional notes),
        // grouped by identical text with a ×N count. Pure numeric boosts (Block +8 etc.) are DELIBERATELY
        // excluded: they apply straight to the relic with no trigger, so listing bare numbers here — divorced
        // from the relic — would just confuse ("when does this fire?"). The relic's own tooltip shows those.
        var notes = new Dictionary<string, int>();
        var order = new List<string>();
        foreach (var relic in player.Relics.ToList())
        {
            if (RelicForgeService.IsCompanion(relic)) continue;
            var rec = RelicForgeService.RecordFor(relic);
            if (rec == null) continue;
            string note = ForgeText.NotesOnly(rec);
            if (note.Length == 0) continue;
            if (!notes.ContainsKey(note)) { notes[note] = 0; order.Add(note); }
            notes[note]++;
        }
        var lines = new List<string>();
        foreach (var n in order)
            lines.Add("• " + Inline(n) + (notes[n] > 1 ? $" (×{notes[n]})" : ""));
        return lines;
    }

    private static List<string> CurseLines(Player player)
    {
        var curses = new Dictionary<string, int>();
        var order = new List<string>();
        foreach (var relic in player.Relics.ToList())
        {
            if (RelicForgeService.IsCompanion(relic)) continue;
            var rec = RelicForgeService.RecordFor(relic);
            if (rec == null) continue;
            string c = ForgeText.CurseBody(rec);
            if (c.Length == 0) continue;
            if (!curses.ContainsKey(c)) { curses[c] = 0; order.Add(c); }
            curses[c]++;
        }
        var lines = new List<string>();
        foreach (var c in order)
            lines.Add("• " + Inline(c) + (curses[c] > 1 ? $" (×{curses[c]})" : ""));
        return lines;
    }

    private static List<string> Chunk(List<string> lines)
    {
        var panels = new List<string>();
        for (int i = 0; i < lines.Count; i += PerPanel)
            panels.Add(string.Join("\n", lines.GetRange(i, Math.Min(PerPanel, lines.Count - i))));
        return panels;
    }

    /// <summary>Flatten a multi-line effect body to one line (each line's BBCode tags are self-contained).</summary>
    private static string Inline(string s) => s.Replace("\n", ", ");
}

using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Dev-console command <c>forgeselfcurse &lt;curse|all&gt; [relic-id]</c> — grants a relic carrying a
/// FORCED self-curse, so each one (Weak / Frail / Vulnerable / Dazed / random on an unblocked hit) can
/// be tested without waiting on the random roll. Mirrors <see cref="ForgeCurseCmd"/> (enemy-rider), but
/// stamps <c>rec.SelfCurse</c> instead. TAKE AN UNBLOCKED HIT in combat to see it fire (proportional to
/// the number of unblocked hits). Auto-registered by DevConsole reflection over AbstractConsoleCmd.
/// </summary>
public class ForgeSelfCurseCmd : AbstractConsoleCmd
{
    public override string CmdName => "forgeselfcurse";
    public override string Args => "<curse|all> [relic-id]";
    public override string Description =>
        "Grants a relic with a forced self-curse (fires when you take unblocked damage); 'all' grants one relic per self-curse.";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (LocalCmdGuard.BlockInRealCoop() is { } blocked) return blocked;   // local-only mutation = desync in real co-op
        if (issuingPlayer == null)
            return new CmdResult(success: false, "No active player — start a run first.");
        if (args.Length < 1)
            return new CmdResult(success: false, $"Usage: forgeselfcurse <curse|all> [relic-id]. Curses: {CurseNames()}");

        if (args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
            return GrantAll(issuingPlayer);

        var resolved = ResolveCurse(args[0]);
        if (resolved == null)
            return new CmdResult(success: false, $"Unknown curse '{args[0]}'. Try: {CurseNames()}");
        var (key, display, effect) = resolved.Value;

        RelicModel? canonical = args.Length >= 2
            ? GetRelicById(args[1])
            : (GetRelicById("anchor") ?? ModelDb.AllRelics.FirstOrDefault());
        if (canonical == null)
            return new CmdResult(success: false, "No relic to grant (specify a relic-id).");

        RelicModel relic = canonical.ToMutable();
        var rs = issuingPlayer.RunState;
        // Force a strong beneficial prefix so the relic is a normal (non-penalty) host for the curse.
        RelicForgeService.Forge(relic, rs.Rng.Seed, rs.TotalFloor, PrefixTable.ByName("Legendary"));

        var rec = RelicForgeService.RecordFor(relic);
        // Forge may have rolled its OWN curse (enemy-rider) — clear it so the forced curse is the only
        // one, matching the mutual-exclusivity of real play. The key is an on-hit self-curse OR a re-homed
        // penalty identity; both dispatch off rec.SelfCurse (their own patches).
        if (rec != null) { rec.SelfCurse = key; rec.EnemyRider = false; rec.EnemyRiderSuffix = ""; }

        return new CmdResult(
            RelicCmd.Obtain(relic, issuingPlayer),
            success: true,
            $"Granted {relic.Id.Entry} cursed with '{display}' — {effect}.");
    }

    /// <summary>Every forgeable curse identity: the on-hit self-curses PLUS the re-homed penalty affixes
    /// (Cursed/…/Bankrupt), all stored in rec.SelfCurse. Character penalties can be forced onto any relic
    /// for testing (their patches dispatch on the curse key, not the character).</summary>
    private static IEnumerable<string> AllCurseKeys()
        => SelfCurseTable.All.Select(c => c.En)
           .Concat(PrefixTable.All.Where(p => p.Penalty && !p.IsFallback).Select(p => p.Name));

    /// <summary>Resolve a curse name to (stored key, display, effect line): first an on-hit self-curse,
    /// else a re-homed penalty prefix (its note is the effect line).</summary>
    private static (string key, string display, string effect)? ResolveCurse(string name)
    {
        var def = SelfCurseTable.All.FirstOrDefault(c => string.Equals(c.En, name, StringComparison.OrdinalIgnoreCase));
        if (def != null) return (def.En, def.Display, def.Effect);
        var pfx = PrefixTable.All.FirstOrDefault(p => p.Penalty && !p.IsFallback && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (pfx != null) return (pfx.Name, pfx.Name, pfx.NoteDisplay);
        return null;
    }

    /// <summary>Grant one DISTINCT relic per self-curse, each stamped with that curse — tests them all at once.</summary>
    private static CmdResult GrantAll(Player player)
    {
        var owned = new HashSet<string>(player.Relics.Select(r => r.Id.Entry));
        var pool = ModelDb.AllRelics.Where(r => !owned.Contains(r.Id.Entry)).ToList();
        var rs = player.RunState;
        var granted = new List<string>();
        int i = 0;
        foreach (var key in AllCurseKeys())
        {
            if (i >= pool.Count) break;
            RelicModel relic = pool[i++].ToMutable();
            RelicForgeService.Forge(relic, rs.Rng.Seed, rs.TotalFloor, PrefixTable.ByName("Legendary"));
            var rec = RelicForgeService.RecordFor(relic);
            if (rec != null) { rec.SelfCurse = key; rec.EnemyRider = false; rec.EnemyRiderSuffix = ""; }
            TaskHelper.RunSafely(RelicCmd.Obtain(relic, player));
            granted.Add($"{relic.Id.Entry}=☠{key}");
        }
        return new CmdResult(success: true, $"Granted {granted.Count} cursed relics (one per curse):\n  " + string.Join("\n  ", granted));
    }

    private static string CurseNames() => string.Join(", ", AllCurseKeys());

    private static RelicModel? GetRelicById(string id)
    {
        id = id.ToUpperInvariant();
        var matches = ModelDb.AllRelics.Where(r => r.Id.Entry.ToUpperInvariant().Contains(id)).ToList();
        return matches.FirstOrDefault(r => r.Id.Entry.ToUpperInvariant() == id)
            ?? matches.FirstOrDefault(r => r.Id.Entry.ToUpperInvariant().StartsWith(id))
            ?? matches.FirstOrDefault();
    }

    public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
    {
        if (args.Length <= 1)
        {
            string partial = args.Length == 1 ? args[0] : string.Empty;
            var candidates = new List<string> { "all" };
            candidates.AddRange(AllCurseKeys());
            return CompleteArgument(candidates, Array.Empty<string>(), partial, CompletionType.Argument,
                (candidate, p) => candidate.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }
        if (args.Length == 2)
        {
            var relics = ModelDb.AllRelics.Select(r => r.Id.Entry).ToList();
            return CompleteArgument(relics, Array.Empty<string>(), args[1], CompletionType.Argument,
                (candidate, p) => candidate.Contains(p, StringComparison.OrdinalIgnoreCase));
        }
        return new CompletionResult { Type = CompletionType.Argument, ArgumentContext = CmdName };
    }
}

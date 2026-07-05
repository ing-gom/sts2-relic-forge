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
        if (issuingPlayer == null)
            return new CmdResult(success: false, "No active player — start a run first.");
        if (args.Length < 1)
            return new CmdResult(success: false, $"Usage: forgeselfcurse <curse|all> [relic-id]. Curses: {CurseNames()}");

        if (args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
            return GrantAll(issuingPlayer);

        var def = SelfCurseTable.All.FirstOrDefault(c => string.Equals(c.En, args[0], StringComparison.OrdinalIgnoreCase));
        if (def == null)
            return new CmdResult(success: false, $"Unknown self-curse '{args[0]}'. Try: {CurseNames()}");

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
        // Forge may have rolled its OWN curse (enemy-rider) — clear it so the forced self-curse is the
        // only curse, matching the mutual-exclusivity of real play.
        if (rec != null) { rec.SelfCurse = def.En; rec.EnemyRider = false; rec.EnemyRiderSuffix = ""; }

        return new CmdResult(
            RelicCmd.Obtain(relic, issuingPlayer),
            success: true,
            $"Granted {relic.Id.Entry} self-cursed with '{def.Display}' — {def.Effect}.");
    }

    /// <summary>Grant one DISTINCT relic per self-curse, each stamped with that curse — tests them all at once.</summary>
    private static CmdResult GrantAll(Player player)
    {
        var owned = new HashSet<string>(player.Relics.Select(r => r.Id.Entry));
        var pool = ModelDb.AllRelics.Where(r => !owned.Contains(r.Id.Entry)).ToList();
        var rs = player.RunState;
        var granted = new List<string>();
        int i = 0;
        foreach (var def in SelfCurseTable.All)
        {
            if (i >= pool.Count) break;
            RelicModel relic = pool[i++].ToMutable();
            RelicForgeService.Forge(relic, rs.Rng.Seed, rs.TotalFloor, PrefixTable.ByName("Legendary"));
            var rec = RelicForgeService.RecordFor(relic);
            if (rec != null) { rec.SelfCurse = def.En; rec.EnemyRider = false; rec.EnemyRiderSuffix = ""; }
            TaskHelper.RunSafely(RelicCmd.Obtain(relic, player));
            granted.Add($"{relic.Id.Entry}=☠{def.Display}");
        }
        return new CmdResult(success: true, $"Granted {granted.Count} self-cursed relics (one per curse):\n  " + string.Join("\n  ", granted));
    }

    private static string CurseNames() => string.Join(", ", SelfCurseTable.All.Select(c => c.En));

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
            candidates.AddRange(SelfCurseTable.All.Select(c => c.En));
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

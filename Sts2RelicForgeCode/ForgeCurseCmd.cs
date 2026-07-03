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
/// Dev-console command <c>forgecurse &lt;suffix&gt; [relic-id]</c> — grants a relic that carries a
/// FORCED enemy-rider curse suffix, so each suffix's enemy buff can be tested without waiting on the
/// random rider roll. Forges the relic with a strong beneficial prefix, then stamps the chosen
/// suffix on its record. Owning it makes elites/bosses gain the mapped buff (see EnemyForge).
///
/// Auto-registered by DevConsole reflection over AbstractConsoleCmd. See ForgeConsoleCmd.
/// </summary>
public class ForgeCurseCmd : AbstractConsoleCmd
{
    public override string CmdName => "forgecurse";
    public override string Args => "<suffix|all> [relic-id]";
    public override string Description =>
        "Grants a relic cursed with a chosen enemy-rider suffix; 'all' grants one relic per suffix (test them all at once).";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (issuingPlayer == null)
            return new CmdResult(success: false, "No active player — start a run first.");
        if (args.Length < 1)
            return new CmdResult(success: false, $"Usage: forgecurse <suffix|all> [relic-id]. Suffixes: {SuffixNames()}");

        if (args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
            return GrantAll(issuingPlayer);

        var sfx = RiderSuffix.Find(args[0]);
        if (sfx == null)
            return new CmdResult(success: false, $"Unknown suffix '{args[0]}'. Try: {SuffixNames()}");

        RelicModel? canonical = args.Length >= 2
            ? GetRelicById(args[1])
            : (GetRelicById("anchor") ?? ModelDb.AllRelics.FirstOrDefault());
        if (canonical == null)
            return new CmdResult(success: false, "No relic to grant (specify a relic-id).");

        RelicModel relic = canonical.ToMutable();
        var rs = issuingPlayer.RunState;
        // Force a strong beneficial prefix so the rider is valid (penalty prefixes never carry it).
        RelicForgeService.Forge(relic, rs.Rng.Seed, rs.TotalFloor, PrefixTable.ByName("Legendary"));

        var rec = RelicForgeService.RecordFor(relic);
        if (rec != null)
        {
            rec.EnemyRider = true;
            rec.EnemyRiderSuffix = sfx.En;
        }

        return new CmdResult(
            RelicCmd.Obtain(relic, issuingPlayer),
            success: true,
            $"Granted {relic.Id.Entry} cursed with '{sfx.Display}' — {sfx.Effect}.");
    }

    /// <summary>Grant one DISTINCT relic per suffix, each stamped with that suffix — tests them all at once.</summary>
    private static CmdResult GrantAll(Player player)
    {
        var owned = new HashSet<string>(player.Relics.Select(r => r.Id.Entry));
        var pool = ModelDb.AllRelics.Where(r => !owned.Contains(r.Id.Entry)).ToList();
        var rs = player.RunState;
        var granted = new List<string>();
        int i = 0;
        foreach (var sfx in RiderSuffix.All)
        {
            if (i >= pool.Count) break;
            RelicModel relic = pool[i++].ToMutable();
            RelicForgeService.Forge(relic, rs.Rng.Seed, rs.TotalFloor, PrefixTable.ByName("Legendary"));
            var rec = RelicForgeService.RecordFor(relic);
            if (rec != null) { rec.EnemyRider = true; rec.EnemyRiderSuffix = sfx.En; }
            TaskHelper.RunSafely(RelicCmd.Obtain(relic, player));
            granted.Add($"{relic.Id.Entry}=〈{sfx.Display}〉");
        }
        return new CmdResult(success: true, $"Granted {granted.Count} cursed relics (one per suffix):\n  " + string.Join("\n  ", granted));
    }

    private static string SuffixNames() => string.Join(", ", RiderSuffix.All.Select(s => s.En));

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
            candidates.AddRange(RiderSuffix.All.Select(s => s.En));
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

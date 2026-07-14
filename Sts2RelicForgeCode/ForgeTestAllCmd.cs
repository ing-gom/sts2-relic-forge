using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Dev-console command <c>forgetest</c> — bulk-grants ONE relic per companion prefix
/// (Thorned, Mighty, Quicksilver, …) so every grafted effect can be re-tested in one shot
/// after a restart (console-granted relics don't persist). Each prefix lands on a DISTINCT
/// benign host so the icons are tell-apart and all effects stack in a single combat.
///
/// Auto-registered by DevConsole reflection over AbstractConsoleCmd. See ForgeConsoleCmd.
/// </summary>
public class ForgeTestAllCmd : AbstractConsoleCmd
{
    public override string CmdName => "forgetest";
    public override string Args => "[penalty|delayed|graft|reactive]";
    public override string Description =>
        "Grants one relic per companion prefix for bulk re-testing. Optional filter: penalty | delayed | graft | reactive.";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    // Hosts to avoid: bespoke-reward relics (LostCoffer/NeowsTalisman rewrite their own
    // tooltip) and the card-select relics that hard-cap at 3 (HeftyTablet/Toolbox) — none
    // make clean test hosts.
    private static readonly HashSet<string> SkipHosts = new()
    {
        "LostCoffer", "NeowsTalisman", "HeftyTablet", "Toolbox"
    };

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (LocalCmdGuard.BlockInRealCoop() is { } blocked) return blocked;   // local-only mutation = desync in real co-op
        if (issuingPlayer == null)
            return new CmdResult(success: false, "No active player — start a run first.");

        // Optional filter: grant only one sub-category (e.g. the newly-added penalties).
        string filter = args.Length >= 1 ? args[0].ToLowerInvariant() : "";
        Func<Prefix, bool> pred = filter switch
        {
            "penalty" => p => p.Penalty,
            "delayed" => p => p.DelayTurn > 0,
            "graft" => p => p.CompanionRelic != null,
            "reactive" => p => p.GainAmplify || p.LossInvert || p.EnergyDischarge > 0,
            _ => p => p.IsCompanionPrefix,
        };
        var companions = PrefixTable.All.Where(pred).ToList();
        if (companions.Count == 0)
            return new CmdResult(success: false, "No matching prefixes (filter: penalty|delayed|graft|reactive).");

        // Distinct eligible non-donor hosts — one per prefix. Deterministic order.
        var donorTypes = companions.Where(p => p.CompanionRelic != null).Select(p => p.CompanionRelic!).ToHashSet();
        var hosts = ModelDb.AllRelics
            .Where(r => PrefixTable.Eligible.Contains(r.Rarity)
                        && !donorTypes.Contains(r.GetType())
                        && !SkipHosts.Contains(r.GetType().Name))
            .OrderBy(r => r.Id.Entry)
            .Take(companions.Count)
            .ToList();
        if (hosts.Count < companions.Count)
            return new CmdResult(success: false,
                $"Only {hosts.Count} eligible hosts for {companions.Count} prefixes.");

        var runState = issuingPlayer.RunState;
        uint seed = runState.Rng.Seed;
        int floor = runState.TotalFloor;

        var plan = new List<(RelicModel host, Prefix pfx)>();
        for (int i = 0; i < companions.Count; i++)
            plan.Add((hosts[i].ToMutable(), companions[i]));

        return new CmdResult(GrantAll(issuingPlayer, plan, seed, floor), success: true,
            $"Granting {plan.Count} test relics: {string.Join(", ", companions.Select(p => p.Name))}.");
    }

    // Forge each host with its forced prefix, then Obtain it. Sequential await so the
    // per-relic grant + companion graft finish cleanly before the next.
    private static async Task GrantAll(Player player, List<(RelicModel host, Prefix pfx)> plan, uint seed, int floor)
    {
        foreach (var (host, pfx) in plan)
        {
            RelicForgeService.Forge(host, seed, floor, pfx);
            await RelicCmd.Obtain(host, player);
        }
    }

    public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
        => new CompletionResult { Type = CompletionType.Argument, ArgumentContext = CmdName };
}

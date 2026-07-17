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
/// Dev-console command <c>forgechar [character]</c> — bulk-grants a character's gated prefixes
/// (effects + curses) onto distinct benign hosts so they can all be tested together in one
/// combat. No argument uses YOUR current character (the common case: start a run, type
/// <c>forgechar</c>, get your test relics); or pass
/// <c>ironclad | silent | defect | necrobinder | regent</c> for a specific one, or <c>all</c>
/// for every character-gated prefix.
///
/// Forcing bypasses the character roll gate, so you can preview any character's prefixes; note the
/// EFFECTS only fire on that character's mechanic (e.g. Silent's poison prefix does nothing on a
/// Defect run), so grant your OWN character's set to actually see them trigger.
///
/// Auto-registered by DevConsole reflection over AbstractConsoleCmd. See ForgeTestAllCmd.
/// </summary>
public class ForgeCharTestCmd : AbstractConsoleCmd
{
    public override string CmdName => "forgechar";
    public override string Args => "[character|all]";
    public override string Description =>
        "Grants one relic per character-gated prefix (effects + curses). No arg = your current character; or ironclad|silent|defect|necrobinder|regent|all.";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    // Same problematic hosts ForgeTestAllCmd skips: bespoke-reward relics that rewrite their own
    // tooltip, and the card-select relics that hard-cap the picker — none make clean test hosts.
    private static readonly HashSet<string> SkipHosts = new()
    {
        "LostCoffer", "NeowsTalisman", "HeftyTablet", "Toolbox"
    };

    private static readonly string[] KnownChars = { "ironclad", "silent", "defect", "necrobinder", "regent", "all" };

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (LocalCmdGuard.BlockInRealCoop() is { } blocked) return blocked;   // local-only mutation = desync in real co-op
        if (issuingPlayer == null)
            return new CmdResult(success: false, "No active player — start a run first.");

        // No arg → your current character; "all" → every character-gated prefix; else the named one.
        string arg = args.Length >= 1 ? args[0].Trim().ToUpperInvariant() : "";
        string? charFilter;
        if (arg.Length == 0)
        {
            charFilter = CharAffix.TitleOf(issuingPlayer);
            if (string.IsNullOrEmpty(charFilter))
                return new CmdResult(success: false,
                    "Couldn't detect your character — pass one: ironclad|silent|defect|necrobinder|regent|all.");
        }
        else if (arg == "ALL") charFilter = null;
        else charFilter = arg;

        var prefixes = PrefixTable.All
            .Where(p => p.CharAffix && (charFilter == null
                || string.Equals(p.RequiredCharacter, charFilter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (prefixes.Count == 0)
            return new CmdResult(success: false,
                $"No character prefixes for '{(charFilter ?? "all").ToLowerInvariant()}'. Try ironclad|silent|defect|necrobinder|regent|all.");

        // Distinct eligible hosts — one per prefix — so the icons are tell-apart and all four
        // effects stack in a single combat. Deterministic order.
        var hosts = ModelDb.AllRelics
            .Where(r => PrefixTable.Eligible.Contains(r.Rarity) && !SkipHosts.Contains(r.GetType().Name))
            .OrderBy(r => r.Id.Entry)
            .Take(prefixes.Count)
            .ToList();
        if (hosts.Count < prefixes.Count)
            return new CmdResult(success: false, $"Only {hosts.Count} eligible hosts for {prefixes.Count} prefixes.");

        var runState = issuingPlayer.RunState;
        uint seed = runState.Rng.Seed;
        int floor = runState.TotalFloor;

        var plan = new List<(RelicModel host, Prefix pfx)>();
        for (int i = 0; i < prefixes.Count; i++)
            plan.Add((hosts[i].ToMutable(), prefixes[i]));

        return new CmdResult(GrantAll(issuingPlayer, plan, seed, floor), success: true,
            $"Granting {plan.Count} relics ({(charFilter ?? "all").ToLowerInvariant()}): {string.Join(", ", prefixes.Select(p => p.Name))}.");
    }

    // Forge each host with its forced prefix (bypasses the char gate + rarity gate), then Obtain it.
    // Sequential await so each grant finishes cleanly before the next.
    private static async Task GrantAll(Player player, List<(RelicModel host, Prefix pfx)> plan, uint seed, int floor)
    {
        foreach (var (host, pfx) in plan)
        {
            RelicForgeService.Forge(host, seed, floor, pfx);
            await RelicCmd.Obtain(host, player);
        }
    }

    public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
    {
        if (args.Length <= 1)
        {
            string partial = args.Length == 1 ? args[0] : string.Empty;
            return CompleteArgument(KnownChars.ToList(), Array.Empty<string>(), partial, CompletionType.Argument,
                (candidate, p) => candidate.Contains(p, StringComparison.OrdinalIgnoreCase));
        }
        return new CompletionResult { Type = CompletionType.Argument, ArgumentContext = CmdName };
    }
}

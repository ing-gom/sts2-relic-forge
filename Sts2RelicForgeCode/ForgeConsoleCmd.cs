using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Dev-console command <c>forge &lt;relic-id&gt; [percent]</c> — grants a relic forged and
/// added to the local player so the enhanced numbers + prefix are visible immediately.
/// With no percent it uses the normal seed-deterministic grade for that relic; with a
/// percent (e.g. <c>forge anchor 100</c>) it forces +100% and bypasses the rarity gate so
/// any relic can be inspected.
///
/// Auto-registered by the game's DevConsole reflection over
/// GetSubtypesInMods&lt;AbstractConsoleCmd&gt;(). Opened with the backtick key when modded.
/// See [[project_sts2_relic_forge]].
/// </summary>
public class ForgeConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "forge";
    public override string Args => "<relic-id> [prefix]";
    public override string Description =>
        "Grants a relic forged (rolled prefix, or a forced Terraria prefix like Legendary) to test Relic Forge.";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (LocalCmdGuard.BlockInRealCoop() is { } blocked) return blocked;   // local-only mutation = desync in real co-op
        if (issuingPlayer == null)
            return new CmdResult(success: false, "No active player — start a run first.");
        if (args.Length < 1)
            return new CmdResult(success: false, "Usage: forge <relic-id> [prefix]");

        Prefix? forced = null;
        if (args.Length >= 2)
        {
            forced = PrefixTable.ByName(args[1]);
            if (forced == null)
                return new CmdResult(success: false, $"Unknown prefix '{args[1]}' (e.g. Legendary, Godly, Broken).");
        }

        RelicModel? canonical = GetRelicById(args[0]);
        if (canonical == null)
            return new CmdResult(success: false, $"Unable to find relic '{args[0]}'.");

        RelicModel relic = canonical.ToMutable();
        var runState = issuingPlayer.RunState;
        string? summary = RelicForgeService.Forge(relic, runState.Rng.Seed, runState.TotalFloor, forced);
        MainFile.Logger.Info($"[{MainFile.ModId}] forge cmd: {summary ?? (relic.Id.Entry + " (no numeric vars changed)")}");

        // Prefix on Obtain will see this instance is already forged and skip re-rolling.
        return new CmdResult(
            RelicCmd.Obtain(relic, issuingPlayer),
            success: true,
            summary ?? $"Granted {relic.Id.Entry} (no enhanceable vars).");
    }

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
            var candidates = ModelDb.AllRelics.Select(r => r.Id.Entry).ToList();
            string partial = args.Length == 1 ? args[0] : string.Empty;
            return CompleteArgument(candidates, Array.Empty<string>(), partial, CompletionType.Argument,
                (candidate, p) => candidate.Contains(p, StringComparison.OrdinalIgnoreCase));
        }
        return new CompletionResult { Type = CompletionType.Argument, ArgumentContext = CmdName };
    }
}

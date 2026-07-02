using System;
using System.Linq;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Dev-console command <c>reforge &lt;relic-id&gt;</c> — re-rolls the prefix of a relic the local
/// player ALREADY owns (as opposed to <c>forge</c>, which grants a new one). Bumps that relic's
/// reforge count and applies a fresh, deterministic-given-the-count grade. Used to verify the
/// re-roll + save/load persistence before any real (cost-gated) UI is wired up.
///
/// Auto-registered by the game's DevConsole reflection over GetSubtypesInMods&lt;
/// AbstractConsoleCmd&gt;(). Opened with the backtick key when modded. See [[project_sts2_relic_forge]].
/// </summary>
public class ReforgeConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "reforge";
    public override string Args => "<relic-id>";
    public override string Description =>
        "Re-rolls the prefix of a relic you already own (bumps its reforge count).";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (issuingPlayer == null)
            return new CmdResult(success: false, "No active player — start a run first.");
        if (args.Length < 1)
            return new CmdResult(success: false, "Usage: reforge <relic-id>");

        RelicModel? relic = FindOwnedRelic(issuingPlayer, args[0]);
        if (relic == null)
            return new CmdResult(success: false, $"You don't own a relic matching '{args[0]}'.");

        // Any owned relic can be reforged (even one with no prefix or an ineligible rarity).
        var outcome = RelicForgeService.Reforge(relic, issuingPlayer);
        relic.Flash();
        return new CmdResult(success: true,
            outcome == RelicForgeService.ReforgeOutcome.RolledPenalty
                ? $"Re-forged {relic.Id.Entry} — rolled a PENALTY prefix (campfire reforge would end)."
                : $"Re-forged {relic.Id.Entry}.");
    }

    // Search only the player's owned relics; prefer exact id, then prefix, then substring. Skip
    // hidden companions (grafted donors) — those aren't player-chosen and can't be re-rolled.
    private static RelicModel? FindOwnedRelic(Player player, string id)
    {
        id = id.ToUpperInvariant();
        var owned = player.Relics.Where(r => !RelicForgeService.IsCompanion(r)).ToList();
        return owned.FirstOrDefault(r => r.Id.Entry.ToUpperInvariant() == id)
            ?? owned.FirstOrDefault(r => r.Id.Entry.ToUpperInvariant().StartsWith(id))
            ?? owned.FirstOrDefault(r => r.Id.Entry.ToUpperInvariant().Contains(id));
    }

    public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
    {
        if (player != null && args.Length <= 1)
        {
            var candidates = player.Relics
                .Where(r => !RelicForgeService.IsCompanion(r))
                .Select(r => r.Id.Entry).ToList();
            string partial = args.Length == 1 ? args[0] : string.Empty;
            return CompleteArgument(candidates, Array.Empty<string>(), partial, CompletionType.Argument,
                (candidate, p) => candidate.Contains(p, StringComparison.OrdinalIgnoreCase));
        }
        return new CompletionResult { Type = CompletionType.Argument, ArgumentContext = CmdName };
    }
}

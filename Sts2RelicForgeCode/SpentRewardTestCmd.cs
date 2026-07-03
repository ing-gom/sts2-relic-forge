using System;
using System.Linq;
using System.Text;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Dev-console command <c>spenttest [relic-id]</c> — verifies the "already granted" footnote on a
/// ONE-TIME reward relic (LostCoffer / NeowsTalisman). These dispense their bonus once, at
/// AfterObtained; reforging re-rolls the prefix but can't hand the reward out again, so the tooltip
/// must stop advertising the (now uncollectable) numbers once the relic is owned AND re-forged.
///
/// It reads the relic's tooltip before and after a reforge and asserts the rule
/// (note present ⇔ owned &amp;&amp; ReforgeCount &gt; 0). Operates on an ALREADY-OWNED one-time relic
/// (the starter NeowsTalisman is ideal — no reward screen, no deck changes beyond the reforge). If
/// none is owned it grants a forged one and asks you to re-run (obtaining fires the real one-time
/// effect, which is what makes it "spent").
///
/// Auto-registered by DevConsole reflection over AbstractConsoleCmd. See ForgeConsoleCmd.
/// </summary>
public class SpentRewardTestCmd : AbstractConsoleCmd
{
    public override string CmdName => "spenttest";
    public override string Args => "[relic-id]";
    public override string Description =>
        "Verifies the 'already granted' tooltip note on a reforged one-time reward relic (LostCoffer/NeowsTalisman).";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (issuingPlayer == null)
            return new CmdResult(success: false, "No active player — start a run first.");

        // Pick the relic to test: an explicit id (must be a one-time reward relic), else the first
        // owned one-time reward relic (NeowsTalisman/LostCoffer).
        RelicModel? relic = args.Length >= 1
            ? FindOwnedOneTime(issuingPlayer, args[0])
            : issuingPlayer.Relics.FirstOrDefault(IsTestableOneTime);

        if (relic == null)
        {
            // Nothing owned to test — grant a forged NeowsTalisman (clean: no reward screen) so a
            // re-run finds an owned, already-fired one-time relic. Obtaining fires its one-time
            // effect, which is exactly the "spent" state the note is about.
            RelicModel? canonical = ModelDb.AllRelics.FirstOrDefault(r => r.GetType().Name == "NeowsTalisman");
            if (canonical == null)
                return new CmdResult(success: false,
                    "No owned one-time reward relic, and NeowsTalisman isn't in ModelDb. Try `forge lost_coffer` then `spenttest lost_coffer`.");
            RelicModel granted = canonical.ToMutable();
            var rs = issuingPlayer.RunState;
            RelicForgeService.Forge(granted, rs.Rng.Seed, rs.TotalFloor, PrefixTable.ByName("Legendary"));
            return new CmdResult(RelicCmd.Obtain(granted, issuingPlayer), success: true,
                "Granted a forged NeowsTalisman (its one-time effect fired). Run `spenttest` again to verify the reforge note.");
        }

        return new CmdResult(success: true, RunChecks(relic, issuingPlayer));
    }

    // The whole check is synchronous: reforge mutates in place and the tooltip getter rebuilds
    // from the live record, so we can read → reforge → read without awaiting anything.
    private static string RunChecks(RelicModel relic, Player player)
    {
        var log = new StringBuilder();
        string id = relic.Id.Entry;

        int countBefore = RelicForgeService.ReforgeCountOf(relic);
        bool ownedBefore = player.Relics.Contains(relic);
        string descBefore = SafeDesc(relic);
        bool noteBefore = descBefore.Contains(BespokeBonus.SpentMarker);
        // Rule: the footnote appears iff the reward is already spent AND the shown bonus is stale
        // (owned && re-forged). A never-reforged owned relic still matches what was granted.
        bool expectBefore = ownedBefore && countBefore > 0;
        bool passBefore = noteBefore == expectBefore;

        RelicForgeService.Reforge(relic, player);
        relic.Flash();

        int countAfter = RelicForgeService.ReforgeCountOf(relic);
        bool ownedAfter = player.Relics.Contains(relic);
        string descAfter = SafeDesc(relic);
        bool noteAfter = descAfter.Contains(BespokeBonus.SpentMarker);
        bool expectAfter = ownedAfter && countAfter > 0; // reforge bumps the count, so we expect the note
        bool passAfter = noteAfter == expectAfter;

        // Full tooltip bodies go to the log for eyeballing (they're long, with BBCode markup).
        MainFile.Logger.Info($"[{MainFile.ModId}] spenttest {id} BEFORE (count {countBefore}):\n{descBefore}");
        MainFile.Logger.Info($"[{MainFile.ModId}] spenttest {id} AFTER  (count {countAfter}):\n{descAfter}");

        log.Append("spenttest ").Append(id).Append(":\n");
        log.Append(Line(passBefore, $"before reforge (owned={ownedBefore}, count={countBefore}): note {(noteBefore ? "shown" : "absent")}, expected {(expectBefore ? "shown" : "absent")}"));
        log.Append('\n');
        log.Append(Line(passAfter, $"after  reforge (owned={ownedAfter}, count={countAfter}): note {(noteAfter ? "shown" : "absent")}, expected {(expectAfter ? "shown" : "absent")}"));
        log.Append('\n');
        log.Append(passBefore && passAfter ? "RESULT: PASS" : "RESULT: FAIL (see log for full tooltip text)");
        return log.ToString();
    }

    private static string Line(bool pass, string msg) => (pass ? "  [PASS] " : "  [FAIL] ") + msg;

    private static string SafeDesc(RelicModel relic)
    {
        try { return relic.HoverTip.Description ?? ""; }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] spenttest tooltip read failed: {e.Message}");
            return "";
        }
    }

    // Owned, not a hidden companion, and a bespoke one-time reward relic.
    private static bool IsTestableOneTime(RelicModel r)
        => !RelicForgeService.IsCompanion(r) && BespokeBonus.IsOneTimeReward(r.GetType().Name);

    private static RelicModel? FindOwnedOneTime(Player player, string id)
    {
        id = id.ToUpperInvariant();
        var owned = player.Relics.Where(IsTestableOneTime).ToList();
        return owned.FirstOrDefault(r => r.Id.Entry.ToUpperInvariant() == id)
            ?? owned.FirstOrDefault(r => r.Id.Entry.ToUpperInvariant().StartsWith(id))
            ?? owned.FirstOrDefault(r => r.Id.Entry.ToUpperInvariant().Contains(id));
    }

    public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
    {
        if (player != null && args.Length <= 1)
        {
            var candidates = player.Relics.Where(IsTestableOneTime).Select(r => r.Id.Entry).ToList();
            string partial = args.Length == 1 ? args[0] : string.Empty;
            return CompleteArgument(candidates, Array.Empty<string>(), partial, CompletionType.Argument,
                (candidate, p) => candidate.Contains(p, StringComparison.OrdinalIgnoreCase));
        }
        return new CompletionResult { Type = CompletionType.Argument, ArgumentContext = CmdName };
    }
}

using System.Globalization;
using MegaCrit.Sts2.Core.Context;              // LocalContext
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;                 // RunManager

namespace Sts2RelicForge;

/// <summary>
/// SYMMETRIC co-op safe mode for a sister-mod registration mismatch. The prefix/curse pools are part
/// of the sim: <c>PrefixTable.Roll</c> walks the pool in order, so peers with DIFFERENT registered
/// sets pick different prefixes from the same seeded roll — and no one-sided mitigation can converge
/// that (e.g. a client that skips its local roll still diverges on the HOST-side companion grafts,
/// which are hashed via the relic list — the replica-wipe lesson). The only convergent behavior is
/// for EVERY peer to make the same decision: when any two peers' pool fingerprints differ, ALL peers
/// trip into safe mode — the forge deactivates for the session (no pickup rolls, no reforge UI, no
/// restores), leaving pure vanilla behavior that trivially converges — plus a loud ERROR naming the
/// root cause, instead of a mystery black screen.
///
/// Symmetry mechanics: every peer announces its own fingerprint once per run via the networked
/// <c>rf_fp</c> command (replayed on all peers), and the host's fingerprint additionally rides
/// <c>rf_config</c> (v1.0.10). Each peer compares every received fingerprint against its own — the
/// mismatch is therefore observed BY ALL PEERS, and all trip together. Order-only differences no
/// longer exist (external registrations are name-sorted into the pool), so a trip means a genuinely
/// different sister-mod SET.
/// </summary>
internal static class ForgeSafeMode
{
    /// <summary>True once a pool mismatch has been observed this session — the forge is inert.</summary>
    public static bool Active { get; private set; }

    public static void Trip(string reason)
    {
        if (Active) return;
        Active = true;
        MainFile.Logger.Error($"[{MainFile.ModId}] ★★ FORGE SAFE MODE — sister-mod prefix/curse registration " +
            $"differs between co-op peers ({reason}). The forge is DISABLED for this session on every peer " +
            "(pure vanilla relics) to prevent a guaranteed desync. Fix: every player must run the same " +
            "RelicForge sister mods, then restart the session.");
    }

    /// <summary>TEST ONLY — lets the self-test battery verify the gates and then restore normal mode.</summary>
    internal static void ResetForTest() => Active = false;

    // --- once-per-run announce -------------------------------------------------------------------

    private static uint _announcedForSeed;

    /// <summary>Announce THIS peer's pool fingerprint to every peer (networked), once per run. Called
    /// from the per-room hook on every peer (unlike the host-only config broadcast) so the HOST also
    /// learns a client's mismatch — that is what makes the trip symmetric.</summary>
    public static void AnnounceOncePerRun()
    {
        var run = RunManager.Instance;
        var state = run?.State;
        if (state == null || run!.IsSingleplayerOrFakeMultiplayer) return;   // co-op only
        uint seed = state.Players.Count > 0 ? state.Players[0].RunState.Rng.Seed : 0u;
        if (seed == 0 || seed == _announcedForSeed) return;
        var me = LocalContext.GetMe(state.Players);
        if (me == null) return;                                               // later room re-triggers
        _announcedForSeed = seed;
        string cmd = string.Format(CultureInfo.InvariantCulture, "{0} {1}",
            RelicForgeFpCmd.Verb, RelicForgeConfigSyncCmd.PoolFingerprint());
        run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, cmd, inCombat: false));
    }
}

/// <summary>
/// NETWORKED fingerprint announce (see <see cref="ForgeSafeMode"/>). Every peer replays every
/// announce; a received fingerprint that differs from the LOCAL one trips safe mode on this peer —
/// since the comparison runs everywhere with the same pair of values, all peers trip together.
/// Same ConsoleCmdGameAction reuse as rf_config/rf_counts (no new INetAction type-id).
/// </summary>
public sealed class RelicForgeFpCmd : AbstractConsoleCmd
{
    public const string Verb = "rf_fp";

    public override string CmdName => Verb;
    public override string Args => "<fingerprint>";
    public override string Description =>
        "Internal (networked): peers exchange prefix/curse pool fingerprints to detect sister-mod mismatches.";
    public override bool IsNetworked => true;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (args.Length < 1) return new CmdResult(success: false, "rf_fp: missing fingerprint");
        string local = RelicForgeConfigSyncCmd.PoolFingerprint();
        if (args[0] != local)
            ForgeSafeMode.Trip($"peer {(issuingPlayer?.NetId.ToString() ?? "?")} '{args[0]}' vs local '{local}'");
        return new CmdResult(success: true, "rf_fp ok");
    }
}

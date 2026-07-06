using System.Linq;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Multiplayer seam for reforge (campfire + shop). See MULTIPLAYER_REFORGE.md.
///
/// A raw reforge mutates relic state LOCALLY (<see cref="RelicForgeService.Reforge"/>), so doing it
/// unsynchronized in co-op would desync the peers' replicated copies. Instead — mirroring how the
/// game's own card upgrade / card removal replicate — reforge rides a synchronized command that runs
/// on EVERY client, and each client re-derives the identical result. This mod's forge is already
/// seed-deterministic (seed+id+floor+count), so nothing about the result needs transmitting: the
/// command carries only (relicEntry, newCount) and every client converges via
/// <see cref="ApplyReforgeStepOnClient"/>.
///
/// This centralizes that decision behind one seam so the two reforge call sites and the two
/// availability gates route through it. Single-player takes the local fast path; co-op takes the
/// networked path (<see cref="DispatchNetworked"/>, wired via the built-in console-command net action
/// — see <see cref="ReforgeNetConsoleCmd"/>). <see cref="TransportReady"/> is the master switch.
/// </summary>
internal static class ReforgeNet
{
    /// <summary>
    /// True now that <see cref="DispatchNetworked"/> is wired against the game's synchronized action
    /// queue (see <see cref="ReforgeNetConsoleCmd"/>): reforge replicates to every co-op client via a
    /// networked command, so the reforge UIs are offered in co-op too. Kept as a <c>static readonly</c>
    /// (not <c>const</c>) so the single-player fast paths below compile without unreachable-code warnings.
    /// </summary>
    internal static readonly bool TransportReady = true;

    /// <summary>
    /// Whether the reforge UI (campfire option / shop button) may be offered right now.
    /// Single-player or fake-multiplayer: always (purely local, no sync needed). Real co-op: only
    /// once the synchronized dispatch below is wired, so we never expose a control that desyncs.
    /// </summary>
    public static bool Available()
    {
        var run = RunManager.Instance;
        if (run == null) return false;
        if (run.IsSingleplayerOrFakeMultiplayer) return true;
        return TransportReady;
    }

    /// <summary>
    /// Perform a reforge so that EVERY client converges on the same result. Single-player (or
    /// fake-MP): apply locally, unchanged historic behavior. Real co-op: broadcast (relic, newCount)
    /// via a synchronized command; each client — including the initiator — re-derives deterministically
    /// in <see cref="ApplyReforgeStepOnClient"/>. Returns the outcome for the caller UI (campfire uses
    /// it to end its free reforge on a penalty roll).
    /// </summary>
    public static RelicForgeService.ReforgeOutcome Reforge(RelicModel relic, Player player)
    {
        var run = RunManager.Instance;
        bool coop = run != null && !run.IsSingleplayerOrFakeMultiplayer;

        if (coop && TransportReady)
        {
            int nextCount = RelicForgeService.ReforgeCountOf(relic) + 1;
            // The synced handler mutates ALL copies (including this client's) uniformly, so we must
            // NOT also mutate locally here — that would double-apply on the initiator. The outcome is
            // a pure function of (seed, id, floor, nextCount), so predict it for the UI up front.
            DispatchNetworked(player, relic.Id.Entry, nextCount);
            return PredictOutcome(relic, player, nextCount);
        }

        // Single-player / fake-MP: mutate locally, unchanged historic behavior.
        return RelicForgeService.Reforge(relic, player);
    }

    /// <summary>
    /// CLEANSE a relic's curse so that EVERY client converges. Single-player (or fake-MP): strip
    /// locally, unchanged historic behavior, returning whether a curse was actually removed. Real
    /// co-op: cleanse is a player DECISION (not seed-derivable like a reforge count), so the action
    /// itself is broadcast — (relicEntry) rides a synchronized command and each client, including the
    /// initiator, strips its own copy in <see cref="ApplyCleanseOnClient"/>. Idempotent (a flag + curse
    /// clear), so re-delivery is safe. Returns true when the cleanse was accepted (a curse was present),
    /// so the shop UI charges gold only on a real cleanse. Mirrors <see cref="Reforge"/>.
    /// </summary>
    public static bool Cleanse(RelicModel relic, Player player)
    {
        var run = RunManager.Instance;
        bool coop = run != null && !run.IsSingleplayerOrFakeMultiplayer;

        if (coop && TransportReady)
        {
            // The synced handler strips ALL copies (including this client's) uniformly, so we must NOT
            // also strip locally here — that stays symmetric with the reforge path. Check (without
            // mutating) that there is a curse to remove so we neither charge nor dispatch a no-op.
            if (!RelicForgeService.CanCleanse(relic)) return false;
            DispatchCleanse(player, relic.Id.Entry);
            return true;
        }

        // Single-player / fake-MP: strip locally, unchanged historic behavior.
        return RelicForgeService.Cleanse(relic);
    }

    /// <summary>
    /// The synchronized cleanse HANDLER body — runs on EVERY client. Finds the owner's copy of the
    /// relic and strips its curse via <see cref="RelicForgeService.ApplyCleanse"/> (idempotent: sets the
    /// cleansed flag + clears the curse fields, so re-delivery and late joins are safe). Mirrors
    /// <see cref="ApplyReforgeStepOnClient"/>.
    /// </summary>
    public static void ApplyCleanseOnClient(Player owner, string relicEntry)
    {
        var relic = owner.Relics.FirstOrDefault(
            r => r.Id.Entry == relicEntry && !RelicForgeService.IsCompanion(r));
        if (relic == null) return;
        RelicForgeService.ApplyCleanse(relic);
    }

    /// <summary>Enqueue the cleanse onto the run's synchronized action stream so it replays on every
    /// client, reusing the same built-in <c>ConsoleCmdGameAction</c> wire type as the reforge dispatch
    /// (see <see cref="DispatchNetworked"/>). The synced string is
    /// "<c><see cref="CleanseNetConsoleCmd.Verb"/> &lt;relicEntry&gt;</c>"; each client replays it and calls
    /// <see cref="ApplyCleanseOnClient"/>. Never happens in combat (shop only), so inCombat is false.</summary>
    private static void DispatchCleanse(Player owner, string relicEntry)
    {
        string synced = $"{CleanseNetConsoleCmd.Verb} {relicEntry}";
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
            new ConsoleCmdGameAction(owner, synced, inCombat: false));
    }

    /// <summary>
    /// The synchronized command HANDLER body — pure determinism, meant to run on EVERY client. Finds
    /// the owner's copy of the relic and steps its reforge count up to <paramref name="targetCount"/>,
    /// reusing <see cref="RelicForgeService.Reforge"/> (deterministic given seed+id+floor+count).
    /// Idempotent: a client already at (or past) the target is left alone, so re-delivery and late
    /// joins are safe. Mirrors how RelicCmd.Obtain's forge prefix already keeps passive prefixes
    /// consistent across clients. NOT yet called by anything — it is the target the networked
    /// dispatch's handler should invoke once wired (see MULTIPLAYER_REFORGE.md).
    /// </summary>
    public static void ApplyReforgeStepOnClient(Player owner, string relicEntry, int targetCount)
    {
        var relic = owner.Relics.FirstOrDefault(
            r => r.Id.Entry == relicEntry && !RelicForgeService.IsCompanion(r));
        if (relic == null) return;

        // Each call bumps the count by exactly one; loop so a lagging client catches up. The guard is
        // a runaway backstop only — normal delivery advances a single step.
        int guard = 0;
        while (RelicForgeService.ReforgeCountOf(relic) < targetCount && guard++ < 64)
            RelicForgeService.Reforge(relic, owner);
    }

    /// <summary>
    /// Predict the reforge outcome (Reforged vs RolledPenalty) WITHOUT mutating, for the initiator's
    /// UI in the co-op path. It is a pure function of (seed, id, floor, count); until the shared
    /// derivation is factored out of <see cref="RelicForgeService.Forge"/>, this is a placeholder.
    /// </summary>
    private static RelicForgeService.ReforgeOutcome PredictOutcome(RelicModel relic, Player player, int nextCount)
    {
        // Pure re-derivation from (seed, id, floor, nextCount) in the same roll order as
        // RelicForgeService.Forge — no mutation. The real mutation lands asynchronously via the synced
        // command (ApplyReforgeStepOnClient), but the initiator's campfire UI needs the outcome up
        // front to end its free reforge on a penalty. Both agree because both are the same function.
        var runState = player.RunState;
        return RelicForgeService.PredictReforgeOutcome(relic, runState.Rng.Seed, relic.FloorAddedToDeck, nextCount);
    }

    /// <summary>
    /// Enqueue the reforge onto the run's synchronized action stream so it replays on every client.
    /// We reuse the game's BUILT-IN console-command net action (<c>ConsoleCmdGameAction</c> /
    /// <c>NetConsoleCmdGameAction</c>): its payload is a plain command string, so the mod adds no new
    /// <c>INetAction</c> subtype and the net type-id table is never perturbed (lockstep-safe). The
    /// string "<c>rf_sync &lt;relicEntry&gt; &lt;targetCount&gt;</c>" is replayed through the DevConsole on each
    /// client, where <see cref="ReforgeNetConsoleCmd"/> calls <see cref="ApplyReforgeStepOnClient"/>.
    ///
    /// <paramref name="owner"/> is the local reforging player (the action's OwnerId = its NetId), so
    /// each client resolves the same player and steps that player's own relic copy. relicEntry is the
    /// canonical UPPER_SNAKE id (no spaces), so the space-delimited console parse round-trips cleanly.
    ///
    /// Live co-op sync needs nothing more: every client re-derives the identical grade from
    /// seed+id+floor+count. (Reforge counts are not carried in MP save packets —
    /// ReforgeKeyPacketGuardPatch strips <c>__rf_count</c> — so a client that JOINS mid-run won't
    /// retroactively see prior reforge counts; that mid-join catch-up is the one remaining gap.)
    /// </summary>
    private static void DispatchNetworked(Player owner, string relicEntry, int targetCount)
    {
        string synced = $"{ReforgeNetConsoleCmd.Verb} {relicEntry} {targetCount}";
        // Reforge only ever happens at a rest site or shop, never in combat, so inCombat is false.
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
            new ConsoleCmdGameAction(owner, synced, inCombat: false));
    }
}

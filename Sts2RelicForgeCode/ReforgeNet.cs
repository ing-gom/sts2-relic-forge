using System;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Multiplayer seam for reforge (campfire + shop). WORK IN PROGRESS — see MULTIPLAYER_REFORGE.md.
///
/// The reforge UIs mutate relic state LOCALLY (<see cref="RelicForgeService.Reforge"/>) with no
/// networked command, so co-op would desync. That is why both entry points are gated to
/// single-player. The fix mirrors how the game's own card upgrade / card removal work: ride a
/// synchronized command that runs on EVERY client, and let each client re-derive the identical
/// result. This mod's forge is already seed-deterministic (seed+id+floor+count), so passive
/// prefixes stay consistent across clients without transmitting the result — reforge only needs the
/// same treatment: broadcast (relic, newCount), then each client re-derives locally.
///
/// This file centralizes that decision behind one seam so the two reforge call sites and the two
/// availability gates route through it. Everything here is single-player-identical UNTIL
/// <see cref="TransportReady"/> flips — the networked dispatch (<see cref="DispatchNetworked"/>) is
/// the one piece that needs the game/ModKit command API and must be filled in locally.
/// </summary>
internal static class ReforgeNet
{
    /// <summary>
    /// Flip to true ONCE <see cref="DispatchNetworked"/> is implemented against the game/ModKit
    /// networked-command API. Until then reforge stays single-player-only, exactly as before, so a
    /// co-op session never sees the desyncing local-only mutation. Kept as a <c>static readonly</c>
    /// (not <c>const</c>) on purpose so the co-op branches below compile without unreachable-code
    /// warnings while the transport is still stubbed.
    /// </summary>
    internal static readonly bool TransportReady = false;

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

        // Single-player / fake-MP, and the current co-op-disabled fallback: local mutation.
        return RelicForgeService.Reforge(relic, player);
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
        // TODO(mp): compute deterministically from (seed, id, floor, nextCount) — same roll order as
        // RelicForgeService.Forge — or read it back after the synced handler runs on this client.
        return RelicForgeService.ReforgeOutcome.Reforged;
    }

    /// <summary>
    /// Enqueue the reforge into the game's synchronized command stream so it runs on every client.
    /// TODO(mp): implement against the game/ModKit command API. Mirror CardCmd.Upgrade / card removal:
    /// a command in the run's synchronized queue whose handler calls
    /// <see cref="ApplyReforgeStepOnClient"/> with (owner, relicEntry, targetCount) on all clients.
    /// Candidate transports (confirm which the ModKit exposes to mods):
    ///   1. A networked console-style command (AbstractConsoleCmd already has IsNetworked) issued
    ///      programmatically — smallest surface, reuses an existing synced channel.
    ///   2. A custom Cmd type carrying (playerId, relicEntry, targetCount).
    ///   3. A networked relic-selection command (the analogue of CardSelectCmd.FromSimpleGrid the mod
    ///      already uses), which also moves the picker itself onto the synced path.
    /// Also fix reforge-count replication: ReforgeKeyPacketGuardPatch currently STRIPS __rf_count from
    /// MP packets. With every client stepping locally that is fine for live sync; register the key (or
    /// carry the count in the command) so late joiners / mid-run state sync stay consistent.
    /// </summary>
    private static void DispatchNetworked(Player owner, string relicEntry, int targetCount)
        => throw new NotImplementedException(
            "networked reforge dispatch not wired — see MULTIPLAYER_REFORGE.md. "
            + "Keep ReforgeNet.TransportReady false until this is implemented and co-op tested.");
}

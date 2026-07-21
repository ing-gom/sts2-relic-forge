using System.Globalization;
using MegaCrit.Sts2.Core.Context;              // LocalContext
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;                 // RunManager

namespace Sts2RelicForge;

/// <summary>
/// NETWORKED transport that carries the HOST's forge settings to every co-op client so all clients
/// derive the same curses / enemy prefixes (see <see cref="HostForgeConfig"/>). The host enqueues it via
/// <see cref="ForgeConfigBroadcaster.BroadcastIfHost"/>; the game replays the string through the
/// DevConsole on every client, where <see cref="Process"/> stores the values in
/// <see cref="HostForgeConfig"/>.
///
/// Like <see cref="ReforgeNetConsoleCmd"/> / <see cref="CleanseNetConsoleCmd"/> this reuses the game's
/// built-in <c>ConsoleCmdGameAction</c> wire type (a plain string payload), so the mod adds NO new
/// <c>INetAction</c> subtype and never perturbs the net type-id ordering — lockstep-safe.
/// </summary>
public sealed class RelicForgeConfigSyncCmd : AbstractConsoleCmd
{
    public const string Verb = "rf_config";

    public override string CmdName => Verb;
    public override string Args => "<noPrefix> <curse> <selfShare> <ancient01> <enemyForge01>";
    public override string Description =>
        "Internal (networked): the host broadcasts its Relic Forge settings so every client derives identically.";
    public override bool IsNetworked => true;   // routes through the synchronized action queue
    public override bool DebugOnly => false;    // must register in normal (non-debug) co-op play

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        // Runs on EVERY client. Parse the five host-authoritative settings and cache them; the
        // accessors in HostForgeConfig only consult them while we are a client, so applying them on
        // the host (which also replays the command) is a harmless no-op. (Enemy balance strength is no
        // longer broadcast — it is a fixed constant identical on every client, so it never diverges.)
        if (args.Length < 5)
            return new CmdResult(success: false, "Usage: rf_config <noPrefix> <curse> <selfShare> <ancient01> <enemyForge01>");

        var inv = CultureInfo.InvariantCulture;
        if (!double.TryParse(args[0], NumberStyles.Float, inv, out double noPrefix) ||
            !double.TryParse(args[1], NumberStyles.Float, inv, out double curse) ||
            !double.TryParse(args[2], NumberStyles.Float, inv, out double selfShare))
            return new CmdResult(success: false, "rf_config: bad numeric arg.");

        bool ancient = args[3] == "1";
        bool enemyForge = args[4] == "1";
        // Optional 7th arg (v1.0.26+ hosts): campfire cleanse enabled. Absent (older host) = true —
        // the pre-toggle behavior. Sits AFTER the fingerprint (arg 6) so both optional args stay
        // positionally stable; older clients ignore both.
        bool campCleanse = args.Length < 7 || args[6] == "1";
        // Optional 8th arg (prefix-pool filter hosts): 0=all / 1=enhance-only / 2=effects-only /
        // 3=custom. Absent (older host) = 0 — the unfiltered pool. Same tail-compat contract as arg 7.
        int prefixPool = args.Length >= 8 && int.TryParse(args[7], NumberStyles.Integer, inv, out int pp) ? pp : 0;
        // Optional 9th arg (custom-pool hosts): disabled prefix/curse INDICES ("p,p;c,c", '-' = none),
        // decoded against the local (fingerprint-guarded) bases into name sets. Absent = empty sets.
        var (dp, dc) = args.Length >= 9 ? CustomPool.Decode(args[8])
                                        : (new System.Collections.Generic.HashSet<string>(), new System.Collections.Generic.HashSet<string>());

        try
        {
            HostForgeConfig.ApplyFromHost(noPrefix, curse, selfShare, ancient, enemyForge, campCleanse, prefixPool);
            HostForgeConfig.ApplyPoolsFromHost(dp, dc);
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] rf_config apply failed: {e.Message}");
            return new CmdResult(success: false, $"rf_config error: {e.Message}");
        }

        // Optional 6th arg (new hosts): the host's prefix/curse POOL fingerprint. Roll/PickCombined walk
        // the pool in order, so a sister mod registered on one peer but not another makes the same seeded
        // roll land a different prefix per peer — an unconvergeable divergence. A mismatch trips the
        // SYMMETRIC safe mode (see ForgeSafeMode): the client trips here, the host trips from the client's
        // own rf_fp announce, so every peer deactivates the forge identically (vanilla = converged).
        if (args.Length >= 6 && args[5] != PoolFingerprint())
            ForgeSafeMode.Trip($"host '{args[5]}' vs local '{PoolFingerprint()}'");
        return new CmdResult(success: true, "rf_config applied.");
    }

    /// <summary>Fingerprint of the combined prefix + self-curse pools ("count/count:hash"). External
    /// registrations are name-sorted into the pool (RegisterExternal), so this is a pure function of
    /// the registered SET — identical sister-mod sets ⇔ identical fingerprint, regardless of load order.</summary>
    internal static string PoolFingerprint()
    {
        unchecked
        {
            uint h = 2166136261u;                                  // FNV-1a over names, order-sensitive
            void Mix(string s) { foreach (char c in s) { h ^= c; h *= 16777619u; } h ^= '\n'; h *= 16777619u; }
            int np = 0, nc = 0;
            foreach (var p in PrefixTable.Pool) { Mix(p.Name); np++; }
            foreach (var c in SelfCurseTable.Pool) { Mix(c.En); nc++; }
            return $"{np}/{nc}:{h:x8}";
        }
    }
}

/// <summary>
/// Host-side helper: enqueue an <c>rf_config</c> onto the run's synchronized action stream so every
/// client caches the host's forge settings. A no-op unless we are the co-op host, so it is safe to call
/// from any client on shared triggers (shop open, rest-site entry, a config change).
/// </summary>
internal static class ForgeConfigBroadcaster
{
    public static void BroadcastIfHost()
    {
        if (!HostForgeConfig.IsHost) return;
        var run = RunManager.Instance;
        var me = LocalContext.GetMe(run.State?.Players ?? System.Linq.Enumerable.Empty<Player>());
        if (me == null) return;   // no resolvable local player yet — a later trigger will re-broadcast

        var inv = CultureInfo.InvariantCulture;
        // Arg 6 = the host's prefix/curse pool fingerprint (order-sensitive) so clients can detect a
        // sister-mod registration mismatch — the one API-contract violation that silently desyncs rolls.
        // Old clients simply ignore the extra arg (they parse the first five positionally).
        // Forge-RNG args are read from HostForgeConfig, which on the host returns the RUN-LOCKED snapshot
        // (EnsureRunLock) — so a mid-run edit broadcasts the locked value, never the new one, and clients
        // stay on the run's original settings. campfire-cleanse (arg 7) is NOT locked → read live.
        string synced = string.Format(inv, "{0} {1} {2} {3} {4} {5} {6} {7} {8} {9}",
            Verb(),
            HostForgeConfig.NoPrefixChance.ToString("R", inv),
            HostForgeConfig.CurseChance.ToString("R", inv),
            HostForgeConfig.SelfCurseShare.ToString("R", inv),
            HostForgeConfig.ForgeAncient ? "1" : "0",
            HostForgeConfig.EnemyForgeEnabled ? "1" : "0",
            RelicForgeConfigSyncCmd.PoolFingerprint(),
            ForgeConfig.CampfireCleanseEnabled ? "1" : "0",    // arg 7 — campfire cleanse (LIVE, not locked)
            HostForgeConfig.PrefixPool.ToString(inv),          // arg 8 — prefix-pool filter (run-locked)
            HostForgeConfig.EncodeLockedPool());               // arg 9 — custom-pool disabled indices (run-locked)

        // Never in combat (shop / rest / menu only), so inCombat is false — matches the reforge dispatch.
        run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, synced, inCombat: false));
        // Breadcrumb for the "black screen on event/room entry" report: this networked enqueue is the ONLY
        // mod code that fires per-room in co-op, so a hang right after this line in a client's log pins it.
        MainFile.Logger.Info($"[{MainFile.ModId}] host broadcast rf_config to clients.");
    }

    /// <summary>
    /// Host-side: broadcast every relic's reforge COUNT + CLEANSED flag (the state that cannot cross the
    /// packet wire — see <see cref="RelicForgeCountSyncCmd"/>) so a RECONNECTED client can restore it. Only
    /// relics that actually need it (count &gt; 0 or cleansed) go in the payload; if none do, nothing is
    /// enqueued. A no-op off the co-op host. Fires on room entry (belt-and-suspenders alongside the config
    /// broadcast), so a client that rebuilt its relics on reconnect reconciles at the next room boundary.
    /// The command is idempotent on every peer, so re-sending each room costs only a cheap in-sync check.
    /// </summary>
    public static void BroadcastCountsIfHost()
    {
        if (ForgeSafeMode.Active) return;                      // nothing to reconcile — the forge is inert everywhere
        if (!HostForgeConfig.IsHost) return;
        var run = RunManager.Instance;
        var state = run?.State;
        var me = LocalContext.GetMe(state?.Players ?? System.Linq.Enumerable.Empty<Player>());
        if (state == null || me == null) return;

        var sb = new System.Text.StringBuilder(RelicForgeCountSyncCmd.Verb);
        int n = 0;
        // Occurrence index per (player, relic id): with TWO copies of the same relic id, a token keyed on
        // id alone would resolve to the FIRST instance on every peer — instance #2 never reconciles, and
        // the host's own replay would even "reconcile" its instance #1 to instance #2's enchantment. The
        // index is appended as an optional 7th field (old decoders ignore it; missing = 0 on new ones).
        var occ = new System.Collections.Generic.Dictionary<(ulong, string), int>();
        foreach (var player in state.Players)
            foreach (var relic in player.Relics)
            {
                if (RelicForgeService.IsCompanion(relic)) continue;      // hidden donors re-derive from their host
                var key = (player.NetId, relic.Id.Entry);
                occ.TryGetValue(key, out int idx);                        // 0 for the first instance
                occ[key] = idx + 1;
                int count = RelicForgeService.ReforgeCountOf(relic);
                bool cleansed = RelicForgeService.IsCleansed(relic);
                int gred = RelicForgeService.GaugeReductionOf(relic);
                string? desc = RelicForgeService.DescriptorOf(relic);
                // Carry any relic the host has FORGED (has a descriptor) so a config-diverged / reconnected
                // client converges to the host's EXACT enchantment — not just re-forged/cleansed relics. The
                // descriptor rides as a ':' field ("prefix|rider|self|fbStat|fbAmt|fbPct" — escaped, so no ':'
                // or space survives to break the token split). A relic with no descriptor and no non-derivable
                // state is vanilla to the mod: nothing to sync.
                if (string.IsNullOrEmpty(desc) && count <= 0 && !cleansed && gred <= 0) continue;
                sb.Append(' ').Append(player.NetId).Append(':').Append(relic.Id.Entry)
                  .Append(':').Append(count).Append(':').Append(cleansed ? '1' : '0').Append(':').Append(gred)
                  .Append(':').Append(RelicForgeService.EscapeWireDesc(desc ?? ""))   // ★escape: rider suffixes may contain a space
                  .Append(':').Append(idx);                                          // duplicate-id disambiguator
                n++;
            }
        if (n == 0) return;   // nothing reforged/cleansed yet — skip the enqueue entirely

        run!.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, sb.ToString(), inCombat: false));
        MainFile.Logger.Info($"[{MainFile.ModId}] host broadcast rf_counts ({n} relic(s)) to clients.");
    }

    private static string Verb() => RelicForgeConfigSyncCmd.Verb;
}

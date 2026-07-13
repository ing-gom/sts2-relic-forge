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

        HostForgeConfig.ApplyFromHost(noPrefix, curse, selfShare, ancient, enemyForge);
        return new CmdResult(success: true, "rf_config applied.");
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
        string synced = string.Format(inv, "{0} {1} {2} {3} {4} {5}",
            Verb(),
            ForgeConfig.NoPrefixChance.ToString("R", inv),
            ForgeConfig.CurseChance.ToString("R", inv),
            ForgeConfig.SelfCurseShare.ToString("R", inv),
            ForgeConfig.ForgeAncientRelics ? "1" : "0",
            ForgeConfig.EnemyForgeEnabled ? "1" : "0");

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
        if (!HostForgeConfig.IsHost) return;
        var run = RunManager.Instance;
        var state = run?.State;
        var me = LocalContext.GetMe(state?.Players ?? System.Linq.Enumerable.Empty<Player>());
        if (state == null || me == null) return;

        var sb = new System.Text.StringBuilder(RelicForgeCountSyncCmd.Verb);
        int n = 0;
        foreach (var player in state.Players)
            foreach (var relic in player.Relics)
            {
                if (RelicForgeService.IsCompanion(relic)) continue;      // hidden donors re-derive from their host
                int count = RelicForgeService.ReforgeCountOf(relic);
                bool cleansed = RelicForgeService.IsCleansed(relic);
                int gred = RelicForgeService.GaugeReductionOf(relic);
                string? desc = RelicForgeService.DescriptorOf(relic);
                // Carry any relic the host has FORGED (has a descriptor) so a config-diverged / reconnected
                // client converges to the host's EXACT enchantment — not just re-forged/cleansed relics. The
                // descriptor rides as a trailing ':' field ("prefix|rider|self|fbStat|fbAmt|fbPct" — no ':' or
                // space, so it never breaks the token split). A relic with no descriptor and no non-derivable
                // state is vanilla to the mod: nothing to sync.
                if (string.IsNullOrEmpty(desc) && count <= 0 && !cleansed && gred <= 0) continue;
                sb.Append(' ').Append(player.NetId).Append(':').Append(relic.Id.Entry)
                  .Append(':').Append(count).Append(':').Append(cleansed ? '1' : '0').Append(':').Append(gred)
                  .Append(':').Append(desc ?? "");
                n++;
            }
        if (n == 0) return;   // nothing reforged/cleansed yet — skip the enqueue entirely

        run!.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, sb.ToString(), inCombat: false));
        MainFile.Logger.Info($"[{MainFile.ModId}] host broadcast rf_counts ({n} relic(s)) to clients.");
    }

    private static string Verb() => RelicForgeConfigSyncCmd.Verb;
}

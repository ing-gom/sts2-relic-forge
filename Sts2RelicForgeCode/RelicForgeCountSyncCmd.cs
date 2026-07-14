using System.Globalization;
using System.Linq;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;                 // RunManager

namespace Sts2RelicForge;

/// <summary>
/// NETWORKED transport that carries the HOST's per-relic reforge COUNT + CLEANSED flag to every co-op
/// client — the one piece of forge state that cannot ride the packet wire (the SavedProperties packet
/// serializer strips our custom "__rf_count" / "__rf_cleansed" keys, see ReforgeKeyPacketGuardPatch), so a
/// client that rebuilt its relic instances on RECONNECT loses them and shows the wrong / vanished prefix.
/// This is the "mid-join catch-up" gap ReforgeNet documents.
///
/// Payload = a space-separated token per relic that needs syncing (count &gt; 0 OR cleansed), each token
/// "<c>netId:RELIC_ID:count:cleansed01</c>". The host enqueues it on room entry via
/// <see cref="ForgeConfigBroadcaster.BroadcastCountsIfHost"/>; the game replays it through the DevConsole
/// on every client, where <see cref="Process"/> reconciles each relic to the host's state.
///
/// Like <see cref="RelicForgeConfigSyncCmd"/> / <see cref="ReforgeNetConsoleCmd"/> this reuses the game's
/// built-in <c>ConsoleCmdGameAction</c> wire type (a plain string payload), so the mod adds NO new
/// <c>INetAction</c> subtype and never perturbs the net type-id ordering — lockstep-safe.
/// </summary>
public sealed class RelicForgeCountSyncCmd : AbstractConsoleCmd
{
    public const string Verb = "rf_counts";

    public override string CmdName => Verb;
    public override string Args => "<netId:RELIC:count:cl> ...";
    public override string Description =>
        "Internal (networked): the host broadcasts per-relic reforge counts so a reconnected client restores them.";
    public override bool IsNetworked => true;   // routes through the synchronized action queue
    public override bool DebugOnly => false;    // must register in normal (non-debug) co-op play

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        // Runs on EVERY client (and the host). Reconcile each listed relic to the host's authoritative
        // reforge count + cleansed flag. ReconcileToHost is idempotent — a no-op wherever the state
        // already matches (the host, and every synced client in normal play), so only a reconnected
        // client that lost its counts actually rebuilds.
        var state = RunManager.Instance?.State;
        if (state == null) return new CmdResult(success: false, "rf_counts: no active run.");

        var inv = CultureInfo.InvariantCulture;
        int applied = 0;
        foreach (var token in args)
        {
            var f = token.Split(':');
            // netId:relicId:count:cleansed[:gaugeReduction[:descriptor]] — fields 5 (gred) and 6 (desc) are
            // back-compat optional. The descriptor is the LAST field and contains no ':' (its own separator is
            // '|'), so rejoining f[5..] is defensive against a future field that might.
            if (f.Length < 4) continue;
            if (!ulong.TryParse(f[0], NumberStyles.Integer, inv, out ulong netId)) continue;
            string relicId = f[1];
            if (!int.TryParse(f[2], NumberStyles.Integer, inv, out int count)) continue;
            bool cleansed = f[3] == "1";
            int gred = 0;
            if (f.Length >= 5) int.TryParse(f[4], NumberStyles.Integer, inv, out gred);
            // Descriptor is escaped on the wire (rider suffixes like "the Tyrant" contain a space that would
            // otherwise split the token). It no longer contains ':', so f[5] is the whole field; unescape it.
            string? desc = f.Length >= 6 ? RelicForgeService.UnescapeWireDesc(f[5]) : null;
            // Optional 7th field: occurrence index for duplicate relic ids (0 = first instance). Absent on
            // a legacy host → 0, which is the old FirstOrDefault behavior.
            int occIdx = 0;
            if (f.Length >= 7) int.TryParse(f[6], NumberStyles.Integer, inv, out occIdx);

            var player = state.Players.FirstOrDefault(p => p.NetId == netId);
            if (player == null) continue;
            var relic = player.Relics.Where(
                r => r.Id.Entry == relicId && !RelicForgeService.IsCompanion(r))
                .Skip(occIdx).FirstOrDefault();
            if (relic == null) continue;

            // Per-relic guard: a throw while reconciling ONE relic must NOT abort the whole rf_counts
            // sync — that would leave the remaining relics un-reconciled and could itself desync the
            // lockstep. Isolate each relic; log the offender (with its ids) so a divergence report can
            // pinpoint which relic/curse broke.
            try
            {
                RelicForgeService.ReconcileToHost(relic, player, count, cleansed, gred, desc);
                applied++;
            }
            catch (System.Exception e)
            {
                MainFile.Logger.Warn($"[{MainFile.ModId}] rf_counts reconcile failed for {relicId} " +
                                     $"(net {netId}, #{count}, desc '{desc}'): {e.Message}");
            }
        }

        // Diagnostic: per-player relic + hidden-companion counts after applying. Co-op checksum divergence
        // (the campfire black screen) is almost always a companion-count mismatch — logging both peers'
        // counts here lets a divergence be spotted by diffing the two clients' logs for the same room.
        foreach (var p in state.Players)
        {
            int relics = 0, companions = 0;
            foreach (var r in p.Relics) { relics++; if (RelicForgeService.IsCompanion(r)) companions++; }
            MainFile.Logger.Info($"[{MainFile.ModId}] rf_counts post-sync net {p.NetId}: {relics} relics ({companions} hidden companions).");
        }
        return new CmdResult(success: true, $"rf_counts applied ({applied}).");
    }
}

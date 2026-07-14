// LOCAL TEST ONLY — dormant unless `selftest.coop.flag` is next to the mod DLL. Delete/exclude before a
// workshop release build. Drives the co-op lobby, grants + reforges a relic on the HOST via the mod's
// networked path (rf_sync), and both peers record the relic's forge descriptor to selftest.coop.<role>.txt.
// Convergence = host descriptor == join descriptor, with no drop. See the coop-verify skill.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

internal static class CoopTest
{
    private const string RelicId = "akabeko";       // granted + reforged; any forgeable relic works
    private static readonly StringBuilder _out = new();
    private static bool _isHost, _readySent, _done;
    private static string _role = "?";

    private static string ModDir() => Path.GetDirectoryName(typeof(CoopTest).Assembly.Location) ?? ".";

    public static void ArmIfRequested()
    {
        try
        {
            if (!File.Exists(Path.Combine(ModDir(), "selftest.coop.flag"))) return;
            var fm = System.Environment.GetCommandLineArgs().FirstOrDefault(a => a.Contains("fastmp"));
            _isHost = fm != null && fm.Contains("host");
            _role = fm == null ? "nofastmp" : (_isHost ? "host" : "join");
            W($"coop selftest armed (role={_role})");
            Poll();
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] coop arm failed: {e.Message}"); }
    }

    private static void Poll()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || _done) return;
        try { Tick(tree); } catch (Exception e) { W("tick exception: " + e.Message); }
        if (!_done) tree.CreateTimer(2.0).Timeout += Poll;
    }

    private static void Tick(SceneTree tree)
    {
        var run = RunManager.Instance;
        if (run != null && run.IsInProgress && (run.State?.Players?.Count ?? 0) >= 2)
        {
            _done = true;
            W($"COOP RUN IN PROGRESS — players={run.State!.Players.Count}");
            TaskHelper.RunSafely(_isHost ? HostPhase(run) : JoinPhase(run));
            return;
        }
        if (!_readySent)
        {
            var screen = FindScreen(tree.Root);
            if (screen == null) { W("waiting for character-select lobby…"); return; }
            var lobby = LobbyOf(screen);
            if (lobby == null) { W("lobby null"); return; }
            try { lobby.SetReady(true); _readySent = true; W("SetReady(true) sent"); }
            catch (Exception e) { W("SetReady failed: " + e.Message); }
        }
    }

    private static async Task HostPhase(RunManager run)
    {
        try
        {
            await Task.Delay(2000);
            var me = LocalPlayerOf(run);
            if (me == null) { W("HOST: no local player"); Flush(false); return; }

            // Networked grant so BOTH peers get the relic (never RelicCmd.Obtain locally in co-op).
            run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, $"relic {RelicId}", inCombat: false));
            await Task.Delay(4000);
            var relic = FindRelic(me);
            W($"HOST: after grant, relic={(relic?.Id.Entry ?? "MISSING")}, desc='{RelicForgeService.DescriptorOf(relic!) ?? "-"}'");
            if (relic == null) { W("HOST: relic not granted"); Flush(false); return; }

            // Reforge twice through the mod's networked path (rf_sync → both peers re-derive).
            ReforgeNet.Reforge(relic, me);
            await Task.Delay(2500);
            ReforgeNet.Reforge(relic, me);
            await Task.Delay(4000);

            W($"HOST: FINAL desc = '{RelicForgeService.DescriptorOf(relic) ?? "-"}' (count {RelicForgeService.ReforgeCountOf(relic)})");
            W("=== coop host done ===");
            Flush(true);
        }
        catch (Exception e) { W("HOST exception: " + e); Flush(false); }
    }

    private static async Task JoinPhase(RunManager run)
    {
        try
        {
            await Task.Delay(13000);   // wait for grant + both reforges to replicate
            var host = run.State!.Players.OrderBy(p => p.NetId).First();
            var relic = FindRelic(host);
            W($"JOIN: FINAL desc = '{(relic != null ? RelicForgeService.DescriptorOf(relic) ?? "-" : "MISSING")}' (count {(relic != null ? RelicForgeService.ReforgeCountOf(relic) : -1)})");
            W("=== coop join done ===");
            Flush(true);
        }
        catch (Exception e) { W("JOIN exception: " + e); Flush(false); }
    }

    private static RelicModel? FindRelic(Player p)
        => p.Relics.FirstOrDefault(r => r.Id.Entry.Equals(RelicId, StringComparison.OrdinalIgnoreCase) && !RelicForgeService.IsCompanion(r))
           ?? p.Relics.FirstOrDefault(r => r.Id.Entry.Contains("AKABEKO") && !RelicForgeService.IsCompanion(r));

    private static NCharacterSelectScreen? FindScreen(Node n)
    {
        if (n is NCharacterSelectScreen s) return s;
        foreach (var c in n.GetChildren()) { var r = FindScreen(c); if (r != null) return r; }
        return null;
    }

    private static StartRunLobby? LobbyOf(NCharacterSelectScreen screen)
    {
        try { return typeof(NCharacterSelectScreen).GetField("_lobby", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(screen) as StartRunLobby; }
        catch { return null; }
    }

    private static Player? LocalPlayerOf(RunManager run)
    {
        var players = run.State!.Players;
        try { var me = LocalContext.GetMe(players); if (me != null) return me; } catch { }
        ulong id; try { id = run.NetService.NetId; } catch { id = 1uL; }
        return players.FirstOrDefault(p => p.NetId == id) ?? players.FirstOrDefault();
    }

    private static void W(string line) { _out.AppendLine(line); MainFile.Logger.Info($"[{MainFile.ModId}] COOP[{_role}] | {line}"); }

    private static void Flush(bool ok)
    {
        _done = true;
        _out.Insert(0, (ok ? "RESULT: OK\n" : "RESULT: FAIL\n") + "role=" + _role + "\n");
        try { File.WriteAllText(Path.Combine(ModDir(), $"selftest.coop.{_role}.txt"), _out.ToString()); } catch { }
    }
}

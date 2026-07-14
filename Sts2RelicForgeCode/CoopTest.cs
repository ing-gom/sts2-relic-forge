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
            await Shot("01_run");   // ★mandatory visual evidence: this instance really entered the co-op run
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
            await Shot("02_final"); // ★mandatory: the screen with the reforged relic applied
            W("=== coop host done ===");
            Flush(true);
        }
        catch (Exception e) { W("HOST exception: " + e); Flush(false); }
    }

    private static async Task JoinPhase(RunManager run)
    {
        try
        {
            await Task.Delay(2000);
            await Shot("01_run");            // ★mandatory: the JOIN side also entered the run
            await Task.Delay(11000);   // wait for grant + both reforges to replicate
            var host = run.State!.Players.OrderBy(p => p.NetId).First();
            var relic = FindRelic(host);
            W($"JOIN: FINAL desc = '{(relic != null ? RelicForgeService.DescriptorOf(relic) ?? "-" : "MISSING")}' (count {(relic != null ? RelicForgeService.ReforgeCountOf(relic) : -1)})");
            await Shot("02_final");          // ★mandatory: what the client actually SEES after replication
            W("=== coop join done ===");
            Flush(true);
        }
        catch (Exception e) { W("JOIN exception: " + e); Flush(false); }
    }

    /// <summary>Save the root viewport to selftest.coop.&lt;role&gt;.&lt;name&gt;.png (role tag: both instances
    /// share one mods folder, so untagged names would overwrite each other). Retries while the frame is
    /// still BLACK — right after run-entry the viewport is often a loading/transition frame, and a pure
    /// black png is worthless as visual evidence (found by the mandatory eyeball check). See coop-verify.</summary>
    private static async Task Shot(string name, int tries = 6)
    {
        try
        {
            for (int i = 0; i < tries; i++)
            {
                if (Engine.GetMainLoop() is not SceneTree tree) return;
                var img = tree.Root.GetTexture()?.GetImage();
                if (img != null && !IsBlank(img))
                {
                    var err = img.SavePng(Path.Combine(ModDir(), $"selftest.coop.{_role}.{name}.png"));
                    W($"shot {name}: {err} (try {i + 1})");
                    return;
                }
                await Task.Delay(2000);   // frame not drawn yet — wait and retry
            }
            // Last resort: save whatever is there (flagged) so the evidence gap is visible in the log.
            if (Engine.GetMainLoop() is SceneTree t2)
                t2.Root.GetTexture()?.GetImage()?.SavePng(Path.Combine(ModDir(), $"selftest.coop.{_role}.{name}.png"));
            W($"shot {name}: still black after {tries} tries (saved anyway)");
        }
        catch (Exception e) { W($"shot {name} failed: {e.Message}"); }
    }

    /// <summary>All-black check on a sparse pixel grid (cheap: ~81 samples, not 2M pixels).</summary>
    private static bool IsBlank(Image img)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        if (w == 0 || h == 0) return true;
        for (int x = w / 10; x < w; x += System.Math.Max(1, w / 10))
            for (int y = h / 10; y < h; y += System.Math.Max(1, h / 10))
            {
                var c = img.GetPixel(x, y);
                if (c.R + c.G + c.B > 0.05f) return false;
            }
        return true;
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

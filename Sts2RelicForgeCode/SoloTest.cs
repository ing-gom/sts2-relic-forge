// LOCAL TEST ONLY — dormant unless `selftest.sp.flag` is next to the mod DLL. Delete this file (or
// exclude it) before a workshop release build. See the solo-verify skill.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// solo-verify self-test for Sts2RelicForge. Armed by `selftest.sp.flag` next to the mod DLL; starts a
/// single-player run, then runs a deterministic in-process battery over the mod's own (internal) service
/// and public API, verifying the untested v1.0.2–v1.0.8 logic in the REAL game (assembly loaded, patches
/// applied, ModelDb/PrefixTable live). Writes RESULT: OK/FAIL + per-test lines to selftest.sp.txt.
///
/// Compiled only under the RELICFORGE_SELFTEST symbol so it never ships in a release build.
/// </summary>
internal static class SoloTest
{
    private static readonly StringBuilder _out = new();
    private static bool _started, _done;
    private static int _pass, _fail;

    private static string ModDir() => Path.GetDirectoryName(typeof(SoloTest).Assembly.Location) ?? ".";

    public static void ArmIfRequested()
    {
        try
        {
            if (!File.Exists(Path.Combine(ModDir(), "selftest.sp.flag"))) return;
            W("solo selftest armed");
            Poll();
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] solo arm failed: {e.Message}"); }
    }

    private static void Poll()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || _done) return;
        try
        {
            var run = RunManager.Instance;
            if (!_started && (run == null || !run.IsInProgress) && NGame.Instance != null)
            {
                _started = true;
                W("starting single-player run…");
                TaskHelper.RunSafely(RunBattery());
            }
        }
        catch (Exception e) { W("poll exception: " + e.Message); }
        if (!_done) tree.CreateTimer(2.0).Timeout += Poll;
    }

    private static bool ContentReady()
    {
        try
        {
            return ModelDb.Contains(typeof(MegaCrit.Sts2.Core.Models.Relics.Anchor))
                || ModelDb.Contains(typeof(MegaCrit.Sts2.Core.Models.Relics.OrnamentalFan));
        }
        catch { return false; }
    }

    private static async Task RunBattery()
    {
        try
        {
            // Wait for ModelDb content registration (relics) — mods can delay it, and T1/T2 need a real
            // relic. T3/T4/T5 don't, so proceed after a cap even if content never becomes available.
            for (int i = 0; i < 12 && !ContentReady(); i++) await Task.Delay(2000);
            W($"content ready = {ContentReady()}");

            // Try to start a full run for the UI screenshot, but DON'T let a broken run-start abort the
            // logic battery: the workshop mod soup (custom-character mods) can break ModelDb.AllCharacters,
            // and the v1.0.2/5/6/8 logic under test needs neither a run nor a character.
            try
            {
                var character = ModelDb.AllCharacters.First();
                var acts = ActModel.GetDefaultList().ToList();
                await NGame.Instance.StartNewSingleplayerRun(character, shouldSave: false, acts,
                    Array.Empty<ModifierModel>(), "SOLOTEST", GameMode.Standard, 0);
                await Task.Delay(3000);
                Shot("01_run");   // visual evidence: the run actually started (map screen)
            }
            catch (Exception e) { W("run-start skipped (mod env): " + e.Message.Split('\n')[0]); }

            var run = RunManager.Instance;
            var player = run?.State?.Players?.FirstOrDefault();
            uint seed = player?.RunState.Rng.Seed ?? 12345u;
            W($"battery: run={run?.IsInProgress == true}, player={player?.Character?.Id.Entry ?? "none"}, seed={seed}");

            // Pick a positive numeric prefix + a relic with a scalable var (for the forge tests).
            var pfx = FirstNumericPrefix();
            var relic = FirstNumericRelic();

            // T1 — core forge: forcing a numeric prefix scales a var and records it.
            Test("T1 forge-applies", () =>
            {
                if (pfx == null || relic == null) return "no numeric prefix/relic available";
                RelicForgeService.Forge(relic, seed, 1, forced: pfx);
                var rec = RelicForgeService.RecordFor(relic);
                if (rec == null) return "no forge record after Forge";
                if (!rec.HasChanges) return "record has no var changes";
                return null;
            });

            // T2 — v1.0.8 ReassertForgeVars: a var reset to canonical is re-applied.
            Test("T2 reassert (Rewind fix)", () =>
            {
                var rec = relic != null ? RelicForgeService.RecordFor(relic) : null;
                var c = rec?.Changes.FirstOrDefault();
                if (relic == null || c == null) return "no change to reassert";
                if (!relic.DynamicVars.TryGetValue(c.VarName, out var dv)) return "var missing";
                dv.BaseValue = c.OldValue;                         // simulate a mid-combat restore reset
                RelicForgeService.ReassertForgeVars(relic);
                return dv.BaseValue == c.NewValue ? null : $"not re-applied ({dv.BaseValue} != {c.NewValue})";
            });

            // T3 — v1.0.2 descriptor encode (pure, no ModelDb relic needed): a record with an enemy-rider
            // "the Tyrant" encodes to the exact "prefix|rider|self|fbStat|fbAmt|fbPct" string (this is also
            // the space-bearing descriptor that T4's escape then makes wire-safe).
            Test("T3 descriptor encode", () =>
            {
                var rec = new ForgeRecord
                {
                    Rarity = MegaCrit.Sts2.Core.Entities.Relics.RelicRarity.Common,
                    Prefix = "Forceful", Percent = 0.3, EnemyRider = true, EnemyRiderSuffix = "the Tyrant",
                };
                // prefix | rider | self-curse | fbStat | fbAmt | fbPct  (self-curse empty here)
                string? desc = RelicForgeService.EncodeDescriptor(rec);
                return desc == "Forceful|the Tyrant|||0|0" ? null : $"encoded '{desc}'";
            });

            // T4 — v1.0.6 wire escape: a space-bearing rider suffix survives the space-delimited payload.
            Test("T4 wire escape (black-screen fix)", () =>
            {
                const string orig = "Anchored|the Tyrant||0|0";
                string esc = RelicForgeService.EscapeWireDesc(orig);
                if (esc.Contains(' ')) return $"escaped still has a space: '{esc}'";
                string back = RelicForgeService.UnescapeWireDesc(esc);
                return back == orig ? null : $"unescape mismatch '{back}'";
            });

            // T5 — v1.0.5 modding API: register a prefix + a self-curse; both resolve.
            Test("T5 register API", () =>
            {
                RelicForgeApi.RegisterPrefix(new ForgePrefixDef { Name = "SoloTestPrefix", PowerPct = 0.5, Weight = 1, Ko = "솔로", Color = "#ff00ff" });
                if (PrefixTable.ByName("SoloTestPrefix") == null) return "registered prefix not found";
                RelicForgeApi.RegisterSelfCurse(new ForgeSelfCurseDef { Name = "SoloTestCurse", OnHitPower = "Weak", EffEn = "test" });
                if (SelfCurseTable.ByKey("SoloTestCurse") == null) return "registered curse not found";
                return null;
            });

            // T6 — v1.0.9 loc differentiation: the campfire option + shop title resolve to a relic-explicit
            // "유물 재련" / "Reforge Relic" / "重铸遗物" via the LIVE loc tables — never the bare "재련"
            // that collided with the game's card upgrade (Smith). Drives the real EnsureLoc() merge.
            Test("T6 loc differentiation (v1.0.9)", () =>
            {
                var ok = new[] { "유물 재련", "重铸遗物", "Reforge Relic" };
                string title = ForgeLoc.Ui("SHOP_REFORGE_TITLE");        // relic_forge table
                if (Array.IndexOf(ok, title) < 0) return $"shop title '{title}' not relic-explicit";
                RestSiteReforgeSupport.EnsureLoc();                       // production merge into rest_site_ui
                var table = MegaCrit.Sts2.Core.Localization.LocManager.Instance?.GetTable("rest_site_ui");
                string opt = table != null && table.HasEntry("OPTION_REFORGE.name")
                    ? table.GetRawText("OPTION_REFORGE.name") : "(missing)";
                if (Array.IndexOf(ok, opt) < 0) return $"campfire option '{opt}' not relic-explicit";
                W($"loc ok: title='{title}', campfire='{opt}'");
                return null;
            });

            W($"=== solo test done: {_pass} passed, {_fail} failed ===");
            Flush(_fail == 0);
        }
        catch (Exception e) { W("battery exception: " + e); Flush(false); }
    }

    private static void Test(string name, Func<string?> body)
    {
        try
        {
            string? err = body();
            if (err == null) { _pass++; W($"PASS  {name}"); }
            else { _fail++; W($"FAIL  {name}: {err}"); }
        }
        catch (Exception e) { _fail++; W($"FAIL  {name}: EX {e.Message}"); }
    }

    private static Prefix? FirstNumericPrefix()
    {
        foreach (var n in new[] { "Forceful", "Superior", "Zealous", "Keen", "Legendary", "Godly" })
        {
            var p = PrefixTable.ByName(n);
            if (p != null && p.PowerPct > 0 && !p.Amplify && !p.IsCompanionPrefix) return p;
        }
        return null;
    }

    private static RelicModel? FirstNumericRelic()
    {
        // Direct type lookup (ModelDb.Relic<T> -> flat _contentById dict), NOT ModelDb.AllRelics — the
        // latter is built from character relic pools, which a workshop custom-character mod can break.
        // Both have a numeric Block var to scale; whichever this build registers is used.
        try { return ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.Anchor>().ToMutable(); } catch { }
        try { return ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.OrnamentalFan>().ToMutable(); }
        catch (Exception e) { W("relic fetch failed: " + e.Message.Split('\n')[0]); return null; }
    }

    /// <summary>Save the root viewport to selftest.ui.&lt;name&gt;.png (visual evidence, no display needed).</summary>
    private static void Shot(string name)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            var img = tree.Root.GetTexture()?.GetImage();
            if (img == null) { W($"shot {name}: no image"); return; }
            var err = img.SavePng(Path.Combine(ModDir(), $"selftest.ui.{name}.png"));
            W($"shot {name}: {err}");
        }
        catch (Exception e) { W($"shot {name} failed: {e.Message}"); }
    }

    private static void W(string line) { _out.AppendLine(line); MainFile.Logger.Info($"[{MainFile.ModId}] SOLO | {line}"); }

    private static void Flush(bool ok)
    {
        _done = true;
        _out.Insert(0, (ok ? "RESULT: OK" : "RESULT: FAIL") + $" ({_pass} pass / {_fail} fail)\n");
        try { File.WriteAllText(Path.Combine(ModDir(), "selftest.sp.txt"), _out.ToString()); } catch { }
    }
}

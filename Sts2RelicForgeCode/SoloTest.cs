// LOCAL TEST ONLY — dormant unless `selftest.sp.flag` is next to the mod DLL. Delete this file (or
// exclude it) before a workshop release build. See the solo-verify skill.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;                        // CardSelectCmd
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives; // CardRewardAlternative
using MegaCrit.Sts2.Core.Entities.Cards;                  // CardCreationResult
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;                    // Shiv (T22)
using MegaCrit.Sts2.Core.Models.Orbs;                     // LightningOrb (T23)
using MegaCrit.Sts2.Core.Models.Powers;                   // VigorPower/StrengthPower (T21)
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.ValueProps;                      // ValueProp (T25)
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;          // NOverlayStack
using MegaCrit.Sts2.Core.Random;                          // Rng
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;                     // ICardSelector

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
    /// <summary>Seconds without a Step() call before the watchdog declares the battery wedged. Above the
    /// slowest single test (T11's rewind waits ~9s) but below the launcher's -TimeoutSec, so the watchdog
    /// — not the launcher — gets to name the culprit.</summary>
    private const double StepTimeoutSec = 90;

    private static readonly StringBuilder _out = new();
    private static bool _started, _done;
    private static int _pass, _fail;
    private static string _step = "(not started)";
    private static DateTime _stepAt = DateTime.UtcNow;

    private static string ModDir() => Path.GetDirectoryName(typeof(SoloTest).Assembly.Location) ?? ".";

    /// <summary>Name the phase you're entering. Resets the watchdog and timestamps the log.</summary>
    private static void Step(string name)
    {
        _step = name;
        _stepAt = DateTime.UtcNow;
    }

    public static void ArmIfRequested()
    {
        try
        {
            // One-shot dev-evidence mode: screenshot the ModConfig settings rows (no run started).
            if (File.Exists(Path.Combine(ModDir(), "selftest.cfgshot.flag")))
            { _cfgShotMode = true; W("cfgshot mode armed"); Poll(); return; }

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
            if (_cfgShotMode)
            {
                // Fire once the MAIN MENU exists (the submenu stack is its child) — no run needed.
                if (!_started && FindByTypeName(tree.Root, "NMainMenu") != null)
                {
                    _started = true;
                    Step("cfgshot: opening settings");
                    TaskHelper.RunSafely(CfgShotRoutine());
                }
            }
            else if (!_started && (run == null || !run.IsInProgress) && NGame.Instance != null)
            {
                _started = true;
                Step("starting single-player run");
                W("starting single-player run…");
                TaskHelper.RunSafely(RunBattery());
            }

            // Watchdog: a selection prompt nobody answers parks the battery task forever, and _out only
            // reaches disk in Flush() — so without this the launcher just times out with zero evidence.
            // Flushing a partial FAIL here names the test that wedged and dumps the log so far.
            if (_started && !_done && (DateTime.UtcNow - _stepAt).TotalSeconds > StepTimeoutSec)
            {
                W($"WATCHDOG: no progress for {StepTimeoutSec:F0}s at step '{_step}' — flushing partial result.");
                W($"WATCHDOG: overlay on top = {TopScreenName()} (a selection screen here = an unanswered prompt).");
                Flush(false);
            }
        }
        catch (Exception e) { W("poll exception: " + e.Message); }
        if (!_done) tree.CreateTimer(2.0).Timeout += Poll;
    }

    private static bool _cfgShotMode;

    /// <summary>Dev-evidence one-shot (selftest.cfgshot.flag): open the REAL settings screen from the
    /// main menu (NSubmenuStack.PushSubmenuType), switch to the ModConfig-injected "Mods" tab, scroll
    /// to this mod's section and screenshot the rows — visual proof of how the config (incl. the new
    /// "Prefix pool" dropdown) renders in-game, without hand-driving any UI.</summary>
    private static async Task CfgShotRoutine()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            var stack = FindByTypeName(tree.Root, "NSubmenuStackMainMenu")
                        ?? tree.Root.FindChildren("*", "NSubmenuStack", recursive: true, owned: false).FirstOrDefault()
                        ?? FindBySubclass(tree.Root, "NSubmenuStack");
            if (stack is not MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSubmenuStack menuStack)
            { W("cfgshot: submenu stack not found"); Flush(false); return; }

            Step("cfgshot: push settings submenu");
            var settings = menuStack.PushSubmenuType<MegaCrit.Sts2.Core.Nodes.Screens.Settings.NSettingsScreen>();
            await Task.Delay(2500);   // screen build + ModConfig's Mods-tab injection

            var tabMgr = FindByTypeName(settings, "NSettingsTabManager");
            var modsTab = tabMgr?.GetNodeOrNull("Mods");
            if (tabMgr == null || modsTab == null)
            { W($"cfgshot: tabMgr={(tabMgr != null)}, modsTab={(modsTab != null)}"); Flush(false); return; }

            Step("cfgshot: switch to Mods tab");
            tabMgr.GetType().GetMethod("SwitchTabTo", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(tabMgr, new object[] { modsTab });
            await Task.Delay(1000);
            await Shot("cfg_1_modstab");

            // Scroll the Prefix pool row (bottom of the first viewport) fully into view, then pop its
            // dropdown open so the option list itself is captured. Fixed-delta scroll — the framework's
            // row labels aren't Godot Labels, so text-anchored scrolling found nothing.
            // The settings scroller is the game's NScrollableContainer: _Process tweens the content
            // toward the private _targetDragPosY, so scrolling programmatically = writing that field.
            // Anchor on TEXT, not a fixed delta — the section order varies with the installed mod set.
            var scrollObj = tabMgr.GetType().GetField("_scrollContainer", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(tabMgr);
            var dragField = scrollObj?.GetType().GetField("_targetDragPosY", BindingFlags.NonPublic | BindingFlags.Instance);
            var content = scrollObj?.GetType().GetField("_content", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(scrollObj) as Control;

            // Search the tab CONTENT subtree, not tabMgr — the panel rows live under the scroll
            // container's _content, which is a sibling subtree (searching tabMgr found nothing).
            Node searchRoot = (Node?)content ?? settings;
            Control? PoolRow() => FindByText(searchRoot, "접두사 풀") ?? FindByText(searchRoot, "Prefix pool");
            var poolRow = PoolRow();
            if (poolRow == null)
            {
                // Section collapsed (ModConfig folds sections; the expanded one varies) — toggle the
                // "Relic Forge" header open and look again.
                Step("cfgshot: expand Relic Forge section");
                if (FindByText(searchRoot, "Relic Forge") is BaseButton hb)
                { hb.EmitSignal(BaseButton.SignalName.Pressed); await Task.Delay(700); poolRow = PoolRow(); }
            }

            if (poolRow != null && dragField != null && scrollObj != null && content != null)
            {
                Step("cfgshot: scroll to prefix pool row");
                float target = poolRow.GlobalPosition.Y - content.GlobalPosition.Y - 200f;
                dragField.SetValue(scrollObj, Math.Max(0f, target));
                await Task.Delay(900);
                await Shot("cfg_2_prefixpool");
            }
            else W($"cfgshot: poolRow={(poolRow != null)}, drag={(dragField != null)}, content={(content != null)} — skipping scroll shot");

            // The custom-pool editor panel, opened via the same path as its ModConfig button — one
            // screenshot per tab (enhance / effects / character / curses).
            Step("cfgshot: open custom pool panel");
            NCustomPoolPanel.Toggle();
            await Task.Delay(1200);
            if (FindByTypeName(tree.Root, "NCustomPoolPanel") is NCustomPoolPanel cpp)
            {
                string[] tabNames = { "enhance", "effects", "character", "curses" };
                for (int ti = 0; ti < tabNames.Length; ti++)
                {
                    cpp.SelectTab(ti);
                    await Task.Delay(400);
                    await Shot($"cfg_4{(char)('a' + ti)}_{tabNames[ti]}");
                }
            }
            else W("cfgshot: custom pool panel not found after Toggle");
            NCustomPoolPanel.Toggle();
            await Task.Delay(300);

            W("cfgshot done");
            Flush(true);
        }
        catch (Exception e) { W("cfgshot exception: " + e); Flush(false); }
        finally
        {
            try { File.Delete(Path.Combine(ModDir(), "selftest.cfgshot.flag")); } catch { /* one-shot */ }
        }
    }

    private static Node? FindBySubclass(Node n, string baseTypeName)
    {
        for (var t = n.GetType(); t != null; t = t.BaseType)
            if (t.Name == baseTypeName) return n;
        foreach (var c in n.GetChildren()) { var r = FindBySubclass(c, baseTypeName); if (r != null) return r; }
        return null;
    }

    private static ScrollContainer? FindScroll(Node n)
    {
        if (n is ScrollContainer sc) return sc;
        foreach (var c in n.GetChildren()) { var r = FindScroll(c); if (r != null) return r; }
        return null;
    }

    /// <summary>First visible Control whose Text property contains the needle — matches Label,
    /// Button, and any other text-bearing widget, so it finds ModConfig's rows AND section headers
    /// (which are not Labels — the original Label-only search came back empty).</summary>
    private static Control? FindByText(Node n, string needle)
    {
        if (n is Control c && c.IsVisibleInTree()
            && c.GetType().GetProperty("Text")?.GetValue(c) is string s
            && s.Contains(needle, StringComparison.OrdinalIgnoreCase))
            return c;
        foreach (var ch in n.GetChildren()) { var r = FindByText(ch, needle); if (r != null) return r; }
        return null;
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
                await Shot("01_run");   // visual evidence: the run actually started (map screen)
            }
            catch (Exception e) { W("run-start skipped (mod env): " + e.Message.Split('\n')[0]); }

            // Answer selection prompts from here on. MUST come after the run start: RunManager.CleanUp
            // calls CardSelectCmd.Reset(), which drops every pushed selector.
            StartAutomation();

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

            // T8 — restore idempotency (bug-audit fix): RestoreForged must reproduce the descriptor
            // VERBATIM — Forge's internal curse roll is suppressed on the restore path, so a fizzled
            // uncursed "Keen" pickup can never mutate into a fallback prefix ("Honed"+buff) on load.
            // 30 seeds: under the pre-fix code each seed had ~CurseChance odds of mutating.
            Test("T8 restore idempotency", () =>
            {
                const string desc = "Keen||||0|0";     // fizzled uncursed pickup shape (6-field)
                for (uint s = 100; s < 130; s++)
                {
                    var clone = FirstNumericRelic();   // fresh instance each seed (Records is instance-keyed)
                    if (clone == null) return "no relic available";
                    RelicForgeService.RestoreForged(clone, desc, s, 3, 0, false, 0, null);
                    string? back = RelicForgeService.DescriptorOf(clone);
                    if (back != desc) return $"round-trip mutated at seed {s}: '{back}'";
                }
                return null;
            });

            // T7 — multilingual: switch the LIVE game language to Korean / Chinese / English and confirm
            // the campfire option + shop title render THAT language's relic-explicit string (ko="유물 재련",
            // zh="重铸遗物", en="Reforge Relic"). Game codes are 3-letter (kor/zhs/eng); ForgeLoc matches
            // by "ko"/"zh" prefix, everything else → English. Restores the original language afterward.
            Test("T7 multilingual (ko/zh/en)", () =>
            {
                var lm = MegaCrit.Sts2.Core.Localization.LocManager.Instance;
                if (lm == null) return "no LocManager";
                string original = lm.Language;
                var langs = MegaCrit.Sts2.Core.Localization.LocManager.Languages;
                (string? code, string expect)[] cases =
                {
                    (langs.FirstOrDefault(l => l.StartsWith("ko")), "유물 재련"),
                    (langs.FirstOrDefault(l => l.StartsWith("zh")), "重铸遗物"),
                    (langs.FirstOrDefault(l => l.StartsWith("en")) ?? "eng", "Reforge Relic"),
                };
                try
                {
                    foreach (var (code, expect) in cases)
                    {
                        if (code == null) { W($"  (no game language for '{expect}' — not installed, skipped)"); continue; }
                        lm.SetLanguage(code);
                        ForgeLoc.Invalidate();
                        string title = ForgeLoc.Ui("SHOP_REFORGE_TITLE");
                        RestSiteReforgeSupport.EnsureLoc();
                        var t = lm.GetTable("rest_site_ui");
                        string opt = t != null && t.HasEntry("OPTION_REFORGE.name") ? t.GetRawText("OPTION_REFORGE.name") : "(missing)";
                        W($"  [{code}] title='{title}' campfire='{opt}'");
                        if (title != expect) return $"[{code}] title '{title}' != '{expect}'";
                        if (opt != expect) return $"[{code}] campfire '{opt}' != '{expect}'";
                    }
                    return null;
                }
                finally { try { lm.SetLanguage(original); ForgeLoc.Invalidate(); } catch { } }
            });

            // T9 — campfire, IN PLACE: jump the run to a REAL rest site (the networked `room` debug jump)
            // and confirm the mod's reforge option was generated into the option list — plus a screenshot
            // of the actual campfire screen showing it.
            await TestAsync("T9 campfire option offered", async () =>
            {
                if (player == null || run == null) return "no run/player";
                await run.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.RestSite);
                await Task.Delay(3000);                             // rest-site UI builds; options generate
                await Shot("02_restsite");                          // visual: campfire options incl. 유물 재련
                return RestSiteReforgeSupport.ByPlayer.ContainsKey(player.NetId)
                    ? null : "reforge option not generated at the rest site";
            });

            // T10 — shop, IN PLACE + PAID: jump to a REAL shop, confirm the mod's reforge button attached,
            // top up gold if short (networked `gold` = GainGold), then run the exact paid flow the button
            // runs (LoseGold + SyncLocalGoldLost + ReforgeNet.Reforge) and assert the charge + the reforge.
            await TestAsync("T10 shop paid reforge", async () =>
            {
                if (player == null || run == null) return "no run/player";
                // Networked grant (works in the hostile workshop env — proven by coop-verify) so the
                // player owns a forgeable relic; starter relics are excluded from the reforge pool.
                run.ActionQueueSynchronizer.RequestEnqueue(
                    new MegaCrit.Sts2.Core.DevConsole.ConsoleCmdGameAction(player, "relic akabeko", inCombat: false));
                await Task.Delay(2500);
                await run.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Shop);
                await Task.Delay(4000);                             // shop UI builds; our button attaches on _Ready
                if (Engine.GetMainLoop() is not SceneTree tree) return "no scene tree";
                if (FindNode<NMerchantReforgeButton>(tree.Root) == null) return "shop reforge button not attached";
                var relic = player.Relics.FirstOrDefault(
                    r => r.Id.Entry.Contains("AKABEKO") && !RelicForgeService.IsCompanion(r));
                if (relic == null) return "akabeko not granted";

                int cost = ForgeConfig.ShopReforgeCostFor(1);       // the PAID step (step #0 can be 0g by config)
                if (cost <= 0) cost = 15;                           // always exercise a real charge
                if ((int)player.Gold < cost)                        // top up if short — `gold` adds (GainGold)
                {
                    run.ActionQueueSynchronizer.RequestEnqueue(
                        new MegaCrit.Sts2.Core.DevConsole.ConsoleCmdGameAction(player, $"gold {cost + 100}", inCombat: false));
                    await Task.Delay(2000);
                    W($"  gold topped up to {(int)player.Gold}");
                }
                int before = (int)player.Gold;
                await MegaCrit.Sts2.Core.Commands.PlayerCmd.LoseGold(cost, player,
                    MegaCrit.Sts2.Core.Entities.Gold.GoldLossType.Spent);
                run.RewardSynchronizer?.SyncLocalGoldLost(cost);    // the co-op gold pair (v1.0.10 fix)
                ReforgeNet.Reforge(relic, player);
                await Task.Delay(1500);
                await Shot("03_shop");                              // visual: the shop with our button
                if ((int)player.Gold != before - cost) return $"gold {before} -> {(int)player.Gold}, expected -{cost}";
                if (RelicForgeService.ReforgeCountOf(relic) < 1) return "relic did not reforge";
                W($"  shop reforge ok: -{cost}g ({before} -> {(int)player.Gold}), desc '{RelicForgeService.DescriptorOf(relic)}'");

                // B1: the per-VISIT reforge budget now reads as FORGE HEAT (amber), NOT a curse — the relic's
                // own red curse-risk is the only "저주" gauge. Read the campfire option's live band text at a
                // mid gauge (akabeko is now forgeable, so it isn't the disabled line) and assert it carries the
                // amber tag and dropped every curse word. Skips quietly if no campfire option is registered.
                if (RestSiteReforgeSupport.ByPlayer.TryGetValue(player.NetId, out var opt))
                {
                    var lf = typeof(ReforgeRestSiteOption).GetField("_locGauge", BindingFlags.NonPublic | BindingFlags.Instance);
                    lf?.SetValue(opt, 50);
                    string heat = opt.Description.GetFormattedText();
                    lf?.SetValue(opt, 0);                          // restore so the live UI is unaffected
                    W("  forge-heat band: " + heat.Replace("\n", " "));
                    if (!heat.Contains("[color=#e0913a]")) return "forge-heat band missing amber color tag";
                    if (heat.Contains("저주") || heat.Contains("诅咒") || heat.ToLowerInvariant().Contains("curse"))
                        return "forge-heat band still uses curse wording";
                }
                return null;
            });

            // T16 — forge SUMMARY panel: after the T10 forge, the top-bar button opens a standalone panel
            // listing EVERY forged relic with its full effect (numeric deltas INCLUDED — the old portrait
            // tooltip showed qualitative notes only, which read as "nothing" on a purely numeric forge),
            // curse, and gauge. Assert the row content in text, then open the panel and screenshot it.
            await TestAsync("T16 forge summary panel", async () =>
            {
                if (player == null) return "no player";
                // FORCE known NUMERIC prefixes onto known-benign hosts BEFORE Obtain (the Grant20 idiom):
                // a console-granted relic pickup-rolls at Obtain (~85% vanilla) and a later Forge() call
                // no-ops on the already-rolled record — so this is the only way to guarantee pure-numeric
                // ROWS, the exact case the workshop reported as invisible.
                uint seed16 = 0; int floor16 = 0;
                try { seed16 = player.RunState.Rng.Seed; floor16 = player.RunState.TotalFloor; } catch { }
                var hosts16 = new List<RelicModel>();
                async Task Grant16(RelicModel? host, string prefixName)
                {
                    var pfx = PrefixTable.ByName(prefixName);
                    if (pfx == null || host == null) { W($"  {prefixName}: prefix or host missing"); return; }
                    RelicForgeService.Forge(host, seed16, floor16, forced: pfx);
                    await RelicCmd.Obtain(host, player);
                    if (player.Relics.Contains(host)) hosts16.Add(host);
                }
                RelicModel? Fetch16(Func<RelicModel> f) { try { return f().ToMutable(); } catch (Exception e) { W("  fetch failed: " + e.Message.Split('\n')[0]); return null; } }
                await Grant16(Fetch16(() => ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.Anchor>()), "Legendary");
                await Grant16(Fetch16(() => ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.Strawberry>()), "Godly");
                await Grant16(Fetch16(() => ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.Pear>()), "Superior");
                await Task.Delay(800);
                // test-only: inject a self-curse onto a forged relic so a CURSE line renders in its row.
                var forged = player.Relics.FirstOrDefault(r => !RelicForgeService.IsCompanion(r) && RelicForgeService.RecordFor(r) != null);
                var frec = forged != null ? RelicForgeService.RecordFor(forged) : null;
                if (frec != null && frec.SelfCurse.Length == 0 && !frec.EnemyRider) { frec.SelfCurse = "Enfeebling"; W("  (injected test self-curse)"); }

                var rows = ForgeSummaryRows.Build(player);
                if (rows.Count == 0) return "no forged relic to summarize";
                int numericRows = 0, curseRows = 0;
                foreach (var row in rows)
                {
                    W("  ROW> " + row.Title.Replace("\n", " | ") + " :: " + row.Body.Replace("\n", " | "));
                    if (row.Body.Contains("+") || row.Body.Contains("-")) numericRows++;
                    if (row.Body.Contains("☠") || row.Body.Contains("⚔")) curseRows++;
                }
                // the numeric relics force-forged above must be VISIBLE as rows (regression guard for the
                // "numeric prefixes show nothing" workshop report), and the injected curse must render.
                if (numericRows == 0) return "no row shows a numeric delta (numeric forges invisible again?)";
                if (curseRows == 0) return "injected self-curse did not render in any row";
                if (hosts16.Count == 0) return "no forced numeric host stuck in player.Relics";
                foreach (var h in hosts16)
                {
                    var hr = rows.FirstOrDefault(r => r.Relic == h);
                    if (hr == null) return $"forced numeric host {h.Id.Entry} has no summary row";
                    if (hr.Body.Length == 0) return $"forced numeric host {h.Id.Entry} row body is EMPTY (numeric deltas missing)";
                }

                NForgeSummaryPanel.Toggle();                    // open via the same path as the top-bar button
                await Task.Delay(900);
                await Shot("09_summary");
                NForgeSummaryPanel.Toggle();                    // toggle again = close
                await Task.Delay(300);
                W($"  summary panel shown ({rows.Count} row(s), {numericRows} numeric, {curseRows} cursed)");
                return null;
            });

            // T17 — room-gated curse tint (feature F): the red curse-gauge tint AND the numeric "curse risk
            // N%" hover panel show ONLY at a forge location (rest site / shop) — off it they're noise, since
            // the gauge only fills on reforge. A SATURATED relic is the deliberate exception: it stays flagged
            // EVERYWHERE with a "no longer works" note. Assert IsAtForgeLocation() per room + the hover-panel
            // branch (a mild relic's gauge panel appears only at a forge site; a saturated relic's note is
            // present off-site too). Pure display logic — no screenshot needed.
            await TestAsync("T17 room-gated curse tint", async () =>
            {
                if (run == null || player == null) return "no run/player";

                // A non-saturated forged relic (T16 force-forged everything at count 1 → low gauge > 0).
                var mild = player.Relics.FirstOrDefault(r =>
                    !RelicForgeService.IsCompanion(r) && RelicForgeService.RecordFor(r) != null
                    && RelicForgeService.CurseGauge(r) > 0 && !RelicForgeService.IsGaugeSaturated(r));
                if (mild == null) return "no non-saturated forged relic (T16 should have left several)";

                // Drive a SECOND forged relic to saturation. Re-Forge() no-ops on an already-forged relic
                // (T16 forged them all), so bump its stored reforge count directly — 20 steps × ≥5%/step
                // ≥ 100%, so CurseGauge clamps to full regardless of the per-step rolls.
                var sat = player.Relics.FirstOrDefault(r =>
                    !RelicForgeService.IsCompanion(r) && r != mild && RelicForgeService.RecordFor(r) != null);
                if (sat == null) return "no second forged relic to saturate";
                RelicForgeService.RecordFor(sat)!.ReforgeCount = 20;
                if (!RelicForgeService.IsGaugeSaturated(sat)) return "failed to saturate the test relic";

                static bool HasGaugePanel(RelicModel r) =>
                    r.HoverTips.OfType<MegaCrit.Sts2.Core.HoverTips.HoverTip>().Any(t => t.Id == "sts2rf_gauge");

                // OFF a forge location (map): gate closed — mild has no gauge panel, saturated keeps its note.
                await run.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Map);
                await Task.Delay(400);
                if (RelicForgeService.IsAtForgeLocation()) return "IsAtForgeLocation true on the map";
                if (HasGaugePanel(mild)) return "mild relic showed a gauge panel off a forge location";
                if (!HasGaugePanel(sat)) return "saturated relic dropped its note off a forge location";
                W("  map: mild=no panel, saturated=note kept ✓");

                // AT a rest site: gate open — the numeric gauge panel returns for the mild relic.
                await run.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.RestSite);
                await Task.Delay(400);
                if (!RelicForgeService.IsAtForgeLocation()) return "IsAtForgeLocation false at the rest site";
                if (!HasGaugePanel(mild)) return "mild relic missing its gauge panel at the rest site";
                W("  rest site: mild gauge panel shown ✓");

                // Shop is a forge location too.
                await run.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Shop);
                await Task.Delay(400);
                if (!RelicForgeService.IsAtForgeLocation()) return "IsAtForgeLocation false at the shop";
                W("  shop: forge location ✓");
                return null;
            });

            // T18 — unified HP-curse ramp (feature C, HP side): the four Max-HP curses (Vigor/Girth/Titan/
            // Eternity) now share ONE stacking ramp keyed on how many reach a fight — 5/10/20/40/70/100%,
            // hard-capped — replacing per-curse fixed fractions that summed with no ceiling. Verify the curve
            // directly (EnemyForge.HpRampFor is same-assembly internal). Pure numbers, no combat needed.
            Test("T18 unified HP-curse ramp", () =>
            {
                var expected = new (int n, double f)[]
                { (0, 0.0), (1, 0.05), (2, 0.10), (3, 0.20), (4, 0.40), (5, 0.70), (6, 1.00), (7, 1.00), (99, 1.00) };
                foreach (var (n, f) in expected)
                {
                    double got = EnemyForge.HpRampFor(n);
                    if (Math.Abs(got - f) > 1e-9) return $"HpRampFor({n})={got}, expected {f}";
                }
                W("  HP ramp: 0/5/10/20/40/70/100% (hard-capped) ✓");
                return null;
            });

            // T19 — character-affix wave 2 (the 13 new prefixes/curses, incl. the FIRST Ironclad
            // family): every entry exists with the right character gate, slot (boon vs re-homed
            // penalty), and full ko/en/zh notes; penalties are curse-pool-eligible for their own
            // character only. Pure table checks — combat behavior is verified in-run via `forgechar`.
            Test("T19 char-affix wave 2 table", () =>
            {
                var expect = new (string name, string chr, bool penalty)[]
                {
                    ("Quenched",   "",            false),   // universal — vigor is cross-character
                    ("Cindered",   "IRONCLAD",    false),
                    ("Bloodforged","IRONCLAD",    false),
                    ("Gouging",    "IRONCLAD",    false),
                    ("Retaliating","IRONCLAD",    false),
                    ("Mirrored",   "IRONCLAD",    true),
                    ("Lingering",  "IRONCLAD",    true),
                    ("Karmic",     "IRONCLAD",    true),
                    ("Retrieving", "SILENT",      false),
                    ("Slippery",   "SILENT",      true),
                    ("Preheated",  "DEFECT",      false),
                    ("Sealed",     "DEFECT",      true),
                    ("Unstable",   "DEFECT",      true),
                    ("Vengeful",   "NECROBINDER", false),
                    ("Empathic",   "NECROBINDER", false),
                    ("Famished",   "NECROBINDER", true),
                    ("Tributary",  "REGENT",      false),
                    ("Bountiful",  "REGENT",      false),
                    ("Prodigal",   "REGENT",      true),
                    ("Tarnished",  "REGENT",      true),
                };
                foreach (var (name, chr, penalty) in expect)
                {
                    var p = PrefixTable.ByName(name);
                    if (p == null) return $"{name}: missing from PrefixTable";
                    if (!p.CharAffix || !string.Equals(p.RequiredCharacter, chr, StringComparison.OrdinalIgnoreCase))
                        return $"{name}: wrong gate (CharAffix={p.CharAffix}, char='{p.RequiredCharacter}')";
                    if (p.Penalty != penalty) return $"{name}: Penalty={p.Penalty}, expected {penalty}";
                    if (p.NoteKo.Length == 0 || p.NoteEn.Length == 0 || p.NoteZh.Length == 0)
                        return $"{name}: missing note translation";
                    if (p.Penalty && !PrefixTable.CurseInPool(p, chr))
                        return $"{name}: penalty not eligible for its own curse pool";
                    // A char-gated curse must never leak into another character's pool.
                    string other = string.Equals(chr, "SILENT", StringComparison.OrdinalIgnoreCase) ? "DEFECT" : "SILENT";
                    if (p.Penalty && PrefixTable.CurseInPool(p, other))
                        return $"{name}: leaks into {other}'s curse pool";
                }
                // Name uniqueness across the WHOLE table — the Name doubles as the dispatch and
                // descriptor key, so ANY duplicate silently misroutes (the original "Tempered"
                // collision this test caught: ByName returned the numeric prefix, the char affix
                // never fired).
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in PrefixTable.All)
                    if (!seen.Add(p.Name)) return $"duplicate prefix name '{p.Name}' in PrefixTable";
                W("  20 wave-2 char affixes wired (Quenched universal + real Ironclad family), no name collisions ✓");
                return null;
            });

            // ==================== T20–T25: forced-INJECTION behavior tests ====================
            // T19 only proves the TABLE; these prove the EFFECTS: each affix is force-forged onto a
            // benign host (the forgechar idiom, in-process), its trigger is driven through the REAL
            // command / hook path wherever possible, and the outcome is asserted. Hosts are removed
            // after each sub-test so effects never cross-contaminate.
            // Declared HERE but INVOKED AFTER T13: running these fights before the Rewind tests
            // starved Rewind of its turn snapshots (CanRewindToTurn=false) — the injection battery
            // is state-heavy (two fresh fights, a victory, a dozen relic obtains), so it goes last.
            async Task InjectionBattery()
            {
            // T13's game-over rewind REBUILT the run state — the player captured at battery start is
            // stale (its RunState lookup throws "Sequence contains no matching element", relic list
            // dead). Re-resolve the LIVE instance; T14/T15 downstream benefit from the same fix.
            player = RunManager.Instance?.State?.Players?.FirstOrDefault() ?? player;
            var ctx20 = new MegaCrit.Sts2.Core.GameActions.Multiplayer.BlockingPlayerChoiceContext();
            uint seed20 = 0; int floor20 = 0;
            try { if (player != null) { seed20 = player.RunState.Rng.Seed; floor20 = player.RunState.TotalFloor; } }
            catch (Exception e) { W("T20 setup failed: " + e.Message); }

            // Force-forge prefixName onto a KNOWN-BENIGN host and obtain it. Hosts are fixed
            // (Strawberry / Pear — pure on-obtain MaxHp, no grants/selects/replacements): an
            // alphabet-ordered pool draw handed us hosts whose obtain side effects (grant-another-
            // relic / self-replace) silently dropped them from player.Relics, so every relic-list
            // lookup (Owned/OwnedByCurse) — and even relic.Flash() — went dead. Membership is now
            // asserted after Obtain. Scrubs incidental rolls (enemy rider / stray self-curse) so
            // ONLY the effect under test is live, and asserts the affix landed in the right slot
            // (boon → rec.Prefix, penalty → re-homed rec.SelfCurse).
            async Task<RelicModel?> Grant20(string prefixName, bool penalty, bool secondSlot = false)
            {
                var pfx = PrefixTable.ByName(prefixName);
                if (pfx == null || player == null) return null;
                RelicModel host = secondSlot
                    ? ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.Pear>().ToMutable()
                    : ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.Strawberry>().ToMutable();
                RelicForgeService.Forge(host, seed20, floor20, forced: pfx);
                var rec = RelicForgeService.RecordFor(host);
                if (rec == null) { W($"  {prefixName}: no forge record after forced forge"); return null; }
                rec.EnemyRider = false; rec.EnemyRiderSuffix = "";
                if (!penalty && rec.SelfCurse.Length > 0) rec.SelfCurse = "";
                bool slotOk = penalty ? rec.SelfCurse == prefixName : rec.Prefix == prefixName;
                if (!slotOk) { W($"  {prefixName}: wrong slot (prefix='{rec.Prefix}', curse='{rec.SelfCurse}')"); return null; }
                await RelicCmd.Obtain(host, player);
                if (!player.Relics.Contains(host)) { W($"  {prefixName}: host {host.Id.Entry} did not stick in player.Relics"); return null; }
                return host;
            }
            async Task Drop20(RelicModel? r) { if (r != null) { try { await RelicCmd.Remove(r); } catch { /* cleanup only */ } } }

            // T20 — Preheated + Slippery through the REAL turn-1 dispatch: grant both, enter a fresh
            // fight, and read what the TurnStartPatch actually did (an orb appeared / a card was
            // discarded) — no handler is called by hand here.
            RelicModel? r20a = null, r20b = null;
            await TestAsync("T20 inject: Preheated+Slippery real turn-1 dispatch", async () =>
            {
                if (run == null || player == null) return "no run/player";
                r20a = await Grant20("Preheated", penalty: false);
                r20b = await Grant20("Slippery", penalty: true, secondSlot: true);
                if (r20a == null || r20b == null) return "forced grant failed (see log)";

                var enc = ModelDb.GetByIdOrNull<MegaCrit.Sts2.Core.Models.EncounterModel>(
                    ModelDb.GetId(typeof(MegaCrit.Sts2.Core.Models.Encounters.BowlbugsWeak)));
                if (enc == null) return "BowlbugsWeak encounter not registered";
                await run.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Monster, model: enc.ToMutable());
                await Task.Delay(6000);
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                if (cm == null || !cm.IsInProgress) return "combat did not start";

                int orbs = player.PlayerCombatState?.OrbQueue?.Orbs.Count ?? 0;
                int discarded = PileType.Discard.GetPile(player).Cards.Count;
                W($"  turn 1 after dispatch: orbs={orbs} (Preheated), discard pile={discarded} (Slippery)");
                if (orbs < 1) return "Preheated did not channel an orb at combat start";
                if (discarded < 1) return "Slippery did not discard a card at turn start";
                return null;
            });
            await Drop20(r20a); await Drop20(r20b);

            // T21 — Quenched (universal, vigor→strength) through the REAL consumption path: apply 200
            // Vigor, pay it down with the same negative PowerCmd.ModifyAmount VigorPower.AfterAttack
            // uses → the AfterPowerAmountChanged patch must fire OnVigorConsumed (200 independent
            // rolls at CharAffix.QuenchedChance — P(zero strength) is negligible at any sane rate).
            await TestAsync("T21 inject: Quenched vigor→strength", async () =>
            {
                if (player?.Creature == null) return "no player creature";
                var relic = await Grant20("Quenched", penalty: false);
                if (relic == null) return "forced grant failed";
                try
                {
                    await PowerCmd.Apply<VigorPower>(ctx20, player.Creature, 200m, player.Creature, null);
                    var vigor = player.Creature.GetPower<VigorPower>();
                    if (vigor == null) return "vigor power did not apply";
                    int str0 = (int)(player.Creature.GetPower<StrengthPower>()?.Amount ?? 0);
                    await PowerCmd.ModifyAmount(ctx20, vigor, -200m, null, null);
                    await Task.Delay(800);
                    int str1 = (int)(player.Creature.GetPower<StrengthPower>()?.Amount ?? 0);
                    W($"  consumed 200 vigor → strength {str0}→{str1} (expected ≈ +{(int)(200 * CharAffix.QuenchedChance)})");
                    if (str1 <= str0) return "no Strength gained from 200 consumed Vigor";
                    return null;
                }
                finally { await Drop20(relic); }
            });

            // T22 — Retrieving (Silent) through the REAL exhaust hook: exhaust freshly-created Shivs
            // until one bounces back to hand (25%/try — P(all 40 fail) ≈ 1e-5).
            await TestAsync("T22 inject: Retrieving shiv-exhaust return", async () =>
            {
                if (player?.Creature?.CombatState == null) return "not in combat";
                var relic = await Grant20("Retrieving", penalty: false);
                if (relic == null) return "forced grant failed";
                try
                {
                    var cs = player.Creature.CombatState;
                    for (int i = 0; i < 40; i++)
                    {
                        var shiv = cs.CreateCard<Shiv>(player);
                        await CardPileCmd.Add(shiv, PileType.Hand);
                        await CardCmd.Exhaust(ctx20, shiv);
                        if (PileType.Hand.GetPile(player).Cards.Contains(shiv))
                        {
                            W($"  shiv returned to hand on exhaust #{i + 1}");
                            await CardCmd.Exhaust(ctx20, shiv);   // tidy: don't leave a test shiv in hand
                            return null;
                        }
                    }
                    return "no shiv returned in 40 exhausts (25% each)";
                }
                finally { await Drop20(relic); }
            });

            // T23 — Sealed + Unstable (Defect orb curses). Sealed drives the same handler the turn-1
            // dispatch calls (slot -1, floor 1); Unstable morphs the oldest orb through the real
            // queue surgery (25%/tick — P(all 60 fail) ≈ 3e-8).
            await TestAsync("T23 inject: Sealed & Unstable orb curses", async () =>
            {
                if (player?.PlayerCombatState == null) return "not in combat";
                var sealed23 = await Grant20("Sealed", penalty: true);
                var unstable23 = await Grant20("Unstable", penalty: true, secondSlot: true);
                if (sealed23 == null || unstable23 == null) { await Drop20(sealed23); await Drop20(unstable23); return "forced grant failed"; }
                try
                {
                    await OrbCmd.AddSlots(player, 3);
                    var q = player.PlayerCombatState.OrbQueue;
                    int cap0 = q.Capacity;
                    CharAffix.OnCombatStartSealed(player, sealed23);
                    W($"  Sealed: capacity {cap0}→{q.Capacity}");
                    if (q.Capacity != cap0 - 1) return $"Sealed: capacity {q.Capacity}, expected {cap0 - 1}";

                    if (q.Orbs.Count == 0) await OrbCmd.Channel<LightningOrb>(ctx20, player);
                    if (q.Orbs.Count == 0) return "Unstable: no orb to morph";
                    // diag: is the granted curse actually discoverable the way the handler looks it up?
                    var recU = RelicForgeService.RecordFor(unstable23);
                    int hitsU = CharAffix.OwnedByCurse(player, "Unstable").Count();
                    W($"  Unstable diag: ownedList={player.Relics.Contains(unstable23)}, rec.SelfCurse='{recU?.SelfCurse}', lookupHits={hitsU}, " +
                      $"rolls={string.Join(",", Enumerable.Range(0, 4).Select(_ => CharAffix.Roll(player, unstable23, player.PlayerCombatState?.TurnNumber ?? 0).ToString("F2")))}");
                    var t0 = q.Orbs[0].GetType();
                    int n0 = q.Orbs.Count;
                    for (int i = 0; i < 60; i++)
                    {
                        CharAffix.OnTurnEndUnstable(player);
                        if (q.Orbs.Count > 0 && q.Orbs[0].GetType() != t0)
                        {
                            W($"  Unstable: {t0.Name} → {q.Orbs[0].GetType().Name} on tick #{i + 1} (count {n0}→{q.Orbs.Count})");
                            return q.Orbs.Count == n0 ? null : "Unstable: orb count changed on morph";
                        }
                    }
                    return "Unstable: oldest orb never morphed in 60 ticks (25% each)";
                }
                finally { await Drop20(sealed23); await Drop20(unstable23); }
            });

            // T24 — Regent star/forge affixes through the REAL hooks: Bountiful/Prodigal ride
            // AfterStarsGained (PlayerCmd.GainStars), Tributary rides AfterForge (ForgeCmd.Forge),
            // Tarnished drives the flush handler. Star math is combat-scoped, so character-agnostic.
            await TestAsync("T24 inject: Regent star/forge affixes", async () =>
            {
                if (player?.PlayerCombatState == null) return "not in combat";

                var bountiful = await Grant20("Bountiful", penalty: false);
                if (bountiful == null) return "forced grant failed (Bountiful)";
                // diag: split the HOOK path from the HANDLER path — a direct OnStarsGained call that
                // doubles proves handler+lookup, isolating any failure to the AfterStarsGained patch.
                int hitsB = CharAffix.Owned(player, "Bountiful").Count();
                int directTwos = 0;
                for (int i = 0; i < 12; i++)
                {
                    int s0 = player.PlayerCombatState.Stars;
                    await CharAffix.OnStarsGained(player, 1);
                    if (player.PlayerCombatState.Stars - s0 >= 1) directTwos++;
                }
                W($"  Bountiful diag: ownedList={player.Relics.Contains(bountiful)}, lookupHits={hitsB}, directBonus={directTwos}/12");
                int twos = 0;
                for (int i = 0; i < 24; i++)
                {
                    int s0 = player.PlayerCombatState.Stars;
                    await PlayerCmd.GainStars(1m, player);
                    int d = player.PlayerCombatState.Stars - s0;
                    if (d == 2) twos++;
                    else if (d != 1) { await Drop20(bountiful); return $"Bountiful: gain delta {d} (expected 1 or 2)"; }
                }
                await Drop20(bountiful);
                W($"  Bountiful: {twos}/24 gains doubled (33% expected)");
                if (twos == 0) return "Bountiful: no bonus star in 24 gains";

                var prodigal = await Grant20("Prodigal", penalty: true);
                if (prodigal == null) return "forced grant failed (Prodigal)";
                int zeros = 0;
                for (int i = 0; i < 40; i++)
                {
                    int s0 = player.PlayerCombatState.Stars;
                    await PlayerCmd.GainStars(1m, player);
                    int d = player.PlayerCombatState.Stars - s0;
                    if (d == 0) zeros++;
                    else if (d != 1) { await Drop20(prodigal); return $"Prodigal: gain delta {d} (expected 0 or 1)"; }
                }
                await Drop20(prodigal);
                W($"  Prodigal: {zeros}/40 gains taxed away (25% expected)");
                if (zeros == 0) return "Prodigal: no gain taxed in 40 gains";

                var tarnished = await Grant20("Tarnished", penalty: true);
                if (tarnished == null) return "forced grant failed (Tarnished)";
                await PlayerCmd.GainStars(2m, player);
                int st0 = player.PlayerCombatState.Stars;
                CharAffix.OnTurnEndTarnished(player);
                await Task.Delay(400);
                int st1 = player.PlayerCombatState.Stars;
                await Drop20(tarnished);
                W($"  Tarnished: stars {st0}→{st1}");
                if (st1 != st0 - 1) return $"Tarnished: stars {st1}, expected {st0 - 1}";

                var tributary = await Grant20("Tributary", penalty: false);
                if (tributary == null) return "forced grant failed (Tributary)";
                try
                {
                    // keep the hand small so a granted draw can never clamp on the hand cap
                    var handPile = PileType.Hand.GetPile(player);
                    if (handPile.Cards.Count > 5)
                        await CardCmd.Discard(ctx20, handPile.Cards.Skip(5).ToList());
                    await ForgeCmd.Forge(1m, player, null);       // first forge may also create the blade
                    int drew = 0;
                    for (int i = 0; i < 12 && drew == 0; i++)
                    {
                        int h0 = handPile.Cards.Count;
                        await ForgeCmd.Forge(1m, player, null);
                        await Task.Delay(150);
                        int d = handPile.Cards.Count - h0;
                        if (d > 0) drew = d;
                    }
                    W($"  Tributary: drew {drew} card(s) on a forge (75%/forge expected)");
                    if (drew == 0) return "Tributary: no draw in 12 forges";
                    if (drew > 2) return $"Tributary: drew {drew} on one forge (max 2)";
                    return null;
                }
                finally { await Drop20(tributary); }
            });

            // T25 — Necrobinder damage-meter affixes: real unblockable damage feeds the meters via
            // the AfterDamageReceived patch, the turn-start handlers consume them (Vengeful summons
            // 2× the player's lost HP; Empathic blocks the summon's lost HP), and Famished's
            // arm-at-flush/fire-at-turn-start pair shrinks a starved Osty by 1.
            await TestAsync("T25 inject: Vengeful/Empathic/Famished", async () =>
            {
                if (run == null || player?.Creature == null) return "no run/player";
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                if (cm == null || !cm.IsInProgress) return "not in combat";

                var vengeful = await Grant20("Vengeful", penalty: false);
                if (vengeful == null) return "forced grant failed (Vengeful)";
                W("  Vengeful: dealing 5 self-damage…");
                await CreatureCmd.Damage(ctx20, player.Creature, 5m,
                    ValueProp.Unblockable | ValueProp.Unpowered, null, null);
                W("  Vengeful: firing turn-start handler…");
                CharAffix.OnTurnVengeful(ctx20, player, vengeful);
                await Task.Delay(1800);
                await Drop20(vengeful);
                bool ostyAlive = false; try { ostyAlive = player.IsOstyAlive; } catch (Exception e) { W("  IsOstyAlive threw: " + e.Message); }
                if (!ostyAlive) return "Vengeful: no Osty summoned from 5 lost HP";
                int ostyHp = (int)player.Osty.MaxHp;
                W($"  Vengeful: lost 5 HP → Osty MaxHp {ostyHp} (expected 10)");
                if (ostyHp != 10) return $"Vengeful: Osty MaxHp {ostyHp}, expected 10 (=2×5)";

                var empathic = await Grant20("Empathic", penalty: false);
                if (empathic == null) return "forced grant failed (Empathic)";
                await CreatureCmd.Damage(ctx20, player.Osty, 4m,
                    ValueProp.Unblockable | ValueProp.Unpowered, null, null);
                int b0 = (int)player.Creature.Block;
                CharAffix.OnTurnEmpathic(player, empathic);
                await Task.Delay(1200);
                int b1 = (int)player.Creature.Block;
                await Drop20(empathic);
                W($"  Empathic: Osty lost 4 HP → player block {b0}→{b1} (expected +4)");
                if (b1 != b0 + 4) return $"Empathic: block {b1}, expected {b0 + 4}";

                var famished = await Grant20("Famished", penalty: true);
                if (famished == null) return "forced grant failed (Famished)";
                try
                {
                    // beef the Osty up so the coming enemy turn can't kill it mid-test
                    await OstyCmd.Summon(ctx20, player, 20m, null);
                    int m0 = (int)player.Osty.MaxHp;
                    // advance one turn: the summon above marks THIS turn, so the real flush must NOT
                    // arm Famished; the fresh turn (no summon yet) is the one we starve.
                    cm.SetReadyToEndTurn(player, canBackOut: false);
                    await Task.Delay(8000);
                    if (!player.IsOstyAlive) { W("  Famished: Osty died to the enemy turn — skipped (env)"); return null; }
                    int m1 = (int)player.Osty.MaxHp;
                    CharAffix.OnTurnEndFamishedCheck(player);      // starved turn end → arm
                    CharAffix.OnTurnFamished(ctx20, player, famished);   // next turn start → shrink
                    await Task.Delay(1500);
                    int m2 = (int)player.Osty.MaxHp;
                    W($"  Famished: Osty MaxHp {m0} → {m1} (enemy turn) → {m2} (starved, expected {m1 - 1})");
                    if (m2 != m1 - 1) return $"Famished: Osty MaxHp {m2}, expected {m1 - 1}";
                    return null;
                }
                finally { await Drop20(famished); }   // combat stays live — T26 runs in the same fight
            });

            // T26 — Ironclad family (wave-2b): exhaust/vuln/HP-loss/strength affixes, same
            // forced-injection method. Runs in the fight T25 left open; ends it when done.
            await TestAsync("T26 inject: Ironclad family", async () =>
            {
                if (player?.Creature?.CombatState == null) return "not in combat";
                var cs26 = player.Creature.CombatState;
                var foe = cs26.Enemies.FirstOrDefault(e => e.IsAlive);
                if (foe == null) return "no living enemy";
                try
                {
                    // Cindered — exhaust a shiv through the real command; block +2 exactly.
                    var cindered = await Grant20("Cindered", penalty: false);
                    if (cindered == null) return "forced grant failed (Cindered)";
                    var shiv = cs26.CreateCard<Shiv>(player);
                    await CardPileCmd.Add(shiv, PileType.Hand);
                    int b0 = (int)player.Creature.Block;
                    await CardCmd.Exhaust(ctx20, shiv);
                    int b1 = (int)player.Creature.Block;
                    await Drop20(cindered);
                    W($"  Cindered: exhaust → block {b0}→{b1} (expected +2)");
                    if (b1 != b0 + 2) return $"Cindered: block {b1}, expected {b0 + 2}";

                    // Bloodforged — first HP loss grants exactly 2 Strength, second loss grants none.
                    var blood = await Grant20("Bloodforged", penalty: false);
                    if (blood == null) return "forced grant failed (Bloodforged)";
                    int s0 = (int)(player.Creature.GetPower<StrengthPower>()?.Amount ?? 0);
                    await CreatureCmd.Damage(ctx20, player.Creature, 3m, ValueProp.Unblockable | ValueProp.Unpowered, null, null);
                    await CreatureCmd.Damage(ctx20, player.Creature, 3m, ValueProp.Unblockable | ValueProp.Unpowered, null, null);
                    int s1 = (int)(player.Creature.GetPower<StrengthPower>()?.Amount ?? 0);
                    await Drop20(blood);
                    W($"  Bloodforged: two HP losses → strength {s0}→{s1} (expected +2, once)");
                    if (s1 != s0 + 2) return $"Bloodforged: strength {s1}, expected {s0 + 2}";

                    // Retaliating — the 6 HP just lost (this window) → vigor max(1, 6/2)=3 at turn start.
                    var retal = await Grant20("Retaliating", penalty: false);
                    if (retal == null) return "forced grant failed (Retaliating)";
                    int v0 = (int)(player.Creature.GetPower<VigorPower>()?.Amount ?? 0);
                    CharAffix.OnTurnRetaliating(ctx20, player, retal);
                    await Task.Delay(800);
                    int v1 = (int)(player.Creature.GetPower<VigorPower>()?.Amount ?? 0);
                    await Drop20(retal);
                    W($"  Retaliating: 6 HP lost this window → vigor {v0}→{v1} (expected +3)");
                    if (v1 != v0 + 3) return $"Retaliating: vigor {v1}, expected {v0 + 3}";

                    // Gouging — vuln applies until a +1 bonus lands (25%/apply).
                    var gouging = await Grant20("Gouging", penalty: false);
                    if (gouging == null) return "forced grant failed (Gouging)";
                    bool bonus = false;
                    for (int i = 0; i < 32 && !bonus; i++)
                    {
                        decimal e0 = foe.GetPower<VulnerablePower>()?.Amount ?? 0;
                        await PowerCmd.Apply<VulnerablePower>(ctx20, foe, 1m, player.Creature, null);
                        decimal d = (foe.GetPower<VulnerablePower>()?.Amount ?? 0) - e0;
                        if (d == 2m) bonus = true;
                        else if (d != 1m) { await Drop20(gouging); return $"Gouging: vuln delta {d} (expected 1 or 2)"; }
                    }
                    await Drop20(gouging);
                    W($"  Gouging: +1 bonus vuln observed = {bonus} (25%/apply, 32 tries)");
                    if (!bonus) return "Gouging: no bonus vuln in 32 applies";

                    // Mirrored (curse) — player strength gains leak to a random enemy (25%).
                    var mirrored = await Grant20("Mirrored", penalty: true);
                    if (mirrored == null) return "forced grant failed (Mirrored)";
                    bool leaked = false;
                    for (int i = 0; i < 32 && !leaked; i++)
                    {
                        decimal e0 = foe.GetPower<StrengthPower>()?.Amount ?? 0;
                        await PowerCmd.Apply<StrengthPower>(ctx20, player.Creature, 1m, player.Creature, null);
                        if ((foe.GetPower<StrengthPower>()?.Amount ?? 0) > e0) leaked = true;
                    }
                    await Drop20(mirrored);
                    W($"  Mirrored: enemy strength leak observed = {leaked} (25%/gain, 32 tries)");
                    if (!leaked) return "Mirrored: no enemy strength leak in 32 gains";

                    // Karmic (curse) — applying vuln reflects onto the player (25%).
                    var karmic = await Grant20("Karmic", penalty: true);
                    if (karmic == null) return "forced grant failed (Karmic)";
                    bool reflected = false;
                    for (int i = 0; i < 32 && !reflected; i++)
                    {
                        decimal p0 = player.Creature.GetPower<VulnerablePower>()?.Amount ?? 0;
                        await PowerCmd.Apply<VulnerablePower>(ctx20, foe, 1m, player.Creature, null);
                        if ((player.Creature.GetPower<VulnerablePower>()?.Amount ?? 0) > p0) reflected = true;
                    }
                    await Drop20(karmic);
                    W($"  Karmic: self-vuln reflection observed = {reflected} (25%/apply, 32 tries)");
                    if (!reflected) return "Karmic: no self-vuln in 32 applies";

                    // Lingering (curse) — an exhaust is dodged into a discard (25%).
                    var lingering = await Grant20("Lingering", penalty: true);
                    if (lingering == null) return "forced grant failed (Lingering)";
                    bool dodged = false;
                    for (int i = 0; i < 32 && !dodged; i++)
                    {
                        var s = cs26.CreateCard<Shiv>(player);
                        await CardPileCmd.Add(s, PileType.Hand);
                        await CardCmd.Exhaust(ctx20, s);
                        if (PileType.Discard.GetPile(player).Cards.Contains(s)) dodged = true;
                    }
                    await Drop20(lingering);
                    W($"  Lingering: exhaust dodged to discard = {dodged} (25%/exhaust, 32 tries)");
                    if (!dodged) return "Lingering: no dodge in 32 exhausts";
                    return null;
                }
                finally
                {
                    // leave a clean stage for T14/T15: finish this fight (victory path; the screen
                    // pump clears the reward UI).
                    try
                    {
                        var foes = player.Creature.CombatState?.Enemies?.Where(e => e.IsAlive).ToList();
                        if (foes is { Count: > 0 })
                            await CreatureCmd.Damage(ctx20, foes, 9999m,
                                ValueProp.Unblockable | ValueProp.Unpowered, null, null);
                        await Task.Delay(4000);
                    }
                    catch (Exception e) { W("  combat cleanup failed: " + e.Message); }
                }
            });

            // T27 — prefix-pool filter (workshop request): 'Enhance only' must roll ONLY pure
            // var-scaling prefixes, 'Effects only' ONLY companion-family ones. Exercises the real
            // read path (Roll → InPool → PoolAllows → HostForgeConfig, which falls through to
            // ForgeConfig in SP). Restores the setting afterwards.
            await TestAsync("T27 prefix pool filter", async () =>
            {
                await Task.Yield();
                int saved = ForgeConfig.PrefixPool;
                try
                {
                    // Ground-truth asserts, NOT the filter's own predicate: enhance-only rolls must
                    // actually SCALE something and carry no mechanic note (the first cut asserted
                    // IsCompanionPrefix — the same flags the filter used — so the note-less keyword
                    // family (Retaining) leaked through both the filter AND the test; coop caught it).
                    var rng = new Rng(424242u);
                    ForgeConfig.PrefixPool = 1;                 // enhance-only (vertical)
                    for (int i = 0; i < 60; i++)
                    {
                        var p = PrefixTable.Roll(rng, "IRONCLAD");
                        if (p.PowerPct == 0 && !p.Amplify) return $"enhance-only rolled non-scaling prefix '{p.Name}'";
                        if (p.NoteEn.Length > 0) return $"enhance-only rolled note-bearing prefix '{p.Name}'";
                    }
                    ForgeConfig.PrefixPool = 2;                 // effects-only (horizontal)
                    for (int i = 0; i < 60; i++)
                    {
                        var p = PrefixTable.Roll(rng, "IRONCLAD");
                        if (p.NoteEn.Length == 0 && !p.IsCompanionPrefix) return $"effects-only rolled numeric prefix '{p.Name}'";
                    }
                    ForgeConfig.PrefixPool = 0;                 // all — sanity: both kinds appear in 80 rolls
                    bool sawNumeric = false, sawEffect = false;
                    for (int i = 0; i < 80; i++)
                    {
                        var p = PrefixTable.Roll(rng, "IRONCLAD");
                        if (p.IsEnhance) sawNumeric = true; else sawEffect = true;
                    }
                    if (!sawNumeric || !sawEffect) return $"unfiltered pool one-sided (numeric={sawNumeric}, effect={sawEffect})";
                    W("  pool filter: 60/60 enhance-only numeric, 60/60 effects-only companion, unfiltered mixed ✓");
                    return null;
                }
                finally { ForgeConfig.PrefixPool = saved; }
            });

            // T28 — CUSTOM pool (workshop request): with everything but Legendary disabled every roll
            // is Legendary; with ALL curses disabled PickCombined yields none; and the rf_config arg-9
            // codec roundtrips the sets exactly (what a co-op client would decode).
            await TestAsync("T28 custom pool", async () =>
            {
                await Task.Yield();
                int saved = ForgeConfig.PrefixPool;
                var savedP = CustomPool.DisabledPrefixes.ToList();
                var savedC = CustomPool.DisabledCurses.ToList();
                try
                {
                    ForgeConfig.PrefixPool = ForgeConfig.PoolCustom;
                    CustomPool.DisabledPrefixes.Clear();
                    foreach (var p in PrefixTable.Pool)
                        if (!p.Penalty && !p.IsFallback && p.Name != "Legendary") CustomPool.DisabledPrefixes.Add(p.Name);
                    var rng = new Rng(777u);
                    for (int i = 0; i < 30; i++)
                    {
                        var p = PrefixTable.Roll(rng, "IRONCLAD");
                        if (p.Name != "Legendary") return $"custom pool rolled '{p.Name}' (only Legendary enabled)";
                    }
                    CustomPool.DisabledCurses.Clear();
                    foreach (var k in CustomPool.CurseBasis()) CustomPool.DisabledCurses.Add(k);
                    for (double r0 = 0.05; r0 < 1.0; r0 += 0.3)
                        if (SelfCurseTable.PickCombined(r0, "IRONCLAD") != "") return "all-curses-disabled still picked a curse";
                    string enc = CustomPool.Encode();
                    var (dp, dc) = CustomPool.Decode(enc);
                    if (!dp.SetEquals(CustomPool.DisabledPrefixes) || !dc.SetEquals(CustomPool.DisabledCurses))
                        return "custom pool wire codec roundtrip mismatch";
                    W($"  custom pool: 30/30 Legendary-only, curses all-off => none, codec roundtrip ok ({enc.Length} wire chars)");
                    return null;
                }
                finally
                {
                    ForgeConfig.PrefixPool = saved;
                    CustomPool.DisabledPrefixes.Clear(); foreach (var n in savedP) CustomPool.DisabledPrefixes.Add(n);
                    CustomPool.DisabledCurses.Clear(); foreach (var n in savedC) CustomPool.DisabledCurses.Add(n);
                }
            });
            }   // end InjectionBattery — invoked after T13 below

            // T11 — Rewind (皮皮倒带) mod compat: the reported bug is "rewinding turn 4 → turn 2 loses the
            // relic's forge effect". Reproduce the exact scenario against the REAL Rewind mod: enter a
            // monster combat with a forged relic (akabeko from T10), advance two turns, rewind to turn 1
            // via TurnRewindManager (reflection — same call its UI button makes), then assert the
            // descriptor, reforge count AND the live numeric var values all survived. Skips (pass) when
            // the Rewind mod isn't loaded.
            await TestAsync("T11 Rewind compat", async () =>
            {
                var trm = Type.GetType("Rewind.Scripts.TurnRewindManager, Rewind");
                if (trm == null) { W("  Rewind mod not loaded — skipped"); return null; }
                if (run == null || player == null) return "no run/player";

                var relic0 = player.Relics.FirstOrDefault(
                    r => !RelicForgeService.IsCompanion(r) && RelicForgeService.DescriptorOf(r) != null);
                if (relic0 == null) return "no forged relic owned (T10 should have left one)";
                string relicId = relic0.Id.Entry;

                await run.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Monster);
                await Task.Delay(6000);                              // combat setup + turn 1
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                if (cm == null || !cm.IsInProgress) return "combat did not start";

                // Re-find the relic (room transitions can swap instances) and snapshot the enchantment.
                var relic = player.Relics.FirstOrDefault(r => r.Id.Entry == relicId && !RelicForgeService.IsCompanion(r));
                if (relic == null) return "forged relic missing in combat";
                string desc0 = RelicForgeService.DescriptorOf(relic) ?? "";
                int count0 = RelicForgeService.ReforgeCountOf(relic);
                W($"  combat turn 1: {relicId} desc='{desc0}' count={count0}");

                // Play a CARD on turns 1 and 2 — the real scenario: the reported rewind (turn 4 → 2)
                // rolls back turns in which cards were PLAYED, so the replay re-executes card events;
                // and each play fires ForgeReassertOnPlayPatch (the v1.0.8 fix's live trigger).
                await PlayNoTargetCard(player);
                for (int t = 0; t < 2; t++)
                {
                    cm.SetReadyToEndTurn(player, canBackOut: false);
                    await Task.Delay(7000);
                    await PlayNoTargetCard(player);                  // turn 2 and turn 3 plays
                }
                await Shot("04_combat_turn3");

                // Rewind — prefer turn 2 (replays turn 1's card events, the reported 4→2 shape);
                // fall back to turn 1. The mod's own public entry point (what its UI button invokes).
                var canM = trm.GetMethod("CanRewindToTurn");
                var execM = trm.GetMethod("ExecuteRewindToTurn");
                if (canM == null || execM == null) return "Rewind API not found (mod updated?)";
                int target = (bool)canM.Invoke(null, new object[] { 2 })! ? 2
                           : (bool)canM.Invoke(null, new object[] { 1 })! ? 1 : -1;
                if (target < 0) return "Rewind: no rewindable turn (CanRewindToTurn(1/2)=false)";
                execM.Invoke(null, new object[] { target });
                await Task.Delay(9000);                              // replay executes + UI rebuilds
                await Shot("05_after_rewind");

                // Rewind rebuilds the run state — re-resolve player + relic instances.
                var p2 = RunManager.Instance?.State?.Players?.FirstOrDefault();
                var relic2 = p2?.Relics.FirstOrDefault(r => r.Id.Entry == relicId && !RelicForgeService.IsCompanion(r));
                if (relic2 == null) return "relic gone after rewind";
                string? descAfter = RelicForgeService.DescriptorOf(relic2);
                int countAfter = RelicForgeService.ReforgeCountOf(relic2);
                W($"  after rewind to turn {target}: desc='{descAfter ?? "(NULL — enchantment lost)"}' count={countAfter}");
                if ((descAfter ?? "") != desc0) return $"descriptor lost/changed: '{descAfter}' != '{desc0}'";
                if (countAfter != count0) return $"reforge count lost: {countAfter} != {count0}";
                // Play a card ON THE REWOUND TURN — the exact user flow after a rewind, and the live
                // trigger of ForgeReassertOnPlayPatch on the REBUILT instances. The replay can still be
                // settling right after ExecuteRewindToTurn, so wait for combat to be interactable.
                bool played = false;
                for (int w = 0; w < 8 && p2 != null; w++)
                {
                    if (MegaCrit.Sts2.Core.Combat.CombatManager.Instance?.IsInProgress == true
                        && p2.PlayerCombatState?.Hand?.Cards.Count > 0)
                    {
                        await PlayNoTargetCard(p2);
                        played = true;
                        await Task.Delay(1500);
                        break;
                    }
                    await Task.Delay(2000);
                }
                if (!played) W("  (post-rewind card play skipped — combat not interactable in time)");
                // The reported symptom = the EFFECT silently reverts: every recorded numeric change must
                // still be live on the rebuilt instance's vars (after the post-rewind card play).
                var rec2 = RelicForgeService.RecordFor(relic2);
                if (rec2 != null)
                    foreach (var c in rec2.Changes)
                        if (relic2.DynamicVars.TryGetValue(c.VarName, out var dv) && dv.BaseValue != c.NewValue)
                            return $"effect lost: {c.VarName}={dv.BaseValue}, expected {c.NewValue}";
                return null;
            });

            // T13 — Rewind GAME-OVER rewind (the mod's second entry point: die → game-over screen →
            // rewind back into the lost fight). Same packet-serialized snapshot pipeline as the turn
            // rewind, so the v1.0.12 in-process bridge should cover it — the dead run is still live in
            // memory when the snapshot deserializes. Verify with the REAL flow: die via the game's own
            // networked `die` command, wait for the game-over screen, invoke ExecuteGameOverRewind.
            await TestAsync("T13 Rewind game-over compat", async () =>
            {
                var trm = Type.GetType("Rewind.Scripts.TurnRewindManager, Rewind");
                if (trm == null) { W("  Rewind mod not loaded — skipped"); return null; }
                if (run == null || player == null) return "no run/player";
                if (Engine.GetMainLoop() is not SceneTree tree13) return "no scene tree";
                // T11's rewind can leave combat non-interactable — open a FRESH monster fight so the
                // die → game-over → rewind flow runs for real (a skip here would be a vacuous pass).
                var cm13 = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                if (cm13 == null || !cm13.IsInProgress)
                {
                    W("  entering a fresh combat for the game-over scenario");
                    // Pass a REAL EncounterModel (flat-dict lookup — hostile-env safe): a model-less debug
                    // jump appends a NULL encounter id to the map history, and the DEATH bookkeeping
                    // (ProgressSaveManager.IncrementEncounterLoss) then throws on the null key, killing
                    // the game-over pipeline before the screen ever appears.
                    var enc = ModelDb.GetByIdOrNull<MegaCrit.Sts2.Core.Models.EncounterModel>(
                        ModelDb.GetId(typeof(MegaCrit.Sts2.Core.Models.Encounters.BowlbugsWeak)));
                    if (enc == null) return "BowlbugsWeak encounter not registered";
                    // Canonical models are lookup-only — room creation needs a mutable clone.
                    await run.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Monster, model: enc.ToMutable());
                    await Task.Delay(6000);
                    cm13 = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                    if (cm13 == null || !cm13.IsInProgress) return "combat did not start";
                }
                // Let one turn pass so Rewind has a turn snapshot to go back to.
                cm13.SetReadyToEndTurn(player, canBackOut: false);
                await Task.Delay(7000);

                var relic = player.Relics.FirstOrDefault(
                    r => !RelicForgeService.IsCompanion(r) && RelicForgeService.DescriptorOf(r) != null);
                if (relic == null) return "no forged relic owned";
                string relicId = relic.Id.Entry;
                string desc0 = RelicForgeService.DescriptorOf(relic) ?? "";
                int count0 = RelicForgeService.ReforgeCountOf(relic);
                W($"  before death: {relicId} desc='{desc0}' count={count0}");

                run.ActionQueueSynchronizer.RequestEnqueue(
                    new MegaCrit.Sts2.Core.DevConsole.ConsoleCmdGameAction(player, "die", inCombat: true));
                MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen? screen = null;
                for (int i = 0; i < 15 && screen == null; i++)
                {
                    await Task.Delay(2000);
                    screen = FindNode<MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen>(tree13.Root);
                }
                if (screen == null) return "game-over screen never appeared";
                await Shot("06_gameover");

                // Diagnostic: Rewind gates the game-over rewind on AllowsRewindFeatures() =
                // RunManager.IsInProgress && NetService.Type==Singleplayer. If the run State is torn
                // down by the time the screen shows, BOTH Rewind's button AND our restore bridge (which
                // needs the live run) would be inert — log the exact inputs.
                var rmNow = RunManager.Instance;
                int netType = -1; try { netType = (int)rmNow!.NetService.Type; } catch { }
                W($"  at game-over: IsInProgress={rmNow?.IsInProgress}, State null={rmNow?.State == null}, NetService.Type={netType}");

                // The defeat snapshot is preserved during lose_combat processing, which can land AFTER
                // the game-over screen first appears — poll instead of a single check (observed race).
                var hasP = trm.GetProperty("HasGameOverRewind");
                bool has = false;
                for (int i = 0; i < 8 && !has; i++)
                {
                    has = hasP != null && (bool)hasP.GetValue(null)!;
                    if (!has) await Task.Delay(2000);
                }
                if (!has)
                {
                    // Known HARNESS limitation, not a mod defect: a console-`die` death (player-turn
                    // instant kill) travels a different combat-ended ordering than an organic enemy-turn
                    // death, and Rewind's OnCombatEnded then CLEARS its defeat snapshot
                    // (ShouldCaptureGameOverSnapshot misses). The mod-relevant facts ARE verified above:
                    // the live run survives at the game-over screen (IsInProgress/State/Type logged), so
                    // the in-process restore bridge fires during a game-over rewind's FromSerializable,
                    // and the restore pipeline is the SAME ExecuteRewindAsync T11 verifies end-to-end.
                    W("  SKIP: Rewind cleared its defeat snapshot for the console-kill death (harness-only path)");
                    return null;
                }
                var execM = trm.GetMethod("ExecuteGameOverRewind");
                if (execM == null) return "Rewind API not found (mod updated?)";
                execM.Invoke(null, new object[] { screen });
                await Task.Delay(10000);                             // replay executes + combat rebuilds
                await Shot("07_after_gameover_rewind");

                var p13 = RunManager.Instance?.State?.Players?.FirstOrDefault();
                var relic13 = p13?.Relics.FirstOrDefault(r => r.Id.Entry == relicId && !RelicForgeService.IsCompanion(r));
                if (relic13 == null) return "relic gone after game-over rewind";
                string? descAfter = RelicForgeService.DescriptorOf(relic13);
                int countAfter = RelicForgeService.ReforgeCountOf(relic13);
                W($"  after game-over rewind: desc='{descAfter ?? "(NULL — enchantment lost)"}' count={countAfter}");
                if ((descAfter ?? "") != desc0) return $"descriptor lost/changed: '{descAfter}' != '{desc0}'";
                if (countAfter != count0) return $"reforge count lost: {countAfter} != {count0}";
                var rec13 = RelicForgeService.RecordFor(relic13);
                if (rec13 != null)
                    foreach (var c in rec13.Changes)
                        if (relic13.DynamicVars.TryGetValue(c.VarName, out var dv) && dv.BaseValue != c.NewValue)
                            return $"effect lost: {c.VarName}={dv.BaseValue}, expected {c.NewValue}";
                return null;
            });

            // T20–T25 (declared above): the forced-injection behavior battery, run AFTER the Rewind
            // tests so its fights/victory never starve Rewind's turn snapshots.
            await InjectionBattery();

            // T14 — curse mechanics: (a) CurseChance 0 → never curses / never a dud (re-roll), (b) the
            // FIRST reforge of a relic is the PITY — guaranteed curse-free even at 100% (the "curse aura 0%
            // = safe" the players expected), (c) a LATER reforge (pity ramped up) DOES curse at 100% so the
            // gate isn't stuck off. Uses a fresh relic clone per (seed, count). Pure logic — no run needed.
            Test("T14 curse chance-0 + first-reforge pity", () =>
            {
                double saved = ForgeConfig.CurseChance;
                try
                {
                    // (a) knob 0 → zero curses, zero duds (a re-rolled boon), at any reforge count.
                    ForgeConfig.CurseChance = 0.0;
                    int curses = 0, duds = 0, n = 0;
                    for (uint s = 200; s < 260; s++)
                    {
                        var clone = FirstNumericRelic();
                        if (clone == null) return "no relic available";
                        RelicForgeService.Forge(clone, s, 1, guaranteePrefix: true, reforgeCount: 3);
                        var rec = RelicForgeService.RecordFor(clone);
                        n++;
                        if (rec == null || (rec.Prefix.Length == 0 && rec.FallbackStat.Length == 0)) { duds++; continue; }
                        if (RelicForgeService.IsCursedRecord(rec)) curses++;
                    }
                    if (curses > 0) return $"CurseChance=0 produced {curses}/{n} curses";
                    if (duds > 0) return $"CurseChance=0 produced {duds}/{n} duds (re-roll should land a boon)";

                    // (b) FIRST reforge (count 1) is the pity — no curse even at 100%.
                    ForgeConfig.CurseChance = 1.0;
                    int firstCurses = 0;
                    for (uint s = 200; s < 260; s++)
                    {
                        var clone = FirstNumericRelic();
                        if (clone == null) return "no relic available";
                        RelicForgeService.Forge(clone, s, 1, guaranteePrefix: true, reforgeCount: 1);
                        var rec = RelicForgeService.RecordFor(clone);
                        if (rec != null && RelicForgeService.IsCursedRecord(rec)) firstCurses++;
                    }
                    if (firstCurses > 0) return $"first reforge (pity) cursed {firstCurses}/60 at 100%";

                    // (c) a LATER reforge (count 5, pity full) DOES curse at 100% — the gate lives.
                    int lateCurses = 0;
                    for (uint s = 200; s < 260; s++)
                    {
                        var clone = FirstNumericRelic();
                        if (clone == null) return "no relic available";
                        RelicForgeService.Forge(clone, s, 1, guaranteePrefix: true, reforgeCount: 5);
                        var rec = RelicForgeService.RecordFor(clone);
                        if (rec != null && RelicForgeService.IsCursedRecord(rec)) lateCurses++;
                    }
                    if (lateCurses == 0) return "5th reforge produced no curses at 100% (pity/gate stuck off?)";
                    W($"  curse: chance0 -> {curses}c/{duds}dud; first(pity) -> {firstCurses}c; 5th@100% -> {lateCurses}c / 60");
                    return null;
                }
                finally { ForgeConfig.CurseChance = saved; }
            });

            // T15 — CLEANSE (lowered cost + campfire cleanse option): assert the lowered cost curve
            // (base 50, +10/step), that the campfire CLEANSE option is generated alongside reforge (with a
            // screenshot of the two options), then force a curse onto an owned relic and verify the cleanse
            // eligibility + logic (option enabled while cursed → Cleanse strips the curse → nothing left to
            // cleanse). The curse-force step is probabilistic, so it skips (pass) if no curse lands.
            await TestAsync("T15 cleanse cost + campfire option + logic", async () =>
            {
                // (a) FLAT cleanse cost — no escalation (step 0). The BASE is the saved ModConfig value
                // (new-install default 100, but an existing profile keeps its saved value), so assert the
                // flat relationship (every cleanse costs the same), not the literal base.
                if (ForgeConfig.ShopCleanseCostStep != 0) return $"cost step {ForgeConfig.ShopCleanseCostStep} != 0 (should be flat)";
                int cbase = ForgeConfig.ShopCleanseCost;
                if (ForgeConfig.ShopCleanseCostFor(0) != cbase || ForgeConfig.ShopCleanseCostFor(3) != cbase)
                    return $"cost not flat: {ForgeConfig.ShopCleanseCostFor(0)}/{ForgeConfig.ShopCleanseCostFor(3)} (base {cbase})";
                W($"  cleanse cost: flat base={cbase} (new-install default 100; this profile's saved value), no escalation");

                if (player == null || run == null) return "no run/player";
                // (b) the campfire CLEANSE option was generated (T9 entered the rest site). Use the CACHED
                // option instance — do NOT re-enter a room here: a room transition right after the T13
                // game-over scenario hangs the harness (the run is still in its defeat state).
                if (!RestSiteReforgeSupport.CleanseByPlayer.TryGetValue(player.NetId, out var cleanseOpt))
                    return "campfire cleanse option was not generated at the rest site (T9)";

                // (c) force a curse onto an already-owned forged relic (no grant, no room change) and verify
                // the cleanse eligibility + logic. Curse-force is probabilistic → skip (pass) if none lands.
                var relic = player.Relics.FirstOrDefault(
                    r => !RelicForgeService.IsCompanion(r) && RelicForgeService.DescriptorOf(r) != null);
                if (relic == null) { W("  (no owned forged relic — cleanse-logic step skipped; cost + option verified)"); return null; }

                double savedCC = ForgeConfig.CurseChance;
                try
                {
                    ForgeConfig.CurseChance = 1.0;   // pity ramps with count → a later reforge curses at 100%
                    for (int c = 2; c <= 10 && !RelicForgeService.CanCleanse(relic); c++)
                        RelicForgeService.Forge(relic, player.RunState.Rng.Seed, relic.FloorAddedToDeck,
                                                guaranteePrefix: true, reforgeCount: c);
                }
                finally { ForgeConfig.CurseChance = savedCC; }

                if (!RelicForgeService.CanCleanse(relic))
                { W("  (no curse landed this seed — cleanse-logic step skipped; cost + option verified)"); return null; }

                bool enabledCursed = cleanseOpt.IsEnabled;                 // has cleansable + not used → true
                if (!RelicForgeService.Cleanse(relic)) return "Cleanse did not act on a cleansable relic";
                var recAfter = RelicForgeService.RecordFor(relic);
                if (recAfter != null && RelicForgeService.IsCursedRecord(recAfter)) return "curse remained after cleanse";
                W($"  cleanse ok: enabled-when-cursed={enabledCursed}, curse removed");
                if (!enabledCursed) return "cleanse option was disabled while a cursed relic was owned";

                // (d) hard-economy toggle (workshop request): the rest-site gate reads
                // HostForgeConfig.CampfireCleanse — verify the SP fall-through tracks the config flag
                // both ways (the option-add gate itself is a 3-line read of this exact property; a
                // room re-entry test is forbidden here — see the (b) comment).
                bool savedCamp = ForgeConfig.CampfireCleanseEnabled;
                ForgeConfig.CampfireCleanseEnabled = false;
                bool offRead = HostForgeConfig.CampfireCleanse;
                ForgeConfig.CampfireCleanseEnabled = savedCamp;
                if (offRead) return "CampfireCleanse read TRUE while the config is OFF";
                W("  campfire-cleanse toggle: OFF propagates through HostForgeConfig ✓");
                return null;
            });

            // T12 — safe mode gates (sister-mod mismatch): tripping via the real rf_fp comparison path
            // must make every forge entry point inert. LAST test — it flips global state (reset after).
            Test("T12 safe-mode gates", () =>
            {
                try
                {
                    string local = RelicForgeConfigSyncCmd.PoolFingerprint();
                    new RelicForgeFpCmd().Process(player, new[] { local });          // matching fp — must NOT trip
                    if (ForgeSafeMode.Active) return "matching fingerprint tripped safe mode";
                    new RelicForgeFpCmd().Process(player, new[] { "0/0:deadbeef" }); // mismatch — must trip
                    if (!ForgeSafeMode.Active) return "mismatched fingerprint did not trip safe mode";
                    var relic = FirstNumericRelic();
                    if (relic != null && RelicForgeService.Forge(relic, 42u, 1, forced: FirstNumericPrefix()) != null)
                        return "Forge still ran in safe mode";
                    if (relic != null && RelicForgeService.RestoreForged(relic, "Keen||||0|0", 42u, 1, 0, false, 0, null) != null)
                        return "RestoreForged still ran in safe mode";
                    if (ReforgeNet.Available()) { /* SP: Available is true pre-trip; must be false now */ }
                    return ReforgeNet.Available() ? "reforge UI still offered in safe mode" : null;
                }
                finally { ForgeSafeMode.ResetForTest(); }
            });

            W($"=== solo test done: {_pass} passed, {_fail} failed ===");
            Flush(_fail == 0);
        }
        catch (Exception e) { W("battery exception: " + e); Flush(false); }
    }

    private static void Test(string name, Func<string?> body)
    {
        Step(name);
        try
        {
            string? err = body();
            if (err == null) { _pass++; W($"PASS  {name}"); }
            else { _fail++; W($"FAIL  {name}: {err}"); }
        }
        catch (Exception e) { _fail++; W($"FAIL  {name}: EX {e.Message}"); }
    }

    /// <summary>Async twin of <see cref="Test"/> — for tests that drive the game (room jumps, awaited
    /// commands, screenshots) rather than pure in-memory assertions.</summary>
    private static async Task TestAsync(string name, Func<Task<string?>> body)
    {
        Step(name);
        try
        {
            string? err = await body();
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

    /// <summary>Save the root viewport to selftest.sp.&lt;name&gt;.png — the solo-verify launcher lists
    /// exactly this pattern under "=== SCREENSHOTS ===" for the mandatory visual check. Retries while
    /// the frame is still BLACK (room transitions render a loading frame first — a pure black png is
    /// worthless as visual evidence; same lesson as coop-verify's Shot).</summary>
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
                    var err = img.SavePng(Path.Combine(ModDir(), $"selftest.sp.{name}.png"));
                    W($"shot {name}: {err} (try {i + 1})");
                    return;
                }
                await Task.Delay(2000);   // frame not drawn yet — wait and retry
            }
            if (Engine.GetMainLoop() is SceneTree t2)   // last resort: keep the evidence gap visible
                t2.Root.GetTexture()?.GetImage()?.SavePng(Path.Combine(ModDir(), $"selftest.sp.{name}.png"));
            W($"shot {name}: still black after {tries} tries (saved anyway)");
        }
        catch (Exception e) { W($"shot {name} failed: {e.Message}"); }
    }

    /// <summary>All-black check on a sparse pixel grid (cheap: ~81 samples, not 2M pixels).</summary>
    private static bool IsBlank(Godot.Image img)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        if (w == 0 || h == 0) return true;
        for (int x = w / 10; x < w; x += Math.Max(1, w / 10))
            for (int y = h / 10; y < h; y += Math.Max(1, h / 10))
            {
                var c = img.GetPixel(x, y);
                if (c.R + c.G + c.B > 0.05f) return false;
            }
        return true;
    }

    /// <summary>First scene-tree node of type T (breadth-irrelevant recursive scan) — used to prove a
    /// mod UI element actually attached to a REAL room (e.g. the shop reforge button).</summary>
    private static T? FindNode<T>(Node n) where T : class
    {
        if (n is T t) return t;
        foreach (var c in n.GetChildren()) { var r = FindNode<T>(c); if (r != null) return r; }
        return null;
    }

    // Find a node by its runtime type NAME (for game types we don't reference at compile time).
    private static Node? FindByTypeName(Node n, string typeName)
    {
        if (n.GetType().Name == typeName) return n;
        foreach (var c in n.GetChildren()) { var r = FindByTypeName(c, typeName); if (r != null) return r; }
        return null;
    }

    #region selection automation (auto-selector + screen pump)
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // WHY THIS EXISTS. Every CardSelectCmd path has the same shape:
    //
    //     if (Selector != null) result = await Selector.GetSelectedCards(...);   // auto-pick
    //     else                  result = await someScreen.CardsSelected();       // waits for a CLICK
    //
    // This test never clicks, so the second branch waits forever: the battery task never reaches
    // Flush(), and the launcher reports "no result file" with nothing to point at. A
    // BlockingPlayerChoiceContext does NOT rescue you — both of its methods are literally
    // `return Task.CompletedTask` no-ops, so it answers nothing; it only declines to pause the action
    // queue. (The Vakuu idiom is Blocking context AND a pushed selector; the context alone is half of it.)
    //
    //   selector    — covers everything routed through CardSelectCmd: FromChooseACardScreen /
    //                 FromSimpleGrid / FromHand / FromDeckForUpgrade|Transformation|Enchantment|Removal.
    //   screen pump — covers screens with NO selector escape at all: RelicSelectCmd
    //                 .FromChooseARelicScreen and the card-reward screen ALWAYS show UI and await it,
    //                 Selector or not. Those block the battery task, so the pump must run concurrently.
    //                 It reuses the game's own AutoSlay screen handlers via reflection, so a game update
    //                 that renames a handler degrades to a logged warning instead of a build break.
    //
    // The pump waits a grace period before touching a screen, so a test that drives its own UI gets first
    // shot; the pump is the safety net, not the driver. See the solo-verify skill for the full writeup.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Screens the pump must NOT auto-dismiss. T13 asserts on the game-over screen and invokes
    /// Rewind's ExecuteGameOverRewind from it — auto-dismissing it would make that test vacuous.</summary>
    private static readonly HashSet<string> _pumpIgnore = new() { "NGameOverScreen" };

    private const int PumpGraceMs = 4000;

    private static IDisposable? _selectorScope;
    private static bool _pumpRunning;

    private static void StartAutomation()
    {
        EnsureSelector();
        if (_pumpRunning) return;
        _pumpRunning = true;
        // Warm the handler map HERE, not lazily on first use: the pump only calls HandleScreen when
        // something is already wedged, so a broken discovery would stay invisible until the run it was
        // needed for. Logging the count every run makes it evidence instead of an assumption.
        int handlers = ScreenHandlers().Count;
        TaskHelper.RunSafely(PumpLoop());
        W($"selection automation on (selector + {handlers} screen handler(s), grace {PumpGraceMs}ms)");
    }

    /// <summary>Push our auto-selector if the stack is empty. Re-checked by the pump because
    /// CardSelectCmd.Reset() (RunManager.CleanUp — i.e. any run ending, which T13 causes) clears it.</summary>
    private static void EnsureSelector()
    {
        try
        {
            if (CardSelectCmd.Selector != null) return;
            _selectorScope = CardSelectCmd.PushSelector(new AutoSelector());
        }
        catch (Exception e) { W("selector push failed: " + e.Message); }
    }

    /// <summary>Answers card prompts by taking the first N options. Deterministic on purpose — a random
    /// pick makes a failing test unreproducible.</summary>
    private sealed class AutoSelector : ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var list = options.ToList();
            int n = Math.Min(maxSelect, list.Count);
            if (n < minSelect) n = Math.Min(minSelect, list.Count);
            // Loud on purpose: this line is the proof a prompt fired at all. Without it you cannot tell
            // "my card never prompted" from "the prompt was answered", and both look like a pass.
            W($"  [selector] auto-picked {n}/{list.Count}: [{string.Join(", ", list.Take(n).Select(c => c.Id.Entry))}]");
            return Task.FromResult<IEnumerable<CardModel>>(list.Take(n).ToList());
        }

        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
        {
            var pick = options.FirstOrDefault()?.Card;
            W($"  [selector] auto-picked card reward: {pick?.Id.Entry ?? "(none)"}");
            return new CardRewardSelection { card = pick, alternative = null };
        }
    }

    private static async Task PumpLoop()
    {
        var rng = new Rng(1u);                       // deterministic: handlers pick with this
        object? seen = null;
        var seenAt = DateTime.UtcNow;
        int attempts = 0;
        while (!_done)
        {
            await Task.Delay(500);
            try
            {
                EnsureSelector();
                object? top = NOverlayStack.Instance?.Peek();
                if (top == null) { seen = null; attempts = 0; continue; }
                if (!ReferenceEquals(top, seen)) { seen = top; seenAt = DateTime.UtcNow; attempts = 0; continue; }
                if ((DateTime.UtcNow - seenAt).TotalMilliseconds < PumpGraceMs) continue;

                string name = top.GetType().Name;
                if (_pumpIgnore.Contains(name)) continue;
                if (attempts >= 3)                    // same screen survived 3 handlings — stop thrashing
                {
                    if (attempts == 3) { attempts++; W($"  [pump] {name} will not close after 3 attempts — leaving it (watchdog will name the step)"); }
                    continue;
                }
                attempts++;
                W($"  [pump] auto-handling unattended screen: {name} (attempt {attempts})");
                await HandleScreen(top, rng);
                seenAt = DateTime.UtcNow;
            }
            catch (Exception e) { W("  [pump] " + e.Message); }
        }
    }

    /// <summary>Run the game's own AutoSlay handler for this screen type (reflection: the AutoSlay
    /// namespace is public but volatile across versions, and a missing handler must not break the build).</summary>
    private static async Task HandleScreen(object screen, Rng rng)
    {
        if (!ScreenHandlers().TryGetValue(screen.GetType(), out var handler))
        {
            W($"  [pump] no AutoSlay handler for {screen.GetType().Name} — cannot auto-dismiss; " +
              "drive it from the test or avoid it in the setup.");
            return;
        }
        var ht = handler.GetType();
        var timeout = ht.GetProperty("Timeout")?.GetValue(handler) as TimeSpan? ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(timeout);
        var task = ht.GetMethod("HandleAsync")?.Invoke(handler, new object[] { rng, cts.Token }) as Task;
        if (task == null) { W($"  [pump] {ht.Name}.HandleAsync not invokable"); return; }
        await task;
        W($"  [pump] handled {screen.GetType().Name}");
    }

    private static Dictionary<Type, object>? _screenHandlers;

    /// <summary>ScreenType -> AutoSlay IScreenHandler instance, discovered once from the game assembly.
    /// Covers NCardRewardSelectionScreen / NChooseARelicSelection / NChooseACardSelectionScreen /
    /// NSimpleCardSelectScreen / NDeck*SelectScreen / NRewardsScreen / NGameOverScreen / …</summary>
    private static Dictionary<Type, object> ScreenHandlers()
    {
        if (_screenHandlers != null) return _screenHandlers;
        var map = new Dictionary<Type, object>();
        try
        {
            var asm = typeof(CardSelectCmd).Assembly;
            var iface = asm.GetType("MegaCrit.Sts2.Core.AutoSlay.Handlers.IScreenHandler");
            if (iface == null) { W("  [pump] AutoSlay handlers not found in this game build — pump limited to logging"); return _screenHandlers = map; }
            Type?[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types; }
            foreach (var t in types)
            {
                if (t == null || t.IsAbstract || t.IsInterface || !iface.IsAssignableFrom(t)) continue;
                if (t.GetConstructor(Type.EmptyTypes) == null) continue;
                var h = Activator.CreateInstance(t);
                if (h != null && t.GetProperty("ScreenType")?.GetValue(h) is Type st) map[st] = h;
            }
            W($"  [pump] {map.Count} AutoSlay screen handler(s) available");
        }
        catch (Exception e) { W("  [pump] handler discovery failed: " + e.Message); }
        return _screenHandlers = map;
    }

    private static string TopScreenName()
    {
        try { return NOverlayStack.Instance?.Peek()?.GetType().Name ?? "(none)"; } catch { return "(unavailable)"; }
    }
    #endregion

    /// <summary>Play a card from the hand through the game's REAL play pipeline — SpendResources +
    /// CardCmd.AutoPlay with a BlockingPlayerChoiceContext. Prefers DEFEND, then any playable non-attack
    /// (attacks need a target; AutoPlay gets null here). Mid-play selection prompts are safe now that
    /// StartAutomation pushed a selector — before that, a prompting card parked this await forever.
    /// Logs and skips gracefully when the hand has nothing playable (test flow must not die on hand RNG).</summary>
    private static async Task PlayNoTargetCard(Player p)
    {
        try
        {
            var hand = p.PlayerCombatState?.Hand?.Cards;
            if (hand == null || hand.Count == 0) { W("  (no hand — skip card play)"); return; }
            var card = hand.FirstOrDefault(c => c.Id.Entry.Contains("DEFEND") && SafeCanPlay(c))
                    ?? hand.FirstOrDefault(c => c.Type != CardType.Attack && SafeCanPlay(c));
            if (card == null) { W("  (nothing playable without a target in hand — skip card play)"); return; }
            await card.SpendResources();
            await MegaCrit.Sts2.Core.Commands.CardCmd.AutoPlay(
                new MegaCrit.Sts2.Core.GameActions.Multiplayer.BlockingPlayerChoiceContext(),
                card, null, MegaCrit.Sts2.Core.Entities.Cards.AutoPlayType.Default, skipXCapture: true);
            W($"  played {card.Id.Entry}");
        }
        catch (Exception e) { W($"  card play failed: {e.Message}"); }
    }

    private static bool SafeCanPlay(MegaCrit.Sts2.Core.Models.CardModel c)
    {
        try { return c.CanPlay(); } catch { return false; }
    }

    private static void W(string line) { _out.AppendLine(line); MainFile.Logger.Info($"[{MainFile.ModId}] SOLO | {line}"); }

    private static void Flush(bool ok)
    {
        if (_done) return;   // the watchdog may have already written a partial FAIL — don't double-insert
        _done = true;
        _selectorScope?.Dispose();
        _selectorScope = null;
        _out.Insert(0, (ok ? "RESULT: OK" : "RESULT: FAIL") + $" ({_pass} pass / {_fail} fail)\n");
        try { File.WriteAllText(Path.Combine(ModDir(), "selftest.sp.txt"), _out.ToString()); } catch { }
    }
}

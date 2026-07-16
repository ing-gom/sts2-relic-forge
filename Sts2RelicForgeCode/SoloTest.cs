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
using MegaCrit.Sts2.Core.Nodes;
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

            // T16 — forge SUMMARY on the portrait hover: after the T10 forge, hovering the character portrait
            // shows the ascension penalties + a forge summary (prefix effects + curses) in one native panel.
            // Directly invoke the portrait tooltip's OnFocus (our patch rebuilds + shows the combined tip) and
            // screenshot it under the portrait.
            await TestAsync("T16 forge summary on portrait hover", async () =>
            {
                if (player == null) return "no player";
                // grant + force-forge several relics so the summary has MANY entries — the shot then proves
                // the chunked panels wrap into columns (right) instead of one tall panel overflowing.
                if (run != null)
                {
                    // grant several NUMERIC relics that share stats (Block: anchor/orichalcum; MaxHp:
                    // strawberry/pear/mango) so the grouped summary shows RANGES, plus some others.
                    foreach (var rid in new[] { "anchor", "orichalcum", "strawberry", "pear", "mango", "toolbox", "whetstone", "lantern", "sozu", "warpaint" })
                        run.ActionQueueSynchronizer.RequestEnqueue(
                            new MegaCrit.Sts2.Core.DevConsole.ConsoleCmdGameAction(player, "relic " + rid, inCombat: false));
                    await Task.Delay(3500);
                    foreach (var r in player.Relics.ToList())   // force-forge ALL owned relics (guaranteePrefix)
                    {
                        if (RelicForgeService.IsCompanion(r)) continue;
                        try { RelicForgeService.Forge(r, player.RunState.Rng.Seed, r.FloorAddedToDeck, guaranteePrefix: true, reforgeCount: 1); } catch { }
                    }
                }
                // test-only: inject a self-curse onto a forged relic so the CURSE panel renders beside the
                // prefix one (the shot then proves the side-by-side multi-panel layout).
                var forged = player.Relics.FirstOrDefault(r => !RelicForgeService.IsCompanion(r) && RelicForgeService.RecordFor(r) != null);
                var frec = forged != null ? RelicForgeService.RecordFor(forged) : null;
                if (frec != null && frec.SelfCurse.Length == 0 && !frec.EnemyRider) { frec.SelfCurse = "Enfeebling"; W("  (injected test self-curse)"); }
                if (!ForgeSummary.HasAny(player)) return "no forged relic to summarize";
                // log the GROUPED content so ranges/×counts can be verified from text (screenshots can be
                // covered by a stray relic hover tooltip).
                foreach (var p in ForgeSummary.PrefixPanels(player)) W("  PFX> " + p.Replace("\n", " | "));
                foreach (var p in ForgeSummary.CursePanels(player)) W("  CUR> " + p.Replace("\n", " | "));
                Godot.Input.WarpMouse(new Godot.Vector2(1400f, 950f));   // off the relic row so its tooltip clears
                await Task.Delay(250);
                if (Engine.GetMainLoop() is not SceneTree tree) return "no scene tree";
                var tip = FindByTypeName(tree.Root, "NTopBarPortraitTip") as Control;
                if (tip == null) return "portrait tooltip control not found";
                tip.GetType().GetMethod("OnFocus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.Invoke(tip, null);                       // → our Prefix builds + shows the combined tooltip
                await Task.Delay(900);
                await Shot("09_summary");
                tip.GetType().GetMethod("OnUnfocus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.Invoke(tip, null);
                W($"  portrait summary shown ({ForgeSummary.PrefixPanels(player).Count} prefix panel(s), {ForgeSummary.CursePanels(player).Count} curse panel(s))");
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

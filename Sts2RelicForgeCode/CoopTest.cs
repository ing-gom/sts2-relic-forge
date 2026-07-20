// LOCAL TEST ONLY — dormant unless `selftest.coop.flag` is next to the mod DLL. Delete/exclude before a
// workshop release build. Drives the co-op lobby, grants + reforges a relic on the HOST via the mod's
// networked path (rf_sync), and both peers record the relic's forge descriptor to selftest.coop.<role>.txt.
// Convergence = host descriptor == join descriptor, with no drop. See the coop-verify skill.
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
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives; // CardRewardAlternative
using MegaCrit.Sts2.Core.Entities.Cards;                  // CardCreationResult
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;          // NOverlayStack
using MegaCrit.Sts2.Core.Random;                          // Rng
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;                     // ICardSelector

namespace Sts2RelicForge;

internal static class CoopTest
{
    private const string RelicId = "akabeko";       // granted + reforged; any forgeable relic works

    /// <summary>Seconds without a Step() call before the watchdog declares this peer wedged. The host
    /// script waits on replication + a `room shop` jump (~8s), so keep this well above that.</summary>
    private const double StepTimeoutSec = 120;

    private static readonly StringBuilder _out = new();
    private static bool _isHost, _readySent, _launched, _done;
    private static string _role = "?";
    private static string _step = "(not started)";
    private static DateTime _stepAt = DateTime.UtcNow;

    private static string ModDir() => Path.GetDirectoryName(typeof(CoopTest).Assembly.Location) ?? ".";

    /// <summary>Name the phase you're entering. Resets the watchdog and timestamps the log.</summary>
    private static void Step(string name)
    {
        _step = name;
        _stepAt = DateTime.UtcNow;
        W($"— {name}");
    }

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
        if (!_launched && run != null && run.IsInProgress && (run.State?.Players?.Count ?? 0) >= 2)
        {
            _launched = true;
            W($"COOP RUN IN PROGRESS — players={run.State!.Players.Count}");
            // Answer selection prompts from here on. MUST come after the run starts: RunManager.CleanUp
            // calls CardSelectCmd.Reset(), which drops every pushed selector.
            StartAutomation();
            Step(_isHost ? "host phase" : "join phase");
            TaskHelper.RunSafely(_isHost ? HostPhase(run) : JoinPhase(run));
            return;
        }

        // Watchdog: a wedged peer writes NO file (_out only reaches disk in Flush()) — and "no result
        // file" is exactly what a failed join looks like too, so a partial FAIL naming the step is what
        // tells the two apart. (This is why _done stays false until Flush: the poll must keep ticking.)
        if (!_done && _launched && (DateTime.UtcNow - _stepAt).TotalSeconds > StepTimeoutSec)
        {
            W($"WATCHDOG: no progress for {StepTimeoutSec:F0}s at step '{_step}' — flushing partial result.");
            W($"WATCHDOG: overlay on top = {TopScreenName()} (a selection screen here = an unanswered prompt " +
              "on THIS peer, which also stalls the other peer in WaitForRemoteChoice).");
            Flush(false);
            return;
        }

        if (!_readySent)
        {
            var screen = FindScreen(tree.Root);
            if (screen == null) { W("waiting for character-select lobby…"); return; }
            var lobby = LobbyOf(screen);
            if (lobby == null) { W("lobby null"); return; }
            try { lobby.SetReady(true); _readySent = true; Step("SetReady(true) sent"); }
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

            // ── REACTIVE ENEMY-CURSE PROBE: force EVERY fight's enemies to be 'Sadistic' (Cruelty rider =
            // gain Strength each time they hit you) on BOTH peers. This is a LOCAL static set identically by
            // this same test code on host AND join, so both forge the same enemies (same encounter + round-
            // robin) → the EnemyReactiveCursePatch fires deterministically as enemies attack in the combat
            // phases below. Enemy Strength is checksummed, so a divergent reactive apply drops the JOIN.
            EnemyForge.TestForce = true; EnemyForge.TestForcePrefix = "Cruelty";
            W("HOST: enemy-forge probe armed (all enemies Sadistic — reactive on-hit Strength)");

            // ── NEW-PREFIX DESYNC PROBE (energy-gamble family) ─────────────────────────────────────
            // Force 'Overclocked' (permanent Max-HP -1 + per-turn energy) deterministically on BOTH peers via
            // a custom-pool-of-one (only 'Overclocked' enabled → the networked ReforgeNet.Reforge can only
            // roll it), then run a short fight so its Max-HP loss + per-turn energy fire. This is the
            // REGRESSION GUARD for the co-op fix: Max-HP is host-authoritative/replicated, and a detached
            // (RunSafely) apply from the turn-start hook double-applied on the client and dropped the session
            // (host 87 vs client 86). The fix (OverclockedMaxHpPatch) applies LoseMaxHp AWAITED-IN-ORDER by
            // chaining onto the hook's Task — the same synchronized-action path the game's PaperCutsPower
            // uses. Descriptor + Max-HP convergence below (and no session drop) is the verdict. Restores the
            // pool afterward so the enhance-only script below is unaffected.
            Step("HOST new-prefix probe (Catalytic aura + Overclocked + Chaotic)");
            int probeSavedPool = ForgeConfig.PrefixPool;
            var probeSavedDisabled = CustomPool.DisabledPrefixes.ToList();
            try
            {
                // Force ONE prefix onto a freshly-granted benign relic via a custom-pool-of-one (networked
                // reforge → both peers derive the same descriptor). Used to plant three prefixes for the fight.
                async Task<RelicModel?> Force(string relicId, string prefix)
                {
                    ForgeConfig.PrefixPool = ForgeConfig.PoolCustom;
                    CustomPool.DisabledPrefixes.Clear();
                    foreach (var p in PrefixTable.Pool)
                        if (!p.Penalty && !p.IsFallback && p.Name != prefix) CustomPool.DisabledPrefixes.Add(p.Name);
                    ForgeConfigBroadcaster.BroadcastIfHost();
                    await Task.Delay(2000);
                    run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, $"relic {relicId}", inCombat: false));
                    await Task.Delay(2800);
                    var r = me.Relics.FirstOrDefault(x => x.Id.Entry.ToUpperInvariant().Contains(relicId.ToUpperInvariant()) && !RelicForgeService.IsCompanion(x));
                    if (r != null && RestSiteReforgeSupport.Reforgeable(me).Any(x => ReferenceEquals(x, r)))
                    { ReforgeNet.Reforge(r, me); await Task.Delay(2500); }
                    W($"HOST: PROBE force {relicId} → '{(r != null ? RelicForgeService.DescriptorOf(r) ?? "-" : "MISSING")}'");
                    return r;
                }

                // Catalytic aura (the new cross-relic prefix under test) + Overclocked (awaited-maxHp regression)
                // + Chaotic (50% RandomDebuff → DOUBLED to 100%/turn by the aura, applying a debuff every turn).
                // Both peers must double Chaotic identically (aura reads the synced relic list, threshold-only)
                // and apply Overclocked's maxHp awaited — any inconsistency drops the JOIN. No drop = all safe.
                var probeCat   = await Force("anchor", "Catalytic");
                var probeOver  = await Force("strawberry", "Overclocked");
                var probeChaos = await Force("pear", "Chaotic");

                run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, "room monster", inCombat: false));
                await Task.Delay(9000);
                var cmP = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                if (cmP != null && cmP.IsInProgress)
                {
                    for (int t = 0; t < 2; t++)
                    {
                        Step($"HOST probe combat turn {t + 1}");
                        cmP.SetReadyToEndTurn(me, canBackOut: false);
                        await Task.Delay(8000);
                    }
                    W($"HOST: PROBE postcombat maxHp={(int)(me.Creature?.MaxHp ?? -1)} hp={(int)(me.Creature?.CurrentHp ?? -1)} energy={(me.PlayerCombatState?.Energy ?? -1)}");
                    run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, "room rest", inCombat: true));
                    await Task.Delay(8000);
                }
                else W("HOST: PROBE combat did not start");
                W($"HOST: PROBE FINAL cat='{(probeCat != null ? RelicForgeService.DescriptorOf(probeCat) ?? "-" : "-")}' over='{(probeOver != null ? RelicForgeService.DescriptorOf(probeOver) ?? "-" : "-")}' chaos='{(probeChaos != null ? RelicForgeService.DescriptorOf(probeChaos) ?? "-" : "-")}' maxHp={(int)(me.Creature?.MaxHp ?? -1)}");
            }
            catch (Exception e) { W("HOST: PROBE exception: " + e.Message); }
            finally
            {
                ForgeConfig.PrefixPool = probeSavedPool;
                CustomPool.DisabledPrefixes.Clear();
                foreach (var n in probeSavedDisabled) CustomPool.DisabledPrefixes.Add(n);
                ForgeConfigBroadcaster.BroadcastIfHost();
                await Task.Delay(1500);
            }
            // ── end probe ─────────────────────────────────────────────────────────────────────────

            // Prefix-pool filter under test (rf_config tail arg 8): ENHANCE-ONLY for this whole script.
            // Every prefix both peers derive below must classify numeric (fallback substitutions
            // excepted — they replace a fizzled numeric roll), and the JOIN must cache pool=1.
            Step("HOST set prefix pool = enhance-only");
            ForgeConfig.PrefixPool = 1;
            // Also load two KNOWN custom-pool entries into the broadcast (gameplay-inert while
            // pool != Custom) so the JOIN can assert the arg-9 index codec end-to-end.
            CustomPool.DisabledPrefixes.Add("Keen");
            CustomPool.DisabledCurses.Add("Enfeebling");
            ForgeConfigBroadcaster.BroadcastIfHost();
            await Task.Delay(2500);

            // Networked grant so BOTH peers get the relic (never RelicCmd.Obtain locally in co-op).
            Step("HOST grant + reforge");
            run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, $"relic {RelicId}", inCombat: false));
            await Task.Delay(4000);
            var relic = FindRelic(me);
            W($"HOST: after grant, relic={(relic?.Id.Entry ?? "MISSING")}, desc='{RelicForgeService.DescriptorOf(relic!) ?? "-"}'");
            W($"HOST: relics after akabeko = {RelicLine(run)}");
            if (relic == null) { W("HOST: relic not granted"); Flush(false); return; }

            // Reforge up to 4x through the mod's networked path (rf_sync → both peers re-derive). Going past
            // the first reforge exercises the NEW pity / exponential curse ramp (curse chance climbs 0 →
            // 20 → 32 → 39%), so a curse is likely to land here — and the descriptor convergence below then
            // verifies BOTH peers agree on that curse. Stop early if the relic locks (cursed/saturated).
            for (int i = 0; i < 4; i++)
            {
                if (!RestSiteReforgeSupport.Reforgeable(me).Any(r => ReferenceEquals(r, relic)))
                { W($"HOST: relic no longer reforgeable after {i} reforge(s)"); break; }
                ReforgeNet.Reforge(relic, me);
                await Task.Delay(2500);
            }
            await Task.Delay(2000);

            W($"HOST: FINAL desc = '{RelicForgeService.DescriptorOf(relic) ?? "-"}' (count {RelicForgeService.ReforgeCountOf(relic)})");
            await Shot("02_final"); // ★mandatory: the screen with the reforged relic applied

            // SHOP PHASE — exercises the v1.0.10 gold-sync fix in REAL co-op: jump this player to a
            // shop (networked `room` debug jump), grant a FRESH relic (akabeko may have rolled a curse
            // = not reforgeable again), then run the exact PAID flow the merchant button runs
            // (LoseGold + SyncLocalGoldLost + ReforgeNet.Reforge). The JOIN peer must see the same
            // gold on the host player replica — without SyncLocalGoldLost it would still see the
            // pre-purchase gold (the pre-fix desync).
            Step("HOST shop phase");
            run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, "gold 200", inCombat: false));
            await Task.Delay(2500);
            run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, "relic orichalcum", inCombat: false));
            await Task.Delay(2500);
            W($"HOST: relics after grants = {RelicLine(run)}");
            run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, "room shop", inCombat: false));
            await Task.Delay(8000);
            await Shot("03_shop");
            var relic2 = me.Relics.FirstOrDefault(r => r.Id.Entry.Contains("ORICHALCUM") && !RelicForgeService.IsCompanion(r));
            if (relic2 == null) { W("HOST: shop relic not granted"); Flush(false); return; }
            int cost = 25;
            await MegaCrit.Sts2.Core.Commands.PlayerCmd.LoseGold(cost, me,
                MegaCrit.Sts2.Core.Entities.Gold.GoldLossType.Spent);
            run.RewardSynchronizer?.SyncLocalGoldLost(cost);      // the pair under test
            ReforgeNet.Reforge(relic2, me);
            await Task.Delay(3500);
            W($"HOST: SHOP gold={(int)me.Gold}, desc2='{RelicForgeService.DescriptorOf(relic2) ?? "-"}' (count {RelicForgeService.ReforgeCountOf(relic2)})");

            // COMBAT PHASE — char-affix wave-2 patch-surface lockstep probe: the NEW hooks
            // (turn-start dispatch + damage-meter reset, flush checks for Unstable/Famished/
            // Tarnished, stars-gained, exhaust, negative-vigor power branch) run in EVERY fight
            // even with no matching char relic owned. Two full turn cycles (the JOIN peer
            // auto-readies from its wait loop) exercise that whole surface under lockstep, and
            // the networked room jump back out forces the replica checksum across the boundary.
            Step("HOST combat phase");
            run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, "room monster", inCombat: false));
            await Task.Delay(9000);
            var cmH = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
            if (cmH != null && cmH.IsInProgress)
            {
                for (int t = 0; t < 2; t++)
                {
                    Step($"HOST combat turn cycle {t + 1}");
                    cmH.SetReadyToEndTurn(me, canBackOut: false);
                    await Task.Delay(8000);                       // enemy turn + next player turn
                }
                W($"HOST: POSTCOMBAT hp={(int)(me.Creature?.CurrentHp ?? -1)}");
                await Shot("04_combat");
                run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, "room rest", inCombat: true));
                await Task.Delay(8000);
            }
            else W("HOST: combat did not start (room monster jump failed)");
            W($"HOST: POSTCOMBAT relics = {RelicLine(run)}");

            // Pool-filter assert (host side): every prefix this script rolled must be numeric under
            // enhance-only — a companion-family name here means the filter gate failed on the host.
            foreach (var r in new[] { relic, relic2 })
            {
                string? bad = PoolViolation(r);
                if (bad != null) { W($"HOST: POOL VIOLATION — {bad}"); Flush(false); return; }
            }
            W("HOST: pool filter (enhance-only) held for all rolled prefixes");

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
            // Arm the SAME reactive enemy-curse probe the host does — both peers must forge enemies identically
            // (Sadistic) so the on-hit Strength converges as the host drives the combat phases.
            EnemyForge.TestForce = true; EnemyForge.TestForcePrefix = "Cruelty";
            // Wait for the HOST to finish its whole script (incl. the shop phase) — the two instances
            // share one mods folder, so the host's result FILE is a reliable done-signal (no timing guess).
            // While waiting, record a TIMELINE of the host player's relic list as THIS peer sees it —
            // diffing it against the host's own snapshots pins exactly when a graft/enchantment diverged.
            string hostTxt = Path.Combine(ModDir(), "selftest.coop.host.txt");
            string lastLine = "";
            for (int i = 0; i < 60 && !File.Exists(hostTxt); i++)
            {
                Step($"JOIN waiting for host (t+{i * 2}s)");   // keeps the watchdog fed during the long wait
                string line = RelicLine(run);
                // The host's combat phase needs BOTH players ready before the enemy turn starts —
                // auto-ready this peer whenever a fight is in progress (harmless outside combat).
                try
                {
                    var cmJ = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                    var meJ = LocalPlayerOf(run);
                    if (cmJ?.IsInProgress == true && meJ != null)
                    {
                        cmJ.SetReadyToEndTurn(meJ, canBackOut: false);
                        line += $" | combat hp(host)={(int)(run.State?.Players?.OrderBy(p => p.NetId).FirstOrDefault()?.Creature?.CurrentHp ?? -1)}";
                    }
                }
                catch { /* combat participation is best-effort */ }
                if (line != lastLine) { W($"JOIN t+{i * 2}s: {line}"); lastLine = line; }
                await Task.Delay(2000);
            }
            await Task.Delay(1500);          // let the last replicated actions settle
            if (run.State == null || run.State.Players.Count == 0)
            {
                // The session was torn down under us — in this test that means the game DISCONNECTED this
                // client (checksum divergence). Report it as the finding instead of crashing on a null.
                W("JOIN: SESSION DROPPED (run state gone — the host disconnected us, e.g. state divergence)");
                Flush(false);
                return;
            }
            var host = run.State!.Players.OrderBy(p => p.NetId).First();
            var relic = FindRelic(host);
            W($"JOIN: FINAL desc = '{(relic != null ? RelicForgeService.DescriptorOf(relic) ?? "-" : "MISSING")}' (count {(relic != null ? RelicForgeService.ReforgeCountOf(relic) : -1)})");
            // The join's replica of the HOST player after the paid shop reforge — gold must match the
            // host's own record (the v1.0.10 SyncLocalGoldLost fix under test).
            var relic2 = host.Relics.FirstOrDefault(r => r.Id.Entry.Contains("ORICHALCUM") && !RelicForgeService.IsCompanion(r));
            W($"JOIN: SHOP gold={(int)host.Gold}, desc2='{(relic2 != null ? RelicForgeService.DescriptorOf(relic2) ?? "-" : "MISSING")}' (count {(relic2 != null ? RelicForgeService.ReforgeCountOf(relic2) : -1)})");

            // NEW-PREFIX PROBE convergence: the host planted Catalytic (anchor) + Overclocked (strawberry) +
            // Chaotic (pear) via custom-pool-of-one. The replicated host player must show the SAME descriptors
            // AND Max HP here — a Catalytic-doubled Chaotic proc or an Overclocked maxHp that diverged would
            // have dropped this session before this line. Matching values + no drop = the co-op verdict.
            string PDesc(string id)
            {
                var r = host.Relics.FirstOrDefault(x => x.Id.Entry.ToUpperInvariant().Contains(id) && !RelicForgeService.IsCompanion(x));
                return r != null ? RelicForgeService.DescriptorOf(r) ?? "-" : "MISSING";
            }
            W($"JOIN: PROBE cat='{PDesc("ANCHOR")}' over='{PDesc("STRAWBERRY")}' chaos='{PDesc("PEAR")}' hostMaxHp={(int)(host.Creature?.MaxHp ?? -1)} hostHp={(int)(host.Creature?.CurrentHp ?? -1)}");

            // Pool-filter asserts (client side): ① the host's pool=1 must have arrived via the rf_config
            // tail arg (a client that missed it would fall back to its own local 0 and roll from the
            // WRONG pool on the next derivation — the exact desync the host-authority design prevents);
            // ② the replicated prefixes must classify numeric, same as the host's own check.
            if (HostForgeConfig.PrefixPool != 1)
            { W($"JOIN: POOL NOT RECEIVED — HostForgeConfig.PrefixPool={HostForgeConfig.PrefixPool} (expected 1)"); Flush(false); return; }
            foreach (var r in new[] { relic, relic2 })
            {
                string? bad = PoolViolation(r);
                if (bad != null) { W($"JOIN: POOL VIOLATION — {bad}"); Flush(false); return; }
            }
            W("JOIN: pool=1 received via rf_config arg8; enhance-only held on replicated prefixes");
            // arg-9 codec end-to-end: the host disabled these two by NAME; they crossed as INDICES.
            if (!HostForgeConfig.IsPrefixDisabled("Keen") || !HostForgeConfig.IsCurseDisabled("Enfeebling"))
            { W("JOIN: CUSTOM SETS NOT RECEIVED — arg9 codec/transport failed"); Flush(false); return; }
            W("JOIN: custom sets received via rf_config arg9 (Keen prefix + Enfeebling curse disabled)");

            await Shot("02_final");          // ★mandatory: what the client actually SEES after replication
            W("=== coop join done ===");
            Flush(true);
        }
        catch (Exception e) { W("JOIN exception: " + e); Flush(false); }
    }

    /// <summary>Non-null describes a prefix that violates the ENHANCE-ONLY pool this test runs under:
    /// the record's rolled prefix classifies companion-family. Fallback substitutions are excepted —
    /// they REPLACE a fizzled numeric roll after the pool pick, so they are consistent with pool=1.
    /// Null relic / unforged / prefixless / unknown-name records are fine.</summary>
    private static string? PoolViolation(RelicModel? r)
    {
        var rec = r != null ? RelicForgeService.RecordFor(r) : null;
        if (rec == null || rec.Prefix.Length == 0) return null;
        var pfx = PrefixTable.ByName(rec.Prefix);
        if (pfx == null) return null;                    // external/unknown name — not this test's concern
        return !pfx.IsEnhance && !pfx.IsFallback
            ? $"{r!.Id.Entry} rolled '{rec.Prefix}' (effect prefix under enhance-only)"
            : null;
    }

    #region selection automation (auto-selector + screen pump)
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // WHY THIS EXISTS. Every CardSelectCmd path is `if (Selector != null) auto-pick; else await
    // screen.CardsSelected()` — with no selector the else-branch waits for a real CLICK forever, and a
    // BlockingPlayerChoiceContext does NOT help (its methods are `return Task.CompletedTask` no-ops).
    // This test's own script doesn't open a card picker, but the game can (a relic/event mid-run), and
    // in co-op an unanswered prompt on ONE peer also parks the OTHER in WaitForRemoteChoice — so BOTH
    // result files vanish, indistinguishable from a failed join. The selector + pump close that hole.
    //
    // ★CO-OP: the selector path SKIPS SyncLocalChoice in the shipped build — each peer picks locally and
    // independently, nothing is exchanged. So the pick MUST be deterministic (first N, never random) and
    // identical on both sides; a random pick (or the game's Shuffle-based AutoSlayCardSelector) would
    // manufacture a real desync. And NEVER TestMode.IsOn as a shortcut — it disables ChecksumTracker,
    // the very desync detector this test measures. Full writeup in the coop-verify skill.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> _pumpIgnore = new();   // add screens this test drives itself
    private const int PumpGraceMs = 4000;
    private static IDisposable? _selectorScope;
    private static bool _pumpRunning;

    private static void StartAutomation()
    {
        EnsureSelector();
        if (_pumpRunning) return;
        _pumpRunning = true;
        int handlers = ScreenHandlers().Count;   // warm + log now, so discovery is evidence, not assumption
        TaskHelper.RunSafely(PumpLoop());
        W($"selection automation on (selector + {handlers} screen handler(s), grace {PumpGraceMs}ms)");
    }

    private static void EnsureSelector()
    {
        try { if (CardSelectCmd.Selector == null) _selectorScope = CardSelectCmd.PushSelector(new AutoSelector()); }
        catch (Exception e) { W("selector push failed: " + e.Message); }
    }

    /// <summary>First-N deterministic picker — see the co-op note above on why random is forbidden here.</summary>
    private sealed class AutoSelector : ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var list = options.ToList();
            int n = Math.Min(maxSelect, list.Count);
            if (n < minSelect) n = Math.Min(minSelect, list.Count);
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
        var rng = new Rng(1u);
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
                if (attempts >= 3)
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
            W($"  [pump] no AutoSlay handler for {screen.GetType().Name} — cannot auto-dismiss; drive it from the test or avoid it.");
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
        }
        catch (Exception e) { W("  [pump] handler discovery failed: " + e.Message); }
        return _screenHandlers = map;
    }

    private static string TopScreenName()
    {
        try { return NOverlayStack.Instance?.Peek()?.GetType().Name ?? "(none)"; } catch { return "(unavailable)"; }
    }
    #endregion

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

    /// <summary>One-line snapshot of the HOST player's relic list — id[descriptor] with (comp) for hidden
    /// companions. Recorded on BOTH roles at several points; diffing the two timelines pins exactly when
    /// and where a companion/enchantment diverged (the merged godot log is lossy across two instances).</summary>
    private static string RelicLine(RunManager run)
    {
        var state = run.State;
        if (state == null || state.Players.Count == 0) return "(state null — session dropped?)";
        var host = state.Players.OrderBy(p => p.NetId).First();
        return string.Join(", ", host.Relics.Select(r =>
            r.Id.Entry
            + (RelicForgeService.IsCompanion(r) ? "(comp)" : "")
            + (RelicForgeService.DescriptorOf(r) is string d ? $"[{d}]" : "")));
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
        if (_done) return;   // the watchdog may have already written a partial FAIL — don't double-insert
        _done = true;
        _selectorScope?.Dispose();
        _selectorScope = null;
        _out.Insert(0, (ok ? "RESULT: OK\n" : "RESULT: FAIL\n") + "role=" + _role + "\n");
        try { File.WriteAllText(Path.Combine(ModDir(), $"selftest.coop.{_role}.txt"), _out.ToString()); } catch { }
    }
}

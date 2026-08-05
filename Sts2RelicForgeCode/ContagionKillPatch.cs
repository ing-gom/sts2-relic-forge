using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Contagious (전염의) — when you kill an enemy that is afflicted with Vulnerable, the debuffs it was
/// carrying (Vulnerable / Weak / Frail / Poison) LEAP to a random OTHER living enemy at their full amounts.
/// A "reclaim" boon: the debuffs you invested in a target aren't wasted when it dies — the plague jumps host.
///
/// Fires from <see cref="Hook.AfterDeath"/> (same hook as <see cref="KillGoldPatch"/>) and CHAINS AWAITED onto
/// <c>__result</c> so the spread runs in-order inside the synchronized death action. Co-op class = the enemy-state
/// prefixes (<see cref="ForgeCombatAffixPatch"/>'s symmetric/strip): applying a power to an ENEMY is simulated
/// identically on both peers, the target pick is seeded from host-authoritative synced state (run RNG seed + turn +
/// dead-enemy identity), and ownership is read from the synced relic list — so both peers resolve the same jump.
/// Applied on BOTH peers with NO IsMe gate (enemy combat state converges), exactly like KillGoldPatch's Finishing
/// block. The transferred power's source is the RECEIVING enemy itself (like the Eroding strip) so it reads as a
/// spread, not a fresh player-applied debuff — it won't double-fire the player's own "on debuff applied" synergies.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDeath))]
internal static class ContagionKillPatch
{
    // NOTE: the real Hook.AfterDeath is AfterDeath(IRunState runState, ICombatState combatState, Creature
    // creature, bool wasRemovalPrevented, float deathAnimLength) — it carries NO PlayerChoiceContext (unlike the
    // PowerModel.AfterDeath override). Harmony matches by name, so we take `combatState` from the hook and build a
    // fresh no-op BlockingPlayerChoiceContext for PowerCmd.Apply (no choice prompt is raised by a debuff apply).
    private static void Postfix(ref Task __result, ICombatState combatState, Creature creature, bool wasRemovalPrevented)
    {
        if (wasRemovalPrevented) return;                            // death prevented — not a kill
        if (creature == null || creature.Player != null) return;    // only ENEMY deaths spread

        // Snapshot the dead enemy's debuffs SYNCHRONOUSLY, before `await original` lets the death pipeline
        // clear its powers. Gate on Vulnerable — no Vulnerable, no spread (cheap early-out, no relic scan).
        decimal vuln = creature.GetPower<VulnerablePower>()?.Amount ?? 0m;
        if (vuln <= 0m) return;
        decimal weak   = creature.GetPower<WeakPower>()?.Amount ?? 0m;
        decimal frail  = creature.GetPower<FrailPower>()?.Amount ?? 0m;
        decimal poison = creature.GetPower<PoisonPower>()?.Amount ?? 0m;

        __result = After(__result, combatState, creature, vuln, weak, frail, poison);
    }

    private static async Task After(Task original, ICombatState combatState, Creature creature,
                                    decimal vuln, decimal weak, decimal frail, decimal poison)
    {
        await original;
        try
        {
            // Any alive player must own a non-suppressed Contagious relic (synced ownership → same on both peers).
            var run = RunManager.Instance;
            var players = run?.State?.Players;
            if (players == null) return;
            bool owned = false;
            foreach (var player in players)
            {
                foreach (var relic in new List<RelicModel>(player.Relics))
                {
                    if (RelicForgeService.IsForgeEffectSuppressed(relic)) continue;
                    var rec = RelicForgeService.RecordFor(relic);
                    if (rec == null || rec.Prefix.Length == 0) continue;
                    var pfx = PrefixTable.ByName(rec.Prefix);
                    if (pfx != null && pfx.SpreadDebuffsOnKill) { owned = true; break; }
                }
                if (owned) break;
            }
            if (!owned) return;

            // Candidate hosts = other living enemies (exclude the corpse and any already-dead).
            var cs = combatState ?? creature.CombatState;
            if (cs == null) return;
            var hosts = new List<Creature>();
            foreach (var e in cs.HittableEnemies)
                if (e != null && e != creature && e.IsAlive)
                    hosts.Add(e);
            if (hosts.Count == 0) return;                               // nowhere for the plague to jump

            // Deterministic target pick from synced state (both peers seed identically at this death).
            var first = players[0];
            uint baseSeed = ForgeSeed.Of(first?.RunState?.Rng, 0);
            int turn = first?.PlayerCombatState?.TurnNumber ?? 0;
            var rng = RngCompat.Create((uint)((int)baseSeed + turn * 47189
                                     + hosts.Count * 769
                                     + ForgeHash.Of(creature.Name ?? "")));
            var target = ForgeCombat.PickOne(hosts, rng);
            if (target == null) return;

            // Spread at full amounts. Source = the receiving enemy itself (a decay-style transfer, not an attack).
            // AfterDeath carries no PlayerChoiceContext; a no-op Blocking context suffices (no prompt is raised).
            var ctx = new BlockingPlayerChoiceContext();
            if (vuln   > 0m) await PowerCmd.Apply<VulnerablePower>(ctx, target, vuln,   target, null);
            if (weak   > 0m) await PowerCmd.Apply<WeakPower>(ctx, target, weak,         target, null);
            if (frail  > 0m) await PowerCmd.Apply<FrailPower>(ctx, target, frail,       target, null);
            if (poison > 0m) await PowerCmd.Apply<PoisonPower>(ctx, target, poison,     target, null);

            MainFile.Logger.Info($"[{MainFile.ModId}] Contagious: {creature.Name}'s debuffs "
                + $"(V{vuln}/W{weak}/F{frail}/P{poison}) jumped to {target.Name} on turn {turn}.");
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] contagion hook failed: {e.Message}"); }
    }
}

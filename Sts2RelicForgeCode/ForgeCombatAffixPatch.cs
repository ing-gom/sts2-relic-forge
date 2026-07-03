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

namespace Sts2RelicForge;

/// <summary>
/// Mixed (gamble) combat affixes that act on ENEMIES, not just the owner:
///   Eroding    — each turn, move ONE enemy power 1 toward zero (strips buffs AND your debuffs)
///   Exposing   — Vulnerable 1 to you AND all enemies at combat start (turn 1)
///   Enervating — Weak 1 to you AND all enemies at combat start (turn 1)
/// Fires from Hook.AfterPlayerTurnStart (same hook as the delayed/penalty prefixes). A relic
/// carries exactly one prefix, so the per-relic dispatch below hits at most one branch.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
internal static class ForgeCombatAffixPatch
{
    // The "common" buffs/debuffs an Eroding relic can strip. Each entry applies a SIGNED delta
    // (so -1 lowers a positive amount, +1 raises a negative one — both move toward zero). The
    // target creature is used as its own applier: this is a decay, not an attack, so it should
    // not read as "the player applied/removed a debuff".
    private static readonly Dictionary<Type, Func<PlayerChoiceContext, Creature, int, Task>> Strippers = new()
    {
        [typeof(StrengthPower)]   = (c, t, a) => PowerCmd.Apply<StrengthPower>(c, t, a, t, null),
        [typeof(DexterityPower)]  = (c, t, a) => PowerCmd.Apply<DexterityPower>(c, t, a, t, null),
        [typeof(WeakPower)]       = (c, t, a) => PowerCmd.Apply<WeakPower>(c, t, a, t, null),
        [typeof(VulnerablePower)] = (c, t, a) => PowerCmd.Apply<VulnerablePower>(c, t, a, t, null),
        [typeof(FrailPower)]      = (c, t, a) => PowerCmd.Apply<FrailPower>(c, t, a, t, null),
        [typeof(ArtifactPower)]   = (c, t, a) => PowerCmd.Apply<ArtifactPower>(c, t, a, t, null),
        [typeof(PlatingPower)]    = (c, t, a) => PowerCmd.Apply<PlatingPower>(c, t, a, t, null),
    };

    private static void Postfix(ICombatState combatState, PlayerChoiceContext choiceContext, Player player)
    {
        try
        {
            int turn = player.PlayerCombatState?.TurnNumber ?? 0;
            if (turn <= 0) return;
            uint seed = player.RunState.Rng.Seed;
            // Snapshot: applying a power won't change player.Relics, but be safe.
            foreach (var relic in new List<RelicModel>(player.Relics))
            {
                var rec = RelicForgeService.RecordFor(relic);
                if (rec == null || rec.Prefix.Length == 0) continue;
                var pfx = PrefixTable.ByName(rec.Prefix);
                if (pfx == null) continue;

                if (pfx.EnemyStrip)
                    StripOne(combatState, choiceContext, relic, seed, turn);
                else if (pfx.SymPower.Length > 0 && turn == 1)
                    ApplySymmetric(combatState, choiceContext, player, relic, pfx, seed, turn);
                else if (pfx.RandomDebuff)
                    ApplyRandomDebuff(combatState, choiceContext, player, relic, seed, turn);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] combat affix apply failed: {e.Message}");
        }
    }

    /// <summary>
    /// Move ONE strippable enemy power 1 toward zero. Never creates or deepens a power: amount 0
    /// is skipped (no candidate), and the delta is -sign(amount) so a value already at ±1 lands
    /// exactly on 0. The pick is deterministic per (seed, turn, relic) so a reload reproduces it.
    /// </summary>
    private static void StripOne(ICombatState cs, PlayerChoiceContext ctx, RelicModel relic, uint seed, int turn)
    {
        var candidates = new List<(Creature enemy, Type type, int amount)>();
        foreach (var enemy in cs.HittableEnemies)
            foreach (var p in enemy.Powers)
                if (p.Amount != 0 && Strippers.ContainsKey(p.GetType()))
                    candidates.Add((enemy, p.GetType(), p.Amount));
        if (candidates.Count == 0) return; // nothing to erode this turn

        var rng = new Rng((uint)((int)seed + turn * 51473 + StringHelper.GetDeterministicHashCode(relic.Id.Entry)));
        int idx = (int)(rng.NextFloat() * candidates.Count);
        if (idx >= candidates.Count) idx = candidates.Count - 1;
        var (enemy2, type, amount) = candidates[idx];
        int delta = amount > 0 ? -1 : 1; // toward zero, never past it

        relic.Flash();
        TaskHelper.RunSafely(Strippers[type](ctx, enemy2, delta));
        MainFile.Logger.Info($"[{MainFile.ModId}] Eroding: {type.Name} {amount}->{amount + delta} on turn {turn} ({relic.Id.Entry}).");
    }

    /// <summary>
    /// Chaotic gamble: each turn, a 50% chance to apply Vulnerable OR Weak 1 to EITHER one enemy
    /// (good) OR one player (bad) — a coin flip on both which debuff and which side. In MP the target
    /// is a single random creature of the chosen side. Deterministic per (seed, turn, relic) so a
    /// reload reproduces the rolls. Stacks with other relics (PowerCmd.Apply adds to existing powers).
    /// </summary>
    private static void ApplyRandomDebuff(ICombatState cs, PlayerChoiceContext ctx, Player player, RelicModel relic, uint seed, int turn)
    {
        var rng = new Rng((uint)((int)seed + turn * 39119 + StringHelper.GetDeterministicHashCode(relic.Id.Entry)));
        if (rng.NextFloat() >= 0.5f) return;   // 50% chance to fire this turn
        bool weak = rng.NextFloat() < 0.5f;    // Weak vs Vulnerable
        bool self = rng.NextFloat() < 0.5f;    // a player vs an enemy
        Creature source = player.Creature;

        Creature? target = ForgeCombat.PickOne(self ? cs.PlayerCreatures : cs.HittableEnemies, rng);
        if (target == null) return;

        relic.Flash();
        if (weak) TaskHelper.RunSafely(PowerCmd.Apply<WeakPower>(ctx, target, 1m, source, null));
        else      TaskHelper.RunSafely(PowerCmd.Apply<VulnerablePower>(ctx, target, 1m, source, null));
        MainFile.Logger.Info($"[{MainFile.ModId}] Chaotic: {(weak ? "Weak" : "Vulnerable")} 1 to a {(self ? "player" : "enemy")} on turn {turn} ({relic.Id.Entry}).");
    }

    /// <summary>
    /// Apply the debuff to ONE enemy AND ONE player at combat start (in MP, a single random creature
    /// of each side). Stacks with other relics' effects (PowerCmd.Apply adds to existing powers).
    /// </summary>
    private static void ApplySymmetric(ICombatState cs, PlayerChoiceContext ctx, Player player, RelicModel relic, Prefix pfx, uint seed, int turn)
    {
        int a = pfx.SymAmount;
        Creature source = player.Creature;
        var rng = new Rng((uint)((int)seed + turn * 22079 + StringHelper.GetDeterministicHashCode(relic.Id.Entry)));
        Creature? ally = ForgeCombat.PickOne(cs.PlayerCreatures, rng);
        Creature? enemy = ForgeCombat.PickOne(cs.HittableEnemies, rng);
        relic.Flash();
        switch (pfx.SymPower)
        {
            case "Vulnerable":
                if (ally != null)  TaskHelper.RunSafely(PowerCmd.Apply<VulnerablePower>(ctx, ally, a, source, null));
                if (enemy != null) TaskHelper.RunSafely(PowerCmd.Apply<VulnerablePower>(ctx, enemy, a, source, null));
                break;
            case "Weak":
                if (ally != null)  TaskHelper.RunSafely(PowerCmd.Apply<WeakPower>(ctx, ally, a, source, null));
                if (enemy != null) TaskHelper.RunSafely(PowerCmd.Apply<WeakPower>(ctx, enemy, a, source, null));
                break;
            default:
                return;
        }
        MainFile.Logger.Info($"[{MainFile.ModId}] {pfx.Name}: {pfx.SymPower} {a} to one player + one enemy ({relic.Id.Entry}).");
    }

}

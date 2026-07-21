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
using MegaCrit.Sts2.Core.ValueProps;

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
                if (RelicForgeService.IsForgeEffectSuppressed(relic)) continue;   // dead relic (spent/disabled/saturated) — no forge affix
                var rec = RelicForgeService.RecordFor(relic);
                if (rec == null || rec.Prefix.Length == 0) continue;
                var pfx = PrefixTable.ByName(rec.Prefix);
                if (pfx == null) continue;

                if (pfx.TurnEnergy > 0 || pfx.StartDamage > 0)
                    ApplyEnergyGamble(choiceContext, player, relic, pfx, turn);
                else if (pfx.EnemyStrip)
                    StripOne(combatState, choiceContext, relic, seed, turn);
                else if (pfx.SymPower.Length > 0 && turn == 1)
                    ApplySymmetric(combatState, choiceContext, player, relic, pfx, seed, turn);
                else if (pfx.RandomDebuff)
                    ApplyRandomDebuff(combatState, choiceContext, player, relic, seed, turn);
                else if (pfx.GoldStrengthPer > 0 && turn == 1)
                    ApplyGoldStrength(choiceContext, player, relic, pfx);
                else if (pfx.CurseScaling && turn == 1)
                    ApplyCurseScaling(choiceContext, player, relic);
                else if (pfx.StartPower.Length > 0 && turn == 1)
                    ApplyStartPower(choiceContext, player, relic, pfx);
                else if (pfx.AttunedBlockPer > 0 && turn == 1)
                    ApplyAttunedBlock(choiceContext, player, relic, pfx);
                else if (pfx.EnduringStr > 0 && turn >= 5)
                    ApplyEnduring(choiceContext, player, relic, pfx, turn);
                else if (pfx.StartVigor > 0 && turn == 1)
                    ApplyStartVigor(choiceContext, player, relic, pfx);
                else if (pfx.TurnVigor > 0)
                    ApplyTurnVigor(choiceContext, player, relic, pfx, turn);
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
    /// Chaotic gamble: each turn, a 50% chance to apply a RANDOM debuff — Vulnerable / Weak / Frail 1
    /// — to EITHER one enemy (good) OR one player (bad). Which debuff and which side are a coin flip.
    /// In MP the target is a single random creature of the chosen side. Deterministic per
    /// (seed, turn, relic) so a reload reproduces every roll. Stacks with other relics.
    /// </summary>
    private static void ApplyRandomDebuff(ICombatState cs, PlayerChoiceContext ctx, Player player, RelicModel relic, uint seed, int turn)
    {
        var rng = new Rng((uint)((int)seed + turn * 39119 + StringHelper.GetDeterministicHashCode(relic.Id.Entry)));
        if (rng.NextFloat() >= MetaAffix.Chance(0.5f, player)) return;   // 50% base; Catalytic aura doubles it (→100%)
        bool self = rng.NextFloat() < 0.5f;    // a player vs an enemy
        Creature source = player.Creature;

        Creature? target = ForgeCombat.PickOne(self ? cs.PlayerCreatures : cs.HittableEnemies, rng);
        if (target == null) return;

        int pick = (int)(rng.NextFloat() * 3);
        if (pick > 2) pick = 2;
        relic.Flash();
        string name;
        switch (pick)
        {
            case 0: TaskHelper.RunSafely(PowerCmd.Apply<VulnerablePower>(ctx, target, 1m, source, null)); name = "Vulnerable"; break;
            case 1: TaskHelper.RunSafely(PowerCmd.Apply<WeakPower>(ctx, target, 1m, source, null));       name = "Weak"; break;
            default: TaskHelper.RunSafely(PowerCmd.Apply<FrailPower>(ctx, target, 1m, source, null));     name = "Frail"; break;
        }
        MainFile.Logger.Info($"[{MainFile.ModId}] Chaotic: {name} to a {(self ? "player" : "enemy")} on turn {turn} ({relic.Id.Entry}).");
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

    /// <summary>
    /// Gilded — at combat start, grant Strength scaled by the player's wallet: +1 per
    /// <see cref="Prefix.GoldStrengthPer"/> gold (e.g. 300g -> +1, 600g -> +2). Read-only on gold
    /// (a pure boon; the Taxing curse is what drains it). Self-sourced so it reads as a buff.
    /// </summary>
    private static void ApplyGoldStrength(PlayerChoiceContext ctx, Player player, RelicModel relic, Prefix pfx)
    {
        if (player.Creature == null || pfx.GoldStrengthPer <= 0) return;
        int str = player.Gold / pfx.GoldStrengthPer;
        if (str <= 0) return;
        relic.Flash();
        TaskHelper.RunSafely(PowerCmd.Apply<StrengthPower>(ctx, player.Creature, str, player.Creature, null));
        MainFile.Logger.Info($"[{MainFile.ModId}] Gilded: +{str} Strength from {player.Gold} gold on turn 1 ({relic.Id.Entry}).");
    }

    // Cursebound ramp: total Str / Dex gained at combat start by the number of curses carried (index by count,
    // clamped to 5). At 5+ curses an extra Intangible 1 is granted. Monotonic — more curses ≥ fewer.
    private static readonly int[] CurseStr = { 0, 1, 1, 2, 3, 3 };
    private static readonly int[] CurseDex = { 0, 0, 1, 2, 2, 3 };

    /// <summary>The Cursebound combat-start package for <paramref name="curses"/> carried curses (clamped to 5):
    /// Str / Dex from the ramp table, plus Intangible 1 at 5+. Pure lookup — directly unit-testable (see SoloTest).</summary>
    internal static (int str, int dex, bool intangible) CurseRampFor(int curses)
    {
        if (curses <= 0) return (0, 0, false);
        int i = curses > 5 ? 5 : curses;
        return (CurseStr[i], CurseDex[i], i >= 5);
    }

    /// <summary>Cursebound (저주결속의) — turn 1: gain a Str/Dex/Intangible package scaled by the number of curses
    /// (self-curses + enemy-riders) the player carries. Self-sourced powers, applied from the co-op-verified
    /// turn-start choke point (deterministic count from the synced relic list) — co-op-safe.</summary>
    private static void ApplyCurseScaling(PlayerChoiceContext ctx, Player player, RelicModel relic)
    {
        var creature = player.Creature;
        if (creature == null) return;
        int curses = CountCurses(player);
        var (str, dex, intangible) = CurseRampFor(curses);
        if (str == 0 && dex == 0 && !intangible) return;
        relic.Flash();
        if (str > 0)     TaskHelper.RunSafely(PowerCmd.Apply<StrengthPower>(ctx, creature, str, creature, null));
        if (dex > 0)     TaskHelper.RunSafely(PowerCmd.Apply<DexterityPower>(ctx, creature, dex, creature, null));
        if (intangible)  TaskHelper.RunSafely(PowerCmd.Apply<IntangiblePower>(ctx, creature, 1, creature, null));
        MainFile.Logger.Info($"[{MainFile.ModId}] Cursebound: {curses} curses → Str+{str} Dex+{dex}{(intangible ? " Intangible+1" : "")} ({relic.Id.Entry}).");
    }

    /// <summary>Warding / Warded / Afterimage (and future defensive boons) — turn 1: grant a native defensive
    /// power to self. Self-sourced combat-start buff (the Gilded/SymPower class) → deterministic, co-op-safe.</summary>
    private static void ApplyStartPower(PlayerChoiceContext ctx, Player player, RelicModel relic, Prefix pfx)
    {
        var creature = player.Creature;
        if (creature == null || pfx.StartPowerAmount <= 0) return;
        int amt = pfx.StartPowerAmount + BolsterBoostFor(player);   // 북돋움의 aura: +1 per Bolstering relic carried
        Task? t = pfx.StartPower switch
        {
            "Artifact" => PowerCmd.Apply<ArtifactPower>(ctx, creature, amt, creature, null),
            "Buffer"   => PowerCmd.Apply<BufferPower>(ctx, creature, amt, creature, null),
            "Blur"     => PowerCmd.Apply<BlurPower>(ctx, creature, amt, creature, null),
            "Regen"    => PowerCmd.Apply<RegenPower>(ctx, creature, amt, creature, null),
            "Plated"   => PowerCmd.Apply<PlatingPower>(ctx, creature, amt, creature, null),
            _ => null,
        };
        if (t == null) return;
        relic.Flash();
        TaskHelper.RunSafely(t);
        MainFile.Logger.Info($"[{MainFile.ModId}] {pfx.Name}: {pfx.StartPower} {amt} on turn 1 ({relic.Id.Entry}).");
    }

    /// <summary>Enduring (지구전의): from combat turn 5 onward, gain Strength each turn — a long-fight closer.
    /// Strength is combat state, applied deterministically on both peers (co-op-safe like the energy prefixes).</summary>
    private static void ApplyEnduring(PlayerChoiceContext ctx, Player player, RelicModel relic, Prefix pfx, int turn)
    {
        var creature = player.Creature;
        if (creature == null) return;
        relic.Flash();
        TaskHelper.RunSafely(PowerCmd.Apply<StrengthPower>(ctx, creature, pfx.EnduringStr, creature, null));
        MainFile.Logger.Info($"[{MainFile.ModId}] Enduring: +{pfx.EnduringStr} Strength on turn {turn} ({relic.Id.Entry}).");
    }

    /// <summary>Roused (북받친) — turn 1: grant Vigor. Vigor is a VANILLA power, so this works on ANY
    /// character (mod characters included). Self-sourced combat-start buff on the co-op-verified turn-start
    /// choke (deterministic on both peers) — same class as ApplyStartPower / Enduring.</summary>
    private static void ApplyStartVigor(PlayerChoiceContext ctx, Player player, RelicModel relic, Prefix pfx)
    {
        var creature = player.Creature;
        if (creature == null || pfx.StartVigor <= 0) return;
        relic.Flash();
        TaskHelper.RunSafely(PowerCmd.Apply<VigorPower>(ctx, creature, pfx.StartVigor, creature, null));
        MainFile.Logger.Info($"[{MainFile.ModId}] {pfx.Name}: Vigor {pfx.StartVigor} on turn 1 ({relic.Id.Entry}).");
    }

    /// <summary>Invigorated (생기찬) — every turn: grant Vigor (consumed by the next Attack, so a steady
    /// trickle). Deterministic self-buff → co-op-safe, same class as Enduring.</summary>
    private static void ApplyTurnVigor(PlayerChoiceContext ctx, Player player, RelicModel relic, Prefix pfx, int turn)
    {
        var creature = player.Creature;
        if (creature == null || pfx.TurnVigor <= 0) return;
        relic.Flash();
        TaskHelper.RunSafely(PowerCmd.Apply<VigorPower>(ctx, creature, pfx.TurnVigor, creature, null));
        MainFile.Logger.Info($"[{MainFile.ModId}] {pfx.Name}: Vigor {pfx.TurnVigor} on turn {turn} ({relic.Id.Entry}).");
    }

    /// <summary>Bolstering (북돋움의) AURA: total +bonus to combat-start defensive power amounts from every
    /// live relic carrying it. Read from the (synced) relic list so both peers compute the same amount.</summary>
    private static int BolsterBoostFor(Player player)
    {
        int b = 0;
        foreach (var relic in new List<RelicModel>(player.Relics))
        {
            if (RelicForgeService.IsForgeEffectSuppressed(relic)) continue;
            var rec = RelicForgeService.RecordFor(relic);
            if (rec == null || rec.Prefix.Length == 0) continue;
            var pfx = PrefixTable.ByName(rec.Prefix);
            if (pfx != null) b += pfx.StartPowerBoost;
        }
        return b;
    }

    /// <summary>Attuned (조화의): at combat start, gain Block per OTHER live forged relic carried (companions and
    /// the Attuned relic itself excluded), capped at ×4. Block is deterministic combat state; the count comes from
    /// the synced relic list → both peers gain the same Block (co-op-safe, same class as ApplyStartPower).</summary>
    private static void ApplyAttunedBlock(PlayerChoiceContext ctx, Player player, RelicModel relic, Prefix pfx)
    {
        var creature = player.Creature;
        if (creature == null || pfx.AttunedBlockPer <= 0) return;
        int others = 0;
        foreach (var r in new List<RelicModel>(player.Relics))
        {
            if (ReferenceEquals(r, relic)) continue;                       // "OTHER" relics only
            if (RelicForgeService.IsForgeEffectSuppressed(r)) continue;
            if (RelicForgeService.IsCompanion(r)) continue;                // count genuine forged relics, not companions
            var rec = RelicForgeService.RecordFor(r);
            if (rec != null && rec.Prefix.Length > 0) others++;
        }
        if (others <= 0) return;
        int block = System.Math.Min(others * pfx.AttunedBlockPer, pfx.AttunedBlockPer * 4);   // cap at ×4 (= 8 at per 2)
        relic.Flash();
        TaskHelper.RunSafely(CreatureCmd.GainBlock(creature, block, ValueProp.Unpowered, null));
        MainFile.Logger.Info($"[{MainFile.ModId}] Attuned: +{block} Block from {others} other forged relic(s) on turn 1 ({relic.Id.Entry}).");
    }

    /// <summary>The number of curses on the player's live relics: each self-curse and each enemy-rider counts 1.</summary>
    internal static int CountCurses(Player player)
    {
        int n = 0;
        foreach (var r in new List<RelicModel>(player.Relics))
        {
            if (RelicForgeService.IsForgeEffectSuppressed(r)) continue;
            var rec = RelicForgeService.RecordFor(r);
            if (rec == null) continue;
            if (rec.SelfCurse.Length > 0) n++;
            if (rec.EnemyRider) n++;
        }
        return n;
    }

    /// <summary>
    /// Energy-gamble affixes (Immolating / Overclocked): grant <see cref="Prefix.TurnEnergy"/> bonus Energy
    /// at the start of EVERY turn (net gain — the turn refill uses SetEnergy, so GainEnergy stacks on top,
    /// same as the bonus-energy relics; this also feeds a Discharging relic), and pay a fixed one-time cost
    /// at combat start (turn 1): <see cref="Prefix.StartDamage"/> self-damage (unblockable at turn 1 — no
    /// block yet) and/or <see cref="Prefix.StartMaxHpLoss"/> PERMANENT Max HP loss (ramps across the run).
    /// All amounts are fixed (no RNG) and applied via commands from the co-op-verified turn-start choke
    /// point, so every peer reproduces them in lockstep. Self-sourced so the damage never reads as an enemy hit.
    /// </summary>
    private static void ApplyEnergyGamble(PlayerChoiceContext ctx, Player player, RelicModel relic, Prefix pfx, int turn)
    {
        var creature = player.Creature;
        if (creature == null) return;
        bool flashed = false;

        // Combat-start cost. Current-HP damage and Energy are LOCALLY simulated (not replicated), so they
        // must be applied on BOTH peers — each simulates the change and they converge (coop-verify confirmed
        // energy converges this way; gating current-HP to one peer instead DESYNCS, because the other peer
        // never receives the reduction). Max-HP, by contrast, is host-authoritative and REPLICATED, so a
        // both-peers apply double-counts it; StartMaxHpLoss (Overclocked) can't satisfy both rules at once —
        // LoseMaxHp mutates run-state Max-HP AND clamps current HP — so it is NOT co-op-safe via this hook
        // and is left OUT of the co-op path (see RelicForgeApi note). StartDamage stays ungated.
        // StartDamage (Immolating) is a flat combat-start CURRENT-HP hit — locally simulated, so a detached
        // both-peers apply converges (coop-verify GREEN). The permanent Max-HP cost (Overclocked) is NOT
        // here: Max-HP is host-authoritative/replicated and must be applied awaited-in-order — see
        // OverclockedMaxHpPatch.
        if (turn == 1 && pfx.StartDamage > 0)
        {
            // NEVER lethal: a combat-start self-cost must leave the player alive — cap the hit so at least
            // 1 HP remains (currentHp is the same on both peers at turn 1, so the clamp converges in co-op).
            int dmg = Math.Min(pfx.StartDamage, (int)creature.CurrentHp - 1);
            if (dmg > 0)
            {
                relic.Flash(); flashed = true;
                TaskHelper.RunSafely(CreatureCmd.Damage(ctx, creature, dmg, ValueProp.Unpowered, creature, null));
            }
        }

        if (pfx.TurnEnergy > 0)   // every turn
        {
            if (!flashed) relic.Flash();
            TaskHelper.RunSafely(PlayerCmd.GainEnergy(pfx.TurnEnergy, player));
        }

        MainFile.Logger.Info($"[{MainFile.ModId}] {pfx.Name}: +{pfx.TurnEnergy} energy"
            + (turn == 1 && pfx.StartDamage > 0 ? $" (start cost: {pfx.StartDamage} dmg)" : "")
            + $" on turn {turn} ({relic.Id.Entry}).");
    }

}

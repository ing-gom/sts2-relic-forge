using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2RelicForge;

/// <summary>Which fights a Max-HP curse reaches (see <see cref="EnemyForge.ApplyHpCurses"/>).</summary>
internal enum HpScope { EliteBoss, Normal, Elite, Boss, Any }

/// <summary>Applies one native power (or block) to an enemy with a computed amount.</summary>
internal delegate Task PowerApply(PlayerChoiceContext ctx, Creature enemy, int amount);

/// <summary>How a REACTIVE enemy curse fires (see <see cref="EnemyReactiveCursePatch"/>) — driven by damage
/// events rather than combat-start / per-turn. None = not reactive (a normal <see cref="EnemyEffect"/>).</summary>
internal enum ReactiveKind { None, OnHitStr, OnDealStr, OnDealDebuff, OnDealHeal, OnDealCard }

/// <summary>
/// One effect an enemy prefix grants: a FIXED amount of a native power (Base = the exact amount) or
/// bonus HP (IsHp, Base = fraction of the enemy's spawn MaxHp). Power amounts are fixed — NO random
/// range and NO magnitude/tier scaling. Each rider curse the player carries applies its exact value;
/// duplicated curses stack (each occurrence applies once). Everything reuses the game's own powers, so
/// each shows its native icon + tooltip for free.
/// </summary>
internal sealed class EnemyEffect
{
    public double Base;         // power effects: the fixed amount; HP curses: fraction of spawn MaxHp
    public bool IsHp;
    public int Period;          // 0 = applied once at combat start; >0 = re-applied every N turns
    public double Chance;       // 0 = always; else the per-turn probability a periodic effect fires
    public PowerApply? Apply;
    public ReactiveKind Reactive; // != None: a reactive curse; recorded on the tag, driven by a damage hook
    public int Reduction;         // > 0: PASSIVE — the enemy takes this much less damage per hit (Calloused)
}

/// <summary>A resolved recurring buff kept on a forged enemy — re-applied every <see cref="Period"/> turns.</summary>
internal sealed class Periodic
{
    public int Period;
    public int Amount;
    public double Chance;       // 0 = always; else the per-turn probability it fires
    public PowerApply Apply = null!;
}

/// <summary>One enemy-side prefix — the mirror of a player relic prefix, decorating an enemy.</summary>
internal sealed class EnemyPrefix
{
    public string Name = "";
    public string Ko = "";
    public string Zh = "";
    public string Color = "#e0554d";
    public HpScope Scope = HpScope.EliteBoss;  // which fights an HP effect on this prefix reaches
    public EnemyEffect[] Effects = Array.Empty<EnemyEffect>();

    /// <summary>Name in the game's current language via the relic_forge loc table (see <see cref="ForgeLoc"/>).</summary>
    public string Display => ForgeLoc.Get("ENEMYPREFIX_" + ForgeLoc.KeyOf(Name) + ".name", Name);
}

/// <summary>The visible decoration stored on a forged enemy (prefix name + tint) for the nameplate.</summary>
internal sealed class EnemyForgeTag
{
    public string Prefix = "";
    public string Color = "#e0554d";
    public readonly List<Periodic> Periodics = new();   // recurring buffs, driven each turn
    // Reactive curses (see EnemyReactiveCursePatch): driven by damage hooks, not per-turn. Stacked per curse.
    public int OnHitStr;       // Enraging: Strength the enemy gains each time IT is hit
    public int OnDealStr;      // Sadistic: Strength the enemy gains each time it damages the player
    public bool OnDealDebuff;  // Hexing: 50% to debuff the player when it damages them
    public int OnDealHealPct;  // Vampiric: % of the damage it deals the player that the enemy heals
    public bool OnDealCard;    // Fouling: add a Wound to the player's discard when it damages them
    // Passive (see EnemyDamageReductionPatch): the enemy takes this much less damage from each hit (Calloused).
    public int DamageReduction;
}

/// <summary>The enemy prefix pool (native buffs only) — each effect is a fixed amount.</summary>
internal static class EnemyPrefixTable
{
    // native power appliers (mirror DelayedCompanionPatch's PowerCmd.Apply<T> usage)
    private static Task Str(PlayerChoiceContext c, Creature e, int a)   => PowerCmd.Apply<StrengthPower>(c, e, a, e, null);
    private static Task Thorn(PlayerChoiceContext c, Creature e, int a) => PowerCmd.Apply<ThornsPower>(c, e, a, e, null);
    private static Task Plate(PlayerChoiceContext c, Creature e, int a) => PowerCmd.Apply<PlatingPower>(c, e, a, e, null);
    private static Task Barric(PlayerChoiceContext c, Creature e, int a)=> PowerCmd.Apply<BarricadePower>(c, e, a, e, null);
    private static Task Regen(PlayerChoiceContext c, Creature e, int a) => PowerCmd.Apply<RegenPower>(c, e, a, e, null);
    private static Task Artif(PlayerChoiceContext c, Creature e, int a) => PowerCmd.Apply<ArtifactPower>(c, e, a, e, null);
    private static Task Curl(PlayerChoiceContext c, Creature e, int a)  => PowerCmd.Apply<CurlUpPower>(c, e, a, e, null);
    private static Task Ritual(PlayerChoiceContext c, Creature e, int a)=> PowerCmd.Apply<RitualPower>(c, e, a, e, null);
    private static Task Buffer(PlayerChoiceContext c, Creature e, int a)=> PowerCmd.Apply<BufferPower>(c, e, a, e, null);
    private static Task Flame(PlayerChoiceContext c, Creature e, int a) => PowerCmd.Apply<FlameBarrierPower>(c, e, a, e, null);
    private static Task Block(PlayerChoiceContext c, Creature e, int a) => CreatureCmd.GainBlock(e, a, default, null);

    private static EnemyEffect Pow(PowerApply apply, int amount, int period = 0, double chance = 0)
        => new() { Apply = apply, Base = amount, Period = period, Chance = chance };
    // HP curses no longer carry a per-prefix magnitude — the UNIFIED stacking ramp in EnemyForge.HpFractionFor
    // sets the amount from how many HP curses reach a fight. Hp() is now just the "this is an HP curse" marker
    // (Base is unused for HP). The four HP curses differ ONLY by scope (which fights they reach).
    private static EnemyEffect Hp() => new() { IsHp = true };

    // Only the prefixes the rider suffixes map to (see RiderSuffix). Amounts are FIXED — each carried
    // rider curse applies exactly this, stacking if duplicated.
    public static readonly EnemyPrefix[] All =
    {
        new EnemyPrefix { Name = "Vicious", Ko = "사나운",   Zh = "凶猛的", Color = "#ff5533",
            Effects = new[] { Pow(Str, 2) } },                                    // Strength 2
        new EnemyPrefix { Name = "Armored", Ko = "강철의",   Zh = "钢铁的", Color = "#9fb2c9",
            Effects = new[] { Pow(Plate, 5) } },                                  // Plated Armor 5
        new EnemyPrefix { Name = "Spiny",   Ko = "가시돋친", Zh = "尖刺的", Color = "#7ed957",
            Effects = new[] { Pow(Thorn, 2) } },                                  // Thorns 2 (per curse; stacks)
        new EnemyPrefix { Name = "Regenerating", Ko = "재생하는", Zh = "再生的", Color = "#6ee0a0",
            Effects = new[] { Pow(Regen, 3, period: 1, chance: 0.5) } },          // 50% each turn: Regen 3
        new EnemyPrefix { Name = "Legendary", Ko = "전설적인", Zh = "传奇的", Color = "#ff8000",
            Effects = new[] { Pow(Str, 1), Pow(Plate, 4), Pow(Thorn, 1) } },      // Strength 1 + Plating 4 + Thorns 1
        new EnemyPrefix { Name = "Warded", Ko = "수호받은", Zh = "守护的", Color = "#ffd23f",
            Effects = new[] { Pow(Artif, 1) } },                                  // Artifact 1: negate your debuffs
        new EnemyPrefix { Name = "Shielded", Ko = "보호막의", Zh = "护盾的", Color = "#7ed0ff",
            Effects = new[] { Pow(Buffer, 1) } },                                 // Buffer 1: negate next hit
        new EnemyPrefix { Name = "Frenzied", Ko = "광란의", Zh = "狂乱的", Color = "#ff6b4d",
            Effects = new[] { Pow(Str, 2, period: 3) } },                         // Strength 2 every 3rd turn

        // Reactive curses (driven by EnemyReactiveCursePatch off damage events, not per-turn).
        new EnemyPrefix { Name = "Enraging", Ko = "격노한", Zh = "暴怒的", Color = "#ff5533",
            Effects = new[] { new EnemyEffect { Reactive = ReactiveKind.OnHitStr, Base = 1 } } },     // +1 Str each time hit
        new EnemyPrefix { Name = "Sadistic", Ko = "가학적인", Zh = "施虐的", Color = "#c04d6a",
            Effects = new[] { new EnemyEffect { Reactive = ReactiveKind.OnDealStr, Base = 1 } } },    // +1 Str each time it hits you
        new EnemyPrefix { Name = "Hexing", Ko = "저주하는", Zh = "咒缚的", Color = "#9b6bff",
            Effects = new[] { new EnemyEffect { Reactive = ReactiveKind.OnDealDebuff } } },           // 50%: debuff you when it hits you
        new EnemyPrefix { Name = "Vampiric", Ko = "흡혈하는", Zh = "吸血的", Color = "#c0335a",
            Effects = new[] { new EnemyEffect { Reactive = ReactiveKind.OnDealHeal, Base = 100 } } }, // heals 100% of damage it deals you
        new EnemyPrefix { Name = "Fouling", Ko = "오염시키는", Zh = "污染的", Color = "#8a6a4a",
            Effects = new[] { new EnemyEffect { Reactive = ReactiveKind.OnDealCard } } },             // adds a Wound to your discard when it hits you
        new EnemyPrefix { Name = "Calloused", Ko = "굳은살의", Zh = "老茧的", Color = "#9a8a6a",
            Effects = new[] { new EnemyEffect { Reduction = 1 } } },                                  // takes 1 less damage per hit

        // Max-HP curses — broadcast to ALL enemies in a scope-matching fight by EnemyForge.ApplyHpCurses
        // (NOT the per-enemy decoration path). GainMaxHp also heals, so they're tankier from turn 1. All four
        // share ONE unified stacking ramp (see EnemyForge.HpFractionFor: the Nth HP curse reaching a fight ->
        // 5/10/20/40/70/100% of spawn MaxHp, hard-capped). They differ ONLY by SCOPE — which fights they reach.
        new EnemyPrefix { Name = "Vigor",    Ko = "활력", Zh = "活力", Color = "#c0335a", Scope = HpScope.Normal,
            Effects = new[] { Hp() } },   // reaches: normal fights
        new EnemyPrefix { Name = "Girth",    Ko = "비대", Zh = "臃肿", Color = "#a03a5a", Scope = HpScope.Any,
            Effects = new[] { Hp() } },   // reaches: every fight
        new EnemyPrefix { Name = "Titan",    Ko = "거인", Zh = "巨人", Color = "#c04d33", Scope = HpScope.Elite,
            Effects = new[] { Hp() } },   // reaches: elites
        new EnemyPrefix { Name = "Eternity", Ko = "영겁", Zh = "永恒", Color = "#7a5ac0", Scope = HpScope.Boss,
            Effects = new[] { Hp() } },   // reaches: boss
    };

    /// <summary>Find a prefix by English name (case-insensitive), for the test console command.</summary>
    public static EnemyPrefix? ByName(string name)
    {
        foreach (var p in All)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }
}

/// <summary>
/// The "enemy forge" balance mechanism: while you carry relics that rolled the enemy-rider curse (see
/// <see cref="RelicForgeService"/>), the enemies in EVERY fight (normal, elite, boss) gain FIXED native
/// buffs matching those curses — Strength 2, Thorns 2, Plated Armor 5, Artifact 1, etc. Duplicated
/// curses stack. No random rolls, no magnitude/tier scaling: what the tooltip states is exactly what
/// enemies get. Everything is deterministic and applied via host-authoritative commands so co-op stays
/// in sync.
/// </summary>
internal static class EnemyForge
{
    private const double HeatCap = 2.0;

    /// <summary>Test override (console <c>enemyforge on</c>): forge any combat.</summary>
    public static bool TestForce;
    /// <summary>Test override (console <c>enemyforge boss</c>): treat the next fight as a boss (for HP-curse scope).</summary>
    public static bool TestAsBoss;
    /// <summary>Test override (console <c>enemyforge name &lt;X&gt;</c>): force this exact rider instead of the ones you carry.</summary>
    public static string? TestForcePrefix;
    private const double TestMagnitude = 1.6;

    private static readonly ConditionalWeakTable<Creature, EnemyForgeTag> Tags = new();
    // Enemies already given their Max-HP curse bonus this combat (idempotent against a re-invoke).
    private static readonly ConditionalWeakTable<Creature, object> HpCursed = new();
    private static readonly object HpMarker = new();

    public static EnemyForgeTag? TagOf(Creature enemy) => Tags.TryGetValue(enemy, out var t) ? t : null;

    /// <summary>
    /// "Rider heat" = how many enemy-rider curses the player carries (each counts 1). Only used as the
    /// combat gate (&gt; 0 = enemies fight back) and the test-command readout — it no longer scales the
    /// applied buff amounts, which are now fixed. No rider relics = 0.
    /// </summary>
    public static double ForgeHeat(Player player)
    {
        double heat = 0;
        foreach (var relic in player.Relics)
        {
            if (RelicForgeService.IsRelicSpent(relic)) continue;   // dead relic → its curse is off too
            var rec = RelicForgeService.RecordFor(relic);
            if (rec != null && rec.EnemyRider) heat += 1;
        }
        return heat;
    }

    /// <summary>Gate value: min(cap, rider count) × balance. &gt; 0 iff the player carries a rider curse
    /// (or a test force is on). No longer scales buff amounts — those are fixed.</summary>
    public static double Magnitude(Player player)
    {
        if (!HostForgeConfig.EnemyForgeEnabled && !TestForce) return 0;
        double balance = HostForgeConfig.BalanceStrength;
        if (balance <= 0) return TestForce ? TestMagnitude : 0;
        double mag = Math.Min(HeatCap, ForgeHeat(player)) * balance;
        return TestForce ? Math.Max(mag, TestMagnitude) : mag;
    }

    /// <summary>
    /// Apply the enemy-rider CURSES the player carries to the enemies in a fight: each rider suffix
    /// (Malice → Plated Armor 5, Wrath → Strength 2, …) is one application, distributed round-robin so a
    /// pack SHARES the curses. A single enemy (boss) gets them all. DUPLICATED curses STACK — carrying
    /// two Wrath curses applies Strength twice. HP curses are handled separately (ApplyHpCurses, all
    /// enemies). Deterministic (assignment is by list order, values are fixed).
    /// </summary>
    public static void ForgePack(IReadOnlyList<Creature> enemies, PlayerChoiceContext ctx, Player player)
    {
        if (!HostForgeConfig.EnemyForgeEnabled && !TestForce) return;
        if (enemies == null || enemies.Count == 0) return;

        // One entry per carried rider curse (duplicates preserved → stacking). Skip HP-only riders
        // (broadcast separately in ApplyHpCurses).
        var suffixes = new List<string>();
        foreach (var suffix in RiderSuffixes(player))
        {
            var def = RiderSuffix.ByKey(suffix);
            var prefix = def == null ? null : EnemyPrefixTable.ByName(def.PrefixName);
            if (def == null || prefix == null) continue;
            if (prefix.Effects.Length > 0 && prefix.Effects.All(e => e.IsHp)) continue;
            suffixes.Add(suffix);
        }
        if (suffixes.Count == 0) return;

        // Round-robin: curse i → enemy[i % count]. 1 enemy → all stack on it; N enemies → spread (and
        // still stacks when there are more curses than enemies).
        var buckets = new List<string>[enemies.Count];
        for (int i = 0; i < suffixes.Count; i++)
        {
            int e = i % enemies.Count;
            (buckets[e] ??= new List<string>()).Add(suffixes[i]);
        }
        for (int e = 0; e < enemies.Count; e++)
            if (buckets[e] != null)
                ForgeEnemyWith(enemies[e], buckets[e], ctx);
    }

    /// <summary>Apply a set of rider curses to one enemy (fixed buffs + nameplate). Guards against
    /// re-forging an already-tagged enemy; returns the tag, or null if nothing applied.</summary>
    private static EnemyForgeTag? ForgeEnemyWith(Creature enemy, List<string> suffixes, PlayerChoiceContext ctx)
    {
        if (enemy == null || Tags.TryGetValue(enemy, out _)) return null;

        var tag = new EnemyForgeTag();
        var names = new List<string>();
        foreach (var suffix in suffixes)
        {
            var def = RiderSuffix.ByKey(suffix);
            var prefix = def == null ? null : EnemyPrefixTable.ByName(def.PrefixName);
            if (def == null || prefix == null) continue;
            ApplyEffects(prefix, enemy, ctx, tag);   // fixed values; stacks per occurrence
            names.Add(def.Display);
            tag.Color = def.Color;
        }
        if (names.Count == 0) return null;

        tag.Prefix = JoinCounted(names);
        Tags.Add(enemy, tag);
        MainFile.Logger.Info($"[{MainFile.ModId}] enemy forge: {tag.Prefix} on '{enemy.Name}'.");
        return tag;
    }

    /// <summary>Join nameplate labels, collapsing duplicates to "name×N" so stacked curses read clearly.</summary>
    private static string JoinCounted(List<string> names)
    {
        var order = new List<string>();
        var count = new Dictionary<string, int>();
        foreach (var n in names)
        {
            if (!count.ContainsKey(n)) { count[n] = 0; order.Add(n); }
            count[n]++;
        }
        return string.Join(" ", order.Select(n => count[n] > 1 ? $"{n}×{count[n]}" : n));
    }

    /// <summary>The enemy-rider suffix key of EACH cursed relic the player carries (duplicates kept, so
    /// they stack). A test force previews one of each (or a single named rider).</summary>
    private static List<string> RiderSuffixes(Player player)
    {
        var list = new List<string>();
        if (TestForce)
        {
            var forced = string.IsNullOrEmpty(TestForcePrefix) ? null : RiderSuffix.Find(TestForcePrefix!);
            if (forced != null) list.Add(forced.En);
            else foreach (var s in RiderSuffix.All) list.Add(s.En);   // preview every suffix's buff
            return list;
        }
        foreach (var relic in player.Relics)
        {
            if (RelicForgeService.IsRelicSpent(relic)) continue;   // dead relic → its curse is off too
            var rec = RelicForgeService.RecordFor(relic);
            if (rec == null || !rec.EnemyRider || rec.EnemyRiderSuffix.Length == 0) continue;
            list.Add(rec.EnemyRiderSuffix);
        }
        return list;
    }

    private static void ApplyEffects(EnemyPrefix prefix, Creature enemy, PlayerChoiceContext ctx, EnemyForgeTag tag)
    {
        foreach (var eff in prefix.Effects)
        {
            try
            {
                if (eff.IsHp) continue;          // HP curses are applied by ApplyHpCurses, never here
                if (eff.Reduction > 0)           // PASSIVE damage reduction — recorded on the tag (Calloused)
                {
                    tag.DamageReduction += eff.Reduction;
                    MainFile.Logger.Info($"[{MainFile.ModId}]   damage reduction {eff.Reduction} → {enemy.Name}");
                    continue;
                }
                if (eff.Reactive != ReactiveKind.None)   // reactive curses are recorded on the tag, fired by a damage hook
                {
                    int r = AtLeastOne(eff.Base);
                    switch (eff.Reactive)
                    {
                        case ReactiveKind.OnHitStr:     tag.OnHitStr += r; break;
                        case ReactiveKind.OnDealStr:    tag.OnDealStr += r; break;
                        case ReactiveKind.OnDealDebuff: tag.OnDealDebuff = true; break;
                        case ReactiveKind.OnDealHeal:   tag.OnDealHealPct += r; break;
                        case ReactiveKind.OnDealCard:   tag.OnDealCard = true; break;
                    }
                    MainFile.Logger.Info($"[{MainFile.ModId}]   reactive {eff.Reactive} ({r}) → {enemy.Name}");
                    continue;
                }
                int amt = AtLeastOne(eff.Base);  // fixed amount
                if (eff.Apply == null) continue;

                if (eff.Period > 0)
                {
                    // Recurring: don't apply now — drive it every Period turns (see RunPeriodic).
                    tag.Periodics.Add(new Periodic { Period = eff.Period, Amount = amt, Chance = eff.Chance, Apply = eff.Apply });
                    MainFile.Logger.Info($"[{MainFile.ModId}]   {eff.Apply.Method.Name} {amt} every {eff.Period}t{(eff.Chance > 0 ? $" @{eff.Chance:P0}" : "")} → {enemy.Name}");
                }
                else
                {
                    TaskHelper.RunSafely(eff.Apply(ctx, enemy, amt));
                    MainFile.Logger.Info($"[{MainFile.ModId}]   {eff.Apply.Method.Name} {amt} → {enemy.Name}");
                }
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"[{MainFile.ModId}]   effect on {enemy.Name} FAILED: {e}");
            }
        }
    }

    /// <summary>Re-apply recurring buffs (Frenzied Strength, Regen…) on forged enemies each turn, with any per-turn chance.</summary>
    public static void RunPeriodic(ICombatState combatState, PlayerChoiceContext ctx, Player player, int turn)
    {
        uint seed = player.RunState?.Rng.Seed ?? 0;
        int floor = player.RunState?.TotalFloor ?? 0;
        foreach (var enemy in combatState.HittableEnemies)
        {
            var tag = TagOf(enemy);
            if (tag == null) continue;
            for (int i = 0; i < tag.Periodics.Count; i++)
            {
                var p = tag.Periodics[i];
                if (p.Period <= 0 || turn % p.Period != 0) continue;
                if (p.Chance > 0)   // seed-fixed per-turn roll (deterministic / MP-safe)
                {
                    var rng = new Rng((uint)((int)seed + floor * 7919 + turn * 48611 + i * 104729));
                    if (rng.NextFloat() >= p.Chance) continue;
                }
                try { TaskHelper.RunSafely(p.Apply(ctx, enemy, p.Amount)); }
                catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}]   periodic on {enemy.Name} FAILED: {e.Message}"); }
            }
        }
    }

    /// <summary>
    /// Apply the player's Max-HP curses (Vigor/Girth/Titan/Eternity riders) to EVERY enemy in the
    /// current fight whose room matches the curse's scope. The bonus is a fraction of each enemy's own
    /// spawn MaxHp, set by the UNIFIED stacking ramp (<see cref="HpFractionFor"/>): the more HP curses
    /// reach this fight, the steeper the bonus (5→10→20→40→70→100%, capped). Applied once per combat.
    /// <see cref="CreatureCmd.GainMaxHp"/> also heals, so they're tankier from turn 1. Gated by the same
    /// master toggle as the rest of the enemy forge.
    /// </summary>
    public static void ApplyHpCurses(ICombatState cs, PlayerChoiceContext ctx, Player player, RoomType room, bool isBoss)
    {
        if (!HostForgeConfig.EnemyForgeEnabled && !TestForce) return;
        foreach (var enemy in new List<Creature>(cs.HittableEnemies))
        {
            if (enemy == null || HpCursed.TryGetValue(enemy, out _)) continue;
            double frac = HpFractionFor(player, room, isBoss);
            if (frac <= 0) continue;
            int hp = AtLeastOne(enemy.MaxHp * frac);
            HpCursed.Add(enemy, HpMarker);
            TaskHelper.RunSafely(CreatureCmd.GainMaxHp(enemy, hp));
            MainFile.Logger.Info($"[{MainFile.ModId}] HP curse: +{hp} MaxHp ({frac:P0}) → {enemy.Name} (room {room}).");
        }
    }

    /// <summary>
    /// The UNIFIED HP-curse ramp: cumulative Max-HP fraction granted when <paramref name="count"/> HP curses
    /// reach a fight. Convex and HARD-CAPPED at 100% (enemies at most double their HP), so a single HP curse
    /// barely stings (5%) while stacking them compounds toward the ceiling — the "greed has a price" curve.
    /// </summary>
    internal static double HpRampFor(int count)
    {
        if (count <= 0) return 0;
        return HpRamp[Math.Min(count, HpRamp.Length) - 1];
    }
    private static readonly double[] HpRamp = { 0.05, 0.10, 0.20, 0.40, 0.70, 1.00 };

    /// <summary>Max-HP fraction for the current fight: COUNT the HP curses whose scope reaches this room
    /// (every HP-curse type counts toward the same stack — see <see cref="HpRampFor"/>), then ramp. Curse
    /// TYPE affects only which fights it reaches (scope), never the per-curse magnitude.</summary>
    private static double HpFractionFor(Player player, RoomType room, bool isBoss)
    {
        int count = 0;
        if (TestForce)   // console preview: count every HP curse whose scope matches this room
        {
            foreach (var p in EnemyPrefixTable.All)
                if (p.Effects.Any(e => e.IsHp) && ScopeMatches(p.Scope, room, isBoss)) count++;
            return HpRampFor(count);
        }
        foreach (var relic in player.Relics)
        {
            if (RelicForgeService.IsRelicSpent(relic)) continue;   // dead relic → its HP curse is off too
            var rec = RelicForgeService.RecordFor(relic);
            if (rec == null || !rec.EnemyRider || rec.EnemyRiderSuffix.Length == 0) continue;
            var def = RiderSuffix.ByKey(rec.EnemyRiderSuffix);
            var prefix = def == null ? null : EnemyPrefixTable.ByName(def.PrefixName);
            if (prefix == null) continue;
            if (prefix.Effects.Any(e => e.IsHp) && ScopeMatches(prefix.Scope, room, isBoss)) count++;
        }
        return HpRampFor(count);
    }

    private static bool ScopeMatches(HpScope scope, RoomType room, bool isBoss) => scope switch
    {
        HpScope.Any    => true,
        HpScope.Boss   => isBoss || room == RoomType.Boss,
        HpScope.Elite  => room == RoomType.Elite && !isBoss,
        HpScope.Normal => !isBoss && room != RoomType.Boss && room != RoomType.Elite,
        _              => isBoss || room == RoomType.Boss || room == RoomType.Elite,   // EliteBoss
    };

    private static int AtLeastOne(double v)
    {
        int r = (int)Math.Round(v, MidpointRounding.AwayFromZero);
        return r < 1 ? 1 : r;
    }
}

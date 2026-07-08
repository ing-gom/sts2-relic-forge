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
    private static EnemyEffect Hp(double frac) => new() { IsHp = true, Base = frac };

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

        // Max-HP curses — a FIXED fraction of each enemy's spawn MaxHp, broadcast to ALL enemies in a
        // scope-matching fight by EnemyForge.ApplyHpCurses (NOT the per-enemy decoration path).
        // GainMaxHp also heals, so they're genuinely tankier from turn 1.
        new EnemyPrefix { Name = "Vigor",    Ko = "활력", Zh = "活力", Color = "#c0335a", Scope = HpScope.Normal,
            Effects = new[] { Hp(0.20) } },   // normal fights: every enemy +20% Max HP
        new EnemyPrefix { Name = "Girth",    Ko = "비대", Zh = "臃肿", Color = "#a03a5a", Scope = HpScope.Any,
            Effects = new[] { Hp(0.12) } },   // all fights: every enemy +12% Max HP
        new EnemyPrefix { Name = "Titan",    Ko = "거인", Zh = "巨人", Color = "#c04d33", Scope = HpScope.Elite,
            Effects = new[] { Hp(0.30) } },   // elites: +30% Max HP
        new EnemyPrefix { Name = "Eternity", Ko = "영겁", Zh = "永恒", Color = "#7a5ac0", Scope = HpScope.Boss,
            Effects = new[] { Hp(0.25) } },   // boss: +25% Max HP
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
    /// current fight whose room matches the curse's scope. Each enemy's bonus is the summed fraction of
    /// its own spawn MaxHp (stacks if multiple HP-curse relics are carried), applied once per combat.
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

    /// <summary>Sum of the Max-HP fraction the player's HP-curse riders grant in the current room scope.</summary>
    private static double HpFractionFor(Player player, RoomType room, bool isBoss)
    {
        double frac = 0;
        if (TestForce)   // console preview: apply every HP curse whose scope matches this room
        {
            foreach (var p in EnemyPrefixTable.All)
                foreach (var eff in p.Effects)
                    if (eff.IsHp && ScopeMatches(p.Scope, room, isBoss)) frac += eff.Base;
            return frac;
        }
        foreach (var relic in player.Relics)
        {
            var rec = RelicForgeService.RecordFor(relic);
            if (rec == null || !rec.EnemyRider || rec.EnemyRiderSuffix.Length == 0) continue;
            var def = RiderSuffix.ByKey(rec.EnemyRiderSuffix);
            var prefix = def == null ? null : EnemyPrefixTable.ByName(def.PrefixName);
            if (prefix == null) continue;
            foreach (var eff in prefix.Effects)
                if (eff.IsHp && ScopeMatches(prefix.Scope, room, isBoss)) frac += eff.Base;
        }
        return frac;
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

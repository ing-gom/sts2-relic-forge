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
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2RelicForge;

/// <summary>Applies one native power (or block) to an enemy with a computed amount.</summary>
internal delegate Task PowerApply(PlayerChoiceContext ctx, Creature enemy, int amount);

/// <summary>
/// One effect an enemy prefix grants: a native power (Base = per-unit-intensity amount, optional
/// Cap) or bonus HP (IsHp, Base = fraction of the enemy's spawn MaxHp). Everything reuses the game's
/// own powers, so each shows its native icon + tooltip for free.
/// </summary>
internal sealed class EnemyEffect
{
    public double Base;         // low end of the per-unit-intensity amount
    public double BaseMax;      // high end (if > Base, the amount is rolled in [Base, BaseMax])
    public int Cap;
    public bool IsHp;
    public int Period;          // 0 = applied once at combat start; >0 = re-applied every N turns
    public PowerApply? Apply;

    /// <summary>Roll the base within its range using a seed-fixed rng (deterministic / MP-safe).</summary>
    public double RollBase(Rng rng) => BaseMax > Base ? Base + rng.NextFloat() * (BaseMax - Base) : Base;
}

/// <summary>A resolved recurring buff kept on a forged enemy — re-applied every <see cref="Period"/> turns.</summary>
internal sealed class Periodic
{
    public int Period;
    public int Amount;
    public PowerApply Apply = null!;
}

/// <summary>One enemy-side prefix — the mirror of a player relic prefix, decorating an elite/boss.</summary>
internal sealed class EnemyPrefix
{
    public string Name = "";
    public string Ko = "";
    public string Zh = "";
    public double Weight;
    public double MinMag;             // eligible only when magnitude >= this
    public string Color = "#e0554d";
    public EnemyEffect[] Effects = Array.Empty<EnemyEffect>();

    public string Display
    {
        get
        {
            string lang = LocManager.Instance?.Language ?? "";
            if (lang.StartsWith("ko") && Ko.Length > 0) return Ko;
            if (lang.StartsWith("zh") && Zh.Length > 0) return Zh;
            return Name;
        }
    }
}

/// <summary>The visible decoration stored on a forged enemy (prefix name + tint) for the nameplate.</summary>
internal sealed class EnemyForgeTag
{
    public string Prefix = "";
    public string Color = "#e0554d";
    public readonly List<Periodic> Periodics = new();   // recurring buffs, driven each turn
}

/// <summary>The enemy prefix pool (native buffs only) and a magnitude-gated weighted pick.</summary>
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

    private static EnemyEffect Pow(PowerApply apply, double min, double max, int cap = 0, int period = 0) => new() { Apply = apply, Base = min, BaseMax = max, Cap = cap, Period = period };
    private static EnemyEffect Hp(double min, double max) => new() { IsHp = true, Base = min, BaseMax = max };

    // Only the prefixes the rider suffixes map to (see RiderSuffix). Amounts are RANGES — the
    // actual value is rolled within [min, max] with a seed-fixed rng, then scaled by contribution.
    public static readonly EnemyPrefix[] All =
    {
        new EnemyPrefix { Name = "Vicious", Ko = "사나운",   Zh = "凶猛的", Color = "#ff5533",
            Effects = new[] { Pow(Str, 1, 3) } },                                 // Strength
        new EnemyPrefix { Name = "Armored", Ko = "강철의",   Zh = "钢铁的", Color = "#9fb2c9",
            Effects = new[] { Pow(Plate, 4, 8) } },                               // Plated Armor (block/turn)
        new EnemyPrefix { Name = "Spiny",   Ko = "가시돋친", Zh = "尖刺的", Color = "#7ed957",
            Effects = new[] { Pow(Thorn, 2, 5) } },                               // Thorns
        new EnemyPrefix { Name = "Regenerating", Ko = "재생하는", Zh = "再生的", Color = "#6ee0a0",
            Effects = new[] { Pow(Regen, 3, 6) } },                               // Regen (heal/turn)
        new EnemyPrefix { Name = "Legendary", Ko = "전설적인", Zh = "传奇的", Color = "#ff8000",
            Effects = new[] { Pow(Str, 1, 2), Pow(Plate, 3, 5), Pow(Thorn, 2, 4) } }, // Strength + Plating + Thorns (maxes shaved)
        new EnemyPrefix { Name = "Warded", Ko = "수호받은", Zh = "守护的", Color = "#ffd23f",
            Effects = new[] { Pow(Artif, 1, 2, cap: 3) } },                           // Artifact: negate your debuffs
        new EnemyPrefix { Name = "Shielded", Ko = "보호막의", Zh = "护盾的", Color = "#7ed0ff",
            Effects = new[] { Pow(Buffer, 1, 2, cap: 2) } },                          // Buffer: negate next hits
        new EnemyPrefix { Name = "Frenzied", Ko = "광란의", Zh = "狂乱的", Color = "#ff6b4d",
            Effects = new[] { Pow(Str, 1, 2, period: 3) } },                          // +Strength every 3rd turn
    };

    /// <summary>Find a prefix by English name (case-insensitive), for the test console command.</summary>
    public static EnemyPrefix? ByName(string name)
    {
        foreach (var p in All)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>Prefixes eligible at the given magnitude (MinMag gate met).</summary>
    public static List<EnemyPrefix> Eligible(double mag) => All.Where(p => mag >= p.MinMag).ToList();

    /// <summary>Weighted, deterministic pick from a pool.</summary>
    public static EnemyPrefix Roll(List<EnemyPrefix> pool, Rng rng)
    {
        double total = 0;
        foreach (var p in pool) total += p.Weight;
        double r = rng.NextFloat() * total;
        foreach (var p in pool)
        {
            r -= p.Weight;
            if (r < 0) return p;
        }
        return pool[pool.Count - 1];
    }
}

/// <summary>
/// The "enemy forge" balance mechanism: while you carry relics that rolled the enemy-rider curse
/// (see <see cref="RelicForgeService"/>), elites and bosses gain native buffs (Strength, Block,
/// Plated Armor, Regen, Thorns, and other native powers) scaled by how much rider power you hold.
/// Everything is deterministic and applied via host-authoritative commands so co-op stays in sync.
/// </summary>
internal static class EnemyForge
{
    private const double HeatCap = 2.0;
    private const double BossTierMult = 1.5;
    private const double EliteTierMult = 1.0;
    private const double ComboMagnitude = 1.5;   // above this a boss gets a SECOND prefix

    /// <summary>Test override (console <c>enemyforge on</c>): forge any combat at a visible magnitude.</summary>
    public static bool TestForce;
    /// <summary>Test override (console <c>enemyforge boss</c>): treat the next fight as a boss (×1.5).</summary>
    public static bool TestAsBoss;
    /// <summary>Test override (console <c>enemyforge name &lt;X&gt;</c>): force this exact prefix instead of rolling.</summary>
    public static string? TestForcePrefix;
    private const double TestMagnitude = 1.6;

    private static readonly ConditionalWeakTable<Creature, EnemyForgeTag> Tags = new();

    public static EnemyForgeTag? TagOf(Creature enemy) => Tags.TryGetValue(enemy, out var t) ? t : null;

    /// <summary>
    /// Total "rider heat": each relic that rolled the enemy-rider curse contributes in proportion to
    /// its prefix power (floored so companion-family curses still count). No rider relics = 0.
    /// </summary>
    public static double ForgeHeat(Player player)
    {
        double heat = 0;
        foreach (var relic in player.Relics)
        {
            var rec = RelicForgeService.RecordFor(relic);
            if (rec != null && rec.EnemyRider) heat += Math.Max(0.15, rec.Percent);
        }
        return heat;
    }

    /// <summary>Intensity = min(cap, rider-heat) × balance. Gated by the master toggle; 0 = no enemy buff.</summary>
    public static double Magnitude(Player player)
    {
        if (!ForgeConfig.EnemyForgeEnabled && !TestForce) return 0;
        double balance = ForgeConfig.BalanceStrength;
        if (balance <= 0) return TestForce ? TestMagnitude : 0;
        double mag = Math.Min(HeatCap, ForgeHeat(player)) * balance;
        return TestForce ? Math.Max(mag, TestMagnitude) : mag;
    }

    /// <summary>
    /// Apply, to one elite/boss, the buffs dictated by the enemy-rider CURSES the player is carrying:
    /// each rider relic's suffix (Malice → Plated Armor, Wrath → Strength, …) grants that enemy the
    /// mapped buff, scaled by the relic's power × balance × tier. So the relic tooltip's promise is
    /// exactly what the enemy gets. Deterministic (no rng — driven by the player's relics).
    /// </summary>
    public static EnemyForgeTag? ForgeEnemy(Creature enemy, bool isBoss, PlayerChoiceContext ctx, Player player, Rng rng)
    {
        if (enemy == null || Tags.TryGetValue(enemy, out _)) return null;
        if (!ForgeConfig.EnemyForgeEnabled && !TestForce) return null;
        double balance = ForgeConfig.BalanceStrength;
        if (balance <= 0 && !TestForce) return null;
        double tier = isBoss ? BossTierMult : EliteTierMult;

        var contrib = Contributions(player);
        if (contrib.Count == 0) return null;

        var tag = new EnemyForgeTag();
        var names = new List<string>();
        foreach (var kv in contrib)
        {
            var def = RiderSuffix.ByKey(kv.Key);
            var prefix = def == null ? null : EnemyPrefixTable.ByName(def.PrefixName);
            if (def == null || prefix == null) continue;
            double mag = Math.Min(HeatCap, kv.Value) * (balance <= 0 ? 1.0 : balance);
            if (TestForce) mag = Math.Max(mag, 1.0);
            ApplyEffects(prefix, enemy, mag, tier, ctx, rng, tag);
            names.Add(def.Display);
            tag.Color = def.Color;
        }
        if (names.Count == 0) return null;

        tag.Prefix = string.Join(" ", names);
        Tags.Add(enemy, tag);
        MainFile.Logger.Info($"[{MainFile.ModId}] enemy forge: {string.Join("+", names)} on {(isBoss ? "boss" : "elite")} '{enemy.Name}'.");
        return tag;
    }

    /// <summary>Sum, per rider suffix, the contribution of the player's cursed relics (or a test preview).</summary>
    private static Dictionary<string, double> Contributions(Player player)
    {
        var map = new Dictionary<string, double>();
        if (TestForce)
        {
            var forced = string.IsNullOrEmpty(TestForcePrefix) ? null : RiderSuffix.Find(TestForcePrefix!);
            if (forced != null) map[forced.En] = 1.0;
            else foreach (var s in RiderSuffix.All) map[s.En] = 1.0;   // preview every suffix's buff
            return map;
        }
        foreach (var relic in player.Relics)
        {
            var rec = RelicForgeService.RecordFor(relic);
            if (rec == null || !rec.EnemyRider || rec.EnemyRiderSuffix.Length == 0) continue;
            double c = Math.Max(0.15, rec.Percent);
            map[rec.EnemyRiderSuffix] = (map.TryGetValue(rec.EnemyRiderSuffix, out var v) ? v : 0) + c;
        }
        return map;
    }

    private static void ApplyEffects(EnemyPrefix prefix, Creature enemy, double mag, double tier, PlayerChoiceContext ctx, Rng rng, EnemyForgeTag tag)
    {
        foreach (var eff in prefix.Effects)
        {
            try
            {
                double b = eff.RollBase(rng);   // seed-fixed roll within [min, max]
                if (eff.IsHp)
                {
                    int hp = AtLeastOne(enemy.MaxHp * b * mag * tier);
                    TaskHelper.RunSafely(CreatureCmd.GainMaxHp(enemy, hp));
                    MainFile.Logger.Info($"[{MainFile.ModId}]   +{hp} MaxHp → {enemy.Name}");
                    continue;
                }
                int amt = AtLeastOne(b * mag * tier);
                if (eff.Cap > 0 && amt > eff.Cap) amt = eff.Cap;
                if (eff.Apply == null) continue;

                if (eff.Period > 0)
                {
                    // Recurring: don't apply now — drive it every Period turns (see RunPeriodic).
                    tag.Periodics.Add(new Periodic { Period = eff.Period, Amount = amt, Apply = eff.Apply });
                    MainFile.Logger.Info($"[{MainFile.ModId}]   {eff.Apply.Method.Name} {amt} every {eff.Period}t → {enemy.Name}");
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

    /// <summary>Re-apply recurring buffs (e.g. Frenzied Strength) on forged enemies each turn.</summary>
    public static void RunPeriodic(ICombatState combatState, PlayerChoiceContext ctx, int turn)
    {
        foreach (var enemy in combatState.HittableEnemies)
        {
            var tag = TagOf(enemy);
            if (tag == null) continue;
            foreach (var p in tag.Periodics)
                if (p.Period > 0 && turn % p.Period == 0)
                {
                    try { TaskHelper.RunSafely(p.Apply(ctx, enemy, p.Amount)); }
                    catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}]   periodic on {enemy.Name} FAILED: {e.Message}"); }
                }
        }
    }

    private static int AtLeastOne(double v)
    {
        int r = (int)Math.Round(v, MidpointRounding.AwayFromZero);
        return r < 1 ? 1 : r;
    }
}

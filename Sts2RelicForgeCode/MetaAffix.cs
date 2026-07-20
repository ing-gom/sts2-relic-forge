using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Catalytic (촉매의) — a cross-relic AURA prefix. While the player owns a live relic whose rolled prefix is
/// Catalytic (<see cref="Prefix.ProcDoubler"/>), EVERY probabilistic COMBAT proc of the player's OTHER forged
/// prefixes rolls at DOUBLE its base chance — boons AND curses alike (the mod's gamble: twice the payoff,
/// twice the backfire). It does NOT touch the FORGE-TIME curse chance (RelicForgeService.EffectiveCurseChance),
/// only in-combat rolls, per the design ("전투 발동만").
///
/// This changes only the THRESHOLD a proc is compared against, never the RNG stream, and the ownership check
/// reads the host-authoritative synced relic list — so every peer computes the same doubled decision from the
/// same seeded roll (co-op-safe, same class as the base chance procs). Char-affix procs are covered centrally
/// in <see cref="CharAffix.Roll"/> (halving the roll doubles the fire rate of every `Roll() &lt; chance` site);
/// the few procs that roll their own Rng (Fickle / Chaotic / Adrenal) call <see cref="Chance"/> / <see cref="ChancePct"/>.
/// </summary>
internal static class MetaAffix
{
    /// <summary>True while the player owns a live Catalytic relic (<see cref="Prefix.ProcDoubler"/>) — DOUBLES
    /// all their other in-combat prefix procs.</summary>
    internal static bool Doubled(Player? player) => Owns(player, p => p.ProcDoubler);

    /// <summary>True while the player owns a live Empowering relic (<see cref="Prefix.ProcReroll"/>) — gives their
    /// other procs a SECOND roll (advantage: fires if either succeeds, 1-(1-p)^2). Catalytic takes precedence.</summary>
    internal static bool Rerolled(Player? player) => Owns(player, p => p.ProcReroll);

    /// <summary>The largest fixed proc-chance BONUS (percentage points) among the player's live Priming relics
    /// (<see cref="Prefix.ProcBoostPct"/>). Boosters don't stack (max). Lowest precedence of the three auras.</summary>
    internal static int BoostPct(Player? player) => Max(player, p => p.ProcBoostPct);

    private static bool Owns(Player? player, System.Func<Prefix, bool> pred) => Max(player, p => pred(p) ? 1 : 0) > 0;

    /// <summary>Max of <paramref name="sel"/> over the player's live forged relics' prefixes (0 if none).</summary>
    private static int Max(Player? player, System.Func<Prefix, int> sel)
    {
        if (player == null) return 0;
        int best = 0;
        foreach (var relic in new List<RelicModel>(player.Relics))
        {
            if (RelicForgeService.IsForgeEffectSuppressed(relic)) continue;
            var rec = RelicForgeService.RecordFor(relic);
            if (rec == null || rec.Prefix.Length == 0) continue;
            var pfx = PrefixTable.ByName(rec.Prefix);
            if (pfx == null) continue;
            int v = sel(pfx);
            if (v > best) best = v;
        }
        return best;
    }

    /// <summary>Transform a `Roll() &lt; chance` site's roll to the AURA-adjusted rate. Catalytic halves the roll
    /// (raw*0.5 &lt; c ⟺ raw &lt; 2c → double); Empowering takes the MIN of two independent rolls (min &lt; c ⟺ either
    /// &lt; c → a genuine second attempt, advantage 1-(1-c)^2); Priming subtracts its flat bonus (raw - b &lt; c ⟺
    /// raw &lt; c + b). Precedence 촉매 &gt; 증강 &gt; 촉진 (never composes) → identical to <see cref="Chance"/> per site.</summary>
    internal static float AdjustRoll(float raw, float raw2, Player? player)
    {
        if (Doubled(player)) return raw * 0.5f;
        if (Rerolled(player)) return raw < raw2 ? raw : raw2;   // min of two rolls = advantage
        int b = BoostPct(player);
        return b > 0 ? raw - b / 100f : raw;
    }

    /// <summary>A 0..1 proc chance under the aura: doubled (Catalytic), advantage 1-(1-p)^2 (Empowering), or
    /// flat-boosted (Priming). Clamped to 1. Same precedence as <see cref="AdjustRoll"/>.</summary>
    internal static float Chance(float baseChance, Player? player)
    {
        if (Doubled(player)) return baseChance * 2f > 1f ? 1f : baseChance * 2f;
        if (Rerolled(player)) return 1f - (1f - baseChance) * (1f - baseChance);
        int b = BoostPct(player);
        if (b <= 0) return baseChance;
        float c = baseChance + b / 100f;
        return c > 1f ? 1f : c;
    }

    /// <summary>A 0..100 percent proc chance under the aura (mirror of <see cref="Chance"/>), clamped to 100.</summary>
    internal static int ChancePct(int pct, Player? player)
    {
        if (Doubled(player)) return pct * 2 > 100 ? 100 : pct * 2;
        if (Rerolled(player)) return 100 - (100 - pct) * (100 - pct) / 100;   // integer advantage
        int b = BoostPct(player);
        if (b <= 0) return pct;
        return pct + b > 100 ? 100 : pct + b;
    }
}

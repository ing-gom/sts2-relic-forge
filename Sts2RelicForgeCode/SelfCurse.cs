using System.Collections.Generic;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2RelicForge;

/// <summary>
/// One SELF-CURSE: an independent "저주" a forged relic may carry ON TOP of its prefix and any
/// enemy-rider curse. Where the enemy-rider curse strengthens elites/bosses, a self-curse punishes
/// the OWNER — it fires each time the player takes UNBLOCKED damage (block failed), proportional to
/// the hit count (see <see cref="UnblockedHitPenaltyPatch"/>). Its own roll dimension, so it neither
/// competes in the prefix pool nor shares the enemy-rider slot.
/// </summary>
internal sealed class SelfCurseDef
{
    public string En = "", Ko = "", Zh = "";        // curse name (English key + localized)
    public string Color = "#c0554d";                // red-ish tint for the tooltip line
    public string OnHitPower = "";                  // "Weak" / "Frail" / "Vulnerable" — 1 to self per hit
    public bool OnHitCard;                          // instead: add a Dazed to the draw pile per hit
    public bool OnHitRandom;                        // instead: a random one of Weak / Frail / Vulnerable
    public string EffKo = "", EffEn = "", EffZh = ""; // short "on unblocked hit …" line

    /// <summary>Stable loc-key base derived from the English name (see <see cref="ForgeLoc"/>).</summary>
    internal string LocKeyBase => "SELFCURSE_" + ForgeLoc.KeyOf(En);

    public string Display => ForgeLoc.Get(LocKeyBase + ".name", En);
    public string Effect => ForgeLoc.Get(LocKeyBase + ".effect", EffEn);
}

/// <summary>The self-curse pool (all player-side on-hit penalties) and a uniform deterministic pick.</summary>
internal static class SelfCurseTable
{
    public static readonly SelfCurseDef[] All =
    {
        new SelfCurseDef { En = "Enfeebling",  Ko = "무력의", Zh = "孱弱的", Color = "#bf5a5a", OnHitPower = "Weak",
            EffKo = "막지 못한 피격마다 자신에게 약화 1", EffEn = "Weak 1 to self on each unblocked hit", EffZh = "每次未格挡受击时给予自己1虚弱" },
        new SelfCurseDef { En = "Cracking",    Ko = "균열의", Zh = "龟裂的", Color = "#a8926a", OnHitPower = "Frail",
            EffKo = "막지 못한 피격마다 자신에게 손상 1", EffEn = "Frail 1 to self on each unblocked hit", EffZh = "每次未格挡受击时给予自己1脆弱" },
        new SelfCurseDef { En = "Vulnerating", Ko = "취약의", Zh = "易伤的", Color = "#c8704d", OnHitPower = "Vulnerable",
            EffKo = "막지 못한 피격마다 자신에게 취약 1", EffEn = "Vulnerable 1 to self on each unblocked hit", EffZh = "每次未格挡受击时给予自己1易伤" },
        new SelfCurseDef { En = "Bewildering", Ko = "혼미의", Zh = "眩乱的", Color = "#6a7a8a", OnHitCard = true,
            EffKo = "막지 못한 피격마다 뽑을 더미에 현기증 1장", EffEn = "Adds a Dazed to your draw pile on each unblocked hit", EffZh = "每次未格挡受击时将1张眩晕加入抽牌堆" },
        new SelfCurseDef { En = "Wretched",    Ko = "비참의", Zh = "悲惨的", Color = "#8a5b7f", OnHitRandom = true,
            EffKo = "막지 못한 피격마다 자신에게 약화·손상·취약 중 무작위 1", EffEn = "A random Weak / Frail / Vulnerable 1 to self on each unblocked hit", EffZh = "每次未格挡受击时给予自己随机1层虚弱/脆弱/易伤" },
    };

    // --- External self-curse registration (public API via RelicForgeApi.RegisterSelfCurse) -----------
    // Sister mods add DATA-DRIVEN self-curses (on-hit Weak/Frail/Vulnerable, a Dazed card, or a random
    // debuff). ★CO-OP CONTRACT identical to PrefixTable: every peer must register the SAME curses in the
    // SAME order — PickCombined is a seed-deterministic uniform pick over the pool.
    private static readonly List<SelfCurseDef> _external = new();
    private static SelfCurseDef[] _pool = All;

    /// <summary>The full self-curse pool (built-ins + externally registered), for the pick and loc.</summary>
    internal static IReadOnlyList<SelfCurseDef> Pool => _pool;

    /// <summary>Append an external self-curse. Rejected (logged) on empty/duplicate name. Rebuilds the pool
    /// + invalidates the loc cache. Init-time only (see RelicForgeApi).</summary>
    internal static bool RegisterExternal(SelfCurseDef c)
    {
        if (c == null || string.IsNullOrEmpty(c.En)) return false;
        // '|' is the forge-descriptor field delimiter — a curse key containing it would corrupt the
        // saved/wire descriptor of every relic that rolls this curse (see PrefixTable.RegisterExternal).
        if (c.En.IndexOf('|') >= 0)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] RegisterSelfCurse: '{c.En}' contains the descriptor delimiter '|' — rejected.");
            return false;
        }
        if (ByKey(c.En) != null)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] RegisterSelfCurse: '{c.En}' collides with an existing curse — ignored.");
            return false;
        }
        _external.Add(c);
        // Order-insensitive combined pool — see PrefixTable.RegisterExternal (same rationale).
        _external.Sort((a, b) => string.CompareOrdinal(a.En, b.En));
        var combined = new SelfCurseDef[All.Length + _external.Count];
        All.CopyTo(combined, 0);
        for (int i = 0; i < _external.Count; i++) combined[All.Length + i] = _external[i];
        _pool = combined;
        ForgeLoc.Invalidate();
        return true;
    }

    public static SelfCurseDef? ByKey(string en)
    {
        foreach (var c in _pool) if (c.En == en) return c;
        return null;
    }

    /// <summary>Uniform deterministic pick from a 0..1 roll. (RiderSuffix.Pick is weighted; self-curses
    /// stay uniform since none is disproportionately punishing.)</summary>
    public static SelfCurseDef Pick(double roll)
    {
        var pool = _pool;
        int i = (int)(roll * pool.Length);
        if (i >= pool.Length) i = pool.Length - 1;
        if (i < 0) i = 0;
        return pool[i];
    }

    public static string Localize(string en) => ByKey(en)?.Display ?? en;

    /// <summary>Localized "on unblocked hit …" line for a stored curse key (empty if unknown).</summary>
    public static string EffectOf(string en) => ByKey(en)?.Effect ?? "";

    /// <summary>The COMBINED curse pool a forged relic draws its self-curse from: the on-hit curses in
    /// <see cref="All"/> PLUS every character-eligible PENALTY prefix (its downside re-homed onto the
    /// curse side — the effect + original trigger still fire via that penalty's own patch, which now
    /// reads rec.SelfCurse). Returns the stored key (an on-hit curse's <c>En</c> name OR a penalty
    /// prefix's <c>Name</c>); the two namespaces are disjoint so each dispatcher skips keys it doesn't own.
    /// Order is stable (on-hit first in source order, then <see cref="PrefixTable.All"/> source order) and
    /// the pick is uniform, so the choice reproduces across peers/loads for the same (seed, character).</summary>
    public static string PickCombined(double roll, string? character)
    {
        var pool = new List<string>(_pool.Length + 16);
        foreach (var c in _pool) pool.Add(c.En);
        foreach (var p in PrefixTable.All)
            if (p.Penalty && !p.IsFallback && PrefixTable.CurseInPool(p, character))
                pool.Add(p.Name);
        int i = (int)(roll * pool.Count);
        if (i >= pool.Count) i = pool.Count - 1;
        if (i < 0) i = 0;
        return pool[i];
    }
}

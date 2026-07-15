using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Enhances a relic on pickup. Because every numeric relic reads its effect magnitude
/// from the SAME DynamicVarSet that feeds its tooltip, mutating BaseValue scales both the
/// effect and the displayed number — one generic path, no per-relic code (so modded
/// relics work too, as long as they expose numeric DynamicVars).
///
/// Grade is SEED-DETERMINISTIC: derived from (runSeed, relicId, floor) exactly like the
/// game's own per-relic RNG (runState.Rng.Seed + TotalFloor + hash(id)). Same run seed →
/// same relic → same enhancement, which also makes save/load re-derivation exact (no
/// side-table needed). Rarity picks the magnitude range + prefix pool; the seeded roll
/// picks the percentage within the range and the prefix from the pool.
///
/// Eligibility = rarity present in RarityConfigs.ByRarity (Common/Uncommon/Rare/Shop/
/// Ancient). Starter/Event/None are excluded by omission.
/// </summary>
internal static class RelicForgeService
{
    // Per-relic-instance forge record; presence also guards against forging the same
    // instance twice. Tooltip patches read this for prefix + colored deltas.
    private static readonly ConditionalWeakTable<RelicModel, ForgeRecord> Records = new();

    // Marks a relic INSTANCE as a hidden companion (granted by a companion prefix), so it
    // can be hidden from the inventory and excluded from serialization. The value is the HOST
    // relic it was grafted onto, so visual effects (flash VFX) can show the host — the relic
    // the player actually owns — instead of the hidden donor. Never serialized; companions
    // are re-derived on load.
    private static readonly ConditionalWeakTable<RelicModel, RelicModel> Companions = new();

    // TEST-ONLY: relic instances whose fallback / tie-break combat-start buff should fire EVERY combat
    // regardless of its rolled chance — so `forgefallback` verifies the effects reliably WHILE the tooltip
    // still shows the real (5–60%) odds. Populated by the test command, consulted by FallbackBuffPatch.
    // Local only (never networked / serialized). ConditionalWeakTable = used as a set, value ignored.
    private static readonly ConditionalWeakTable<RelicModel, object> ForceFireSet = new();
    public static void MarkForceFire(RelicModel relic) => ForceFireSet.AddOrUpdate(relic, new object());
    public static bool IsForceFire(RelicModel relic) => ForceFireSet.TryGetValue(relic, out _);

    // Save/load carries the reforge COUNT (the one non-seed-derivable value) as a SavedProperty
    // on the serialized relic. FromSerializable rebuilds a fresh RelicModel instance well before
    // LoadRun re-forges, so the count captured there is parked here, keyed by that instance, and
    // taken by RunLoadReforgePatch when it re-forges. StrongBox because a CWT value must be a ref
    // type. See ReforgeSaveInjectPatch / ReforgeLoadCapturePatch.
    private static readonly ConditionalWeakTable<RelicModel, StrongBox<int>> Pending = new();

    // Same parking, for the curse-gauge cleanse reduction (see ForgeRecord.GaugeReduction): captured off the
    // serialized relic on FromSerializable, taken when the relic is (re-)forged on load / heal / reconnect.
    private static readonly ConditionalWeakTable<RelicModel, StrongBox<int>> PendingReduction = new();

    /// <summary>The SavedProperty name our reforge count rides on inside a serialized relic.</summary>
    public const string RfCountKey = "__rf_count";

    /// <summary>The SavedProperty (int) name carrying the curse-gauge cleanse reduction (see
    /// ForgeRecord.GaugeReduction). Persisted alongside the reforge count; stripped from packet sync.</summary>
    public const string RfReductionKey = "__rf_gred";

    /// <summary>Park a gauge reduction for a load-restored relic instance (set from FromSerializable).</summary>
    public static void SetPendingGaugeReduction(RelicModel relic, int n) => PendingReduction.AddOrUpdate(relic, new StrongBox<int>(n));

    /// <summary>Read + consume the parked gauge reduction for a relic instance (0 if none).</summary>
    public static int TakePendingGaugeReduction(RelicModel relic)
    {
        if (!PendingReduction.TryGetValue(relic, out var box)) return 0;
        PendingReduction.Remove(relic);
        return box.Value;
    }

    /// <summary>The current curse-gauge cleanse reduction of a relic instance (0 if never cleansed).</summary>
    public static int GaugeReductionOf(RelicModel relic)
        => Records.TryGetValue(relic, out var rec) ? rec.GaugeReduction : 0;

    /// <summary>The SavedProperty (string) name carrying a compact forge summary ("prefix|rider|self")
    /// for the RUN-HISTORY view, where the real grade can't be re-derived (history keeps only the
    /// display seed string). See ReforgeSaveInjectPatch / HistoryForgeDisplayPatch.</summary>
    public const string RfDescKey = "__rf_desc";

    /// <summary>Set ONLY while the run-history screen reconstructs relics (see HistoryForgeDisplayPatch),
    /// so FromSerializable attaches a display-only record there and NEVER on a normal run load.</summary>
    public static bool InHistoryLoad;

    /// <summary>The SavedProperty (int, 1) name marking a relic whose curse has been CLEANSED at a shop,
    /// so the seed-derived curse is stripped again on load. See Cleanse / RunLoadReforgePatch.</summary>
    public const string RfCleansedKey = "__rf_cleansed";

    // A load-restored relic instance that was cleansed before saving (parked here by
    // ReforgeLoadCapturePatch, consumed by RunLoadReforgePatch after it re-derives the forge).
    private static readonly ConditionalWeakTable<RelicModel, StrongBox<bool>> PendingCleansed = new();
    public static void SetPendingCleansed(RelicModel relic) => PendingCleansed.AddOrUpdate(relic, new StrongBox<bool>(true));
    public static bool TakePendingCleansed(RelicModel relic)
    {
        if (!PendingCleansed.TryGetValue(relic, out _)) return false;
        PendingCleansed.Remove(relic);
        return true;
    }

    /// <summary>True if this relic instance currently carries a cleansed (curse-removed) forge record.</summary>
    public static bool IsCleansed(RelicModel relic) => Records.TryGetValue(relic, out var rec) && rec.Cleansed;

    // --- AUTHORITATIVE forge descriptor (the fix for save/load + co-op prefix drift) ---------------
    // The prefix identity + curse USED to be re-derived from (seed, floor, character, config) on every
    // load / reconnect / peer. Any drift in those inputs (a client's local config before the host's
    // broadcast lands, a relic obtained off the normal floor path, a reconnected client rebuilt at
    // count 0) silently re-rolled the enchantment. The full identity is now PERSISTED as a compact
    // descriptor and RESTORED verbatim (see RestoreForged) so the outcome is a stored fact, not a guess
    // — only the numeric var deltas are recomputed (deterministic from prefix power + canonical bases,
    // config/character/rng-independent). Format: "prefix|rider|self|fbStat|fbAmt|fbPct" (all fields are
    // single tokens with no ':' or space, so the whole descriptor rides one ':'-delimited rf_counts field).
    private static readonly ConditionalWeakTable<RelicModel, StrongBox<string>> PendingDesc = new();
    public static void SetPendingDesc(RelicModel relic, string desc) => PendingDesc.AddOrUpdate(relic, new StrongBox<string>(desc));

    /// <summary>Non-consuming check — used by the in-process restore bridge to avoid overriding a
    /// descriptor that the serialized props actually carried (disk saves keep our keys).</summary>
    public static bool HasPendingDesc(RelicModel relic) => PendingDesc.TryGetValue(relic, out _);
    public static string? TakePendingDesc(RelicModel relic)
    {
        if (!PendingDesc.TryGetValue(relic, out var box)) return null;
        PendingDesc.Remove(relic);
        return box.Value;
    }

    /// <summary>Encode a record's authoritative identity to the compact descriptor (see PendingDesc).
    /// Returns null when there is nothing worth persisting (no prefix, no curse, no fallback buff).</summary>
    public static string? EncodeDescriptor(ForgeRecord rec)
    {
        if (rec == null) return null;
        bool any = rec.Prefix.Length > 0 || rec.EnemyRider || rec.SelfCurse.Length > 0 || rec.FallbackPercent > 0;
        if (!any) return null;
        string rider = rec.EnemyRider ? rec.EnemyRiderSuffix : "";
        return $"{rec.Prefix}|{rider}|{rec.SelfCurse}|{rec.FallbackStat}|{rec.FallbackAmount}|{rec.FallbackPercent}";
    }

    /// <summary>The current authoritative descriptor for an owned relic instance, or null if unforged /
    /// nothing to persist. Used by the co-op broadcaster and the idempotency check in ReconcileToHost.</summary>
    public static string? DescriptorOf(RelicModel relic)
        => Records.TryGetValue(relic, out var rec) ? EncodeDescriptor(rec) : null;

    /// <summary>Escape a descriptor for the SPACE-delimited, ':'-fielded rf_counts command payload. An
    /// enemy-rider suffix ("the Tyrant" / "the Titan") contains a SPACE, which would otherwise split the
    /// token and corrupt the co-op reconcile (→ a checksum divergence / black screen on room exit). Escapes
    /// '%', ' ' and ':'. The DISK save keeps the raw descriptor (JSON handles spaces) — only the wire needs
    /// this. Escape '%' FIRST so the escapes never collide.</summary>
    public static string EscapeWireDesc(string desc)
        => string.IsNullOrEmpty(desc) ? "" : desc.Replace("%", "%25").Replace(" ", "%20").Replace(":", "%3A");

    /// <summary>Inverse of <see cref="EscapeWireDesc"/> — unescape ':' and ' ' first, '%' LAST.</summary>
    public static string UnescapeWireDesc(string s)
        => string.IsNullOrEmpty(s) ? "" : s.Replace("%3A", ":").Replace("%20", " ").Replace("%25", "%");

    /// <summary>Parsed descriptor fields (see EncodeDescriptor). Missing trailing fields (legacy 3-field
    /// saves) default to empty / 0, so an old "prefix|rider|self" descriptor still restores its identity
    /// and curse (only the fallback-buff chance, which those saves never carried, is absent).</summary>
    private static (string prefix, string rider, string self, string fbStat, int fbAmt, int fbPct) DecodeDescriptor(string desc)
    {
        var p = desc.Split('|');
        string prefix = p.Length > 0 ? p[0] : "";
        string rider  = p.Length > 1 ? p[1] : "";
        string self   = p.Length > 2 ? p[2] : "";
        string fbStat = p.Length > 3 ? p[3] : "";
        int fbAmt = 0, fbPct = 0;
        if (p.Length > 4) int.TryParse(p[4], out fbAmt);
        if (p.Length > 5) int.TryParse(p[5], out fbPct);
        return (prefix, rider, self, fbStat, fbAmt, fbPct);
    }

    /// <summary>
    /// Unified "curse" test on a record: a relic is CURSED if its prefix is itself a penalty prefix
    /// (a self-downside like Cursed/Cumbersome/Tainted…), OR it carries an enemy-rider / self-curse.
    /// This is the merged concept — a single word for every downside — that both the reforge-ends-on-curse
    /// rule (campfire + shop) and Cleanse act on. Rider/self curses count regardless of the enemy-forge
    /// toggle here so a curse can never be trapped uncleansable; the reforge-termination check gates them
    /// on the toggle instead (see <see cref="RolledCurseForReforge"/>).
    /// </summary>
    public static bool IsCursedRecord(ForgeRecord rec)
        => (PrefixTable.ByName(rec.Prefix)?.Penalty ?? false)
           || rec.EnemyRider || rec.SelfCurse.Length != 0;

    /// <summary>
    /// CLEANSE a relic (single-player live path). Purifies it two ways: removes any curse (enemy-rider /
    /// self-curse, or purges a penalty prefix back to un-prefixed) AND reduces the curse gauge — a CURSED
    /// relic sheds half its fill, a saturated non-cursed relic sheds all of it (→ 0) — giving a burnt-out
    /// relic more reforges. Offered on a relic that is cursed OR gauge-saturated (see <see cref="CanCleanse"/>).
    /// Returns true if it acted.
    /// </summary>
    public static bool Cleanse(RelicModel relic)
    {
        if (!CanCleanse(relic)) return false;
        ApplyCleanseLive(relic);
        relic.Flash();
        MainFile.Logger.Info($"[{MainFile.ModId}] cleansed {relic.Id.Entry} (curse removed + gauge reset).");
        return true;
    }

    /// <summary>Whether <see cref="Cleanse"/> would do anything to this relic — it is CURSED or its curse
    /// gauge has SATURATED (100%). A NON-mutating check. Used by the co-op cleanse seam
    /// (<see cref="ReforgeNet.Cleanse"/>) to decide whether to charge gold + dispatch, and by
    /// <see cref="ReforgeRestSiteOption"/> to exclude cleanse-only relics from the reforge picker.</summary>
    public static bool CanCleanse(RelicModel relic)
        => Records.TryGetValue(relic, out var rec) && (IsCursedRecord(rec) || IsGaugeSaturated(relic));

    /// <summary>LIVE cleanse applied on EVERY co-op client (see <see cref="ReforgeNet.ApplyCleanseOnClient"/>)
    /// and by the SP <see cref="Cleanse"/>. Strips any curse and REDUCES the curse gauge: a CURSED relic
    /// sheds only HALF its current fill (the curse-removal is the main service), a saturated (non-cursed)
    /// relic sheds ALL of it (→ 0). Runs exactly once per cleanse per peer, computed from the deterministic
    /// gauge + synced reforge count, so every peer arrives at the same reduction. NOT the load/reconnect
    /// re-apply (that is <see cref="ApplyCleanse"/>, curse-only; the reduction is restored from persistence /
    /// the rf_counts sync there).</summary>
    public static void ApplyCleanseLive(RelicModel relic)
    {
        if (!Records.TryGetValue(relic, out var rec)) return;
        int fill = CurseGauge(relic);                 // current fill BEFORE this cleanse (unaffected by StripCurse)
        bool cursed = IsCursedRecord(rec);
        if (cursed) StripCurse(rec);
        rec.GaugeReduction += cursed ? fill / 2 : fill;   // cursed → half off; saturated non-cursed → all off
    }

    /// <summary>Re-apply the cleansed CURSE state to a just-re-derived record (load / reconnect / heal
    /// path). Curse-strip ONLY — the gauge reduction is restored separately from persistence (__rf_gred) /
    /// the parked pending value during the re-forge, so it must NOT be re-reset here.</summary>
    public static void ApplyCleanse(RelicModel relic)
    {
        if (Records.TryGetValue(relic, out var rec)) StripCurse(rec);
    }

    private static void StripCurse(ForgeRecord rec)
    {
        rec.EnemyRider = false;
        rec.EnemyRiderSuffix = "";
        rec.SelfCurse = "";
        // Merged curse: when the PREFIX is the curse (a penalty prefix), cleansing purges it — the relic
        // reverts to un-prefixed (penalty prefixes graft nothing and scale no host var, so clearing the
        // name is enough to disable the combat hooks that read it; there is no upside to preserve).
        if (PrefixTable.ByName(rec.Prefix)?.Penalty ?? false)
        {
            rec.Prefix = "";
            rec.Percent = 0;
            rec.Amplify = false;
            rec.CompanionRelic = null;
            rec.FallbackStat = "";
            rec.FallbackAmount = 0;
            rec.FallbackPercent = 0;
        }
        rec.Cleansed = true;
    }

    /// <summary>The reforge count of a relic instance (0 if never forged / never re-forged).</summary>
    public static int ReforgeCountOf(RelicModel relic)
        => Records.TryGetValue(relic, out var rec) ? rec.ReforgeCount : 0;

    /// <summary>Park a reforge count for a load-restored relic instance (set from FromSerializable).</summary>
    public static void SetPendingReforgeCount(RelicModel relic, int n) => Pending.AddOrUpdate(relic, new StrongBox<int>(n));

    /// <summary>Read the parked reforge count for a relic instance (0 if none), consuming it.</summary>
    public static int TakePendingReforgeCount(RelicModel relic)
    {
        if (!Pending.TryGetValue(relic, out var box)) return 0;
        Pending.Remove(relic);
        return box.Value;
    }

    /// <summary>The base per-relic grade seed, matching the game's own per-relic RNG idiom
    /// (runState.Rng.Seed + TotalFloor + hash(id), see decompile ~line 291714).</summary>
    private static uint GradeSeed(uint runSeed, int floor, string relicId)
        => (uint)((int)runSeed + floor + StringHelper.GetDeterministicHashCode(relicId));

    // splitmix32 finalizer (same as Sts2RngFix.Mix): every input bit affects every output bit, so
    // consecutive reforge counts map to unrelated grades instead of the correlated drift a linear
    // "+ count * k" mix would cause (see reference_sts2_rng_correlation).
    private static uint SplitMix32(uint x)
    {
        x += 0x9E3779B9u;
        x = (x ^ (x >> 16)) * 0x21F0AAADu;
        x = (x ^ (x >> 15)) * 0x735A2D97u;
        return x ^ (x >> 15);
    }

    // Curse chance SCALES with the prefix's power ("great power, great cost"): CurseChance is the
    // reference at CurseRefPower, so a Legendary (+60%) approaches the cap while a Keen (+4%) sits
    // near the floor. Non-numeric prefixes (companion/mixed/delayed, PowerPct 0) use the reference;
    // negative prefixes use the floor (an already-weakened relic is rarely also cursed).
    private const double CurseRefPower = 0.15;
    private const double CurseFloor = 0.05;
    private const double CurseCap = 0.85;

    // A NO-PREFIX forged relic (the ~45% NoPrefixChance "dud") can still land a curse ON ITS OWN — a
    // "curse-only" relic: pure downside, no boon, the outcome that replaces the old penalty prefixes.
    // It is deliberately RARER than a curse riding a boon (a dud that turns actively bad should be the
    // occasional unlucky roll, not common), so the reference chance is scaled by this factor. At the
    // default CurseChance (33%) this is 33% × 0.7 ≈ 23% of no-prefix relics, i.e. ~10% of all forges.
    // Reforge never produces one (guaranteePrefix always lands a prefix), so curse-only is pickup-only.
    private const double CurseOnlyFactor = 0.7;

    // A GUARANTEED reforge (guaranteePrefix) that lands a numeric prefix should never move nothing.
    // But a min-1 floor on every relic would over-buff the 62% whose numeric base is <= 3 (there +1 is
    // a 33-100% gain). So the floor is GATED to a large primary var (base >= this): +1 there is a minor
    // relative gain (<= ~17%), and small-base relics are left to lean on effect prefixes instead (their
    // pool share was raised in PrefixTable). Deterministic — no rng draw — so it reproduces on load/co-op.
    private const decimal ReforgeFloorMinBase = 6m;

    /// <summary>Chance (percent) the FALLBACK buff fires, banded from the fizzled magnitude prefix's
    /// power tier: the stronger the prefix that scaled nothing, the better the odds of its replacement.
    /// Host-independent (the raw fractional magnitude isn't used), so var-less relics and base-1 relics
    /// get a sensible chance instead of a ~4% one. Capped at 50% (a minor buff, never a sure thing).
    /// Bands: &lt;12% tier → 20, 12–24% → 35, ≥25% → 50. See RelicForgeService.Forge substitution.</summary>
    private static int FallbackChanceFor(double pct)
        => pct >= 0.25 ? 50 : pct >= 0.12 ? 35 : 20;

    /// <summary>Chance (percent) a PENALTY fallback fires — the darker mirror of <see cref="FallbackChanceFor"/>
    /// for a re-homed weakening prefix's self-downside. Lower bands (10 / 15 / 20 vs 20 / 35 / 50) so the
    /// combat-start self-debuff bites far less often than a boon fallback grants — it's the mild variant of a
    /// downside, not a guaranteed curse. Banded on the |power| of the fizzled negative prefix's tier.</summary>
    private static int FallbackPenaltyChanceFor(double pct)
    {
        double a = System.Math.Abs(pct);
        return a >= 0.25 ? 20 : a >= 0.12 ? 15 : 10;
    }

    /// <summary>Scaled vars that map to a combat-grantable stat, so a tier tie-break can grant "a chance
    /// of +1 more of the same stat". Non-power vars (Gold, Cards, MaxHp…) can't, and get no tie-break.</summary>
    private static readonly System.Collections.Generic.Dictionary<string, string> VarStat = new()
    {
        ["StrengthPower"] = "Strength", ["DexterityPower"] = "Dexterity",
        ["ThornsPower"] = "Thorns", ["Block"] = "Block",
    };

    /// <summary>Tier tie-break chance (percent) from the tier's power — higher tier → higher chance, so
    /// tied tiers still order strictly by expected value. The % maps to the chance, clamped [5,60].</summary>
    private static int TieChance(double pct) => System.Math.Clamp((int)System.Math.Round(pct * 100), 5, 60);

    /// <summary>Tier tie-break: if <paramref name="prefix"/> scaled the primary var to the SAME integer
    /// delta the tier just below it would (integer rounding collapses adjacent tiers on small/mid relics),
    /// give it a small combat-start chance-of-more of that stat so the higher tier is strictly better and
    /// the tooltip shows the edge. No-op when the tier already stands apart or the var isn't grantable.</summary>
    private static void ApplyTierTiebreak(string policyKey, Prefix prefix, ForgeRecord record)
    {
        VarChange? primary = null;
        foreach (var c in record.Changes)
            if (c.Dir == AffixDir.Increase && (primary == null || c.OldValue > primary.OldValue)) primary = c;
        if (primary == null) return;
        if (!VarStat.TryGetValue(primary.VarName, out string? stat)) return;   // stat must be combat-grantable
        Prefix? fb = PrefixTable.FallbackByStat(stat);
        if (fb == null) return;

        double lowerPct = PrefixTable.NextLowerTierPct(prefix.PowerPct);
        if (lowerPct <= 0) return;   // already the lowest tier — nothing below to tie with

        double b = AffixPolicy.BoostFor(policyKey, primary.VarName);
        int deltaThis = (int)System.Math.Abs(primary.NewValue - primary.OldValue);
        int deltaLower = (int)System.Math.Round(primary.OldValue * (decimal)(lowerPct * b),
                                                System.MidpointRounding.AwayFromZero);
        if (deltaLower != deltaThis) return;   // the tier already gives more than the one below — no tie

        record.FallbackPercent = TieChance(prefix.PowerPct);
        record.FallbackStat = fb.FallbackStat;
        record.FallbackAmount = fb.FallbackAmount;
    }

    private static double CurseChanceFor(Prefix prefix)
    {
        // Curses fully disabled (knob at 0): honor it literally — the CurseFloor must NOT re-introduce a
        // 5% minimum. Only when curses are ON does the per-power scaling (and its floor) apply.
        if (HostForgeConfig.CurseChance <= 0) return 0;
        double boon = prefix.PowerPct > 0 ? prefix.PowerPct
                    : prefix.PowerPct < 0 ? 0.02
                    : CurseRefPower;
        return Math.Clamp(HostForgeConfig.CurseChance * (boon / CurseRefPower), CurseFloor, CurseCap);
    }

    /// <summary>Chance a NO-PREFIX relic is nonetheless cursed (a "curse-only" pure-downside result) —
    /// the reference (zero-power) curse chance scaled down by <see cref="CurseOnlyFactor"/>. Same host-
    /// authoritative CurseChance knob as everything else, so it stays co-op consistent and user-tunable.</summary>
    private static double CurseOnlyChance()
        => HostForgeConfig.CurseChance <= 0 ? 0   // curses disabled → no curse-only either (bypass the floor)
         : Math.Clamp(HostForgeConfig.CurseChance * CurseOnlyFactor, CurseFloor, CurseCap);

    /// <summary>A prefix that would become a CURSE on a reforge: an explicit penalty, or a negative-magnitude
    /// (weakening) prefix that is neither an Amplify nor a companion/effect prefix. The single predicate the
    /// prefix gate and the curses-off re-roll share, so they stay in lockstep.</summary>
    private static bool IsWeakeningOrPenalty(Prefix p)
        => p.Penalty || (p.PowerPct < 0 && !p.Amplify && !p.IsCompanionPrefix);

    /// <summary>Curses-off reforge re-roll: a paid reforge with curses disabled must never land a downside,
    /// but it also shouldn't waste the gold on a dud — so draw fresh prefixes until one is a BOON (or a
    /// neutral companion/effect prefix). The pool is ~90% boons, so this converges in ~1 extra draw; bounded
    /// at 8 tries, and on the astronomically unlikely all-weakening streak it returns null (a safe no-prefix,
    /// never a curse). Deterministic — the local per-relic <paramref name="rng"/> is seeded from
    /// (runSeed, floor, relicId, reforgeCount), so both peers and every reload reproduce the exact result.</summary>
    private static Prefix? RerollBoonPrefix(Rng rng, string? charTitle)
    {
        for (int i = 0; i < 8; i++)
        {
            Prefix p = PrefixTable.Roll(rng, charTitle);
            if (!IsWeakeningOrPenalty(p)) return p;
        }
        return null;   // exhausted → safe dud (no curse); effectively unreachable at ~90% boon pool
    }

    // --- Per-relic CURSE GAUGE (reforge fatigue) ---------------------------------------------------
    // Each reforge fills the picked relic's gauge; at 100% the relic SATURATES and can no longer be
    // reforged (only the gauge — not the grade — gates this, so a saturated relic keeps its last roll).
    // A normal reforge adds a seeded 5–30%; a reforge that ROLLED A CURSE adds a bigger 40% chunk, so
    // pushing a relic that keeps cursing burns out fast. The gauge is a PURE FUNCTION of (seed, id,
    // floor, count) — exactly like the grade — so it re-derives identically on load / co-op / reconnect
    // with NO new persistence (the reforge count already rides the save + the rf_counts sync). Cleansing
    // does NOT lower it: the fatigue of a reforge that once cursed lingers even after the curse is gone
    // (ReforgeRollsCurse is seed-derived and ignores the cleansed flag), so a cleansed relic has fewer
    // reforges left. Replaces the old probabilistic per-visit "curse aura ends reforging" limiter.
    private const int GaugeStepMin = 5, GaugeStepMax = 30, GaugeCurseStep = 40, GaugeFull = 100;

    // Memo: the gauge only changes when (count, reduction) change (a reforge or a cleanse). The shop cleanse
    // button re-scans every relic EVERY FRAME (HasCleansable → CanCleanse → CurseGauge), so cache the last
    // result per instance and recompute only on a count/reduction change — turning the per-frame scan O(1).
    private static readonly ConditionalWeakTable<RelicModel, int[]> GaugeCache = new();   // [count, reduction, gauge]

    /// <summary>The curse-gauge fill (0–100): the seeded raw fill over all reforges MINUS the cleanse
    /// reduction, clamped. 0 when never re-forged. Deterministic + memoized on (count, reduction); safe to
    /// call from per-frame display code (count is small).</summary>
    public static int CurseGauge(RelicModel relic)
    {
        if (relic == null) return 0;
        int count = ReforgeCountOf(relic);
        if (count <= 0) return 0;                  // never re-forged
        int reduction = GaugeReductionOf(relic);   // gauge points removed by cleansing
        if (GaugeCache.TryGetValue(relic, out var cached) && cached[0] == count && cached[1] == reduction)
            return cached[2];                       // unchanged since last compute — return the memoized fill

        var owner = relic.Owner as Player;
        uint runSeed = owner?.RunState.Rng.Seed ?? RunManager.Instance?.State?.Rng.Seed ?? 0;
        int floor = relic.FloorAddedToDeck;
        string? character = CharAffix.TitleOf(owner);
        string relicId = relic.Id.Entry;

        int raw = 0;
        for (int k = 1; k <= count; k++)
        {
            if (ReforgeRollsCurse(relic, runSeed, floor, k, character))
                raw += GaugeCurseStep;
            else
            {
                // Seeded 5–30 increment for step k, from a derived stream (never touches the grade rng).
                var rng = new Rng(SplitMix32(GradeSeed(runSeed, floor, relicId) + (uint)k * 0x85EBCA6Bu + 0x27D4EB2Fu));
                raw += GaugeStepMin + (int)(rng.NextFloat() * (GaugeStepMax - GaugeStepMin + 1));
            }
        }
        int gauge = Math.Clamp(raw - reduction, 0, GaugeFull);   // subtract what cleansing removed
        GaugeCache.AddOrUpdate(relic, new[] { count, reduction, gauge });
        return gauge;
    }

    /// <summary>True once a relic's curse gauge has saturated (100%) — it can no longer be reforged.</summary>
    public static bool IsGaugeSaturated(RelicModel relic) => CurseGauge(relic) >= GaugeFull;

    // --- Per-LOCATION (campfire / shop visit) reforge aura — a SESSION budget on top of the per-relic gauge ---
    // Each reforge done at a location fills this initiator-local gauge 5–20%; at 100% the forge "goes cold"
    // for the visit (that reforge control greys). Unlike the per-relic curse gauge this is UI-only, per-visit,
    // NOT persisted or synced — it lives on the campfire option / shop button instance, so it just limits the
    // acting player's own session and is co-op-irrelevant (the reforge mutation still replicates via ReforgeNet).
    private const int LocationStepMin = 5, LocationStepMax = 20;
    public const int LocationGaugeFull = 100;

    /// <summary>Seeded 5–20% fill added to a LOCATION's reforge aura for the <paramref name="reforgeIndexThisVisit"/>-th
    /// reforge this visit (index from 0). Seeded from (run seed, current floor as a per-visit nonce, index) so it is
    /// honest + reload-stable, and it never touches the run rng stream. Initiator-local → co-op unaffected.</summary>
    public static int LocationGaugeStep(Player player, int reforgeIndexThisVisit)
    {
        uint seed = player?.RunState?.Rng.Seed ?? 0;
        uint floor = (uint)(player?.RunState?.TotalFloor ?? 0);
        var rng = new Rng(SplitMix32(seed + floor * 2654435761u + (uint)reforgeIndexThisVisit * 0x9E3779B9u + 0x165667B1u));
        return LocationStepMin + (int)(rng.NextFloat() * (LocationStepMax - LocationStepMin + 1));
    }

    /// <summary>Flavor band (0 = faint … 4 = full/ended) for a location aura fill, so the reforge control can
    /// pick escalating "the forge is going cold" text.</summary>
    public static int LocationGaugeBand(int gauge)
        => gauge >= LocationGaugeFull ? 4 : gauge >= 75 ? 3 : gauge >= 50 ? 2 : gauge >= 25 ? 1 : 0;

    /// <summary>
    /// Whether the relic's OWN base effect is GONE — consumed or self-disabled — so BOTH its forge boon AND
    /// its curse must go silent (a dead relic does nothing at all):
    ///   · <c>IsMelted</c> — a volatile / consumed relic (e.g. a wax relic used up);
    ///   · <c>Status == RelicStatus.Disabled</c> — a relic that self-disabled by condition (e.g. a
    ///     combats-left relic whose charges ran out — the game's IsUsedUp → Status = Disabled).
    /// Gauge saturation is DELIBERATELY excluded here: an over-reforged relic still exists and keeps its
    /// curse (only its upside dies — see <see cref="IsForgeEffectSuppressed"/>). The CURSE sites (EnemyForge /
    /// UnblockedHitPenaltyPatch) gate on THIS, so a spent/melted relic gives no curse, but a saturated one
    /// still bites.
    /// </summary>
    public static bool IsRelicSpent(RelicModel relic)
        => relic != null && (relic.IsMelted || relic.Status == RelicStatus.Disabled);

    /// <summary>
    /// Whether a relic's forge BOON (prefix effect / combat-start buff / grafted companion) must be
    /// suppressed: the relic is spent (<see cref="IsRelicSpent"/>) OR curse-gauge SATURATED (100%, the mod's
    /// "over-reforged, burnt out" state). Saturation kills the upside but NOT the curse — a saturated relic
    /// becomes pure downside. The forge combat-start buff sites and the companion hook-exclusion gate on this.
    /// (Numeric prefix boosts already follow the native effect: a melted relic is dropped from hook dispatch
    /// by the game, a Disabled relic no-ops its own hooks, and a saturated relic is dropped by the mod's
    /// hook filter — see <see cref="SaturatedRelicFilter"/>.)
    /// </summary>
    public static bool IsForgeEffectSuppressed(RelicModel relic)
        => relic != null && (IsRelicSpent(relic) || IsGaugeSaturated(relic));

    /// <summary>Whether to force-drop a relic from the game's HOOK DISPATCH (see SaturatedRelicDisablePatch).
    /// ONLY curse-gauge saturation does this for a normal relic — a melted relic is already excluded by the
    /// game, and a Disabled relic must stay listed so its own hooks can re-enable it. A hidden companion is
    /// dropped whenever its HOST's forge effect is suppressed (the companion embodies that host's prefix).</summary>
    public static bool IsEffectDisabled(RelicModel relic)
    {
        if (relic == null) return false;
        if (IsGaugeSaturated(relic)) return true;
        var host = HostOf(relic);                       // a grafted companion dies with its suppressed host
        return host != null && IsForgeEffectSuppressed(host);
    }

    /// <summary>Flavor band (0 = faint … 4 = saturated) for a gauge fill, so the tooltip / picker can
    /// pick escalating "curse-aura" text. 100% maps to the dedicated saturated band (4).</summary>
    public static int GaugeBand(int gauge)
        => gauge >= GaugeFull ? 4 : gauge >= 75 ? 3 : gauge >= 50 ? 2 : gauge >= 25 ? 1 : 0;

    /// <summary>The forge record for a relic instance, or null if it was never forged.</summary>
    public static ForgeRecord? RecordFor(RelicModel relic)
        => Records.TryGetValue(relic, out var rec) ? rec : null;

    /// <summary>The forge record to DISPLAY for a hovered relic: the relic's own (owned) record, else the
    /// record on its offered/preview clone — a canonical offer (treasure/reward/event) forges onto a clone,
    /// so RecordFor(__instance) is null but the preview record lives on the clone. Null if unforged. Used by
    /// the tooltip patches so owned AND offered relics both surface the prefix/curse panels.</summary>
    public static ForgeRecord? RecordForHover(RelicModel relic)
    {
        var rec = RecordFor(relic);
        if (rec != null) return rec;
        try
        {
            var clone = PreviewCloneFor(relic) ?? PreviewOnHover(relic);
            return clone != null ? RecordFor(clone) : null;
        }
        catch { return null; }   // preview forge on a canonical can throw — treat as "nothing to show"
    }

    /// <summary>
    /// Attach a DISPLAY-ONLY forge record to a run-history-reconstructed relic, parsed from the stored
    /// "prefix|riderSuffix|selfCurse" summary (see <see cref="RfDescKey"/>). No var deltas are stored,
    /// so the tooltip shows the prefix name + curse only. Skips if a real record already exists.
    /// </summary>
    public static void RegisterDisplayRecord(RelicModel relic, string desc)
    {
        if (relic == null || string.IsNullOrEmpty(desc) || Records.TryGetValue(relic, out _)) return;
        var parts = desc.Split('|');
        string prefix = parts.Length > 0 ? parts[0] : "";
        string rider  = parts.Length > 1 ? parts[1] : "";
        string self   = parts.Length > 2 ? parts[2] : "";
        if (prefix.Length == 0 && rider.Length == 0 && self.Length == 0) return;
        Records.Add(relic, new ForgeRecord
        {
            Rarity = relic.Rarity, Prefix = prefix, DisplayOnly = true,
            EnemyRider = rider.Length > 0, EnemyRiderSuffix = rider, SelfCurse = self,
        });
    }

    /// <summary>True if this relic instance is a hidden companion granted by a companion prefix.</summary>
    public static bool IsCompanion(RelicModel relic) => Companions.TryGetValue(relic, out _);

    /// <summary>The host relic a companion was grafted onto, or null if not a companion.</summary>
    public static RelicModel? HostOf(RelicModel companion)
        => Companions.TryGetValue(companion, out var host) ? host : null;

    /// <summary>
    /// If <paramref name="host"/> rolled a companion prefix, grant the donor relic as a HIDDEN
    /// instance to <paramref name="player"/> (once per host). The donor is added via
    /// AddRelicInternal(silent) so no "relic get" popup/sound/inventory icon is created, but it
    /// still enters player.Relics — so its native hooks fire (verified: both RunState and
    /// CombatState IterateHookListeners include every non-melted relic). Called from the obtain
    /// path and the load re-forge path; the preview/tooltip path never grants.
    /// </summary>
    /// <param name="index">Insert position in player.Relics (-1 = append). The co-op replica re-graft
    /// (ForgeReplicaSyncPatch) passes the companion's original index: the relic LIST ORDER is part of the
    /// synced state a checksum hashes, so an appended re-graft would mismatch the owner's live order.</param>
    public static void GrantCompanionIfAny(RelicModel host, Player player, int index = -1)
    {
        var rec = RecordFor(host);
        if (rec?.CompanionRelic == null || rec.CompanionGranted) return;

        // Flat _contentById lookup — NEVER ModelDb.AllRelics here: AllRelics is built from the character
        // relic pools, which a broken workshop custom-character mod makes THROW (KeyNotFoundException on
        // CHARACTER.*). This runs on the campfire reforge path, so an unguarded throw there is a black
        // screen. GetByIdOrNull is pool-independent and returns null gracefully.
        RelicModel? template;
        try { template = ModelDb.GetByIdOrNull<RelicModel>(ModelDb.GetId(rec.CompanionRelic)); }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] companion lookup failed: {e.Message}"); return; }
        if (template == null)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] companion type {rec.CompanionRelic.Name} not found in ModelDb.");
            return;
        }
        RelicModel companion = template.ToMutable();
        WeakenCompanion(companion);                // grafted effect is a reduced version of the real relic
        Companions.Add(companion, host);           // tag (value=host) BEFORE adding so save/inventory/vfx patches see it
        if (index > player.Relics.Count) index = -1;   // stale position (list shrank) — fall back to append
        player.AddRelicInternal(companion, index, silent: true);
        rec.CompanionGranted = true;
        rec.Companion = companion;
        // Mirror the companion's live counter (e.g. "attacks until trigger") on the HOST icon:
        // when the hidden companion's DisplayAmount changes, refresh the host's holder too.
        companion.DisplayAmountChanged += host.InvokeDisplayAmountChanged;
        MainFile.Logger.Info($"[{MainFile.ModId}] grafted {companion.Id.Entry} onto {host.Id.Entry} ({rec.Prefix}).");
    }

    /// <summary>
    /// COMBAT-START self-heal for one owned relic. The forge grade lives ONLY on the in-memory
    /// DynamicVarSet + the <see cref="Records"/> table (keyed by instance) — it is never serialized,
    /// and is normally re-derived by just two hooks: pickup (Obtain) and <see cref="RunLoadReforgePatch"/>
    /// (NGame.LoadRun). A foreign "restart combat" feature (e.g. Better Spire 2's double-R retry) restores
    /// run/combat state through a path that NEVER calls LoadRun, so the affix is silently lost:
    ///   · the restart swapped in a FRESH relic instance -> no record, canonical BaseValues (prefix vanishes);
    ///   · or it kept the instance but reset its DynamicVars to canonical (effect vanishes).
    /// This runs at every combat start (idempotent) and repairs BOTH cases before combat reads any value:
    ///   · record present -> re-assert the stored enhanced BaseValues (a cheap no-op when already applied);
    ///   · record absent  -> re-derive the SAME seed-deterministic grade LoadRun would, consuming any parked
    ///     reforge-count / cleansed flag, and re-graft companions.
    /// Deterministic per (seed, floor, id, count) and identical on every peer, so it is co-op safe.
    /// </summary>
    /// <summary>
    /// LIGHTWEIGHT re-assertion of a relic's forged BASE VALUES only — no companion graft, no seed
    /// re-derive. Idempotent: re-applies each stored delta ONLY where the var currently holds its captured
    /// canonical value (the exact signal it was reset), so it is a no-op in normal play and never clobbers a
    /// legitimate mid-combat change. Cheap enough for a per-card-play hook (see ForgeReassertOnPlayPatch),
    /// which closes the window that <see cref="HealForge"/> (turn-start only) misses: a foreign MID-COMBAT
    /// state restore — e.g. the Rewind mod's in-combat turn rewind, which reconstructs via NGame.LoadRun
    /// (so RunLoadReforgePatch restores the record) but can leave the live vars canonical until re-asserted.
    /// Only touches relics that already carry a real (non-display) record; a record-less fresh instance still
    /// needs the full <see cref="HealForge"/> re-derive at turn start.
    /// </summary>
    public static void ReassertForgeVars(RelicModel relic)
    {
        if (relic == null || IsCompanion(relic)) return;
        if (!Records.TryGetValue(relic, out var rec) || rec.DisplayOnly || !rec.HasChanges) return;
        foreach (var c in rec.Changes)
            if (relic.DynamicVars.TryGetValue(c.VarName, out var dv) && dv.BaseValue == c.OldValue)
                dv.BaseValue = c.NewValue;
    }

    public static void HealForge(RelicModel relic, Player player)
    {
        if (relic == null || player == null) return;
        if (IsCompanion(relic)) return;                 // hidden donors are never forged themselves

        if (Records.TryGetValue(relic, out var rec))
        {
            if (rec.DisplayOnly) return;                // history-view stub — no live var deltas to assert
            // Instance kept, but a foreign restart may have reset BaseValues to canonical -> put them back.
            // Gate on "== OldValue" (the canonical value forge captured): that is the exact signal the var
            // was reset. If it holds NewValue we're already forged (no-op); if it holds anything ELSE the
            // game legitimately changed it mid-run, so we must NOT clobber it back to the forged number.
            foreach (var c in rec.Changes)
                if (relic.DynamicVars.TryGetValue(c.VarName, out var dv) && dv.BaseValue == c.OldValue)
                    dv.BaseValue = c.NewValue;
            EnsureCompanion(relic, player, rec);   // a hover may have re-derived the record WITHOUT grafting
            return;
        }

        // No record: the restart swapped in a fresh instance. Re-derive exactly like LoadRun does —
        // a re-forged relic persisted a count>0 (parked on FromSerializable) and guarantees a prefix.
        uint seed = player.RunState.Rng.Seed;
        int rf = TakePendingReforgeCount(relic);
        bool cleansed = TakePendingCleansed(relic);
        int gred = TakePendingGaugeReduction(relic);
        string? summary = Forge(relic, seed, relic.FloorAddedToDeck,
              reforgeCount: rf, guaranteePrefix: rf > 0, character: CharAffix.TitleOf(player), gaugeReduction: gred);
        // Non-eligible relics (Starter/Event) never get a record — Forge returned null without adding one.
        // Don't log or graft for them: they'd otherwise spam this line every single combat.
        var rec2 = RecordFor(relic);
        if (rec2 == null) return;
        if (cleansed) ApplyCleanse(relic);
        EnsureCompanion(relic, player, rec2);
        // A relic that reached combat WITHOUT its forge record was restored by a LoadRun-bypassing restart
        // (e.g. BetterSpire2 Reset Round) — re-derived here from its pickup floor + reforge count.
        MainFile.Logger.Info($"[{MainFile.ModId}] combat-start heal restored {relic.Id.Entry} -> '{rec2.Prefix}'"
            + $"{(rf > 0 ? $" (reforge #{rf})" : "")} [{summary ?? "no numeric change"}]");
    }

    /// <summary>Grant the record's graft companion if it wants one but it is missing (never granted, or the
    /// hidden instance was dropped by a restart). No-op for non-graft prefixes or an already-owned companion.
    /// Called at combat start (HealForge) — where both co-op peers run deterministically — NOT from the hover
    /// preview path, so a per-client hover never adds a hidden relic on only one peer.</summary>
    private static void EnsureCompanion(RelicModel relic, Player player, ForgeRecord rec)
    {
        if (rec.CompanionRelic == null) return;                                   // not a graft prefix
        if (rec.CompanionGranted && rec.Companion != null
            && player.Relics.Contains(rec.Companion)) return;                     // already present
        // A re-derived record lost the reference (Companion == null) while the donor instance may still
        // be owned. ADOPT that existing companion rather than granting a SECOND one — a duplicate leaves
        // this peer with one more hidden relic than the others and desyncs co-op. Only adopt the matching
        // donor type; a wrong-type leftover is purged on the next reforge (RemoveCompanions).
        var existing = player.Relics.FirstOrDefault(
            r => IsCompanion(r) && HostOf(r) == relic && r.GetType() == rec.CompanionRelic);
        if (existing != null)
        {
            rec.Companion = existing;
            rec.CompanionGranted = true;
            return;
        }
        rec.CompanionGranted = false;                                            // stale/missing -> allow re-grant
        rec.Companion = null;
        GrantCompanionIfAny(relic, player);
    }

    /// <summary>Result of a reforge: a normal re-roll, or one that landed a CURSE — a penalty prefix
    /// OR (while enemy-forge is on) an enemy-rider / self-curse. Both the campfire AND the shop caller
    /// end their reforge action on a curse (the gamble's cost): a curse can no longer be re-rolled away
    /// for cheap gold — only removed by Cleanse. See <see cref="RolledCurseForReforge"/>.</summary>
    public enum ReforgeOutcome { Reforged, RolledCurse }

    /// <summary>Whether a freshly re-forged record counts as a CURSE for the reforge-ends-on-curse rule.
    /// ANY curse ends the reforge now (the enemy-forge toggle no longer gates this): an enemy-rider OR a
    /// self-curse — which, since penalties were re-homed onto the curse slot, also covers every former
    /// penalty. (The leading Prefix?.Penalty clause only fires for legacy saves that still carry a penalty
    /// in the prefix slot; the pool can no longer produce one, and Forge re-homes a forced one.) With
    /// enemy-forge off, Forge already manifests a non-AlwaysCurse curse as a (real) self-curse, so the
    /// reforge never ends on an invisible one.</summary>
    private static bool RolledCurseForReforge(ForgeRecord rec)
        => (PrefixTable.ByName(rec.Prefix)?.Penalty ?? false)
           || rec.EnemyRider || rec.SelfCurse.Length != 0;

    /// <summary>
    /// Re-roll a relic's prefix. Works on ANY owned relic — one that already has a prefix (the
    /// normal case), one that rolled "no prefix", AND one that was never eligible for the pickup
    /// auto-forge (Starter/Event): a deliberate campfire reforge bypasses the eligibility gate and
    /// always lands a prefix. Restores the relic to its canonical numbers first (so the new grade
    /// scales from base, not from a previous enhancement), un-grafts any companion the old prefix
    /// granted, bumps the reforge count, and forges again. Deterministic given (seed, id, floor,
    /// newCount) — reloading reproduces it, so it can't be save-scummed.
    ///
    /// If breaking is enabled, each reforge past the first rolls a (deterministic) break chance; on
    /// a break the relic is destroyed and removed instead. Returns which happened.
    /// </summary>
    public static ReforgeOutcome Reforge(RelicModel relic, Player player)
    {
        // A relic may have NO record yet (never forged / ineligible on pickup) — that's fine, it
        // just starts from count 0 and canonical vars (nothing to restore).
        Records.TryGetValue(relic, out var rec);
        int next = (rec?.ReforgeCount ?? 0) + 1;
        int reduction = rec?.GaugeReduction ?? 0;   // carry the cleanse reduction across the reforge (new fill adds on top)

        // (1) Undo the previous grade so Forge scales from canonical values again.
        //     Grafted companion prefixes added a hidden relic instance -> remove it. Delayed
        //     prefixes need no cleanup: they re-derive from the record each turn, so swapping the
        //     record below is enough. Numeric prefixes just restore each changed var to OldValue.
        if (rec != null)
        {
            // Scan-based purge (not the stored reference) so a re-derived record can never strand an
            // orphaned companion — the co-op relic-count divergence that black-screens at a campfire.
            RemoveCompanions(relic, rec, player);
            foreach (var c in rec.Changes)
                if (relic.DynamicVars.TryGetValue(c.VarName, out var dv))
                    dv.BaseValue = c.OldValue; // setter also ResetToBase() -> tooltip reverts
            Records.Remove(relic); // drop so Forge's "already processed" guard passes
        }

        // (2) Re-roll: guaranteePrefix also bypasses the rarity-eligibility gate, so a deliberate
        //     reforge can prefix even a Starter/Event relic the pickup path would have skipped.
        var runState = player.RunState;
        string? summary = Forge(relic, runState.Rng.Seed, relic.FloorAddedToDeck,
                                reforgeCount: next, guaranteePrefix: true, character: CharAffix.TitleOf(player),
                                gaugeReduction: reduction);

        // (3) If the new prefix is a graft companion, grant it.
        GrantCompanionIfAny(relic, player);

        // (4) Did this roll land a CURSE? RolledCurse now means only "a curse APPEARED this reforge" — it
        //     locks the cursed relic (cleanse-only, see Reforgeable) and adds a bigger curse-gauge chunk;
        //     it no longer ends a session. Reported for feedback/logging. Matches PredictReforgeOutcome.
        bool cursed = Records.TryGetValue(relic, out var newRec) && RolledCurseForReforge(newRec);
        MainFile.Logger.Info($"[{MainFile.ModId}] reforge #{next} {relic.Id.Entry}: {summary ?? "(no numeric change)"}{(cursed ? " [CURSE]" : "")}");
        return cursed ? ReforgeOutcome.RolledCurse : ReforgeOutcome.Reforged;
    }

    /// <summary>
    /// Restore a relic's forge to a PERSISTED authoritative descriptor (see <see cref="EncodeDescriptor"/>)
    /// instead of re-deriving it from the seed. This is the drift-proof load / reconnect / co-op path: the
    /// prefix identity and curse are taken VERBATIM from <paramref name="desc"/>, and only the numeric var
    /// deltas are recomputed — deterministic from the prefix's power + the relic's canonical base values, so
    /// they carry no rng / config / character / floor sensitivity. The caller must clear any existing record
    /// first (a fresh load instance has none; <see cref="ReconcileToHost"/> undoes the old one). Applies the
    /// cleanse itself. Returns the restored record (null if nothing to restore).
    /// </summary>
    public static ForgeRecord? RestoreForged(RelicModel relic, string desc, uint runSeed, int floor,
                                             int reforgeCount, bool cleansed, int gaugeReduction, string? character)
    {
        if (ForgeSafeMode.Active) return null;                 // sister-mod mismatch — forge inert
        if (relic == null || string.IsNullOrEmpty(desc)) return null;
        if (Records.TryGetValue(relic, out _)) return null;   // already has a record — caller must clear first
        var (prefixName, rider, self, fbStat, fbAmt, fbPct) = DecodeDescriptor(desc);

        // Curse-only / no-prefix record (a re-homed penalty, or a pickup "curse-only" result): nothing to
        // scale, so build the bare record directly — no numeric application, matching Forge's no-prefix path.
        if (prefixName.Length == 0)
        {
            var bare = new ForgeRecord
            {
                Rarity = relic.Rarity, Prefix = "", Percent = 0, ReforgeCount = reforgeCount,
                GaugeReduction = gaugeReduction, EnemyRider = rider.Length > 0, EnemyRiderSuffix = rider,
                SelfCurse = self, FallbackStat = fbStat, FallbackAmount = fbAmt, FallbackPercent = fbPct,
                Cleansed = cleansed,
            };
            Records.Add(relic, bare);
            return bare;
        }

        Prefix? def = PrefixTable.ByName(prefixName);
        if (def == null)
        {
            // Unknown prefix name (a stale save from a since-removed prefix, or a sibling-mod prefix whose
            // mod was uninstalled): fall back to a seed re-derive so the relic gets a valid grade rather
            // than nothing — but the CURSE stays authoritative from the descriptor (the re-derive would
            // otherwise re-roll it from seed+config, silently dropping a cleanse-worthy curse or conjuring
            // a different one, and in co-op the descriptor would then never match the host's).
            Forge(relic, runSeed, floor, reforgeCount: reforgeCount, guaranteePrefix: reforgeCount > 0,
                  character: character, gaugeReduction: gaugeReduction, suppressCurse: true);
            var rederived = RecordFor(relic);
            if (rederived != null)
            {
                rederived.EnemyRider = rider.Length > 0;
                rederived.EnemyRiderSuffix = rider;
                rederived.SelfCurse = self;
                if (fbStat.Length > 0 || fbPct > 0)
                {
                    rederived.FallbackStat = fbStat;
                    rederived.FallbackAmount = fbAmt;
                    rederived.FallbackPercent = fbPct;
                }
            }
            if (cleansed) ApplyCleanse(relic);
            return rederived;
        }

        // Reuse Forge with a FORCED prefix so the numeric scaling / tier tie-break / reforge floor all run
        // deterministically (config/rng-independent), then OVERWRITE the curse + fallback fields with the
        // persisted authoritative values. suppressCurse blocks Forge's INTERNAL curse roll entirely (the
        // rng draws still happen, so the stream is unchanged): without it, a forced restore could land a
        // curse the original pickup never rolled (allowCurse=true because forced!=null), and that curse
        // enables the fallback substitution — silently mutating e.g. a fizzled "Keen" into "Honed"+buff on
        // load, breaking round-trip idempotency and diverging from the host's state in co-op.
        Forge(relic, runSeed, floor, forced: def, reforgeCount: reforgeCount, guaranteePrefix: reforgeCount > 0,
              character: character, gaugeReduction: gaugeReduction, suppressCurse: true);
        var rec = RecordFor(relic);
        if (rec == null) return null;
        rec.EnemyRider = rider.Length > 0;
        rec.EnemyRiderSuffix = rider;
        rec.SelfCurse = self;
        // Only overwrite the fallback-buff fields when the descriptor actually CARRIED them. A legacy
        // 3-field descriptor ("prefix|rider|self") decodes fb as empty/0 — for a fallback-substituted relic
        // Forge(forced) already re-derived a sensible default chance, so clobbering it to 0 would silence the
        // buff. New (6-field) descriptors always carry a real stat when there is a buff, so this restores it.
        if (fbStat.Length > 0 || fbPct > 0)
        {
            rec.FallbackStat = fbStat;
            rec.FallbackAmount = fbAmt;
            rec.FallbackPercent = fbPct;
        }
        if (cleansed) ApplyCleanse(relic);
        return rec;
    }

    /// <summary>
    /// CO-OP reconnect repair: reconcile <paramref name="relic"/> to the HOST's authoritative reforge
    /// state (reforge <paramref name="count"/> + <paramref name="cleansed"/> flag). Those two values are
    /// the ONLY forge data that cannot cross the packet wire — the SavedProperties packet serializer strips
    /// our custom keys (see ReforgeKeyPacketGuardPatch) to avoid a net-id throw — so a client that rebuilt
    /// its relic instances on reconnect re-derives them at count 0 / un-cleansed and shows the wrong (or
    /// vanished) prefix, or a cleansed curse comes back. The host re-broadcasts the true state on room
    /// entry (see <see cref="ForgeConfigBroadcaster.BroadcastCountsIfHost"/>); this applies one entry.
    ///
    /// IDEMPOTENT: a no-op when the relic already matches — which is the host itself, and every synced
    /// client in normal play (the count already rode the live reforge command, <see cref="ReforgeNet"/>).
    /// Only a desynced (reconnected) client actually rebuilds, re-deriving the SAME seed-deterministic
    /// grade the host holds — so the two CONVERGE rather than diverge. Runs through the synchronized
    /// action queue (a ConsoleCmdGameAction), so it replays in lockstep on every peer; deterministic given
    /// (seed, floor, id, count), so any peer that DOES apply it agrees. Mirrors the undo+re-forge of
    /// <see cref="Reforge"/>, but targets an explicit count instead of incrementing.
    /// </summary>
    public static void ReconcileToHost(RelicModel relic, Player player, int count, bool cleansed, int gaugeReduction, string? desc = null)
    {
        if (ForgeSafeMode.Active) return;                      // safe mode: never undo/rebuild (would half-tear)
        if (relic == null || player == null || IsCompanion(relic)) return;
        // Idempotent: no-op when this peer already matches the host — count, cleanse, gauge reduction AND the
        // authoritative enchantment descriptor. This is the host itself and every in-sync client in normal
        // play, so the per-room re-broadcast costs only a cheap compare; only a DIVERGED (config-race or
        // reconnected) client actually rebuilds — and it rebuilds to the host's EXACT descriptor, so the two
        // converge instead of the old re-derive that could diverge again. A null desc = legacy caller (no
        // descriptor on the wire) → fall back to the seed re-derive and skip the descriptor half of the check.
        bool descMatches = string.IsNullOrEmpty(desc) || string.Equals(DescriptorOf(relic) ?? "", desc, StringComparison.Ordinal);
        if (ReforgeCountOf(relic) == count && IsCleansed(relic) == cleansed
            && GaugeReductionOf(relic) == gaugeReduction && descMatches) return;   // already in sync — no churn

        // Reaching here means THIS peer diverged from the host (a reconcile fires only on a mismatch).
        // Snapshot the client's state BEFORE the rebuild so the log below pins the exact divergence — the
        // single most useful breadcrumb when chasing a co-op black screen (it shows which relic drifted and
        // from what to what).
        string beforeDesc = DescriptorOf(relic) ?? "";
        int beforeCount = ReforgeCountOf(relic);

        // Undo the current (wrong) grade so the restore/re-derive scales from canonical again — same as
        // Reforge(). The undo is DESTRUCTIVE (companions removed, vars reverted, record dropped), so the
        // rebuild below runs under a catch that puts the snapshot back on failure: a reconcile fires only
        // on the already-diverged peer (the host no-ops via descMatches), so a half-torn-down relic here
        // would be an ASYMMETRIC state — e.g. a missing hidden companion shifts player.Relics count, which
        // is checksummed → permanent desync. Restoring the snapshot leaves the peer diverged-but-STABLE,
        // healable by the next room's re-broadcast (or at worst the same divergence it already had).
        ForgeRecord? old = null;
        if (Records.TryGetValue(relic, out var rec))
        {
            old = rec;
            RemoveCompanions(relic, rec, player);                 // scan-based: never strands an orphan companion
            foreach (var c in rec.Changes)
                if (relic.DynamicVars.TryGetValue(c.VarName, out var dv))
                    dv.BaseValue = c.OldValue;
            Records.Remove(relic);
        }

        try
        {
            // Prefer the host's AUTHORITATIVE descriptor (restore verbatim, no re-roll); fall back to a seed
            // re-derive only when no descriptor was carried (legacy wire). Then graft, reproducing the exact
            // state the host holds so checksums reconverge.
            if (!string.IsNullOrEmpty(desc))
                RestoreForged(relic, desc!, player.RunState.Rng.Seed, relic.FloorAddedToDeck, count, cleansed, gaugeReduction, CharAffix.TitleOf(player));
            else
            {
                Forge(relic, player.RunState.Rng.Seed, relic.FloorAddedToDeck,
                      reforgeCount: count, guaranteePrefix: count > 0, character: CharAffix.TitleOf(player), gaugeReduction: gaugeReduction);
                if (cleansed) ApplyCleanse(relic);
            }
            GrantCompanionIfAny(relic, player);
        }
        catch (Exception e)
        {
            // Roll back to the pre-reconcile state: re-apply the old numeric deltas, re-insert the old
            // record, and re-graft its companion (CompanionGranted was left true on the old record, so
            // clear the stale instance ref first — GrantCompanionIfAny re-checks the flag).
            MainFile.Logger.Warn($"[{MainFile.ModId}] co-op reconcile of {relic.Id.Entry} failed — rolling back: {e.Message}");
            try
            {
                RemoveCompanions(relic, null, player);            // clear any half-granted companion (scan-based)
                Records.Remove(relic);                            // drop any half-built record from the failed rebuild
                if (old != null)
                {
                    foreach (var c in old.Changes)
                        if (relic.DynamicVars.TryGetValue(c.VarName, out var dv))
                            dv.BaseValue = c.NewValue;
                    old.CompanionGranted = false;                 // companion was removed in the undo — re-graft
                    old.Companion = null;
                    Records.Add(relic, old);
                    GrantCompanionIfAny(relic, player);
                }
            }
            catch (Exception e2) { MainFile.Logger.Warn($"[{MainFile.ModId}] reconcile rollback failed: {e2.Message}"); }
            return;
        }
        // Divergence breadcrumb: client-before -> host-after. If a black screen still follows, this line
        // (compared across the two peers' logs) names the relic + enchantment that failed to converge.
        MainFile.Logger.Info($"[{MainFile.ModId}] co-op reconcile {relic.Id.Entry}: client [#{beforeCount} '{beforeDesc}'] "
            + $"-> host [#{count} '{desc}']{(cleansed ? " cleansed" : "")} gred {gaugeReduction}.");
    }

    /// <summary>
    /// Predict the outcome (Reforged vs RolledCurse) a reforge to <paramref name="reforgeCount"/>
    /// would produce, WITHOUT mutating anything. Pure function of (seed, id, floor, count): it rebuilds
    /// the same seeded RNG and draws in the exact fixed order <see cref="Forge"/> uses under a guaranteed
    /// reforge — gate draw, <see cref="PrefixTable.Roll"/>, then the curse roll — then reports whether the
    /// result is a curse (a penalty prefix, or an active enemy-rider / self-curse). The co-op path uses
    /// this because the real mutation lands asynchronously via the synchronized command, yet the initiator's
    /// reforge UI needs the outcome up front to end its reforge on a curse. It matches what the eventual
    /// Reforge produces — both are this same derivation, reading the same host-authoritative config.
    /// </summary>
    public static ReforgeOutcome PredictReforgeOutcome(RelicModel relic, uint runSeed, int floor, int reforgeCount, string? character = null)
        => ReforgeRollsCurse(relic, runSeed, floor, reforgeCount, character)
            ? ReforgeOutcome.RolledCurse : ReforgeOutcome.Reforged;

    // --- Reforge limiting is now PER-RELIC via the curse gauge (see CurseGauge) ---
    // The old probabilistic per-visit "curse aura ends reforging here" limiter was removed: each relic
    // instead fills its own gauge and SATURATES at 100% (IsGaugeSaturated → excluded from Reforgeable),
    // so reforging is bounded per-relic and re-derives deterministically from the seed. A curse no longer
    // ends the visit — it locks the cursed relic (cleanse-only) and adds a bigger gauge chunk. Nothing
    // here is per-visit or UI-counted anymore. ReforgeOutcome.RolledCurse still reports "a curse appeared
    // this reforge" for feedback/logging, but no longer gates a session end.

    /// <summary>Pure "does a guaranteed reforge to <paramref name="count"/> land a CURSE?" — mirrors
    /// <see cref="Forge"/>'s curse determination in the same fixed rng order: a re-homed penalty, a
    /// re-homed WEAKENING (negative-magnitude) prefix, or a rolled enemy-rider / self-curse. No mutation.
    /// Deterministic, so the co-op UI prediction and every client's re-derivation agree.</summary>
    private static bool ReforgeRollsCurse(RelicModel relic, uint runSeed, int floor, int count, string? character)
    {
        uint seed = GradeSeed(runSeed, floor, relic.Id.Entry);
        if (count > 0) seed = SplitMix32(seed + (uint)count * 0x9E3779B9u);
        var rng = new Rng(seed);
        rng.NextFloat();                        // gate draw — ignored under guaranteePrefix, kept for rng order
        string? charTitle = character ?? CharAffix.TitleOf(relic.Owner) ?? CharAffix.LocalTitle();
        Prefix rolled = PrefixTable.Roll(rng, charTitle);
        // Curses disabled (chance 0): Forge drops a rolled penalty / weakening prefix to no-prefix and never
        // rides an AlwaysCurse, so NO reforge lands a curse — mirror that here or the co-op UI would mispredict.
        bool cursesOn = HostForgeConfig.CurseChance > 0;
        if (rolled.Penalty) return cursesOn;    // defensive: Roll can no longer return a penalty
        // A weakening prefix is re-homed to a curse in Forge (guaranteed, unless curses are off) — match that.
        if (rolled.PowerPct < 0 && !rolled.Amplify && !rolled.IsCompanionPrefix) return cursesOn;
        // Otherwise a boon prefix may still roll a curse (the first curse draw, same rng order as Forge).
        double curseRoll = rng.NextFloat();
        return (rolled.AlwaysCurse && cursesOn) || curseRoll < CurseChanceFor(rolled);
    }

    /// <summary>
    /// A HOST relic left the player's inventory (sold via a foreign Sell mod, or any non-silent
    /// <c>RemoveRelicInternal</c>). Its numeric prefix + enemy-rider / self-curse stop mattering on their
    /// own (both read only from relics still in <c>player.Relics</c>), but a GRAFTED companion is a hidden
    /// relic that STAYS in <c>player.Relics</c> after its host is gone — so its effect keeps firing "in the
    /// background". Un-graft it and drop the host's record. Scan-based (like <see cref="RemoveCompanions"/>)
    /// so it runs identically on every co-op peer. No-op for an unforged relic or one with no companion.
    /// </summary>
    public static void OnHostRemoved(RelicModel host, Player player)
    {
        if (host == null || player == null) return;
        if (!Records.TryGetValue(host, out var rec)) return;   // never forged — nothing to clean
        RemoveCompanions(host, rec, player);                   // drop the hidden donor(s) the prefix grafted
        Records.Remove(host);                                  // clear the stale record (re-derives if re-obtained)
    }

    /// <summary>
    /// Remove EVERY hidden companion grafted onto <paramref name="host"/>, found by SCANNING the player's
    /// relic list — deliberately NOT by the record's stored <c>Companion</c> reference.
    ///
    /// A record can be re-derived (LoadRun, combat-start <see cref="HealForge"/>, or a foreign "restart
    /// combat" feature) which resets <c>Companion</c> to null while the OLD donor instance is still owned.
    /// A reference-based removal would then STRAND that orphan; in co-op it strands on only the peer that
    /// re-derived, so the two clients' relic lists differ by one hidden relic — the exact 3-vs-2 count
    /// divergence that fails the game checksum on "Exiting rest site room" and drops the session (black
    /// screen). Scanning removes every companion of this host on every peer identically, so a reforge
    /// always reconverges: after this the host owns no companion, and the new prefix grafts the right one.
    /// </summary>
    private static void RemoveCompanions(RelicModel host, ForgeRecord? rec, Player player)
    {
        // Snapshot: we mutate player.Relics while iterating.
        foreach (var companion in player.Relics.Where(r => IsCompanion(r) && HostOf(r) == host).ToList())
        {
            companion.DisplayAmountChanged -= host.InvokeDisplayAmountChanged; // mirror the grant in reverse
            try
            {
                if (player.Relics.Contains(companion))
                    player.RemoveRelicInternal(companion, silent: true);
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"[{MainFile.ModId}] reforge un-graft failed: {e.Message}");
            }
            Companions.Remove(companion);
        }
        if (rec != null) { rec.Companion = null; rec.CompanionGranted = false; }
    }

    // A grafted effect should be a WEAKER version of owning the real relic — a relic's EMBELLISHMENT,
    // never an equivalent half-relic slot. Reduce each beneficial (INCREASE) var to WeakenFactor and
    // FLOOR it (round DOWN) so a graft never exceeds 30% of the donor — the design cap on a prefix's
    // benefit — with a min of 1 so it never disappears. (Rounding to nearest let e.g. Anchor's Block 10
    // land at 4 = 40%.) Counters/thresholds (SKIP) are left alone. VarOverride handles the odd case where
    // "weaker" means RAISING a var — HappyFlower's "every N turns" interval (3 -> 4). At 0.30 + floor:
    // Anchored 3 Block, Ferocious Vigor 2, Bladed/Tempered 1 — a garnish, well under owning the real relic.
    private const double WeakenFactor = 0.30;
    private static readonly Dictionary<(string relic, string var), decimal> VarOverride = new()
    {
        [("HappyFlower", "Turns")] = 4m,     // every 3 turns -> every 4 (less frequent = weaker)
        // "every N cards" counters (Cards is the modulo) — raise N so they trigger less often.
        [("LetterOpener", "Cards")] = 4m,    // every 3 skills -> 4
        [("Shuriken", "Cards")] = 4m,        // every 3 attacks -> 4
        [("OrnamentalFan", "Cards")] = 4m,   // every 3 attacks -> 4
        [("Kunai", "Cards")] = 4m,           // every 3 attacks -> 4
        [("Nunchaku", "Cards")] = 12m,       // every 10 attacks -> 12
    };

    private static void WeakenCompanion(RelicModel companion)
    {
        string key = companion.GetType().Name;
        foreach (DynamicVar dv in companion.DynamicVars.Values)
        {
            if (VarOverride.TryGetValue((key, dv.Name), out var forced)) { dv.BaseValue = forced; continue; }
            if (dv.BaseValue <= 0m) continue;
            if (AffixPolicy.DirectionFor(key, dv.Name) != AffixDir.Increase) continue; // only cut boons
            dv.BaseValue = Math.Max(1m, Math.Floor(dv.BaseValue * (decimal)WeakenFactor));
        }
    }

    /// <summary>
    /// Forge a candidate relic that isn't owned yet, so its tooltip previews the enhanced
    /// state before pickup (shop offers, reward/event choices). Gated to MUTABLE instances
    /// so canonical templates in ModelDb are never touched. Uses the active run's fixed
    /// seed + current floor; because a shop's offered relic IS the same instance later
    /// passed to Obtain, the previewed grade is exactly what you get.
    /// </summary>
    public static void TryForgePreview(RelicModel relic)
    {
        if (!relic.IsMutable) return;                   // never mutate ModelDb templates
        if (Records.TryGetValue(relic, out _)) return;  // already forged
        // An OWNED relic can momentarily lose its forge record when a foreign combat-restart (e.g.
        // BetterSpire2 double-tap R) swaps its instance without re-forging. Hovering it then reached here
        // and re-forged it against the CURRENT TotalFloor — a DIFFERENT floor than pickup — rolling a
        // different (wrong) prefix that STUCK (e.g. Insightful -> Fickle). Route owned relics through the
        // SAME load/heal derivation (FloorAddedToDeck + reforge count + owner character + companion regraft),
        // so a hover RESTORES the correct affix instead of corrupting it. Only a genuinely OFFERED (unowned)
        // relic previews against the current floor — which is correct, since its pickup floor is the current one.
        if (relic.Owner is Player owner)
        {
            // DISPLAY-only record re-derive against the REAL pickup floor (+ reforge count + character), so
            // the tooltip shows the correct prefix. Deliberately does NOT graft the companion / apply combat
            // effects: a hover is a LOCAL, per-client action, and mutating shared state (adding a hidden
            // relic) on hover would desync co-op. The authoritative companion graft happens at combat start
            // in HealForge (which both peers run). Uses FloorAddedToDeck — NOT TotalFloor — so the derived
            // prefix matches pickup instead of the current floor (the Insightful->Fickle corruption bug).
            int rf = TakePendingReforgeCount(relic);
            bool cleansed = TakePendingCleansed(relic);
            int gred = TakePendingGaugeReduction(relic);
            Forge(relic, owner.RunState.Rng.Seed, relic.FloorAddedToDeck,
                  reforgeCount: rf, guaranteePrefix: rf > 0, character: CharAffix.TitleOf(owner), gaugeReduction: gred);
            if (cleansed) ApplyCleanse(relic);
            return;
        }
        var state = RunManager.Instance.State;
        if (state == null) return;                      // not in a run
        Forge(relic, state.Rng.Seed, state.TotalFloor);
    }

    // Treasure-room relics are shared CANONICAL instances (not per-offer mutables like
    // shop/reward), so they can't be forged in place. Instead we forge a throwaway mutable
    // clone and cache it, keyed by the offered canonical, so the tooltip can preview the
    // enhanced numbers + prefix. Cleared when the treasure closes (EndRelicVoting) so the
    // shared canonical doesn't carry a forged look into collection screens.
    private static readonly Dictionary<RelicModel, RelicModel> OfferedPreview = new();

    public static void OfferPreview(RelicModel canonical, uint runSeed, int floor)
    {
        if (canonical.IsMutable) return;        // ToMutable() would throw on a non-canonical
        var clone = canonical.ToMutable();
        Forge(clone, runSeed, floor);
        OfferedPreview[canonical] = clone;      // latest offer wins
    }

    /// <summary>The forged preview clone for a currently-offered canonical relic, if any.</summary>
    public static RelicModel? PreviewCloneFor(RelicModel canonical)
        => OfferedPreview.TryGetValue(canonical, out var clone) ? clone : null;

    /// <summary>
    /// ON-DEMAND forge preview for a CANONICAL relic being hovered during a run — covers every "offered
    /// relic" surface (event options both inline & factory, treasure, rewards) in one place, so the
    /// tooltip previews the prefix + curse + numbers without a per-screen hook. Owned relics are mutable
    /// (handled by the pickup/hover Prefix), so this only ever fires for offered/shared canonicals.
    /// Returns the (cached) forged clone, or null if not applicable (mutable, or not in a run).
    /// </summary>
    public static RelicModel? PreviewOnHover(RelicModel relic)
    {
        if (relic == null || relic.IsMutable) return null;
        if (OfferedPreview.TryGetValue(relic, out var existing)) return existing;
        var state = RunManager.Instance?.State;
        if (state == null) return null;
        OfferPreview(relic, state.Rng.Seed, state.TotalFloor);
        return PreviewCloneFor(relic);
    }

    public static void ClearOffers() => OfferedPreview.Clear();

    /// <summary>
    /// Enhance a relic deterministically. <paramref name="runSeed"/> is the run's fixed
    /// seed (runState.Rng.Seed) and <paramref name="floor"/> is the floor the relic was
    /// obtained on — TotalFloor at pickup, or relic.FloorAddedToDeck when re-applying on
    /// load (both the same, so the derived prefix is identical across save/load).
    /// <paramref name="forced"/> (test command only) forces a specific prefix and bypasses
    /// the eligibility gate.
    /// <paramref name="reforgeCount"/> folds into the RNG so a re-forged relic rolls a
    /// different-but-deterministic grade; 0 reproduces the original pre-reforge outcome exactly.
    /// <paramref name="guaranteePrefix"/> skips the no-prefix gate (used by reforge, so paying
    /// to re-roll never yields "no prefix"). Returns a log summary or null.
    /// </summary>
    public static string? Forge(RelicModel relic, uint runSeed, int floor, Prefix? forced = null,
                                int reforgeCount = 0, bool guaranteePrefix = false, string? character = null,
                                int gaugeReduction = 0, bool suppressCurse = false)
    {
        if (ForgeSafeMode.Active) return null;              // sister-mod mismatch — forge inert (see ForgeSafeMode)
        if (Records.TryGetValue(relic, out _)) return null; // already processed

        bool test = forced != null;
        // Eligibility gates only the automatic pickup forge. A forced test or a deliberate reforge
        // (guaranteePrefix) may prefix any relic, including Starter/Event rarities.
        if (!test && !guaranteePrefix && !PrefixTable.Eligible.Contains(relic.Rarity)) return null;
        // Opt-out for Ancient (先古) relics: keep them pure vanilla when the player disables it. Only
        // the automatic pickup forge is gated here — a forced test or deliberate reforge still may
        // touch them — but the reforge picker also hides Ancient relics while this is off, so in
        // normal play they stay untouched end-to-end.
        if (!test && !guaranteePrefix && !HostForgeConfig.ForgeAncient
            && relic.Rarity == RelicRarity.Ancient) return null;

        string relicId = relic.Id.Entry;               // canonical UPPER_SNAKE id (seed + logs)
        // Per-relic policy (Overrides/BoostFactor) is keyed by the C# class name, which is
        // PascalCase — NOT Id.Entry (which is UPPER_SNAKE like "LOST_COFFER"). Keying those
        // lookups off Id.Entry silently missed every per-relic override/boost. Use the type
        // name so the hand-authored PascalCase tables actually match.
        string policyKey = relic.GetType().Name;

        // Same derivation the game uses for per-relic RNG (see decompile ~line 291714).
        // reforgeCount==0 leaves the seed byte-identical to the original behavior, so existing
        // saves / first grades never shift; count>0 avalanche-mixes the count in so each re-roll
        // is an unrelated but reproducible grade.
        uint seed = GradeSeed(runSeed, floor, relicId);
        if (reforgeCount > 0)
            seed = SplitMix32(seed + (uint)reforgeCount * 0x9E3779B9u);
        var rng = new Rng(seed);

        // Prefix gate + roll. Draw both from the seeded rng in a fixed order so the
        // outcome stays deterministic and stable even if the config threshold changes:
        // a relic's would-be prefix is fixed by seed, and whether it applies depends on
        // the gate roll vs the (config) no-prefix chance. Forced (test) skips the gate.
        // Character-gated prefixes only enter the pool for the owning player's character. At
        // pickup the relic isn't owned yet (relic.Owner is null), so the caller passes the
        // obtaining player's character explicitly; on load/reforge the owner is set; preview
        // (offered, unowned) falls back to the local player. Fixed per run → stays deterministic.
        // Hoisted above the prefix branch because the curse pick (below) is also character-gated.
        string? charTitle = character ?? CharAffix.TitleOf(relic.Owner) ?? CharAffix.LocalTitle();
        Prefix? prefix;
        if (forced != null)
        {
            prefix = forced;
        }
        else
        {
            double gate = rng.NextFloat();
            Prefix rolled = PrefixTable.Roll(rng, charTitle);
            // Draw the gate roll unconditionally (fixed rng order) but ignore it when
            // guaranteePrefix is set, so a reforge always lands a prefix.
            prefix = (!guaranteePrefix && gate < HostForgeConfig.NoPrefixChance) ? null : rolled;
        }

        // E — CURSES COME FROM GREED, NOT LOOT: a PICKUP carries no downside, so a curse can land ONLY on a
        // deliberate reforge (guaranteePrefix) or a forced test. A pickup that rolled a penalty / weakening
        // (negative-magnitude) prefix — which on a reforge becomes a pure curse — is dropped to no-prefix
        // (vanilla) here instead, so pickups are boon-or-nothing. The reforge path is unchanged: it still
        // re-homes those to a "curse-only" roll (the gamble's worst outcome).
        // suppressCurse: the RESTORE path (RestoreForged) forces the saved prefix through here just to
        // recompute the numeric deltas — the curse is overwritten verbatim from the descriptor afterward.
        // Without this, the internal curse roll (same seed, but allowCurse=true because forced!=null)
        // could LAND a curse the original pickup never had, which then satisfies the fallback-substitution
        // gate below and silently mutates the saved prefix into a fallback one on every load.
        bool allowCurse = !suppressCurse && (guaranteePrefix || forced != null);
        // A rolled penalty / weakening (negative-magnitude) prefix becomes a CURSE downstream (re-home).
        // When it must NOT (pickup, or curses fully disabled), handle it here instead — never letting it
        // reach the curse re-home. Skipped for a FORCED (test) prefix so `forge <relic> Broken` still demos
        // the re-home on demand.
        bool cursesDisabled = HostForgeConfig.CurseChance <= 0 && forced == null;
        if (prefix != null && IsWeakeningOrPenalty(prefix))
        {
            if (!allowCurse)
                prefix = null;                                 // pickup: "curses come from greed, not loot" → vanilla
            else if (cursesDisabled)
                prefix = RerollBoonPrefix(rng, charTitle);     // reforge, curses OFF: re-roll to a boon, never a dud
            // else (reforge, curses ON): keep it — the negative re-home below turns it into a curse (by design)
        }

        // Enemy-rider roll (a Terraria-style curse): any forged relic MIGHT also strengthen enemies.
        // Drawn in fixed rng order so the stream stays stable if the chance is retuned; whether it
        // lands depends on the config chance, and penalty prefixes never carry it. A second draw
        // picks the flavor suffix name (used only if the rider lands).
        // One MUTUALLY-EXCLUSIVE curse: a non-penalty relic may carry either an enemy-rider curse OR a
        // self-curse, never both. Three fixed-order draws: does it carry a curse, which kind, which one.
        double curseRoll = rng.NextFloat();      // carries a curse at all?
        double curseTypeRoll = rng.NextFloat();  // if so: self-curse vs enemy-rider
        double cursePickRoll = rng.NextFloat();  // which specific curse of the chosen kind

        if (prefix == null)
        {
            // No prefix — but a curse may still land ON ITS OWN: a "curse-only" relic (pure downside, no
            // boon), the outcome that replaces the old penalty prefixes. Uses the already-drawn curse rolls
            // (no new rng, no stream shift) at the reduced CurseOnlyChance. Record it either way so the
            // relic isn't re-rolled.
            var bare = new ForgeRecord { Rarity = relic.Rarity, Prefix = "", Percent = 0, ReforgeCount = reforgeCount, GaugeReduction = gaugeReduction };
            if (allowCurse && curseRoll < CurseOnlyChance())   // pickups never curse-only (E) — reforge/test only
            {
                // Same kind split as a boon+curse, incl. the enemy-forge-off self-forcing (a real curse,
                // not an inert rider). One curse only.
                bool self = !HostForgeConfig.EnemyForgeEnabled || curseTypeRoll < HostForgeConfig.SelfCurseShare;
                if (self)
                    bare.SelfCurse = SelfCurseTable.PickCombined(cursePickRoll, charTitle);
                else
                {
                    bare.EnemyRider = true;
                    bare.EnemyRiderSuffix = RiderSuffix.Pick(cursePickRoll);
                }
            }
            Records.Add(relic, bare);
            return bare.SelfCurse.Length > 0 ? $"curse-only: {bare.SelfCurse}"
                 : bare.EnemyRider ? $"curse-only: rider {bare.EnemyRiderSuffix}" : null;
        }

        double pct = prefix.PowerPct;
        var record = new ForgeRecord { Rarity = relic.Rarity, Prefix = prefix.Name, Percent = pct, Amplify = prefix.Amplify, ReforgeCount = reforgeCount, GaugeReduction = gaugeReduction };
        bool rehomedNegative = false;   // a weakening prefix converted to a pure curse (skip all numeric logic)
        if (prefix.Penalty && !prefix.IsFallback)
        {
            // A FORCED penalty prefix (test: `forge <relic> <Penalty>`) — auto-rolls never reach here with
            // a penalty since InPool now excludes them. Re-home it onto the curse slot so its effect+trigger
            // fire via its own patch (which reads rec.SelfCurse), matching the auto-roll. The prefix slot is
            // left empty (a penalty grafts nothing and scales no host var — there is no boon to keep).
            record.Prefix = "";
            record.Percent = 0;
            record.Amplify = false;
            record.SelfCurse = prefix.Name;
        }
        // CONSOLIDATION: a relic-WEAKENING prefix (negative magnitude: Damaged / Shoddy / Broken) is no
        // longer a prefix at all — every downside is a CURSE. Re-home it to a real curse (the same self /
        // enemy-rider split a boon+curse uses, drawn from the already-rolled curse dice so the rng stream
        // never shifts) and early-return below so the magnitude NEVER scales the relic's vars. The prefix
        // slot is left empty: the relic reads as a pure curse-only downside, exactly like the penalty
        // prefixes. (Penalties are handled above; positive/Amplify prefixes fall through to the optional
        // boon-curse roll.)
        else if (pct < 0 && !prefix.Amplify && !prefix.IsCompanionPrefix)
        {
            record.Prefix = "";
            record.Percent = 0;
            record.Amplify = false;
            bool self = !HostForgeConfig.EnemyForgeEnabled || curseTypeRoll < HostForgeConfig.SelfCurseShare;
            if (self)
            {
                // The self-side downside manifests as EITHER an on-hit self-curse (the primary flavor) OR —
                // reviving the penalty-fallback family — a combat-start chance-gated self-debuff
                // (FallbackBuffPatch). A stream-safe SplitMix bit (no rng draw, so the curse dice stay
                // untouched and co-op/load reproduce it) picks: ~1/3 penalty fallback, ~2/3 on-hit curse,
                // keeping the harsher per-hit curse dominant while the milder timed debuff adds variety.
                if (SplitMix32(seed ^ 0x7A3B1D2Fu) % 3u == 0u)
                {
                    Prefix pf = PrefixTable.PickFallbackPenalty(SplitMix32(seed ^ 0x2C9E7F15u));
                    record.Prefix = pf.Name;                 // named so the tooltip shows the penalty note + odds
                    record.FallbackPercent = FallbackPenaltyChanceFor(pct);
                    record.FallbackStat = pf.FallbackStat;
                    record.FallbackAmount = pf.FallbackAmount;
                }
                else
                    record.SelfCurse = SelfCurseTable.PickCombined(cursePickRoll, charTitle);
            }
            else
            {
                record.EnemyRider = true;
                record.EnemyRiderSuffix = RiderSuffix.Pick(cursePickRoll);
            }
            rehomedNegative = true;
        }
        // Exactly one curse (or none) rides on a beneficial prefix. AlwaysCurse prefixes (e.g. 공명의/Resonant)
        // always take the enemy-rider — their designed cost — UNLESS curses are fully disabled (chance 0), in
        // which case even AlwaysCurse yields no curse (Resonant becomes a pure boon; the knob wins). Otherwise
        // CurseChance gates whether there's a curse; SelfCurseShare decides the kind. The self-curse pool is now
        // the COMBINED pool (on-hit curses + re-homed penalty identities), so a penalty's downside rides a boon.
        else if (allowCurse && ((prefix.AlwaysCurse && HostForgeConfig.CurseChance > 0) || curseRoll < CurseChanceFor(prefix)))
        {
            // Curse rides a boon ONLY on a reforge/test (E — pickups are curse-free). With enemy-forge OFF an
            // enemy-rider is inert, so a NON-AlwaysCurse curse manifests as a self-curse instead.
            bool self = !prefix.AlwaysCurse
                        && (!HostForgeConfig.EnemyForgeEnabled || curseTypeRoll < HostForgeConfig.SelfCurseShare);
            if (self)
                record.SelfCurse = SelfCurseTable.PickCombined(cursePickRoll, charTitle);
            else
            {
                record.EnemyRider = true;
                record.EnemyRiderSuffix = RiderSuffix.Pick(cursePickRoll);
            }
        }
        Records.Add(relic, record); // guard re-forge even if nothing changes

        // A re-homed weakening prefix is a pure curse — skip companion graft, var scaling, the reforge
        // floor and the fallback substitution entirely (there is no boon to scale or rescue).
        if (rehomedNegative)
        {
            if (record.FallbackStat.Length > 0)
                return $"{prefix.Name}->{record.Prefix} {relicId}: {record.FallbackPercent}% {record.FallbackStat} (penalty fallback)";
            return $"{prefix.Name}->curse {relicId}: " +
                   (record.SelfCurse.Length > 0 ? record.SelfCurse : "rider " + record.EnemyRiderSuffix);
        }

        // Companion-family prefix (grafted OR delayed): don't scale the host's vars. Grafted
        // prefixes graft a donor later (GrantCompanionIfAny); delayed prefixes apply a fixed
        // effect on a set turn (DelayedCompanionPatch). Works on ANY host relic.
        if (prefix.IsCompanionPrefix)
        {
            record.CompanionRelic = prefix.CompanionRelic; // null for delayed prefixes
            // A fallback prefix is normally only reached via substitution below (which sets chance+stat);
            // if one is FORCED directly (test command `forge <relic> Honed`), give it a visible default
            // so it actually fires and previews in-game.
            if (prefix.IsFallback && record.FallbackPercent == 0)
            {
                // No fizzled-tier context when FORCED, so show a representative mid-band chance
                // (buff 35 of 20/35/50; penalty 15 of 10/15/20) rather than a made-up 100%.
                record.FallbackPercent = prefix.Penalty ? 15 : 35;
                record.FallbackStat = prefix.FallbackStat;
                record.FallbackAmount = prefix.FallbackAmount;
            }
            return $"{prefix.Name} {relicId}: {(prefix.CompanionRelic != null ? "graft " + prefix.CompanionRelic.Name : "delayed t" + prefix.DelayTurn)}";
        }

        foreach (DynamicVar dv in relic.DynamicVars.Values)
        {
            decimal baseVal = dv.BaseValue;
            if (baseVal <= 0m) continue; // semantic flags (=0) and empties

            var dir = AffixPolicy.DirectionFor(policyKey, dv.Name);
            if (dir == AffixDir.Skip) continue;

            // Magnitude = proportional round of |pct| (NO min-1 floor, so a small prefix
            // on a small base rounds to 0 = no change, instead of always jumping +1 which
            // is a huge relative gain on a base like 1). Sign from pct (negative weakens);
            // direction decides which way: INCREASE var moves with the sign, DECREASE
            // (downside/counter) var moves against.
            int mag = (int)Math.Round(baseVal * (decimal)(Math.Abs(pct) * AffixPolicy.BoostFor(policyKey, dv.Name)), MidpointRounding.AwayFromZero);
            decimal newVal;
            if (prefix.Amplify)
            {
                // raise every var's raw magnitude regardless of benefit direction
                newVal = baseVal + mag;
            }
            else
            {
                int signed = pct >= 0 ? mag : -mag;
                newVal = dir == AffixDir.Increase ? baseVal + signed : baseVal - signed;
            }
            newVal = Math.Max(1m, newVal); // never disable a trigger / divide-by-zero

            if (newVal == baseVal) continue;
            dv.BaseValue = newVal; // setter also ResetToBase() -> tooltip picks it up
            record.Changes.Add(new VarChange
            {
                VarName = dv.Name, OldValue = baseVal, NewValue = newVal, Dir = dir
            });
        }

        // Tier tie-break: a positive prefix that rounded to the same var delta as the tier below it gains
        // a small combat-start chance-of-more so it stays strictly better (see ApplyTierTiebreak). Applies
        // whenever it actually scaled a grantable stat — pickup, reforge, and load alike.
        if (pct > 0 && !prefix.Amplify && record.HasChanges)
            ApplyTierTiebreak(policyKey, prefix, record);

        // B2 floor: a GUARANTEED reforge that produced no numeric change (a low-tier prefix rounding
        // to 0) shouldn't feel empty. If the relic has a large-enough primary var, nudge THAT ONE var
        // by 1 in the prefix's direction. Gated to base >= ReforgeFloorMinBase so small relics are
        // never over-buffed, to reforge (guaranteePrefix) so pickups keep their honest rounding, and
        // to non-Amplify numeric prefixes (Amplify is the high-variance gamble — a fizzle stays a fizzle).
        if (guaranteePrefix && !record.HasChanges && !prefix.Amplify)
        {
            DynamicVar? primary = null; decimal bestBase = 0m; var pdir = AffixDir.Increase;
            foreach (DynamicVar dv in relic.DynamicVars.Values)
            {
                if (dv.BaseValue < ReforgeFloorMinBase) continue;
                var d = AffixPolicy.DirectionFor(policyKey, dv.Name);
                if (d == AffixDir.Skip) continue;
                if (dv.BaseValue > bestBase) { bestBase = dv.BaseValue; primary = dv; pdir = d; }
            }
            if (primary != null)
            {
                decimal baseVal = primary.BaseValue;
                int signed = pct >= 0 ? 1 : -1;
                decimal newVal = Math.Max(1m, pdir == AffixDir.Increase ? baseVal + signed : baseVal - signed);
                if (newVal != baseVal)
                {
                    primary.BaseValue = newVal;
                    record.Changes.Add(new VarChange { VarName = primary.Name, OldValue = baseVal, NewValue = newVal, Dir = pdir });
                }
            }
        }

        // Fallback substitution: a POSITIVE magnitude prefix (not Amplify, not a companion/effect
        // prefix) that scaled NOTHING on this relic — too small / var-less, and the floor above couldn't
        // help — would otherwise show a named prefix that does nothing. Replace it with a host-independent
        // chance-gated minor combat-start buff (FallbackBuffPatch), whose odds reflect the fizzled tier,
        // so the prefix always does something and the chance is shown. Scoped to where an empty boon is a
        // real loss: a PAID reforge, or a relic that also carries a curse (the boon is its counterweight).
        // A plain uncursed PICKUP keeps its honest rounding (a weak roll reads as vanilla). Negative /
        // Amplify fizzles are left as-is (a dodged downside / a deliberate high-variance gamble).
        // Deterministic: the pick uses a derived seed (SplitMix, no main-rng draw), so relics that DID
        // change stay byte-identical and co-op/load reproduce the same substitution.
        if (!record.HasChanges && !prefix.IsCompanionPrefix && !prefix.Amplify && pct > 0
            && (guaranteePrefix || record.EnemyRider || record.SelfCurse.Length > 0))
        {
            Prefix fb = PrefixTable.PickFallback(SplitMix32(seed ^ 0x5F356495u));
            int chance = FallbackChanceFor(pct);
            string original = prefix.Name;
            record.Prefix = fb.Name;   // same record instance in Records — downstream reads the fallback
            record.Percent = 0;
            record.Amplify = false;
            record.FallbackPercent = chance;
            record.FallbackStat = fb.FallbackStat;
            record.FallbackAmount = fb.FallbackAmount;
            return $"{original}->{fb.Name} {relicId}: {chance}% {fb.FallbackStat} +{fb.FallbackAmount}";
        }
        // (A negative-magnitude prefix never reaches here: it was re-homed above — to a self-curse, an
        // enemy-rider, or (~1/3 of self downsides) a penalty fallback — and early-returned, so there is no
        // "fizzled downside" left to rescue here.)

        if (!record.HasChanges) return null;
        var sb = new StringBuilder();
        sb.Append(prefix.Name).Append(' ').Append(relicId)
          .Append(" (").Append(pct >= 0 ? "+" : "").Append((int)Math.Round(pct * 100)).Append("%): ");
        sb.Append(string.Join(", ", record.Changes.ConvertAll(c => $"{c.VarName} {c.OldValue:0}->{c.NewValue:0}")));
        return sb.ToString();
    }
}

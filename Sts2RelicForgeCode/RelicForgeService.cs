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

    /// <summary>The SavedProperty name our reforge count rides on inside a serialized relic.</summary>
    public const string RfCountKey = "__rf_count";

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
    /// CLEANSE a relic's curse. Removes the enemy-rider or self-curse and keeps the prefix; if the
    /// prefix ITSELF is the curse (a penalty prefix), purges it so the relic falls back to un-prefixed
    /// (a penalty prefix has no upside to keep). Sets a persisted "cleansed" flag so the seed-derived
    /// curse doesn't return on load. A later reforge re-rolls everything (fresh record, cleansed
    /// cleared). Returns true if a curse was removed.
    /// </summary>
    public static bool Cleanse(RelicModel relic)
    {
        if (!Records.TryGetValue(relic, out var rec)) return false;
        if (!IsCursedRecord(rec)) return false; // nothing to cleanse
        StripCurse(rec);
        relic.Flash();
        MainFile.Logger.Info($"[{MainFile.ModId}] cleansed curse from {relic.Id.Entry}.");
        return true;
    }

    /// <summary>Whether this relic currently carries a curse that <see cref="Cleanse"/> would remove —
    /// a NON-mutating check. Used by the co-op cleanse seam (<see cref="ReforgeNet.Cleanse"/>) to decide
    /// whether to charge gold + dispatch, since the actual strip runs on every client via the synced
    /// command rather than locally here.</summary>
    public static bool CanCleanse(RelicModel relic)
        => Records.TryGetValue(relic, out var rec) && IsCursedRecord(rec);

    /// <summary>Re-apply the cleansed state to a just-re-derived record (load path, and the co-op
    /// per-client apply — see <see cref="ReforgeNet.ApplyCleanseOnClient"/>).</summary>
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

    /// <summary>Chance (percent) the PENALTY fallback fires, banded from the fizzled NEGATIVE prefix's
    /// tier. Deliberately LOW (10/15/20) — a fizzled downside on a paid reforge keeps a little bite, but
    /// far less often than a buff fallback helps. See RelicForgeService.Forge substitution.</summary>
    private static int FallbackPenaltyChanceFor(double pct)
        => pct <= -0.25 ? 20 : pct <= -0.18 ? 15 : 10;

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
        double boon = prefix.PowerPct > 0 ? prefix.PowerPct
                    : prefix.PowerPct < 0 ? 0.02
                    : CurseRefPower;
        return Math.Clamp(HostForgeConfig.CurseChance * (boon / CurseRefPower), CurseFloor, CurseCap);
    }

    /// <summary>The forge record for a relic instance, or null if it was never forged.</summary>
    public static ForgeRecord? RecordFor(RelicModel relic)
        => Records.TryGetValue(relic, out var rec) ? rec : null;

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
    public static void GrantCompanionIfAny(RelicModel host, Player player)
    {
        var rec = RecordFor(host);
        if (rec?.CompanionRelic == null || rec.CompanionGranted) return;

        var template = ModelDb.AllRelics.FirstOrDefault(r => r.GetType() == rec.CompanionRelic);
        if (template == null)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] companion type {rec.CompanionRelic.Name} not found in ModelDb.");
            return;
        }
        RelicModel companion = template.ToMutable();
        WeakenCompanion(companion);                // grafted effect is a reduced version of the real relic
        Companions.Add(companion, host);           // tag (value=host) BEFORE adding so save/inventory/vfx patches see it
        player.AddRelicInternal(companion, -1, silent: true);
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
        string? summary = Forge(relic, seed, relic.FloorAddedToDeck,
              reforgeCount: rf, guaranteePrefix: rf > 0, character: CharAffix.TitleOf(player));
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

    /// <summary>Whether a freshly re-forged record counts as a CURSE for the reforge-ends-on-curse rule:
    /// a penalty prefix always does; an enemy-rider / self-curse does only while enemy-forge is enabled
    /// (a recorded rider is inert when the toggle is off, so it would be confusing to end a reforge on an
    /// invisible curse — with the toggle off only self-downside penalty prefixes end it, the old behavior).</summary>
    private static bool RolledCurseForReforge(ForgeRecord rec)
        => (PrefixTable.ByName(rec.Prefix)?.Penalty ?? false)
           || (HostForgeConfig.EnemyForgeEnabled && (rec.EnemyRider || rec.SelfCurse.Length != 0));

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
                                reforgeCount: next, guaranteePrefix: true, character: CharAffix.TitleOf(player));

        // (3) If the new prefix is a graft companion, grant it.
        GrantCompanionIfAny(relic, player);

        // (4) Did this roll land a CURSE (penalty prefix, or an active enemy-rider / self-curse)? Both
        //     the campfire AND shop callers use this to END their reforge action — a curse can't be
        //     re-rolled away, only Cleansed (the risk that gives the gamble teeth).
        bool cursed = Records.TryGetValue(relic, out var newRec) && RolledCurseForReforge(newRec);
        MainFile.Logger.Info($"[{MainFile.ModId}] reforge #{next} {relic.Id.Entry}: {summary ?? "(no numeric change)"}{(cursed ? " [CURSE]" : "")}");
        return cursed ? ReforgeOutcome.RolledCurse : ReforgeOutcome.Reforged;
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
    {
        uint seed = GradeSeed(runSeed, floor, relic.Id.Entry);
        if (reforgeCount > 0)
            seed = SplitMix32(seed + (uint)reforgeCount * 0x9E3779B9u);
        var rng = new Rng(seed);
        rng.NextFloat();                        // gate draw — ignored under guaranteePrefix, kept for rng order
        string? charTitle = character ?? CharAffix.TitleOf(relic.Owner) ?? CharAffix.LocalTitle();
        Prefix rolled = PrefixTable.Roll(rng, charTitle);  // the prefix a guaranteed reforge lands
        if (rolled.Penalty) return ReforgeOutcome.RolledCurse;   // a penalty prefix is always a curse
        // Replicate Forge's FIRST curse draw (fixed rng order) to see whether an enemy-rider / self-curse
        // rides this prefix. It only ends the reforge while enemy-forge is on (matches Reforge()); an
        // AlwaysCurse prefix (e.g. Resonant) carries its rider unconditionally.
        double curseRoll = rng.NextFloat();
        bool cursed = HostForgeConfig.EnemyForgeEnabled
                      && (rolled.AlwaysCurse || curseRoll < CurseChanceFor(rolled));
        return cursed ? ReforgeOutcome.RolledCurse : ReforgeOutcome.Reforged;
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
    // never an equivalent half-relic slot. Reduce each beneficial (INCREASE) var to WeakenFactor,
    // floored at 1 so it never disappears. Counters/thresholds (SKIP) are left alone. VarOverride
    // handles the odd case where "weaker" means RAISING a var — HappyFlower's "every N turns" interval
    // (3 -> 4). Kept at ~1/3 (was 0.6, which let e.g. Anchored graft +6 Block ≈ half an Anchor); a
    // third keeps grafts a garnish (Anchored 4 Block, Thorned 1 Thorn vs Bronze Scales' 3).
    private const double WeakenFactor = 0.35;
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
            dv.BaseValue = Math.Max(1m, Math.Round(dv.BaseValue * (decimal)WeakenFactor, MidpointRounding.AwayFromZero));
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
            Forge(relic, owner.RunState.Rng.Seed, relic.FloorAddedToDeck,
                  reforgeCount: rf, guaranteePrefix: rf > 0, character: CharAffix.TitleOf(owner));
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
                                int reforgeCount = 0, bool guaranteePrefix = false, string? character = null)
    {
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
        Prefix? prefix;
        if (forced != null)
        {
            prefix = forced;
        }
        else
        {
            // Character-gated prefixes only enter the pool for the owning player's character. At
            // pickup the relic isn't owned yet (relic.Owner is null), so the caller passes the
            // obtaining player's character explicitly; on load/reforge the owner is set; preview
            // (offered, unowned) falls back to the local player. Fixed per run → stays deterministic.
            string? charTitle = character ?? CharAffix.TitleOf(relic.Owner) ?? CharAffix.LocalTitle();
            double gate = rng.NextFloat();
            Prefix rolled = PrefixTable.Roll(rng, charTitle);
            // Draw the gate roll unconditionally (fixed rng order) but ignore it when
            // guaranteePrefix is set, so a reforge always lands a prefix.
            prefix = (!guaranteePrefix && gate < HostForgeConfig.NoPrefixChance) ? null : rolled;
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
            // No prefix (stays vanilla). Record it anyway so the relic isn't re-rolled.
            Records.Add(relic, new ForgeRecord { Rarity = relic.Rarity, Prefix = "", Percent = 0, ReforgeCount = reforgeCount });
            return null;
        }

        double pct = prefix.PowerPct;
        var record = new ForgeRecord { Rarity = relic.Rarity, Prefix = prefix.Name, Percent = pct, Amplify = prefix.Amplify, ReforgeCount = reforgeCount };
        // Exactly one curse (or none). AlwaysCurse prefixes (e.g. 공명의/Resonant) always take the
        // enemy-rider — their designed cost. Otherwise CurseChance gates whether there's a curse and
        // SelfCurseShare decides the kind (self-curse vs enemy-rider). Penalty prefixes never carry one.
        if (!prefix.Penalty && (prefix.AlwaysCurse || curseRoll < CurseChanceFor(prefix)))
        {
            bool self = !prefix.AlwaysCurse && curseTypeRoll < HostForgeConfig.SelfCurseShare;
            if (self)
                record.SelfCurse = SelfCurseTable.Pick(cursePickRoll).En;
            else
            {
                record.EnemyRider = true;
                record.EnemyRiderSuffix = RiderSuffix.Pick(cursePickRoll);
            }
        }
        Records.Add(relic, record); // guard re-forge even if nothing changes

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
        // Mirror for a NEGATIVE magnitude prefix that scaled nothing: on a PAID reforge only, replace it
        // with a low-chance combat-start self-debuff so a fizzled downside keeps a little of the gamble's
        // bite. Plain uncursed pickups keep their dodged-downside luck (honest rounding). Same derived-seed
        // determinism as the buff branch.
        else if (!record.HasChanges && !prefix.IsCompanionPrefix && !prefix.Amplify && pct < 0 && guaranteePrefix)
        {
            Prefix fb = PrefixTable.PickFallbackPenalty(SplitMix32(seed ^ 0x2545F491u));
            int chance = FallbackPenaltyChanceFor(pct);
            string original = prefix.Name;
            record.Prefix = fb.Name;
            record.Percent = 0;
            record.Amplify = false;
            record.FallbackPercent = chance;
            record.FallbackStat = fb.FallbackStat;
            record.FallbackAmount = fb.FallbackAmount;
            return $"{original}->{fb.Name} {relicId}: {chance}% self {fb.FallbackStat} +{fb.FallbackAmount}";
        }

        if (!record.HasChanges) return null;
        var sb = new StringBuilder();
        sb.Append(prefix.Name).Append(' ').Append(relicId)
          .Append(" (").Append(pct >= 0 ? "+" : "").Append((int)Math.Round(pct * 100)).Append("%): ");
        sb.Append(string.Join(", ", record.Changes.ConvertAll(c => $"{c.VarName} {c.OldValue:0}->{c.NewValue:0}")));
        return sb.ToString();
    }
}

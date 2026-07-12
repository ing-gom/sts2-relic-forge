using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>One var that the forge changed, with signed delta for tooltip coloring.</summary>
internal sealed class VarChange
{
    public string VarName = "";
    public decimal OldValue;
    public decimal NewValue;
    public AffixDir Dir;

    /// <summary>Numeric change (new - old); its sign is what the tooltip prints (+N / -N).</summary>
    public decimal Delta => NewValue - OldValue;

    /// <summary>Whether this change HELPS the player. Colour is by benefit, not numeric sign:
    /// an INCREASE var is good when it rose; a DECREASE (downside/counter) var is good when it
    /// fell. A negative prefix flips these. So a downside dropping shows a red-free green -N.</summary>
    public bool IsBuff => Dir == AffixDir.Increase ? NewValue > OldValue : NewValue < OldValue;
}

/// <summary>
/// What the forge did to one relic instance. Keyed by RelicModel in
/// RelicForgeService.Records; read by the tooltip patches for the title prefix (Tier)
/// and per-var colored deltas (Changes). Also the unit that a future save/load layer
/// will persist by (relicId, floorAddedToDeck).
/// </summary>
internal sealed class ForgeRecord
{
    public RelicRarity Rarity;
    public string Prefix = "";
    public double Percent;               // 0.30 = +30%
    public bool Amplify;                 // the rolled prefix was an amplify (Volatile) one
    public readonly List<VarChange> Changes = new();
    public bool HasChanges => Changes.Count > 0;

    // How many times this relic instance has been RE-forged (rolled again after the first
    // grade). 0 = the original seed-deterministic grade (unchanged from pre-reforge behavior);
    // N folds into the RNG so the outcome is still deterministic given (seed, id, floor, N).
    // This is the ONLY forge state that can't be re-derived from the seed, so it is the one
    // value persisted across save/load (SavedProperty "__rf_count", see ReforgeSaveInjectPatch).
    public int ReforgeCount;

    // Curse-gauge REDUCTION: cumulative gauge percentage-points removed by cleansing. The gauge
    // (RelicForgeService.CurseGauge) is the seeded per-reforge raw fill (counts 1..ReforgeCount) MINUS this,
    // clamped to [0,100]. A cleanse of a saturated relic subtracts the whole current fill (→ 0); a cleanse
    // of a CURSED relic subtracts only half (→ ~50%). Later reforges add to the raw fill, so the gauge
    // climbs again from the reduced point. Player-driven like ReforgeCount and NOT seed-derivable, so it is
    // persisted the same way (SavedProperty "__rf_gred") and carried across reforges + the rf_counts co-op
    // sync. 0 = never cleansed.
    public int GaugeReduction;

    // Enemy-rider: a Terraria-style "curse" that a forged relic MAY also carry — while owned, it
    // strengthens elites/bosses (see EnemyForge). Rolled deterministically at forge time, so it is
    // re-derived on load (not persisted). Never set on penalty prefixes.
    public bool EnemyRider;

    // The flavor SUFFIX name (English key, e.g. "Wrath") shown on the relic when it carries the
    // enemy-rider curse — "Legendary Anchor of Wrath". Localized at display via RiderSuffix.
    public string EnemyRiderSuffix = "";

    // Cleansed: the curse was removed at a shop (see RelicForgeService.Cleanse). Persisted (as
    // "__rf_cleansed") so the seed-derived curse doesn't come back on load; cleared by a reforge.
    public bool Cleansed;

    // Display-only: this record was reconstructed for the RUN-HISTORY view from the serialized forge
    // summary (see RelicForgeService.RegisterDisplayRecord). It has NO var Changes (deltas aren't
    // stored) — the tooltip shows just the prefix name + curse — and is never re-forged.
    public bool DisplayOnly;

    // Self-curse: an INDEPENDENT player-side "저주" (English key, e.g. "Enfeebling") a forged relic may
    // also carry — it punishes the OWNER on unblocked hits (see SelfCurseTable / UnblockedHitPenaltyPatch).
    // Its own roll dimension: separate from the prefix pool AND the enemy-rider slot, so a relic can carry
    // a boon prefix, an enemy-rider curse, AND a self-curse at once. Re-derived on load (not persisted).
    public string SelfCurse = "";

    // Companion prefix: the donor relic type to graft, and a guard so the hidden instance
    // is granted exactly once per host instance (re-derived, not persisted — see
    // CompanionSerializationPatch + RunLoadReforgePatch).
    public Type? CompanionRelic;
    public bool CompanionGranted;

    // The granted hidden companion instance (runtime only), so the host icon can mirror the
    // companion's counter (e.g. "attacks until next trigger"). Null for delayed/non-graft prefixes.
    public RelicModel? Companion;

    // Fallback / tie-break combat-start chance buff (see FallbackBuffPatch). Two producers:
    //  · SUBSTITUTION — a magnitude prefix that rounded to NO change on this relic was replaced by a
    //    fallback prefix (Prefix is set to the fallback's name);
    //  · TIER TIE-BREAK — a positive prefix whose var delta ties the tier just below it keeps its
    //    prefix name but gains this chance buff to stay strictly better (RelicForgeService.ApplyTierTiebreak).
    // FallbackPercent is the fire chance; FallbackStat/Amount is what the roll grants at combat start
    // (Strength/Dexterity/Block/Thorns, or a self-debuff Weak/Frail/Vulnerable for a penalty fallback).
    // 0 = none. All re-derived on load (not persisted).
    public int FallbackPercent;
    public string FallbackStat = "";
    public int FallbackAmount;
}

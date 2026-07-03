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

    // Enemy-rider: a Terraria-style "curse" that a forged relic MAY also carry — while owned, it
    // strengthens elites/bosses (see EnemyForge). Rolled deterministically at forge time, so it is
    // re-derived on load (not persisted). Never set on penalty prefixes.
    public bool EnemyRider;

    // The flavor SUFFIX name (English key, e.g. "Wrath") shown on the relic when it carries the
    // enemy-rider curse — "Legendary Anchor of Wrath". Localized at display via RiderSuffix.
    public string EnemyRiderSuffix = "";

    // Companion prefix: the donor relic type to graft, and a guard so the hidden instance
    // is granted exactly once per host instance (re-derived, not persisted — see
    // CompanionSerializationPatch + RunLoadReforgePatch).
    public Type? CompanionRelic;
    public bool CompanionGranted;

    // The granted hidden companion instance (runtime only), so the host icon can mirror the
    // companion's counter (e.g. "attacks until next trigger"). Null for delayed/non-graft prefixes.
    public RelicModel? Companion;
}

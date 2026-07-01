using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Relics;

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
}

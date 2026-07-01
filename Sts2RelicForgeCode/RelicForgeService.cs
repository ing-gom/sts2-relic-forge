using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
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

    /// <summary>The forge record for a relic instance, or null if it was never forged.</summary>
    public static ForgeRecord? RecordFor(RelicModel relic)
        => Records.TryGetValue(relic, out var rec) ? rec : null;

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

    public static void ClearOffers() => OfferedPreview.Clear();

    /// <summary>
    /// Enhance a relic deterministically. <paramref name="runSeed"/> is the run's fixed
    /// seed (runState.Rng.Seed) and <paramref name="floor"/> is the floor the relic was
    /// obtained on — TotalFloor at pickup, or relic.FloorAddedToDeck when re-applying on
    /// load (both the same, so the derived prefix is identical across save/load).
    /// <paramref name="forced"/> (test command only) forces a specific prefix and bypasses
    /// the eligibility gate. Returns a log summary or null.
    /// </summary>
    public static string? Forge(RelicModel relic, uint runSeed, int floor, Prefix? forced = null)
    {
        if (Records.TryGetValue(relic, out _)) return null; // already processed

        bool test = forced != null;
        if (!test && !PrefixTable.Eligible.Contains(relic.Rarity)) return null;

        string relicId = relic.Id.Entry;               // canonical UPPER_SNAKE id (seed + logs)
        // Per-relic policy (Overrides/BoostFactor) is keyed by the C# class name, which is
        // PascalCase — NOT Id.Entry (which is UPPER_SNAKE like "LOST_COFFER"). Keying those
        // lookups off Id.Entry silently missed every per-relic override/boost. Use the type
        // name so the hand-authored PascalCase tables actually match.
        string policyKey = relic.GetType().Name;

        // Same derivation the game uses for per-relic RNG (see decompile ~line 291714).
        uint seed = (uint)((int)runSeed + floor + StringHelper.GetDeterministicHashCode(relicId));
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
            double gate = rng.NextFloat();
            Prefix rolled = PrefixTable.Roll(rng);
            prefix = gate < ForgeConfig.NoPrefixChance ? null : rolled;
        }

        if (prefix == null)
        {
            // No prefix (stays vanilla). Record it anyway so the relic isn't re-rolled.
            Records.Add(relic, new ForgeRecord { Rarity = relic.Rarity, Prefix = "", Percent = 0 });
            return null;
        }

        double pct = prefix.PowerPct;
        var record = new ForgeRecord { Rarity = relic.Rarity, Prefix = prefix.Name, Percent = pct, Amplify = prefix.Amplify };
        Records.Add(relic, record); // guard re-forge even if nothing changes

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

        if (!record.HasChanges) return null;
        var sb = new StringBuilder();
        sb.Append(prefix.Name).Append(' ').Append(relicId)
          .Append(" (").Append(pct >= 0 ? "+" : "").Append((int)Math.Round(pct * 100)).Append("%): ");
        sb.Append(string.Join(", ", record.Changes.ConvertAll(c => $"{c.VarName} {c.OldValue:0}->{c.NewValue:0}")));
        return sb.ToString();
    }
}

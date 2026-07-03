using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Random;

namespace Sts2RelicForge;

/// <summary>
/// Shared combat-forge utilities used by BOTH the prefix path (<see cref="ForgeCombatAffixPatch"/>,
/// symmetric debuffs on one ally + one enemy) and the suffix path (<see cref="EnemyForge"/>, rider
/// buffs on one enemy). Only the genuinely common bits live here; each system keeps its own dispatch.
/// </summary>
internal static class ForgeCombat
{
    /// <summary>Pick one creature from a list using the given (seed-fixed) rng, or null if empty.</summary>
    public static Creature? PickOne(IReadOnlyList<Creature> list, Rng rng)
    {
        if (list == null || list.Count == 0) return null;
        int idx = (int)(rng.NextFloat() * list.Count);
        if (idx >= list.Count) idx = list.Count - 1;
        return list[idx];
    }
}

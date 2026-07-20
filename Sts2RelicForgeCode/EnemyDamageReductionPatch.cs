using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;

namespace Sts2RelicForge;

/// <summary>
/// Calloused (굳은살) enemy-rider curse: a forged enemy takes a FLAT amount less damage from every hit
/// (recorded on its <see cref="EnemyForgeTag.DamageReduction"/>). Unlike a hard damage CAP, this subtracts a
/// constant, so it bites multi-hit / small-hit decks hardest (each hit loses the full reduction) while barely
/// denting a single big hit — the intended downside.
///
/// Rides the game's own damage pipeline: <see cref="Hook.ModifyDamage"/> is the single point that computes a
/// hit's final amount (summing every listener's ModifyDamageAdditive, then the multiplicative pass). A postfix
/// on it subtracts the target's reduction from the final result — the same additive-then-clamped shape the
/// native powers use, and it flows into damage PREVIEWS too, so the number shown on cards already reflects it.
/// Pure deterministic calculation (no state mutation, no command, target tag from the synced enemy-forge) →
/// co-op-safe by construction: both peers compute the identical reduced number.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
internal static class EnemyDamageReductionPatch
{
    private static void Postfix(ref decimal __result, Creature? target, ModifyDamageHookType modifyDamageHookType)
    {
        try
        {
            if (__result <= 0m) return;
            if (!modifyDamageHookType.HasFlag(ModifyDamageHookType.Additive)) return;   // apply once, on the full pass
            if (target == null || target.Player != null) return;                        // forged ENEMIES only
            var tag = EnemyForge.TagOf(target);
            if (tag == null || tag.DamageReduction <= 0) return;
            __result -= tag.DamageReduction;
            if (__result < 0m) __result = 0m;
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] enemy damage reduction failed: {e.Message}"); }
    }
}

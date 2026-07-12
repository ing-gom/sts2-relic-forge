using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Disables a curse-gauge-SATURATED relic's own effect while keeping its curse. Every relic effect fires
/// because the game's hook dispatch iterates the run/combat hook listeners, which include each non-melted
/// relic (RunState.IterateHookListeners / CombatState.IterateHookListeners — both filter <c>!IsMelted</c>).
/// We postfix both to ALSO drop any relic whose effect is disabled by saturation (see
/// RelicForgeService.IsEffectDisabled) — so the saturated relic's native game effect AND its forge numeric
/// boons (read through those same native hooks) go silent, generically, for every relic and mod.
///
/// The CURSE is untouched: enemy-rider curses (EnemyForge) and self-curses (UnblockedHitPenaltyPatch) fire
/// from a DIRECT <c>player.Relics</c> scan, not the hook listener list, so a saturated relic keeps punishing
/// you until it is cleansed (which resets its gauge and re-enables the effect). Deterministic (saturation is
/// a pure function of the synced reforge count + gauge reduction), so every co-op peer filters identically.
///
/// The mod's SEPARATE forge combat-start buffs (FallbackBuffPatch / ForgeCombatAffixPatch) patch the hook
/// METHOD and scan player.Relics themselves, so they are gated on saturation at their own sites, not here.
/// </summary>
internal static class SaturatedRelicFilter
{
    /// <summary>Lazily wrap a hook-listener sequence, skipping relics disabled by saturation. Non-relic
    /// listeners (cards, powers, potions, orbs — the bulk of the list) pass straight through, and the
    /// per-relic saturation test is memoized (RelicForgeService.CurseGauge), so the overhead on this hot
    /// path stays O(n) with cheap per-item work.</summary>
    public static IEnumerable<AbstractModel> Apply(IEnumerable<AbstractModel> src)
    {
        if (src == null) yield break;
        foreach (var m in src)
        {
            if (m is RelicModel r && RelicForgeService.IsEffectDisabled(r)) continue;
            yield return m;
        }
    }
}

[HarmonyPatch(typeof(RunState), nameof(RunState.IterateHookListeners), new[] { typeof(CombatState) })]
internal static class RunStateHookFilterPatch
{
    private static void Postfix(ref IEnumerable<AbstractModel> __result)
    {
        try { __result = SaturatedRelicFilter.Apply(__result); }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] run-state hook filter failed: {e.Message}"); }
    }
}

[HarmonyPatch(typeof(CombatState), nameof(CombatState.IterateHookListeners), new Type[0])]
internal static class CombatStateHookFilterPatch
{
    private static void Postfix(ref IEnumerable<AbstractModel> __result)
    {
        try { __result = SaturatedRelicFilter.Apply(__result); }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] combat-state hook filter failed: {e.Message}"); }
    }
}

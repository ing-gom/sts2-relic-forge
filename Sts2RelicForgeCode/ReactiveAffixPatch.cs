using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace Sts2RelicForge;

/// <summary>
/// Event interception for the reactive prefix family (see <see cref="ReactiveAffix"/> /
/// REACTIVE_PREFIXES.md). These Harmony patches ride the game's per-event Hook methods — the same
/// idiom as <see cref="ForgeCombatAffixPatch"/> (which patches Hook.AfterPlayerTurnStart) — because
/// the mod tags EXISTING game relics with a prefix rather than owning a custom RelicModel it could
/// override the hook virtuals on. Each patch just reads the event and calls the engine; all
/// mutations go through networked/deterministic commands, so co-op needs no extra sync.
/// </summary>
internal static class ReactiveAffixPatches
{
    /// <summary>공명의 / Resonant — a power's amount changed. If the player self-gained Strength or
    /// Dexterity (&gt; 0), grant +1 more. Also the primary capture point for the combat context that
    /// 방전의 borrows. Fires post-modify-pipeline, so <c>amount</c> is the final signed delta.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPowerAmountChanged))]
    internal static class AfterPowerAmountChangedPatch
    {
        private static void Postfix(PlayerChoiceContext choiceContext, PowerModel power, decimal amount, Creature? applier)
        {
            try
            {
                ReactiveAffix.CaptureCtx(choiceContext);
                if (amount <= 0m) return;
                if (!(power is StrengthPower || power is DexterityPower)) return;
                var owner = power.Owner;
                if (owner?.Side != CombatSide.Player) return;   // target is the player
                if (applier?.Side != CombatSide.Player) return; // self-gain only (enemy-applied handled by Obstinate)
                var player = owner.Player;
                if (player == null) return;
                ReactiveAffix.OnPlayerGainPower(choiceContext, player, power is DexterityPower, (int)amount);
            }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] reactive power hook failed: {e.Message}"); }
        }
    }

    /// <summary>완강한 / Obstinate — the amount of a power being applied to a creature is being
    /// finalized. If it's an enemy-applied Strength/Dexterity loss on the player and the player owns
    /// an Obstinate relic, flip the applied amount to its positive (loss becomes gain) in one step.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyPowerAmountReceived))]
    internal static class ModifyPowerAmountReceivedPatch
    {
        private static void Postfix(PowerModel canonicalPower, Creature target, Creature? giver, ref decimal __result)
        {
            try
            {
                if (ReactiveAffix.ShouldInvertEnemyLoss(canonicalPower, target, __result, giver))
                    __result = -__result;
            }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] reactive invert hook failed: {e.Message}"); }
        }
    }

    /// <summary>방전의 / Discharging — the player gained BONUS energy (the turn-start refill sets
    /// energy directly and never reaches this hook). Deal the discharge damage to all enemies.
    /// <c>__result</c> is the final energy gained after all modifiers.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyEnergyGain))]
    internal static class ModifyEnergyGainPatch
    {
        private static void Postfix(ICombatState combatState, Player player, decimal __result)
        {
            try
            {
                if (__result > 0m)
                    ReactiveAffix.OnPlayerGainBonusEnergy(player, combatState, (int)__result);
            }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] reactive energy hook failed: {e.Message}"); }
        }
    }

    /// <summary>Turn start: refresh the captured context and reset the echo-suppress counter. Runs
    /// before the player plays cards, so 방전의 has a context in hand even for a turn-1 energy gain.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
    internal static class TurnStartCapturePatch
    {
        private static void Postfix(PlayerChoiceContext choiceContext)
        {
            ReactiveAffix.CaptureCtx(choiceContext);
            ReactiveAffix.ResetTurn();
        }
    }
}

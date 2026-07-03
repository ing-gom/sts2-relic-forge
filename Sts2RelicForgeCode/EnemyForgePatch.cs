using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;

namespace Sts2RelicForge;

/// <summary>
/// The "enemy forge" balance mechanism (see <see cref="EnemyForge"/>): on the first player turn of
/// an ELITE or BOSS fight, every enemy rolls a prefix and gains the matching native buffs, scaled by
/// how much enemy-rider curse power the player carries. Normal fights and a disabled mechanic are
/// never touched.
///
/// Hooked at <c>Hook.AfterPlayerTurnStart</c> (turn == 1) — the same proven entry point as
/// <see cref="DelayedCompanionPatch"/>, which hands us a live <see cref="PlayerChoiceContext"/>.
/// Enemies already have their final spawn HP by turn 1, and <see cref="EnemyForge.ForgeEnemy"/>
/// self-guards against re-applying to an already-forged enemy.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
internal static class EnemyForgePatch
{
    private static void Postfix(ICombatState combatState, PlayerChoiceContext choiceContext, Player player)
    {
        try
        {
            int turn = player.PlayerCombatState?.TurnNumber ?? 0;
            if (turn < 1) return;

            // Turn 1: forge one enemy (apply the once-off buffs, register the recurring ones).
            if (turn == 1)
            {
                double mag = EnemyForge.Magnitude(player);
                RoomType room = combatState.Encounter?.RoomType ?? RoomType.Unassigned;
                bool isBoss = room == RoomType.Boss || EnemyForge.TestAsBoss;
                bool eligibleRoom = EnemyForge.TestForce || EnemyForge.TestAsBoss || isBoss || room == RoomType.Elite;
                MainFile.Logger.Info($"[{MainFile.ModId}] enemy-forge check: room={room} mag={mag:F2} enabled={ForgeConfig.EnemyForgeEnabled} test={EnemyForge.TestForce} eligible={eligibleRoom}");

                if (mag > 0 && eligibleRoom)
                {
                    var enemies = new List<Creature>(combatState.HittableEnemies);
                    if (enemies.Count > 0)
                    {
                        // Seed-fixed rng (runSeed, floor) → deterministic & MP-safe. Pick ONE enemy; all
                        // rider buffs stack on it, amounts rolled within their ranges from this same rng.
                        uint seed = player.RunState?.Rng.Seed ?? 0;
                        int floor = player.RunState?.TotalFloor ?? 0;
                        var rng = new Rng((uint)((int)seed + floor * 7919 + 4211));
                        var target = ForgeCombat.PickOne(enemies, rng);   // shared with the prefix path
                        if (target != null)
                        {
                            var tag = EnemyForge.ForgeEnemy(target, isBoss, choiceContext, player, rng);
                            MainFile.Logger.Info($"[{MainFile.ModId}] enemy-forge: buffed '{target.Name}' ({(tag != null ? "ok" : "none")}) of {enemies.Count} enemies.");
                        }
                    }
                }
            }

            // Every turn: drive recurring buffs (Frenzied +Strength every 3rd turn, Regen 50%/turn, …).
            EnemyForge.RunPeriodic(combatState, choiceContext, player, turn);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] enemy forge apply failed: {e}");
        }
    }
}

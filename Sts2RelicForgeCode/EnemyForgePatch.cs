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
/// EVERY combat (normal, elite, and boss), the enemies gain the native buffs matching the enemy-rider
/// curses the player carries, scaled by how much rider power they hold. Bosses get a higher tier
/// multiplier; normal packs share the same total buff pool spread across the mobs. A disabled mechanic
/// (master toggle off) is never touched.
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
                // Rider power-buffs now reach NORMAL fights too (RoomType.Monster), not just elites/bosses —
                // the pack round-robin spreads the same total buff pool across the (weaker) mobs, so every
                // combat "fights back". Unassigned covers the `fight` debug entry and event-spawned combats.
                bool eligibleRoom = EnemyForge.TestForce || EnemyForge.TestAsBoss || isBoss
                                    || room == RoomType.Elite || room == RoomType.Monster
                                    || room == RoomType.Unassigned;
                MainFile.Logger.Info($"[{MainFile.ModId}] enemy-forge check: room={room} mag={mag:F2} enabled={ForgeConfig.EnemyForgeEnabled} test={EnemyForge.TestForce} eligible={eligibleRoom}");

                // Max-HP curses broadcast to ALL enemies (normal fights included), scope-gated —
                // independent of the elite/boss-only single-enemy decoration below.
                if (mag > 0 || EnemyForge.TestForce)
                    EnemyForge.ApplyHpCurses(combatState, choiceContext, player, room, isBoss);

                if (mag > 0 && eligibleRoom)
                {
                    var enemies = new List<Creature>(combatState.HittableEnemies);
                    if (enemies.Count > 0)
                    {
                        // Rider curses are DISTRIBUTED across the pack (round-robin); a single enemy
                        // (boss) gets them all, and duplicated curses stack. Fixed values, so no rng.
                        EnemyForge.ForgePack(enemies, choiceContext, player);
                        MainFile.Logger.Info($"[{MainFile.ModId}] enemy-forge: distributed rider curses across {enemies.Count} enemies.");
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

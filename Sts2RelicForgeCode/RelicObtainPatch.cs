using System;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// RelicCmd.Obtain(relic, player, index) is the single choke point for ALL relic
/// acquisition (rewards, shops, events, treasure, generic Obtain&lt;T&gt;). The relic passed
/// in is already .ToMutable() with its own cloned DynamicVarSet, so a Prefix here can
/// enhance it before it is added to the player and before AfterObtained() fires
/// (important for on-pickup relics like MaxHp/Gold that read their value immediately).
///
/// There are two Obtain overloads; we target the non-generic (RelicModel, Player, …)
/// one — the generic Obtain&lt;T&gt; funnels into it, so this covers every path.
///
/// Resolved via <see cref="TargetMethod"/> (runtime probing) rather than an attribute-pinned exact
/// signature: this is the SINGLE choke point for the whole pickup-forge feature, and an exact pin
/// silently kills it when a game update adds/renames a trailing parameter (the LethalSummonDamagePatch
/// lesson — CreatureCmd.Damage grew a tail param and the pinned patch just vanished). Probing by
/// name + (RelicModel, Player) parameter prefix survives signature drift; a total miss logs loudly.
/// </summary>
[HarmonyPatch]
internal static class RelicObtainPatch
{
    private static System.Reflection.MethodBase? TargetMethod()
    {
        var m = typeof(RelicCmd)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(x => x.Name == nameof(RelicCmd.Obtain) && !x.IsGenericMethodDefinition)
            .Where(x =>
            {
                var p = x.GetParameters();
                return p.Length >= 2 && p[0].ParameterType == typeof(RelicModel)
                                     && p[1].ParameterType == typeof(Player);
            })
            .OrderByDescending(x => x.GetParameters().Length)   // most specific (the real worker) first
            .FirstOrDefault();
        if (m == null)
            MainFile.Logger.Warn($"[{MainFile.ModId}] RelicCmd.Obtain(RelicModel, Player, …) not found — " +
                                 "PICKUP FORGE DISABLED (game update changed the signature?).");
        return m;
    }

    private static void Prefix(RelicModel relic, Player player)
    {
        try
        {
            var runState = player.RunState;

            // TRANSMUTE INHERIT (Sts2RelicTransmute): if a sibling mod stashed a forge descriptor on this
            // relic before obtaining it, RESTORE that state verbatim instead of a fresh pickup roll — so a
            // transmuted relic carries the SOURCE relic's prefix (numeric or fallback), curse, reforge count
            // and curse-gauge onto the new relic (no curse-laundering; gauge preserved). Mirrors the
            // save/load restore in RunLoadReforgePatch, reusing the same pending-desc facility. Normal
            // obtains never set a pending descriptor, so this is a pure no-op for them.
            string? pendingDesc = RelicForgeService.TakePendingDesc(relic);
            if (!string.IsNullOrEmpty(pendingDesc))
            {
                int rf = RelicForgeService.TakePendingReforgeCount(relic);
                bool cleansed = RelicForgeService.TakePendingCleansed(relic);
                int gred = RelicForgeService.TakePendingGaugeReduction(relic);
                if (RelicForgeService.RestoreForged(relic, pendingDesc!, runState.Rng.Seed, runState.TotalFloor,
                        rf, cleansed, gred, CharAffix.TitleOf(player)) != null)
                    MainFile.Logger.Info($"[{MainFile.ModId}] inherited forge on {relic.Id.Entry} [{pendingDesc}]");
            }
            else
            {
                // Pass the obtaining player's character explicitly — the relic isn't added yet, so
                // relic.Owner is still null and the roll can't derive the character on its own.
                string? summary = RelicForgeService.Forge(relic, runState.Rng.Seed, runState.TotalFloor,
                                                          character: CharAffix.TitleOf(player));
                if (summary != null)
                    MainFile.Logger.Info($"[{MainFile.ModId}] forged {summary}");
            }
            // If this relic carries a companion prefix (fresh-rolled OR inherited), graft the hidden donor
            // relic now (we have the player here). No-op for numeric/no prefix; guarded to grant once.
            RelicForgeService.GrantCompanionIfAny(relic, player);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] forge failed: {e.Message}");
        }
    }
}

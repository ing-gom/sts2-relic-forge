using System;
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
/// There are two Obtain overloads; we target the non-generic (RelicModel, Player, int)
/// one — the generic Obtain&lt;T&gt; funnels into it, so this covers every path.
/// </summary>
[HarmonyPatch(typeof(RelicCmd), nameof(RelicCmd.Obtain), new[] { typeof(RelicModel), typeof(Player), typeof(int) })]
internal static class RelicObtainPatch
{
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

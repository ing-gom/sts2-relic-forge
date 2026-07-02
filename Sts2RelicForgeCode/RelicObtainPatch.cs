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
            string? summary = RelicForgeService.Forge(relic, runState.Rng.Seed, runState.TotalFloor);
            if (summary != null)
                MainFile.Logger.Info($"[{MainFile.ModId}] forged {summary}");
            // If this relic rolled a companion prefix, graft the hidden donor relic now (we
            // have the player here). No-op for numeric/no prefix; guarded to grant once.
            RelicForgeService.GrantCompanionIfAny(relic, player);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] forge failed: {e.Message}");
        }
    }
}

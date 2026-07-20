using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Forging (단조의) AURA — while you carry a live relic with this prefix, EVERY relic you obtain immediately
/// reforges ONE other eligible relic you own, exactly like a campfire/shop reforge: it runs the same
/// <see cref="ReforgeNet.Reforge"/> path, so it re-rolls the target's prefix AND builds its curse charge — leaning
/// on it ramps curse risk, the mechanic's built-in cost (self-limiting). A Postfix on the same RelicCmd.Obtain
/// choke point as the pickup-forge (<see cref="RelicObtainPatch"/>): it fires AFTER the new relic is forged and
/// added, so the new relic is excluded from the pool and the aura is read off the post-obtain inventory.
///
/// Co-op: gated to the LOCAL player (sp || LocalContext.IsMe) so it runs ONCE from the owner's peer, then
/// ReforgeNet.Reforge broadcasts the (relic, count) step and every client re-derives the identical outcome — the
/// exact model the campfire button and Plundering's gold use. The target pick is seed-deterministic so a reload
/// reproduces it. A re-entrancy guard stops a reforge that grafts a companion (a nested Obtain) from recursing.
/// </summary>
[HarmonyPatch]
internal static class PickupReforgePatch
{
    [ThreadStatic] private static bool _busy;

    private static MethodBase? TargetMethod()
        => typeof(RelicCmd).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.Name == nameof(RelicCmd.Obtain) && !x.IsGenericMethodDefinition)
            .Where(x =>
            {
                var p = x.GetParameters();
                return p.Length >= 2 && p[0].ParameterType == typeof(RelicModel)
                                     && p[1].ParameterType == typeof(Player);
            })
            .OrderByDescending(x => x.GetParameters().Length)
            .FirstOrDefault();

    private static void Postfix(RelicModel relic, Player player)
    {
        if (_busy) return;                                   // a reforge-grafted companion re-entered Obtain — don't chain
        try
        {
            if (player == null || relic == null) return;
            var run = RunManager.Instance;
            bool sp = run?.IsSingleplayerOrFakeMultiplayer ?? true;
            if (!(sp || LocalContext.IsMe(player))) return;  // fire ONCE from the owner (ReforgeNet broadcasts in co-op)
            if (!OwnsForgingAura(player)) return;            // aura only while a live Forging relic is held

            var candidates = RestSiteReforgeSupport.Reforgeable(player)
                .Where(r => !ReferenceEquals(r, relic) && !CarriesForging(r))   // never the new relic, never a Forging carrier
                .ToList();
            if (candidates.Count == 0) return;

            var runState = player.RunState;
            var rng = new Rng((uint)((int)runState.Rng.Seed
                        + runState.TotalFloor * 24917
                        + StringHelper.GetDeterministicHashCode(relic.Id.Entry) * 131
                        + player.Relics.Count * 7));
            int idx = (int)(rng.NextFloat() * candidates.Count);
            if (idx >= candidates.Count) idx = candidates.Count - 1;
            var target = candidates[idx];

            _busy = true;
            try { ReforgeNet.Reforge(target, player); }
            finally { _busy = false; }
            MainFile.Logger.Info($"[{MainFile.ModId}] Forging: obtaining {relic.Id.Entry} auto-reforged {target.Id.Entry}.");
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] pickup-reforge failed: {e.Message}"); }
    }

    private static bool OwnsForgingAura(Player player)
    {
        foreach (var r in new List<RelicModel>(player.Relics))
        {
            if (RelicForgeService.IsForgeEffectSuppressed(r)) continue;   // dead relic — its aura is off
            if (CarriesForging(r)) return true;
        }
        return false;
    }

    private static bool CarriesForging(RelicModel r)
    {
        var rec = RelicForgeService.RecordFor(r);
        if (rec == null || rec.Prefix.Length == 0) return false;
        var pfx = PrefixTable.ByName(rec.Prefix);
        return pfx != null && pfx.PickupReforge;
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2RelicForge;

/// <summary>
/// The POTION-META prefix family — each grants an effect when the owner uses a potion:
///   Alchemical (연금의)  Block 4       Fermented (발효의)   Strength 1 (permanent this combat)
///   Distilled (증류의)   Dexterity 1   Corrosive (부식의)   Vulnerable 1 to ALL enemies
///   Diluting (희석의)    Weak 1 all    Effervescent (발포의) 25% chance Buffer 1
/// Deliberately BLOCK / permanent-Str / debuffs / Buffer — never the vanilla temp-Str/Dex-this-turn potion relics,
/// so it stacks a distinct "potion build" rather than overlapping.
///
/// Rides <see cref="Hook.BeforePotionUsed"/> — the awaited hook the game fires inside PotionModel.OnUseWrapper,
/// which runs through the ActionQueueSynchronizer (lockstep on every peer). CHAINS onto its Task so effects land
/// in-order. Block / Str / Dex / Buffer (self) and Vuln / Weak (enemies) are deterministically simulated combat
/// state — the current-HP class, not the host-authoritative gold/Max-HP class — so applying on both peers
/// converges. The Effervescent roll reuses <see cref="CharAffix.Roll"/> (seed-deterministic + picks up the
/// Catalytic/Empowering/Priming aura). Co-op-safe by construction.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.BeforePotionUsed))]
internal static class PotionBlockPatch
{
    private static void Postfix(ref Task __result, PotionModel potion)
        => __result = After(__result, potion);

    private static async Task After(Task original, PotionModel potion)
    {
        await original;
        try
        {
            if (potion?.Owner is not Player player) return;
            var self = player.Creature;
            if (self == null) return;
            var ctx = new BlockingPlayerChoiceContext();
            int turn = player.PlayerCombatState?.TurnNumber ?? 0;
            var enemies = self.CombatState?.HittableEnemies;
            int mult = OwnsDoubler(player) ? 2 : 1;   // Concentrated (농축의) aura DOUBLES every potion-use effect

            foreach (var relic in new List<RelicModel>(player.Relics))
            {
                if (RelicForgeService.IsForgeEffectSuppressed(relic)) continue;
                var rec = RelicForgeService.RecordFor(relic);
                if (rec == null || rec.Prefix.Length == 0) continue;
                var pfx = PrefixTable.ByName(rec.Prefix);
                if (pfx == null) continue;

                bool any = pfx.PotionBlock > 0 || pfx.PotionStr > 0 || pfx.PotionDex > 0
                           || pfx.PotionVuln > 0 || pfx.PotionWeak > 0 || pfx.PotionBufferPct > 0;
                if (!any) continue;
                relic.Flash();

                if (pfx.PotionBlock > 0) await CreatureCmd.GainBlock(self, pfx.PotionBlock * mult, ValueProp.Unpowered, null);
                if (pfx.PotionStr > 0)   await PowerCmd.Apply<StrengthPower>(ctx, self, pfx.PotionStr * mult, self, null);
                if (pfx.PotionDex > 0)   await PowerCmd.Apply<DexterityPower>(ctx, self, pfx.PotionDex * mult, self, null);
                if (pfx.PotionBufferPct > 0 && CharAffix.Roll(player, relic, turn) * 100f < System.Math.Min(100, pfx.PotionBufferPct * mult))
                    await PowerCmd.Apply<BufferPower>(ctx, self, 1, self, null);
                if ((pfx.PotionVuln > 0 || pfx.PotionWeak > 0) && enemies != null)
                    foreach (var enemy in new List<Creature>(enemies))
                    {
                        if (pfx.PotionVuln > 0) await PowerCmd.Apply<VulnerablePower>(ctx, enemy, pfx.PotionVuln * mult, self, null);
                        if (pfx.PotionWeak > 0) await PowerCmd.Apply<WeakPower>(ctx, enemy, pfx.PotionWeak * mult, self, null);
                    }
                MainFile.Logger.Info($"[{MainFile.ModId}] {pfx.Name}: potion-use effect x{mult} ({potion.Id.Entry}).");
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] potion affix hook failed: {e.Message}"); }
    }

    /// <summary>True while the player owns a live relic carrying Concentrated (농축의) — doubles every potion-use
    /// effect. Ownership from the synced relic list → both peers compute the same multiplier (co-op-safe).</summary>
    private static bool OwnsDoubler(Player player)
    {
        foreach (var relic in new List<RelicModel>(player.Relics))
        {
            if (RelicForgeService.IsForgeEffectSuppressed(relic)) continue;
            var rec = RelicForgeService.RecordFor(relic);
            if (rec == null || rec.Prefix.Length == 0) continue;
            var pfx = PrefixTable.ByName(rec.Prefix);
            if (pfx != null && pfx.PotionDoubler) return true;
        }
        return false;
    }
}

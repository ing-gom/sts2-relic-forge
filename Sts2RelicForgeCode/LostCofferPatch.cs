using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// LostCoffer (잃어버린 궤짝) is hardcoded: on pickup it offers one card reward + one potion,
/// no DynamicVar to scale. We replace its AfterObtained with a version that reads the forge
/// prefix and adds extra card rewards / potions per BespokeBonus.LostCoffer. First bespoke
/// booster for hardcoded reward relics.
/// </summary>
[HarmonyPatch(typeof(LostCoffer), "AfterObtained")]
internal static class LostCofferPatch
{
    private static bool Prefix(LostCoffer __instance, ref Task __result)
    {
        try
        {
            __result = Run(__instance);
            return false; // replace the original
        }
        // Replaces AfterObtained, so a synchronous throw here would crash the pickup. Fall back to vanilla.
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] LostCoffer forge run failed, using vanilla: {e.Message}");
            return true; // let the original AfterObtained run
        }
    }

    private static async Task Run(RelicModel relic)
    {
        ForgeRecord? rec = RelicForgeService.RecordFor(relic);
        bool active = rec != null && rec.Prefix.Length > 0;
        double pct = active ? rec!.Percent : 0;
        bool amplify = active && rec!.Amplify;
        (int extraCards, int extraPotions) = BespokeBonus.LostCoffer(pct, amplify);

        var options = new CardCreationOptions(
            new[] { relic.Owner.Character.CardPool },
            CardCreationSource.Other, CardRarityOddsType.RegularEncounter);

        var list = new List<Reward>();
        for (int i = 0; i < 1 + extraCards; i++)
            list.Add(new CardReward(options, 3, relic.Owner));
        int potions = Math.Max(0, 1 + extraPotions);
        for (int i = 0; i < potions; i++)
            list.Add(new PotionReward(relic.Owner));

        await RewardsCmd.OfferCustom(relic.Owner, list);
    }
}

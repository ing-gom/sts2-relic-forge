using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// NeowsTalisman (니오우의 호부) is hardcoded: it upgrades the last basic Strike + last basic
/// Defend (2 cards, no DynamicVar). We replace AfterObtained with a version that upgrades
/// BespokeBonus.NeowsTalismanCount of each — more with a strong prefix, none on a negative.
/// </summary>
[HarmonyPatch(typeof(NeowsTalisman), "AfterObtained")]
internal static class NeowsTalismanPatch
{
    private static bool Prefix(NeowsTalisman __instance, ref Task __result)
    {
        ForgeRecord? rec = RelicForgeService.RecordFor(__instance);
        double pct = (rec != null && rec.Prefix.Length > 0) ? rec.Percent : 0;
        int count = BespokeBonus.NeowsTalismanCount(pct);

        List<CardModel> basics = PileType.Deck.GetPile(__instance.Owner).Cards
            .Where(c => c.Rarity == CardRarity.Basic).ToList();
        UpgradeLast(basics, CardTag.Strike, count);
        UpgradeLast(basics, CardTag.Defend, count);

        __result = Task.CompletedTask;
        return false; // replace the original
    }

    private static void UpgradeLast(List<CardModel> basics, CardTag tag, int count)
    {
        var matches = basics.Where(c => c.Tags.Contains(tag)).ToList();
        int done = 0;
        for (int i = matches.Count - 1; i >= 0 && done < count; i--, done++)
            CardCmd.Upgrade(matches[i]);
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// FromChooseACardScreen is a fixed "1 of ≤3" discover screen that THROWS when handed
/// more than 3 cards. Instead of clamping (losing options) or softlocking, we redirect
/// the over-offer to FromSimpleGrid — the game's own uncapped grid picker — so a forged
/// HeftyTablet/Toolbox can present 4+ cards and you still pick 1. Vanilla never offers
/// more than 3, so this only fires on a forge-boosted count and leaves the nice 3-card
/// screen untouched for everything else (potions, bosses, other discovers).
///
/// (Multi-select like "pick 2 of 4" isn't done here: the calling relic only consumes a
/// single chosen card, so keeping 2 would need per-relic changes.)
/// </summary>
[HarmonyPatch(typeof(CardSelectCmd), "FromChooseACardScreen")]
internal static class CardSelectCapPatch
{
    private static bool Prefix(PlayerChoiceContext context, IReadOnlyList<CardModel> cards,
                               Player player, bool canSkip, ref Task<CardModel?> __result)
    {
        if (cards == null || cards.Count <= 3) return true; // vanilla 3-card screen
        try
        {
            MainFile.Logger.Info($"[{MainFile.ModId}] {cards.Count} cards offered; routing to grid picker.");
            __result = ChooseViaGrid(context, cards, player, canSkip);
            return false; // skip original (which would throw on > 3)
        }
        // If the grid route can't be set up, don't crash the choose-a-card flow — let the original run.
        catch (System.Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] grid picker route failed, using original screen: {e.Message}");
            return true;
        }
    }

    private static async Task<CardModel?> ChooseViaGrid(PlayerChoiceContext context,
                                                        IReadOnlyList<CardModel> cards, Player player, bool canSkip)
    {
        var prefs = new CardSelectorPrefs(new LocString("gameplay_ui", "CHOOSE_CARD_HEADER"),
                                          canSkip ? 0 : 1, 1)
        {
            RequireManualConfirmation = true,
        };
        IEnumerable<CardModel> selected = await CardSelectCmd.FromSimpleGrid(context, cards, player, prefs);
        return selected.FirstOrDefault();
    }
}

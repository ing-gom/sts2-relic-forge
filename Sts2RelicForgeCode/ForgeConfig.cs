using System;

namespace Sts2RelicForge;

/// <summary>Runtime-tunable settings, backed by ModConfig (see MainFile.RegisterConfig).</summary>
internal static class ForgeConfig
{
    /// <summary>
    /// Probability that a relic receives NO prefix (stays vanilla). Default 0.60, i.e.
    /// only ~40% of relics get a Terraria prefix — a prefix is a lucky find. Adjustable
    /// in-game via ModConfig; 0 = every eligible relic is prefixed.
    /// </summary>
    public static double NoPrefixChance = 0.45;

    /// <summary>
    /// Enemy-forge strength multiplier, FIXED at the designed 1.0 (100%). Formerly a 0–200% ModConfig
    /// slider, removed as a redundant fourth difficulty lever: the master <see cref="EnemyForgeEnabled"/>
    /// toggle (on/off), <see cref="CurseChance"/> (how many enemy-rider curses you accumulate), the
    /// hard <c>HeatCap</c>, and the self-inflicted-cost design already bound enemy strength. Kept as a
    /// code-level constant so the <c>× BalanceStrength</c> sites in <see cref="EnemyForge"/> stay a
    /// no-op yet remain a single retune anchor. See <see cref="EnemyForge"/>.
    /// </summary>
    public const double BalanceStrength = 1.0;

    /// <summary>
    /// Escalating gold cost of shop reforges (see <see cref="NMerchantReforgeButton"/>). Uses are
    /// unlimited per shop, but each reforge in the SAME shop visit costs more than the last, so
    /// spamming rerolls is self-limiting. The cost is <see cref="ShopReforgeBaseCost"/> for the first
    /// reforge and rises by <see cref="ShopReforgeCostStep"/> per reforge done in that shop; it resets
    /// to the base at the next shop (the counter lives on the button instance — see
    /// <see cref="NMerchantReforgeButton"/> — so no save persistence is needed). A penalty prefix can
    /// still roll (paid gamble). Not user-tunable (deliberately fixed).
    /// </summary>
    public const int ShopReforgeBaseCost = 30;

    /// <summary>Gold added to the shop reforge cost per reforge already done this shop visit. See
    /// <see cref="ShopReforgeBaseCost"/>.</summary>
    public const int ShopReforgeCostStep = 10;

    /// <summary>Gold cost of the next shop reforge given how many have already been done in the current
    /// shop visit (<paramref name="reforgesThisShop"/> = 0 for the first).</summary>
    public static int ShopReforgeCostFor(int reforgesThisShop) =>
        ShopReforgeBaseCost + ShopReforgeCostStep * Math.Max(0, reforgesThisShop);

    /// <summary>
    /// Fixed gold cost of one shop CLEANSE — remove the curse from a relic, keeping its prefix (see
    /// <see cref="NMerchantCleanseButton"/>). Costs more than a reforge because it's a guaranteed
    /// upside (removes a downside without gambling the prefix). Default 150, adjustable in-game; 0 = free.
    /// </summary>
    public static int ShopCleanseCost = 150;

    /// <summary>
    /// Master switch for the "enemy forge" mechanic (elites &amp; bosses rolling their own prefixes).
    /// Default ON; turn off for a pure power fantasy. The player-side relic forging is unaffected
    /// either way. See <see cref="EnemyForge"/>.
    /// </summary>
    public static bool EnemyForgeEnabled = true;

    /// <summary>
    /// REFERENCE probability that a freshly-forged (non-penalty) relic carries ONE curse — either an
    /// enemy-rider curse (strengthens elites/bosses, see <see cref="EnemyForge"/>) OR a self-curse
    /// (punishes you on unblocked hits, see <see cref="SelfCurseTable"/>), but NEVER both. Which kind
    /// is decided by <see cref="SelfCurseShare"/>. This is the chance for a MID-power prefix; the real
    /// chance scales with the prefix's power ("great power, great cost"), so a weak prefix is rarely
    /// cursed and a Legendary is almost always — see RelicForgeService.CurseChanceFor. Default 0.33.
    /// Penalty prefixes never roll a curse.
    /// </summary>
    public static double CurseChance = 0.33;

    /// <summary>
    /// Whether Ancient (先古) rarity relics may be forged at all. Default true (they roll prefixes
    /// like any other eligible rarity). Turn OFF to leave Ancient relics as pure vanilla — the
    /// automatic pickup forge skips them and they are hidden from the reforge picker, so the mod
    /// never touches them end-to-end. Only Ancient relics are affected; every other rarity is
    /// unchanged. See <see cref="PrefixTable.Eligible"/>.
    /// </summary>
    public static bool ForgeAncientRelics = true;

    /// <summary>
    /// Of the relics that DO carry a curse (see <see cref="CurseChance"/>), the fraction that get a
    /// SELF-CURSE (punishes you on unblocked hits) instead of an enemy-rider curse. The two are
    /// mutually exclusive per relic. Default 0.22 — the self-curse is the harsher, stickier kind
    /// (a self-Vulnerable/Frail lingers all fight), so it's kept the minority: an occasional "bad
    /// luck" roll rather than the norm, while the enemy-rider curse (a self-inflicted, cap-scaled
    /// cost) stays the default. 0 = all curses are enemy-rider; 1 = all curses are self-curse.
    /// Adjustable in-game via ModConfig. See <see cref="SelfCurseTable"/>.
    /// </summary>
    public static double SelfCurseShare = 0.22;
}

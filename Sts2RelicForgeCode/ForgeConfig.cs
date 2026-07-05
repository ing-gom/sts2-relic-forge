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
    /// Master multiplier for the "enemy forge" balance mechanism: elites and bosses always roll
    /// their own Terraria-style prefix (no Ascension or forged relics required). Forging more at
    /// higher Ascension only pushes the strength above the baseline. 1.0 = 100% (the designed
    /// strength), 0 = feature OFF, 2.0 = double. See <see cref="EnemyForge"/>.
    /// </summary>
    public static double BalanceStrength = 1.0;

    /// <summary>
    /// Fixed gold cost of one shop reforge (see <see cref="NMerchantReforgeButton"/>). Uses are
    /// unlimited per shop, so each reforge charges this amount; a penalty prefix can still roll
    /// (paid gamble). Default 50, adjustable in-game via ModConfig (0–100); 0 = free.
    /// </summary>
    public static int ShopReforgeCost = 50;

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
    /// mutually exclusive per relic. Default 0.50 (half self-curse, half enemy-rider). 0 = all curses
    /// are enemy-rider; 1 = all curses are self-curse. See <see cref="SelfCurseTable"/>.
    /// </summary>
    public static double SelfCurseShare = 0.35;
}

using System;

namespace Sts2RelicForge;

/// <summary>Runtime-tunable settings, backed by ModConfig (see MainFile.RegisterConfig).</summary>
internal static class ForgeConfig
{
    /// <summary>
    /// Probability that a relic receives NO prefix (stays vanilla). Default 0.65, i.e. ~35% of PICKED-UP
    /// relics get a prefix — a prefix is a lucky find, and (since E) a pickup prefix is pure upside with no
    /// curse. Surfaced in-game as the POSITIVE "Prefix chance" slider (1 - this); 0 = every eligible relic
    /// is prefixed. Reforging ignores this entirely — it always lands a prefix.
    /// </summary>
    public static double NoPrefixChance = 0.65;

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
    /// spamming rerolls is self-limiting. The FIRST reforge this visit is free (<see cref="ShopReforgeBaseCost"/>
    /// = 0), then it rises by <see cref="ShopReforgeCostStep"/> per reforge done in that shop; it resets to
    /// the base at the next shop (the counter lives on the button instance — see
    /// <see cref="NMerchantReforgeButton"/> — so no save persistence is needed). Reforging is now also bounded
    /// per-relic by the curse gauge (a reforge fills it; at 100% the relic saturates), and a rolled curse
    /// locks that relic to cleanse-only — so gold is the soft, secondary limiter. Not user-tunable.
    /// </summary>
    public const int ShopReforgeBaseCost = 0;

    /// <summary>Gold added to the shop reforge cost per reforge already done this shop visit. See
    /// <see cref="ShopReforgeBaseCost"/>.</summary>
    public const int ShopReforgeCostStep = 15;

    /// <summary>Gold cost of the next shop reforge given how many have already been done in the current
    /// shop visit (<paramref name="reforgesThisShop"/> = 0 for the first).</summary>
    public static int ShopReforgeCostFor(int reforgesThisShop) =>
        ShopReforgeBaseCost + ShopReforgeCostStep * Math.Max(0, reforgesThisShop);

    /// <summary>
    /// BASE gold cost of the FIRST shop CLEANSE this visit — remove the curse from a relic, keeping its
    /// prefix (see <see cref="NMerchantCleanseButton"/>). A cursed relic can no longer be reforged at all
    /// (the reforge picker excludes it), so Cleanse is the ONLY way to shed a curse — priced as the
    /// premium, guaranteed (no-gamble) removal. Like the shop reforge, the cost ESCALATES within a single
    /// shop visit (base + <see cref="ShopCleanseCostStep"/> per cleanse already done — see
    /// <see cref="ShopCleanseCostFor"/>), so clearing several curses at once gets progressively expensive;
    /// the per-visit counter lives on the button instance (<see cref="NMerchantCleanseButton"/>), so it
    /// resets to this base at the next shop. Default 100, adjustable in-game; 0 = free first cleanse.
    /// </summary>
    public static int ShopCleanseCost = 100;

    /// <summary>Gold added to the shop cleanse cost per cleanse already done this shop visit. Fixed
    /// (not user-tunable), mirroring <see cref="ShopReforgeCostStep"/>. See <see cref="ShopCleanseCostFor"/>.</summary>
    public const int ShopCleanseCostStep = 50;

    /// <summary>Gold cost of the next shop cleanse given how many have already been done in the current
    /// shop visit (<paramref name="cleansesThisShop"/> = 0 for the first). Base is the tunable
    /// <see cref="ShopCleanseCost"/>; each prior cleanse this visit adds <see cref="ShopCleanseCostStep"/>.</summary>
    public static int ShopCleanseCostFor(int cleansesThisShop) =>
        ShopCleanseCost + ShopCleanseCostStep * Math.Max(0, cleansesThisShop);

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

    /// <summary>
    /// DIAGNOSTIC / co-op: whether the host re-broadcasts its forge config on EVERY room entry
    /// (<see cref="RoomEnterConfigBroadcastPatch"/>). Default ON — unchanged behavior. Turn OFF only to
    /// diagnose a co-op "black screen on room/event entry" hang: the shop/rest broadcasts remain, so the
    /// host config still reaches clients before shop/rest reforges; only combat-reward / event / treasure
    /// obtains lose the pre-broadcast, which can let their CURSE kind (not the prefix or its numbers)
    /// re-derive per-client until the next shop/rest broadcast. Single-player is unaffected either way —
    /// the broadcast is a no-op off a real co-op host. See <see cref="ForgeConfigBroadcaster.BroadcastIfHost"/>.
    /// </summary>
    public static bool RoomBroadcastEnabled = true;
}

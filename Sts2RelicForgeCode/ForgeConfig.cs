namespace Sts2RelicForge;

/// <summary>Runtime-tunable settings, backed by ModConfig (see MainFile.RegisterConfig).</summary>
internal static class ForgeConfig
{
    /// <summary>
    /// Probability that a relic receives NO prefix (stays vanilla). Default 0.60, i.e.
    /// only ~40% of relics get a Terraria prefix — a prefix is a lucky find. Adjustable
    /// in-game via ModConfig; 0 = every eligible relic is prefixed.
    /// </summary>
    public static double NoPrefixChance = 0.60;
}

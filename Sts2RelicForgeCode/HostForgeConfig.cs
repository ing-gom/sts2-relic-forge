using MegaCrit.Sts2.Core.Multiplayer.Game;   // NetGameType, INetGameService
using MegaCrit.Sts2.Core.Runs;               // RunManager

namespace Sts2RelicForge;

/// <summary>
/// Co-op: make every relic-forge DERIVATION follow the HOST's settings, so all clients converge.
///
/// The forge is deterministic given (seed, id, floor, count) EXCEPT for the config-tunable rolls —
/// curse chance/kind, no-prefix chance, Ancient eligibility, and the enemy-forge strength. Those live
/// in <see cref="ForgeConfig"/>, which is per-CLIENT ModConfig: two players with different sliders would
/// re-derive DIFFERENT curses / enemy prefixes and desync (the reported "reforge in shop desyncs" — the
/// prefix name and numeric magnitude come from static data and already match; only the curse diverged).
///
/// Fix: the host broadcasts its config (<see cref="ForgeConfigBroadcaster"/> → <c>rf_config</c>), every
/// client caches it here, and all forge derivation reads these accessors instead of the raw local
/// <see cref="ForgeConfig"/>. On the host or in single-player the accessors fall through to the local
/// values, so those paths are unchanged.
/// </summary>
internal static class HostForgeConfig
{
    private static bool _received;
    private static double _noPrefix, _curse, _selfShare;
    private static bool _ancient, _enemyForge, _campCleanse;
    private static int _prefixPool;
    private static System.Collections.Generic.HashSet<string> _disabledPrefixes = new();
    private static System.Collections.Generic.HashSet<string> _disabledCurses = new();

    /// <summary>True only when WE are a real co-op client that has received the host's config: the
    /// accessors then return the host values. Host / single-player fall through to local.</summary>
    private static bool UseHost => _received && IsCoopClient;

    private static bool IsCoopClient
        => RunManager.Instance?.NetService?.Type == NetGameType.Client;

    /// <summary>True when we ARE the host of a real co-op game (so we broadcast; we never defer).</summary>
    public static bool IsHost
        => RunManager.Instance?.NetService?.Type == NetGameType.Host;

    // Effective (host-authoritative in co-op) reads used by all forge derivation.
    public static double NoPrefixChance    => UseHost ? _noPrefix   : ForgeConfig.NoPrefixChance;
    public static double CurseChance       => UseHost ? _curse      : ForgeConfig.CurseChance;
    public static double SelfCurseShare    => UseHost ? _selfShare  : ForgeConfig.SelfCurseShare;
    public static bool   ForgeAncient      => UseHost ? _ancient    : ForgeConfig.ForgeAncientRelics;
    public static bool   EnemyForgeEnabled => UseHost ? _enemyForge : ForgeConfig.EnemyForgeEnabled;
    public static bool   CampfireCleanse   => UseHost ? _campCleanse : ForgeConfig.CampfireCleanseEnabled;
    public static int    PrefixPool        => UseHost ? _prefixPool  : ForgeConfig.PrefixPool;

    /// <summary>Custom-pool membership (only consulted when <see cref="PrefixPool"/> == PoolCustom):
    /// host-authoritative name sets, local <see cref="CustomPool"/> on host / single-player.</summary>
    public static bool IsPrefixDisabled(string name)
        => UseHost ? _disabledPrefixes.Contains(name) : CustomPool.DisabledPrefixes.Contains(name);
    public static bool IsCurseDisabled(string key)
        => UseHost ? _disabledCurses.Contains(key) : CustomPool.DisabledCurses.Contains(key);

    /// <summary>Store the host's custom-pool sets (rf_config arg 9, decoded to names client-side).</summary>
    public static void ApplyPoolsFromHost(System.Collections.Generic.HashSet<string> prefixes,
                                          System.Collections.Generic.HashSet<string> curses)
    {
        _disabledPrefixes = prefixes;
        _disabledCurses = curses;
    }

    /// <summary>Enemy-forge strength: a fixed designed constant (the 0–200% slider was removed), so it
    /// is identical on every client and needs no host broadcast — it never diverges. See <see cref="ForgeConfig.BalanceStrength"/>.</summary>
    public static double BalanceStrength   => ForgeConfig.BalanceStrength;

    /// <summary>Store the host's broadcast values (called on every client by the rf_config command).
    /// Idempotent — re-delivery just refreshes the cache, so re-broadcasts and late joins are safe.</summary>
    public static void ApplyFromHost(double noPrefix, double curse, double selfShare,
                                     bool ancient, bool enemyForge, bool campCleanse = true, int prefixPool = 0)
    {
        _noPrefix = noPrefix; _curse = curse; _selfShare = selfShare;
        _ancient = ancient; _enemyForge = enemyForge; _campCleanse = campCleanse; _prefixPool = prefixPool;
        _received = true;
        MainFile.Logger.Info(
            $"[{MainFile.ModId}] host forge config applied: curse {curse:P0}, selfShare {selfShare:P0}, " +
            $"noPrefix {noPrefix:P0}, ancient {ancient}, enemyForge {enemyForge}, prefixPool {prefixPool}.");
    }
}

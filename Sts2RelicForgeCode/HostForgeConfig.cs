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

    // ── RUN LOCK ────────────────────────────────────────────────────────────────────────────────────
    // The forge-RNG settings (prefix chance/pool/custom-pool filter, curse chance, self-curse share,
    // Ancient, enemy-forge) are SNAPSHOT at run start and used for the whole run, so mid-run ModConfig /
    // custom-pool edits only take effect on the NEXT run. This keeps "same seed = same result" honest and
    // removes the co-op config-change desync window. Prices, the cleanse toggles and the co-op room-sync
    // toggle are NOT locked — they stay live. Keyed on the run seed so a new run re-captures automatically
    // (no explicit run-start hook). Host/SP capture from the live ForgeConfig; a co-op CLIENT ignores this
    // entirely and follows the host's broadcast (which itself carries the host's locked snapshot).
    private static uint _lockSeed;
    private static bool _lockValid;
    private static double _lkNoPrefix, _lkCurse, _lkSelfShare;
    private static bool _lkAncient, _lkEnemyForge;
    private static int _lkPrefixPool;
    private static System.Collections.Generic.HashSet<string> _lkDisabledPrefixes = new();
    private static System.Collections.Generic.HashSet<string> _lkDisabledCurses = new();

    /// <summary>Snapshot the forge-RNG settings for the current run (keyed on run seed). Idempotent within
    /// a run; re-captures when the seed changes (= a new run started). No-op outside a run.</summary>
    internal static void EnsureRunLock()
    {
        uint seed;
        try { var st = RunManager.Instance?.State; if (st == null) return; seed = st.Rng.Seed; }
        catch { return; }
        if (_lockValid && _lockSeed == seed) return;
        _lockSeed = seed;
        _lockValid = true;
        _lkNoPrefix   = ForgeConfig.NoPrefixChance;
        _lkCurse      = ForgeConfig.CurseChance;
        _lkSelfShare  = ForgeConfig.SelfCurseShare;
        _lkAncient    = ForgeConfig.ForgeAncientRelics;
        _lkEnemyForge = ForgeConfig.EnemyForgeEnabled;
        _lkPrefixPool = ForgeConfig.PrefixPool;
        _lkDisabledPrefixes = new System.Collections.Generic.HashSet<string>(CustomPool.DisabledPrefixes);
        _lkDisabledCurses   = new System.Collections.Generic.HashSet<string>(CustomPool.DisabledCurses);
        MainFile.Logger.Info($"[{MainFile.ModId}] forge RNG locked for run (seed {seed}): pool {_lkPrefixPool}, " +
            $"noPrefix {_lkNoPrefix:P0}, curse {_lkCurse:P0}, self {_lkSelfShare:P0}, ancient {_lkAncient}, " +
            $"enemyForge {_lkEnemyForge}, custom {_lkDisabledPrefixes.Count}p/{_lkDisabledCurses.Count}c.");
    }

    /// <summary>Encode the RUN-LOCKED custom-pool sets for the host broadcast (so clients receive the
    /// locked snapshot, not the live edit sets).</summary>
    internal static string EncodeLockedPool()
    {
        EnsureRunLock();
        return _lockValid ? CustomPool.Encode(_lkDisabledPrefixes, _lkDisabledCurses) : CustomPool.Encode();
    }

    /// <summary>True only when WE are a real co-op client that has received the host's config: the
    /// accessors then return the host values. Host / single-player fall through to local.</summary>
    private static bool UseHost => _received && IsCoopClient;

    private static bool IsCoopClient
        => RunManager.Instance?.NetService?.Type == NetGameType.Client;

    /// <summary>True when we ARE the host of a real co-op game (so we broadcast; we never defer).</summary>
    public static bool IsHost
        => RunManager.Instance?.NetService?.Type == NetGameType.Host;

    // Effective reads used by all forge derivation. Co-op CLIENT → host broadcast (already the host's
    // locked snapshot). Host / single-player → the RUN-LOCKED snapshot (EnsureRunLock), NOT the live
    // ForgeConfig — so a mid-run edit doesn't take effect until the next run. CampfireCleanse is the one
    // exception below: it stays LIVE (not a forge-RNG setting).
    public static double NoPrefixChance    { get { if (UseHost) return _noPrefix;    EnsureRunLock(); return _lockValid ? _lkNoPrefix   : ForgeConfig.NoPrefixChance; } }
    public static double CurseChance       { get { if (UseHost) return _curse;       EnsureRunLock(); return _lockValid ? _lkCurse      : ForgeConfig.CurseChance; } }
    public static double SelfCurseShare    { get { if (UseHost) return _selfShare;   EnsureRunLock(); return _lockValid ? _lkSelfShare  : ForgeConfig.SelfCurseShare; } }
    public static bool   ForgeAncient      { get { if (UseHost) return _ancient;     EnsureRunLock(); return _lockValid ? _lkAncient    : ForgeConfig.ForgeAncientRelics; } }
    public static bool   EnemyForgeEnabled { get { if (UseHost) return _enemyForge;  EnsureRunLock(); return _lockValid ? _lkEnemyForge : ForgeConfig.EnemyForgeEnabled; } }
    public static int    PrefixPool        { get { if (UseHost) return _prefixPool;  EnsureRunLock(); return _lockValid ? _lkPrefixPool : ForgeConfig.PrefixPool; } }
    public static bool   CampfireCleanse   => UseHost ? _campCleanse : ForgeConfig.CampfireCleanseEnabled;   // NOT locked — live

    /// <summary>Custom-pool membership (only consulted when <see cref="PrefixPool"/> == PoolCustom):
    /// host-authoritative name sets on a client, the RUN-LOCKED snapshot on host / single-player.</summary>
    public static bool IsPrefixDisabled(string name)
    { if (UseHost) return _disabledPrefixes.Contains(name); EnsureRunLock(); return (_lockValid ? _lkDisabledPrefixes : CustomPool.DisabledPrefixes).Contains(name); }
    public static bool IsCurseDisabled(string key)
    { if (UseHost) return _disabledCurses.Contains(key); EnsureRunLock(); return (_lockValid ? _lkDisabledCurses : CustomPool.DisabledCurses).Contains(key); }

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

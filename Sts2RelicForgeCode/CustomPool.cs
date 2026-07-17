using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Sts2RelicForge;

/// <summary>
/// The CUSTOM prefix/curse pool (workshop request): per-entry enable/disable sets that apply when
/// <see cref="ForgeConfig.PrefixPool"/> == <see cref="ForgeConfig.PoolCustom"/>. Holds the LOCAL
/// player's sets (edited in <see cref="NCustomPoolPanel"/>), persists them to a JSON next to the
/// mod DLL, and encodes/decodes them for the rf_config wire (arg 9) as INDICES over deterministic
/// bases — names are authored by sister mods too and may contain spaces, which would break the
/// console-command token split; indices are wire-safe and the bases are already fingerprint-guarded
/// (PoolFingerprint trips safe mode when peers' registered sets differ).
///
/// Bases (must be identical on every peer):
///   • prefixes: <see cref="PrefixTable.Pool"/> order (built-ins + name-sorted externals);
///   • curses:   <see cref="CurseBasis"/> — SelfCurseTable.Pool order, then PrefixTable.All penalty
///     prefixes in source order, UNGATED by character (the pick applies its own character gate, but
///     the wire basis must not depend on who is playing).
/// </summary>
internal static class CustomPool
{
    /// <summary>Disabled prefix NAMEs (Prefix.Name). Local authority — in co-op clients read the
    /// host's copy via <see cref="HostForgeConfig"/>.</summary>
    public static readonly HashSet<string> DisabledPrefixes = new();

    /// <summary>Disabled curse KEYs — SelfCurseDef.En or a penalty prefix's Name (the combined
    /// namespace <see cref="SelfCurseTable.PickCombined"/> draws from).</summary>
    public static readonly HashSet<string> DisabledCurses = new();

    /// <summary>The character-UNGATED curse key basis: every key PickCombined could ever draw.</summary>
    public static List<string> CurseBasis()
    {
        var basis = new List<string>();
        foreach (var c in SelfCurseTable.Pool) basis.Add(c.En);
        foreach (var p in PrefixTable.All)
            if (p.Penalty && !p.IsFallback) basis.Add(p.Name);
        return basis;
    }

    // ---- persistence (local settings, not run state — lives next to the DLL like ModConfig's own) ----

    private static string StorePath()
        => Path.Combine(Path.GetDirectoryName(typeof(CustomPool).Assembly.Location) ?? ".", "custom_pool.json");

    private sealed class Dto { public List<string>? disabledPrefixes { get; set; } public List<string>? disabledCurses { get; set; } }

    public static void Load()
    {
        try
        {
            if (!File.Exists(StorePath())) return;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(StorePath()));
            DisabledPrefixes.Clear();
            foreach (var n in dto?.disabledPrefixes ?? new()) DisabledPrefixes.Add(n);
            DisabledCurses.Clear();
            foreach (var n in dto?.disabledCurses ?? new()) DisabledCurses.Add(n);
            MainFile.Logger.Info($"[{MainFile.ModId}] custom pool loaded: {DisabledPrefixes.Count} prefix(es), {DisabledCurses.Count} curse(s) disabled.");
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] custom pool load failed: {e.Message}"); }
    }

    public static void Save()
    {
        try
        {
            var dto = new Dto { disabledPrefixes = DisabledPrefixes.ToList(), disabledCurses = DisabledCurses.ToList() };
            File.WriteAllText(StorePath(), JsonSerializer.Serialize(dto));
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] custom pool save failed: {e.Message}"); }
    }

    // ---- wire codec (rf_config arg 9): "pIdx,pIdx;cIdx,cIdx", '-' for an empty side ----

    public static string Encode()
    {
        var pool = PrefixTable.Pool;
        var pIdx = new List<int>();
        for (int i = 0; i < pool.Count; i++)
            if (DisabledPrefixes.Contains(pool[i].Name)) pIdx.Add(i);
        var basis = CurseBasis();
        var cIdx = new List<int>();
        for (int i = 0; i < basis.Count; i++)
            if (DisabledCurses.Contains(basis[i])) cIdx.Add(i);
        string p = pIdx.Count == 0 ? "-" : string.Join(",", pIdx);
        string c = cIdx.Count == 0 ? "-" : string.Join(",", cIdx);
        return p + ";" + c;
    }

    /// <summary>Decode a host payload against the LOCAL bases into name sets. A mismatched sister-mod
    /// set would skew indices, but that exact condition already trips ForgeSafeMode via the pool
    /// fingerprint (arg 6), so by the time rolls happen the forge is inert on divergent peers.</summary>
    public static (HashSet<string> prefixes, HashSet<string> curses) Decode(string payload)
    {
        var prefixes = new HashSet<string>();
        var curses = new HashSet<string>();
        try
        {
            var halves = payload.Split(';');
            var pool = PrefixTable.Pool;
            if (halves.Length > 0 && halves[0] != "-")
                foreach (var tok in halves[0].Split(','))
                    if (int.TryParse(tok, out int i) && i >= 0 && i < pool.Count) prefixes.Add(pool[i].Name);
            var basis = CurseBasis();
            if (halves.Length > 1 && halves[1] != "-")
                foreach (var tok in halves[1].Split(','))
                    if (int.TryParse(tok, out int i) && i >= 0 && i < basis.Count) curses.Add(basis[i]);
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] custom pool decode failed: {e.Message}"); }
        return (prefixes, curses);
    }
}

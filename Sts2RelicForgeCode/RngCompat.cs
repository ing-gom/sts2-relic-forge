using System;
using System.Reflection;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Version-tolerant construction of <see cref="Rng"/> and reading of the run seed.
///
/// ★WHY. Game v0.110.0 widened the run seed from 32 to 64 bits, which reshaped every entry point
/// this mod uses:
///
/// <code>
///   v0.107.1                          v0.110.1
///   Rng(uint seed, int counter)   ->  Rng(ulong seed)          // counter dropped
///   Rng(uint seed, string name)   ->  Rng(ulong seed, string name)
///   RunRngSet.Seed : uint         ->  RunRngSet.Seed : ulong
/// </code>
///
/// A .NET member reference carries its full signature, so <c>new Rng(...)</c> written against one
/// branch throws <see cref="MissingMethodException"/> on the other — and it throws when the method
/// that contains the call is JITted, so the stack points at the JIT hook rather than at the call.
/// The Workshop serves one payload to players on both branches, so every one of these goes through
/// here.
///
/// ★The seed VALUE differs between branches (different width, and v0.110 also rehashed the string
/// seed). That is fine: these are inputs to this mod's own deterministic derivations, which only
/// have to agree among peers on the SAME build — which multiplayer already guarantees.
///
/// ★Not in Sts2.ModKit on purpose: a new ModKit API is unusable until every installed sister mod
/// has been rebuilt, because an older bundled ModKit.dll wins first-wins resolution. Each mod keeps
/// its own copy of this file.
/// </summary>
internal static class RngCompat
{
    private const string WideCtorLost =
        "MegaCrit.Sts2.Core.Random.Rng has neither the 64-bit (v0.110+) nor the 32-bit (v0.107) " +
        "constructor this mod knows about; the game's RNG API changed again.";

    /// <summary><c>Rng(ulong)</c> on v0.110+, else <c>Rng(uint, int)</c>.</summary>
    private static readonly ConstructorInfo? SeedCtor;
    private static readonly bool SeedCtorIsWide;

    /// <summary><c>Rng(ulong, string)</c> on v0.110+, else <c>Rng(uint, string)</c>.</summary>
    private static readonly ConstructorInfo? NamedCtor;
    private static readonly bool NamedCtorIsWide;

    private static readonly PropertyInfo? SeedProperty;

    static RngCompat()
    {
        SeedCtor = typeof(Rng).GetConstructor(new[] { typeof(ulong) });
        SeedCtorIsWide = SeedCtor != null;
        SeedCtor ??= typeof(Rng).GetConstructor(new[] { typeof(uint), typeof(int) });

        NamedCtor = typeof(Rng).GetConstructor(new[] { typeof(ulong), typeof(string) });
        NamedCtorIsWide = NamedCtor != null;
        NamedCtor ??= typeof(Rng).GetConstructor(new[] { typeof(uint), typeof(string) });

        SeedProperty = typeof(RunRngSet).GetProperty("Seed");
    }

    /// <summary>An <see cref="Rng"/> seeded with <paramref name="seed"/>.</summary>
    public static Rng Create(ulong seed)
    {
        if (SeedCtor == null) throw new MissingMethodException(WideCtorLost);

        object[] args = SeedCtorIsWide
            ? new object[] { seed }
            : new object[] { unchecked((uint)seed), 0 };   // v0.107's counter argument

        return (Rng)SeedCtor.Invoke(args);
    }

    /// <summary>An <see cref="Rng"/> seeded with <paramref name="seed"/> and a stream name.</summary>
    public static Rng Create(ulong seed, string name)
    {
        if (NamedCtor == null) throw new MissingMethodException(WideCtorLost);

        object[] args = NamedCtorIsWide
            ? new object[] { seed, name }
            : new object[] { unchecked((uint)seed), name };

        return (Rng)NamedCtor.Invoke(args);
    }

    /// <summary>
    /// The run's seed, widened to 64 bits. Zero when there is no run — callers use this to key
    /// per-run caches, so "no run" and "seed 0" are equivalent for them.
    /// </summary>
    public static ulong SeedOf(RunRngSet? rngSet)
    {
        if (rngSet == null || SeedProperty == null) return 0uL;

        object? value = SeedProperty.GetValue(rngSet);
        return value == null ? 0uL : Convert.ToUInt64(value);
    }
}

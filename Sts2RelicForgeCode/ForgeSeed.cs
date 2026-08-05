using System;
using System.Reflection;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Reads the run seed off <see cref="RunRngSet" /> without binding to its return type at compile
/// time, and narrows it to the <c>uint</c> every forge derivation here expects.
///
/// Why this is not just <c>(uint)runState.Rng.Seed</c>: v0.110 widened
/// <c>RunRngSet.Seed</c> from <c>uint</c> to <c>ulong</c>. A static call compiles against exactly
/// one of those, so the DLL then throws <c>MissingMethodException: Method not found 'UInt32
/// ... get_Seed()'</c> on the *other* game branch — which is what took the forge gauge tint down
/// on the 110 beta. One published DLL has to run on both `public` and `public-beta`, so the getter
/// is bound once at runtime and adapted.
///
/// The delegate is built a single time in the static ctor, so per-call cost is one delegate
/// invocation — these sites sit on combat paths (per hit, per card draw) and must not reflect
/// per call.
///
/// Narrowing to the low 32 bits is deliberate and safe here: the value is only ever a seed for a
/// derived <see cref="MegaCrit.Sts2.Core.Random.Rng" />, never persisted or compared against a
/// game-side seed. Every peer narrows identically, so co-op determinism is preserved.
/// </summary>
internal static class ForgeSeed
{
    private static readonly Func<RunRngSet, uint> Read;

    static ForgeSeed()
    {
        Func<RunRngSet, uint> fallback = _ => 0u;
        try
        {
            var getter = typeof(RunRngSet).GetProperty("Seed", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
            if (getter == null)
            {
                Read = fallback;
                return;
            }

            if (getter.ReturnType == typeof(ulong))
            {
                var f = (Func<RunRngSet, ulong>)Delegate.CreateDelegate(typeof(Func<RunRngSet, ulong>), getter);
                Read = r => (uint)f(r);
            }
            else if (getter.ReturnType == typeof(uint))
            {
                Read = (Func<RunRngSet, uint>)Delegate.CreateDelegate(typeof(Func<RunRngSet, uint>), getter);
            }
            else
            {
                // Some other numeric width — pay the boxing cost rather than lose the seed entirely.
                Read = r => unchecked((uint)Convert.ToUInt64(getter.Invoke(r, null)));
            }
        }
        catch
        {
            Read = fallback;
        }
    }

    /// <summary>Run seed as a uint, or <paramref name="fallback" /> when the set is null.</summary>
    internal static uint Of(RunRngSet? rng, uint fallback = 0u) => rng == null ? fallback : Read(rng);
}

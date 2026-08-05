using System;
using System.Reflection;
using MegaCrit.Sts2.Core.Helpers;

namespace Sts2RelicForge;

/// <summary>
/// The string hash every forge derivation mixes into its per-relic seed.
///
/// v0.110 changed <c>StringHelper.GetDeterministicHashCode</c> from <c>int</c> to <c>ulong</c> —
/// a different algorithm producing different values — and kept the original as
/// <c>GetDeterministicHashCodeOld</c>. Binding to the new one would silently re-roll the prefix of
/// every relic in every in-flight run, because grades are re-derived from
/// <c>hash(relicId)</c> on run load (see RunLoadReforgePatch). So we deliberately prefer the OLD
/// function: on 0.110+ that is <c>GetDeterministicHashCodeOld</c>, and on earlier builds
/// <c>GetDeterministicHashCode</c> IS that same function. Both branches therefore produce
/// identical grades, and saved runs survive the update.
///
/// Bound reflectively for the same reason as [[ForgeSeed]]: one published DLL has to run on both
/// `public` and `public-beta`, and a static call would pin exactly one signature.
/// </summary>
internal static class ForgeHash
{
    private static readonly Func<string, int> Read;

    static ForgeHash()
    {
        var t = typeof(StringHelper);
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Static;
        var args = new[] { typeof(string) };

        try
        {
            // 0.110+: the pre-change algorithm, preserved under its new name.
            var old = t.GetMethod("GetDeterministicHashCodeOld", Flags, null, args, null);
            if (old != null && old.ReturnType == typeof(int))
            {
                Read = (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), old);
                return;
            }

            var cur = t.GetMethod("GetDeterministicHashCode", Flags, null, args, null);
            if (cur != null && cur.ReturnType == typeof(int))
            {
                // <=0.107: this is the same algorithm, under its original name.
                Read = (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), cur);
                return;
            }

            if (cur != null && cur.ReturnType == typeof(ulong))
            {
                // Neither name gave us the old algorithm. Narrow the new one rather than lose
                // hashing entirely — grades shift once, but stay deterministic and peer-consistent.
                var f = (Func<string, ulong>)Delegate.CreateDelegate(typeof(Func<string, ulong>), cur);
                MainFile.Logger.Warn($"[{MainFile.ModId}] StringHelper exposes only the post-0.110 hash; forged grades will differ from pre-0.110 runs.");
                Read = s => unchecked((int)f(s));
                return;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] resolving StringHelper hash failed: {ex.Message}");
        }

        // Last resort: a local copy of the original algorithm, so forging still works.
        Read = Local;
    }

    /// <summary>
    /// The game's pre-0.110 deterministic string hash, transcribed from the decompiled
    /// <c>StringHelper.GetDeterministicHashCodeOld</c> so the fallback yields identical grades.
    /// </summary>
    private static int Local(string str)
    {
        unchecked
        {
            int h1 = 352654597, h2 = h1;
            for (var i = 0; i < str.Length; i += 2)
            {
                h1 = ((h1 << 5) + h1) ^ str[i];
                if (i == str.Length - 1) break;
                h2 = ((h2 << 5) + h2) ^ str[i + 1];
            }
            return h1 + h2 * 1566083941;
        }
    }

    internal static int Of(string? str) => str == null ? 0 : Read(str);
}

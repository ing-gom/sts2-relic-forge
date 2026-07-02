using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.RestSite;

namespace Sts2RelicForge;

/// <summary>
/// A rest-site button caches its clickable/greyed state at creation and never re-reads the
/// option's IsEnabled. So when our reforge option disables itself mid-rest (a penalty rolled),
/// the button would stay lit and clickable. This postfix re-syncs each button to its option's
/// IsEnabled on every RefreshTextState (fired on focus / hover / selection), so a disabled option
/// greys out (its own greyscale shader) and stops responding — while heal/smith stay untouched
/// (they keep IsEnabled == true, so nothing changes for them).
/// </summary>
[HarmonyPatch(typeof(NRestSiteButton), "RefreshTextState")]
internal static class RestButtonSyncPatch
{
    private static readonly MethodInfo? ReloadM = AccessTools.Method(typeof(NRestSiteButton), "Reload");

    private static void Postfix(NRestSiteButton __instance)
    {
        try
        {
            var opt = __instance.Option;
            if (opt == null) return;
            bool block = !opt.IsEnabled;
            if (__instance._isUnclickable != block)   // only touch it when the state actually changed
            {
                __instance._isUnclickable = block;
                ReloadM?.Invoke(__instance, null);     // re-apply the greyscale tint for the new state
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] rest button sync failed: {e.Message}");
        }
    }
}

using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;

namespace Sts2RelicForge;

/// <summary>
/// Run-history view: while <see cref="NRelicHistory"/> rebuilds each past relic
/// (RelicModel.FromSerializable → NRelicBasicHolder), flag the reconstruction so
/// <see cref="ReforgeLoadCapturePatch"/> attaches a DISPLAY-ONLY forge record (prefix + curse) parsed
/// from the serialized summary. The existing tooltip patch then shows what was forged onto the relic
/// in that run. The flag is scoped to this call, so a normal run load (which re-derives the real
/// forge from the seed) is never affected. Finalizer resets the flag even if LoadRelics throws.
/// </summary>
[HarmonyPatch(typeof(NRelicHistory), "LoadRelics")]
internal static class HistoryForgeDisplayPatch
{
    private static void Prefix() => RelicForgeService.InHistoryLoad = true;
    private static void Finalizer() => RelicForgeService.InHistoryLoad = false;
}

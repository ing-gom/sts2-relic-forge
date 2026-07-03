using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace Sts2RelicForge;

/// <summary>
/// Shows a forged elite/boss's prefix on its nameplate — "Legendary Jaw Worm" tinted gold — the
/// on-screen mirror of a forged relic's tier-colored name. Unlike the relic title (a hover-tip
/// MegaLabel that can't be colored inline, so RelicTooltipPatch uses an invisible marker), the
/// creature nameplate is a plain MegaLabel we hold directly, so we can prepend the prefix AND set
/// its Modulate color outright.
///
/// Postfixes <c>NCreatureStateDisplay.RefreshValues()</c> — the sink that (re-)stamps the label
/// from <c>_creature.Name</c> on SetCreature / bounds change / every combat-state change — so the
/// prefix survives every re-stamp. Only enemies that actually forged (EnemyForge.PrefixOf != null,
/// which already implies elite/boss + active mechanism) are decorated; everyone else is untouched.
/// </summary>
[HarmonyPatch(typeof(NCreatureStateDisplay), "RefreshValues")]
internal static class EnemyNamePlatePatch
{
    private static void Postfix(NCreatureStateDisplay __instance)
    {
        var creature = __instance._creature;
        var label = __instance._nameplateLabel;
        if (creature == null || label == null) return;

        var tag = EnemyForge.TagOf(creature);
        if (tag == null) return;

        // RefreshValues already set the label to the bare name; re-stamp from _creature.Name (not
        // the label text) so we never double-prefix on repeated calls.
        label.SetTextAutoSize(tag.Prefix + " " + creature.Name);
        label.Modulate = Color.FromHtml(tag.Color.TrimStart('#'));
    }
}

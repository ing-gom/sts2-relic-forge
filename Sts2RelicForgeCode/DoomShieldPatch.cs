using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Powers;

namespace Sts2RelicForge;

/// <summary>
/// Undying (불사의, Necrobinder) — Doom (종말) no longer kills the player OUTRIGHT; it DEFERS the death.
///
/// Doom's kill is a distinct path from HP-damage death. DoomPower.BeforeSideTurnEnd fires at the
/// owner's side-turn end when <c>CurrentHp &lt;= Amount</c>, and both it and EndOfDays funnel every doom
/// kill through the single public choke <see cref="DoomPower.DoomKill"/>(list). Here we strip the
/// Undying player's OWN body out of that list, so the turn-end Doom death never lands — AND we ARM a
/// reprieve on that player (<see cref="CharAffix.ArmDoomReprieve"/>). The reprieve is spent in
/// <see cref="CharAffix.OnPlayerHpLost"/>: once armed, the next HP the player loses while still doomed
/// finishes them. So it is 죽음 유예, not immunity — 종말로 즉사하지 않고, 그 뒤 피가 감소하면 죽는다.
///
/// Filtering DoomKill (not GetDoomedCreatures) avoids BeforeSideTurnEnd's <c>doomedCreatures.First()</c>
/// throwing on an emptied list, and still lets a co-doomed summon die — the summon stays in the list.
///
/// Co-op: the strip + arm is a pure, RNG-free read of replicated relic state (RelicForgeService record)
/// and runs inside the lockstep combat resolution, so host and client spare/arm the SAME player — no
/// desync, no new net wire.
/// </summary>
[HarmonyPatch(typeof(DoomPower), nameof(DoomPower.DoomKill))]
internal static class DoomShieldPatch
{
    private static void Prefix(ref IReadOnlyList<Creature> creatures)
    {
        try
        {
            if (!CharAffix.Enabled || creatures == null || creatures.Count == 0) return;
            List<Creature>? kept = null;
            for (int i = 0; i < creatures.Count; i++)
            {
                Creature c = creatures[i];
                Player? shielded = ShieldedPlayerOf(c);
                if (shielded != null)
                {
                    CharAffix.ArmDoomReprieve(shielded);              // dodged Doom at turn end → borrowed time
                    (kept ??= new List<Creature>(creatures)).Remove(c);
                }
            }
            if (kept != null) creatures = kept;   // may become empty → DoomKill no-ops (nobody died)
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] Undying doom-shield failed: {e.Message}"); }
    }

    /// <summary>The PLAYER behind a creature that is its OWN body (not a summon) and carries Undying, or
    /// null. Restricting to <c>p.Creature</c> means a doomed summon is never shielded — Undying spares
    /// the player, not the Osty.</summary>
    private static Player? ShieldedPlayerOf(Creature? c)
    {
        Player? p = c?.Player;
        if (p == null || c != p.Creature) return null;
        foreach (var _ in CharAffix.Owned(p, "Undying")) return p;
        return null;
    }
}

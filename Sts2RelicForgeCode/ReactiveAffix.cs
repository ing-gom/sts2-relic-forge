using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace Sts2RelicForge;

/// <summary>
/// Reaction engine for the reactive prefix family (see REACTIVE_PREFIXES.md):
///   공명의 / Resonant    — gain Str/Dex -> gain +1 more of it
///   완강한 / Obstinate   — an enemy-applied Str/Dex loss becomes a gain
///   방전의 / Discharging  — each bonus Energy gained deals damage to all enemies
///
/// The guard + effect logic here is complete and reused by all three. The one piece that needs the
/// game/ModKit assemblies is the EVENT INTERCEPTION that calls these handlers (and the damage
/// command for 방전의) — see the big comment block at the bottom. Until those Harmony patches are
/// wired, <see cref="Enabled"/> stays false and the prefixes carry Weight 0, so nothing half-works
/// in a real game. Everything routes through PowerCmd / damage commands, which are networked +
/// deterministic, so co-op stays consistent with no extra sync.
/// </summary>
internal static class ReactiveAffix
{
    /// <summary>
    /// Master gate. Flip to true ONCE the event-interception patches below are implemented and
    /// co-op tested. Kept <c>static readonly</c> (not <c>const</c>) so the handlers compile without
    /// unreachable-code warnings while the wiring is still stubbed.
    /// </summary>
    internal static readonly bool Enabled = false;

    // 공명의 hard cap: at most this many amplifier triggers per relic per turn. This is the real
    // infinite-loop backstop — even if the +1 we apply re-enters the gain event, a relic amplifies
    // at most PerTurnCap times per turn, so it always terminates. The reentrancy flag below is only
    // an optimization so the bonus itself doesn't eat into the cap.
    private const int PerTurnCap = 3;

    // Reentrancy optimization: while we are applying an amplifier bonus, ignore the gain event it
    // itself raises (that +1 must not be amplified again). Thread-static to stay isolated per turn
    // execution. NOTE: PowerCmd.Apply is async; whether the gain event fires synchronously inside it
    // determines how much this catches — but PerTurnCap guarantees termination regardless. Validate
    // the exact timing locally once the interception patch is wired.
    [ThreadStatic] private static bool _applyingBonus;

    // Per-relic [lastTurn, countThisTurn], so the cap self-resets each turn with no separate hook.
    private static readonly ConditionalWeakTable<RelicModel, int[]> AmpTurn = new();

    /// <summary>
    /// 공명의 / Resonant. The player gained a positive amount of Strength or Dexterity. For each
    /// owned Resonant relic (under its per-turn cap), grant +1 more of that same power.
    /// </summary>
    public static void OnPlayerGainPower(PlayerChoiceContext ctx, Player player, bool isDexterity, int delta)
    {
        if (!Enabled || _applyingBonus || delta <= 0) return;
        try
        {
            int turn = player.PlayerCombatState?.TurnNumber ?? 0;
            foreach (var relic in ReactiveRelics(player, p => p.GainAmplify))
            {
                var box = AmpTurn.GetValue(relic, _ => new int[2]);
                if (box[0] != turn) { box[0] = turn; box[1] = 0; } // new turn -> reset the count
                if (box[1] >= PerTurnCap) continue;
                box[1]++;

                _applyingBonus = true;
                try { ApplyPower(ctx, player, isDexterity, 1); }
                finally { _applyingBonus = false; }
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] Resonant apply failed: {e.Message}"); }
    }

    /// <summary>
    /// 완강한 / Obstinate. An enemy tried to reduce the player's Strength or Dexterity (delta &lt; 0).
    /// Grant +|delta| of that power instead. The caller's interception patch must ALSO cancel the
    /// original reduction (skip the original apply, or add the amount back) — this only grants the
    /// inverted gain. Self-inflicted reductions (Flex-style) must NOT reach here (enemy applier only).
    /// </summary>
    public static void OnEnemyReducePower(PlayerChoiceContext ctx, Player player, bool isDexterity, int delta)
    {
        if (!Enabled || delta >= 0) return;
        if (!ReactiveRelics(player, p => p.LossInvert).Any()) return;
        try
        {
            // Flag as a bonus so this granted gain doesn't also trip 공명의 on a relic that has both.
            _applyingBonus = true;
            try { ApplyPower(ctx, player, isDexterity, -delta); }
            finally { _applyingBonus = false; }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] Obstinate invert failed: {e.Message}"); }
    }

    /// <summary>
    /// 방전의 / Discharging. The player gained bonus Energy (beyond the turn refill). Deal the
    /// relic's EnergyDischarge damage to every enemy. Multiple such relics don't stack the count —
    /// the strongest applies (they'd re-trigger on the same event otherwise).
    /// </summary>
    public static void OnPlayerGainBonusEnergy(PlayerChoiceContext ctx, Player player, ICombatState cs, int amount)
    {
        if (!Enabled || amount <= 0 || cs == null) return;
        try
        {
            int dmg = 0;
            foreach (var relic in ReactiveRelics(player, p => p.EnergyDischarge > 0))
            {
                var pfx = PrefixTable.ByName(RelicForgeService.RecordFor(relic)?.Prefix ?? "");
                if (pfx != null) dmg = Math.Max(dmg, pfx.EnergyDischarge);
            }
            if (dmg <= 0) return;

            foreach (var enemy in cs.HittableEnemies.ToList())
                DealDamage(ctx, player, enemy, dmg);
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] Discharging failed: {e.Message}"); }
    }

    /// <summary>Owned, forged relics whose rolled prefix matches <paramref name="pick"/>.</summary>
    private static IEnumerable<RelicModel> ReactiveRelics(Player player, Func<Prefix, bool> pick)
    {
        foreach (var relic in new List<RelicModel>(player.Relics))
        {
            var rec = RelicForgeService.RecordFor(relic);
            if (rec == null || rec.Prefix.Length == 0) continue;
            var pfx = PrefixTable.ByName(rec.Prefix);
            if (pfx != null && pick(pfx)) yield return relic;
        }
    }

    /// <summary>Grant the player +<paramref name="amount"/> Strength or Dexterity (self-sourced, so
    /// it reads as a buff, not an enemy debuff). Mirrors the PowerCmd usage in ForgeCombatAffixPatch.</summary>
    private static void ApplyPower(PlayerChoiceContext ctx, Player player, bool isDexterity, int amount)
    {
        var creature = player.Creature;
        if (creature == null || amount == 0) return;
        if (isDexterity)
            TaskHelper.RunSafely(PowerCmd.Apply<DexterityPower>(ctx, creature, amount, creature, null));
        else
            TaskHelper.RunSafely(PowerCmd.Apply<StrengthPower>(ctx, creature, amount, creature, null));
    }

    /// <summary>
    /// Deal a flat hit to one enemy for 방전의.
    /// TODO(hook): wire the game's damage command here. Mirror however the game deals a fixed,
    /// source-attributed hit (the damage analogue of PowerCmd.Apply / CreatureCmd.GainBlock used in
    /// EnemyForge). Signature to confirm against the assemblies — until then this only logs, so the
    /// prefix is inert even if Enabled/Weight are turned on prematurely.
    /// </summary>
    private static void DealDamage(PlayerChoiceContext ctx, Player player, Creature enemy, int amount)
    {
        // e.g. TaskHelper.RunSafely(DamageCmd.Deal(ctx, enemy, amount, player.Creature, ...));
        MainFile.Logger.Info($"[{MainFile.ModId}] 방전의: would deal {amount} to {enemy?.Id} (damage cmd TODO)");
    }

    // ------------------------------------------------------------------------------------------
    // TODO(hook): EVENT INTERCEPTION — the piece that needs the game/ModKit assemblies.
    //
    // Add Harmony patches that call the handlers above. Each needs an event point the mod does not
    // yet hook (the existing ForgeCombatAffixPatch uses Hook.AfterPlayerTurnStart, which is turn-
    // scoped, not per power/energy event). Confirm the exact target methods, then:
    //
    //   * Player gains a power  -> if target is the player and the power is Strength/Dexterity with
    //     delta > 0: ReactiveAffix.OnPlayerGainPower(ctx, player, isDex, delta).
    //       - 공명의 reads this.
    //   * Same interception, delta < 0 AND applier is an ENEMY: ReactiveAffix.OnEnemyReducePower(...)
    //     and CANCEL the original reduction (skip original / add it back).
    //       - 완강한 reads this. Self-inflicted (Flex) reductions must be left alone.
    //   * Player gains bonus energy (beyond the turn refill):
    //     ReactiveAffix.OnPlayerGainBonusEnergy(ctx, player, combatState, amount).
    //       - 방전의 reads this. Also fill in DealDamage above.
    //
    // The PlayerChoiceContext (ctx) should come from the hook/patched method, same as
    // ForgeCombatAffixPatch's Postfix receives it. Once wired and co-op tested, set Enabled = true
    // and give the three prefixes a real Weight in PrefixTable so they enter the roll pool.
    // ------------------------------------------------------------------------------------------
}

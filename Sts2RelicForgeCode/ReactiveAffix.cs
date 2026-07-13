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
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2RelicForge;

/// <summary>
/// Reaction engine for the reactive prefix family (see REACTIVE_PREFIXES.md):
///   공명의 / Resonant    — gain Str/Dex -> gain +1 more of it (up to 3/turn, always cursed)
///   완강한 / Obstinate   — an enemy-applied Str/Dex loss becomes a gain
///   방전의 / Discharging  — each bonus Energy gained deals damage to all enemies
///
/// The event interception lives in <see cref="ReactiveAffixPatches"/> (Harmony patches on the
/// game's power/energy Hook methods), which call the handlers here. Everything routes through
/// PowerCmd / CreatureCmd (networked + deterministic), so co-op stays consistent with no extra sync —
/// same property the passive forge already relies on. <see cref="Enabled"/> is the master gate.
/// </summary>
internal static class ReactiveAffix
{
    /// <summary>Master gate for the whole reactive family. Off = handlers no-op (and the prefixes
    /// carry Weight 0), on = live. Kept <c>static readonly</c> so branches compile without warnings.</summary>
    internal static readonly bool Enabled = true;

    // 공명의 hard cap: at most this many amplifier triggers per relic per turn. The real backstop —
    // even if an echo slips past the suppress counter, a relic amplifies at most PerTurnCap/turn.
    private const int PerTurnCap = 3;

    // Suppress-echo counter: the +1 we grant echoes back into OnPlayerGainPower as its own gain
    // event. Increment before granting; the echo decrements and is ignored, so one real gain yields
    // exactly one +1. Plain static (combat runs single-threaded through the action executor); reset
    // each turn so a missed echo can't leak into the next turn.
    private static int _suppressSelfGains;

    // Per-relic [lastTurn, countThisTurn], so the cap self-resets each turn with no separate hook.
    private static readonly ConditionalWeakTable<RelicModel, int[]> AmpTurn = new();

    // Most-recent real combat PlayerChoiceContext, captured from hooks that carry one. The energy
    // hook (Hook.ModifyEnergyGain) provides none, so 방전의's damage borrows this. Cleared each turn.
    private static PlayerChoiceContext? _lastCtx;

    /// <summary>Remember the latest real hook context (for 방전의, whose hook carries none).</summary>
    public static void CaptureCtx(PlayerChoiceContext? ctx) { if (ctx != null) _lastCtx = ctx; }

    /// <summary>Per-turn reset: clear the echo-suppress counter so a missed echo can't leak.</summary>
    public static void ResetTurn() { _suppressSelfGains = 0; }

    /// <summary>
    /// 공명의 / Resonant. The player gained a positive amount of Strength or Dexterity. For each
    /// owned Resonant relic (under its per-turn cap), grant +1 more of that same power.
    /// </summary>
    public static async Task OnPlayerGainPower(PlayerChoiceContext ctx, Player player, bool isDexterity, int delta)
    {
        if (!Enabled || delta <= 0) return;
        if (_suppressSelfGains > 0) { _suppressSelfGains--; return; } // the echo of our own +1 grant
        try
        {
            int turn = player.PlayerCombatState?.TurnNumber ?? 0;
            foreach (var relic in ReactiveRelics(player, p => p.GainAmplify))
            {
                var box = AmpTurn.GetValue(relic, _ => new int[2]);
                if (box[0] != turn) { box[0] = turn; box[1] = 0; } // new turn -> reset the count
                if (box[1] >= PerTurnCap) continue;
                box[1]++;

                relic.Flash();
                _suppressSelfGains++;                 // swallow the echo this grant will raise (set BEFORE the await)
                // AWAITED in-order (not detached): this rides the mid-command AfterPowerAmountChanged hook, so a
                // fire-and-forget PowerCmd.Apply would interleave and desync co-op (the Cursefed class). The
                // patch chains us onto the hook's ref Task __result; awaiting here keeps every peer in lockstep.
                await ApplyPower(ctx, player, isDexterity, 1);
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] Resonant apply failed: {e.Message}"); }
    }

    /// <summary>
    /// 완강한 / Obstinate. True when an ENEMY-applied reduction of the player's Strength/Dexterity
    /// (<paramref name="amount"/> &lt; 0) should be inverted into a gain. The caller (the
    /// ModifyPowerAmountReceived patch) then rewrites the applied amount to its positive — a single
    /// step, no separate apply, no double event. Self-inflicted reductions (Flex-style) are excluded
    /// by the enemy-applier check, so the give-then-take exploit never happens.
    /// </summary>
    public static bool ShouldInvertEnemyLoss(PowerModel power, Creature? target, decimal amount, Creature? applier)
    {
        if (!Enabled || amount >= 0m) return false;
        if (target?.Side != CombatSide.Player || applier?.Side != CombatSide.Enemy) return false;
        if (!(power is StrengthPower || power is DexterityPower)) return false;
        var player = target.Player;
        return player != null && ReactiveRelics(player, p => p.LossInvert).Any();
    }

    /// <summary>
    /// 방전의 / Discharging. The player gained bonus Energy (beyond the turn refill). Deal the
    /// relic's EnergyDischarge damage to every enemy. Multiple such relics don't stack the count —
    /// the strongest applies. Borrows the last captured context (the energy hook carries none).
    /// </summary>
    public static async Task OnPlayerGainBonusEnergy(Player player, ICombatState cs, int amount)
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

            var ctx = _lastCtx;
            if (ctx == null) { MainFile.Logger.Info($"[{MainFile.ModId}] 방전의: no combat context captured yet — skipping this discharge."); return; }

            // AWAITED in-order per enemy (the caller chains this onto PlayerCmd.GainEnergy's Task): a detached
            // batch of damage commands would interleave with the rest of the energy-gain flow and desync co-op.
            foreach (var enemy in cs.HittableEnemies.ToList())
                await DealDamage(ctx, player, enemy, dmg);
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] Discharging failed: {e.Message}"); }
    }

    /// <summary>Owned, forged relics whose rolled prefix matches <paramref name="pick"/>.</summary>
    private static IEnumerable<RelicModel> ReactiveRelics(Player player, Func<Prefix, bool> pick)
    {
        foreach (var relic in new List<RelicModel>(player.Relics))
        {
            if (RelicForgeService.IsForgeEffectSuppressed(relic)) continue;   // dead relic (spent/disabled/saturated) — no reactive effect
            var rec = RelicForgeService.RecordFor(relic);
            if (rec == null || rec.Prefix.Length == 0) continue;
            var pfx = PrefixTable.ByName(rec.Prefix);
            if (pfx != null && pick(pfx)) yield return relic;
        }
    }

    /// <summary>Grant the player +<paramref name="amount"/> Strength or Dexterity (self-sourced, so
    /// it reads as a buff, not an enemy debuff). Mirrors the PowerCmd usage in ForgeCombatAffixPatch.</summary>
    private static Task ApplyPower(PlayerChoiceContext ctx, Player player, bool isDexterity, int amount)
    {
        var creature = player.Creature;
        if (creature == null || amount == 0) return Task.CompletedTask;
        return isDexterity
            ? PowerCmd.Apply<DexterityPower>(ctx, creature, amount, creature, null)
            : PowerCmd.Apply<StrengthPower>(ctx, creature, amount, creature, null);
    }

    /// <summary>Deal a flat, player-sourced hit to one enemy for 방전의. Unblockable + Unpowered so
    /// it's a fixed ping (Block and Strength-scaling are skipped; Vulnerable on the target still
    /// applies — there is no ValueProp flag to suppress it, which is fine for a relic ping).</summary>
    private static Task DealDamage(PlayerChoiceContext ctx, Player player, Creature enemy, int amount)
    {
        if (ctx == null || enemy == null || player.Creature == null || amount <= 0) return Task.CompletedTask;
        return CreatureCmd.Damage(ctx, enemy, amount,
            ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.SkipHurtAnim, player.Creature, null);
    }
}

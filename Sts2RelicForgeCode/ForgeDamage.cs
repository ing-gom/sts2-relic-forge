using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2RelicForge;

/// <summary>
/// Single-target damage with an explicit dealer, bound to whichever <c>CreatureCmd.Damage</c>
/// overload this game build actually exposes.
///
/// <c>CreatureCmd.Damage</c> keeps growing tail parameters (see the same note on
/// LethalSummonDamagePatch). Through 0.107 the dealer-carrying shape was
/// <c>(ctx, target, decimal, ValueProp, Creature? dealer, CardModel? cardSource)</c>; 0.110 appended
/// <c>CardPlay? cardPlay</c> and re-purposed the 6-arg shape so that slot 5 is now a
/// <c>CardModel?</c>. A direct call therefore compiles against exactly one branch and fails on the
/// other — statically (wrong argument type) or at runtime (MissingMethod).
///
/// Resolving once here keeps both branches working from one published DLL and means the next tail
/// parameter costs one entry in <see cref="Candidates" /> instead of a hunt through combat code.
/// </summary>
internal static class ForgeDamage
{
    /// <summary>Dealer-carrying single-target shapes, richest first. Slot 5 must be the dealer.</summary>
    private static readonly Type[][] Candidates =
    {
        // 0.110+: … Creature? dealer, CardModel? cardSource, CardPlay? cardPlay
        new[] { typeof(PlayerChoiceContext), typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel), typeof(CardPlay) },
        // <=0.107: … Creature? dealer, CardModel? cardSource
        new[] { typeof(PlayerChoiceContext), typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel) },
        // present on both: … Creature dealer
        new[] { typeof(PlayerChoiceContext), typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(Creature) },
    };

    private static readonly MethodInfo? Bound;
    private static readonly int ArgCount;

    static ForgeDamage()
    {
        (Bound, ArgCount) = Resolve(Candidates, "single-target");
        (BoundBatch, BatchArgCount) = Resolve(BatchCandidates, "batch");

        // Hook.ModifyDamage has no overloads — only a shifting parameter count — so match by name.
        BoundModify = AccessTools.GetDeclaredMethods(typeof(MegaCrit.Sts2.Core.Hooks.Hook))
            .Find(m => m.Name == nameof(MegaCrit.Sts2.Core.Hooks.Hook.ModifyDamage));
        ModifyArgCount = BoundModify?.GetParameters().Length ?? 0;
        if (BoundModify == null)
            MainFile.Logger.Warn($"[{MainFile.ModId}] Hook.ModifyDamage not found — damage-modifier probes will return the unmodified value.");
    }

    private static (MethodInfo?, int) Resolve(Type[][] candidates, string label)
    {
        foreach (var sig in candidates)
        {
            var m = AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Damage), sig);
            if (m != null) return (m, sig.Length);
        }
        MainFile.Logger.Warn($"[{MainFile.ModId}] no dealer-carrying {label} CreatureCmd.Damage overload on this game version — that affix damage path is disabled.");
        return (null, 0);
    }

    /// <summary>
    /// Deal <paramref name="amount" /> to <paramref name="target" /> attributed to
    /// <paramref name="dealer" />. Returns the game's task so callers keep awaiting it in order
    /// (a detached self-debuff races the remaining hits and desyncs co-op).
    /// </summary>
    internal static Task Deal(PlayerChoiceContext ctx, Creature target, decimal amount, ValueProp props, Creature? dealer)
    {
        if (Bound == null || ctx == null || target == null || amount <= 0) return Task.CompletedTask;
        try
        {
            var args = new object?[ArgCount];
            args[0] = ctx; args[1] = target; args[2] = amount; args[3] = props; args[4] = dealer;
            // Slots 5+ (cardSource, cardPlay) stay null — affix damage has no originating card.
            return Bound.Invoke(null, args) as Task ?? Task.CompletedTask;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] affix damage failed: {ex.InnerException?.Message ?? ex.Message}");
            return Task.CompletedTask;
        }
    }

    /// <summary>Batch shapes, richest first. Slot 5 is the dealer, as in <see cref="Candidates" />.</summary>
    private static readonly Type[][] BatchCandidates =
    {
        new[] { typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel), typeof(CardPlay) },
        new[] { typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel) },
        new[] { typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature) },
    };

    private static readonly MethodInfo? BoundBatch;
    private static readonly int BatchArgCount;

    /// <summary>Same shim, multi-target. 0.110 dropped the 6-arg batch shape entirely, so a direct
    /// call cannot be written that compiles on both branches.</summary>
    internal static Task DealAll(PlayerChoiceContext ctx, IEnumerable<Creature> targets, decimal amount, ValueProp props, Creature? dealer)
    {
        if (BoundBatch == null || ctx == null || targets == null || amount <= 0) return Task.CompletedTask;
        try
        {
            var args = new object?[BatchArgCount];
            args[0] = ctx; args[1] = targets; args[2] = amount; args[3] = props; args[4] = dealer;
            return BoundBatch.Invoke(null, args) as Task ?? Task.CompletedTask;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] batch affix damage failed: {ex.InnerException?.Message ?? ex.Message}");
            return Task.CompletedTask;
        }
    }

    private static readonly MethodInfo? BoundModify;
    private static readonly int ModifyArgCount;

    /// <summary>
    /// <c>Hook.ModifyDamage</c> as a pure calculation (what a relic/power would do to this hit).
    /// 0.110 inserted a <c>CardPlay?</c> before the hook-type argument, so the parameter list is
    /// 10 wide on &lt;=0.107 and 11 on 0.110+; the trailing <c>out modifiers</c> moves with it.
    /// </summary>
    internal static decimal ModifyDamage(IRunState runState, ICombatState? combatState, Creature? target, Creature? dealer, decimal damage, ValueProp props)
    {
        if (BoundModify == null) return damage;
        try
        {
            var args = new object?[ModifyArgCount];
            args[0] = runState; args[1] = combatState; args[2] = target; args[3] = dealer;
            args[4] = damage; args[5] = props; args[6] = null;                 // cardSource
            var i = 7;
            if (ModifyArgCount == 11) args[i++] = null;                        // 0.110+ cardPlay
            args[i++] = ModifyDamageHookType.All;
            args[i++] = CardPreviewMode.None;
            args[i] = null;                                                    // out modifiers
            return (decimal)(BoundModify.Invoke(null, args) ?? damage);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] ModifyDamage probe failed: {ex.InnerException?.Message ?? ex.Message}");
            return damage;
        }
    }
}

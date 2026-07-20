using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2RelicForge;

/// <summary>
/// Engine for the CHARACTER-GATED prefix family (see PrefixTable's CharAffix block + CharAffixPatches).
/// Each prefix rolls only when the owner plays a specific character (RequiredCharacter) and reacts to
/// that character's signature mechanic:
///   Universal   — Quenched (vigor→strength; vigor lives on Regent/shared, so no char gate)
///   Ironclad    — Cindered (block on exhaust), Bloodforged (strength on first HP loss), Gouging
///                 (vuln +1), Retaliating (vigor from HP lost), Mirrored (enemy copies strength),
///                 Lingering (exhaust dodged to discard), Karmic (vuln reflects)
///   Silent      — Envenomed (poison +1), Flurrying (shiv on shiv), Cycling (draw on discard),
///                 Retrieving (shiv returns), Toxic (self-poison), Slippery (turn-start discard)
///   Defect      — Focused (first focus +1), Amplified (bonus channel), Supercharged (focus while full),
///                 Preheated (combat-start orb), Shorted (focus -1), Sealed (slot -1), Unstable (orb morphs)
///   Necrobinder — Necromantic (block on summon), Dooming (doom +1), Bonebound (summon each turn),
///                 Vengeful (summon from HP lost), Empathic (block from summon's pain), Sacrificial
///                 (self-debuff), Famished (osty shrinks on summon-less turns)
///   Regent      — Starlit (star each turn), Reforging (star on forge), Regal (star refund), Tributary
///                 (draw on forge), Bountiful (bonus star), Bankrupt (spend cost), Prodigal (star tax),
///                 Tarnished (turn-end star loss)
///
/// The Harmony patches in <see cref="CharAffixPatches"/> ride the game's per-event Hooks and call the
/// handlers here. Every mutation routes through a networked/deterministic command (PowerCmd / OrbCmd /
/// OstyCmd / CreatureCmd / CardPileCmd / PlayerCmd) — the same idiom the reactive/combat affixes already
/// rely on for co-op consistency. Random decisions are drawn from a seed-deterministic Rng (runSeed +
/// turn + relic + per-turn occurrence) so every client agrees without extra sync, exactly like
/// <see cref="ForgeCombatAffixPatch"/>.
/// </summary>
internal static class CharAffix
{
    /// <summary>Master gate for the whole character-affix family (mirrors ReactiveAffix.Enabled).</summary>
    internal static readonly bool Enabled = true;

    // ---- Character identity ----

    /// <summary>The owning player's CharacterModel Id.Entry (e.g. "SILENT"), or null if unavailable.
    /// Used both for the roll gate and — implicitly — at fire time (a char prefix only exists on a
    /// relic the matching character owns, so no extra fire-time character check is needed).</summary>
    public static string? TitleOf(Player? player)
    {
        try { return player?.Character?.Id.Entry; }
        catch { return null; }
    }

    /// <summary>The LOCAL player's character title, for forge PREVIEW of an unowned (offered) relic —
    /// the previewer is the one who will obtain it, so their character is the right pool. Single-player
    /// has exactly one player; in co-op the actual obtain re-derives per owner, so a preview mismatch
    /// (rare) self-corrects on pickup.</summary>
    public static string? LocalTitle()
    {
        try
        {
            var players = RunManager.Instance?.State?.Players;
            return players != null && players.Count > 0 ? TitleOf(players[0]) : null;
        }
        catch { return null; }
    }

    // ---- Per-turn / per-combat state ----

    // Per-relic [turn, occurrence] so deterministic rolls vary within a turn yet reproduce on all
    // clients. Reset lazily when the turn changes.
    private static readonly ConditionalWeakTable<RelicModel, int[]> Occ = new();

    // 집속의 / Focused: per-relic [combatEpoch, used] — grant once per combat. Supercharged: per-relic
    // [granted]. Sacrificial: per-relic [lastFiredTurn]. All lazily reset against the epoch/turn.
    private static readonly ConditionalWeakTable<RelicModel, int[]> FocusOnce = new();
    private static readonly ConditionalWeakTable<RelicModel, int[]> Charged = new();
    private static readonly ConditionalWeakTable<RelicModel, int[]> SacTurn = new();

    // 양극의 / Polarized: per-relic [epochWhenArmed] — armed at turn end (queue all-empty / all-full),
    // consumed at the next turn start. Keyed on the combat epoch so a flag armed on a combat's final
    // turn can never leak into the next combat's turn 1.
    private static readonly ConditionalWeakTable<RelicModel, int[]> PolarizedArmed = new();

    // 파산한 / Bankrupt: per-relic [epoch, accumulatedStarsSpent] — the debt meter. Resets per combat.
    private static readonly ConditionalWeakTable<RelicModel, int[]> BankruptDebt = new();

    // RelicModel.InvokeDisplayAmountChanged is protected — the relic icon's amount label redraws off
    // that event, so we raise it via reflection whenever the debt meter moves (counter overlay in
    // CompanionCounterPatch reads BankruptRemaining).
    private static readonly System.Reflection.MethodInfo? InvokeDisplayChanged =
        typeof(RelicModel).GetMethod("InvokeDisplayAmountChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static void NotifyCounter(RelicModel relic)
    {
        try { InvokeDisplayChanged?.Invoke(relic, null); } catch { /* display only */ }
    }

    /// <summary>Stars still to spend before Bankrupt's next trigger on this relic, or -1 when the
    /// relic doesn't carry the Bankrupt prefix. Outside combat (or before the first spend of a
    /// combat) this reads as the full threshold — the meter always starts full.</summary>
    public static int BankruptRemaining(RelicModel relic, string prefix)
    {
        if (!string.Equals(prefix, "Bankrupt", StringComparison.Ordinal)) return -1;
        var debt = BankruptDebt.GetValue(relic, _ => new int[2]);
        if (debt[0] != _combatEpoch) return BankruptStarsPerTrigger;
        return BankruptStarsPerTrigger - debt[1];
    }

    // Bumped once per combat (BeforeCombatStart) so 집속의 resets and Supercharged clears its grant.
    private static int _combatEpoch;

    // 증폭의 echo suppressor: the extra orb we channel raises AfterOrbChanneled again; swallow that one
    // echo so a single channel yields at most one bonus. Reset each turn (mirrors ReactiveAffix).
    private static int _suppressChannel;

    // 풍요의 echo suppressor: Bountiful's own bonus GainStars re-raises AfterStarsGained; swallow
    // exactly that echo (the _suppressChannel idiom). Reset each turn / combat.
    private static int _suppressStarGain;

    // Depth of OrbCmd.Channel calls in flight (see ChannelBracketPatch). Channeling into a FULL queue
    // auto-evokes the oldest orb FIRST and enqueues the new one after (decompile-verified), so the
    // evoke hook sees a transiently non-full queue mid-channel. Reconcile skips those transients; the
    // bracket patch reconciles once when the channel op settles. Nest-safe for Amplified's bonus
    // channel — only the outermost completion (depth back to 0) reconciles.
    private static int _channelDepth;

    /// <summary>OrbCmd.Channel entered (bracket patch prefix).</summary>
    public static void BeginChannel()
    {
        _channelDepth++;
    }

    /// <summary>OrbCmd.Channel settled (bracket patch, after the wrapped task completes).</summary>
    public static void EndChannel()
    {
        if (_channelDepth > 0) _channelDepth--;
    }

    /// <summary>New-combat reset (Hook.BeforeCombatStart): bump the epoch so per-combat state re-arms.</summary>
    public static void OnCombatStart() { _combatEpoch++; _suppressChannel = 0; _channelDepth = 0; _suppressStarGain = 0; }

    /// <summary>Per-turn reset (Hook.AfterPlayerTurnStart): clear the echo suppressors.</summary>
    public static void ResetTurn() { _suppressChannel = 0; _suppressStarGain = 0; }

    private static int TurnOf(Player p) => p.PlayerCombatState?.TurnNumber ?? 0;

    /// <summary>Deterministic float in [0,1) for a per-(relic,turn) occurrence — same on every client.</summary>
    internal static float Roll(Player player, RelicModel relic, int turn)
    {
        var box = Occ.GetValue(relic, _ => new int[2]);
        if (box[0] != turn) { box[0] = turn; box[1] = 0; }
        int occ = box[1]++;
        uint seed = player.RunState.Rng.Seed;
        var rng = new Rng((uint)((int)seed + turn * 24107 + StringHelper.GetDeterministicHashCode(relic.Id.Entry) + occ * 6151));
        float raw = rng.NextFloat();
        float raw2 = rng.NextFloat();   // a second independent roll — used only by Empowering (advantage)
        // Meta auras ([[MetaAffix]]): AdjustRoll transforms the roll so every `Roll() < chance` site fires at the
        // adjusted rate — Catalytic halves it (→ double), Empowering takes min(raw,raw2) (→ a genuine second
        // attempt), else Priming subtracts its flat bonus (→ +b). One place covers all char-affix procs (and
        // Searing). Deterministic (raw2 is a fixed draw from the same local rng) → co-op-safe.
        return MetaAffix.AdjustRoll(raw, raw2, player);
    }

    /// <summary>Owned, forged relics of <paramref name="player"/> whose rolled prefix name matches.</summary>
    internal static IEnumerable<RelicModel> Owned(Player player, string prefixName)
    {
        foreach (var relic in new List<RelicModel>(player.Relics))
        {
            var rec = RelicForgeService.RecordFor(relic);
            if (rec != null && string.Equals(rec.Prefix, prefixName, StringComparison.Ordinal))
                yield return relic;
        }
    }

    /// <summary>Owned, forged relics of <paramref name="player"/> carrying the given CURSE identity — the
    /// mirror of <see cref="Owned"/> for the curse slot. Penalty affixes (Dulled/Levied/Sacrificial/
    /// Bankrupt/…) were re-homed onto rec.SelfCurse, so their patches match here instead of on rec.Prefix.
    /// (Do NOT repoint <see cref="Owned"/> itself — beneficial char prefixes still live in the prefix slot.)</summary>
    internal static IEnumerable<RelicModel> OwnedByCurse(Player player, string curseKey)
    {
        foreach (var relic in new List<RelicModel>(player.Relics))
        {
            var rec = RelicForgeService.RecordFor(relic);
            if (rec != null && string.Equals(rec.SelfCurse, curseKey, StringComparison.Ordinal))
                yield return relic;
        }
    }

    private static void Fire(RelicModel relic, Task effect)
    {
        relic.Flash();
        TaskHelper.RunSafely(effect);
    }

    /// <summary>AWAITED sibling of <see cref="Fire"/> for handlers that ride a MID-COMMAND hook
    /// (AfterPowerAmountChanged / AfterOrbEvoked / AfterDamageReceived): the effect must run IN-ORDER
    /// inside the hook's awaited Task, not detached — a fire-and-forget command interleaves
    /// non-deterministically with the rest of the command and desyncs co-op lockstep (the Cursefed
    /// class). The caller chains this onto the hook's <c>ref Task __result</c>. Swallows the effect's
    /// own exception so one failed grant never breaks the awaited chain.</summary>
    private static async Task FireAsync(RelicModel relic, Task effect)
    {
        relic.Flash();
        try { await effect; }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] affix effect failed: {e.Message}"); }
    }

    // ============================ Quenched (universal) ============================

    /// <summary>Quenched — the player's Vigor was consumed (VigorPower.AfterAttack pays it down via a
    /// negative PowerCmd.ModifyAmount); each point consumed rolls an independent chance to grant
    /// 1 Strength. Echo-free by construction: the grant touches StrengthPower, never VigorPower.
    /// AWAITED (the hook fires mid-attack inside the vigor pay-down command).</summary>
    // 15% per point: vigor supply is thin everywhere (Regent's Patter 2-3 / Terraforming 6-8, the
    // shared Prep Time 4-6/turn, Akabeko 8/combat). At 15% that's ~1.2 Strength per Akabeko combat
    // (the Focused "+1 Focus per combat" benchmark) and ~3-4/combat in a dedicated Prep Time or
    // Regent vigor deck; the original 5% averaged a dead-feeling 0.4.
    public const float QuenchedChance = 0.15f;

    public static async Task OnVigorConsumed(PlayerChoiceContext ctx, Player player, int consumed)
    {
        if (!Enabled || consumed <= 0 || player.Creature == null) return;
        int turn = TurnOf(player);
        foreach (var relic in Owned(player, "Quenched"))
        {
            int gained = 0;
            for (int i = 0; i < consumed; i++)
                if (Roll(player, relic, turn) < QuenchedChance) gained++;
            if (gained > 0)
                await FireAsync(relic, PowerCmd.Apply<StrengthPower>(ctx, player.Creature, gained, player.Creature, null));
        }
    }

    // ============================ Ironclad ============================

    // 혈철의 / Bloodforged: per-relic [combatEpoch, used] — the Focused once-per-combat idiom.
    private static readonly ConditionalWeakTable<RelicModel, int[]> BloodOnce = new();

    /// <summary>Cindered — a card was exhausted; gain 2 Block per relic. AWAITED inside the exhaust
    /// hook chain (mid-command).</summary>
    public static async Task OnCardExhausted(PlayerChoiceContext ctx, Player player)
    {
        if (!Enabled || player.Creature == null) return;
        foreach (var relic in Owned(player, "Cindered"))
            await FireAsync(relic, CreatureCmd.GainBlock(player.Creature, 2m, ValueProp.Unpowered, null));
    }

    /// <summary>Bloodforged — the player's own creature lost HP; the FIRST loss each combat grants
    /// 2 Strength. AWAITED inside the damage hook chain.</summary>
    public static async Task OnPlayerHpLost(PlayerChoiceContext ctx, Player player)
    {
        if (!Enabled || player.Creature == null) return;
        foreach (var relic in Owned(player, "Bloodforged"))
        {
            var box = BloodOnce.GetValue(relic, _ => new int[2]);
            if (box[0] != _combatEpoch) { box[0] = _combatEpoch; box[1] = 0; }
            if (box[1] != 0) continue;
            box[1] = 1;
            await FireAsync(relic, PowerCmd.Apply<StrengthPower>(ctx, player.Creature, 2m, player.Creature, null));
        }
    }

    /// <summary>Retaliating — turn start: gain Vigor equal to HALF the HP lost since the last own
    /// turn start (floor, minimum 1 when anything was lost). Reads the same damage meter as
    /// Vengeful/Empathic — the meter is reset AFTER the turn-start relic loop, so all three
    /// consumers see the same window.</summary>
    public static void OnTurnRetaliating(PlayerChoiceContext ctx, Player player, RelicModel relic)
    {
        int lost = Meter(player)[1];
        if (lost <= 0 || player.Creature == null) return;
        decimal vigor = Math.Max(1, lost / 2);
        Fire(relic, PowerCmd.Apply<VigorPower>(ctx, player.Creature, vigor, player.Creature, null));
    }

    /// <summary>Gouging — the player applied Vulnerable to an enemy; 25% chance to apply 1 more
    /// (enemy as its own applier — the Envenomed echo-free trick). Karmic (curse) — the same
    /// trigger, 25% chance the player gains 1 Vulnerable too (self-apply has a player-side owner,
    /// so the dispatch's enemy-owner gate blocks any echo).</summary>
    public static async Task OnVulnApplied(PlayerChoiceContext ctx, Player player, Creature enemy)
    {
        if (!Enabled) return;
        int turn = TurnOf(player);
        foreach (var relic in Owned(player, "Gouging"))
            if (Roll(player, relic, turn) < 0.25f)
                await FireAsync(relic, PowerCmd.Apply<VulnerablePower>(ctx, enemy, 1m, enemy, null));
        if (player.Creature == null) return;
        foreach (var relic in OwnedByCurse(player, "Karmic"))
            if (Roll(player, relic, turn) < 0.25f)
                await FireAsync(relic, PowerCmd.Apply<VulnerablePower>(ctx, player.Creature, 1m, player.Creature, null));
    }

    /// <summary>Mirrored (curse) — the player gained Strength (any source, owner-side attribution);
    /// 25% chance a RANDOM living enemy gains 1 Strength. The enemy grant has an enemy-side owner,
    /// so the dispatch's player-owner gate blocks any echo.</summary>
    public static async Task OnStrengthGained(PlayerChoiceContext ctx, Player player)
    {
        if (!Enabled || player.Creature?.CombatState == null) return;
        int turn = TurnOf(player);
        foreach (var relic in OwnedByCurse(player, "Mirrored"))
        {
            if (Roll(player, relic, turn) >= 0.25f) continue;
            var foes = new List<Creature>();
            foreach (var e in player.Creature.CombatState.Enemies)
                if (e.IsAlive) foes.Add(e);
            if (foes.Count == 0) continue;
            int pick = (int)(Roll(player, relic, turn) * foes.Count);
            if (pick >= foes.Count) pick = foes.Count - 1;
            await FireAsync(relic, PowerCmd.Apply<StrengthPower>(ctx, foes[pick], 1m, foes[pick], null));
        }
    }

    /// <summary>Lingering (curse) — a card of this player is about to be EXHAUSTED; 25% chance the
    /// exhaust is dodged (the caller discards it instead — see ExhaustDodgePatch, a Harmony prefix
    /// on CardCmd.Exhaust). Deterministic Roll, so every co-op peer dodges the same exhausts.</summary>
    public static bool ShouldLingeringDodge(Player player, CardModel card)
    {
        if (!Enabled) return false;
        int turn = TurnOf(player);
        foreach (var relic in OwnedByCurse(player, "Lingering"))
            if (Roll(player, relic, turn) < 0.25f)
            {
                relic.Flash();
                return true;
            }
        return false;
    }

    // ============================ Silent ============================

    /// <summary>Envenomed — the player applied Poison to an enemy; 50% chance to apply 1 more. The +1
    /// is applied with the enemy as its own applier (the StripOne trick), so it does NOT read as a
    /// player-applied poison and never re-triggers this handler — no echo counter needed.</summary>
    public static async Task OnPoisonApplied(PlayerChoiceContext ctx, Player player, Creature enemy)
    {
        if (!Enabled) return;
        int turn = TurnOf(player);
        foreach (var relic in Owned(player, "Envenomed"))
            if (Roll(player, relic, turn) < 0.5f)
                await FireAsync(relic, PowerCmd.Apply<PoisonPower>(ctx, enemy, 1m, enemy, null));
    }

    /// <summary>Flurrying — the player played a Shiv; 25% chance to add a Shiv to hand.</summary>
    public static async Task OnShivPlayed(Player player, ICombatStateAccess cs)
    {
        if (!Enabled) return;
        int turn = TurnOf(player);
        foreach (var relic in Owned(player, "Flurrying"))
            if (Roll(player, relic, turn) < 0.25f)
                await FireAsync(relic, CardPileCmd.Add(cs.CreateShiv(player), PileType.Hand));
    }

    /// <summary>Retrieving — a Shiv was EXHAUSTED (exhaust only — a plain discard does not trigger,
    /// by design); 25% chance it returns to hand. AWAITED inside the exhaust hook chain (the
    /// Cycling reasoning). One success is terminal — the card is already back in hand, so further
    /// Retrieving relics stop rolling.</summary>
    public static async Task OnShivExhausted(Player player, CardModel card)
    {
        if (!Enabled) return;
        int turn = TurnOf(player);
        foreach (var relic in Owned(player, "Retrieving"))
            if (Roll(player, relic, turn) < 0.25f)
            {
                relic.Flash();
                await CardPileCmd.Add(card, PileType.Hand);
                break;
            }
    }

    /// <summary>Slippery (curse) — each turn start, discard a random card from hand. Rides
    /// CardCmd.Discard so AfterCardDiscarded fires normally: Cycling may react — the curse's
    /// sting is softened by the very archetype it punishes, by design.</summary>
    public static void OnTurnSlippery(PlayerChoiceContext ctx, Player player, RelicModel relic, int turn)
    {
        var hand = PileType.Hand.GetPile(player).Cards;
        if (hand.Count == 0) return;
        int pick = (int)(Roll(player, relic, turn) * hand.Count);
        if (pick >= hand.Count) pick = hand.Count - 1;
        Fire(relic, CardCmd.Discard(ctx, hand[pick]));
    }

    /// <summary>Dulled (curse) — poison the player's side applies to an enemy cannot push that enemy's
    /// poison above this cap. Clamped pre-application (ModifyPowerAmountReceived), so downstream
    /// handlers see the clamped amount. Note: Envenomed's echo-safe +1 rides applier=enemy and thus
    /// slips past the player-attribution check — a deliberate crack in the cap, not a bug.</summary>
    // 6 = the boundary that lets the bread-and-butter singles land whole (Poisoned Stab 3 / Haze 4 /
    // Deadly Poison 5) while clipping the big hits and all stacking (Snakebite 7, Bubble Bubble 9,
    // Bouncing Flask 3x3, Catalyst-style scaling) — measured against the current card pool.
    public const int DulledPoisonCap = 6;

    /// <summary>Clamp a positive enemy-bound poison application for Dulled. Returns the (possibly
    /// reduced, floor 0) amount; unchanged when the applying player doesn't carry the curse.</summary>
    public static decimal ClampPoisonForDulled(Creature? giver, Creature target, decimal amount)
    {
        if (!Enabled || amount <= 0m || target.Side != CombatSide.Enemy) return amount;
        Player? applier = giver?.Player ?? giver?.PetOwner;
        if (applier == null) return amount;
        bool cursed = false;
        foreach (var _ in OwnedByCurse(applier, "Dulled")) { cursed = true; break; }
        if (!cursed) return amount;
        decimal current = target.GetPower<PoisonPower>()?.Amount ?? 0;
        decimal allowed = Math.Max(0m, DulledPoisonCap - current);
        return Math.Min(amount, allowed);
    }

    /// <summary>Levied (curse) — true if the player carries the star-cost-increase curse.</summary>
    public static bool HasLevied(Player player)
    {
        foreach (var _ in OwnedByCurse(player, "Levied")) return true;
        return false;
    }

    /// <summary>Cycling — the player discarded a card; draw one. Draw is a no-op when both draw and
    /// discard piles are empty, so there is no mill/loop risk (discarding a card doesn't draw-then-
    /// discard).
    ///
    /// CO-OP: AWAITED, not fire-and-forget. AfterCardDiscarded fires inside DiscardAndDraw's per-card
    /// loop (decompile-verified), exactly like the draw loop that broke Cursefed — a detached Draw would
    /// interleave non-deterministically with the remaining discards and desync lockstep. The discard
    /// patch chains this onto the hook's Task so the draw runs in-order on every client.</summary>
    public static async Task OnCardDiscardedAsync(PlayerChoiceContext ctx, Player player)
    {
        if (!Enabled) return;
        foreach (var relic in Owned(player, "Cycling"))
        {
            relic.Flash();
            await CardPileCmd.Draw(ctx, 1m, player);
        }
    }

    // ============================ Defect ============================

    /// <summary>Focused — the player gained Focus from ANY source; the first gain each combat grants
    /// +1 more. Source-agnostic on purpose: Supercharged's full-slots grant COUNTS (the two prefixes
    /// synergize — verified as the expected behavior in play testing), and no loop is possible — the
    /// echo of Focused's own +1 is blocked by the per-combat once-flag below, and every negative
    /// (Shorted, Supercharged's revoke) is filtered by the amount &gt; 0 gate in the dispatch patch.</summary>
    public static async Task OnFocusGained(PlayerChoiceContext ctx, Player player)
    {
        if (!Enabled) return;
        int turn = TurnOf(player);
        foreach (var relic in Owned(player, "Focused"))
        {
            var box = FocusOnce.GetValue(relic, _ => new int[2]);
            if (box[0] != _combatEpoch) { box[0] = _combatEpoch; box[1] = 0; }
            if (box[1] != 0) continue;
            box[1] = 1;
            await FireAsync(relic, PowerCmd.Apply<FocusPower>(ctx, player.Creature, 1m, player.Creature, null));
        }
    }

    /// <summary>Amplified — the player channeled an orb; 25% chance to channel a random orb. AWAITED
    /// in-order (ChannelPatch chains this onto Hook.AfterOrbChanneled's Task), NOT fire-and-forget:
    /// AfterOrbChanneled runs inside OrbCmd.Channel's own await chain, which runs inside the card-play
    /// (and Replay) series loop in CardModel.OnPlayWrapper. A detached bonus channel raced that series —
    /// re-entering the shared OrbQueue and desyncing the echo counter against the series' real channels —
    /// so a replayed Zap/Tempest could stall mid-series (card stuck centered, no orb, no effect) and also
    /// desync co-op. Awaiting resolves the bonus (and its single echo) depth-first before the series
    /// advances, and the deterministic order keeps every co-op client in lockstep.</summary>
    public static async Task OnOrbChanneledAsync(PlayerChoiceContext ctx, Player player)
    {
        if (!Enabled) return;
        if (_suppressChannel > 0) { _suppressChannel--; }   // swallow the echo of our own bonus channel
        else
        {
            int turn = TurnOf(player);
            foreach (var relic in Owned(player, "Amplified"))
                if (Roll(player, relic, turn) < 0.25f)
                {
                    relic.Flash();
                    _suppressChannel++;                     // consumed by the bonus channel's own AfterOrbChanneled echo
                    await ChannelRandom(ctx, player, Roll(player, relic, turn));
                }
        }
        await Reconcile(ctx, player);   // channeling may have filled the slots — refresh Supercharged
    }

    /// <summary>Polarized (curse) — at each player's turn end (Hook.BeforeFlush), arm the penalty if
    /// the orb slots are ALL empty or ALL full; the next turn start consumes it (-1 Energy). Capacity
    /// 0 (no slots at all) never arms — there is no orb state to manage.</summary>
    public static void OnTurnEndOrbCheck(Player player)
    {
        if (!Enabled) return;
        var q = player.PlayerCombatState?.OrbQueue;
        if (q == null || q.Capacity <= 0) return;
        bool allEmpty = q.Orbs.Count == 0;
        bool allFull = q.Orbs.Count >= q.Capacity;
        if (!allEmpty && !allFull) return;
        foreach (var relic in OwnedByCurse(player, "Polarized"))
        {
            PolarizedArmed.GetValue(relic, _ => new int[1])[0] = _combatEpoch;
        }
    }

    /// <summary>Polarized — turn start: consume an armed flag from THIS combat's previous turn end.
    /// Runs after the turn's energy refill, so LoseEnergy lands as a net -1.</summary>
    public static void OnTurnPolarized(Player player, RelicModel relic)
    {
        var box = PolarizedArmed.GetValue(relic, _ => new int[1]);
        if (box[0] != _combatEpoch) return;
        box[0] = 0;
        Fire(relic, PlayerCmd.LoseEnergy(1m, player));
    }

    /// <summary>Channel one of the four core orbs, chosen by a deterministic roll.</summary>
    private static Task ChannelRandom(PlayerChoiceContext ctx, Player player, float roll)
    {
        int pick = (int)(roll * 4);
        return pick switch
        {
            0 => OrbCmd.Channel<LightningOrb>(ctx, player),
            1 => OrbCmd.Channel<FrostOrb>(ctx, player),
            2 => OrbCmd.Channel<DarkOrb>(ctx, player),
            _ => OrbCmd.Channel<PlasmaOrb>(ctx, player),
        };
    }

    /// <summary>Preheated — combat start (turn 1): Channel one random orb. A REAL OrbCmd.Channel,
    /// so Amplified can chain off it like any other channel. Detached at the turn-start choke
    /// (the Bonebound idiom).</summary>
    public static void OnCombatStartPreheated(PlayerChoiceContext ctx, Player player, RelicModel relic)
        => Fire(relic, ChannelRandom(ctx, player, Roll(player, relic, 1)));

    /// <summary>Sealed (curse) — combat start (turn 1): remove 1 orb slot, never below 1.
    /// OrbCmd.RemoveSlots is synchronous and self-clamping; it also drops overflow orbs.</summary>
    public static void OnCombatStartSealed(Player player, RelicModel relic)
    {
        var q = player.PlayerCombatState?.OrbQueue;
        if (q == null || q.Capacity <= 1) return;
        relic.Flash();
        OrbCmd.RemoveSlots(player, 1);
    }

    /// <summary>Unstable (curse) — turn end (BeforeFlush, after the queue's own end-of-turn
    /// triggers), 25% chance the OLDEST orb turns into a DIFFERENT random orb. Pure synchronous
    /// queue surgery (the removed OrbCmd.Replace idiom, reconstructed below) — no hooks raised,
    /// so it is safe detached and reproduces on every client via the deterministic Roll.</summary>
    public static void OnTurnEndUnstable(Player player)
    {
        if (!Enabled) return;
        var q = player.PlayerCombatState?.OrbQueue;
        if (q == null) return;
        int turn = TurnOf(player);
        foreach (var relic in OwnedByCurse(player, "Unstable"))
        {
            if (q.Orbs.Count == 0) break;
            if (Roll(player, relic, turn) >= 0.25f) continue;
            var oldOrb = q.Orbs[0];
            var newOrb = CreateRandomOrbExcept(oldOrb, Roll(player, relic, turn));
            relic.Flash();
            ReplaceOrb(player, oldOrb, newOrb);
        }
    }

    /// <summary>A fresh mutable orb of a random core type DIFFERENT from <paramref name="old"/>'s
    /// (so the morph is always a real change; a non-core old orb draws from all four).</summary>
    private static OrbModel CreateRandomOrbExcept(OrbModel old, float roll)
    {
        var types = new List<int>(4);
        if (old is not LightningOrb) types.Add(0);
        if (old is not FrostOrb) types.Add(1);
        if (old is not DarkOrb) types.Add(2);
        if (old is not PlasmaOrb) types.Add(3);
        int pick = (int)(roll * types.Count);
        if (pick >= types.Count) pick = types.Count - 1;
        return types[pick] switch
        {
            0 => ModelDb.Orb<LightningOrb>().ToMutable(),
            1 => ModelDb.Orb<FrostOrb>().ToMutable(),
            2 => ModelDb.Orb<DarkOrb>().ToMutable(),
            _ => ModelDb.Orb<PlasmaOrb>().ToMutable(),
        };
    }

    /// <summary>In-place queue swap — the old game's OrbCmd.Replace (dropped from the current build),
    /// reconstructed: same Remove/Insert order and the node-side ReplaceOrb anim.</summary>
    private static void ReplaceOrb(Player player, OrbModel oldOrb, OrbModel newOrb)
    {
        var q = player.PlayerCombatState?.OrbQueue;
        if (q == null) return;
        int idx = 0;
        for (int i = 0; i < q.Orbs.Count; i++)
            if (ReferenceEquals(q.Orbs[i], oldOrb)) { idx = i; break; }
        newOrb.Owner = player;
        if (q.Remove(oldOrb)) oldOrb.RemoveInternal();
        q.Insert(idx, newOrb);
        try { NCombatRoom.Instance?.GetCreatureNode(player.Creature)?.OrbManager?.ReplaceOrb(oldOrb, newOrb); }
        catch { /* visual only */ }
    }

    /// <summary>Supercharged — reconcile the +1 Focus that applies WHILE the orb slots are full. Called
    /// on channel / evoke / turn start (any slot-count change). Tracks a per-relic granted flag and
    /// applies/removes exactly one Focus on the full↔not-full transition. Skips while an OrbCmd.Channel
    /// is in flight — channeling into a full queue evokes-then-enqueues, and reacting to that transient
    /// "not full" would flicker the Focus off and back on; the bracket patch reconciles once after the
    /// channel settles instead.</summary>
    public static async Task Reconcile(PlayerChoiceContext ctx, Player player)
    {
        if (!Enabled || player.Creature == null) return;
        if (_channelDepth > 0)
        {
            return;
        }
        var q = player.PlayerCombatState?.OrbQueue;
        if (q == null) return;
        bool full = q.Capacity > 0 && q.Orbs.Count >= q.Capacity;
        foreach (var relic in Owned(player, "Supercharged"))
        {
            var box = Charged.GetValue(relic, _ => new int[1]);
            if (box[0] != 0 && box[0] != _combatEpoch) box[0] = 0;   // new combat -> clear stale grant
            bool granted = box[0] == _combatEpoch;
            if (full && !granted)
            {
                box[0] = _combatEpoch;
                await FireAsync(relic, PowerCmd.Apply<FocusPower>(ctx, player.Creature, 1m, player.Creature, null));
            }
            else if (!full && granted)
            {
                box[0] = 0;
                await FireAsync(relic, PowerCmd.Apply<FocusPower>(ctx, player.Creature, -1m, player.Creature, null));
            }
        }
    }

    // ============================ Necrobinder ============================

    // 설욕의/감응의/보복의 damage meters: per-player [epoch, playerHpLost, summonHpLost], accumulated
    // between the player's OWN turn starts (so the enemy turn in between is captured). Fed by the
    // damage patches (NotePlayerDamage / NotePetDamage), consumed by Vengeful / Empathic /
    // Retaliating at turn start, then cleared (ResetDamageMeter) — all inside the same
    // deterministic turn-start dispatch.
    private static readonly ConditionalWeakTable<Player, int[]> DmgMeter = new();

    private static int[] Meter(Player p)
    {
        var m = DmgMeter.GetValue(p, _ => new int[3]);
        if (m[0] != _combatEpoch) { m[0] = _combatEpoch; m[1] = 0; m[2] = 0; }
        return m;
    }

    /// <summary>Damage bookkeeping (CharAffixPatches.DamagePatch): the player's own creature lost HP.</summary>
    public static void NotePlayerDamage(Player owner, int unblocked) { if (unblocked > 0) Meter(owner)[1] += unblocked; }

    /// <summary>The owner's summon lost HP (both the normal and the lethal damage path).</summary>
    public static void NotePetDamage(Player owner, int unblocked) { if (unblocked > 0) Meter(owner)[2] += unblocked; }

    /// <summary>Turn start, AFTER Vengeful/Empathic consumed their meters: restart the window.</summary>
    public static void ResetDamageMeter(Player player) { var m = Meter(player); m[1] = 0; m[2] = 0; }

    /// <summary>Vengeful — turn start: Summon 2× the HP the player lost since their last turn start.
    /// The summon is a REAL OstyCmd.Summon, so Necromantic's block (and Famished's defusal) chain off it.</summary>
    public static void OnTurnVengeful(PlayerChoiceContext ctx, Player player, RelicModel relic)
    {
        int dmg = Meter(player)[1];
        if (dmg <= 0) return;
        Fire(relic, OstyCmd.Summon(ctx, player, dmg * 2m, relic));
    }

    /// <summary>Empathic — turn start: gain Block equal to the HP the summon lost since last turn start.</summary>
    public static void OnTurnEmpathic(Player player, RelicModel relic)
    {
        int dmg = Meter(player)[2];
        if (dmg <= 0 || player.Creature == null) return;
        Fire(relic, CreatureCmd.GainBlock(player.Creature, dmg, ValueProp.Unpowered, null));
    }

    // 굶주린 / Famished: per-player [epoch, lastSummonTurn] + per-relic [epochWhenArmed] — the
    // Polarized idiom: armed at turn end (no summon happened this turn), consumed at the next turn
    // start where a PlayerChoiceContext is available for the shrink's damage path.
    private static readonly ConditionalWeakTable<Player, int[]> SummonMark = new();
    private static readonly ConditionalWeakTable<RelicModel, int[]> FamishedArmed = new();

    /// <summary>AfterSummon bookkeeping: the player summoned this turn (defuses Famished).</summary>
    private static void NoteSummon(Player player)
    {
        var m = SummonMark.GetValue(player, _ => new int[2] { -1, -1 });
        m[0] = _combatEpoch; m[1] = TurnOf(player);
    }

    /// <summary>Famished (curse) — turn end: arm if the player did NOT summon this turn.</summary>
    public static void OnTurnEndFamishedCheck(Player player)
    {
        if (!Enabled) return;
        var m = SummonMark.GetValue(player, _ => new int[2] { -1, -1 });
        if (m[0] == _combatEpoch && m[1] == TurnOf(player)) return;   // summoned — defused
        foreach (var relic in OwnedByCurse(player, "Famished"))
            FamishedArmed.GetValue(relic, _ => new int[1])[0] = _combatEpoch;
    }

    /// <summary>Famished — next turn start: consume the armed flag; Osty loses 1 Max HP (never below
    /// 1; no-op with no living Osty). LoseMaxHp runs the real damage path when current HP exceeds
    /// the new max, so a starved Osty genuinely withers.</summary>
    public static void OnTurnFamished(PlayerChoiceContext ctx, Player player, RelicModel relic)
    {
        var box = FamishedArmed.GetValue(relic, _ => new int[1]);
        if (box[0] != _combatEpoch) return;
        box[0] = 0;
        if (!player.IsOstyAlive) return;
        var osty = player.Osty;
        if (osty == null || osty.MaxHp <= 1) return;
        relic.Flash();
        TaskHelper.RunSafely(ShrinkOsty(ctx, osty));
    }

    private static async Task ShrinkOsty(PlayerChoiceContext ctx, Creature osty)
    {
        await CreatureCmd.LoseMaxHp(ctx, osty, 1m, isFromCard: false);
        try
        {
            if (osty.IsAlive)   // mirror OstyCmd.Summon's rescale so the sprite tracks the new size
                NCombatRoom.Instance?.GetCreatureNode(osty)?.OstyScaleToSize(osty.MaxHp, 0.75f);
        }
        catch { /* visual only */ }
    }

    /// <summary>Necromantic — the player summoned (grew Osty); gain 3 Block. Also the Famished
    /// bookkeeping point: ANY summon this turn (card, Bonebound, Vengeful) defuses the curse.</summary>
    public static async Task OnSummon(PlayerChoiceContext ctx, Player player)
    {
        if (!Enabled || player.Creature == null) return;
        NoteSummon(player);
        foreach (var relic in Owned(player, "Necromantic"))
            await FireAsync(relic, CreatureCmd.GainBlock(player.Creature, 3m, ValueProp.Unpowered, null));
    }

    /// <summary>Dooming — the player applied Doom to an enemy; apply 1 more (enemy as its own applier,
    /// so no echo — same trick as Envenomed).</summary>
    public static async Task OnDoomApplied(PlayerChoiceContext ctx, Player player, Creature enemy)
    {
        if (!Enabled) return;
        foreach (var relic in Owned(player, "Dooming"))
            await FireAsync(relic, PowerCmd.Apply<DoomPower>(ctx, enemy, 1m, enemy, null));
    }

    /// <summary>Bonebound — per turn, Summon 1 (grows Osty). Fires from the turn-start patch.</summary>
    public static void OnTurnBonebound(PlayerChoiceContext ctx, Player player, RelicModel relic)
        => Fire(relic, OstyCmd.Summon(ctx, player, 1m, relic));

    /// <summary>Doombound (curse) — per turn, 1 Doom to YOURSELF. Fires from the turn-start patch.
    /// Echo-safe: the Dooming handler only reacts to doom on ENEMY-side owners.</summary>
    public static void OnTurnDoombound(PlayerChoiceContext ctx, Player player, RelicModel relic)
        => Fire(relic, PowerCmd.Apply<DoomPower>(ctx, player.Creature, 1m, player.Creature, null));

    /// <summary>Sacrificial (curse) — your summon took unblocked damage; apply Weak / Frail /
    /// Vulnerable 1 to YOURSELF, once per turn per relic. A LETHAL hit (the summon died) applies 2
    /// and bypasses the once-per-turn gate — the death is a distinct, bigger event than the chip
    /// hit that may already have fired this turn.</summary>
    public static async Task OnSummonDamaged(PlayerChoiceContext ctx, Player owner, bool lethal = false)
    {
        if (!Enabled || owner.Creature == null) return;
        int turn = TurnOf(owner);
        decimal stacks = lethal ? 2m : 1m;
        foreach (var relic in OwnedByCurse(owner, "Sacrificial"))
        {
            var box = SacTurn.GetValue(relic, _ => new int[1] { -1 });
            if (!lethal && box[0] == turn) continue;   // once per turn (death fires regardless)
            box[0] = turn;
            int pick = (int)(Roll(owner, relic, turn) * 3);
            Task t = pick switch
            {
                0 => PowerCmd.Apply<WeakPower>(ctx, owner.Creature, stacks, owner.Creature, null),
                1 => PowerCmd.Apply<FrailPower>(ctx, owner.Creature, stacks, owner.Creature, null),
                _ => PowerCmd.Apply<VulnerablePower>(ctx, owner.Creature, stacks, owner.Creature, null),
            };
            await FireAsync(relic, t);
        }
    }

    // ============================ Regent ============================

    /// <summary>Starlit — per turn, gain 1 Star. Fires from the turn-start patch.</summary>
    public static void OnTurnStarlit(Player player, RelicModel relic)
        => Fire(relic, PlayerCmd.GainStars(1m, player));

    /// <summary>Reforging — the player forged a card in combat; gain 1 Star. Tributary — tiered
    /// draw on the same trigger: 25% draw 2, else 50% draw 1 (75% total to draw something).
    /// Hook.AfterForge carries no PlayerChoiceContext, so the draw uses a fresh
    /// BlockingPlayerChoiceContext (the Vakuu idiom — a plain draw prompts no real choice;
    /// the Throwing variant would freeze).</summary>
    public static async Task OnForge(Player player)
    {
        if (!Enabled) return;
        int turn = TurnOf(player);
        foreach (var relic in Owned(player, "Reforging"))
            await FireAsync(relic, PlayerCmd.GainStars(1m, player));
        foreach (var relic in Owned(player, "Tributary"))
        {
            float r = Roll(player, relic, turn);
            int draw = r < 0.25f ? 2 : r < 0.75f ? 1 : 0;
            if (draw > 0)
                await FireAsync(relic, CardPileCmd.Draw(new BlockingPlayerChoiceContext(), draw, player));
        }
    }

    /// <summary>Bountiful — the player gained Stars; 33% chance to gain 1 more (echo-suppressed).
    /// Prodigal (curse) — 25% chance to lose 1 right back ("gains 1 less"; PlayerCmd.LoseStars
    /// raises no hook, so no echo is possible). AWAITED mid PlayerCmd.GainStars.</summary>
    public static async Task OnStarsGained(Player player, int amount)
    {
        if (!Enabled || amount <= 0) return;
        if (_suppressStarGain > 0) { _suppressStarGain--; return; }
        int turn = TurnOf(player);
        foreach (var relic in Owned(player, "Bountiful"))
            if (Roll(player, relic, turn) < 0.33f)
            {
                relic.Flash();
                _suppressStarGain++;
                await PlayerCmd.GainStars(1m, player);
            }
        foreach (var relic in OwnedByCurse(player, "Prodigal"))
            if (Roll(player, relic, turn) < 0.25f)
            {
                relic.Flash();
                await PlayerCmd.LoseStars(1m, player);
            }
    }

    /// <summary>Tarnished (curse) — turn end: lose 1 Star (no-op at 0). LoseStars is synchronous
    /// and hook-free, so the detached dispatch is deterministic.</summary>
    public static void OnTurnEndTarnished(Player player)
    {
        if (!Enabled) return;
        foreach (var relic in OwnedByCurse(player, "Tarnished"))
        {
            if ((player.PlayerCombatState?.Stars ?? 0) <= 0) return;
            Fire(relic, PlayerCmd.LoseStars(1m, player));
        }
    }

    /// <summary>Every 4th Star spent evaporates an asset (Bankrupt's debt meter, below).</summary>
    public const int BankruptStarsPerTrigger = 4;

    /// <summary>Regal (refund) + Bankrupt (curse) both trigger on spending Stars. LoseStars/GainStars
    /// do NOT re-fire AfterStarsSpent (verified), so there is no echo. Bankrupt is a deterministic
    /// DEBT METER: every <see cref="BankruptStarsPerTrigger"/> Stars spent this combat, a random
    /// non-Ethereal card in hand becomes Ethereal (CardCmd.ApplyKeyword — the GhostSeed idiom, UI
    /// refresh included) — play it this turn or watch the asset evaporate. No eligible card in hand
    /// = that trigger fizzles (the debt is still paid).</summary>
    public static async Task OnStarsSpent(Player player, ICombatStateAccess cs, int amount)
    {
        if (!Enabled) return;
        int turn = TurnOf(player);

        foreach (var relic in Owned(player, "Regal"))
            if (Roll(player, relic, turn) < 0.5f)
            {
                await FireAsync(relic, PlayerCmd.GainStars(1m, player));
            }

        if (amount <= 0) return;
        foreach (var relic in OwnedByCurse(player, "Bankrupt"))
        {
            var debt = BankruptDebt.GetValue(relic, _ => new int[2]);
            if (debt[0] != _combatEpoch) { debt[0] = _combatEpoch; debt[1] = 0; }
            debt[1] += amount;
            while (debt[1] >= BankruptStarsPerTrigger)
            {
                debt[1] -= BankruptStarsPerTrigger;
                var hand = PileType.Hand.GetPile(player).Cards;
                var eligible = new List<CardModel>();
                foreach (var c in hand)
                    if (!c.Keywords.Contains(CardKeyword.Ethereal)) eligible.Add(c);
                if (eligible.Count == 0)
                {
                    continue;
                }
                int pick = (int)(Roll(player, relic, turn) * eligible.Count);
                if (pick >= eligible.Count) pick = eligible.Count - 1;
                var card = eligible[pick];
                relic.Flash();
                CardCmd.ApplyKeyword(card, CardKeyword.Ethereal);
            }
            NotifyCounter(relic);   // redraw the icon's countdown with the settled remainder
        }
    }

    // ---- Combat-start curses (fired from the turn-start patch on turn 1) ----

    public static void OnCombatStartToxic(PlayerChoiceContext ctx, Player player, RelicModel relic)
        => Fire(relic, PowerCmd.Apply<PoisonPower>(ctx, player.Creature, 3m, player.Creature, null));

    public static void OnCombatStartShorted(PlayerChoiceContext ctx, Player player, RelicModel relic)
        => Fire(relic, PowerCmd.Apply<FocusPower>(ctx, player.Creature, -2m, player.Creature, null));

    // ============================ Echoing (universal) ============================
    // 메아리의 / Echoing — each turn a random hand card gains Replay 1; playing that card costs the
    // player Vulnerable 1 + Frail 1. BaseReplayCount is PERMANENT on the card instance (GeneratePlayCount
    // reads it live and never decrements it — decompile-verified), so a raw ++ every turn would snowball
    // and even leak across combats. We therefore grant a SINGLE-TURN Replay: bump at turn start, revert
    // at turn end (and defensively at the next grant, in case a combat-ending turn skipped its flush).

    private sealed class EchoState { public CardModel? Card; public int Turn = -1; public bool Penalized; }
    private static readonly ConditionalWeakTable<RelicModel, EchoState> Echo = new();

    // Undo exactly the +1 we granted (setter raises ReplayCountChanged → the card face refreshes). Guarded
    // so we never push a card below its own baseline if some other effect cleared the replay meanwhile.
    private static void RevertReplay(CardModel? card)
    {
        if (card != null && card.BaseReplayCount > 0) card.BaseReplayCount--;
    }

    /// <summary>Echoing — turn start: revert any lingering grant, then grant Replay 1 to a random
    /// playable (non-Curse/Status) card in hand. The pick rides the deterministic per-(relic,turn) Roll so
    /// all clients grant the same card; the hand order is already synced.</summary>
    public static void OnTurnEchoing(Player player, RelicModel relic, int turn)
    {
        if (!Enabled) return;
        var st = Echo.GetValue(relic, _ => new EchoState());
        RevertReplay(st.Card);                 // undo a grant a combat-ending turn never flushed
        st.Card = null; st.Turn = turn; st.Penalized = false;

        var eligible = new List<CardModel>();
        foreach (var c in PileType.Hand.GetPile(player).Cards)
            if (c.Type != CardType.Curse && c.Type != CardType.Status && c.Type != CardType.None) eligible.Add(c);
        if (eligible.Count == 0) return;        // nothing worth doubling → no grant, no cost this turn

        int pick = (int)(Roll(player, relic, turn) * eligible.Count);
        if (pick >= eligible.Count) pick = eligible.Count - 1;
        var card = eligible[pick];
        card.BaseReplayCount++;
        st.Card = card;
        relic.Flash();
    }

    /// <summary>Echoing — a card was played: if it is THIS relic's granted card, pay for the boon with
    /// Vulnerable 1 + Frail 1 to the player, once. Replay makes the card play as a series, so
    /// AfterCardPlayed can fire per play — the Penalized flag collapses that to a single charge.</summary>
    public static async Task OnCardPlayedEchoing(PlayerChoiceContext ctx, Player player, CardModel card)
    {
        if (!Enabled || player.Creature == null) return;
        int turn = TurnOf(player);
        foreach (var relic in Owned(player, "Echoing"))
        {
            var st = Echo.GetValue(relic, _ => new EchoState());
            if (st.Penalized || st.Turn != turn || !ReferenceEquals(st.Card, card)) continue;
            st.Penalized = true;
            relic.Flash();
            // AWAITED in-order (AfterCardPlayed is a mid-play / Replay-series hook): a detached apply
            // would race the play series and desync co-op (the Cursefed class).
            await PowerCmd.Apply<VulnerablePower>(ctx, player.Creature, 1m, player.Creature, null);
            await PowerCmd.Apply<FrailPower>(ctx, player.Creature, 1m, player.Creature, null);
        }
    }

    /// <summary>Echoing — turn end (BeforeFlush): revert the single-turn Replay so it never accumulates on
    /// the card instance (played card is now in a pile; unplayed card stays in hand — either way, clear).</summary>
    public static void OnTurnEndEchoing(Player player)
    {
        if (!Enabled) return;
        foreach (var relic in Owned(player, "Echoing"))
        {
            var st = Echo.GetValue(relic, _ => new EchoState());
            RevertReplay(st.Card);
            st.Card = null;
        }
    }
}

/// <summary>Tiny seam over ICombatState's generic CreateCard so the engine can build a Shiv without
/// the patch layer leaking the combat-state type everywhere. Implemented inline by the patches
/// (which hold the live ICombatState).</summary>
internal interface ICombatStateAccess
{
    CardModel CreateShiv(Player player);
}

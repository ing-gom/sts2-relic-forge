using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Enhances a relic on pickup. Because every numeric relic reads its effect magnitude
/// from the SAME DynamicVarSet that feeds its tooltip, mutating BaseValue scales both the
/// effect and the displayed number — one generic path, no per-relic code (so modded
/// relics work too, as long as they expose numeric DynamicVars).
///
/// Grade is SEED-DETERMINISTIC: derived from (runSeed, relicId, floor) exactly like the
/// game's own per-relic RNG (runState.Rng.Seed + TotalFloor + hash(id)). Same run seed →
/// same relic → same enhancement, which also makes save/load re-derivation exact (no
/// side-table needed). Rarity picks the magnitude range + prefix pool; the seeded roll
/// picks the percentage within the range and the prefix from the pool.
///
/// Eligibility = rarity present in RarityConfigs.ByRarity (Common/Uncommon/Rare/Shop/
/// Ancient). Starter/Event/None are excluded by omission.
/// </summary>
internal static class RelicForgeService
{
    // Per-relic-instance forge record; presence also guards against forging the same
    // instance twice. Tooltip patches read this for prefix + colored deltas.
    private static readonly ConditionalWeakTable<RelicModel, ForgeRecord> Records = new();

    // Marks a relic INSTANCE as a hidden companion (granted by a companion prefix), so it
    // can be hidden from the inventory and excluded from serialization. The value is the HOST
    // relic it was grafted onto, so visual effects (flash VFX) can show the host — the relic
    // the player actually owns — instead of the hidden donor. Never serialized; companions
    // are re-derived on load.
    private static readonly ConditionalWeakTable<RelicModel, RelicModel> Companions = new();

    // Save/load carries the reforge COUNT (the one non-seed-derivable value) as a SavedProperty
    // on the serialized relic. FromSerializable rebuilds a fresh RelicModel instance well before
    // LoadRun re-forges, so the count captured there is parked here, keyed by that instance, and
    // taken by RunLoadReforgePatch when it re-forges. StrongBox because a CWT value must be a ref
    // type. See ReforgeSaveInjectPatch / ReforgeLoadCapturePatch.
    private static readonly ConditionalWeakTable<RelicModel, StrongBox<int>> Pending = new();

    /// <summary>The SavedProperty name our reforge count rides on inside a serialized relic.</summary>
    public const string RfCountKey = "__rf_count";

    /// <summary>The reforge count of a relic instance (0 if never forged / never re-forged).</summary>
    public static int ReforgeCountOf(RelicModel relic)
        => Records.TryGetValue(relic, out var rec) ? rec.ReforgeCount : 0;

    /// <summary>Park a reforge count for a load-restored relic instance (set from FromSerializable).</summary>
    public static void SetPendingReforgeCount(RelicModel relic, int n) => Pending.AddOrUpdate(relic, new StrongBox<int>(n));

    /// <summary>Read the parked reforge count for a relic instance (0 if none), consuming it.</summary>
    public static int TakePendingReforgeCount(RelicModel relic)
    {
        if (!Pending.TryGetValue(relic, out var box)) return 0;
        Pending.Remove(relic);
        return box.Value;
    }

    /// <summary>The base per-relic grade seed, matching the game's own per-relic RNG idiom
    /// (runState.Rng.Seed + TotalFloor + hash(id), see decompile ~line 291714).</summary>
    private static uint GradeSeed(uint runSeed, int floor, string relicId)
        => (uint)((int)runSeed + floor + StringHelper.GetDeterministicHashCode(relicId));

    // splitmix32 finalizer (same as Sts2RngFix.Mix): every input bit affects every output bit, so
    // consecutive reforge counts map to unrelated grades instead of the correlated drift a linear
    // "+ count * k" mix would cause (see reference_sts2_rng_correlation).
    private static uint SplitMix32(uint x)
    {
        x += 0x9E3779B9u;
        x = (x ^ (x >> 16)) * 0x21F0AAADu;
        x = (x ^ (x >> 15)) * 0x735A2D97u;
        return x ^ (x >> 15);
    }

    /// <summary>The forge record for a relic instance, or null if it was never forged.</summary>
    public static ForgeRecord? RecordFor(RelicModel relic)
        => Records.TryGetValue(relic, out var rec) ? rec : null;

    /// <summary>True if this relic instance is a hidden companion granted by a companion prefix.</summary>
    public static bool IsCompanion(RelicModel relic) => Companions.TryGetValue(relic, out _);

    /// <summary>The host relic a companion was grafted onto, or null if not a companion.</summary>
    public static RelicModel? HostOf(RelicModel companion)
        => Companions.TryGetValue(companion, out var host) ? host : null;

    /// <summary>
    /// If <paramref name="host"/> rolled a companion prefix, grant the donor relic as a HIDDEN
    /// instance to <paramref name="player"/> (once per host). The donor is added via
    /// AddRelicInternal(silent) so no "relic get" popup/sound/inventory icon is created, but it
    /// still enters player.Relics — so its native hooks fire (verified: both RunState and
    /// CombatState IterateHookListeners include every non-melted relic). Called from the obtain
    /// path and the load re-forge path; the preview/tooltip path never grants.
    /// </summary>
    public static void GrantCompanionIfAny(RelicModel host, Player player)
    {
        var rec = RecordFor(host);
        if (rec?.CompanionRelic == null || rec.CompanionGranted) return;

        var template = ModelDb.AllRelics.FirstOrDefault(r => r.GetType() == rec.CompanionRelic);
        if (template == null)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] companion type {rec.CompanionRelic.Name} not found in ModelDb.");
            return;
        }
        RelicModel companion = template.ToMutable();
        WeakenCompanion(companion);                // grafted effect is a reduced version of the real relic
        Companions.Add(companion, host);           // tag (value=host) BEFORE adding so save/inventory/vfx patches see it
        player.AddRelicInternal(companion, -1, silent: true);
        rec.CompanionGranted = true;
        rec.Companion = companion;
        // Mirror the companion's live counter (e.g. "attacks until trigger") on the HOST icon:
        // when the hidden companion's DisplayAmount changes, refresh the host's holder too.
        companion.DisplayAmountChanged += host.InvokeDisplayAmountChanged;
        MainFile.Logger.Info($"[{MainFile.ModId}] grafted {companion.Id.Entry} onto {host.Id.Entry} ({rec.Prefix}).");
    }

    /// <summary>Result of a reforge attempt: the prefix was re-rolled, or the relic broke and was destroyed.</summary>
    /// <summary>Result of a reforge: a normal re-roll, or one that landed a PENALTY prefix
    /// (the campfire caller ends its reforge action on a penalty — the gamble's cost).</summary>
    public enum ReforgeOutcome { Reforged, RolledPenalty }

    /// <summary>
    /// Re-roll a relic's prefix. Works on ANY owned relic — one that already has a prefix (the
    /// normal case), one that rolled "no prefix", AND one that was never eligible for the pickup
    /// auto-forge (Starter/Event): a deliberate campfire reforge bypasses the eligibility gate and
    /// always lands a prefix. Restores the relic to its canonical numbers first (so the new grade
    /// scales from base, not from a previous enhancement), un-grafts any companion the old prefix
    /// granted, bumps the reforge count, and forges again. Deterministic given (seed, id, floor,
    /// newCount) — reloading reproduces it, so it can't be save-scummed.
    ///
    /// If breaking is enabled, each reforge past the first rolls a (deterministic) break chance; on
    /// a break the relic is destroyed and removed instead. Returns which happened.
    /// </summary>
    public static ReforgeOutcome Reforge(RelicModel relic, Player player)
    {
        // A relic may have NO record yet (never forged / ineligible on pickup) — that's fine, it
        // just starts from count 0 and canonical vars (nothing to restore).
        Records.TryGetValue(relic, out var rec);
        int next = (rec?.ReforgeCount ?? 0) + 1;

        // (1) Undo the previous grade so Forge scales from canonical values again.
        //     Grafted companion prefixes added a hidden relic instance -> remove it. Delayed
        //     prefixes need no cleanup: they re-derive from the record each turn, so swapping the
        //     record below is enough. Numeric prefixes just restore each changed var to OldValue.
        if (rec != null)
        {
            RemoveCompanion(rec, player);
            foreach (var c in rec.Changes)
                if (relic.DynamicVars.TryGetValue(c.VarName, out var dv))
                    dv.BaseValue = c.OldValue; // setter also ResetToBase() -> tooltip reverts
            Records.Remove(relic); // drop so Forge's "already processed" guard passes
        }

        // (2) Re-roll: guaranteePrefix also bypasses the rarity-eligibility gate, so a deliberate
        //     reforge can prefix even a Starter/Event relic the pickup path would have skipped.
        var runState = player.RunState;
        string? summary = Forge(relic, runState.Rng.Seed, relic.FloorAddedToDeck,
                                reforgeCount: next, guaranteePrefix: true);

        // (3) If the new prefix is a graft companion, grant it.
        GrantCompanionIfAny(relic, player);

        // (4) Did this roll land a PENALTY prefix? The campfire caller uses this to end its
        //     reforge action (the risk that balances unlimited free re-rolls).
        bool penalty = Records.TryGetValue(relic, out var newRec)
                       && (PrefixTable.ByName(newRec.Prefix)?.Penalty ?? false);
        MainFile.Logger.Info($"[{MainFile.ModId}] reforge #{next} {relic.Id.Entry}: {summary ?? "(no numeric change)"}{(penalty ? " [PENALTY]" : "")}");
        return penalty ? ReforgeOutcome.RolledPenalty : ReforgeOutcome.Reforged;
    }

    /// <summary>Remove the hidden companion instance a graft prefix granted, if any (safe if none).</summary>
    private static void RemoveCompanion(ForgeRecord rec, Player player)
    {
        var companion = rec.Companion;
        if (companion == null) return;

        // Mirror the grant path in reverse: unsubscribe the host-mirror handler, then drop the
        // hidden instance from the player so its hooks stop firing.
        if (HostOf(companion) is RelicModel host)
            companion.DisplayAmountChanged -= host.InvokeDisplayAmountChanged;
        try
        {
            if (player.Relics.Contains(companion))
                player.RemoveRelicInternal(companion, silent: true);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] reforge un-graft failed: {e.Message}");
        }
        Companions.Remove(companion);
        rec.Companion = null;
        rec.CompanionGranted = false;
    }

    // A grafted effect should be a WEAKER version of owning the real relic. Reduce each
    // beneficial (INCREASE) var by WeakenFactor, floored at 1 so it never disappears.
    // Counters/thresholds (SKIP) are left alone. VarOverride handles the odd case where
    // "weaker" means RAISING a var — HappyFlower's "every N turns" interval (3 -> 4).
    private const double WeakenFactor = 0.6;
    private static readonly Dictionary<(string relic, string var), decimal> VarOverride = new()
    {
        [("HappyFlower", "Turns")] = 4m,     // every 3 turns -> every 4 (less frequent = weaker)
        // "every N cards" counters (Cards is the modulo) — raise N so they trigger less often.
        [("LetterOpener", "Cards")] = 4m,    // every 3 skills -> 4
        [("Shuriken", "Cards")] = 4m,        // every 3 attacks -> 4
        [("OrnamentalFan", "Cards")] = 4m,   // every 3 attacks -> 4
        [("Kunai", "Cards")] = 4m,           // every 3 attacks -> 4
        [("Nunchaku", "Cards")] = 12m,       // every 10 attacks -> 12
    };

    private static void WeakenCompanion(RelicModel companion)
    {
        string key = companion.GetType().Name;
        foreach (DynamicVar dv in companion.DynamicVars.Values)
        {
            if (VarOverride.TryGetValue((key, dv.Name), out var forced)) { dv.BaseValue = forced; continue; }
            if (dv.BaseValue <= 0m) continue;
            if (AffixPolicy.DirectionFor(key, dv.Name) != AffixDir.Increase) continue; // only cut boons
            dv.BaseValue = Math.Max(1m, Math.Round(dv.BaseValue * (decimal)WeakenFactor, MidpointRounding.AwayFromZero));
        }
    }

    /// <summary>
    /// Forge a candidate relic that isn't owned yet, so its tooltip previews the enhanced
    /// state before pickup (shop offers, reward/event choices). Gated to MUTABLE instances
    /// so canonical templates in ModelDb are never touched. Uses the active run's fixed
    /// seed + current floor; because a shop's offered relic IS the same instance later
    /// passed to Obtain, the previewed grade is exactly what you get.
    /// </summary>
    public static void TryForgePreview(RelicModel relic)
    {
        if (!relic.IsMutable) return;                   // never mutate ModelDb templates
        if (Records.TryGetValue(relic, out _)) return;  // already forged
        var state = RunManager.Instance.State;
        if (state == null) return;                      // not in a run
        Forge(relic, state.Rng.Seed, state.TotalFloor);
    }

    // Treasure-room relics are shared CANONICAL instances (not per-offer mutables like
    // shop/reward), so they can't be forged in place. Instead we forge a throwaway mutable
    // clone and cache it, keyed by the offered canonical, so the tooltip can preview the
    // enhanced numbers + prefix. Cleared when the treasure closes (EndRelicVoting) so the
    // shared canonical doesn't carry a forged look into collection screens.
    private static readonly Dictionary<RelicModel, RelicModel> OfferedPreview = new();

    public static void OfferPreview(RelicModel canonical, uint runSeed, int floor)
    {
        if (canonical.IsMutable) return;        // ToMutable() would throw on a non-canonical
        var clone = canonical.ToMutable();
        Forge(clone, runSeed, floor);
        OfferedPreview[canonical] = clone;      // latest offer wins
    }

    /// <summary>The forged preview clone for a currently-offered canonical relic, if any.</summary>
    public static RelicModel? PreviewCloneFor(RelicModel canonical)
        => OfferedPreview.TryGetValue(canonical, out var clone) ? clone : null;

    public static void ClearOffers() => OfferedPreview.Clear();

    /// <summary>
    /// Enhance a relic deterministically. <paramref name="runSeed"/> is the run's fixed
    /// seed (runState.Rng.Seed) and <paramref name="floor"/> is the floor the relic was
    /// obtained on — TotalFloor at pickup, or relic.FloorAddedToDeck when re-applying on
    /// load (both the same, so the derived prefix is identical across save/load).
    /// <paramref name="forced"/> (test command only) forces a specific prefix and bypasses
    /// the eligibility gate.
    /// <paramref name="reforgeCount"/> folds into the RNG so a re-forged relic rolls a
    /// different-but-deterministic grade; 0 reproduces the original pre-reforge outcome exactly.
    /// <paramref name="guaranteePrefix"/> skips the no-prefix gate (used by reforge, so paying
    /// to re-roll never yields "no prefix"). Returns a log summary or null.
    /// </summary>
    public static string? Forge(RelicModel relic, uint runSeed, int floor, Prefix? forced = null,
                                int reforgeCount = 0, bool guaranteePrefix = false)
    {
        if (Records.TryGetValue(relic, out _)) return null; // already processed

        bool test = forced != null;
        // Eligibility gates only the automatic pickup forge. A forced test or a deliberate reforge
        // (guaranteePrefix) may prefix any relic, including Starter/Event rarities.
        if (!test && !guaranteePrefix && !PrefixTable.Eligible.Contains(relic.Rarity)) return null;
        // Opt-out for Ancient (先古) relics: keep them pure vanilla when the player disables it. Only
        // the automatic pickup forge is gated here — a forced test or deliberate reforge still may
        // touch them — but the reforge picker also hides Ancient relics while this is off, so in
        // normal play they stay untouched end-to-end.
        if (!test && !guaranteePrefix && !ForgeConfig.ForgeAncientRelics
            && relic.Rarity == RelicRarity.Ancient) return null;

        string relicId = relic.Id.Entry;               // canonical UPPER_SNAKE id (seed + logs)
        // Per-relic policy (Overrides/BoostFactor) is keyed by the C# class name, which is
        // PascalCase — NOT Id.Entry (which is UPPER_SNAKE like "LOST_COFFER"). Keying those
        // lookups off Id.Entry silently missed every per-relic override/boost. Use the type
        // name so the hand-authored PascalCase tables actually match.
        string policyKey = relic.GetType().Name;

        // Same derivation the game uses for per-relic RNG (see decompile ~line 291714).
        // reforgeCount==0 leaves the seed byte-identical to the original behavior, so existing
        // saves / first grades never shift; count>0 avalanche-mixes the count in so each re-roll
        // is an unrelated but reproducible grade.
        uint seed = GradeSeed(runSeed, floor, relicId);
        if (reforgeCount > 0)
            seed = SplitMix32(seed + (uint)reforgeCount * 0x9E3779B9u);
        var rng = new Rng(seed);

        // Prefix gate + roll. Draw both from the seeded rng in a fixed order so the
        // outcome stays deterministic and stable even if the config threshold changes:
        // a relic's would-be prefix is fixed by seed, and whether it applies depends on
        // the gate roll vs the (config) no-prefix chance. Forced (test) skips the gate.
        Prefix? prefix;
        if (forced != null)
        {
            prefix = forced;
        }
        else
        {
            double gate = rng.NextFloat();
            Prefix rolled = PrefixTable.Roll(rng);
            // Draw the gate roll unconditionally (fixed rng order) but ignore it when
            // guaranteePrefix is set, so a reforge always lands a prefix.
            prefix = (!guaranteePrefix && gate < ForgeConfig.NoPrefixChance) ? null : rolled;
        }

        // Enemy-rider roll (a Terraria-style curse): any forged relic MIGHT also strengthen enemies.
        // Drawn in fixed rng order so the stream stays stable if the chance is retuned; whether it
        // lands depends on the config chance, and penalty prefixes never carry it. A second draw
        // picks the flavor suffix name (used only if the rider lands).
        double riderRoll = rng.NextFloat();
        double suffixRoll = rng.NextFloat();

        if (prefix == null)
        {
            // No prefix (stays vanilla). Record it anyway so the relic isn't re-rolled.
            Records.Add(relic, new ForgeRecord { Rarity = relic.Rarity, Prefix = "", Percent = 0, ReforgeCount = reforgeCount });
            return null;
        }

        double pct = prefix.PowerPct;
        var record = new ForgeRecord { Rarity = relic.Rarity, Prefix = prefix.Name, Percent = pct, Amplify = prefix.Amplify, ReforgeCount = reforgeCount,
            EnemyRider = !prefix.Penalty && riderRoll < ForgeConfig.EnemyRiderChance };
        if (record.EnemyRider) record.EnemyRiderSuffix = RiderSuffix.Pick(suffixRoll);
        Records.Add(relic, record); // guard re-forge even if nothing changes

        // Companion-family prefix (grafted OR delayed): don't scale the host's vars. Grafted
        // prefixes graft a donor later (GrantCompanionIfAny); delayed prefixes apply a fixed
        // effect on a set turn (DelayedCompanionPatch). Works on ANY host relic.
        if (prefix.IsCompanionPrefix)
        {
            record.CompanionRelic = prefix.CompanionRelic; // null for delayed prefixes
            return $"{prefix.Name} {relicId}: {(prefix.CompanionRelic != null ? "graft " + prefix.CompanionRelic.Name : "delayed t" + prefix.DelayTurn)}";
        }

        foreach (DynamicVar dv in relic.DynamicVars.Values)
        {
            decimal baseVal = dv.BaseValue;
            if (baseVal <= 0m) continue; // semantic flags (=0) and empties

            var dir = AffixPolicy.DirectionFor(policyKey, dv.Name);
            if (dir == AffixDir.Skip) continue;

            // Magnitude = proportional round of |pct| (NO min-1 floor, so a small prefix
            // on a small base rounds to 0 = no change, instead of always jumping +1 which
            // is a huge relative gain on a base like 1). Sign from pct (negative weakens);
            // direction decides which way: INCREASE var moves with the sign, DECREASE
            // (downside/counter) var moves against.
            int mag = (int)Math.Round(baseVal * (decimal)(Math.Abs(pct) * AffixPolicy.BoostFor(policyKey, dv.Name)), MidpointRounding.AwayFromZero);
            decimal newVal;
            if (prefix.Amplify)
            {
                // raise every var's raw magnitude regardless of benefit direction
                newVal = baseVal + mag;
            }
            else
            {
                int signed = pct >= 0 ? mag : -mag;
                newVal = dir == AffixDir.Increase ? baseVal + signed : baseVal - signed;
            }
            newVal = Math.Max(1m, newVal); // never disable a trigger / divide-by-zero

            if (newVal == baseVal) continue;
            dv.BaseValue = newVal; // setter also ResetToBase() -> tooltip picks it up
            record.Changes.Add(new VarChange
            {
                VarName = dv.Name, OldValue = baseVal, NewValue = newVal, Dir = dir
            });
        }

        if (!record.HasChanges) return null;
        var sb = new StringBuilder();
        sb.Append(prefix.Name).Append(' ').Append(relicId)
          .Append(" (").Append(pct >= 0 ? "+" : "").Append((int)Math.Round(pct * 100)).Append("%): ");
        sb.Append(string.Join(", ", record.Changes.ConvertAll(c => $"{c.VarName} {c.OldValue:0}->{c.NewValue:0}")));
        return sb.ToString();
    }
}

using System.Collections.Generic;

namespace Sts2RelicForge;

/// <summary>
/// Per-DynamicVar-type direction policy, generated from
/// data/relic_affix_var_policy.json in the dev repo (69 var types across 218 relics).
///
///   INCREASE — bigger = better for the player; enhancement raises the value.
///   DECREASE — the var is a downside/cost (or an "every N" modulo counter);
///              enhancement lowers it (floored at 1).
///   SKIP     — neutral counter / threshold / semantic flag / multiplier; untouched.
///
/// A var not in Increase/Decrease is SKIP. Overrides win over the global bucket for
/// relics where the same var name means the opposite thing (Cards-as-counter relics).
/// </summary>
internal enum AffixDir { Skip, Increase, Decrease }

internal static class AffixPolicy
{
    private static readonly HashSet<string> Increase = new() { "Block", "Heal", "Damage", "MaxHp", "Energy", "GainEnergy", "StrengthPower", "DexterityPower", "FocusPower", "ThornsPower", "PoisonPower", "VulnerablePower", "WeakPower", "VigorPower", "PlatingPower", "SelfStrength", "ExtraDamage", "DamageMinimum", "Lightning", "Dark", "Summon", "OrbCount", "Momentum", "Swift", "SwiftAmount", "NimbleAmount", "SharpAmount", "Shivs", "Stars", "Forge", "Wishes", "Sacrifices", "Repeat", "Increase", "BlockNextTurn", "StartOfCombat", "StartOfTurn", "HpLossReduction", "PotionSlots", "Potions", "Discount", "Gold", "Relics", "Cards" };

    private static readonly HashSet<string> Decrease = new() { "MaxHpLoss", "HpLoss", "LoseEnergy", "EnemyStrength", "Curses", "DazedCount" };

    // per-relic (relicId, varName) -> direction override. Verified from decompiled
    // effect code: for these relics a LOWER Cards value is BETTER — 'every N' counters
    // (% Cards), a skills threshold (>= Cards, TuningFork), a heal divisor (deck/Cards,
    // EternalFeather), or a draw penalty (draw - Cards, BigMushroom).
    private static readonly Dictionary<(string, string), AffixDir> Overrides = new()
    {
        [("Kusarigama", "Cards")] = AffixDir.Decrease,
        [("IronClub", "Cards")] = AffixDir.Decrease,
        [("Kunai", "Cards")] = AffixDir.Decrease,
        [("Nunchaku", "Cards")] = AffixDir.Decrease,
        [("LetterOpener", "Cards")] = AffixDir.Decrease,
        [("Shuriken", "Cards")] = AffixDir.Decrease,
        [("OrnamentalFan", "Cards")] = AffixDir.Decrease,
        [("BookOfFiveRings", "Cards")] = AffixDir.Decrease,
        [("TuningFork", "Cards")] = AffixDir.Decrease,
        [("EternalFeather", "Cards")] = AffixDir.Decrease,
        [("BigMushroom", "Cards")] = AffixDir.Decrease,
        // BlessedAntler's Cards is how many DAZED status cards it adds to your deck — a
        // downside. Fewer is better, so it flips to DECREASE (its Energy var stays a boon).
        [("BlessedAntler", "Cards")] = AffixDir.Decrease,

        // Activation-condition thresholds (normally SKIP): a prefix now changes how
        // easily these one-time/conditional relics trigger. Direction = toward more
        // frequent triggering. Integer thresholds only; HP% thresholds deferred (clamp).
        [("Pocketwatch", "CardThreshold")] = AffixDir.Increase,   // played <= N cards -> draw
        [("IvoryTile", "EnergyThreshold")] = AffixDir.Decrease,   // card costs >= N energy
        [("TheBoot", "DamageThreshold")] = AffixDir.Increase,     // hit < N -> boosted
        [("StoneCalendar", "DamageTurn")] = AffixDir.Decrease,    // fires on turn N

        // Too strong when forged — leave these untouched:
        //  BeatingRemnant's whole cost is the 20 max-HP loss; shrinking it (e.g. ->8)
        //  makes an already-strong relic nearly free. DiamondDiadem's card-count
        //  condition is build-defining; easing it snowballs. Both are single-var, so
        //  SKIP effectively makes them non-forgeable (no prefix).
        [("BeatingRemnant", "MaxHpLoss")] = AffixDir.Skip,
        [("DiamondDiadem", "CardThreshold")] = AffixDir.Skip,

        // Card-discover relics are all forge-able again: HeftyTablet/Toolbox go through the
        // capped FromChooseACardScreen but CardSelectCapPatch reroutes an over-offer to the
        // uncapped grid picker; ChoicesParadox/SeaGlass already use the grid natively. So
        // none of them are skipped anymore — forging their count just offers more to pick from.
    };

    // Per-relic power multiplier. For a tiny-base var (ForgottenSoul Damage 1) proportional
    // rounding barely moves it, so a bigger factor gives graded steps across prefixes.
    // The "Fake" relics are intentionally weak versions with small values — boost them
    // harder so forging makes them worthwhile. 1.0 = normal.
    private static readonly Dictionary<string, double> BoostFactor = new()
    {
        ["ForgottenSoul"] = 5.0,
        ["FakeAnchor"] = 3.0,
        ["FakeBloodVial"] = 3.0,
        ["FakeLeesWaffle"] = 3.0,
        ["FakeMango"] = 3.0,
        ["FakeOrichalcum"] = 3.0,
        ["FakeStrikeDummy"] = 3.0,
        ["FakeHappyFlower"] = 3.0,
        ["FakeVenerableTeaSet"] = 3.0,
    };

    // Vars that never receive the per-relic boost (kept at normal scaling). Energy is
    // powerful/swingy, so a boosted fake's energy value stays as-is per design.
    private static readonly HashSet<string> NoBoostVars = new() { "Energy", "GainEnergy", "LoseEnergy" };

    public static double BoostFor(string relicId, string varName)
        => NoBoostVars.Contains(varName) ? 1.0 : BoostFactor.GetValueOrDefault(relicId, 1.0);

    public static AffixDir DirectionFor(string relicId, string varName)
    {
        if (Overrides.TryGetValue((relicId, varName), out var dir)) return dir;
        if (Increase.Contains(varName)) return AffixDir.Increase;
        if (Decrease.Contains(varName)) return AffixDir.Decrease;
        return AffixDir.Skip;
    }
}

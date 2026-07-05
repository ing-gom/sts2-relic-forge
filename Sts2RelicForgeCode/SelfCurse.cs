using MegaCrit.Sts2.Core.Localization;

namespace Sts2RelicForge;

/// <summary>
/// One SELF-CURSE: an independent "저주" a forged relic may carry ON TOP of its prefix and any
/// enemy-rider curse. Where the enemy-rider curse strengthens elites/bosses, a self-curse punishes
/// the OWNER — it fires each time the player takes UNBLOCKED damage (block failed), proportional to
/// the hit count (see <see cref="UnblockedHitPenaltyPatch"/>). Its own roll dimension, so it neither
/// competes in the prefix pool nor shares the enemy-rider slot.
/// </summary>
internal sealed class SelfCurseDef
{
    public string En = "", Ko = "", Zh = "";        // curse name (English key + localized)
    public string Color = "#c0554d";                // red-ish tint for the tooltip line
    public string OnHitPower = "";                  // "Weak" / "Frail" / "Vulnerable" — 1 to self per hit
    public bool OnHitCard;                          // instead: add a Dazed to the draw pile per hit
    public bool OnHitRandom;                        // instead: a random one of Weak / Frail / Vulnerable
    public string EffKo = "", EffEn = "", EffZh = ""; // short "on unblocked hit …" line

    public string Display => Localize(Ko, Zh, En);
    public string Effect => Localize(EffKo, EffZh, EffEn);

    private static string Localize(string ko, string zh, string en)
    {
        string lang = LocManager.Instance?.Language ?? "";
        if (lang.StartsWith("ko") && ko.Length > 0) return ko;
        if (lang.StartsWith("zh") && zh.Length > 0) return zh;
        return en;
    }
}

/// <summary>The self-curse pool (all player-side on-hit penalties) and a uniform deterministic pick.</summary>
internal static class SelfCurseTable
{
    public static readonly SelfCurseDef[] All =
    {
        new SelfCurseDef { En = "Enfeebling",  Ko = "무력의", Zh = "孱弱的", Color = "#bf5a5a", OnHitPower = "Weak",
            EffKo = "막지 못한 피격마다 자신에게 약화 1", EffEn = "Weak 1 to self on each unblocked hit", EffZh = "每次未格挡受击时给予自己1虚弱" },
        new SelfCurseDef { En = "Cracking",    Ko = "균열의", Zh = "龟裂的", Color = "#a8926a", OnHitPower = "Frail",
            EffKo = "막지 못한 피격마다 자신에게 손상 1", EffEn = "Frail 1 to self on each unblocked hit", EffZh = "每次未格挡受击时给予自己1脆弱" },
        new SelfCurseDef { En = "Vulnerating", Ko = "취약의", Zh = "易伤的", Color = "#c8704d", OnHitPower = "Vulnerable",
            EffKo = "막지 못한 피격마다 자신에게 취약 1", EffEn = "Vulnerable 1 to self on each unblocked hit", EffZh = "每次未格挡受击时给予自己1易伤" },
        new SelfCurseDef { En = "Bewildering", Ko = "혼미의", Zh = "眩乱的", Color = "#6a7a8a", OnHitCard = true,
            EffKo = "막지 못한 피격마다 뽑을 더미에 현기증 1장", EffEn = "Adds a Dazed to your draw pile on each unblocked hit", EffZh = "每次未格挡受击时将1张眩晕加入抽牌堆" },
        new SelfCurseDef { En = "Wretched",    Ko = "비참의", Zh = "悲惨的", Color = "#8a5b7f", OnHitRandom = true,
            EffKo = "막지 못한 피격마다 자신에게 약화·손상·취약 중 무작위 1", EffEn = "A random Weak / Frail / Vulnerable 1 to self on each unblocked hit", EffZh = "每次未格挡受击时给予自己随机1层虚弱/脆弱/易伤" },
    };

    public static SelfCurseDef? ByKey(string en)
    {
        foreach (var c in All) if (c.En == en) return c;
        return null;
    }

    /// <summary>Uniform deterministic pick from a 0..1 roll (matches RiderSuffix.Pick).</summary>
    public static SelfCurseDef Pick(double roll)
    {
        int i = (int)(roll * All.Length);
        if (i >= All.Length) i = All.Length - 1;
        if (i < 0) i = 0;
        return All[i];
    }

    public static string Localize(string en) => ByKey(en)?.Display ?? en;

    /// <summary>Localized "on unblocked hit …" line for a stored curse key (empty if unknown).</summary>
    public static string EffectOf(string en) => ByKey(en)?.Effect ?? "";
}

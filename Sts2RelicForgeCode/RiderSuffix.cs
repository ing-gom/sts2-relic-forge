using System.Collections.Generic;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2RelicForge;

/// <summary>
/// The enemy-rider "curse" suffix pool. Each suffix is BOTH a flavor name shown on the relic
/// ("Legendary Anchor of Malice") AND a concrete enemy buff — it maps to one enemy prefix
/// (see <see cref="EnemyPrefixTable"/>), so the relic tooltip can state exactly what elites/bosses
/// gain. Picked deterministically at forge time.
/// </summary>
internal sealed class RiderSuffixDef
{
    public string En = "", Ko = "", Zh = "";       // suffix name
    public string Color = "#e0554d";               // tint for the enemy nameplate
    public string PrefixName = "";                 // the EnemyPrefix this grants to enemies (empty for on-hit riders)
    public string EffKo = "", EffEn = "", EffZh = ""; // short "enemies gain X" line

    public string Display
    {
        get
        {
            string lang = LocManager.Instance?.Language ?? "";
            if (lang.StartsWith("ko") && Ko.Length > 0) return Ko;
            if (lang.StartsWith("zh") && Zh.Length > 0) return Zh;
            return En;
        }
    }

    public string Effect
    {
        get
        {
            string lang = LocManager.Instance?.Language ?? "";
            if (lang.StartsWith("ko") && EffKo.Length > 0) return EffKo;
            if (lang.StartsWith("zh") && EffZh.Length > 0) return EffZh;
            return EffEn;
        }
    }
}

internal static class RiderSuffix
{
    public static readonly RiderSuffixDef[] All =
    {
        new RiderSuffixDef { En = "Wrath",      Ko = "재앙", Zh = "灾祸", Color = "#ff5533", PrefixName = "Vicious",
            EffKo = "적이 힘을 얻습니다",           EffEn = "Enemies gain Strength",    EffZh = "敌人获得力量" },
        new RiderSuffixDef { En = "Malice",     Ko = "악의", Zh = "恶意", Color = "#9fb2c9", PrefixName = "Armored",
            EffKo = "적이 판금을 얻습니다", EffEn = "Enemies gain Plated Armor", EffZh = "敌人获得镀甲" },
        new RiderSuffixDef { En = "Spite",      Ko = "원한", Zh = "怨恨", Color = "#7ed957", PrefixName = "Spiny",
            EffKo = "적이 가시를 얻습니다",     EffEn = "Enemies gain Thorns",       EffZh = "敌人获得荆棘" },
        new RiderSuffixDef { En = "the Tyrant", Ko = "폭군", Zh = "暴君", Color = "#ff8000", PrefixName = "Legendary",
            EffKo = "적이 힘·판금·가시를 얻습니다", EffEn = "Enemies gain Strength, Plated Armor & Thorns", EffZh = "敌人获得力量·镀甲·荆棘" },
        new RiderSuffixDef { En = "Bloodlust",  Ko = "피",   Zh = "血",   Color = "#c0335a", PrefixName = "Regenerating",
            EffKo = "적이 매 턴 50% 확률로 재생 3을 얻습니다", EffEn = "Enemies gain Regen 3 each turn (50% chance)", EffZh = "敌人每回合有50%概率获得3层再生" },
        new RiderSuffixDef { En = "Warding",    Ko = "인공물", Zh = "守护", Color = "#ffd23f", PrefixName = "Warded",
            EffKo = "적이 인공물을 얻습니다", EffEn = "Enemies gain Artifact", EffZh = "敌人获得神器" },
        new RiderSuffixDef { En = "Shielding",  Ko = "버퍼", Zh = "护盾", Color = "#7ed0ff", PrefixName = "Shielded",
            EffKo = "적이 버퍼를 얻습니다", EffEn = "Enemies gain Buffer", EffZh = "敌人获得缓冲" },
        new RiderSuffixDef { En = "Frenzy",     Ko = "광란", Zh = "狂乱", Color = "#ff6b4d", PrefixName = "Frenzied",
            EffKo = "적이 3번째 턴마다 힘을 얻습니다", EffEn = "Enemies gain Strength every 3rd turn", EffZh = "敌人每第3回合获得力量" },

        // --- Max-HP curses: strengthen enemies by raising their Max HP (and healing to it), scoped by
        //     room type. Applied to ALL enemies in a matching fight (see EnemyForge.ApplyHpCurses),
        //     not just the one decorated elite/boss — so "normal-mob HP up" reaches every enemy. ---
        new RiderSuffixDef { En = "Vigor",     Ko = "활력", Zh = "活力", Color = "#c0335a", PrefixName = "Vigor",
            EffKo = "일반 전투의 모든 적이 최대 체력을 얻습니다", EffEn = "Normal-fight enemies gain Max HP", EffZh = "普通战斗的所有敌人获得最大生命" },
        new RiderSuffixDef { En = "Girth",     Ko = "비대", Zh = "臃肿", Color = "#a03a5a", PrefixName = "Girth",
            EffKo = "모든 적이 최대 체력을 얻습니다", EffEn = "All enemies gain Max HP", EffZh = "所有敌人获得最大生命" },
        new RiderSuffixDef { En = "the Titan", Ko = "거인", Zh = "巨人", Color = "#c04d33", PrefixName = "Titan",
            EffKo = "엘리트가 최대 체력을 크게 얻습니다", EffEn = "Elites gain a lot of Max HP", EffZh = "精英获得大量最大生命" },
        new RiderSuffixDef { En = "Eternity",  Ko = "영겁", Zh = "永恒", Color = "#7a5ac0", PrefixName = "Eternity",
            EffKo = "보스가 최대 체력을 얻습니다", EffEn = "Bosses gain Max HP", EffZh = "首领获得最大生命" },
    };

    /// <summary>Deterministic pick from a 0..1 roll; returns the English key stored on the record.</summary>
    public static string Pick(double roll)
    {
        int i = (int)(roll * All.Length);
        if (i >= All.Length) i = All.Length - 1;
        if (i < 0) i = 0;
        return All[i].En;
    }

    public static RiderSuffixDef? ByKey(string en)
    {
        foreach (var s in All) if (s.En == en) return s;
        return null;
    }

    /// <summary>Loose lookup for the test console: matches English (with/without "the "), Korean, or Chinese.</summary>
    public static RiderSuffixDef? Find(string name)
    {
        foreach (var s in All)
        {
            string en = s.En.StartsWith("the ") ? s.En.Substring(4) : s.En;
            if (string.Equals(s.En, name, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(en, name, System.StringComparison.OrdinalIgnoreCase)
                || s.Ko == name || s.Zh == name) return s;
        }
        return null;
    }

    public static string Localize(string en) => ByKey(en)?.Display ?? en;

    /// <summary>Localized "enemies gain X" line for a stored suffix key (empty if unknown).</summary>
    public static string EffectOf(string en) => ByKey(en)?.Effect ?? "";
}

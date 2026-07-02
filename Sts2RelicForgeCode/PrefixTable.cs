using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Random;

namespace Sts2RelicForge;

/// <summary>One Terraria-style prefix: a name and the power % it applies to the relic.</summary>
internal sealed class Prefix
{
    public string Name = "";   // English (canonical / default fallback)
    public string Ko = "";     // Korean (Terraria KR name)
    public string Zh = "";     // Chinese Simplified (Terraria zh name)
    public double PowerPct;     // 0.60 = +60% stronger, -0.10 = 10% weaker
    public double Weight;       // relative roll odds (Terraria reforge = weighted uniform pool)
    public string Color = "#e0b64d"; // tier tint for the ⚒ header (BBCode [color=#hex]); the
                                //   MegaRichTextLabel description renders it. Title can't be
                                //   colored — the tooltip title is a plain MegaLabel.
    public bool Amplify;        // if true, RAISE every var's raw magnitude regardless of
                                // benefit direction (a mixed relic like Brimstone gets both
                                // its self-strength AND the enemy-strength downside amplified)

    // --- Companion prefix (grafts another relic's whole effect onto the host) ---
    // When CompanionRelic is set, this prefix does NOT scale the host's vars; instead the
    // forge grants a HIDDEN instance of CompanionRelic to the player (Player.AddRelicInternal
    // silent), so that relic's native hooks fire on the owner. NoteXx describes the grafted
    // effect for the tooltip (there are no var-change deltas to show).
    public Type? CompanionRelic;
    public string NoteKo = "";
    public string NoteEn = "";
    public string NoteZh = "";

    /// <summary>Name in the game's current language (ko / zh_Hans), else English.</summary>
    public string Display => Localize(Ko, Zh, Name);

    /// <summary>Grafted-effect note in the current language (companion prefixes only).</summary>
    public string NoteDisplay => Localize(NoteKo, NoteZh, NoteEn);

    private static string Localize(string ko, string zh, string en)
    {
        string lang = LocManager.Instance?.Language ?? "";
        if (lang.StartsWith("ko") && ko.Length > 0) return ko;
        if (lang.StartsWith("zh") && zh.Length > 0) return zh;
        return en;
    }
}

/// <summary>
/// Single universal prefix pool, adapted from Terraria's item modifiers. Magnitudes track
/// Terraria's value multipliers, scaled down to fit relics (Legendary is the rare top,
/// down through Keen). Negatives are included but SOFTENED — low weight (~12% total) and
/// small magnitude — so a bad roll only slightly weakens a relic. A relic's rarity does
/// NOT change the pool or magnitude (Terraria-style: any item can roll any prefix); rarity
/// only gates eligibility (Starter/Event never get a prefix).
///
/// NOTE: names are English Terraria terms for now; localization (incl. Korean) is deferred
/// until the prefix set is settled.
/// </summary>
internal static class PrefixTable
{
    public static readonly Prefix[] All =
    {
        // positive — top is rare, low tiers common. Color grades by power: gold/orange at the
        // top, cooling through green → blue for the low tiers.
        new Prefix { Name = "Legendary", Ko = "전설적인",   Zh = "传奇的",   PowerPct =  0.60, Weight =  2, Color = "#ff8000" },
        new Prefix { Name = "Godly",     Ko = "신성한",     Zh = "神级的",   PowerPct =  0.35, Weight =  4, Color = "#ffd23f" },
        new Prefix { Name = "Demonic",   Ko = "악마의",     Zh = "恶魔的",   PowerPct =  0.25, Weight =  6, Color = "#c04dff" },
        new Prefix { Name = "Superior",  Ko = "훌륭한",     Zh = "高级的",   PowerPct =  0.18, Weight =  9, Color = "#4dd24d" },
        new Prefix { Name = "Forceful",  Ko = "강력한",     Zh = "强力的",   PowerPct =  0.12, Weight = 12, Color = "#7ed957" },
        new Prefix { Name = "Hurtful",   Ko = "고통스러운", Zh = "致伤的",   PowerPct =  0.08, Weight = 15, Color = "#a7e34d" },
        new Prefix { Name = "Zealous",   Ko = "열성적인",   Zh = "狂热的",   PowerPct =  0.06, Weight = 15, Color = "#4db8ff" },
        new Prefix { Name = "Keen",      Ko = "날카로운",   Zh = "锐利的",   PowerPct =  0.04, Weight = 15, Color = "#9fd8ff" },
        // amplify — raises EVERY var's raw magnitude (both boons and downsides). On a
        // single-boon relic it's just a strong buff; on a mixed relic (Brimstone: self +
        // enemy strength) it's a high-risk/high-reward trade-off. Power ~= Demonic. Volatile
        // orange-red marks the risk.
        new Prefix { Name = "Volatile",  Ko = "불안정한",   Zh = "不稳定的", PowerPct =  0.25, Weight =  6, Amplify = true, Color = "#ff5c33" },
        // negative — magnitudes matched to Forceful/Superior/Demonic (−12/−18/−25%), ~26% total
        // roll chance. Dull gray darkening to a broken red.
        new Prefix { Name = "Damaged",   Ko = "금이 간",    Zh = "破损的",   PowerPct = -0.12, Weight = 14, Color = "#b0b0b0" },
        new Prefix { Name = "Shoddy",    Ko = "하찮은",     Zh = "粗劣的",   PowerPct = -0.18, Weight =  8, Color = "#8f8f8f" },
        new Prefix { Name = "Broken",    Ko = "부서진",     Zh = "碎裂的",   PowerPct = -0.25, Weight =  5, Color = "#e0554d" },

        // --- Companion prefixes ---
        // Each grafts a FIXED donor relic's whole effect onto ANY host relic, regardless of
        // the host's own vars (so var-less relics can roll these too). Themed name, not the
        // donor's name. The donor is granted as a hidden instance so its native hooks fire;
        // the effect is surfaced on the HOST tooltip via NoteXx. All donors are hook-driven
        // (not on-pickup) and benign/moderate — verified against decompiled effect code.
        // Weights: rare-ish treats, tuned by power (bigger effect → lower weight).
        new Prefix { Name = "Thorned", Ko = "가시돋친", Zh = "尖刺的", Weight = 9, Color = "#7ed957",
            CompanionRelic = typeof(BronzeScales),
            NoteKo = "전투 시작 시 가시 3", NoteEn = "Thorns 3 at combat start", NoteZh = "战斗开始时获得3荆棘" },
        new Prefix { Name = "Mighty", Ko = "강건한", Zh = "强壮的", Weight = 6, Color = "#ff6b4d",
            CompanionRelic = typeof(Vajra),
            NoteKo = "전투 시작 시 힘 +1", NoteEn = "Strength +1 at combat start", NoteZh = "战斗开始时获得1力量" },
        new Prefix { Name = "Quicksilver", Ko = "수은의", Zh = "水银的", Weight = 6, Color = "#c0c8d8",
            CompanionRelic = typeof(MercuryHourglass),
            NoteKo = "매 턴 모든 적에게 3 피해", NoteEn = "3 damage to all enemies each turn", NoteZh = "每回合对所有敌人造成3点伤害" },
        new Prefix { Name = "Anchored", Ko = "닻내린", Zh = "沉稳的", Weight = 7, Color = "#4db8ff",
            CompanionRelic = typeof(Anchor),
            NoteKo = "전투 시작 시 블록 10", NoteEn = "Block 10 at combat start", NoteZh = "战斗开始时获得10格挡" },
        new Prefix { Name = "Vital", Ko = "피끓는", Zh = "血涌的", Weight = 8, Color = "#ff5c8a",
            CompanionRelic = typeof(BloodVial),
            NoteKo = "첫 턴에 체력 2 회복", NoteEn = "Heal 2 on turn 1", NoteZh = "第1回合回复2点生命" },
        new Prefix { Name = "Rhythmic", Ko = "규칙적인", Zh = "规律的", Weight = 5, Color = "#ffd23f",
            CompanionRelic = typeof(HappyFlower),
            NoteKo = "3턴마다 에너지 +1", NoteEn = "+1 energy every 3 turns", NoteZh = "每3回合获得1点能量" },
        new Prefix { Name = "Insightful", Ko = "통찰의", Zh = "洞察的", Weight = 7, Color = "#c04dff",
            CompanionRelic = typeof(CentennialPuzzle),
            NoteKo = "전투 중 첫 피격 시 카드 3장 드로우", NoteEn = "Draw 3 cards when first hit", NoteZh = "战斗中首次受击时抓3张牌" },
        new Prefix { Name = "Intimidating", Ko = "위협적인", Zh = "威慑的", Weight = 8, Color = "#9b6bff",
            CompanionRelic = typeof(BagOfMarbles),
            NoteKo = "전투 시작 시 모든 적에게 취약 1", NoteEn = "Vulnerable 1 to all enemies at combat start", NoteZh = "战斗开始时对所有敌人施加1易伤" },
    };

    // Rarities that can receive a prefix at all. Starter/Event/None never do.
    public static readonly HashSet<RelicRarity> Eligible = new()
    {
        RelicRarity.Common, RelicRarity.Uncommon, RelicRarity.Rare, RelicRarity.Shop, RelicRarity.Ancient
    };

    /// <summary>Weighted, deterministic pick from the pool (Terraria reforge style).</summary>
    public static Prefix Roll(Rng rng)
    {
        double total = 0;
        foreach (var p in All) total += p.Weight;
        double r = rng.NextFloat() * total;
        foreach (var p in All)
        {
            r -= p.Weight;
            if (r < 0) return p;
        }
        return All[All.Length - 1];
    }

    /// <summary>Find a prefix by name (case-insensitive) for the test console command.</summary>
    public static Prefix? ByName(string name)
    {
        foreach (var p in All)
            if (string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }

    /// <summary>Localized display name for a stored (English) prefix name.</summary>
    public static string Localize(string englishName) => ByName(englishName)?.Display ?? englishName;

    /// <summary>Tier tint (BBCode hex) for a stored (English) prefix name; gold fallback.</summary>
    public static string ColorOf(string englishName) => ByName(englishName)?.Color ?? "#e0b64d";
}

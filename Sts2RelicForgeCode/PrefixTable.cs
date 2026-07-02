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

    // Delayed companion: if > 0, this prefix does NOT graft a relic; instead a fixed effect
    // (see DelayedCompanionPatch) is applied on this combat turn. Used to weaken min-1 effects
    // that can't be scaled down — a later trigger is strictly worse than combat-start.
    public int DelayTurn;

    // Penalty (curse) prefix: a pure downside applied to the player on some trigger
    // (see PenaltyCompanionPatch). Grafts nothing; its NoteXx shows in red on the tooltip.
    public bool Penalty;

    /// <summary>True for any companion-family prefix (grafts a relic, delays an effect, or is a penalty).</summary>
    public bool IsCompanionPrefix => CompanionRelic != null || DelayTurn > 0 || Penalty;

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
        // Grafted companions — the donor's effect is granted at a REDUCED magnitude (×0.6,
        // floor 1) so it stays weaker than owning the real relic. Notes state the reduced value.
        new Prefix { Name = "Thorned", Ko = "가시돋친", Zh = "尖刺的", Weight = 9, Color = "#7ed957",
            CompanionRelic = typeof(BronzeScales),
            NoteKo = "전투 시작 시 가시 2", NoteEn = "Thorns 2 at combat start", NoteZh = "战斗开始时获得2荆棘" },
        new Prefix { Name = "Quicksilver", Ko = "수은의", Zh = "水银的", Weight = 6, Color = "#c0c8d8",
            CompanionRelic = typeof(MercuryHourglass),
            NoteKo = "매 턴 모든 적에게 2 피해", NoteEn = "2 damage to all enemies each turn", NoteZh = "每回合对所有敌人造成2点伤害" },
        new Prefix { Name = "Anchored", Ko = "닻내린", Zh = "沉稳的", Weight = 7, Color = "#4db8ff",
            CompanionRelic = typeof(Anchor),
            NoteKo = "전투 시작 시 블록 6", NoteEn = "Block 6 at combat start", NoteZh = "战斗开始时获得6格挡" },
        new Prefix { Name = "Vital", Ko = "피끓는", Zh = "血涌的", Weight = 8, Color = "#ff5c8a",
            CompanionRelic = typeof(BloodVial),
            NoteKo = "첫 턴에 체력 1 회복", NoteEn = "Heal 1 on turn 1", NoteZh = "第1回合回复1点生命" },
        new Prefix { Name = "Rhythmic", Ko = "규칙적인", Zh = "规律的", Weight = 5, Color = "#ffd23f",
            CompanionRelic = typeof(HappyFlower),
            NoteKo = "4턴마다 에너지 +1", NoteEn = "+1 energy every 4 turns", NoteZh = "每4回合获得1点能量" },
        new Prefix { Name = "Insightful", Ko = "통찰의", Zh = "洞察的", Weight = 7, Color = "#c04dff",
            CompanionRelic = typeof(CentennialPuzzle),
            NoteKo = "전투 중 첫 피격 시 카드 2장 드로우", NoteEn = "Draw 2 cards when first hit", NoteZh = "战斗中首次受击时抓2张牌" },
        // Delayed companions — no graft; a fixed min-1 effect applies LATER than the original
        // relic (turn 2/3 instead of combat start), so it's strictly weaker. See DelayedCompanionPatch.
        new Prefix { Name = "Mighty", Ko = "강건한", Zh = "强壮的", Weight = 6, Color = "#ff6b4d",
            DelayTurn = 3,
            NoteKo = "3번째 턴에 힘 +1", NoteEn = "Strength +1 on turn 3", NoteZh = "第3回合获得1力量" },
        new Prefix { Name = "Intimidating", Ko = "위협적인", Zh = "威慑的", Weight = 8, Color = "#9b6bff",
            DelayTurn = 2,
            NoteKo = "2번째 턴에 모든 적에게 취약 1", NoteEn = "Vulnerable 1 to all enemies on turn 2", NoteZh = "第2回合对所有敌人施加1易伤" },

        // --- 2nd batch (all weakened vs the real relic: reduced value, longer interval, or delay) ---
        new Prefix { Name = "Ferocious", Ko = "사나운", Zh = "凶猛的", Weight = 5, Color = "#ff5533",
            CompanionRelic = typeof(Akabeko),   // Vigor 8 -> 5 (×0.6)
            NoteKo = "첫 턴에 활력 5", NoteEn = "Vigor 5 on turn 1", NoteZh = "第1回合获得5鼓舞" },
        new Prefix { Name = "Bladed", Ko = "칼날의", Zh = "锋刃的", Weight = 6, Color = "#d9d9e0",
            CompanionRelic = typeof(LetterOpener),   // Damage 5->3 (×0.6), interval 3->4 (VarOverride) — per-turn counter
            NoteKo = "한 턴에 스킬 4회마다 모든 적에게 3 피해", NoteEn = "3 damage to all enemies per 4 skills in one turn", NoteZh = "一回合内每4张技能牌对所有敌人造成3点伤害" },
        new Prefix { Name = "Relentless", Ko = "연격의", Zh = "连击的", Weight = 5, Color = "#ff8c42",
            CompanionRelic = typeof(Shuriken),   // Strength +1, interval 3->4 (VarOverride) — per-turn counter
            NoteKo = "한 턴에 공격 4회마다 힘 +1", NoteEn = "Strength +1 per 4 attacks in one turn", NoteZh = "一回合内每4张攻击牌获得1力量" },
        new Prefix { Name = "Tempered", Ko = "단단한", Zh = "淬火的", Weight = 7, Color = "#5a9fd4",
            CompanionRelic = typeof(Orichalcum),   // Block 6 -> 4 (×0.6)
            NoteKo = "턴 종료 시 블록이 없으면 블록 4", NoteEn = "Block 4 if you end your turn with no Block", NoteZh = "回合结束时若无格挡则获得4格挡" },
        new Prefix { Name = "Gusting", Ko = "질풍의", Zh = "疾风的", Weight = 7, Color = "#7ed9e0",
            CompanionRelic = typeof(OrnamentalFan),   // Block 4->2 (×0.6), interval 3->4 (VarOverride) — per-turn counter
            NoteKo = "한 턴에 공격 4회마다 블록 2", NoteEn = "Block 2 per 4 attacks in one turn", NoteZh = "一回合内每4张攻击牌获得2格挡" },
        new Prefix { Name = "Darting", Ko = "표창의", Zh = "迅捷的", Weight = 6, Color = "#6ee0a0",
            CompanionRelic = typeof(Kunai),   // Dexterity +1, interval 3->4 (VarOverride) — per-turn counter
            NoteKo = "한 턴에 공격 4회마다 민첩 +1", NoteEn = "Dexterity +1 per 4 attacks in one turn", NoteZh = "一回合内每4张攻击牌获得1敏捷" },
        new Prefix { Name = "Supple", Ko = "유연한", Zh = "柔韧的", Weight = 7, Color = "#6ed9c0",
            DelayTurn = 2,   // Oddly Smooth Stone's Dex +1, delayed to turn 2 instead of combat start
            NoteKo = "2번째 턴에 민첩 +1", NoteEn = "Dexterity +1 on turn 2", NoteZh = "第2回合获得1敏捷" },
        new Prefix { Name = "Accelerating", Ko = "가속의", Zh = "加速的", Weight = 5, Color = "#ffcf3f",
            CompanionRelic = typeof(Nunchaku),   // Energy +1, interval 10->12 (VarOverride)
            NoteKo = "공격 12회마다 에너지 +1", NoteEn = "+1 energy every 12 attacks", NoteZh = "每12张攻击牌获得1点能量" },

        // --- Penalty (curse) prefixes: pure downside, low weight (see PenaltyCompanionPatch) ---
        new Prefix { Name = "Cursed", Ko = "저주받은", Zh = "被诅咒的", Weight = 8, Penalty = true, Color = "#b0554d",
            NoteKo = "전투 시작 시 자신에게 약화 1", NoteEn = "Weak 1 to self at combat start", NoteZh = "战斗开始时给予自己1虚弱" },
        new Prefix { Name = "Cumbersome", Ko = "무거운", Zh = "笨重的", Weight = 8, Penalty = true, Color = "#8f8f8f",
            NoteKo = "첫 턴에 자신 민첩 -1", NoteEn = "Dexterity -1 to self on turn 1", NoteZh = "第1回合自身敏捷-1" },
        new Prefix { Name = "Fickle", Ko = "변덕스러운", Zh = "善变的", Weight = 6, Penalty = true, Color = "#9a6b8f",
            NoteKo = "매 턴 25% 확률로 자신에게 랜덤 디버프 1", NoteEn = "25% each turn: a random debuff 1 to self", NoteZh = "每回合25%概率给予自己1个随机减益" },
        new Prefix { Name = "Overloaded", Ko = "과부하", Zh = "超载的", Weight = 6, Penalty = true, Color = "#a0605a",
            NoteKo = "한 턴에 카드 6장 사용 시 자신에게 취약 1", NoteEn = "Vulnerable 1 to self after 6 cards in one turn", NoteZh = "一回合内打出6张牌后给予自己1易伤" },

        // --- Card-insertion penalties: shove a status card into a combat pile (see PenaltyCompanionPatch) ---
        new Prefix { Name = "Tainted", Ko = "오염된", Zh = "污秽的", Weight = 5, Penalty = true, Color = "#7a8a5a",
            NoteKo = "매 턴 뽑을 더미에 현기증 1장", NoteEn = "Adds a Dazed to your draw pile each turn", NoteZh = "每回合将1张眩晕加入抽牌堆" },
        new Prefix { Name = "Festering", Ko = "곪은", Zh = "溃烂的", Weight = 5, Penalty = true, Color = "#8a6a4a",
            NoteKo = "전투 시작 시 버린 더미에 상처 2장", NoteEn = "Adds 2 Wounds to your discard at combat start", NoteZh = "战斗开始时将2张伤口加入弃牌堆" },
        new Prefix { Name = "Smoldering", Ko = "불타는", Zh = "阴燃的", Weight = 5, Penalty = true, Color = "#c0603a",
            NoteKo = "전투 시작 시 뽑을 더미에 화상 1장", NoteEn = "Adds a Burn to your draw pile at combat start", NoteZh = "战斗开始时将1张灼烧加入抽牌堆" },
        new Prefix { Name = "Hollow", Ko = "공허한", Zh = "虚空的", Weight = 5, Penalty = true, Color = "#6a5a8a",
            NoteKo = "전투 시작 시 뽑을 더미에 공허 1장", NoteEn = "Adds a Void to your draw pile at combat start", NoteZh = "战斗开始时将1张虚无加入抽牌堆" },
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

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

    // Enemy-strip prefix: each turn, move ONE of the enemies' powers 1 toward zero
    // (see ForgeCombatAffixPatch.StripOne). Chaos — strips enemy buffs (good) AND the
    // debuffs you applied (bad) alike, so it's a gamble, not a pure boon.
    public bool EnemyStrip;

    // Symmetric combat-start prefix: on turn 1, apply SymPower (amount SymAmount) to BOTH the
    // player and every enemy (see ForgeCombatAffixPatch.ApplySymmetric). SymPower is a short
    // key ("Vulnerable" / "Weak"); grafts nothing.
    public string SymPower = "";
    public int SymAmount;

    // Random-debuff prefix: each turn, a 50% chance to apply Vulnerable/Weak 1 to either all
    // enemies or the player (a coin flip on both), see ForgeCombatAffixPatch.ApplyRandomDebuff.
    public bool RandomDebuff;

    // Mixed (gamble) prefix: its note is neither a pure boon nor a curse, so it renders amber
    // (neither green nor red). Set on the enemy-strip and symmetric prefixes.
    public bool Mixed;

    // --- Reactive affixes (see ReactiveAffix + REACTIVE_PREFIXES.md). These graft nothing and
    //     scale no host var; instead they react to the player's power/energy events in combat. ---
    public bool GainAmplify;     // 공명의: when the player gains Strength/Dexterity (>0), gain +1 more
    public bool LossInvert;      // 완강한: an enemy-applied Strength/Dexterity loss becomes a gain
    public int  EnergyDischarge; // 방전의: damage dealt to all enemies per bonus Energy gained (0 = off)

    // --- Run-state affixes (see RunState prefixes below). These graft nothing and scale no host var;
    //     instead they react to the player's GOLD / DECK / CURSE state rather than combat power events. ---
    public bool CurseDrawStrength; // 저주먹은: when the player draws a Curse card, gain 1 Strength
    public int  GoldStrengthPer;   // 황금빛: at combat start, gain 1 Strength per this much gold (0 = off)

    // --- Replay-grant affix (see CharAffix.OnTurnEchoing / CharAffixPatches). Grafts nothing and scales
    //     no host var; each turn it grants Replay 1 (CardModel.BaseReplayCount++, reverted at turn end so
    //     it never accumulates) to a random non-Curse/Status card in hand, and PLAYING that card costs the
    //     player Vulnerable 1 + Frail 1. Mixed (amber note): a strong boon with a built-in cost. ---
    public bool ReplayGrant;

    // Force the enemy-rider curse on unconditionally (bypasses EnemyRiderChance). Used by 공명의 so
    // its strength always comes bundled with a curse — the mod's own cost, in place of a per-trigger
    // penalty. Ignored on penalty prefixes (which never carry a rider).
    public bool AlwaysCurse;

    // --- Character-gated behavioral affix (see CharAffix / CharAffixPatches). Only rolls when the
    //     owning player plays RequiredCharacter, and reacts to that character's signature mechanic
    //     (poison / orbs / summons / stars …). Grafts nothing and scales no host var, so it's a
    //     companion-family prefix; dispatched by Name in the patches. RequiredCharacter is a
    //     CharacterModel Id.Entry (e.g. "SILENT"); "" = universal (every legacy prefix). ---
    public string RequiredCharacter = "";
    public bool CharAffix;

    // --- Fallback prefix (see RelicForgeService.Forge substitution + FallbackBuffPatch). NOT in the
    //     normal roll pool (PrefixTable.InPool excludes it): a magnitude prefix that rounded to NO
    //     change on a relic (too small / var-less) is REPLACED by one of these — a host-independent,
    //     CHANCE-gated minor combat-start buff. The chance is derived from the fizzled prefix's tier
    //     (RelicForgeService.FallbackChanceFor) and stored on the record (ForgeRecord.FallbackPercent),
    //     so the note shows the real odds. FallbackStat names the power granted; scales no host var. ---
    public string FallbackStat = "";   // "" = not a fallback; else "Strength"/"Dexterity"/"Block"/"Thorns"
    public int FallbackAmount;          // how much of the stat the combat-start roll grants
    public bool IsFallback => FallbackStat.Length > 0;

    /// <summary>True for any companion-family prefix (grafts a relic, delays/strips an effect,
    /// applies a symmetric/random effect, is a penalty, is a reactive/character affix, or is a
    /// fallback buff) — none of these scale the host's vars.</summary>
    public bool IsCompanionPrefix => CompanionRelic != null || DelayTurn > 0 || Penalty
                                     || EnemyStrip || SymPower.Length > 0 || RandomDebuff
                                     || GainAmplify || LossInvert || EnergyDischarge > 0
                                     || CurseDrawStrength || GoldStrengthPer > 0 || CharAffix
                                     || ReplayGrant || IsFallback;

    /// <summary>Stable loc-key base derived from the English name (see <see cref="ForgeLoc"/>).</summary>
    internal string LocKeyBase => "PREFIX_" + ForgeLoc.KeyOf(Name);

    /// <summary>Name in the game's current language, via the relic_forge loc table (external
    /// translations honored); English fallback.</summary>
    public string Display => ForgeLoc.Get(LocKeyBase + ".name", Name);

    /// <summary>Grafted-effect note in the current language (companion prefixes only).</summary>
    public string NoteDisplay => ForgeLoc.Get(LocKeyBase + ".note", NoteEn);
}

/// <summary>
/// Single universal prefix pool, adapted from Terraria's item modifiers. Magnitudes track
/// Terraria's value multipliers, scaled down to fit relics (Legendary is the rare top,
/// down through Keen). This mod's identity is a GAMBLE: downside is intentionally substantial
/// (~25% of the pool is a pure penalty/curse — magnitude negatives Damaged/Shoddy/Broken plus
/// the penalty-companion prefixes — with more on the mixed/amber gambles), so a reforge is a
/// real risk, not a free upgrade. A pickup may still roll no prefix (ForgeConfig.NoPrefixChance),
/// but a deliberate reforge always lands one (guaranteePrefix) — the gamble you paid for.
/// A relic's rarity does NOT change the pool or magnitude (Terraria-style: any item can roll any
/// prefix); rarity only gates the automatic pickup forge (Starter/Event never auto-forge).
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
        new Prefix { Name = "Godly",     Ko = "신성한",     Zh = "神级的",   PowerPct =  0.30, Weight =  4, Color = "#ffd23f" },
        new Prefix { Name = "Demonic",   Ko = "악마의",     Zh = "恶魔的",   PowerPct =  0.25, Weight =  6, Color = "#c04dff" },
        new Prefix { Name = "Superior",  Ko = "훌륭한",     Zh = "高级的",   PowerPct =  0.18, Weight =  9, Color = "#4dd24d" },
        new Prefix { Name = "Forceful",  Ko = "강력한",     Zh = "强力的",   PowerPct =  0.12, Weight = 12, Color = "#7ed957" },
        // Low-tier magnitude prefixes weighted DOWN (was 15/15/15): a +4~8% bump rounds to +0 on
        // most relics (62% have a numeric base <= 3), so they were the main source of "empty"
        // reforges. Cutting their weight raises the RELATIVE share of every effect prefix (companion/
        // reactive/character) — which do something regardless of base — without touching any magnitude,
        // so there's zero overbalance risk. See RelicForgeService.Forge (ReforgeFloorMinBase) for the
        // complementary floor that keeps big relics from ever reforging to a literal 0-change.
        new Prefix { Name = "Hurtful",   Ko = "고통스러운", Zh = "致伤的",   PowerPct =  0.08, Weight = 10, Color = "#a7e34d" },
        new Prefix { Name = "Zealous",   Ko = "열성적인",   Zh = "狂热的",   PowerPct =  0.06, Weight =  7, Color = "#4db8ff" },
        new Prefix { Name = "Keen",      Ko = "날카로운",   Zh = "锐利的",   PowerPct =  0.04, Weight =  5, Color = "#9fd8ff" },
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
        // Grafted companions — the donor's effect is granted at a REDUCED magnitude (×0.35,
        // floor 1) so it stays a garnish, well weaker than owning the real relic. Notes state the
        // reduced value; keep them in sync with RelicForgeService.WeakenFactor.
        new Prefix { Name = "Thorned", Ko = "가시돋친", Zh = "尖刺的", Weight = 9, Color = "#7ed957",
            CompanionRelic = typeof(BronzeScales),   // Thorns 3 -> 1 (×0.35; Bronze Scales is 3)
            NoteKo = "전투 시작 시 가시 1", NoteEn = "Thorns 1 at combat start", NoteZh = "战斗开始时获得1荆棘" },
        new Prefix { Name = "Quicksilver", Ko = "수은의", Zh = "水银的", Weight = 6, Color = "#c0c8d8",
            CompanionRelic = typeof(MercuryHourglass),   // damage -> 1 (×0.35)
            NoteKo = "매 턴 모든 적에게 1 피해", NoteEn = "1 damage to all enemies each turn", NoteZh = "每回合对所有敌人造成1点伤害" },
        new Prefix { Name = "Anchored", Ko = "닻내린", Zh = "沉稳的", Weight = 7, Color = "#4db8ff",
            CompanionRelic = typeof(Anchor),   // Block 10 -> 3 (×0.30 floor)
            NoteKo = "전투 시작 시 블록 3", NoteEn = "Block 3 at combat start", NoteZh = "战斗开始时获得3格挡" },
        new Prefix { Name = "Vital", Ko = "피끓는", Zh = "血涌的", Weight = 8, Color = "#ff5c8a",
            CompanionRelic = typeof(BloodVial),   // Heal 2 -> 1 (floor 1)
            NoteKo = "첫 턴에 체력 1 회복", NoteEn = "Heal 1 on turn 1", NoteZh = "第1回合回复1点生命" },
        new Prefix { Name = "Rhythmic", Ko = "규칙적인", Zh = "规律的", Weight = 5, Color = "#ffd23f",
            CompanionRelic = typeof(HappyFlower),   // energy +1, interval 3->4 (VarOverride)
            NoteKo = "4턴마다 에너지 +1", NoteEn = "+1 energy every 4 turns", NoteZh = "每4回合获得1点能量" },
        new Prefix { Name = "Insightful", Ko = "통찰의", Zh = "洞察的", Weight = 7, Color = "#c04dff",
            CompanionRelic = typeof(CentennialPuzzle),   // draw -> 1 (×0.35)
            NoteKo = "전투 중 첫 피격 시 카드 1장 드로우", NoteEn = "Draw 1 card when first hit", NoteZh = "战斗中首次受击时抓1张牌" },
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
            CompanionRelic = typeof(Akabeko),   // Vigor 8 -> 2 (×0.30 floor)
            NoteKo = "첫 턴에 활력 2", NoteEn = "Vigor 2 on turn 1", NoteZh = "第1回合获得2鼓舞" },
        new Prefix { Name = "Bladed", Ko = "칼날의", Zh = "锋刃的", Weight = 6, Color = "#d9d9e0",
            CompanionRelic = typeof(LetterOpener),   // Damage 5->1 (×0.30 floor), interval 3->4 (VarOverride) — per-turn counter
            NoteKo = "한 턴에 스킬 4회마다 모든 적에게 1 피해", NoteEn = "1 damage to all enemies per 4 skills in one turn", NoteZh = "一回合内每4张技能牌对所有敌人造成1点伤害" },
        new Prefix { Name = "Relentless", Ko = "연격의", Zh = "连击的", Weight = 5, Color = "#ff8c42",
            CompanionRelic = typeof(Shuriken),   // Strength +1, interval 3->4 (VarOverride) — per-turn counter
            NoteKo = "한 턴에 공격 4회마다 힘 +1", NoteEn = "Strength +1 per 4 attacks in one turn", NoteZh = "一回合内每4张攻击牌获得1力量" },
        new Prefix { Name = "Tempered", Ko = "단단한", Zh = "淬火的", Weight = 7, Color = "#5a9fd4",
            CompanionRelic = typeof(Orichalcum),   // Block 6 -> 1 (×0.30 floor)
            NoteKo = "턴 종료 시 블록이 없으면 블록 1", NoteEn = "Block 1 if you end your turn with no Block", NoteZh = "回合结束时若无格挡则获得1格挡" },
        new Prefix { Name = "Gusting", Ko = "질풍의", Zh = "疾风的", Weight = 7, Color = "#7ed9e0",
            CompanionRelic = typeof(OrnamentalFan),   // Block 4->1 (×0.35), interval 3->4 (VarOverride) — per-turn counter
            NoteKo = "한 턴에 공격 4회마다 블록 1", NoteEn = "Block 1 per 4 attacks in one turn", NoteZh = "一回合内每4张攻击牌获得1格挡" },
        new Prefix { Name = "Darting", Ko = "표창의", Zh = "迅捷的", Weight = 6, Color = "#6ee0a0",
            CompanionRelic = typeof(Kunai),   // Dexterity +1, interval 3->4 (VarOverride) — per-turn counter
            NoteKo = "한 턴에 공격 4회마다 민첩 +1", NoteEn = "Dexterity +1 per 4 attacks in one turn", NoteZh = "一回合内每4张攻击牌获得1敏捷" },
        new Prefix { Name = "Supple", Ko = "유연한", Zh = "柔韧的", Weight = 7, Color = "#6ed9c0",
            DelayTurn = 2,   // Oddly Smooth Stone's Dex +1, delayed to turn 2 instead of combat start
            NoteKo = "2번째 턴에 민첩 +1", NoteEn = "Dexterity +1 on turn 2", NoteZh = "第2回合获得1敏捷" },
        new Prefix { Name = "Accelerating", Ko = "가속의", Zh = "加速的", Weight = 5, Color = "#ffcf3f",
            CompanionRelic = typeof(Nunchaku),   // Energy +1, interval 10->12 (VarOverride)
            NoteKo = "공격 12회마다 에너지 +1", NoteEn = "+1 energy every 12 attacks", NoteZh = "每12张攻击牌获得1点能量" },

        // --- Penalty prefixes = CURSES (merged concept): a pure self-downside, low weight (see
        //     PenaltyCompanionPatch). Like an enemy-rider / self-curse, rolling one ENDS the reforge
        //     (campfire + shop) and can only be shed with Cleanse — which reverts it to no prefix. ---
        new Prefix { Name = "Cursed", Ko = "저주받은", Zh = "被诅咒的", Weight = 8, Penalty = true, Color = "#b0554d",
            NoteKo = "전투 시작 시 자신에게 약화 1", NoteEn = "Weak 1 to self at combat start", NoteZh = "战斗开始时给予自己1虚弱" },
        new Prefix { Name = "Cumbersome", Ko = "무거운", Zh = "笨重的", Weight = 8, Penalty = true, Color = "#8f8f8f",
            NoteKo = "첫 턴에 자신 민첩 -1", NoteEn = "Dexterity -1 to self on turn 1", NoteZh = "第1回合自身敏捷-1" },
        new Prefix { Name = "Fickle", Ko = "변덕스러운", Zh = "善变的", Weight = 6, Penalty = true, Color = "#9a6b8f",
            NoteKo = "매 턴 25% 확률로 자신에게 랜덤 디버프 1", NoteEn = "25% each turn: a random debuff 1 to self", NoteZh = "每回合25%概率给予自己1个随机减益" },
        new Prefix { Name = "Overloaded", Ko = "과부하", Zh = "超载的", Weight = 6, Penalty = true, Color = "#a0605a",
            NoteKo = "한 턴에 카드 6장 사용 시 자신에게 취약 1", NoteEn = "Vulnerable 1 to self after 6 cards in one turn", NoteZh = "一回合内打出6张牌后给予自己1易伤" },

        // --- Keyword family (grants/inflicts card keywords via the game's own systems) ---
        new Prefix { Name = "Retaining", Ko = "보존의", Zh = "留存的", Weight = 6, Color = "#6ec0d9",
            NoteKo = "턴 종료 시 손에 든 무작위 카드 1장을 보존한다",
            NoteEn = "At the end of your turn, Retain a random card in your hand",
            NoteZh = "回合结束时，随机保留1张手牌" },
        new Prefix { Name = "Searing", Ko = "불사르는", Zh = "灼焚的", Weight = 6, Penalty = true, Color = "#c0704d",
            NoteKo = "카드를 낼 때 25% 확률로 그 카드에 소멸이 부여된다 (다음 사용부터 적용)",
            NoteEn = "When you play a card, 25% chance it gains Exhaust (takes effect from its next play)",
            NoteZh = "打出卡牌时，25%概率使其获得消耗（下次打出时生效）" },
        // Replay-grant (see ReplayGrant): each turn a random hand card gains Replay 1 (single-turn,
        // reverted at turn end), and playing that card costs the player Vulnerable 1 + Frail 1. A strong
        // gamble — you don't choose which card, and the boon is paid for on use. Mixed → amber note.
        new Prefix { Name = "Echoing", Ko = "메아리의", Zh = "回响的", Weight = 3, Mixed = true, ReplayGrant = true, Color = "#a35cff",
            NoteKo = "매 턴, 손에 든 무작위 카드 1장이 재사용 1을 얻는다. 그 카드를 사용하면 자신에게 취약 1·손상 1",
            NoteEn = "Each turn, a random card in your hand gains Replay 1. Playing that card gives you Vulnerable 1 and Frail 1",
            NoteZh = "每回合，手牌中随机1张牌获得1层重演。打出该牌时，给予自己1层易伤和1层脆弱" },

        // --- Card-insertion penalties: shove a status card into a combat pile (see PenaltyCompanionPatch) ---
        new Prefix { Name = "Tainted", Ko = "오염된", Zh = "污秽的", Weight = 5, Penalty = true, Color = "#7a8a5a",
            NoteKo = "매 턴 뽑을 더미에 현기증 1장", NoteEn = "Adds a Dazed to your draw pile each turn", NoteZh = "每回合将1张眩晕加入抽牌堆" },
        new Prefix { Name = "Festering", Ko = "곪은", Zh = "溃烂的", Weight = 5, Penalty = true, Color = "#8a6a4a",
            NoteKo = "전투 시작 시 버린 더미에 상처 2장", NoteEn = "Adds 2 Wounds to your discard at combat start", NoteZh = "战斗开始时将2张伤口加入弃牌堆" },
        new Prefix { Name = "Smoldering", Ko = "불타는", Zh = "阴燃的", Weight = 5, Penalty = true, Color = "#c0603a",
            NoteKo = "전투 시작 시 뽑을 더미에 화상 1장", NoteEn = "Adds a Burn to your draw pile at combat start", NoteZh = "战斗开始时将1张灼烧加入抽牌堆" },
        new Prefix { Name = "Hollow", Ko = "공허한", Zh = "虚空的", Weight = 5, Penalty = true, Color = "#6a5a8a",
            NoteKo = "전투 시작 시 뽑을 더미에 공허 1장", NoteEn = "Adds a Void to your draw pile at combat start", NoteZh = "战斗开始时将1张虚无加入抽牌堆" },

        // --- Mixed (gamble) affixes: no var scaling, an amber note. See ForgeCombatAffixPatch. ---
        // Enemy-strip: each turn erodes one enemy power toward zero. Chaos — it strips enemy
        // buffs (Strength/Plating/Artifact…) AND the debuffs you applied (Weak/Vuln/Frail) alike.
        new Prefix { Name = "Eroding", Ko = "침식의", Zh = "侵蚀的", Weight = 6, Mixed = true, EnemyStrip = true, Color = "#8fbf6f",
            NoteKo = "매 턴 적 능력치 하나를 0쪽으로 1 감소 (버프·디버프 모두)",
            NoteEn = "Each turn, move one enemy power 1 toward zero (buffs and debuffs alike)",
            NoteZh = "每回合使敌方一个能力值向零靠近1点（增益与减益皆可）" },
        // Symmetric combat-start: applies a debuff to BOTH you and every enemy on turn 1.
        new Prefix { Name = "Exposing", Ko = "노출의", Zh = "暴露的", Weight = 6, Mixed = true, SymPower = "Vulnerable", SymAmount = 1, Color = "#e0904d",
            NoteKo = "전투 시작 시 적 하나와 자신에게 취약 1",
            NoteEn = "Vulnerable 1 to one enemy and yourself at combat start",
            NoteZh = "战斗开始时对一个敌人和自己施加1层易伤" },
        new Prefix { Name = "Enervating", Ko = "쇠약의", Zh = "衰弱的", Weight = 6, Mixed = true, SymPower = "Weak", SymAmount = 1, Color = "#c0a04d",
            NoteKo = "전투 시작 시 적 하나와 자신에게 약화 1",
            NoteEn = "Weak 1 to one enemy and yourself at combat start",
            NoteZh = "战斗开始时对一个敌人和自己施加1层虚弱" },
        // Chaotic: a coin flip each turn — good (enemies) or bad (you).
        new Prefix { Name = "Chaotic", Ko = "혼돈의", Zh = "混沌的", Weight = 6, Mixed = true, RandomDebuff = true, Color = "#a06fd0",
            NoteKo = "매 턴 50% 확률로 적 하나 또는 자신에게 무작위 디버프(취약/약화/손상)",
            NoteEn = "Each turn, 50% chance: a random debuff (Vulnerable / Weak / Frail) to one enemy or one player",
            NoteZh = "每回合50%概率：对一个敌人或一名玩家施加随机减益（易伤/虚弱/脆弱）" },

        // --- Reactive affixes (see ReactiveAffix / ReactiveAffixPatches / REACTIVE_PREFIXES.md). They
        //     react to the player's power/energy events in combat. Forceable via `forge <relic> Resonant`. ---
        new Prefix { Name = "Resonant", Ko = "공명의", Zh = "共鸣的", Weight = 5, Mixed = true, GainAmplify = true, AlwaysCurse = true, Color = "#4db8ff",
            NoteKo = "힘·민첩을 얻을 때마다 그 능력치를 1 더 얻는다 (턴당 3회)",
            NoteEn = "When you gain Strength or Dexterity, gain 1 more of it (up to 3/turn)",
            NoteZh = "获得力量或敏捷时，额外获得1点（每回合最多3次）" },
        new Prefix { Name = "Obstinate", Ko = "완강한", Zh = "顽固的", Weight = 6, Mixed = true, LossInvert = true, Color = "#8fbf6f",
            NoteKo = "적이 힘·민첩을 깎으면, 그만큼 오히려 얻는다",
            NoteEn = "When an enemy reduces your Strength or Dexterity, gain that amount instead",
            NoteZh = "当敌人降低你的力量或敏捷时，反而获得等量" },
        new Prefix { Name = "Discharging", Ko = "방전의", Zh = "放电的", Weight = 6, Mixed = true, EnergyDischarge = 4, Color = "#e0904d",
            NoteKo = "추가 에너지를 얻을 때마다 모든 적에게 4 피해",
            NoteEn = "Whenever you gain bonus Energy, deal 4 damage to all enemies",
            NoteZh = "每当你获得额外能量时，对所有敌人造成4点伤害" },

        // --- Run-state affixes: react to GOLD / DECK / CURSE state rather than combat power events.
        //     Cursefed/Gilded are boons (green note); Taxing is a curse (red note). All three scale no
        //     host var (IsCompanionPrefix). Forceable via `forge <relic> Cursefed|Gilded|Taxing`. ---
        new Prefix { Name = "Cursefed", Ko = "저주먹은", Zh = "噬咒的", Weight = 5, CurseDrawStrength = true, Color = "#a86fd0",
            NoteKo = "저주 카드를 뽑으면 힘 또는 민첩 +1 (턴당 1회)",
            NoteEn = "When you draw a Curse card, gain 1 Strength or Dexterity (once per turn)",
            NoteZh = "抽到诅咒牌时，获得1点力量或敏捷（每回合1次）" },
        new Prefix { Name = "Gilded", Ko = "황금빛", Zh = "镀金的", Weight = 4, GoldStrengthPer = 300, Color = "#ffd23f",
            NoteKo = "전투 시작 시 골드 300당 힘 +1",
            NoteEn = "At combat start, gain 1 Strength per 300 gold",
            NoteZh = "战斗开始时，每300金币获得1点力量" },
        new Prefix { Name = "Taxing", Ko = "과세의", Zh = "征税的", Weight = 6, Penalty = true, Color = "#b0554d",
            NoteKo = "전투 시작 시 덱의 카드 1장당 골드 1 손실",
            NoteEn = "At combat start, lose 1 gold per card in your deck",
            NoteZh = "战斗开始时，每有1张牌失去1金币" },

        // --- Fallback prefixes (NOT rolled normally — InPool excludes them; Weight 0). A magnitude
        //     prefix that rounded to no change on a relic is REPLACED by one of these (see
        //     RelicForgeService.Forge). A host-independent, chance-gated minor combat-start buff whose
        //     chance ({0}) is filled from the fizzled prefix's tier — so a reforge/cursed pickup always
        //     does something and the odds are shown. Green (a boon), never Mixed/Penalty. ---
        new Prefix { Name = "Honed", Ko = "벼려진", Zh = "磨砺的", Weight = 0, FallbackStat = "Strength", FallbackAmount = 1, Color = "#7ed957",
            NoteKo = "전투 시작 시 {0}% 확률로 힘 +1", NoteEn = "At combat start, {0}% chance to gain 1 Strength", NoteZh = "战斗开始时，{0}%概率获得1点力量" },
        new Prefix { Name = "Bulwarked", Ko = "굳건한", Zh = "坚壁的", Weight = 0, FallbackStat = "Block", FallbackAmount = 3, Color = "#4db8ff",
            NoteKo = "전투 시작 시 {0}% 확률로 블록 3", NoteEn = "At combat start, {0}% chance to gain 3 Block", NoteZh = "战斗开始时，{0}%概率获得3点格挡" },
        new Prefix { Name = "Nimble", Ko = "날렵한", Zh = "敏捷的", Weight = 0, FallbackStat = "Dexterity", FallbackAmount = 1, Color = "#6ed9c0",
            NoteKo = "전투 시작 시 {0}% 확률로 민첩 +1", NoteEn = "At combat start, {0}% chance to gain 1 Dexterity", NoteZh = "战斗开始时，{0}%概率获得1点敏捷" },
        new Prefix { Name = "Barbed", Ko = "미늘의", Zh = "倒刺的", Weight = 0, FallbackStat = "Thorns", FallbackAmount = 2, Color = "#a7e34d",
            NoteKo = "전투 시작 시 {0}% 확률로 가시 2", NoteEn = "At combat start, {0}% chance to gain 2 Thorns", NoteZh = "战斗开始时，{0}%概率获得2荆棘" },

        // Penalty fallbacks: the mirror of the buff fallbacks above. A NEGATIVE magnitude prefix
        // (Damaged/Shoddy/Broken) that rounds to no change on a relic is replaced (on a reforge) by
        // one of these — a LOW-chance combat-start self-debuff, so a fizzled downside still keeps a
        // little of the gamble's bite instead of vanishing. Penalty = true → red note. Out of pool.
        new Prefix { Name = "Sapped", Ko = "무기력한", Zh = "虚弱的", Weight = 0, FallbackStat = "Weak", FallbackAmount = 1, Penalty = true, Color = "#b0554d",
            NoteKo = "전투 시작 시 {0}% 확률로 자신에게 약화 1", NoteEn = "At combat start, {0}% chance to gain 1 Weak", NoteZh = "战斗开始时，{0}%概率给予自己1虚弱" },
        new Prefix { Name = "Wilted", Ko = "시든", Zh = "枯萎的", Weight = 0, FallbackStat = "Frail", FallbackAmount = 1, Penalty = true, Color = "#a0605a",
            NoteKo = "전투 시작 시 {0}% 확률로 자신에게 손상 1", NoteEn = "At combat start, {0}% chance to gain 1 Frail", NoteZh = "战斗开始时，{0}%概率给予自己1脆弱" },
        new Prefix { Name = "Exposed", Ko = "허술한", Zh = "破绽的", Weight = 0, FallbackStat = "Vulnerable", FallbackAmount = 1, Penalty = true, Color = "#9a6b8f",
            NoteKo = "전투 시작 시 {0}% 확률로 자신에게 취약 1", NoteEn = "At combat start, {0}% chance to gain 1 Vulnerable", NoteZh = "战斗开始时，{0}%概率给予自己1易伤" },

        // ============================ Character-gated affixes ============================
        // Each rolls ONLY when the owner plays the named character (CharAffix + RequiredCharacter),
        // and reacts to that character's signature mechanic. See CharAffix / CharAffixPatches.

        // --- Silent (poison / shivs / discard) ---
        new Prefix { Name = "Envenomed", Ko = "맹독의", Zh = "剧毒的", Weight = 8, CharAffix = true, RequiredCharacter = "SILENT", Color = "#6ee07a",
            NoteKo = "적에게 중독을 부여할 때 50% 확률로 중독 1 추가",
            NoteEn = "When you apply Poison to an enemy, 50% chance to apply 1 more",
            NoteZh = "对敌人施加中毒时，50%概率额外施加1层" },
        new Prefix { Name = "Flurrying", Ko = "비수의", Zh = "飞刀的", Weight = 7, CharAffix = true, RequiredCharacter = "SILENT", Color = "#c0c8d8",
            NoteKo = "시브를 낼 때 25% 확률로 시브 1장을 손에 넣는다",
            NoteEn = "When you play a Shiv, 25% chance to add a Shiv to your hand",
            NoteZh = "打出小刀时，25%概率将1张小刀加入手牌" },
        new Prefix { Name = "Cycling", Ko = "순환의", Zh = "循环的", Weight = 5, CharAffix = true, RequiredCharacter = "SILENT", Color = "#ffd23f",
            NoteKo = "카드를 버릴 때 카드 1장을 뽑는다",
            NoteEn = "When you discard a card, draw a card",
            NoteZh = "弃牌时，抓1张牌" },
        new Prefix { Name = "Toxic", Ko = "자멸의", Zh = "自毒的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "SILENT", Color = "#7a8a5a",
            NoteKo = "전투 시작 시 자신에게 중독 3",
            NoteEn = "Poison 3 to yourself at combat start",
            NoteZh = "战斗开始时，给予自己3层中毒" },
        new Prefix { Name = "Dulled", Ko = "무뎌진", Zh = "钝毒的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "SILENT", Color = "#5a7a6a",
            NoteKo = "내가 부여하는 중독으로는 적의 중독이 6을 초과해 쌓이지 않는다",
            NoteEn = "Poison you apply cannot stack an enemy above 6 Poison",
            NoteZh = "你施加的中毒无法使敌人的中毒层数超过6" },

        // --- Defect (orbs / focus) ---
        new Prefix { Name = "Focused", Ko = "집속의", Zh = "集束的", Weight = 7, CharAffix = true, RequiredCharacter = "DEFECT", Color = "#4db8ff",
            NoteKo = "전투 중 처음으로 밀집을 얻을 때 밀집 1 추가",
            NoteEn = "The first time you gain Focus in combat, gain 1 more Focus",
            NoteZh = "战斗中首次获得集中时，额外获得1点集中" },
        new Prefix { Name = "Amplified", Ko = "증폭의", Zh = "增幅的", Weight = 6, CharAffix = true, RequiredCharacter = "DEFECT", Color = "#c04dff",
            NoteKo = "오브를 소환할 때 25% 확률로 무작위 오브 1개를 추가 소환",
            NoteEn = "When you Channel an orb, 25% chance to Channel a random orb",
            NoteZh = "引导充能球时，25%概率额外引导1个随机充能球" },
        new Prefix { Name = "Supercharged", Ko = "과충전의", Zh = "过载的", Weight = 6, CharAffix = true, RequiredCharacter = "DEFECT", Color = "#ffd23f",
            NoteKo = "오브 슬롯이 가득 찼을 때 밀집 1을 얻는다 (슬롯이 차지 않으면 잃는다)",
            NoteEn = "While your orb slots are full, gain 1 Focus (lost while not full)",
            NoteZh = "充能球槽位填满时，获得1点集中（未填满则失去）" },
        new Prefix { Name = "Shorted", Ko = "누전의", Zh = "短路的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "DEFECT", Color = "#6a5a8a",
            NoteKo = "전투 시작 시 밀집 2 감소",
            NoteEn = "Focus -2 at combat start",
            NoteZh = "战斗开始时，集中-2" },
        new Prefix { Name = "Polarized", Ko = "양극의", Zh = "两极的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "DEFECT", Color = "#5a6a8a",
            NoteKo = "오브 슬롯이 모두 비거나 모두 가득 찬 채 턴을 마치면 다음 턴에 에너지 1을 잃는다",
            NoteEn = "If you end your turn with your orb slots all empty or all full, lose 1 Energy next turn",
            NoteZh = "回合结束时充能球槽全空或全满，则下回合失去1点能量" },

        // --- Necrobinder (summon / doom) ---
        new Prefix { Name = "Necromantic", Ko = "사령술의", Zh = "死灵的", Weight = 8, CharAffix = true, RequiredCharacter = "NECROBINDER", Color = "#7ed957",
            NoteKo = "소환할 때 블록 3을 얻는다",
            NoteEn = "When you Summon, gain 3 Block",
            NoteZh = "召唤时，获得3点格挡" },
        new Prefix { Name = "Dooming", Ko = "파멸의", Zh = "厄运的", Weight = 7, CharAffix = true, RequiredCharacter = "NECROBINDER", Color = "#a06fd0",
            NoteKo = "적에게 종말을 부여할 때 종말 1 추가",
            NoteEn = "When you apply Doom to an enemy, apply 1 more",
            NoteZh = "对敌人施加厄运时，额外施加1层" },
        new Prefix { Name = "Bonebound", Ko = "뼈엮인", Zh = "缚骨的", Weight = 6, CharAffix = true, RequiredCharacter = "NECROBINDER", Color = "#d9d9e0",
            NoteKo = "매 턴 소환 1 (오스티가 성장한다)",
            NoteEn = "Summon 1 each turn (grows your Osty)",
            NoteZh = "每回合召唤1（成长你的奥斯提）" },
        new Prefix { Name = "Sacrificial", Ko = "희생의", Zh = "献祭的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "NECROBINDER", Color = "#8a6a4a",
            NoteKo = "소환수가 피해를 입으면 자신에게 약화·손상·취약 중 하나 1 (턴당 1회), 소환수가 죽으면 2",
            NoteEn = "When your summon takes damage, apply Weak / Frail / Vulnerable 1 to yourself (once per turn); 2 when it dies",
            NoteZh = "召唤物受到伤害时，给予自己1层虚弱/脆弱/易伤（每回合1次）；召唤物死亡时为2层" },
        new Prefix { Name = "Doombound", Ko = "잠식된", Zh = "被蚀的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "NECROBINDER", Color = "#5a4a7a",
            NoteKo = "매턴 자신에게 종말 1",
            NoteEn = "Each turn, apply 1 Doom to yourself",
            NoteZh = "每回合给予自己1层厄运" },
        new Prefix { Name = "Levied", Ko = "징세의", Zh = "重税的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "REGENT", Color = "#8a7a4a",
            NoteKo = "별을 소모하는 카드의 별 소모량 1 증가",
            NoteEn = "Cards that cost Stars cost 1 more Star",
            NoteZh = "消耗星星的卡牌，星星费用+1" },

        // --- Regent (stars / forge) ---
        new Prefix { Name = "Starlit", Ko = "별빛의", Zh = "星耀的", Weight = 7, CharAffix = true, RequiredCharacter = "REGENT", Color = "#ffd23f",
            NoteKo = "매 턴 별 1을 얻는다",
            NoteEn = "Gain 1 Star each turn",
            NoteZh = "每回合获得1颗星" },
        new Prefix { Name = "Reforging", Ko = "재련의", Zh = "重铸的", Weight = 6, CharAffix = true, RequiredCharacter = "REGENT", Color = "#e0904d",
            NoteKo = "단조할 때 별 1을 얻는다",
            NoteEn = "When you Forge, gain 1 Star",
            NoteZh = "锻造卡牌时，获得1颗星" },
        new Prefix { Name = "Regal", Ko = "위엄의", Zh = "威严的", Weight = 6, CharAffix = true, RequiredCharacter = "REGENT", Color = "#4dd24d",
            NoteKo = "별을 소비할 때 50% 확률로 별 1을 돌려받는다",
            NoteEn = "When you spend Stars, 50% chance to refund 1 Star",
            NoteZh = "消耗星辰时，50%概率返还1颗星" },
        new Prefix { Name = "Bankrupt", Ko = "파산한", Zh = "破产的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "REGENT", Color = "#b0554d",
            NoteKo = "별을 4개 소비할 때마다 손에 든 무작위 카드 1장에 휘발성이 부여된다",
            NoteEn = "Every 4 Stars you spend, a random card in your hand becomes Ethereal",
            NoteZh = "每消耗4颗星，手牌中随机1张牌获得虚无" },
    };

    // Rarities that can receive a prefix at all. Starter/Event/None never do. Ancient is included
    // here but can be opted out at runtime via ForgeConfig.ForgeAncientRelics (see RelicForgeService.Forge).
    public static readonly HashSet<RelicRarity> Eligible = new()
    {
        RelicRarity.Common, RelicRarity.Uncommon, RelicRarity.Rare, RelicRarity.Shop, RelicRarity.Ancient
    };

    /// <summary>Weighted, deterministic pick from the pool (Terraria reforge style). Character-gated
    /// prefixes (RequiredCharacter set) are only in the pool when <paramref name="character"/> — the
    /// owner's CharacterModel Id.Entry — matches; universal prefixes always are. Because the character
    /// is fixed for a whole run, the filtered pool is stable per run, so the roll stays
    /// seed-deterministic and reproduces on load.</summary>
    public static Prefix Roll(Rng rng, string? character = null)
    {
        double total = 0;
        foreach (var p in All) if (InPool(p, character)) total += p.Weight;
        double r = rng.NextFloat() * total;
        Prefix? last = null;
        foreach (var p in All)
        {
            if (!InPool(p, character)) continue;
            last = p;
            r -= p.Weight;
            if (r < 0) return p;
        }
        return last ?? All[All.Length - 1];
    }

    /// <summary>Whether a prefix is eligible for the current character's roll pool: fallback prefixes
    /// are NEVER rolled (they only appear via substitution — see RelicForgeService.Forge); PENALTY
    /// prefixes are NEVER rolled either (their downside is re-homed onto the curse side — a penalty
    /// identity is drawn into rec.SelfCurse via <see cref="SelfCurseTable.PickCombined"/>, never into
    /// the beneficial prefix slot); universal (no RequiredCharacter) always, character-gated only when
    /// the character matches (case-insensitive on CharacterModel Id.Entry, e.g. "SILENT").</summary>
    private static bool InPool(Prefix p, string? character)
        => !p.IsFallback && !p.Penalty && CharacterMatches(p, character);

    /// <summary>Character-eligibility half of <see cref="InPool"/>, shared with the curse pool: universal
    /// (no RequiredCharacter) always, character-gated only when <paramref name="character"/> matches. The
    /// curse-pool builder (<see cref="SelfCurseTable.PickCombined"/>) pre-filters on <c>Penalty &amp;&amp; !IsFallback</c>
    /// and uses this for the character gate, so a penalty like Toxic only rolls for its own character.</summary>
    public static bool CurseInPool(Prefix p, string? character) => CharacterMatches(p, character);

    private static bool CharacterMatches(Prefix p, string? character)
        => p.RequiredCharacter.Length == 0
           || (character != null && string.Equals(p.RequiredCharacter, character, StringComparison.OrdinalIgnoreCase));

    /// <summary>The fallback prefixes (out of the normal roll pool), used to REPLACE a magnitude prefix
    /// that scaled nothing on a relic — BUFF fallbacks for a positive prefix, PENALTY fallbacks for a
    /// negative one. Order is stable (source order), so the deterministic picks below reproduce across
    /// peers/loads.</summary>
    private static readonly Prefix[] Fallbacks = Array.FindAll(All, p => p.IsFallback && !p.Penalty);
    private static readonly Prefix[] FallbackPenalties = Array.FindAll(All, p => p.IsFallback && p.Penalty);

    /// <summary>Deterministically pick one BUFF fallback from a (mixed) seed. No RNG object / no stream
    /// consumption — a pure index off the seed — so substituting one relic never perturbs any other
    /// relic's roll, and co-op/load reproduce the same choice.</summary>
    public static Prefix PickFallback(uint seed) => Fallbacks[(int)(seed % (uint)Fallbacks.Length)];

    /// <summary>Deterministically pick one PENALTY fallback (mirror of <see cref="PickFallback"/>, for a
    /// negative magnitude prefix that scaled nothing).</summary>
    public static Prefix PickFallbackPenalty(uint seed) => FallbackPenalties[(int)(seed % (uint)FallbackPenalties.Length)];

    /// <summary>The positive, non-Amplify magnitude tiers' powers, ascending. Used to find the tier
    /// just below a rolled one for the tie-break check (see RelicForgeService.ApplyTierTiebreak).</summary>
    private static readonly double[] PositiveTiers = BuildPositiveTiers();
    private static double[] BuildPositiveTiers()
    {
        var list = new List<double>();
        foreach (var p in All)
            if (!p.IsCompanionPrefix && !p.Amplify && p.PowerPct > 0) list.Add(p.PowerPct);
        list.Sort();
        return list.ToArray();
    }

    /// <summary>The largest positive magnitude tier strictly below <paramref name="pct"/>, or 0 if it is
    /// already the lowest tier (nothing below to tie with).</summary>
    public static double NextLowerTierPct(double pct)
    {
        double lower = 0;
        foreach (double t in PositiveTiers) { if (t < pct - 1e-9) lower = t; else break; }
        return lower;
    }

    /// <summary>The fallback prefix that grants a given stat (e.g. "Strength" → Honed), for the tie-break
    /// tooltip note + amount; null if none.</summary>
    public static Prefix? FallbackByStat(string stat)
    {
        foreach (var p in Fallbacks)
            if (string.Equals(p.FallbackStat, stat, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
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

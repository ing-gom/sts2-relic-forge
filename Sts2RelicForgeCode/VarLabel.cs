using System.Collections.Generic;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2RelicForge;

/// <summary>
/// Localized display name for a relic var, used in the tooltip's change block.
/// Hybrid strategy:
///   1. Power vars (StrengthPower, ThornsPower, …): the var name IS the power id, so the
///      game's own "powers" loc table gives every language for free.
///   2. Block / Energy / Forge: the game's "static_hover_tips" keyword loc table.
///   3. Everything else: a hand-kept ko/en/zh table (no standalone game loc exists — those
///      terms only appear woven into descriptions). Falls back to a trimmed English name.
/// </summary>
internal static class VarLabel
{
    private static readonly HashSet<string> PowerLoc = new()
    {
        "StrengthPower", "DexterityPower", "FocusPower", "ThornsPower", "PoisonPower",
        "VulnerablePower", "WeakPower", "VigorPower", "PlatingPower"
    };

    private static readonly HashSet<string> KeywordLoc = new() { "Block", "Energy", "Forge" };

    // (en, ko, zh) for terms the game has no standalone loc key for.
    private static readonly Dictionary<string, (string en, string ko, string zh)> Manual = new()
    {
        ["Cards"] = ("Cards", "카드", "卡牌"),
        ["BlockNextTurn"] = ("Next-turn Block", "다음 턴 방어도", "下回合格挡"),
        ["Heal"] = ("Heal", "회복", "治疗"),
        ["MaxHp"] = ("Max HP", "최대 체력", "最大生命"),
        ["Damage"] = ("Damage", "피해", "伤害"),
        ["DamageMinimum"] = ("Min Damage", "최소 피해", "最小伤害"),
        ["ExtraDamage"] = ("Extra Damage", "추가 피해", "额外伤害"),
        ["Lightning"] = ("Lightning", "번개", "闪电"),
        ["Dark"] = ("Dark", "어둠", "黑暗"),
        ["Summon"] = ("Summon", "소환", "召唤"),
        ["OrbCount"] = ("Orbs", "오브", "法球"),
        ["Momentum"] = ("Momentum", "기세", "气势"),
        ["Swift"] = ("Swift", "신속", "迅捷"),
        ["SwiftAmount"] = ("Swift", "신속", "迅捷"),
        ["NimbleAmount"] = ("Nimble", "날렵", "灵巧"),
        ["SharpAmount"] = ("Sharp", "예리", "锋利"),
        ["Shivs"] = ("Shivs", "시브", "苦无"),
        ["Stars"] = ("Stars", "별", "星星"),
        ["Wishes"] = ("Wishes", "소원", "愿望"),
        ["Sacrifices"] = ("Sacrifices", "희생", "牺牲"),
        ["Repeat"] = ("Repeat", "반복", "重复"),
        ["Increase"] = ("Increase", "증가량", "提升"),
        ["StartOfCombat"] = ("Combat Start", "전투 시작", "战斗开始"),
        ["StartOfTurn"] = ("Turn Start", "턴 시작", "回合开始"),
        ["HpLossReduction"] = ("HP-loss Reduction", "체력 손실 감소", "生命损失减少"),
        ["PotionSlots"] = ("Potion Slots", "포션 슬롯", "药水槽"),
        ["Potions"] = ("Potions", "포션", "药水"),
        ["Discount"] = ("Discount", "할인", "折扣"),
        ["Gold"] = ("Gold", "골드", "金币"),
        ["Relics"] = ("Relics", "유물", "遗物"),
        ["GainEnergy"] = ("Gain Energy", "에너지 획득", "获得能量"),
        ["MaxHpLoss"] = ("Max HP Loss", "최대 체력 손실", "最大生命损失"),
        ["HpLoss"] = ("HP Loss", "체력 손실", "生命损失"),
        ["LoseEnergy"] = ("Lose Energy", "에너지 상실", "失去能量"),
        ["EnemyStrength"] = ("Enemy Strength", "적 힘", "敌方力量"),
        ["SelfStrength"] = ("Strength", "자신 힘", "自身力量"),
        ["Curses"] = ("Curses", "저주", "诅咒"),
        ["DazedCount"] = ("Dazed", "현기증", "眩晕"),
        ["CardThreshold"] = ("Card Threshold", "카드 수 조건", "卡牌数条件"),
        ["EnergyThreshold"] = ("Energy Threshold", "에너지 조건", "能量条件"),
        ["DamageThreshold"] = ("Damage Threshold", "피해 조건", "伤害条件"),
        ["DamageTurn"] = ("Trigger Turn", "발동 턴", "触发回合"),
    };

    public static string Of(string varName)
    {
        if (PowerLoc.Contains(varName))
        {
            string? s = LocText("powers", varName + ".title");
            if (s != null) return s;
        }
        if (KeywordLoc.Contains(varName))
        {
            string? s = LocText("static_hover_tips", varName + ".title");
            if (s != null) return s;
        }
        if (Manual.TryGetValue(varName, out var t))
        {
            string lang = LocManager.Instance?.Language ?? "";
            if (lang.StartsWith("ko")) return t.ko;
            if (lang.StartsWith("zh")) return t.zh;
            return t.en;
        }
        return Trim(varName);
    }

    private static string? LocText(string table, string key)
    {
        // Defensive: a game LocString.GetFormattedText() can throw (SmartFormat) in some
        // languages. Never let a label lookup break the whole tooltip — fall back instead.
        try
        {
            LocString? loc = LocString.GetIfExists(table, key);
            if (loc == null) return null;
            string s = loc.GetFormattedText();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch
        {
            return null;
        }
    }

    private static string Trim(string v)
    {
        if (v.EndsWith("Power")) return v[..^"Power".Length];
        if (v.EndsWith("Amount")) return v[..^"Amount".Length];
        return v;
    }
}

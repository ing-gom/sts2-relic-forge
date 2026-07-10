using System.Collections.Generic;
using System.Text;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2RelicForge;

/// <summary>
/// Routes every player-facing mod string through a mod-owned LocManager table (<see cref="TableName"/>)
/// so external tools — most notably Sts2ModTranslator, which reads/edits LocManager tables — can see
/// and translate them. The shipped en/ko/zh strings stay INLINE in the data tables (PrefixTable,
/// RiderSuffix, SelfCurseTable, EnemyPrefixTable, VarLabel, and the UI trios below) as the authoring
/// source; this class only re-routes the LOOKUP, it does not move the data.
///
/// Why lazy re-injection: the game rebuilds LocManager's table set wholesale on a language switch
/// (SetLanguageInternal does `_tables = tables`), which silently drops any injected table. So every
/// read goes through <see cref="EnsureTable"/>, which re-registers the table whenever it is missing
/// or the language changed since the last build — the same self-healing idiom as the rest_site_ui
/// merge in RestSiteReforgeSupport.EnsureLoc, generalized. Reads then hit the LIVE table
/// (GetRawText), so external edits to entries are honored until the next language rebuild.
///
/// Every accessor falls back to the caller-supplied English string on ANY failure (LocManager not
/// up yet, table lookup throw, missing key), so display code can never crash or go blank from loc.
/// Keys are derived from the English identifiers (stable across releases — translation packs keyed
/// on them survive version bumps): PREFIX_LEGENDARY.name / .note, RIDER_THE_TYRANT.name / .effect,
/// SELFCURSE_ENFEEBLING.name / .effect, ENEMYPREFIX_VICIOUS.name, VAR_MaxHp, UI.SKIP, …
/// </summary>
internal static class ForgeLoc
{
    public const string TableName = "relic_forge";

    // UI string trios (en, ko, zh) that have no data-table home. UI.NOT_ENOUGH_GOLD carries a {0}
    // placeholder — callers string.Format it (keep the placeholder when translating).
    internal static readonly Dictionary<string, (string en, string ko, string zh)> UiStrings = new()
    {
        ["UI.CURSED_MARK"] = ("(Cursed)", "〈저주〉", "〈诅咒〉"),
        ["UI.CURSE_TITLE"] = ("Curse", "저주", "诅咒"),
        ["UI.PICKER_BANNER_REFORGE"] = ("Choose a relic to reforge", "재련할 유물 선택", "选择要重铸的遗物"),
        ["UI.SKIP"] = ("Skip", "건너뛰기", "跳过"),
        ["UI.SHOP_REFORGE_TITLE"] = ("Reforge", "재련", "重铸"),
        ["UI.SHOP_REFORGE_BODY"] = ("Reforge the relic. Each reforge in the same shop costs more. Every curse that appears deepens the aura — the more curses, the likelier reforging ends here.",
                                    "유물을 다시 재련합니다. 같은 상점에서 재련할수록 비용이 오릅니다. 저주가 뜰 때마다 그 기운이 짙어지고, 저주가 쌓일수록 이 상점에서의 재련이 끝날 확률이 높아집니다.",
                                    "重新锻造遗物。同一商店中每次重铸费用递增。每出现一次诅咒，其气息便更浓——诅咒越多，本店重铸结束的几率越高。"),
        ["UI.SHOP_REFORGE_AURA1"] = ("A faint curse-aura lingers. You can keep reforging — for now.",
                                     "저주의 기운이 희미하게 감돈다. 아직은 재련을 이어갈 수 있다.",
                                     "隐约萦绕着一丝诅咒之气，暂时仍可继续重铸。"),
        ["UI.SHOP_REFORGE_AURA2"] = ("The curse-aura thickens; the forge's flames begin to waver.",
                                     "저주의 기운이 짙어진다. 대장간의 불꽃이 흔들리기 시작한다.",
                                     "诅咒之气渐浓，炉火开始摇曳。"),
        ["UI.SHOP_REFORGE_AURA3"] = ("The curse-aura hangs heavy; the flames gutter dangerously.",
                                     "저주의 기운이 자욱하게 감돈다. 불꽃이 위태롭게 사그라든다.",
                                     "诅咒之气弥漫，炉火岌岌可危地黯淡下去。"),
        ["UI.SHOP_REFORGE_AURA4"] = ("The embers are nearly out — one more curse may snuff the forge.",
                                     "불씨가 꺼지기 직전이다. 다음 저주가 뜨면 대장간이 식을 듯하다.",
                                     "余烬将熄，再出现一次诅咒，炉火恐将冷却。"),
        ["UI.SHOP_REFORGE_ENDED"] = ("A curse settled and the forge went cold here. Cleanse it, or reforge again at the next shop.",
                                     "저주가 서려 이 상점에서는 대장간의 불이 식었습니다. 정화하거나 다음 상점에서 다시 재련하세요.",
                                     "诅咒缠身，本店炉火已冷。请净化，或到下一个商店再重铸。"),
        ["UI.SHOP_CLEANSE_TITLE"] = ("Cleanse", "정화", "净化"),
        ["UI.SHOP_CLEANSE_BODY"] = ("Remove the relic's curse (a penalty prefix reverts it to no prefix).",
                                    "유물의 저주를 제거합니다 (페널티 접두사는 접두사 없음으로 되돌립니다).",
                                    "移除遗物上的诅咒（负面词缀将还原为无词缀）。"),
        ["UI.CLEANSE_NONE"] = ("No cursed relic to cleanse.", "정화할 저주가 걸린 유물이 없습니다.", "没有可净化诅咒的遗物。"),
        ["UI.NOT_ENOUGH_GOLD"] = ("Not enough gold. (need {0})", "골드가 부족합니다. (필요: {0})", "金币不足。(需要：{0})"),
        // Bespoke one-time-reward relic tooltip labels (see BespokeBonus).
        ["UI.BESPOKE_CARD_REWARD"] = ("Card Reward", "카드 보상", "卡牌奖励"),
        ["UI.BESPOKE_POTION"] = ("Potion", "포션", "药水"),
        ["UI.BESPOKE_STRIKE_UPGRADE"] = ("Strike Upgrade", "강타 강화", "打击强化"),
        ["UI.BESPOKE_DEFEND_UPGRADE"] = ("Defend Upgrade", "수비 강화", "防御强化"),
        ["UI.BESPOKE_SPENT"] = ("One-time effect · already granted", "일회성 효과 · 이미 지급됨", "一次性效果 · 已发放"),
    };

    private static Dictionary<string, (string en, string ko, string zh)>? _entries;
    private static string? _builtLang;   // language the injected table was last built for

    /// <summary>The mod string for <paramref name="key"/> in the current language, read through the
    /// live LocManager table (so external translations win); <paramref name="en"/> on any failure.</summary>
    public static string Get(string key, string en)
    {
        try
        {
            var lm = LocManager.Instance;
            if (lm == null) return en;
            EnsureTable(lm);
            var table = lm.GetTable(TableName);
            if (table.HasEntry(key)) return table.GetRawText(key);
        }
        catch { /* loc must never break display code — fall through to the English inline */ }
        return en;
    }

    /// <summary>Shorthand for the UI trios above: <c>Ui("SKIP")</c> → the localized "UI.SKIP".</summary>
    public static string Ui(string name)
    {
        string key = "UI." + name;
        return Get(key, UiStrings.TryGetValue(key, out var t) ? t.en : name);
    }

    /// <summary>English display name → stable key fragment: upper-case, non-alphanumerics to '_'
    /// ("the Tyrant" → "THE_TYRANT"). Keys must never change once shipped — translation packs
    /// reference them.</summary>
    public static string KeyOf(string en)
    {
        var sb = new StringBuilder(en.Length);
        foreach (char c in en) sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        return sb.ToString();
    }

    /// <summary>(Re)build and register the table if it is missing (language switch replaced
    /// LocManager's table set) or the language changed. No-op on the hot path otherwise.</summary>
    private static void EnsureTable(LocManager lm)
    {
        string lang = lm.Language ?? "";
        bool missing = !lm._tables.ContainsKey(TableName);
        if (!missing && lang == _builtLang) return;

        _entries ??= BuildRegistry();
        var dict = new Dictionary<string, string>(_entries.Count);
        foreach (var kv in _entries) dict[kv.Key] = PickLang(kv.Value, lang);

        if (missing) lm._tables[TableName] = new LocTable(TableName, dict);
        else lm.GetTable(TableName).MergeWith(dict);
        _builtLang = lang;
        MainFile.Logger.Info($"[{MainFile.ModId}] loc table '{TableName}' injected ({dict.Count} keys, lang '{lang}').");
    }

    /// <summary>Collect every (en, ko, zh) trio from the data tables + UI strings into one keyed
    /// registry. Built once — the data tables are immutable static arrays.</summary>
    private static Dictionary<string, (string en, string ko, string zh)> BuildRegistry()
    {
        var d = new Dictionary<string, (string en, string ko, string zh)>(UiStrings);
        foreach (var p in PrefixTable.All)
        {
            string k = "PREFIX_" + KeyOf(p.Name);
            d[k + ".name"] = (p.Name, p.Ko, p.Zh);
            if (p.NoteEn.Length > 0 || p.NoteKo.Length > 0 || p.NoteZh.Length > 0)
                d[k + ".note"] = (p.NoteEn, p.NoteKo, p.NoteZh);
        }
        foreach (var s in RiderSuffix.All)
        {
            string k = "RIDER_" + KeyOf(s.En);
            d[k + ".name"] = (s.En, s.Ko, s.Zh);
            d[k + ".effect"] = (s.EffEn, s.EffKo, s.EffZh);
        }
        foreach (var c in SelfCurseTable.All)
        {
            string k = "SELFCURSE_" + KeyOf(c.En);
            d[k + ".name"] = (c.En, c.Ko, c.Zh);
            d[k + ".effect"] = (c.EffEn, c.EffKo, c.EffZh);
        }
        foreach (var e in EnemyPrefixTable.All)
            d["ENEMYPREFIX_" + KeyOf(e.Name) + ".name"] = (e.Name, e.Ko, e.Zh);
        foreach (var kv in VarLabel.Manual)
            d["VAR_" + kv.Key] = kv.Value;
        return d;
    }

    /// <summary>Same language-pick semantics as the old inline Localize helpers: ko/zh when the
    /// game language starts with that code AND the string is non-empty, else English.</summary>
    private static string PickLang((string en, string ko, string zh) t, string lang)
    {
        if (lang.StartsWith("ko") && t.ko.Length > 0) return t.ko;
        if (lang.StartsWith("zh") && t.zh.Length > 0) return t.zh;
        return t.en;
    }
}

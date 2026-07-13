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
        ["UI.SHOP_REFORGE_BODY"] = ("Reforge a relic (cost rises each time). Its curse-aura fills; when full, its effect stops — only the curse remains. Cleanse to revive it.",
                                    "유물을 재련합니다(재련할수록 비용↑). 저주 기운이 차오르고, 가득 차면 유물 효과가 멈춥니다 — 저주만 남습니다. 정화로 되살립니다.",
                                    "重铸遗物（费用递增）。诅咒之气积满后遗物效果停止——仅留诅咒。净化可使其恢复。"),
        // Shop location aura (per-visit reforge budget). SHOP_REFORGE_AURA takes a {0} percent.
        ["UI.SHOP_REFORGE_AURA"] = ("This shop's curse-aura: {0}%. Each reforge stirs the aura; when it fills, reforging ends here.",
                                    "이 상점의 저주 기운: {0}%. 재련마다 저주의 기운이 맴돌며, 저주가 가득 차면 재련이 종료됩니다.",
                                    "本店诅咒之气：{0}%。每次重铸都会牵动诅咒之气，积满后重铸即结束。"),
        ["UI.SHOP_REFORGE_ENDED"] = ("This shop's curse-aura is full — the forge is cold here. Reforge again at the next shop.",
                                     "이 상점의 저주 기운이 가득 차 대장간의 불이 식었습니다. 다음 상점에서 다시 재련하세요.",
                                     "本店诅咒之气已满——炉火已冷。请到下一个商店再重铸。"),
        // Curse-gauge panel (see RelicExtraPanelsPatch / ForgeText.GaugeBody). GAUGE_FILL takes a {0} percent.
        ["UI.GAUGE_TITLE"] = ("Curse Aura", "저주 기운", "诅咒之气"),
        ["UI.GAUGE_FILL"] = ("Curse aura {0}%", "저주 기운 {0}%", "诅咒之气 {0}%"),
        ["UI.GAUGE_BAND0"] = ("A faint curse-aura clings to it.", "저주의 기운이 희미하게 서려 있다.", "隐约萦绕着一丝诅咒之气。"),
        ["UI.GAUGE_BAND1"] = ("The curse-aura is settling in.", "저주의 기운이 서리기 시작한다.", "诅咒之气开始凝聚。"),
        ["UI.GAUGE_BAND2"] = ("The curse-aura thickens.", "저주의 기운이 짙어진다.", "诅咒之气渐浓。"),
        ["UI.GAUGE_BAND3"] = ("The curse-aura hangs heavy — nearly saturated.", "저주의 기운이 자욱하다 — 곧 포화된다.", "诅咒之气弥漫——即将饱和。"),
        ["UI.GAUGE_BAND4"] = ("Saturated — this relic's effect is DISABLED (only its curse remains) and it can no longer be reforged. Cleanse to revive it.",
                              "포화 — 이 유물의 효과가 비활성화됨(저주만 남음), 더는 재련 불가. 정화로 되살릴 수 있다.",
                              "已饱和——此遗物效果被禁用（仅保留诅咒），无法再重铸。净化可使其恢复。"),
        ["UI.SHOP_CLEANSE_TITLE"] = ("Cleanse", "정화", "净化"),
        ["UI.SHOP_CLEANSE_BODY"] = ("Cleanse a cursed or saturated relic: removes its curse and lowers its curse-aura. Cost rises each time.",
                                    "저주 걸림·포화 유물을 정화합니다: 저주를 없애고 저주 기운을 낮춥니다. 정화할수록 비용↑.",
                                    "净化被诅咒或已满的遗物：移除诅咒并降低诅咒之气。费用递增。"),
        ["UI.CLEANSE_NONE"] = ("No cursed or saturated relic to cleanse.", "정화할 (저주 걸림·기운 가득 찬) 유물이 없습니다.", "没有可净化的（被诅咒或诅咒之气已满的）遗物。"),
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

    /// <summary>Drop the cached loc registry so the next <see cref="Get"/> rebuilds it — called when a
    /// sister mod registers an external prefix / self-curse (see PrefixTable.RegisterExternal), so the
    /// newly-registered names/effects get localized entries. No-op-cheap; registration is init-time.</summary>
    internal static void Invalidate() { _entries = null; _builtLang = null; }

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
        foreach (var p in PrefixTable.Pool)   // built-ins + externally registered (RelicForgeApi.RegisterPrefix)
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
        foreach (var c in SelfCurseTable.Pool)   // built-ins + externally registered (RelicForgeApi.RegisterSelfCurse)
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

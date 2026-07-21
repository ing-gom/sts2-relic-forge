using System;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using Sts2.ModKit.Bootstrap;
using Sts2.ModKit.Config;

namespace Sts2RelicForge;

/// <summary>
/// ModBootstrap.Run does harmony.PatchAll(assembly), applying all the forge patches.
/// We also register a ModConfig slider so the no-prefix chance can be tuned in-game.
/// </summary>
[ModInitializer(nameof(Initialize))]
public class MainFile
{
    public const string ModId = "Sts2RelicForge";
    private const string EntryKeyPrefix = "prefixChance";   // POSITIVE framing (F): % of pickups that GET a prefix
    private const string EntryKeyEnemyForge = "enemyForgeEnabled";
    private const string EntryKeyRiderChance = "enemyRiderChance";
    private const string EntryKeyForgeAncient = "forgeAncientRelics";
    private const string EntryKeySelfCurse = "selfCurseChance";
    private const string EntryKeyCleanseCost = "shopCleanseCost";
    private const string EntryKeyCampCleanse = "campfireCleanseEnabled";
    private const string EntryKeyRoomBroadcast = "roomBroadcastEnabled";
    private const string EntryKeyPrefixPool = "prefixPool";

    // Dropdown option labels for the prefix-pool filter — the SAVED VALUE is the option string, so
    // these are also the persistence format (do not rename without a migration). They also cannot be
    // localized: ModConfig's dropdown display only localizes via ITS OWN translation files
    // (OptionsKeys → I18n), so option text stays English; each language's row DESCRIPTION explains them.
    private static readonly string[] PrefixPoolOptions = { "All", "Enhance only", "Effects only", "Custom" };
    private static int PrefixPoolIndexOf(string v) => Math.Max(0, Array.IndexOf(PrefixPoolOptions, v));

    /// <summary>Per-language ModConfig text ("kor" exact; "zh" prefix-matches zhs AND zht). English is
    /// the entry's plain Label/Description fallback, so it is not repeated here. Other languages fall
    /// back to English — same 3-language policy as the rest of the mod (ForgeLoc trios).</summary>
    private static System.Collections.Generic.Dictionary<string, string> L(string kor, string zh)
        => new() { ["kor"] = kor, ["zh"] = zh };

    /// <summary>
    /// Attach per-language label/description to the LAST-ADDED entry via REFLECTION, never a direct
    /// call: ModKit resolves first-wins across every installed sister mod's bundled copy, so on a user
    /// machine an OLDER Sts2.ModKit without LocalizedLabels can shadow ours — a direct call then
    /// throws MissingMethodException at JIT time and kills the WHOLE config registration (reproduced
    /// locally). With reflection, an old ModKit just means the config stays English.
    /// </summary>
    private static void Loc(Sts2.ModKit.Config.ConfigEntryBuilder b,
                            System.Collections.Generic.Dictionary<string, string> labels,
                            System.Collections.Generic.Dictionary<string, string> descriptions)
    {
        try
        {
            var t = b.GetType();
            t.GetMethod("LocalizedLabels")?.Invoke(b, new object[] { labels });
            t.GetMethod("LocalizedDescriptions")?.Invoke(b, new object[] { descriptions });
        }
        catch (Exception e) { Logger.Info($"[{ModId}] config localization skipped (old ModKit loaded): {e.Message}"); }
    }

    public static readonly MegaCrit.Sts2.Core.Logging.Logger Logger
        = ModBootstrap.CreateLogger(ModId);

    public static void Initialize() =>
        ModBootstrap.Run(ModId, Logger, typeof(MainFile).Assembly, body: () =>
        {
            Logger.Info($"[{ModId}] relic-affix prototype active.");
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            // Defer so ModConfig has finished its own Initialize before we Register().
            tree.CreateTimer(0.0).Timeout += RegisterConfig;
#if RELICFORGE_SELFTEST
            SoloTest.ArmIfRequested();   // dormant unless selftest.sp.flag is present (solo-verify)
            CoopTest.ArmIfRequested();   // dormant unless selftest.coop.flag is present (coop-verify)
#endif
        });

    private static void RegisterConfig()
    {
        // Register the entries FIRST. A GetValue BEFORE registration returns default(T)=0 for an
        // unknown key (which clobbered the field defaults — e.g. shop cost fell to 0). After Register,
        // ModConfig knows each key's registered default, so a read returns the saved value or the
        // real default. Persisted values are then applied just below.
        // Per-entry statements (not one fluent chain): every Localized* attachment goes through the
        // reflection helper Loc() so an OLD first-loaded ModKit degrades to English instead of
        // killing the whole registration — see Loc()'s doc comment.
        var b = ModConfigBridge.For(ModId, "Relic Forge", Logger);
        b.Slider(EntryKeyPrefix, "Prefix chance (%)",
                defaultValue: 15.0,
                onChanged: v => { ForgeConfig.NoPrefixChance = 1.0 - v / 100.0; ForgeConfigBroadcaster.BroadcastIfHost(); })
            .Range(0f, 100f, 5f, format: "F0")
            .Description("Chance a PICKED-UP relic gets a prefix (pure upside — no curse; curses come only from reforging). Kept low by default (15) so you pick relics on their base effect and CRAFT affixes by reforging at a campfire, rather than pickups arriving pre-enhanced. 0 = all pickups vanilla; 100 = every eligible relic gets one. Reforging always lands a prefix regardless. Applies from your NEXT run — locked at run start.");
        Loc(b, L("접두사 확률 (%)", "词缀几率 (%)"), L(
            "획득한 유물에 접두사가 붙을 확률 (순수 이득 — 저주 없음; 저주는 재련에서만 나옵니다). 기본값 15로 낮게 유지 — 유물은 본래 효과로 고르고, 접두사는 모닥불 재련으로 직접 만드는 설계입니다. 0 = 모든 획득 유물이 바닐라, 100 = 대상 유물 전부 접두사. 재련은 이 값과 무관하게 항상 접두사가 붙습니다. 변경은 다음 런부터 적용됩니다 (런 시작에 고정).",
            "拾取的遗物获得词缀的几率（纯增益——无诅咒；诅咒只来自重铸）。默认较低(15)：遗物按本体效果挑选，词缀靠篝火重铸打造。0 = 所有拾取遗物保持原版；100 = 每个符合条件的遗物都有词缀。重铸不受此值影响，必定附加词缀。"));
        b.Toggle(EntryKeyEnemyForge, "Enemy forge (elites & bosses fight back)",
                defaultValue: true,
                onChanged: v => { ForgeConfig.EnemyForgeEnabled = v; ForgeConfigBroadcaster.BroadcastIfHost(); })
            .Description("Master switch for the enemy-forge mechanic: when ON, some of your forged relics also carry an 'enemy-rider' curse that strengthens elites & bosses. Turn OFF for a pure power fantasy (the relic forging itself is unaffected). Applies from your NEXT run — locked at run start.");
        Loc(b, L("적의 재련 (정예·보스의 반격)", "敌方重铸（精英与Boss的反击）"), L(
            "적 재련 메커니즘 마스터 스위치: 켜면 재련된 유물 일부가 정예·보스를 강화하는 '적 강화' 저주를 함께 지닙니다. 순수한 파워 판타지를 원하면 끄세요 (유물 재련 자체는 영향받지 않습니다). 변경은 다음 런부터 적용됩니다 (런 시작에 고정).",
            "敌方重铸机制总开关：开启时，你重铸的部分遗物会附带强化精英与Boss的「敌方强化」诅咒。想要纯粹的力量幻想请关闭（遗物重铸本身不受影响）。"));
        b.Slider(EntryKeyRiderChance, "Curse chance (%)",
                defaultValue: 33.0,
                onChanged: v => { ForgeConfig.CurseChance = v / 100.0; ForgeConfigBroadcaster.BroadcastIfHost(); })
            .Range(0f, 100f, 1f, format: "F0")
            .Description("Reference chance a forged relic carries ONE curse — either an enemy-rider curse (strengthens elites & bosses) OR a self-curse (punishes you on unblocked hits), never both. The real chance SCALES with the prefix's power: a weak prefix is rarely cursed, a Legendary almost always. Which kind is set by 'Self-curse share'. Re-forge to re-roll it. Applies from your NEXT run — locked at run start.");
        Loc(b, L("저주 확률 (%)", "诅咒几率 (%)"), L(
            "재련된 유물이 저주 하나를 지닐 기준 확률 — 적 강화 저주(정예·보스 강화) 또는 자기 저주(막지 못한 피격 시 불이익) 중 하나이며, 둘이 겹치지 않습니다. 실제 확률은 접두사의 위력에 비례 — 약한 접두사는 저주가 드물고 전설급은 거의 항상 붙습니다. 종류 비율은 '자기 저주 비율'로 조절합니다. 재련하면 다시 굴립니다. 변경은 다음 런부터 적용됩니다 (런 시작에 고정).",
            "重铸遗物携带一个诅咒的基准几率——敌方强化诅咒（强化精英与Boss）或自我诅咒（未格挡受击时惩罚你），二者只取其一。实际几率随词缀强度缩放：弱词缀几乎无诅咒，传奇级几乎必带。种类由「自我诅咒占比」决定。再次重铸可重掷。"));
        b.Slider(EntryKeyCleanseCost, "Shop cleanse cost (gold)",
                defaultValue: 100.0,
                onChanged: v => ForgeConfig.ShopCleanseCost = (int)v)
            .Range(0f, 300f, 10f, format: "F0")
            .Description("FLAT gold to cleanse at a merchant — removes the curse from a relic (a penalty prefix reverts to no prefix; an enemy-rider / self-curse is stripped, keeping the prefix). The cost does NOT escalate — every cleanse this shop costs the same. A cursed relic can no longer be reforged at all, so cleanse is the only way to shed a curse. A rest site also offers ONE free cleanse per visit. 0 = free.");
        Loc(b, L("상점 정화 비용 (골드)", "商店净化费用（金币）"), L(
            "상인에게 정화하는 고정 골드 — 유물의 저주를 제거합니다 (페널티 접두사는 무접두사로 되돌리고, 적 강화/자기 저주는 접두사를 유지한 채 벗겨냅니다). 비용은 오르지 않아 같은 상점에서 몇 번을 정화해도 같은 값입니다. 저주 걸린 유물은 더는 재련할 수 없으므로 정화가 저주를 벗는 유일한 길입니다. 휴식처에서도 방문당 1회 무료 정화를 제공합니다. 0 = 무료.",
            "商人处净化的固定金币——移除遗物的诅咒（惩罚词缀退回无词缀；敌方强化/自我诅咒被剥离并保留词缀）。费用不会递增，本店每次净化同价。被诅咒的遗物无法再重铸，净化是摆脱诅咒的唯一途径。休息处每次拜访也提供1次免费净化。0 = 免费。"));
        b.Toggle(EntryKeyCampCleanse, "Campfire cleanse",
                defaultValue: true,
                onChanged: v => { ForgeConfig.CampfireCleanseEnabled = v; ForgeConfigBroadcaster.BroadcastIfHost(); })
            .Description("Whether rest sites offer the free once-per-visit Cleanse. Turn OFF for a harder economy: the merchant becomes the ONLY way to shed a curse, so every risky reforge carries a real gold price (see 'Shop cleanse cost'). In co-op the HOST's setting applies to everyone.");
        Loc(b, L("모닥불 정화", "篝火净化"), L(
            "휴식처에서 방문당 1회 무료 정화를 제공할지 여부. 끄면 더 어려운 경제가 됩니다: 상인이 유일한 정화 수단이 되어, 위험한 재련마다 실제 골드 값이 붙습니다 ('상점 정화 비용' 참고). co-op에서는 호스트 설정이 모두에게 적용됩니다.",
            "休息处是否提供每次拜访1次的免费净化。关闭 = 更严酷的经济：商人成为唯一的净化途径，每次冒险重铸都背负真实的金币代价（见「商店净化费用」）。联机时以主机设置为准。"));
        b.Dropdown(EntryKeyPrefixPool, "Prefix pool",
                defaultValue: PrefixPoolOptions[0],
                options: PrefixPoolOptions,
                onChanged: v => { ForgeConfig.PrefixPool = PrefixPoolIndexOf(v); ForgeConfigBroadcaster.BroadcastIfHost(); })
            .Description("Which prefixes can roll, on pickup and reforge alike. 'Enhance only' = prefixes that just strengthen the relic's own numbers (e.g. a Legendary Anchor blocks more) — for players who want vertical upgrades without new mechanics. 'Effects only' = prefixes that add mechanics (grafted relic effects, combat triggers, character affixes). 'Custom' = your own per-prefix / per-curse picks (edit them with the button below). 'All' (default) = everything. LOCKED at the start of a run — a change takes effect on your NEXT run, not the current one. In co-op the HOST's setting applies to everyone.");
        Loc(b, L("접두사 풀", "词缀池"), L(
            "어떤 접두사가 롤될지 정합니다 (획득·재련 공통). 'Enhance only' = 유물 본체 수치만 강화하는 접두사 (예: 전설적인 닻은 블록만 더 줌) — 새 메커니즘 없이 수직 강화만 원하는 플레이어용. 'Effects only' = 메커니즘을 추가하는 접두사 (유물 효과 접합·전투 트리거·캐릭터 접두사). 'Custom' = 접두사·저주를 하나하나 직접 고른 나만의 풀 (아래 버튼으로 편집). 'All'(기본) = 전부. 런 시작 시점에 고정되어 변경은 현재 런이 아니라 다음 런부터 적용되며, co-op에서는 호스트 설정이 모두에게 적용됩니다.",
            "决定哪些词缀可以掷出（拾取与重铸皆同）。「Enhance only」= 只强化遗物本体数值的词缀（如传奇的锚提供更多格挡）——适合想要纯垂直强化、不要新机制的玩家。「Effects only」= 增加新机制的词缀（嫁接遗物效果、战斗触发、角色词缀）。「Custom」= 逐项自选的词缀/诅咒池（用下方按钮编辑）。「All」（默认）= 全部。仅对之后的锻造生效；联机时以主机设置为准。"));
        // The custom-pool editor row — a Button entry, which is BOTH a new ModConfig type AND a new
        // ModKit API, so it goes through reflection end to end (see Loc()'s skew rationale): an old
        // first-loaded ModKit or an old ModConfig just means no button row, never a dead registration.
        try
        {
            var btnM = b.GetType().GetMethod("Button");
            if (btnM != null)
            {
                btnM.Invoke(b, new object[] { "customPool", "Customize prefixes & curses", "Open",
                    (Action)NCustomPoolPanel.Toggle });
                b.Description("Opens the editor for the Custom pool: every prefix and curse as its own on/off switch. The picks apply while 'Prefix pool' is set to Custom, and take effect from your NEXT run (locked at run start).");
                Loc(b, L("접두사·저주 커스텀", "自定义词缀与诅咒"), L(
                    "Custom 풀 편집기를 엽니다: 모든 접두사·저주를 각각 켜고 끌 수 있습니다. '접두사 풀'이 Custom일 때 적용되며, 변경은 다음 런부터 반영됩니다 (런 시작에 고정).",
                    "打开 Custom 池编辑器：每个词缀与诅咒都可单独开关。当「词缀池」设为 Custom 时生效。"));
                b.GetType().GetMethod("LocalizedButtonTexts")?.Invoke(b,
                    new object[] { new System.Collections.Generic.Dictionary<string, string> { ["kor"] = "열기", ["zh"] = "打开" } });
            }
            else Logger.Info($"[{ModId}] custom-pool button skipped (old ModKit loaded).");
        }
        catch (Exception e) { Logger.Warn($"[{ModId}] custom-pool button registration failed: {e.Message}"); }
        b.Toggle(EntryKeyForgeAncient, "Forge Ancient relics",
                defaultValue: true,
                onChanged: v => { ForgeConfig.ForgeAncientRelics = v; ForgeConfigBroadcaster.BroadcastIfHost(); })
            .Description("Whether Ancient relics get prefixes like any other relic. Turn OFF to leave Ancient relics as pure vanilla — they skip the pickup forge and are hidden from the reforge picker. LOCKED at the start of a run — a change takes effect on your next run.");
        Loc(b, L("고대 유물 재련", "重铸远古遗物"), L(
            "고대(Ancient) 유물도 다른 유물처럼 접두사를 받을지 여부. 끄면 고대 유물은 순수 바닐라로 남습니다 — 획득 재련을 건너뛰고 재련 선택 목록에서도 숨겨집니다. 런 시작 시점에 고정되어 변경은 다음 런부터 적용됩니다.",
            "远古(Ancient)遗物是否像其他遗物一样获得词缀。关闭 = 远古遗物保持纯原版——跳过拾取锻造，并从重铸选择列表中隐藏。对之后锻造（或读档时重新派生）的遗物生效。"));
        b.Slider(EntryKeySelfCurse, "Self-curse share (%)",
                defaultValue: 22.0,
                onChanged: v => { ForgeConfig.SelfCurseShare = v / 100.0; ForgeConfigBroadcaster.BroadcastIfHost(); })
            .Range(0f, 100f, 5f, format: "F0")
            .Description("Of the relics that carry a curse (see 'Curse chance'), the share that get a self-curse — punishes YOU on unblocked hits (Weak/Frail/Vulnerable to self, or a status card) — instead of an enemy-rider curse. 0 = all curses strengthen enemies; 100 = all curses punish you. The two never stack on one relic. Applies from your NEXT run — locked at run start.");
        Loc(b, L("자기 저주 비율 (%)", "自我诅咒占比 (%)"), L(
            "저주가 붙는 유물 중('저주 확률' 참고) 적 강화 저주 대신 자기 저주 — 막지 못한 피격 시 자신에게 약화/손상/취약 또는 상태이상 카드 — 를 받는 비율. 0 = 모든 저주가 적을 강화, 100 = 모든 저주가 나를 벌합니다. 한 유물에 둘이 겹치지 않습니다. 변경은 다음 런부터 적용됩니다 (런 시작에 고정).",
            "在携带诅咒的遗物中（见「诅咒几率」），获得自我诅咒——未格挡受击时对自己施加虚弱/脆弱/易伤或状态牌——而非敌方强化诅咒的占比。0 = 所有诅咒强化敌人；100 = 所有诅咒惩罚自己。两者不会叠加在同一遗物上。"));
        b.Toggle(EntryKeyRoomBroadcast, "Sync forge config every room (co-op)",
                defaultValue: true,
                onChanged: v => ForgeConfig.RoomBroadcastEnabled = v)
            .Description("Co-op only: the host re-sends its forge settings to clients on every room entry so event/reward/treasure relics derive identically. Default ON — leave it on for normal play. Turn OFF only if you get a BLACK SCREEN entering rooms/events in co-op: shop & rest still sync, so only combat-reward/event/treasure curse rolls may differ per client until the next shop/rest. Single-player is unaffected either way.");
        Loc(b, L("방마다 재련 설정 동기화 (co-op)", "每个房间同步重铸设置（联机）"), L(
            "co-op 전용: 호스트가 방 진입마다 재련 설정을 클라이언트에 재전송해 이벤트/보상/보물 유물이 동일하게 파생되게 합니다. 기본 켬 — 평소엔 그대로 두세요. co-op에서 방/이벤트 진입 시 검은 화면이 뜰 때만 끄세요: 상점·휴식은 계속 동기화되므로, 전투 보상/이벤트/보물의 저주 롤만 다음 상점/휴식까지 피어별로 다를 수 있습니다. 싱글플레이는 어느 쪽이든 무관합니다.",
            "仅联机：主机在每次进入房间时向客户端重发重铸设置，使事件/奖励/宝箱遗物派生一致。默认开启——正常游玩请保持。仅当联机进入房间/事件出现黑屏时才关闭：商店与休息仍会同步，只有战斗奖励/事件/宝箱的诅咒掷点可能在下个商店/休息前因端而异。单人游戏不受影响。"));
        b.Register();

        // Now that the keys are registered, read the saved-or-default values.
        ForgeConfig.NoPrefixChance = 1.0 - ModConfigBridge.GetValue<double>(ModId, EntryKeyPrefix, 15.0) / 100.0;
        ForgeConfig.ShopCleanseCost = (int)ModConfigBridge.GetValue<double>(ModId, EntryKeyCleanseCost, 100.0);
        ForgeConfig.EnemyForgeEnabled = ModConfigBridge.GetValue<bool>(ModId, EntryKeyEnemyForge, true);
        ForgeConfig.CurseChance = ModConfigBridge.GetValue<double>(ModId, EntryKeyRiderChance, 33.0) / 100.0;
        ForgeConfig.ForgeAncientRelics = ModConfigBridge.GetValue<bool>(ModId, EntryKeyForgeAncient, true);
        ForgeConfig.SelfCurseShare = ModConfigBridge.GetValue<double>(ModId, EntryKeySelfCurse, 22.0) / 100.0;
        ForgeConfig.RoomBroadcastEnabled = ModConfigBridge.GetValue<bool>(ModId, EntryKeyRoomBroadcast, true);
        ForgeConfig.CampfireCleanseEnabled = ModConfigBridge.GetValue<bool>(ModId, EntryKeyCampCleanse, true);
        ForgeConfig.PrefixPool = PrefixPoolIndexOf(ModConfigBridge.GetValue<string>(ModId, EntryKeyPrefixPool, PrefixPoolOptions[0]) ?? PrefixPoolOptions[0]);
        CustomPool.Load();   // the Custom mode's persisted per-entry picks

        Logger.Info($"[{ModId}] shop reforge cost {ForgeConfig.ShopReforgeBaseCost}g +{ForgeConfig.ShopReforgeCostStep}/reforge, no-prefix chance {ForgeConfig.NoPrefixChance:P0}.");
    }
}

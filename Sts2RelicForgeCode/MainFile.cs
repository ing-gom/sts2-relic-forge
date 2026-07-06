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
    private const string EntryKeyNoPrefix = "noPrefixChance";
    private const string EntryKeyEnemyForge = "enemyForgeEnabled";
    private const string EntryKeyRiderChance = "enemyRiderChance";
    private const string EntryKeyForgeAncient = "forgeAncientRelics";
    private const string EntryKeySelfCurse = "selfCurseChance";
    private const string EntryKeyCleanseCost = "shopCleanseCost";

    public static readonly MegaCrit.Sts2.Core.Logging.Logger Logger
        = ModBootstrap.CreateLogger(ModId);

    public static void Initialize() =>
        ModBootstrap.Run(ModId, Logger, typeof(MainFile).Assembly, body: () =>
        {
            Logger.Info($"[{ModId}] relic-affix prototype active.");
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            // Defer so ModConfig has finished its own Initialize before we Register().
            tree.CreateTimer(0.0).Timeout += RegisterConfig;
        });

    private static void RegisterConfig()
    {
        // Register the entries FIRST. A GetValue BEFORE registration returns default(T)=0 for an
        // unknown key (which clobbered the field defaults — e.g. shop cost fell to 0). After Register,
        // ModConfig knows each key's registered default, so a read returns the saved value or the
        // real default. Persisted values are then applied just below.
        ModConfigBridge.For(ModId, "Relic Forge", Logger)
            .Slider(EntryKeyNoPrefix, "No-prefix chance (%)",
                defaultValue: 45.0,
                onChanged: v => { ForgeConfig.NoPrefixChance = v / 100.0; ForgeConfigBroadcaster.BroadcastIfHost(); })
                .Range(0f, 100f, 5f, format: "F0")
                .Description("Chance a relic gets NO prefix and stays vanilla. 45 = ~55% of relics are enhanced; 0 = every eligible relic gets a prefix. Changing this affects relics forged (or re-derived on load) afterward.")
            .Toggle(EntryKeyEnemyForge, "Enemy forge (elites & bosses fight back)",
                defaultValue: true,
                onChanged: v => { ForgeConfig.EnemyForgeEnabled = v; ForgeConfigBroadcaster.BroadcastIfHost(); })
                .Description("Master switch for the enemy-forge mechanic: when ON, some of your forged relics also carry an 'enemy-rider' curse that strengthens elites & bosses. Turn OFF for a pure power fantasy (the relic forging itself is unaffected).")
            .Slider(EntryKeyRiderChance, "Curse chance (%)",
                defaultValue: 33.0,
                onChanged: v => { ForgeConfig.CurseChance = v / 100.0; ForgeConfigBroadcaster.BroadcastIfHost(); })
                .Range(0f, 100f, 1f, format: "F0")
                .Description("Reference chance a forged relic carries ONE curse — either an enemy-rider curse (strengthens elites & bosses) OR a self-curse (punishes you on unblocked hits), never both. The real chance SCALES with the prefix's power: a weak prefix is rarely cursed, a Legendary almost always. Which kind is set by 'Self-curse share'. Re-forge to re-roll it.")
            .Slider(EntryKeyCleanseCost, "Shop cleanse cost (gold)",
                defaultValue: 150.0,
                onChanged: v => ForgeConfig.ShopCleanseCost = (int)v)
                .Range(0f, 300f, 10f, format: "F0")
                .Description("Gold charged per cleanse at a merchant — removes the curse from a relic while keeping its prefix. Costs more than a reforge because it's a guaranteed upside (no gamble). 0 = free.")
            .Toggle(EntryKeyForgeAncient, "Forge Ancient relics",
                defaultValue: true,
                onChanged: v => { ForgeConfig.ForgeAncientRelics = v; ForgeConfigBroadcaster.BroadcastIfHost(); })
                .Description("Whether Ancient relics get prefixes like any other relic. Turn OFF to leave Ancient relics as pure vanilla — they skip the pickup forge and are hidden from the reforge picker. Affects relics forged (or re-derived on load) afterward.")
            .Slider(EntryKeySelfCurse, "Self-curse share (%)",
                defaultValue: 22.0,
                onChanged: v => { ForgeConfig.SelfCurseShare = v / 100.0; ForgeConfigBroadcaster.BroadcastIfHost(); })
                .Range(0f, 100f, 5f, format: "F0")
                .Description("Of the relics that carry a curse (see 'Curse chance'), the share that get a self-curse — punishes YOU on unblocked hits (Weak/Frail/Vulnerable to self, or a status card) — instead of an enemy-rider curse. 0 = all curses strengthen enemies; 100 = all curses punish you. The two never stack on one relic.")
            .Register();

        // Now that the keys are registered, read the saved-or-default values.
        ForgeConfig.NoPrefixChance = ModConfigBridge.GetValue<double>(ModId, EntryKeyNoPrefix, 45.0) / 100.0;
        ForgeConfig.ShopCleanseCost = (int)ModConfigBridge.GetValue<double>(ModId, EntryKeyCleanseCost, 150.0);
        ForgeConfig.EnemyForgeEnabled = ModConfigBridge.GetValue<bool>(ModId, EntryKeyEnemyForge, true);
        ForgeConfig.CurseChance = ModConfigBridge.GetValue<double>(ModId, EntryKeyRiderChance, 33.0) / 100.0;
        ForgeConfig.ForgeAncientRelics = ModConfigBridge.GetValue<bool>(ModId, EntryKeyForgeAncient, true);
        ForgeConfig.SelfCurseShare = ModConfigBridge.GetValue<double>(ModId, EntryKeySelfCurse, 22.0) / 100.0;

        Logger.Info($"[{ModId}] shop reforge cost {ForgeConfig.ShopReforgeBaseCost}g +{ForgeConfig.ShopReforgeCostStep}/reforge, no-prefix chance {ForgeConfig.NoPrefixChance:P0}.");
    }
}

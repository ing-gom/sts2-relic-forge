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
    private const string EntryKeyBalance = "enemyBalanceStrength";
    private const string EntryKeyEnemyForge = "enemyForgeEnabled";
    private const string EntryKeyRiderChance = "enemyRiderChance";
    private const string EntryKeyShopCost = "shopReforgeCost";

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
                defaultValue: 60.0,
                onChanged: v => ForgeConfig.NoPrefixChance = v / 100.0)
                .Range(0f, 100f, 5f, format: "F0")
                .Description("Chance a relic gets NO prefix and stays vanilla. 60 = only ~40% of relics are enhanced; 0 = every eligible relic gets a prefix. Changing this affects relics forged (or re-derived on load) afterward.")
            .Toggle(EntryKeyEnemyForge, "Enemy forge (elites & bosses fight back)",
                defaultValue: true,
                onChanged: v => ForgeConfig.EnemyForgeEnabled = v)
                .Description("Master switch for the enemy-forge mechanic: when ON, some of your forged relics also carry an 'enemy-rider' curse that strengthens elites & bosses. Turn OFF for a pure power fantasy (the relic forging itself is unaffected).")
            .Slider(EntryKeyRiderChance, "Enemy-rider chance (%)",
                defaultValue: 33.0,
                onChanged: v => ForgeConfig.EnemyRiderChance = v / 100.0)
                .Range(0f, 100f, 1f, format: "F0")
                .Description("Chance a freshly-forged relic ALSO strengthens elites & bosses (only when Enemy forge is ON). Stronger relics that roll the curse strengthen enemies more. Shown on the relic's tooltip. Re-forge to re-roll it.")
            .Slider(EntryKeyBalance, "Enemy balance strength (%)",
                defaultValue: 100.0,
                onChanged: v => ForgeConfig.BalanceStrength = v / 100.0)
                .Range(0f, 200f, 10f, format: "F0")
                .Description("How hard elites & bosses forge back (only when Enemy forge is ON). They always roll their own prefix; forging more at higher Ascension pushes the strength higher. 100 = designed strength.")
            .Slider(EntryKeyShopCost, "Shop reforge cost (gold)",
                defaultValue: 50.0,
                onChanged: v => ForgeConfig.ShopReforgeCost = (int)v)
                .Range(0f, 100f, 10f, format: "F0")
                .Description("Gold charged per reforge at a merchant. Unlimited uses per shop (pay each time), and a penalty prefix can still roll — a paid gamble. 0 = free.")
            .Register();

        // Now that the keys are registered, read the saved-or-default values.
        ForgeConfig.NoPrefixChance = ModConfigBridge.GetValue<double>(ModId, EntryKeyNoPrefix, 60.0) / 100.0;
        ForgeConfig.BalanceStrength = ModConfigBridge.GetValue<double>(ModId, EntryKeyBalance, 100.0) / 100.0;
        ForgeConfig.ShopReforgeCost = (int)ModConfigBridge.GetValue<double>(ModId, EntryKeyShopCost, 50.0);
        ForgeConfig.EnemyForgeEnabled = ModConfigBridge.GetValue<bool>(ModId, EntryKeyEnemyForge, true);
        ForgeConfig.EnemyRiderChance = ModConfigBridge.GetValue<double>(ModId, EntryKeyRiderChance, 33.0) / 100.0;

        Logger.Info($"[{ModId}] shop reforge cost {ForgeConfig.ShopReforgeCost}g, no-prefix chance {ForgeConfig.NoPrefixChance:P0}.");
    }
}

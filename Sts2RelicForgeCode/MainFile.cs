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
        // Pull any persisted value first (percent 0..100) so we don't briefly use the default.
        double savedPct = ModConfigBridge.GetValue<double>(ModId, EntryKeyNoPrefix, 60.0);
        ForgeConfig.NoPrefixChance = savedPct / 100.0;

        ModConfigBridge.For(ModId, "Relic Forge", Logger)
            .Slider(EntryKeyNoPrefix, "No-prefix chance (%)",
                defaultValue: 60.0,
                onChanged: v => ForgeConfig.NoPrefixChance = v / 100.0)
                .Range(0f, 100f, 5f, format: "F0")
                .Description("Chance a relic gets NO prefix and stays vanilla. 60 = only ~40% of relics are enhanced; 0 = every eligible relic gets a prefix. Changing this affects relics forged (or re-derived on load) afterward.")
            .Register();

        Logger.Info($"[{ModId}] no-prefix chance {ForgeConfig.NoPrefixChance:P0}.");
    }
}

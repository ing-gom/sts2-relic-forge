using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// Shared gate for the mod's LOCAL (IsNetworked=false) test/debug console commands. Those commands
/// mutate run state on THIS client only — typing one in a REAL co-op session (not fake-MP) forges /
/// curses / grants on one peer and not the others, which is a guaranteed checksum desync. SP and
/// fake-multiplayer (solo `multiplayer test`) stay fully usable; the synced paths (`rf_sync`,
/// `rf_cleanse`, the built-in networked `relic` command) are the co-op-safe alternatives.
/// </summary>
internal static class LocalCmdGuard
{
    /// <summary>Non-null blocking result when in REAL co-op; null (proceed) in SP / fake-MP / no run.</summary>
    public static CmdResult? BlockInRealCoop()
    {
        var run = RunManager.Instance;
        if (run == null || run.IsSingleplayerOrFakeMultiplayer) return null;
        return new CmdResult(success: false,
            "Disabled in co-op: this test command mutates only THIS client (desync). Play solo to use it.");
    }
}

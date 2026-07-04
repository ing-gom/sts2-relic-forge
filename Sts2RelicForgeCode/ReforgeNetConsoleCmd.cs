using System.Globalization;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Sts2RelicForge;

/// <summary>
/// The NETWORKED transport for co-op reforge. <see cref="ReforgeNet.DispatchNetworked"/> enqueues a
/// <c>ConsoleCmdGameAction</c> carrying "<see cref="Verb"/> &lt;relicEntry&gt; &lt;targetCount&gt;" onto the run's
/// synchronized action stream; the game replays that string through <c>DevConsole.ProcessNetCommand</c>
/// on EVERY client (including the initiator), so this <see cref="Process"/> runs identically everywhere.
/// Each client then steps its OWN copy of the owner's relic up to targetCount via the deterministic
/// re-derivation (<see cref="ReforgeNet.ApplyReforgeStepOnClient"/>) — no result is transmitted, only
/// (relicEntry, targetCount). The forge is a pure function of seed+id+floor+count, so every client
/// converges on the same prefix / companion graft / stats without serializing them.
///
/// This deliberately reuses the game's BUILT-IN <c>NetConsoleCmdGameAction</c> wire type (a plain
/// string payload), so the mod adds NO new <c>INetAction</c> subtype and never perturbs the net
/// type-id ordering — lockstep-safe as long as both clients run this mod. It is issued
/// programmatically by ReforgeNet, not meant for manual typing; <see cref="DebugOnly"/> is false only
/// so it registers (and thus can be dispatched) in normal, non-debug co-op play.
///
/// Auto-registered by the game's DevConsole reflection over GetSubtypesInMods&lt;AbstractConsoleCmd&gt;().
/// </summary>
public sealed class ReforgeNetConsoleCmd : AbstractConsoleCmd
{
    /// <summary>The console verb this registers under. ReforgeNet builds the synced string from it —
    /// keep it short and space-free so it survives the space-delimited console parse.</summary>
    public const string Verb = "rf_sync";

    public override string CmdName => Verb;
    public override string Args => "<relicEntry> <targetCount>";
    public override string Description =>
        "Internal (networked): applies one deterministic reforge step on every co-op client.";
    public override bool IsNetworked => true;   // routes through the synchronized action queue
    public override bool DebugOnly => false;     // must register in normal (non-debug) co-op play

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        // Runs on EVERY client. issuingPlayer is the reforging player, resolved per-client by net id,
        // so ApplyReforgeStepOnClient steps that same player's own copy of the relic on each machine.
        if (issuingPlayer == null)
            return new CmdResult(success: false, "rf_sync: no active player.");
        if (args.Length < 2)
            return new CmdResult(success: false, "Usage: rf_sync <relicEntry> <targetCount>");
        if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int targetCount))
            return new CmdResult(success: false, $"rf_sync: bad count '{args[1]}'.");

        ReforgeNet.ApplyReforgeStepOnClient(issuingPlayer, args[0], targetCount);
        return new CmdResult(success: true, $"rf_sync {args[0]} -> {targetCount}");
    }
}

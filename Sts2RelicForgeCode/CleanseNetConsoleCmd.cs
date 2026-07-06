using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Sts2RelicForge;

/// <summary>
/// The NETWORKED transport for co-op CLEANSE — sibling to <see cref="ReforgeNetConsoleCmd"/>.
/// <see cref="ReforgeNet.Cleanse"/> enqueues a <c>ConsoleCmdGameAction</c> carrying
/// "<see cref="Verb"/> &lt;relicEntry&gt;" onto the run's synchronized action stream; the game replays that
/// string through <c>DevConsole.ProcessNetCommand</c> on EVERY client (including the initiator), so this
/// <see cref="Process"/> runs identically everywhere and each client strips the curse on its OWN copy of
/// the owner's relic via <see cref="ReforgeNet.ApplyCleanseOnClient"/>. Unlike a reforge (which is a
/// deterministic function of seed+id+floor+count), a cleanse is a player DECISION, so the action itself
/// must be transmitted — but it carries only (relicEntry); the strip is idempotent so no result needs
/// serializing.
///
/// Reuses the game's BUILT-IN <c>NetConsoleCmdGameAction</c> wire type (a plain string payload), so the
/// mod adds NO new <c>INetAction</c> subtype and never perturbs the net type-id ordering — lockstep-safe
/// as long as both clients run this mod. Issued programmatically by ReforgeNet, not for manual typing;
/// <see cref="DebugOnly"/> is false only so it registers (and thus can be dispatched) in normal co-op.
///
/// Auto-registered by the game's DevConsole reflection over GetSubtypesInMods&lt;AbstractConsoleCmd&gt;().
/// </summary>
public sealed class CleanseNetConsoleCmd : AbstractConsoleCmd
{
    /// <summary>The console verb this registers under. ReforgeNet builds the synced string from it —
    /// keep it short and space-free so it survives the space-delimited console parse.</summary>
    public const string Verb = "rf_cleanse";

    public override string CmdName => Verb;
    public override string Args => "<relicEntry>";
    public override string Description =>
        "Internal (networked): strips a relic's forge curse on every co-op client.";
    public override bool IsNetworked => true;   // routes through the synchronized action queue
    public override bool DebugOnly => false;     // must register in normal (non-debug) co-op play

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        // Runs on EVERY client. issuingPlayer is the cleansing player, resolved per-client by net id,
        // so ApplyCleanseOnClient strips that same player's own copy of the relic on each machine.
        if (issuingPlayer == null)
            return new CmdResult(success: false, "rf_cleanse: no active player.");
        if (args.Length < 1)
            return new CmdResult(success: false, "Usage: rf_cleanse <relicEntry>");

        ReforgeNet.ApplyCleanseOnClient(issuingPlayer, args[0]);
        return new CmdResult(success: true, $"rf_cleanse {args[0]}");
    }
}

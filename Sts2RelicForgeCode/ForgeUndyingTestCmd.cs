using System;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2RelicForge;

/// <summary>
/// Dev-console command <c>forgeundyingtest [die]</c> — a self-contained, deterministic demo of the
/// Undying (불사의) prefix's death-deferral, for verifying it in a live run (the full SoloTest battery
/// can't reach it — it stalls in the unrelated T13 rewind test). Must be run IN COMBAT.
///
/// It force-forges an Undying relic onto you, applies Doom == your current HP (so you are doomed), then
/// runs the real turn-end kill choke DoomPower.DoomKill. You SURVIVE (HP unchanged) and the reprieve
/// arms — proving 종말로 즉사하지 않는다. Default then removes the Doom so you keep playing; the optional
/// <c>die</c> arg instead deals 1 unblocked damage to witness the deferred death fire (그 뒤 피가 감소하면
/// 죽는다) — which ends your run, so it is opt-in. Results are logged with the [Sts2RelicForge] tag.
///
/// Local-only (IsNetworked=false, blocked in real co-op) — it mutates one client's run state directly.
/// Auto-registered by DevConsole reflection over AbstractConsoleCmd.
/// </summary>
public class ForgeUndyingTestCmd : AbstractConsoleCmd
{
    public override string CmdName => "forgeundyingtest";
    public override string Args => "[die|clear]";
    public override string Description =>
        "In combat: grants Undying + dooms you to HP, then LEAVES you doomed so you can watch Doom NOT kill you at turn end. 'die' takes a hit to show the deferred death (ends your run); 'clear' removes the test Doom.";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    private enum Mode { Setup, Die, Clear }

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (LocalCmdGuard.BlockInRealCoop() is { } blocked) return blocked;   // local mutation = desync in real co-op
        if (issuingPlayer?.Creature == null)
            return new CmdResult(success: false, "No active player — start a run first.");

        string arg = args.Length >= 1 ? args[0].ToLowerInvariant() : "";
        Mode mode = arg == "die" ? Mode.Die : arg == "clear" ? Mode.Clear : Mode.Setup;

        if (mode == Mode.Clear)
            return new CmdResult(RunClear(issuingPlayer), success: true, "Clearing the test Doom from you.");

        var cm = CombatManager.Instance;
        if (cm == null || !cm.IsInProgress)
            return new CmdResult(success: false, "Start a COMBAT first — the Doom death fires at turn end.");

        return new CmdResult(
            RunTest(issuingPlayer, mode == Mode.Die),
            success: true,
            mode == Mode.Die
                ? "Undying LETHAL demo — you WILL die to show the deferred death. Watch the log."
                : "Undying set up: you are now DOOMED. End your turn → Doom won't kill you. Take a hit → you die. Watch HP + log. ('forgeundyingtest clear' to remove it.)");
    }

    private static async System.Threading.Tasks.Task RunTest(Player player, bool lethal)
    {
        void Log(string m) => MainFile.Logger.Info($"[{MainFile.ModId}] [undying-test] {m}");
        try
        {
            var body = player.Creature;
            var rs = player.RunState;

            // 1) force-forge an Undying relic onto the player (same idiom as SoloTest.Grant20).
            RelicModel host = ModelDb.Relic<Anchor>().ToMutable();
            RelicForgeService.Forge(host, rs.Rng.Seed, rs.TotalFloor, forced: PrefixTable.ByName("Undying"));
            var rec = RelicForgeService.RecordFor(host);
            if (rec != null) { rec.EnemyRider = false; rec.EnemyRiderSuffix = ""; if (rec.SelfCurse.Length > 0) rec.SelfCurse = ""; }
            await RelicCmd.Obtain(host, player);
            Log($"granted {host.Id.Entry} (prefix={rec?.Prefix ?? "?"})");

            var ctx = new BlockingPlayerChoiceContext();

            // 2) apply Doom == current HP → the player is now doomed (CurrentHp <= Amount).
            int hp = body.CurrentHp;
            await PowerCmd.Apply<DoomPower>(ctx, body, hp, body, null);
            var doom = body.GetPower<DoomPower>();
            Log($"applied Doom={hp} at HP={hp} → doomed={doom?.IsOwnerDoomed() == true}");

            // 3) run the real turn-end kill choke → Undying strips the player out, so they SURVIVE.
            await DoomPower.DoomKill(DoomPower.GetDoomedCreatures(new[] { body }));
            bool armed = CharAffix.IsDoomReprieveArmed(player);
            Log($"after turn-end DoomKill: alive={body.IsAlive} HP={body.CurrentHp} reprieveArmed={armed}");
            if (!body.IsAlive) { Log("FAIL: player died at turn end — deferral broken."); return; }
            if (!armed)        { Log("FAIL: survived but reprieve not armed — a later HP loss would never kill."); return; }
            Log("OK ✓ survived turn-end Doom, HP unchanged, reprieve armed (죽음 유예).");

            // 4) the follow-up: HP loss while doomed spends the reprieve → death.
            if (lethal)
            {
                Log("dealing 1 unblocked damage while doomed → the deferred death should fire…");
                await CreatureCmd.Damage(ctx, body, 1m, ValueProp.Unblockable | ValueProp.Unpowered, null, null);
                Log($"after 1 dmg: alive={body.IsAlive} (expected dead)");
                Log(body.IsAlive ? "FAIL: still alive after HP loss while doomed."
                                 : "OK ✓ died on HP loss while doomed (유예 소진).");
            }
            else
            {
                // LEAVE the Doom on the player so the deferral is observable in normal play: end your turn
                // and Doom won't kill you; take any hit and you die. 'forgeundyingtest clear' removes it.
                Log($"Doom={hp} LEFT ON you (doomed). End your turn → survive; take a hit → die. 'forgeundyingtest clear' to remove.");
            }
        }
        catch (Exception e) { Log("ERROR: " + e.Message); }
    }

    private static async System.Threading.Tasks.Task RunClear(Player player)
    {
        void Log(string m) => MainFile.Logger.Info($"[{MainFile.ModId}] [undying-test] {m}");
        try
        {
            if (player.Creature?.GetPower<DoomPower>() != null)
            {
                await PowerCmd.Remove<DoomPower>(player.Creature);
                Log("cleared the test Doom — you are safe now.");
            }
            else Log("no Doom on you to clear.");
        }
        catch (Exception e) { Log("ERROR: " + e.Message); }
    }

    public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
    {
        if (args.Length <= 1)
        {
            string partial = args.Length == 1 ? args[0] : string.Empty;
            return CompleteArgument(new System.Collections.Generic.List<string> { "die", "clear" }, Array.Empty<string>(), partial,
                CompletionType.Argument, (c, p) => c.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }
        return new CompletionResult { Type = CompletionType.Argument, ArgumentContext = CmdName };
    }
}

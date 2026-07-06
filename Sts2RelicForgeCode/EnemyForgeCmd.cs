using System;
using System.Text;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Sts2RelicForge;

/// <summary>
/// Dev-console command <c>enemyforge [on|off|boss|list|&lt;prefix&gt;]</c> — inspects and tests the
/// "enemy forge" mechanism (see <see cref="EnemyForge"/>). No arg reports rider heat + magnitude.
/// on/off toggle a test override that forges any next combat; boss treats it as a boss (×1.5);
/// list shows all prefixes; a name forces that prefix.
///
/// Auto-registered by DevConsole reflection over AbstractConsoleCmd. See ForgeConsoleCmd.
/// </summary>
public class EnemyForgeCmd : AbstractConsoleCmd
{
    public override string CmdName => "enemyforge";
    public override string Args => "[on|off|boss|relics|list|<prefix>]";
    public override string Description =>
        "Tests the enemy-forge mechanism. on/off/boss toggle a preview; relics=your rider-cursed relics; list=all prefixes; <name>=force one prefix.";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (args.Length >= 1)
        {
            string a = args[0].ToLowerInvariant();
            string? nameArg = a == "name" && args.Length >= 2 ? args[1] : null;

            if (a is "off" or "0" or "false")
            {
                EnemyForge.TestForce = EnemyForge.TestAsBoss = false;
                EnemyForge.TestForcePrefix = null;
            }
            else if (a is "on" or "1" or "true") { EnemyForge.TestForce = true; EnemyForge.TestAsBoss = false; EnemyForge.TestForcePrefix = null; }
            else if (a is "boss") { EnemyForge.TestForce = true; EnemyForge.TestAsBoss = true; EnemyForge.TestForcePrefix = null; }
            else if (a is "list")
                return new CmdResult(success: true, ListPrefixes());
            else if (a is "relics")
                return new CmdResult(success: true, ListRiderRelics(issuingPlayer));
            else
            {
                string want = nameArg ?? args[0];
                var sfx = RiderSuffix.Find(want);
                if (sfx == null)
                    return new CmdResult(success: false, $"Unknown suffix '{want}'. Try `enemyforge list`.");
                EnemyForge.TestForce = true; EnemyForge.TestAsBoss = true;
                EnemyForge.TestForcePrefix = sfx.En;
            }
        }

        var log = new StringBuilder("enemyforge:\n");
        string state = !EnemyForge.TestForce ? "off"
            : EnemyForge.TestForcePrefix != null ? $"ON — forcing '{EnemyForge.TestForcePrefix}' on the next combat"
            : EnemyForge.TestAsBoss ? "ON as BOSS (next combat, for HP-curse scope)"
            : "ON (next combat gets prefixes)";
        log.Append("  test override: ").Append(state).Append('\n');

        if (issuingPlayer == null)
        {
            log.Append("  (no active player — start a run to see heat/magnitude)");
            return new CmdResult(success: true, log.ToString());
        }

        double heat = EnemyForge.ForgeHeat(issuingPlayer);
        double mag = EnemyForge.Magnitude(issuingPlayer);
        bool on = ForgeConfig.EnemyForgeEnabled;

        log.Append($"  enemy forge: {(on ? "ON" : "OFF")}  ·  curse chance {ForgeConfig.CurseChance:P0} (enemy-rider share {(1 - ForgeConfig.SelfCurseShare):P0})\n");
        log.Append($"  rider heat: {heat:F2}  (from relics carrying the enemy-rider curse)\n");
        log.Append($"  balance: {ForgeConfig.BalanceStrength:P0} (fixed designed strength)\n");
        log.Append($"  => magnitude: {mag:F2}  {(mag <= 0 ? "(no enemy buff — off or no rider relics)" : "(elites/bosses buffed)")}");
        return new CmdResult(success: true, log.ToString());
    }

    /// <summary>Lists the player's owned relics that currently carry the enemy-rider curse.</summary>
    private static string ListRiderRelics(Player? player)
    {
        if (player == null) return "No active player — start a run first.";
        var sb = new StringBuilder("relics carrying the enemy-rider curse:\n");
        int n = 0;
        double heat = 0;
        foreach (var relic in player.Relics)
        {
            var rec = RelicForgeService.RecordFor(relic);
            if (rec == null || !rec.EnemyRider) continue;
            double c = Math.Max(0.15, rec.Percent);
            heat += c;
            n++;
            string suffix = RiderSuffix.Localize(rec.EnemyRiderSuffix);
            sb.Append($"  ★ {relic.Id.Entry}  [{PrefixTable.Localize(rec.Prefix)} · {suffix}]  +{c:F2}\n");
        }
        if (n == 0) sb.Append("  (none — forge relics; ~").Append((ForgeConfig.CurseChance * (1 - ForgeConfig.SelfCurseShare) * 100).ToString("F0")).Append("% roll the enemy-rider curse)\n");
        sb.Append($"  total rider heat: {heat:F2}  →  magnitude {EnemyForge.Magnitude(player):F2}");
        return sb.ToString();
    }

    private static string ListPrefixes()
    {
        var sb = new StringBuilder("rider suffixes (force with `enemyforge <name>`):\n");
        foreach (var s in RiderSuffix.All)
            sb.Append($"  {s.En}  →  {s.Effect}\n");
        return sb.ToString();
    }

    public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
    {
        if (args.Length <= 1)
        {
            string partial = args.Length == 1 ? args[0] : string.Empty;
            var candidates = new System.Collections.Generic.List<string> { "on", "off", "boss", "relics", "list" };
            foreach (var s in RiderSuffix.All) candidates.Add(s.En);
            return CompleteArgument(candidates, System.Array.Empty<string>(), partial, CompletionType.Argument,
                (candidate, p) => candidate.StartsWith(p, System.StringComparison.OrdinalIgnoreCase));
        }
        return new CompletionResult { Type = CompletionType.Argument, ArgumentContext = CmdName };
    }
}

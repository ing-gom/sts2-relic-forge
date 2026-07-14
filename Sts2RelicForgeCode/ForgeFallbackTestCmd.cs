using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicForge;

/// <summary>
/// Dev-console command <c>forgefallback</c> — grants test relics for the FALLBACK feature so it can be
/// verified in one combat: one relic per fallback prefix (buff Honed/Bulwarked/Nimble/Barbed + penalty
/// Sapped/Wilted/Exposed), each pinned to FallbackPercent 100 so its combat-start chance buff fires every
/// fight; plus a few tier TIE-BREAK demos (a low tier forced onto a mappable-var relic where it rounds to
/// the same delta as the tier below it, so the tie-break chance buff kicks in), also pinned to 100%.
///
/// Console-granted relics don't persist, so re-run after a restart. Auto-registered by DevConsole
/// reflection over AbstractConsoleCmd (see ForgeConsoleCmd).
/// </summary>
public class ForgeFallbackTestCmd : AbstractConsoleCmd
{
    public override string CmdName => "forgefallback";
    public override string Args => "";
    public override string Description =>
        "Grants test relics forced with each fallback prefix (buff + penalty) at 100% fire, plus a few tier tie-break demos, to verify the combat-start chance buffs.";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    // Bespoke-reward / card-select relics make poor test hosts (they rewrite their own tooltip / hard-cap).
    private static readonly HashSet<string> Skip = new() { "LostCoffer", "NeowsTalisman", "HeftyTablet", "Toolbox" };
    // Vars the tie-break can grant a chance-of-more of (mirrors RelicForgeService.VarStat).
    private static readonly HashSet<string> Mappable = new() { "StrengthPower", "DexterityPower", "ThornsPower", "Block" };

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (LocalCmdGuard.BlockInRealCoop() is { } blocked) return blocked;   // local-only mutation = desync in real co-op
        if (issuingPlayer == null)
            return new CmdResult(success: false, "No active player — start a run first.");

        var runState = issuingPlayer.RunState;
        uint seed = runState.Rng.Seed;
        int floor = runState.TotalFloor;

        // Distinct benign host TYPES — the fallback effect is host-independent, so any relic works.
        var hostTypes = ModelDb.AllRelics
            .Where(r => PrefixTable.Eligible.Contains(r.Rarity) && !Skip.Contains(r.GetType().Name))
            .OrderBy(r => r.Id.Entry)
            .Select(r => r.GetType())
            .Distinct()
            .ToList();

        var grants = new List<RelicModel>();
        var names = new List<string>();
        int hi = 0;

        // 1) One relic per fallback prefix (buff + penalty), forced and pinned to fire every combat.
        foreach (var fb in PrefixTable.All.Where(p => p.IsFallback))
        {
            if (hi >= hostTypes.Count) break;
            var host = Fresh(hostTypes[hi++]);
            RelicForgeService.Forge(host, seed, floor, fb);
            ForceFire(host);
            grants.Add(host);
            names.Add(fb.Name);
        }

        // 2) Up to 3 tier tie-break demos: a low tier forced onto a mappable-var relic where it ties the
        //    tier just below it (so ApplyTierTiebreak sets the chance buff). Pinned to fire.
        var tiers = new[] { PrefixTable.ByName("Hurtful"), PrefixTable.ByName("Forceful"), PrefixTable.ByName("Superior") }
            .Where(p => p != null).Select(p => p!).ToArray();
        int demos = 0;
        foreach (var proto in ModelDb.AllRelics.OrderBy(r => r.Id.Entry))
        {
            if (demos >= 3) break;
            if (!PrefixTable.Eligible.Contains(proto.Rarity) || Skip.Contains(proto.GetType().Name)) continue;
            if (!proto.ToMutable().DynamicVars.Values.Any(dv => Mappable.Contains(dv.Name) && dv.BaseValue > 0)) continue;

            foreach (var tier in tiers)
            {
                var host = proto.ToMutable();
                RelicForgeService.Forge(host, seed, floor, tier);
                var rec = RelicForgeService.RecordFor(host);
                if (rec != null && rec.HasChanges && rec.FallbackStat.Length > 0)   // scaled AND tied
                {
                    ForceFire(host);
                    grants.Add(host);
                    names.Add($"{tier.Name}+tie");
                    demos++;
                    break;
                }
            }
        }

        if (grants.Count == 0)
            return new CmdResult(success: false, "No eligible hosts found.");

        return new CmdResult(GrantAll(issuingPlayer, grants), success: true,
            $"Granting {grants.Count} fallback-test relics (100% fire): {string.Join(", ", names)}. Enter combat to see them fire.");
    }

    private static RelicModel Fresh(Type t) => ModelDb.AllRelics.First(r => r.GetType() == t).ToMutable();

    /// <summary>Force the relic's combat-start buff to fire EVERY fight for testing — WITHOUT touching the
    /// displayed chance, so the tooltip still shows the real tier odds (5–60% / band values).</summary>
    private static void ForceFire(RelicModel host)
    {
        var rec = RelicForgeService.RecordFor(host);
        if (rec != null && rec.FallbackStat.Length > 0) RelicForgeService.MarkForceFire(host);
    }

    private static async Task GrantAll(Player player, List<RelicModel> hosts)
    {
        foreach (var h in hosts)
            await RelicCmd.Obtain(h, player);
    }

    public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
        => new CompletionResult { Type = CompletionType.Argument, ArgumentContext = CmdName };
}

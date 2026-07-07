using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// A campfire (rest site) option that re-rolls the prefix on one of the player's forged relics.
/// Choosing it consumes the rest (like Heal/Smith) — that is the cost. The player picks which
/// relic via the game's own relic-selection screen; the pick is re-forged with a bumped count
/// (deterministic + persisted, see RelicForgeService.Reforge). Enabled only while the player owns
/// at least one relic that carries a forge record.
///
/// Added to the rest site by RestSiteReforgeOptionPatch (postfix on RestSiteOption.Generate), so
/// it rides in the synchronizer's option list and index-based selection / MP sync just work.
/// </summary>
internal sealed class ReforgeRestSiteOption : RestSiteOption
{
    public const string Id = "REFORGE";

    public override string OptionId => Id;

    // Set once a reforge lands a PENALTY prefix: this campfire's reforge action is over (the forge
    // "goes cold"). Only THIS option ends — heal/smith and the rest continue as normal.
    private bool _ended;

    // Icon (res://images/ui/rest_site/option_reforge.png) ships in the mod's .pck, so the base
    // Icon/AssetPaths members resolve it normally — no override needed.

    // The base IsEnabled is a read-only virtual (`=> true`) with NO setter — reflectively "setting"
    // it was a silent no-op, which is why the option never greyed. OVERRIDE it to compute live:
    // disabled once a penalty ended reforging, or when the player has nothing reforgeable. The rest
    // button reads _option.IsEnabled for its greyscale tint + clickability, so both now follow this.
    public override bool IsEnabled => !_ended && RestSiteReforgeSupport.HasReforgeable(Owner);

    public override LocString Description
        => new LocString("rest_site_ui",
            "OPTION_" + Id + (_ended ? ".descriptionEnded" : IsEnabled ? ".description" : ".descriptionDisabled"));

    public ReforgeRestSiteOption(Player owner) : base(owner) { }

    public override async Task<bool> OnSelect()
    {
        if (_ended) return false;   // a penalty roll already ended reforging at this campfire

        // CO-OP: RestSiteSynchronizer.ChooseOption runs this OnSelect on EVERY client — the acting
        // player's choice is replayed on peers via OptionIndexChosenMessage. Our relic picker is a
        // LOCAL, un-synced UI, so it must open ONLY on the acting player's own machine. On a peer,
        // do nothing here: the reforge still replicates through the synced rf_sync command the acting
        // client dispatches (see ReforgeNet.Reforge). Without this gate the picker popped up on
        // teammates' screens showing — and letting them click — the actor's relics, and a peer's
        // stray pick double-dispatched the reforge, desyncing the session. SP / fake-MP: Owner is the
        // local player (IsMe true), so the picker opens exactly as before.
        var run = RunManager.Instance;
        if (run != null && !run.IsSingleplayerOrFakeMultiplayer && !LocalContext.IsMe(Owner))
            return false;

        var candidates = RestSiteReforgeSupport.Reforgeable(Owner).ToList();
        if (candidates.Count == 0) return false;

        // Our own scrolling grid picker (game's screen clips a full inventory into one row). Returns
        // null only if the player cancels with Escape.
        RelicModel? chosen = await NReforgeRelicPicker.Show(candidates, NRestSiteRoom.Instance);

        if (chosen != null)
        {
            var outcome = ReforgeNet.Reforge(chosen, Owner);
            chosen.Flash();                                  // pulse the re-forged relic for feedback
            if (outcome == RelicForgeService.ReforgeOutcome.RolledPenalty)
            {
                // Gambled once too often: the forge goes cold. End THIS action only (return false —
                // rest not consumed, fire stays lit, heal/smith remain). IsEnabled now returns false;
                // rebuild the option buttons so THIS one comes back greyed + unclickable immediately.
                _ended = true;
                NRestSiteRoom.Instance?.CallDeferred(NRestSiteRoom.MethodName.UpdateRestSiteOptions);
            }
        }

        // Return false so the rest is NOT consumed — the player can pick "Reforge" again to re-roll
        // more relics (until a penalty ends it) before finally healing / smithing / leaving.
        return false;
    }

    public override Task DoLocalPostSelectVfx(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Shared helpers for the reforge rest option: eligibility, localization, and icon reuse.</summary>
internal static class RestSiteReforgeSupport
{
    /// <summary>The reforge option instance for each player at the current rest site, keyed by
    /// NetId. Populated per-player in RestSiteReforgeOptionPatch (during RestSiteOption.Generate,
    /// which runs for every player on every client), so ReaddReforgeAfterChoosePatch can re-add the
    /// SAME instance (preserving its penalty-ended state) after Heal/Smith clears a player's option
    /// list. Keyed per-player — NOT a single static — so co-op peers resolve the correct owner's
    /// option and the synchronizer's per-player option lists stay identical across clients.</summary>
    public static readonly Dictionary<ulong, ReforgeRestSiteOption> ByPlayer = new();

    /// <summary>
    /// Every owned, player-chosen relic can be reforged — whether it currently has a prefix, rolled
    /// "no prefix", or was never eligible on pickup (a deliberate reforge always lands a prefix).
    /// Hidden companion instances (grafted donors) are excluded, since those aren't relics the
    /// player picked; Ancient (先古) relics are excluded too while <see cref="ForgeConfig.ForgeAncientRelics"/>
    /// is off, so the opt-out leaves them untouched at campfires and shops as well as on pickup.
    /// </summary>
    public static IEnumerable<RelicModel> Reforgeable(Player player)
        => player.Relics.Where(r => !RelicForgeService.IsCompanion(r)
            && (HostForgeConfig.ForgeAncient || r.Rarity != RelicRarity.Ancient));

    public static bool HasReforgeable(Player player) => Reforgeable(player).Any();

    /// <summary>Owned relics that currently carry a curse (enemy-rider OR self-curse) — the only relics a
    /// shop CLEANSE can act on. Unlike <see cref="Reforgeable"/>, cleanse deliberately does NOT honor the
    /// ForgeAncientRelics opt-out: that option only blocks ADDING a prefix/curse to Ancient (先古) relics,
    /// but a curse already sitting on one (forged before the toggle was flipped, or via a forced command)
    /// must stay removable — otherwise it would be trapped on the relic forever. Hidden companion instances
    /// are excluded since they carry no curse of their own.</summary>
    public static IEnumerable<RelicModel> Cleansable(Player player)
        => player.Relics.Where(r =>
        {
            if (RelicForgeService.IsCompanion(r)) return false;
            var rec = RelicForgeService.RecordFor(r);
            return rec != null && (rec.EnemyRider || rec.SelfCurse.Length > 0);
        });

    public static bool HasCleansable(Player player) => Cleansable(player).Any();

    private static string Localize(string ko, string zh, string en)
    {
        string lang = LocManager.Instance?.Language ?? "";
        if (lang.StartsWith("ko")) return ko;
        if (lang.StartsWith("zh")) return zh;
        return en;
    }

    // Inject our option's loc keys into the live rest_site_ui table (the game itself uses this
    // table for SMITH/HEAL, so it always exists at a rest site). Re-run each time the rest site
    // generates options, which also re-applies the correct strings after a language change.
    public static void EnsureLoc()
    {
        var table = LocManager.Instance.GetTable("rest_site_ui");
        table.MergeWith(new Dictionary<string, string>
        {
            ["OPTION_REFORGE.name"] = Localize("재련", "重铸", "Reforge"),
            ["OPTION_REFORGE.description"] =
                Localize("유물 하나를 선택해 재련합니다. 휴식을 소모하지 않아 여러 번 할 수 있습니다. 유물에 안좋은 기운이 서리면 재련이 불가능해집니다.",
                         "选择一件遗物进行重铸。不消耗休息，可多次使用。但若遗物沾染不祥之气，将无法再重铸。",
                         "Pick a relic and reforge it. Doesn't use up your rest — repeatable. But if an ill aura settles on a relic, you can no longer reforge here."),
            ["OPTION_REFORGE.descriptionDisabled"] =
                Localize("재련할 강화된 유물이 없습니다.", "没有可重铸的已强化遗物。",
                         "You have no forged relic to reforge."),
            ["OPTION_REFORGE.descriptionEnded"] =
                Localize("유물에 안좋은 기운이 서려 대장간의 불이 식었습니다. 이번 휴식에선 더 재련할 수 없습니다.",
                         "遗物沾染了不祥之气，炉火已冷。本次休息无法再重铸。",
                         "An ill aura settles over the relic and the forge goes cold — no more reforging at this rest."),
        });
    }
}

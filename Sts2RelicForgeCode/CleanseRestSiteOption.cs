using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2RelicForge;

/// <summary>
/// A campfire (rest site) option that CLEANSES the curse from one of the player's cursed relics —
/// the free, campfire counterpart to the shop's paid cleanse (<see cref="NMerchantCleanseButton"/>).
/// Unlike the campfire REFORGE option (free + repeatable), cleanse is limited to ONE use per rest-site
/// visit: after a successful cleanse the option greys for the rest of the visit (<see cref="_used"/>),
/// so a rest site can undo at most one curse. The player picks which cursed relic via the game's own
/// relic picker; the cleanse rides <see cref="ReforgeNet.Cleanse"/>, so it strips the curse on EVERY
/// co-op client (a local-only strip would desync each peer's re-derived curse effects).
///
/// Added to the rest site by <see cref="RestSiteReforgeOptionPatch"/> (same postfix that adds Reforge),
/// so it rides in the synchronizer's option list and index-based selection / MP sync just work. The
/// icon resolves from the base <see cref="RestSiteOption"/> as
/// <c>res://images/ui/rest_site/option_cleanse.png</c> (OptionId "CLEANSE", lower-cased) — shipped in
/// the mod's .pck next to option_reforge.png.
/// </summary>
internal sealed class CleanseRestSiteOption : RestSiteOption
{
    public const string Id = "CLEANSE";

    public override string OptionId => Id;

    // One cleanse per rest-site visit. Initiator-local, per-visit (a fresh instance each visit resets
    // it), NOT synced/persisted — like ReforgeRestSiteOption._ended it only greys IsEnabled and never
    // removes the option from the list, so it can never diverge the co-op option lists.
    private bool _used;

    // Greys once this visit's single cleanse is spent OR nothing is cleansable (no cursed relic). The
    // rest button reads IsEnabled for its tint + clickability.
    public override bool IsEnabled => !_used && RestSiteReforgeSupport.HasCleansable(Owner);

    public override LocString Description =>
        new LocString("rest_site_ui", "OPTION_" + Id + DescriptionSuffix());

    private string DescriptionSuffix()
    {
        if (_used) return ".descriptionEnded";
        if (!RestSiteReforgeSupport.HasCleansable(Owner)) return ".descriptionDisabled";
        return ".description";
    }

    public CleanseRestSiteOption(Player owner) : base(owner) { }

    public override async Task<bool> OnSelect()
    {
        if (_used) return false;   // this visit's single cleanse is already spent

        // CO-OP: RestSiteSynchronizer.ChooseOption runs this OnSelect on EVERY client — the acting
        // player's choice is replayed on peers. Our relic picker is a LOCAL, un-synced UI, so it must
        // open ONLY on the acting player's own machine; the cleanse itself still replicates through the
        // synced rf_cleanse command ReforgeNet.Cleanse dispatches. Same gate as ReforgeRestSiteOption.
        var run = RunManager.Instance;
        if (run != null && !run.IsSingleplayerOrFakeMultiplayer && !LocalContext.IsMe(Owner))
            return false;

        // Runs inside the awaited rest-site option task — an escaped exception faults that chain (the
        // campfire black-screen class). Guard everything past the cheap gates; on failure log + return
        // false (rest not consumed, campfire still usable). Same discipline as ReforgeRestSiteOption.
        try
        {
            var candidates = RestSiteReforgeSupport.Cleansable(Owner).ToList();
            if (candidates.Count == 0) return false;

            RelicModel? chosen = await NReforgeRelicPicker.Show(candidates, NRestSiteRoom.Instance);

            // Only spend the visit's cleanse on a real pick that actually carried a curse to remove.
            if (chosen != null && ReforgeNet.Cleanse(chosen, Owner))
            {
                chosen.Flash();                                  // pulse the cleansed relic for feedback
                _used = true;                                    // one cleanse per rest site — grey it now
                MainFile.Logger.Info($"[{MainFile.ModId}] campfire cleanse: {chosen.Id.Entry}.");
                // Rebuild the options so THIS one greys (used) — and Reforge re-evaluates (a cleansed
                // relic becomes reforgeable again).
                NRestSiteRoom.Instance?.CallDeferred(NRestSiteRoom.MethodName.UpdateRestSiteOptions);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] campfire cleanse failed: {e}");
        }

        // Return false so the rest is NOT consumed — cleanse is a free side-action; the player can still
        // heal / smith / reforge afterward (cleanse itself is capped to one via _used).
        return false;
    }

    public override Task DoLocalPostSelectVfx(CancellationToken ct = default) => Task.CompletedTask;
}

using System;
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

    // Icon (res://images/ui/rest_site/option_reforge.png) ships in the mod's .pck, so the base
    // Icon/AssetPaths members resolve it normally — no override needed.

    // Per-VISIT location aura: each reforge at THIS campfire fills it 5–20% (LocationGaugeStep); at 100% the
    // forge goes cold here (_ended) and this option greys for the visit. Initiator-local, per-visit (fresh
    // instance resets it), not synced/persisted — a session budget ON TOP of the per-relic curse gauge.
    private int _locGauge;
    private int _reforges;
    private bool _ended;

    // Greys once the location aura fills (session budget spent) OR nothing is reforgeable (every relic
    // cursed/saturated/volatile — see Reforgeable). The rest button reads IsEnabled for its tint + clickability.
    public override bool IsEnabled => !_ended && RestSiteReforgeSupport.HasReforgeable(Owner);

    public override LocString Description
    {
        get
        {
            var ls = new LocString("rest_site_ui", "OPTION_" + Id + DescriptionSuffix());
            ls.Add("Pct", _locGauge);   // fills the {Pct} placeholder in the location-aura band texts
            return ls;
        }
    }

    // Base → escalating location-aura bands as it fills → the ended line once it hits 100%.
    private string DescriptionSuffix()
    {
        if (_ended) return ".descriptionEnded";
        if (!RestSiteReforgeSupport.HasReforgeable(Owner)) return ".descriptionDisabled";
        return _locGauge <= 0 ? ".description" : ".descriptionLoc" + RelicForgeService.LocationGaugeBand(_locGauge);
    }

    public ReforgeRestSiteOption(Player owner) : base(owner) { }

    public override async Task<bool> OnSelect()
    {
        if (_ended) return false;   // location aura full — the forge is cold at this campfire this visit

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

        // This whole flow runs inside the awaited rest-site option task (RestSiteSynchronizer.ChooseOption)
        // — an escaped exception faults that chain = the campfire black-screen class. Guard everything past
        // the cheap gates; on failure log + return false (rest not consumed, campfire still usable).
        try
        {
            var candidates = RestSiteReforgeSupport.Reforgeable(Owner).ToList();
            if (candidates.Count == 0) return false;

            // Our own scrolling grid picker (game's screen clips a full inventory into one row). Returns
            // null only if the player cancels with Escape.
            RelicModel? chosen = await NReforgeRelicPicker.Show(candidates, NRestSiteRoom.Instance);

            if (chosen != null)
            {
                ReforgeNet.Reforge(chosen, Owner);
                chosen.Flash();                                  // pulse the re-forged relic for feedback
                // Fill this campfire's location aura 5–20%; at 100% the forge goes cold here for the visit.
                _locGauge = Math.Min(RelicForgeService.LocationGaugeFull,
                                     _locGauge + RelicForgeService.LocationGaugeStep(Owner, _reforges));
                _reforges++;
                if (_locGauge >= RelicForgeService.LocationGaugeFull) _ended = true;
                // Rebuild the options so THIS one reflects the new aura band (or greys when full / all relics gone).
                NRestSiteRoom.Instance?.CallDeferred(NRestSiteRoom.MethodName.UpdateRestSiteOptions);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] campfire reforge failed: {e}");
        }

        // Return false so the rest is NOT consumed — the player can pick "Reforge" again to re-roll more
        // relics (each bounded by its own curse gauge) before finally healing / smithing / leaving.
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
    /// "no prefix", or was never eligible on pickup (a deliberate reforge always lands a prefix) — EXCEPT
    /// one that currently carries a CURSE. A cursed relic can no longer be re-rolled (which used to wash
    /// the curse away, cheaply duplicating Cleanse); its only recourse is <see cref="Cleansable"/>. This
    /// makes reforge (improve an un-cursed prefix) and cleanse (remove a curse) fully disjoint, so a curse
    /// is a real commitment, and it never overlaps Cleanse's role. Hidden companion instances (grafted
    /// donors) are excluded, since those aren't relics the player picked; Ancient (先古) relics are excluded
    /// too while <see cref="ForgeConfig.ForgeAncientRelics"/> is off, so the opt-out leaves them untouched
    /// at campfires and shops as well as on pickup.
    /// </summary>
    public static IEnumerable<RelicModel> Reforgeable(Player player)
        => player.Relics.Where(r => !RelicForgeService.IsCompanion(r)
            && !r.IsWax                                              // volatile (wax) relic — melts on use, never reforge
            && !RelicForgeService.IsRelicSpent(r)                    // already melted / used-up (Disabled) — nothing to reforge
            && !RelicForgeService.CanCleanse(r)                      // cursed → cleanse-only, never reforge
            && !RelicForgeService.IsGaugeSaturated(r)                // curse gauge full → saturated, no more reforging
            && (HostForgeConfig.ForgeAncient || r.Rarity != RelicRarity.Ancient));

    public static bool HasReforgeable(Player player) => Reforgeable(player).Any();

    /// <summary>Owned relics that currently carry a curse — the merged concept: a penalty prefix (the
    /// prefix IS the curse), OR an enemy-rider / self-curse. These are the only relics a shop CLEANSE can
    /// act on. Unlike <see cref="Reforgeable"/>, cleanse deliberately does NOT honor the ForgeAncientRelics
    /// opt-out: that option only blocks ADDING a prefix/curse to Ancient (先古) relics, but a curse already
    /// sitting on one (forged before the toggle was flipped, or via a forced command) must stay removable —
    /// otherwise it would be trapped on the relic forever. Hidden companion instances are excluded.</summary>
    public static IEnumerable<RelicModel> Cleansable(Player player)
        => player.Relics.Where(r => !RelicForgeService.IsCompanion(r) && RelicForgeService.CanCleanse(r));

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
            // "유물" is explicit in every language so the campfire option never reads the same as the
            // game's card-upgrade (Smith, "재련" in KO) or any card-reforge option.
            ["OPTION_REFORGE.name"] = Localize("유물 재련", "重铸遗物", "Reforge Relic"),
            ["OPTION_REFORGE.description"] =
                Localize("유물을 재련합니다(휴식 소모 없음, 반복 가능). 재련할수록 저주 기운이 차오르고, 가득 차면 유물 효과가 멈춥니다 — 저주만 남습니다. 상점 정화로 되살립니다.",
                         "重铸遗物（不消耗休息，可重复）。诅咒之气积满后遗物效果停止——仅留诅咒。可在商店净化使其恢复。",
                         "Reforge a relic (free, repeatable). Each reforge fills its curse-aura; when full, its effect stops — only the curse remains. Cleanse at a shop to revive it."),
            ["OPTION_REFORGE.descriptionDisabled"] =
                Localize("재련할 유물이 없습니다 (모두 저주에 걸렸거나 저주 기운이 가득 찼습니다).",
                         "没有可重铸的遗物（都已被诅咒或诅咒之气已满）。",
                         "No relic left to reforge (all cursed or curse-saturated)."),
            // Location aura bands: this campfire's own reforge aura fills 5–20% per reforge; at 100% it ends.
            ["OPTION_REFORGE.descriptionLoc0"] =
                Localize("이 대장간의 저주 기운 {Pct}% — 희미하게 서리기 시작한다. 계속 재련할 수 있다.",
                         "本熔炉诅咒之气 {Pct}% — 隐约开始萦绕，仍可继续重铸。",
                         "This forge's curse-aura: {Pct}% — a faint haze; keep reforging."),
            ["OPTION_REFORGE.descriptionLoc1"] =
                Localize("이 대장간의 저주 기운 {Pct}% — 짙어지고 있다.",
                         "本熔炉诅咒之气 {Pct}% — 渐浓。",
                         "This forge's curse-aura: {Pct}% — thickening."),
            ["OPTION_REFORGE.descriptionLoc2"] =
                Localize("이 대장간의 저주 기운 {Pct}% — 자욱하다. 불꽃이 흔들린다.",
                         "本熔炉诅咒之气 {Pct}% — 弥漫，炉火摇曳。",
                         "This forge's curse-aura: {Pct}% — heavy; the flames waver."),
            ["OPTION_REFORGE.descriptionLoc3"] =
                Localize("이 대장간의 저주 기운 {Pct}% — 불씨가 꺼지기 직전. 곧 재련할 수 없다.",
                         "本熔炉诅咒之气 {Pct}% — 余烬将熄，即将无法重铸。",
                         "This forge's curse-aura: {Pct}% — embers nearly out; soon cold."),
            ["OPTION_REFORGE.descriptionLoc4"] =
                Localize("이 대장간의 저주 기운 {Pct}% — 가득 찼다.", "本熔炉诅咒之气 {Pct}% — 已满。", "This forge's curse-aura: {Pct}% — full."),
            ["OPTION_REFORGE.descriptionEnded"] =
                Localize("저주 기운이 가득 차 이 대장간의 불이 식었습니다. 다른 휴식처에서 다시 재련하세요.",
                         "诅咒之气缠满，这座熔炉的炉火已冷。请在其他休息处再重铸。",
                         "The curse-aura fills and this forge goes cold — reforge again at another rest site."),
        });
    }
}

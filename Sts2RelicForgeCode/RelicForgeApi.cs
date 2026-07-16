using MegaCrit.Sts2.Core.Models;   // RelicModel
using MegaCrit.Sts2.Core.Runs;     // RunManager

namespace Sts2RelicForge;

/// <summary>
/// PUBLIC integration API for sibling mods. Two use cases:
///   · READ / INHERIT a relic's forge state — used by Sts2RelicTransmute to carry a source relic's
///     enchantment onto a transmuted one (see <see cref="SetPendingForge"/>);
///   · EXTEND the forge with data-driven prefixes / self-curses (<see cref="RegisterPrefix"/> /
///     <see cref="RegisterSelfCurse"/>) — e.g. a mod that adds new prefixes or new curses.
///
/// ACCESS: this class and its DTOs are PUBLIC, but RelicForge ships as a standalone mod DLL that other
/// mods do NOT build against (so they degrade cleanly when it is absent). Consumers therefore resolve
/// "Sts2RelicForge.RelicForgeApi" from the loaded assembly and call these members by REFLECTION. The
/// method NAMES + SIGNATURES here are a STABLE CONTRACT — do not rename / re-sign without a major bump
/// (a rename silently breaks integrations, which fail to bind and no-op).
///
/// ★CO-OP: registration must happen at mod INIT (before any run) and be IDENTICAL on every peer — the
/// roll pool is seed-deterministic, so a peer with a different set/order of registered prefixes desyncs.
/// A registration attempted while a run is active still applies but is logged as unsafe.
/// </summary>
public static class RelicForgeApi
{
    // ---------------- Layer A: read / inherit forge state ----------------

    /// <summary>This relic's forge descriptor ("prefix|rider|self|fbStat|fbAmt|fbPct"), or null if unforged.
    /// Pass it back to <see cref="SetPendingForge"/> to copy the enchantment onto another relic.</summary>
    public static string? GetDescriptor(RelicModel relic) => RelicForgeService.DescriptorOf(relic);

    /// <summary>How many times the relic has been re-forged (0 = original grade).</summary>
    public static int GetReforgeCount(RelicModel relic) => relic == null ? 0 : RelicForgeService.ReforgeCountOf(relic);

    /// <summary>Whether the relic's curse has been cleansed.</summary>
    public static bool IsCleansed(RelicModel relic) => relic != null && RelicForgeService.IsCleansed(relic);

    /// <summary>The relic's cumulative curse-gauge cleanse reduction (0 if never cleansed).</summary>
    public static int GetGaugeReduction(RelicModel relic) => relic == null ? 0 : RelicForgeService.GaugeReductionOf(relic);

    /// <summary>Whether RelicForge currently has this relic's effect DISABLED (curse-gauge saturated, etc.)
    /// without the game marking it — so an integrating mod can exclude it from its own actions.</summary>
    public static bool IsEffectDisabled(RelicModel relic) => relic != null && RelicForgeService.IsEffectDisabled(relic);

    /// <summary>Whether this relic is a HIDDEN COMPANION — a donor instance grafted onto a forged host to
    /// realize a companion prefix (e.g. "Quicksilver" grants a weakened Mercury Hourglass). It lives in
    /// <c>player.Relics</c> so its native hooks fire, but the player does NOT own it: it has no inventory
    /// icon, is never serialized, and is re-derived from its host's forge record on load.
    ///
    /// ★An integrating mod that enumerates <c>player.Relics</c> and ACTS on the result MUST exclude these —
    /// every internal RelicForge enumeration does. Treating a companion as an owned relic lets the player
    /// remove/consume a relic that simply reappears on the next load (a duplication exploit), and silently
    /// kills the host's prefix for the session.</summary>
    public static bool IsCompanion(RelicModel relic) => relic != null && RelicForgeService.IsCompanion(relic);

    /// <summary>Stash a forge state on <paramref name="relic"/> to be RESTORED verbatim the next time it is
    /// obtained (RelicCmd.Obtain) — the enchantment-inheritance seam (a transmuted relic keeps the source's
    /// prefix + curse + reforge count + curse-gauge). A pure no-op for a relic that is never re-obtained.
    /// Read the arguments off the SOURCE relic via the getters above.</summary>
    public static void SetPendingForge(RelicModel relic, string? descriptor, int reforgeCount, bool cleansed, int gaugeReduction)
    {
        if (relic == null) return;
        if (!string.IsNullOrEmpty(descriptor)) RelicForgeService.SetPendingDesc(relic, descriptor!);
        if (reforgeCount > 0) RelicForgeService.SetPendingReforgeCount(relic, reforgeCount);
        if (cleansed) RelicForgeService.SetPendingCleansed(relic);
        if (gaugeReduction > 0) RelicForgeService.SetPendingGaugeReduction(relic, gaugeReduction);
    }

    // ---------------- Layer B: extend the forge (data-driven) ----------------

    /// <summary>Register a numeric prefix into the pickup / reforge roll pool. Returns false (logged) if the
    /// name is empty or already taken. ★Call at mod init, identically on every co-op peer.</summary>
    public static bool RegisterPrefix(ForgePrefixDef def)
    {
        if (def == null || string.IsNullOrEmpty(def.Name)) return false;
        WarnIfRunActive("RegisterPrefix");
        bool ok = PrefixTable.RegisterExternal(new Prefix
        {
            Name = def.Name, Ko = def.Ko ?? "", Zh = def.Zh ?? "",
            PowerPct = def.PowerPct, Weight = def.Weight <= 0 ? 1 : def.Weight,
            Color = string.IsNullOrEmpty(def.Color) ? "#e0b64d" : def.Color,
            Amplify = def.Amplify,
        });
        if (ok) MainFile.Logger.Info($"[{MainFile.ModId}] RegisterPrefix '{def.Name}' ({def.PowerPct:+0.##;-0.##}, weight {def.Weight}).");
        return ok;
    }

    /// <summary>Register a data-driven self-curse (fires on the owner's unblocked hits). Set EXACTLY ONE
    /// effect: <see cref="ForgeSelfCurseDef.OnHitPower"/> = "Weak"/"Frail"/"Vulnerable", OR OnHitCard (a
    /// Dazed to the draw pile), OR OnHitRandom (a random Weak/Frail/Vulnerable). Returns false (logged) on
    /// empty/duplicate name or an unsupported effect. ★Init-time, identical on every peer.</summary>
    public static bool RegisterSelfCurse(ForgeSelfCurseDef def)
    {
        if (def == null || string.IsNullOrEmpty(def.Name)) return false;
        string pw = def.OnHitPower ?? "";
        if (pw.Length != 0 && pw != "Weak" && pw != "Frail" && pw != "Vulnerable")
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] RegisterSelfCurse '{def.Name}': unsupported OnHitPower '{pw}' " +
                                 "(use Weak/Frail/Vulnerable, or OnHitCard / OnHitRandom) — ignored.");
            return false;
        }
        if (pw.Length == 0 && !def.OnHitCard && !def.OnHitRandom)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] RegisterSelfCurse '{def.Name}': no effect set — ignored.");
            return false;
        }
        WarnIfRunActive("RegisterSelfCurse");
        bool ok = SelfCurseTable.RegisterExternal(new SelfCurseDef
        {
            En = def.Name, Ko = def.Ko ?? "", Zh = def.Zh ?? "",
            Color = string.IsNullOrEmpty(def.Color) ? "#c0554d" : def.Color,
            OnHitPower = pw, OnHitCard = def.OnHitCard, OnHitRandom = def.OnHitRandom,
            EffEn = def.EffEn ?? "", EffKo = def.EffKo ?? "", EffZh = def.EffZh ?? "",
        });
        if (ok) MainFile.Logger.Info($"[{MainFile.ModId}] RegisterSelfCurse '{def.Name}'.");
        return ok;
    }

    private static void WarnIfRunActive(string who)
    {
        try
        {
            if (RunManager.Instance?.State != null)
                MainFile.Logger.Warn($"[{MainFile.ModId}] {who} called during an active run — register at mod init " +
                                     "instead; a mid-run pool change can desync co-op.");
        }
        catch { /* best-effort guard only */ }
    }
}

/// <summary>Public DTO for <see cref="RelicForgeApi.RegisterPrefix"/> — a data-driven numeric prefix.</summary>
public sealed class ForgePrefixDef
{
    /// <summary>English name / stable key (unique across all prefixes). Shown when no localized name fits.</summary>
    public string Name = "";
    /// <summary>Optional localized display names (Korean / Chinese).</summary>
    public string Ko = "", Zh = "";
    /// <summary>Power magnitude: 0.30 = +30% stronger, -0.10 = 10% weaker. Scales the relic's numeric vars.</summary>
    public double PowerPct;
    /// <summary>Relative roll weight (higher = more common). Coerced to 1 if &lt;= 0.</summary>
    public double Weight = 5;
    /// <summary>Tier tint (BBCode hex, e.g. "#4db8ff") for the tooltip header.</summary>
    public string Color = "#e0b64d";
    /// <summary>If true, RAISE every var's magnitude regardless of benefit direction (a high-variance
    /// "amplify" prefix) instead of scaling only beneficial vars.</summary>
    public bool Amplify;
}

/// <summary>Public DTO for <see cref="RelicForgeApi.RegisterSelfCurse"/> — fires on the owner's unblocked hits.</summary>
public sealed class ForgeSelfCurseDef
{
    /// <summary>English name / stable key (unique across all self-curses).</summary>
    public string Name = "";
    /// <summary>Optional localized display names.</summary>
    public string Ko = "", Zh = "";
    /// <summary>Tooltip tint (BBCode hex).</summary>
    public string Color = "#c0554d";
    /// <summary>On each unblocked hit, apply 1 of this power to the owner: "Weak" / "Frail" / "Vulnerable".
    /// Leave empty to use <see cref="OnHitCard"/> or <see cref="OnHitRandom"/> instead.</summary>
    public string OnHitPower = "";
    /// <summary>Instead of a power, add a Dazed to the owner's draw pile on each unblocked hit.</summary>
    public bool OnHitCard;
    /// <summary>Instead: a random one of Weak / Frail / Vulnerable (1) on each unblocked hit.</summary>
    public bool OnHitRandom;
    /// <summary>Optional localized "on unblocked hit …" effect line for the tooltip.</summary>
    public string EffEn = "", EffKo = "", EffZh = "";
}

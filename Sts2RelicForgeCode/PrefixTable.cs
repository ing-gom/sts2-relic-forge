using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Random;

namespace Sts2RelicForge;

/// <summary>One Terraria-style prefix: a name and the power % it applies to the relic.</summary>
internal sealed class Prefix
{
    public string Name = "";   // English (canonical / default fallback)
    public string Ko = "";     // Korean (Terraria KR name)
    public string Zh = "";     // Chinese Simplified (Terraria zh name)
    public double PowerPct;     // 0.60 = +60% stronger, -0.10 = 10% weaker
    public double Weight;       // relative roll odds (Terraria reforge = weighted uniform pool)
    public string Color = "#e0b64d"; // tier tint for the ⚒ header (BBCode [color=#hex]); the
                                //   MegaRichTextLabel description renders it. Title can't be
                                //   colored — the tooltip title is a plain MegaLabel.
    public bool Amplify;        // if true, RAISE every var's raw magnitude regardless of
                                // benefit direction (a mixed relic like Brimstone gets both
                                // its self-strength AND the enemy-strength downside amplified)

    // --- Companion prefix (grafts another relic's whole effect onto the host) ---
    // When CompanionRelic is set, this prefix does NOT scale the host's vars; instead the
    // forge grants a HIDDEN instance of CompanionRelic to the player (Player.AddRelicInternal
    // silent), so that relic's native hooks fire on the owner. NoteXx describes the grafted
    // effect for the tooltip (there are no var-change deltas to show).
    public Type? CompanionRelic;
    public string NoteKo = "";
    public string NoteEn = "";
    public string NoteZh = "";

    // Delayed companion: if > 0, this prefix does NOT graft a relic; instead a fixed effect
    // (see DelayedCompanionPatch) is applied on this combat turn. Used to weaken min-1 effects
    // that can't be scaled down — a later trigger is strictly worse than combat-start.
    public int DelayTurn;

    // Penalty (curse) prefix: a pure downside applied to the player on some trigger
    // (see PenaltyCompanionPatch). Grafts nothing; its NoteXx shows in red on the tooltip.
    public bool Penalty;

    // Enemy-strip prefix: each turn, move ONE of the enemies' powers 1 toward zero
    // (see ForgeCombatAffixPatch.StripOne). Chaos — strips enemy buffs (good) AND the
    // debuffs you applied (bad) alike, so it's a gamble, not a pure boon.
    public bool EnemyStrip;

    // Symmetric combat-start prefix: on turn 1, apply SymPower (amount SymAmount) to BOTH the
    // player and every enemy (see ForgeCombatAffixPatch.ApplySymmetric). SymPower is a short
    // key ("Vulnerable" / "Weak"); grafts nothing.
    public string SymPower = "";
    public int SymAmount;

    // Random-debuff prefix: each turn, a 50% chance to apply Vulnerable/Weak 1 to either all
    // enemies or the player (a coin flip on both), see ForgeCombatAffixPatch.ApplyRandomDebuff.
    public bool RandomDebuff;

    // Mixed (gamble) prefix: its note is neither a pure boon nor a curse, so it renders amber
    // (neither green nor red). Set on the enemy-strip and symmetric prefixes.
    public bool Mixed;

    // --- Reactive affixes (see ReactiveAffix + REACTIVE_PREFIXES.md). These graft nothing and
    //     scale no host var; instead they react to the player's power/energy events in combat. ---
    public bool GainAmplify;     // 공명의: when the player gains Strength/Dexterity (>0), gain +1 more
    public bool LossInvert;      // 완강한: an enemy-applied Strength/Dexterity loss becomes a gain
    public int  EnergyDischarge; // 방전의: damage dealt to all enemies per bonus Energy gained (0 = off)

    // --- Run-state affixes (see RunState prefixes below). These graft nothing and scale no host var;
    //     instead they react to the player's GOLD / DECK / CURSE state rather than combat power events. ---
    public bool CurseDrawStrength; // 저주먹은: when the player draws a Curse card, gain 1 Strength
    public int  GoldStrengthPer;   // 황금빛: at combat start, gain 1 Strength per this much gold (0 = off)

    // --- Replay-grant affix (see CharAffix.OnTurnEchoing / CharAffixPatches). Grafts nothing and scales
    //     no host var; each turn it grants Replay 1 (CardModel.BaseReplayCount++, reverted at turn end so
    //     it never accumulates) to a random non-Curse/Status card in hand, and PLAYING that card costs the
    //     player Vulnerable 1 + Frail 1. Mixed (amber note): a strong boon with a built-in cost. ---
    public bool ReplayGrant;

    // --- Energy-gamble affixes (see ForgeCombatAffixPatch.ApplyEnergyGamble + OverclockedMaxHpPatch).
    //     Graft nothing and scale no host var: a strong per-turn boon (bonus Energy) bundled with a
    //     self-inflicted COST, so the note renders amber (Mixed). TurnEnergy fires every turn (net +N over
    //     the SetEnergy refill, like the bonus-energy relics — so it also feeds a Discharging relic).
    //     StartDamage is a flat combat-start CURRENT-HP hit (locally simulated → co-op-safe on both peers).
    //     StartMaxHpLoss is a PERMANENT Max-HP loss — Max-HP is host-authoritative/REPLICATED, so it must
    //     be applied AWAITED IN-ORDER inside the synchronized turn-start action (OverclockedMaxHpPatch chains
    //     onto the hook's Task, exactly like the game's own PaperCutsPower awaits LoseMaxHp); a detached
    //     RunSafely from this hook double-applied on the client and desynced (coop-verify caught it). ---
    public int TurnEnergy;      // 과열의/작열의: bonus Energy granted at the start of EVERY turn (0 = off)
    public int StartDamage;     // 작열의: flat self-damage taken once at combat start (0 = off)
    public int StartMaxHpLoss;  // 과열의: permanent Max HP lost once at combat start (0 = off; via OverclockedMaxHpPatch)

    // --- Hit-reactive energy affix (see HitEnergyAffixPatch). Grafts nothing and scales no host var:
    //     each time the OWNER takes UNBLOCKED damage FROM AN ENEMY (block failed → real HP lost), a
    //     per-hit roll may grant 1 bonus Energy. A pure boon (green note) with a built-in tension —
    //     the payoff only comes when you're actually being hit. Deterministic per hit (seeded from the
    //     post-hit HP, which strictly decreases → independent rolls that reload/co-op reproduce). ---
    public int HitEnergyPercent; // 투지의: % chance per unblocked ENEMY hit to gain 1 bonus Energy (0 = off)

    // --- Meta / aura affix (see MetaAffix). Grafts nothing and scales no host var: while the player owns a
    //     relic with this prefix, EVERY probabilistic COMBAT proc of the player's OTHER forged prefixes rolls
    //     at DOUBLE chance — boons AND curses (does NOT touch the forge-time curse chance). The first
    //     cross-relic prefix (all others affect only their own host). ---
    public bool ProcDoubler;    // 촉매의: aura — doubles other prefixes' in-combat proc chances (boon + curse)
    public int  ProcBoostPct;   // 촉진의: aura — the "light Catalytic": ADDS this many percentage points to other
                                //   prefixes' in-combat proc chances (a flat boost, not a double). Catalytic wins if both.
    public bool ProcReroll;     // 증강의: aura — other prefixes' proc gets a SECOND roll (fires if either succeeds =
                                //   advantage, 1-(1-p)^2). NOT a flat chance bump — a genuine re-attempt. Precedence: 촉매 > 증강 > 촉진.
    public bool CurseScaling;   // 저주결속의: at combat start, gain Str/Dex/Intangible by the number of CURSES you carry (ramp table)

    // --- Card-play affixes (see PrefixCardPlayPatch, on Hook.AfterCardPlayed) ---
    public bool PowerDrawFirst; // 학구의: the FIRST Power card you play each combat → draw 2
    public bool PowerEnergy;    // 충만의: playing a Power card → +1 Energy (once per turn)
    public bool SkillDraw;      // 교활의: playing a Skill card → 25% chance to draw 1
    // --- Exhaust/Ethereal savers (see ExhaustDodgePatch — the universal siblings of the IC-only Lingering) ---
    public int  ExhaustDodgePct;// 끈질긴: a card that would EXHAUST is discarded instead, this % of the time (any character)
    public bool EtherealSave;   // 잔존의: an ETHEREAL card that would exhaust is discarded instead (it survives)

    // --- Combat-start defensive power (see ForgeCombatAffixPatch.ApplyStartPower) ---
    public string StartPower = "";  // 수호의/방벽의/잔상의…: a native power granted to self on turn 1 ("Artifact"/"Buffer"/"Blur"/"Regen"/"Plated")
    public int    StartPowerAmount; // how much of it
    // --- Kill / shop affixes ---
    public int  KillGold;      // 강탈의: gold gained each time an enemy is killed (see KillGoldPatch)
    public int  ShopTaxPct;    // 탐욕의 (curse): merchant prices are raised by this % while carried (see MerchantPricePatch)
    // --- Potion-use family (all fire from PotionBlockPatch on Hook.BeforePotionUsed) — a "potion meta" ---
    public int  PotionBlock;      // 연금의: Block gained on potion use
    public int  PotionStr;        // 발효의: Strength gained on potion use (permanent this combat, NOT the vanilla temp buff)
    public int  PotionDex;        // 증류의: Dexterity gained on potion use
    public int  PotionVuln;       // 부식의: Vulnerable applied to ALL enemies on potion use
    public int  PotionWeak;       // 희석의: Weak applied to ALL enemies on potion use
    public int  PotionBufferPct;  // 발포의: % chance to gain Buffer 1 on potion use
    public bool PotionDoubler;    // 농축의: AURA — while owned, all of your potion-use effects are DOUBLED (rare)

    // --- Low-HP comeback boon (see LowHpDrawPatch on Hook.ModifyHandDraw) ---
    public bool LowHpDraw;      // 궁지의: while at/below 50% max HP, draw 1 EXTRA card each turn (rides the game's own hand-draw count)
    // --- Gold-for-armor (see GoldArmorPatch): pay gold to shave incoming enemy hits ---
    public int  GoldArmorCost;  // 매수의: each enemy hit deals 1 LESS, spending this much gold per hit (0 = off; no gold → no reduction)

    // --- Mild amplifier / synergy auras (ownership read from the synced relic list → co-op-safe) ---
    public int StartPowerBoost;  // 북돋움의: AURA — your combat-start defensive power prefixes (수호/방벽/잔상) grant +this
    public int PotionBoost;      // 정련의: AURA — each of your potion-use AMOUNT effects (block/str/dex/vuln/weak) gains +this
    public int AttunedBlockPer;  // 조화의: at combat start, gain this much Block per OTHER forged relic carried (capped at ×4, see ForgeCombatAffixPatch)
    public int SecondWindBlock;  // 재기의: the FIRST time your HP drops to 50% or below in a combat, gain this much Block (once/combat, see SecondWindPatch)
    public bool PickupReforge;   // 단조의: AURA — obtaining ANY relic auto-reforges one OTHER eligible relic (campfire path, builds its curse charge, see PickupReforgePatch)

    // --- Mild combat boons (batch: healing / kill-fuel / late-fight / opener / execute) ---
    public int  KillBlock;       // 처치의: Block gained each time an enemy is killed (combat state → both peers, see KillGoldPatch)
    public int  EnduringStr;     // 지구전의: from combat turn 5 onward, gain this much Strength each turn (see ForgeCombatAffixPatch)
    public bool FirstTurnDraw;   // 선제의: draw 1 extra card on turn 1 only (rides ModifyHandDraw, see LowHpDrawPatch)
    public int  ExecutePct;      // 처단의: % MORE damage dealt to enemies at/below 20% max HP (see ExecutePatch)

    // Force the enemy-rider curse on unconditionally (bypasses EnemyRiderChance). Used by 공명의 so
    // its strength always comes bundled with a curse — the mod's own cost, in place of a per-trigger
    // penalty. Ignored on penalty prefixes (which never carry a rider).
    public bool AlwaysCurse;

    // --- Character-gated behavioral affix (see CharAffix / CharAffixPatches). Only rolls when the
    //     owning player plays RequiredCharacter, and reacts to that character's signature mechanic
    //     (poison / orbs / summons / stars …). Grafts nothing and scales no host var, so it's a
    //     companion-family prefix; dispatched by Name in the patches. RequiredCharacter is a
    //     CharacterModel Id.Entry (e.g. "SILENT"); "" = universal (every legacy prefix). ---
    public string RequiredCharacter = "";
    public bool CharAffix;

    // --- Vigor family (universal). Vigor is a VANILLA power, so these work on ANY character — mod
    //     characters included (Vigor is a first-class status in the no-code character creator, so
    //     many mod characters build around it). Graft nothing, scale no host var → IsCompanionPrefix.
    //     StartVigor/TurnVigor GRANT Vigor from the combat turn-start dispatch (ForgeCombatAffixPatch);
    //     the *Pct fields REACT to Vigor being consumed, rolled per point in CharAffix.OnVigorConsumed
    //     (the VigorPower pay-down hook). NOT CharAffix, so they sit in the Custom pool's Effects tab
    //     and toggle per-entry like every other universal effect prefix. ---
    public int StartVigor;        // 전투 시작 시(턴1) 활력 N
    public int TurnVigor;         // 매 턴 활력 N
    public int VigorStrengthPct;  // 활력 1 소모마다 N% 확률로 힘 1 (Quenched)
    public int VigorBlockPct;     // 활력 1 소모마다 N% 확률로 방어도 2 (Bracing)
    public int VigorVulnPct;      // 활력 1 소모마다 N% 확률로 무작위 적에게 취약 1 (Rending)
    public int VigorWeakPct;      // 활력 1 소모마다 N% 확률로 무작위 적에게 약화 1 (Withering)
    public int VigorEnergyPct;    // 활력 1 소모마다 N% 확률로 에너지 1 (per-point, 무제한 — Stoking)
    public bool VigorProcDoubler; // AURA: 다른 활력 소모 반응 접두사의 발동 확률 2배 (Frenzied)
    public int  VigorSelfDebuffPct; // 활력 소모 시 N% 확률로 자신에게 손상/취약/약화 중 1 — Frenzied의 자체 저주(per-event)
    public int  VigorStatDownPct; // 활력 소모 시 N% 확률로 자신의 힘 -1 또는 민첩 -1 — Frenzied의 추가 저주(per-event)
    public int  VigorStrDownPct;  // 활력 소모 시 N% 확률로 자신의 힘 -1 — 저주 슬롯 전용(Waning, Penalty), per-event
    public bool VigorGainAmplify; // AURA: 활력을 얻을 때마다 활력 +1 (턴당 3회 캡 — Surging)
    public int  VigorRegenPct;    // 활력 1 소모마다 N% 확률로 재생 1 (Wellspring)
    public bool VigorTurnEndBlock; // 턴 종료 시 안 쓴 활력 전부를 방어도로 전환 1:1 (Congealing)
    public int  VigorTurnEndStrPct; // 턴 종료 시 안 쓴 활력 소모, 1당 N% 확률로 힘 1 (Evolving)

    // --- Fallback prefix (see RelicForgeService.Forge substitution + FallbackBuffPatch). NOT in the
    //     normal roll pool (PrefixTable.InPool excludes it): a magnitude prefix that rounded to NO
    //     change on a relic (too small / var-less) is REPLACED by one of these — a host-independent,
    //     CHANCE-gated minor combat-start buff. The chance is derived from the fizzled prefix's tier
    //     (RelicForgeService.FallbackChanceFor) and stored on the record (ForgeRecord.FallbackPercent),
    //     so the note shows the real odds. FallbackStat names the power granted; scales no host var. ---
    public string FallbackStat = "";   // "" = not a fallback; else "Strength"/"Dexterity"/"Block"/"Thorns"
    public int FallbackAmount;          // how much of the stat the combat-start roll grants
    public bool IsFallback => FallbackStat.Length > 0;

    /// <summary>True for any companion-family prefix (grafts a relic, delays/strips an effect,
    /// applies a symmetric/random effect, is a penalty, is a reactive/character affix, or is a
    /// fallback buff) — none of these scale the host's vars. NOTE: keyword-family prefixes
    /// (Retaining …) are dispatched by NAME and set no flag here, so this alone under-counts the
    /// effect prefixes — pool classification must use <see cref="IsEnhance"/>.</summary>
    public bool IsCompanionPrefix => CompanionRelic != null || DelayTurn > 0 || Penalty
                                     || EnemyStrip || SymPower.Length > 0 || RandomDebuff
                                     || GainAmplify || LossInvert || EnergyDischarge > 0
                                     || CurseDrawStrength || GoldStrengthPer > 0 || CharAffix
                                     || ReplayGrant || IsFallback
                                     || TurnEnergy > 0 || StartDamage > 0 || StartMaxHpLoss > 0
                                     || HitEnergyPercent > 0 || ProcDoubler || ProcBoostPct > 0
                                     || ProcReroll || CurseScaling
                                     || PowerDrawFirst || PowerEnergy || SkillDraw
                                     || ExhaustDodgePct > 0 || EtherealSave
                                     || StartPower.Length > 0 || KillGold > 0 || ShopTaxPct > 0
                                     || PotionBlock > 0 || PotionStr > 0 || PotionDex > 0
                                     || PotionVuln > 0 || PotionWeak > 0 || PotionBufferPct > 0
                                     || PotionDoubler || LowHpDraw || GoldArmorCost > 0
                                     || StartPowerBoost > 0 || PotionBoost > 0
                                     || AttunedBlockPer > 0 || SecondWindBlock > 0 || PickupReforge
                                     || KillBlock > 0 || EnduringStr > 0 || FirstTurnDraw || ExecutePct > 0
                                     || StartVigor > 0 || TurnVigor > 0 || VigorStrengthPct > 0 || VigorBlockPct > 0
                                     || VigorVulnPct > 0 || VigorWeakPct > 0 || VigorEnergyPct > 0
                                     || VigorProcDoubler || VigorSelfDebuffPct > 0
                                     || VigorStatDownPct > 0 || VigorStrDownPct > 0
                                     || VigorGainAmplify || VigorRegenPct > 0
                                     || VigorTurnEndBlock || VigorTurnEndStrPct > 0;

    /// <summary>"Vertical" classification for the prefix-pool filter (<see cref="ForgeConfig.PrefixPool"/>):
    /// a prefix that ONLY scales the relic's own numbers. Flags alone under-count (keyword-family
    /// prefixes carry none), so this also requires an EMPTY effect note — the invariant that holds
    /// across the whole table and external registrations alike: every mechanic-adding prefix must
    /// describe itself in NoteXx (or it would be invisible in-game), while pure magnitude tiers
    /// (incl. negatives and Amplify) render as var deltas and carry no note.</summary>
    public bool IsEnhance => !IsCompanionPrefix
                             && NoteEn.Length == 0 && NoteKo.Length == 0 && NoteZh.Length == 0;

    /// <summary>Stable loc-key base derived from the English name (see <see cref="ForgeLoc"/>).</summary>
    internal string LocKeyBase => "PREFIX_" + ForgeLoc.KeyOf(Name);

    /// <summary>Name in the game's current language, via the relic_forge loc table (external
    /// translations honored); English fallback.</summary>
    public string Display => ForgeLoc.Get(LocKeyBase + ".name", Name);

    /// <summary>Grafted-effect note in the current language (companion prefixes only).</summary>
    public string NoteDisplay => ForgeLoc.Get(LocKeyBase + ".note", NoteEn);
}

/// <summary>
/// Single universal prefix pool, adapted from Terraria's item modifiers. Magnitudes track
/// Terraria's value multipliers, scaled down to fit relics (Legendary is the rare top,
/// down through Keen). This mod's identity is a GAMBLE: downside is intentionally substantial
/// (~25% of the pool is a pure penalty/curse — magnitude negatives Damaged/Shoddy/Broken plus
/// the penalty-companion prefixes — with more on the mixed/amber gambles), so a reforge is a
/// real risk, not a free upgrade. A pickup may still roll no prefix (ForgeConfig.NoPrefixChance),
/// but a deliberate reforge always lands one (guaranteePrefix) — the gamble you paid for.
/// A relic's rarity does NOT change the pool or magnitude (Terraria-style: any item can roll any
/// prefix); rarity only gates the automatic pickup forge (Starter/Event never auto-forge).
///
/// NOTE: names are English Terraria terms for now; localization (incl. Korean) is deferred
/// until the prefix set is settled.
/// </summary>
internal static class PrefixTable
{
    // ★ ADDING A PREFIX — checklist (the filter/custom UI need NO manual registration, but these
    //   invariants make the auto-derivation correct):
    //   1. EFFECT prefixes MUST fill NoteKo/NoteEn/NoteZh. An empty note classifies the prefix as
    //      'Enhance' (IsEnhance) — it would land in the wrong pool-filter bucket AND the wrong
    //      custom-panel tab (the keyword family leaked exactly this way; coop-verify caught it).
    //   2. Name is a STABLE KEY: custom_pool.json persists disabled entries by Name and the rf_config
    //      arg-9 wire indexes this table's order — renaming resets users' custom picks for that entry
    //      (and, mid-run in co-op, requires both peers on the same version, as always).
    //   3. Character-gated affixes set RequiredCharacter/CharAffix — that (not the note) routes them
    //      to the custom panel's Character tab.
    //   Everything else (pool filter buckets, custom tabs, per-entry toggles, localization keys) is
    //   derived from the fields automatically.
    public static readonly Prefix[] All =
    {
        // positive — top is rare, low tiers common. Color grades by power: gold/orange at the
        // top, cooling through green → blue for the low tiers.
        new Prefix { Name = "Legendary", Ko = "전설적인",   Zh = "传奇的",   PowerPct =  0.60, Weight =  2, Color = "#ff8000" },
        new Prefix { Name = "Godly",     Ko = "신성한",     Zh = "神级的",   PowerPct =  0.30, Weight =  4, Color = "#ffd23f" },
        new Prefix { Name = "Demonic",   Ko = "악마의",     Zh = "恶魔的",   PowerPct =  0.25, Weight =  6, Color = "#c04dff" },
        new Prefix { Name = "Superior",  Ko = "훌륭한",     Zh = "高级的",   PowerPct =  0.18, Weight =  9, Color = "#4dd24d" },
        new Prefix { Name = "Forceful",  Ko = "강력한",     Zh = "强力的",   PowerPct =  0.12, Weight = 12, Color = "#7ed957" },
        // Low-tier magnitude prefixes weighted DOWN (was 15/15/15): a +4~8% bump rounds to +0 on
        // most relics (62% have a numeric base <= 3), so they were the main source of "empty"
        // reforges. Cutting their weight raises the RELATIVE share of every effect prefix (companion/
        // reactive/character) — which do something regardless of base — without touching any magnitude,
        // so there's zero overbalance risk. See RelicForgeService.Forge (ReforgeFloorMinBase) for the
        // complementary floor that keeps big relics from ever reforging to a literal 0-change.
        new Prefix { Name = "Hurtful",   Ko = "고통스러운", Zh = "致伤的",   PowerPct =  0.08, Weight = 10, Color = "#a7e34d" },
        new Prefix { Name = "Zealous",   Ko = "열성적인",   Zh = "狂热的",   PowerPct =  0.06, Weight =  7, Color = "#4db8ff" },
        new Prefix { Name = "Keen",      Ko = "날카로운",   Zh = "锐利的",   PowerPct =  0.04, Weight =  5, Color = "#9fd8ff" },
        // amplify — raises EVERY var's raw magnitude (both boons and downsides). On a
        // single-boon relic it's just a strong buff; on a mixed relic (Brimstone: self +
        // enemy strength) it's a high-risk/high-reward trade-off. Power ~= Demonic. Volatile
        // orange-red marks the risk.
        new Prefix { Name = "Volatile",  Ko = "불안정한",   Zh = "不稳定的", PowerPct =  0.25, Weight =  6, Amplify = true, Color = "#ff5c33" },
        // negative — magnitudes matched to Forceful/Superior/Demonic (−12/−18/−25%), ~26% total
        // roll chance. Dull gray darkening to a broken red.
        new Prefix { Name = "Damaged",   Ko = "금이 간",    Zh = "破损的",   PowerPct = -0.12, Weight = 14, Color = "#b0b0b0" },
        new Prefix { Name = "Shoddy",    Ko = "하찮은",     Zh = "粗劣的",   PowerPct = -0.18, Weight =  8, Color = "#8f8f8f" },
        new Prefix { Name = "Broken",    Ko = "부서진",     Zh = "碎裂的",   PowerPct = -0.25, Weight =  5, Color = "#e0554d" },

        // --- Companion prefixes ---
        // Each grafts a FIXED donor relic's whole effect onto ANY host relic, regardless of
        // the host's own vars (so var-less relics can roll these too). Themed name, not the
        // donor's name. The donor is granted as a hidden instance so its native hooks fire;
        // the effect is surfaced on the HOST tooltip via NoteXx. All donors are hook-driven
        // (not on-pickup) and benign/moderate — verified against decompiled effect code.
        // Weights: rare-ish treats, tuned by power (bigger effect → lower weight).
        // Grafted companions — the donor's effect is granted at a REDUCED magnitude (×0.35,
        // floor 1) so it stays a garnish, well weaker than owning the real relic. Notes state the
        // reduced value; keep them in sync with RelicForgeService.WeakenFactor.
        new Prefix { Name = "Thorned", Ko = "가시돋친", Zh = "尖刺的", Weight = 9, Color = "#7ed957",
            CompanionRelic = typeof(BronzeScales),   // Thorns 3 -> 1 (×0.35; Bronze Scales is 3)
            NoteKo = "전투 시작 시 가시 1", NoteEn = "Thorns 1 at combat start", NoteZh = "战斗开始时获得1荆棘" },
        new Prefix { Name = "Quicksilver", Ko = "수은의", Zh = "水银的", Weight = 6, Color = "#c0c8d8",
            CompanionRelic = typeof(MercuryHourglass),   // damage -> 1 (×0.35)
            NoteKo = "매 턴 모든 적에게 1 피해", NoteEn = "1 damage to all enemies each turn", NoteZh = "每回合对所有敌人造成1点伤害" },
        new Prefix { Name = "Anchored", Ko = "닻내린", Zh = "沉稳的", Weight = 7, Color = "#4db8ff",
            CompanionRelic = typeof(Anchor),   // Block 10 -> 3 (×0.30 floor)
            NoteKo = "전투 시작 시 블록 3", NoteEn = "Block 3 at combat start", NoteZh = "战斗开始时获得3格挡" },
        new Prefix { Name = "Vital", Ko = "피끓는", Zh = "血涌的", Weight = 8, Color = "#ff5c8a",
            CompanionRelic = typeof(BloodVial),   // Heal 2 -> 1 (floor 1)
            NoteKo = "첫 턴에 체력 1 회복", NoteEn = "Heal 1 on turn 1", NoteZh = "第1回合回复1点生命" },
        new Prefix { Name = "Rhythmic", Ko = "규칙적인", Zh = "规律的", Weight = 5, Color = "#ffd23f",
            CompanionRelic = typeof(HappyFlower),   // energy +1, interval 3->4 (VarOverride)
            NoteKo = "4턴마다 에너지 +1", NoteEn = "+1 energy every 4 turns", NoteZh = "每4回合获得1点能量" },
        new Prefix { Name = "Insightful", Ko = "통찰의", Zh = "洞察的", Weight = 7, Color = "#c04dff",
            CompanionRelic = typeof(CentennialPuzzle),   // draw -> 1 (×0.35)
            NoteKo = "전투 중 첫 피격 시 카드 1장 드로우", NoteEn = "Draw 1 card when first hit", NoteZh = "战斗中首次受击时抓1张牌" },
        // Delayed companions — no graft; a fixed min-1 effect applies LATER than the original
        // relic (turn 2/3 instead of combat start), so it's strictly weaker. See DelayedCompanionPatch.
        new Prefix { Name = "Mighty", Ko = "강건한", Zh = "强壮的", Weight = 6, Color = "#ff6b4d",
            DelayTurn = 3,
            NoteKo = "3번째 턴에 힘 +1", NoteEn = "Strength +1 on turn 3", NoteZh = "第3回合获得1力量" },
        new Prefix { Name = "Intimidating", Ko = "위협적인", Zh = "威慑的", Weight = 8, Color = "#9b6bff",
            DelayTurn = 2,
            NoteKo = "2번째 턴에 모든 적에게 취약 1", NoteEn = "Vulnerable 1 to all enemies on turn 2", NoteZh = "第2回合对所有敌人施加1易伤" },

        // --- 2nd batch (all weakened vs the real relic: reduced value, longer interval, or delay) ---
        new Prefix { Name = "Ferocious", Ko = "사나운", Zh = "凶猛的", Weight = 5, Color = "#ff5533",
            CompanionRelic = typeof(Akabeko),   // Vigor 8 -> 2 (×0.30 floor)
            NoteKo = "첫 턴에 활력 2", NoteEn = "Vigor 2 on turn 1", NoteZh = "第1回合获得2点活力" },
        new Prefix { Name = "Bladed", Ko = "칼날의", Zh = "锋刃的", Weight = 6, Color = "#d9d9e0",
            CompanionRelic = typeof(LetterOpener),   // Damage 5->1 (×0.30 floor), interval 3->4 (VarOverride) — per-turn counter
            NoteKo = "한 턴에 스킬 4회마다 모든 적에게 1 피해", NoteEn = "1 damage to all enemies per 4 skills in one turn", NoteZh = "一回合内每4张技能牌对所有敌人造成1点伤害" },
        new Prefix { Name = "Relentless", Ko = "연격의", Zh = "连击的", Weight = 5, Color = "#ff8c42",
            CompanionRelic = typeof(Shuriken),   // Strength +1, interval 3->4 (VarOverride) — per-turn counter
            NoteKo = "한 턴에 공격 4회마다 힘 +1", NoteEn = "Strength +1 per 4 attacks in one turn", NoteZh = "一回合内每4张攻击牌获得1力量" },
        new Prefix { Name = "Tempered", Ko = "단단한", Zh = "淬火的", Weight = 7, Color = "#5a9fd4",
            CompanionRelic = typeof(Orichalcum),   // Block 6 -> 1 (×0.30 floor)
            NoteKo = "턴 종료 시 블록이 없으면 블록 1", NoteEn = "Block 1 if you end your turn with no Block", NoteZh = "回合结束时若无格挡则获得1格挡" },
        new Prefix { Name = "Gusting", Ko = "질풍의", Zh = "疾风的", Weight = 7, Color = "#7ed9e0",
            CompanionRelic = typeof(OrnamentalFan),   // Block 4->1 (×0.35), interval 3->4 (VarOverride) — per-turn counter
            NoteKo = "한 턴에 공격 4회마다 블록 1", NoteEn = "Block 1 per 4 attacks in one turn", NoteZh = "一回合内每4张攻击牌获得1格挡" },
        new Prefix { Name = "Darting", Ko = "표창의", Zh = "迅捷的", Weight = 6, Color = "#6ee0a0",
            CompanionRelic = typeof(Kunai),   // Dexterity +1, interval 3->4 (VarOverride) — per-turn counter
            NoteKo = "한 턴에 공격 4회마다 민첩 +1", NoteEn = "Dexterity +1 per 4 attacks in one turn", NoteZh = "一回合内每4张攻击牌获得1敏捷" },
        new Prefix { Name = "Supple", Ko = "유연한", Zh = "柔韧的", Weight = 7, Color = "#6ed9c0",
            DelayTurn = 2,   // Oddly Smooth Stone's Dex +1, delayed to turn 2 instead of combat start
            NoteKo = "2번째 턴에 민첩 +1", NoteEn = "Dexterity +1 on turn 2", NoteZh = "第2回合获得1敏捷" },
        new Prefix { Name = "Accelerating", Ko = "가속의", Zh = "加速的", Weight = 5, Color = "#ffcf3f",
            CompanionRelic = typeof(Nunchaku),   // Energy +1, interval 10->12 (VarOverride)
            NoteKo = "공격 12회마다 에너지 +1", NoteEn = "+1 energy every 12 attacks", NoteZh = "每12张攻击牌获得1点能量" },

        // --- Penalty prefixes = CURSES (merged concept): a pure self-downside, low weight (see
        //     PenaltyCompanionPatch). Like an enemy-rider / self-curse, rolling one ENDS the reforge
        //     (campfire + shop) and can only be shed with Cleanse — which reverts it to no prefix. ---
        new Prefix { Name = "Cursed", Ko = "저주받은", Zh = "被诅咒的", Weight = 8, Penalty = true, Color = "#b0554d",
            NoteKo = "전투 시작 시 자신에게 약화 1", NoteEn = "Weak 1 to self at combat start", NoteZh = "战斗开始时给予自己1虚弱" },
        new Prefix { Name = "Cumbersome", Ko = "무거운", Zh = "笨重的", Weight = 8, Penalty = true, Color = "#8f8f8f",
            NoteKo = "첫 턴에 자신 민첩 -1", NoteEn = "Dexterity -1 to self on turn 1", NoteZh = "第1回合自身敏捷-1" },
        new Prefix { Name = "Fickle", Ko = "변덕스러운", Zh = "善变的", Weight = 6, Penalty = true, Color = "#9a6b8f",
            NoteKo = "매 턴 25% 확률로 자신에게 랜덤 디버프 1", NoteEn = "25% each turn: a random debuff 1 to self", NoteZh = "每回合25%概率给予自己1个随机减益" },
        new Prefix { Name = "Overloaded", Ko = "과부하", Zh = "超载的", Weight = 6, Penalty = true, Color = "#a0605a",
            NoteKo = "한 턴에 카드 6장 사용 시 자신에게 취약 1", NoteEn = "Vulnerable 1 to self after 6 cards in one turn", NoteZh = "一回合内打出6张牌后给予自己1易伤" },

        // --- Keyword family (grants/inflicts card keywords via the game's own systems) ---
        new Prefix { Name = "Retaining", Ko = "보존의", Zh = "留存的", Weight = 6, Color = "#6ec0d9",
            NoteKo = "턴 종료 시 손에 든 무작위 카드 1장을 보존한다",
            NoteEn = "At the end of your turn, Retain a random card in your hand",
            NoteZh = "回合结束时，随机保留1张手牌" },
        new Prefix { Name = "Searing", Ko = "불사르는", Zh = "灼焚的", Weight = 6, Penalty = true, Color = "#c0704d",
            NoteKo = "카드를 낼 때 25% 확률로 그 카드에 소멸이 부여된다 (다음 사용부터 적용)",
            NoteEn = "When you play a card, 25% chance it gains Exhaust (takes effect from its next play)",
            NoteZh = "打出卡牌时，25%概率使其获得消耗（下次打出时生效）" },
        // Replay-grant (see ReplayGrant): each turn a random hand card gains Replay 1 (single-turn,
        // reverted at turn end), and playing that card costs the player Vulnerable 1 + Frail 1. A strong
        // gamble — you don't choose which card, and the boon is paid for on use. Mixed → amber note.
        new Prefix { Name = "Echoing", Ko = "메아리의", Zh = "回响的", Weight = 3, Mixed = true, ReplayGrant = true, Color = "#a35cff",
            NoteKo = "매 턴, 손에 든 무작위 카드 1장이 재사용 1을 얻는다. 그 카드를 사용하면 자신에게 취약 1·손상 1",
            NoteEn = "Each turn, a random card in your hand gains Replay 1. Playing that card gives you Vulnerable 1 and Frail 1",
            NoteZh = "每回合，手牌中随机1张牌获得1层重演。打出该牌时，给予自己1层易伤和1层脆弱" },

        // --- Card-insertion penalties: shove a status card into a combat pile (see PenaltyCompanionPatch) ---
        new Prefix { Name = "Tainted", Ko = "오염된", Zh = "污秽的", Weight = 5, Penalty = true, Color = "#7a8a5a",
            NoteKo = "매 턴 뽑을 더미에 현기증 1장", NoteEn = "Adds a Dazed to your draw pile each turn", NoteZh = "每回合将1张眩晕加入抽牌堆" },
        new Prefix { Name = "Festering", Ko = "곪은", Zh = "溃烂的", Weight = 5, Penalty = true, Color = "#8a6a4a",
            NoteKo = "전투 시작 시 버린 더미에 상처 2장", NoteEn = "Adds 2 Wounds to your discard at combat start", NoteZh = "战斗开始时将2张伤口加入弃牌堆" },
        new Prefix { Name = "Smoldering", Ko = "불타는", Zh = "阴燃的", Weight = 5, Penalty = true, Color = "#c0603a",
            NoteKo = "전투 시작 시 뽑을 더미에 화상 1장", NoteEn = "Adds a Burn to your draw pile at combat start", NoteZh = "战斗开始时将1张灼烧加入抽牌堆" },
        new Prefix { Name = "Hollow", Ko = "공허한", Zh = "虚空的", Weight = 5, Penalty = true, Color = "#6a5a8a",
            NoteKo = "전투 시작 시 뽑을 더미에 공허 1장", NoteEn = "Adds a Void to your draw pile at combat start", NoteZh = "战斗开始时将1张虚无加入抽牌堆" },

        // --- Mixed (gamble) affixes: no var scaling, an amber note. See ForgeCombatAffixPatch. ---
        // Enemy-strip: each turn erodes one enemy power toward zero. Chaos — it strips enemy
        // buffs (Strength/Plating/Artifact…) AND the debuffs you applied (Weak/Vuln/Frail) alike.
        new Prefix { Name = "Eroding", Ko = "침식의", Zh = "侵蚀的", Weight = 6, Mixed = true, EnemyStrip = true, Color = "#8fbf6f",
            NoteKo = "매 턴 적 능력치 하나를 0쪽으로 1 감소 (버프·디버프 모두)",
            NoteEn = "Each turn, move one enemy power 1 toward zero (buffs and debuffs alike)",
            NoteZh = "每回合使敌方一个能力值向零靠近1点（增益与减益皆可）" },
        // Symmetric combat-start: applies a debuff to BOTH you and every enemy on turn 1.
        new Prefix { Name = "Exposing", Ko = "노출의", Zh = "暴露的", Weight = 6, Mixed = true, SymPower = "Vulnerable", SymAmount = 1, Color = "#e0904d",
            NoteKo = "전투 시작 시 적 하나와 자신에게 취약 1",
            NoteEn = "Vulnerable 1 to one enemy and yourself at combat start",
            NoteZh = "战斗开始时对一个敌人和自己施加1层易伤" },
        new Prefix { Name = "Enervating", Ko = "쇠약의", Zh = "衰弱的", Weight = 6, Mixed = true, SymPower = "Weak", SymAmount = 1, Color = "#c0a04d",
            NoteKo = "전투 시작 시 적 하나와 자신에게 약화 1",
            NoteEn = "Weak 1 to one enemy and yourself at combat start",
            NoteZh = "战斗开始时对一个敌人和自己施加1层虚弱" },
        // Chaotic: a coin flip each turn — good (enemies) or bad (you).
        new Prefix { Name = "Chaotic", Ko = "혼돈의", Zh = "混沌的", Weight = 6, Mixed = true, RandomDebuff = true, Color = "#a06fd0",
            NoteKo = "매 턴 50% 확률로 적 하나 또는 자신에게 무작위 디버프(취약/약화/손상)",
            NoteEn = "Each turn, 50% chance: a random debuff (Vulnerable / Weak / Frail) to one enemy or one player",
            NoteZh = "每回合50%概率：对一个敌人或一名玩家施加随机减益（易伤/虚弱/脆弱）" },

        // --- Reactive affixes (see ReactiveAffix / ReactiveAffixPatches / REACTIVE_PREFIXES.md). They
        //     react to the player's power/energy events in combat. Forceable via `forge <relic> Resonant`. ---
        new Prefix { Name = "Resonant", Ko = "공명의", Zh = "共鸣的", Weight = 5, Mixed = true, GainAmplify = true, AlwaysCurse = true, Color = "#4db8ff",
            NoteKo = "힘·민첩을 얻을 때마다 그 능력치를 1 더 얻는다 (턴당 3회)",
            NoteEn = "When you gain Strength or Dexterity, gain 1 more of it (up to 3/turn)",
            NoteZh = "获得力量或敏捷时，额外获得1点（每回合最多3次）" },
        new Prefix { Name = "Obstinate", Ko = "완강한", Zh = "顽固的", Weight = 6, Mixed = true, LossInvert = true, Color = "#8fbf6f",
            NoteKo = "적이 힘·민첩을 깎으면, 그만큼 오히려 얻는다",
            NoteEn = "When an enemy reduces your Strength or Dexterity, gain that amount instead",
            NoteZh = "当敌人降低你的力量或敏捷时，反而获得等量" },
        new Prefix { Name = "Discharging", Ko = "방전의", Zh = "放电的", Weight = 6, Mixed = true, EnergyDischarge = 4, Color = "#e0904d",
            NoteKo = "추가 에너지를 얻을 때마다 모든 적에게 4 피해",
            NoteEn = "Whenever you gain bonus Energy, deal 4 damage to all enemies",
            NoteZh = "每当你获得额外能量时，对所有敌人造成4点伤害" },

        // --- Energy-gamble affixes (see ForgeCombatAffixPatch.ApplyEnergyGamble): a strong per-turn boon
        //     — +1 bonus Energy EVERY turn — paid for with a fixed combat-start cost. Mixed (amber): a real
        //     gamble, not a pure boon. Immolating's cost is HP each combat (chip that adds up over a run);
        //     Overclocked's is a PERMANENT Max HP bite that ramps across the run. Both scale no host var, so
        //     any relic can roll them. Fixed amounts → no RNG → deterministic → co-op safe (same class as the
        //     other AfterPlayerTurnStart combat affixes). ---
        new Prefix { Name = "Immolating", Ko = "작열의", Zh = "灼身的", Weight = 4, Mixed = true, TurnEnergy = 1, StartDamage = 5, Color = "#ff6a3d",
            NoteKo = "전투 시작 시 HP 5를 잃지만, 매 턴 에너지 1을 추가로 얻는다",
            NoteEn = "Lose 5 HP at combat start, but gain 1 bonus Energy each turn",
            NoteZh = "战斗开始时失去5点生命，但每回合额外获得1点能量" },
        new Prefix { Name = "Overclocked", Ko = "과열의", Zh = "过热的", Weight = 4, Mixed = true, TurnEnergy = 1, StartMaxHpLoss = 1, Color = "#ff8c42",
            NoteKo = "전투 시작 시 최대 HP 1을 영구히 잃지만, 매 턴 에너지 1을 추가로 얻는다",
            NoteEn = "Permanently lose 1 Max HP at combat start, but gain 1 bonus Energy each turn",
            NoteZh = "战斗开始时永久失去1点最大生命，但每回合额外获得1点能量" },
        // Hit-reactive energy (see HitEnergyAffixPatch): a pure boon — pain fuels you. Each unblocked
        // ENEMY hit has a chance to grant 1 bonus Energy. Green note (no downside), but it only pays out
        // when you're taking real damage, so it rewards an aggressive / low-block style. Scales no host var.
        new Prefix { Name = "Adrenal", Ko = "투지의", Zh = "斗志的", Weight = 5, HitEnergyPercent = 25, Color = "#ffb84d",
            NoteKo = "적에게 막지 못한 피해를 입을 때마다 25% 확률로 에너지 1을 얻는다",
            NoteEn = "Whenever you take unblocked damage from an enemy, 25% chance to gain 1 bonus Energy",
            NoteZh = "每当你受到敌人的未格挡伤害时，25%概率获得1点能量" },
        // Meta aura (see MetaAffix): doubles the IN-COMBAT proc chance of ALL your OTHER forged prefixes —
        // the boons AND the curses (the forge-time curse chance is untouched). Mixed (amber): a build-around
        // gamble — twice the payoff, twice the backfire. The first cross-relic prefix.
        new Prefix { Name = "Catalytic", Ko = "촉매의", Zh = "催化的", Weight = 3, Mixed = true, ProcDoubler = true, Color = "#c04dff",
            NoteKo = "다른 유물 접두사의 전투 중 발동 확률이 2배가 된다 (저주의 발동 확률도 2배)",
            NoteEn = "Your other relics' prefixes proc twice as often in combat — curses included",
            NoteZh = "你其他遗物词缀的战斗触发几率翻倍（诅咒的触发几率也翻倍）" },
        // The "light Catalytic": a FLAT +10% to other prefixes' in-combat proc chances (a fixed bump, not a
        // double), curses included but gentler. Mixed (amber), commoner than Catalytic. Catalytic wins if both.
        new Prefix { Name = "Priming", Ko = "촉진의", Zh = "促进的", Weight = 6, Mixed = true, ProcBoostPct = 10, Color = "#b06fd0",
            NoteKo = "다른 유물 접두사의 전투 중 발동 확률이 10%p 증가한다 (저주 포함)",
            NoteEn = "Your other relics' prefixes proc 10% more often in combat (curses included)",
            NoteZh = "你其他遗物词缀的战斗触发几率提高10%（含诅咒）" },
        // Empowering (증강의): NOT a flat chance bump — your other prefixes' probabilistic combat effects get a
        // SECOND roll and fire if EITHER succeeds (advantage; e.g. 25% → ~44%). A genuine re-attempt. Mixed —
        // it re-rolls curse procs too. Catalytic (×2) takes precedence over it, which takes precedence over Priming.
        new Prefix { Name = "Empowering", Ko = "증강의", Zh = "增幅的", Weight = 3, Mixed = true, ProcReroll = true, Color = "#c04dff",
            NoteKo = "다른 유물 접두사의 전투 중 확률 효과를 한 번 더 굴린다 — 둘 중 하나만 성공해도 발동 (저주 포함)",
            NoteEn = "Your other relics' probabilistic combat effects get a second roll — they fire if either succeeds (curses included)",
            NoteZh = "你其他遗物词缀的战斗几率效果获得第二次骰值——任一成功即触发（含诅咒）" },
        // Cursebound (저주결속의): make your curses an asset. At combat start, gain a Str/Dex/Intangible package
        // scaled by how many CURSES you carry (self-curses + enemy-riders), via a fixed ramp table (see
        // ForgeCombatAffixPatch.ApplyCurseScaling). A boon that rewards a heavily-cursed build.
        new Prefix { Name = "Cursebound", Ko = "저주결속의", Zh = "缚咒的", Weight = 5, CurseScaling = true, Color = "#a86fd0",
            NoteKo = "전투 시작 시 지닌 저주 수에 따라 힘·민첩·불가침을 얻는다 (저주가 많을수록 강해짐)",
            NoteEn = "At combat start, gain Strength / Dexterity / Intangible scaled by how many curses you carry",
            NoteZh = "战斗开始时，根据你携带的诅咒数量获得力量/敏捷/无懈可击（诅咒越多越强）" },

        // --- Card-play boons (see PrefixCardPlayPatch) ---
        new Prefix { Name = "Studious", Ko = "학구의", Zh = "博学的", Weight = 6, PowerDrawFirst = true, Color = "#4dd24d",
            NoteKo = "전투 중 처음으로 파워 카드를 낼 때 카드 2장을 뽑는다",
            NoteEn = "The first time you play a Power card each combat, draw 2 cards",
            NoteZh = "每场战斗首次打出能力牌时，抓2张牌" },
        new Prefix { Name = "Overflowing", Ko = "충만의", Zh = "充盈的", Weight = 5, PowerEnergy = true, Color = "#ffd23f",
            NoteKo = "파워 카드를 낼 때 에너지 1을 얻는다 (턴당 1회)",
            NoteEn = "When you play a Power card, gain 1 Energy (once per turn)",
            NoteZh = "打出能力牌时，获得1点能量（每回合1次）" },
        new Prefix { Name = "Cunning", Ko = "교활의", Zh = "狡黠的", Weight = 6, SkillDraw = true, Color = "#6ec0d9",
            NoteKo = "스킬 카드를 낼 때 25% 확률로 카드 1장을 뽑는다",
            NoteEn = "When you play a Skill card, 25% chance to draw a card",
            NoteZh = "打出技能牌时，25%概率抓1张牌" },

        // --- Exhaust / Ethereal savers (universal siblings of the IC-only Lingering) ---
        new Prefix { Name = "Tenacious", Ko = "끈질긴", Zh = "顽韧的", Weight = 6, ExhaustDodgePct = 25, Color = "#c0a86f",
            NoteKo = "카드가 소멸될 때 25% 확률로 소멸되지 않고 버려진다",
            NoteEn = "When a card would be exhausted, 25% chance it is discarded instead",
            NoteZh = "卡牌将被消耗时，25%概率改为弃置" },
        new Prefix { Name = "Persistent", Ko = "잔존의", Zh = "残留的", Weight = 6, EtherealSave = true, Color = "#8fd0c8",
            NoteKo = "휘발성 카드가 사라질 때 소멸되지 않고 버려진다",
            NoteEn = "An Ethereal card that would exhaust is discarded instead — it survives",
            NoteZh = "虚无牌将消失时，改为弃置而非消耗" },

        // --- Combat-start defensive boons (see ForgeCombatAffixPatch.ApplyStartPower) ---
        new Prefix { Name = "Warding", Ko = "수호의", Zh = "守护的", Weight = 4, StartPower = "Artifact", StartPowerAmount = 1, Color = "#ffd23f",
            NoteKo = "전투 시작 시 인공물 1을 얻는다 (다음 디버프 1회 무효)",
            NoteEn = "Gain 1 Artifact at combat start (negates the next debuff)",
            NoteZh = "战斗开始时获得1层神器（抵消下一个减益）" },
        new Prefix { Name = "Warded", Ko = "방벽의", Zh = "壁垒的", Weight = 3, StartPower = "Buffer", StartPowerAmount = 1, Color = "#7ed0ff",
            NoteKo = "전투 시작 시 방벽 1을 얻는다 (다음 피해 1회 무효)",
            NoteEn = "Gain 1 Buffer at combat start (negates the next instance of damage)",
            NoteZh = "战斗开始时获得1层缓冲（抵消下一次伤害）" },
        new Prefix { Name = "Afterimage", Ko = "잔상의", Zh = "残影的", Weight = 5, StartPower = "Blur", StartPowerAmount = 1, Color = "#9fd8ff",
            NoteKo = "전투 시작 시 잔상 1을 얻는다 (다음 턴 시작 시 방어도가 사라지지 않는다)",
            NoteEn = "Gain 1 Blur at combat start (Block isn't lost at your next turn start)",
            NoteZh = "战斗开始时获得1层残影（下回合开始时格挡不消失）" },

        // --- Kill boon / shop curse ---
        new Prefix { Name = "Plundering", Ko = "강탈의", Zh = "掠夺的", Weight = 6, KillGold = 3, Color = "#ffd97a",
            NoteKo = "적을 처치할 때마다 골드 3을 얻는다",
            NoteEn = "Gain 3 gold each time you kill an enemy",
            NoteZh = "每击杀一个敌人获得3金币" },
        new Prefix { Name = "Covetous", Ko = "탐욕의", Zh = "贪婪的", Weight = 6, Penalty = true, ShopTaxPct = 20, Color = "#b0554d",
            NoteKo = "상점 가격이 20% 오른다",
            NoteEn = "Merchant prices are 20% higher",
            NoteZh = "商店价格提高20%" },
        new Prefix { Name = "Alchemical", Ko = "연금의", Zh = "炼金的", Weight = 6, PotionBlock = 4, Color = "#6ed9c0",
            NoteKo = "포션을 사용할 때 방어도 4를 얻는다",
            NoteEn = "When you use a potion, gain 4 Block",
            NoteZh = "使用药水时，获得4点格挡" },
        // Potion-meta family (permanent buffs / enemy debuffs, distinct from the vanilla temp-Str/Dex potion relics)
        new Prefix { Name = "Fermented", Ko = "발효의", Zh = "发酵的", Weight = 6, PotionStr = 1, Color = "#ff6b4d",
            NoteKo = "포션을 사용할 때 힘 1을 얻는다",
            NoteEn = "When you use a potion, gain 1 Strength",
            NoteZh = "使用药水时，获得1点力量" },
        new Prefix { Name = "Distilled", Ko = "증류의", Zh = "蒸馏的", Weight = 6, PotionDex = 1, Color = "#6ed9c0",
            NoteKo = "포션을 사용할 때 민첩 1을 얻는다",
            NoteEn = "When you use a potion, gain 1 Dexterity",
            NoteZh = "使用药水时，获得1点敏捷" },
        new Prefix { Name = "Corrosive", Ko = "부식의", Zh = "腐蚀的", Weight = 6, PotionVuln = 1, Color = "#e0904d",
            NoteKo = "포션을 사용할 때 모든 적에게 취약 1을 부여한다",
            NoteEn = "When you use a potion, apply 1 Vulnerable to all enemies",
            NoteZh = "使用药水时，对所有敌人施加1层易伤" },
        new Prefix { Name = "Diluting", Ko = "희석의", Zh = "稀释的", Weight = 6, PotionWeak = 1, Color = "#c0a04d",
            NoteKo = "포션을 사용할 때 모든 적에게 약화 1을 부여한다",
            NoteEn = "When you use a potion, apply 1 Weak to all enemies",
            NoteZh = "使用药水时，对所有敌人施加1层虚弱" },
        new Prefix { Name = "Effervescent", Ko = "발포의", Zh = "起泡的", Weight = 5, PotionBufferPct = 25, Color = "#7ed0ff",
            NoteKo = "포션을 사용할 때 25% 확률로 방벽 1을 얻는다",
            NoteEn = "When you use a potion, 25% chance to gain 1 Buffer",
            NoteZh = "使用药水时，25%概率获得1层缓冲" },
        // Potion-meta AURA: while owned, all of your other potion-use prefixes fire at DOUBLE. Rare (low weight).
        new Prefix { Name = "Concentrated", Ko = "농축의", Zh = "浓缩的", Weight = 2, PotionDoubler = true, Color = "#c04dff",
            NoteKo = "포션 사용으로 얻는 접두사 효과가 2배가 된다",
            NoteEn = "Your potion-use prefix effects are doubled",
            NoteZh = "你的药水使用词缀效果翻倍" },

        // --- Low-HP comeback boon: cards flow when you're cornered (rides the turn-start hand-draw count) ---
        new Prefix { Name = "Cornered", Ko = "궁지의", Zh = "绝境的", Weight = 5, LowHpDraw = true, Color = "#ff9f6b",
            NoteKo = "체력이 최대치의 50% 이하일 때 매 턴 카드를 1장 더 뽑는다",
            NoteEn = "While at 50% max HP or below, draw 1 extra card each turn",
            NoteZh = "生命值等于或低于最大值50%时，每回合多抽1张牌" },
        // --- Gold-for-armor: bribe the pain away — a real gold drain, so an amber (Mixed) note ---
        new Prefix { Name = "Bribing", Ko = "매수의", Zh = "收买的", Weight = 4, Mixed = true, GoldArmorCost = 5, Color = "#ffd97a",
            NoteKo = "받는 적 피해가 1 감소하고, 그때마다 골드 5를 소모한다 (골드가 없으면 감소하지 않는다)",
            NoteEn = "Enemy damage you take is reduced by 1, spending 5 gold each time (no reduction while broke)",
            NoteZh = "受到的敌人伤害减少1，每次消耗5金币（没钱时不减免）" },

        // --- Mild amplifier / synergy boons: gently boost OTHER prefixes or reward a forge-heavy build ---
        new Prefix { Name = "Bolstering", Ko = "북돋움의", Zh = "鼓舞的", Weight = 4, StartPowerBoost = 1, Color = "#ffd23f",
            NoteKo = "전투 시작 방어 접두사(수호/방벽/잔상)가 부여하는 수치가 1 증가한다",
            NoteEn = "Your combat-start defensive prefixes (Warding/Warded/Afterimage) grant +1",
            NoteZh = "你的战斗开始防御词缀（守护/壁垒/残影）授予的数值+1" },
        new Prefix { Name = "Refined", Ko = "정련의", Zh = "精炼的", Weight = 4, PotionBoost = 1, Color = "#6ed9c0",
            NoteKo = "포션 사용으로 얻는 접두사 효과의 수치가 각각 1 증가한다",
            NoteEn = "Each of your potion-use prefix effects is increased by 1",
            NoteZh = "你的每个药水使用词缀效果+1" },
        new Prefix { Name = "Attuned", Ko = "조화의", Zh = "调和的", Weight = 5, AttunedBlockPer = 2, Color = "#7ed0ff",
            NoteKo = "전투 시작 시 강화된 다른 유물 1개당 방어도 2를 얻는다 (최대 8)",
            NoteEn = "At combat start, gain 2 Block per other forged relic you carry (max 8)",
            NoteZh = "战斗开始时，每持有一个其他已强化遗物获得2点格挡（最多8）" },
        new Prefix { Name = "Renewing", Ko = "재기의", Zh = "重振的", Weight = 4, SecondWindBlock = 8, Color = "#9fd8ff",
            NoteKo = "전투 중 체력이 처음으로 50% 이하가 될 때 방어도 8을 얻는다 (전투당 1회)",
            NoteEn = "The first time your HP drops to 50% or below in a combat, gain 8 Block (once per combat)",
            NoteZh = "战斗中生命值首次降至50%或以下时，获得8点格挡（每场战斗一次）" },

        // --- Mild combat boons: healing / kill-fuel / late-fight closer / opener / execute ---
        new Prefix { Name = "Regenerating", Ko = "재생의", Zh = "再生的", Weight = 4, StartPower = "Regen", StartPowerAmount = 2, Color = "#7ee0a0",
            NoteKo = "전투 시작 시 재생 2를 얻는다 (매 턴 HP를 회복한다)",
            NoteEn = "Gain 2 Regen at combat start (heal some HP each turn)",
            NoteZh = "战斗开始时获得2层再生（每回合恢复一些生命）" },
        new Prefix { Name = "Finishing", Ko = "처치의", Zh = "终结的", Weight = 5, KillBlock = 3, Color = "#7ed0ff",
            NoteKo = "적을 처치할 때마다 방어도 3을 얻는다",
            NoteEn = "Gain 3 Block each time you kill an enemy",
            NoteZh = "每击杀一个敌人获得3点格挡" },
        new Prefix { Name = "Enduring", Ko = "지구전의", Zh = "持久的", Weight = 5, EnduringStr = 1, Color = "#ff6b4d",
            NoteKo = "전투 5번째 턴부터 매 턴 힘 1을 얻는다",
            NoteEn = "From the 5th turn of combat onward, gain 1 Strength each turn",
            NoteZh = "从战斗第5回合起，每回合获得1点力量" },
        new Prefix { Name = "Preemptive", Ko = "선제의", Zh = "先制的", Weight = 5, FirstTurnDraw = true, Color = "#9fd8ff",
            NoteKo = "전투 첫 턴에 카드를 1장 더 뽑는다",
            NoteEn = "Draw 1 extra card on the first turn of combat",
            NoteZh = "战斗第1回合多抽1张牌" },
        new Prefix { Name = "Executing", Ko = "처단의", Zh = "处决的", Weight = 4, ExecutePct = 25, Color = "#b0554d",
            NoteKo = "체력이 20% 이하인 적에게 주는 피해가 25% 증가한다",
            NoteEn = "Deal 25% more damage to enemies at 20% HP or below",
            NoteZh = "对生命值20%或以下的敌人造成的伤害提高25%" },

        // --- Pickup auto-reforge aura: every relic you find reworks another (curse charge builds = self-limit) ---
        new Prefix { Name = "Forging", Ko = "단조의", Zh = "锻造的", Weight = 3, Mixed = true, PickupReforge = true, Color = "#e0913a",
            NoteKo = "유물을 획득할 때마다, 보유한 다른 유물 하나를 재련한다 (그 유물의 저주 기운이 쌓인다)",
            NoteEn = "Each time you obtain a relic, reforge one other relic you carry (its curse charge builds)",
            NoteZh = "每次获得遗物时，重铸你持有的另一个遗物（其诅咒能量累积）" },

        // --- Run-state affixes: react to GOLD / DECK / CURSE state rather than combat power events.
        //     Cursefed/Gilded are boons (green note); Taxing is a curse (red note). All three scale no
        //     host var (IsCompanionPrefix). Forceable via `forge <relic> Cursefed|Gilded|Taxing`. ---
        new Prefix { Name = "Cursefed", Ko = "저주먹은", Zh = "噬咒的", Weight = 5, CurseDrawStrength = true, Color = "#a86fd0",
            NoteKo = "저주 카드를 뽑으면 힘 또는 민첩 +1 (턴당 1회)",
            NoteEn = "When you draw a Curse card, gain 1 Strength or Dexterity (once per turn)",
            NoteZh = "抽到诅咒牌时，获得1点力量或敏捷（每回合1次）" },
        new Prefix { Name = "Gilded", Ko = "황금빛", Zh = "镀金的", Weight = 4, GoldStrengthPer = 300, Color = "#ffd23f",
            NoteKo = "전투 시작 시 골드 300당 힘 +1",
            NoteEn = "At combat start, gain 1 Strength per 300 gold",
            NoteZh = "战斗开始时，每300金币获得1点力量" },
        new Prefix { Name = "Taxing", Ko = "과세의", Zh = "征税的", Weight = 6, Penalty = true, Color = "#b0554d",
            NoteKo = "전투 시작 시 덱의 카드 1장당 골드 1 손실",
            NoteEn = "At combat start, lose 1 gold per card in your deck",
            NoteZh = "战斗开始时，每有1张牌失去1金币" },

        // --- Fallback prefixes (NOT rolled normally — InPool excludes them; Weight 0). A magnitude
        //     prefix that rounded to no change on a relic is REPLACED by one of these (see
        //     RelicForgeService.Forge). A host-independent, chance-gated minor combat-start buff whose
        //     chance ({0}) is filled from the fizzled prefix's tier — so a reforge/cursed pickup always
        //     does something and the odds are shown. Green (a boon), never Mixed/Penalty. ---
        new Prefix { Name = "Honed", Ko = "벼려진", Zh = "磨砺的", Weight = 0, FallbackStat = "Strength", FallbackAmount = 1, Color = "#7ed957",
            NoteKo = "전투 시작 시 {0}% 확률로 힘 +1", NoteEn = "At combat start, {0}% chance to gain 1 Strength", NoteZh = "战斗开始时，{0}%概率获得1点力量" },
        new Prefix { Name = "Bulwarked", Ko = "굳건한", Zh = "坚壁的", Weight = 0, FallbackStat = "Block", FallbackAmount = 3, Color = "#4db8ff",
            NoteKo = "전투 시작 시 {0}% 확률로 블록 3", NoteEn = "At combat start, {0}% chance to gain 3 Block", NoteZh = "战斗开始时，{0}%概率获得3点格挡" },
        new Prefix { Name = "Nimble", Ko = "날렵한", Zh = "敏捷的", Weight = 0, FallbackStat = "Dexterity", FallbackAmount = 1, Color = "#6ed9c0",
            NoteKo = "전투 시작 시 {0}% 확률로 민첩 +1", NoteEn = "At combat start, {0}% chance to gain 1 Dexterity", NoteZh = "战斗开始时，{0}%概率获得1点敏捷" },
        new Prefix { Name = "Barbed", Ko = "미늘의", Zh = "倒刺的", Weight = 0, FallbackStat = "Thorns", FallbackAmount = 2, Color = "#a7e34d",
            NoteKo = "전투 시작 시 {0}% 확률로 가시 2", NoteEn = "At combat start, {0}% chance to gain 2 Thorns", NoteZh = "战斗开始时，{0}%概率获得2荆棘" },

        // Penalty fallbacks: the mirror of the buff fallbacks above. A NEGATIVE magnitude prefix
        // (Damaged/Shoddy/Broken) that rounds to no change on a relic is replaced (on a reforge) by
        // one of these — a LOW-chance combat-start self-debuff, so a fizzled downside still keeps a
        // little of the gamble's bite instead of vanishing. Penalty = true → red note. Out of pool.
        new Prefix { Name = "Sapped", Ko = "무기력한", Zh = "虚弱的", Weight = 0, FallbackStat = "Weak", FallbackAmount = 1, Penalty = true, Color = "#b0554d",
            NoteKo = "전투 시작 시 {0}% 확률로 자신에게 약화 1", NoteEn = "At combat start, {0}% chance to gain 1 Weak", NoteZh = "战斗开始时，{0}%概率给予自己1虚弱" },
        new Prefix { Name = "Wilted", Ko = "시든", Zh = "枯萎的", Weight = 0, FallbackStat = "Frail", FallbackAmount = 1, Penalty = true, Color = "#a0605a",
            NoteKo = "전투 시작 시 {0}% 확률로 자신에게 손상 1", NoteEn = "At combat start, {0}% chance to gain 1 Frail", NoteZh = "战斗开始时，{0}%概率给予自己1脆弱" },
        new Prefix { Name = "Exposed", Ko = "허술한", Zh = "破绽的", Weight = 0, FallbackStat = "Vulnerable", FallbackAmount = 1, Penalty = true, Color = "#9a6b8f",
            NoteKo = "전투 시작 시 {0}% 확률로 자신에게 취약 1", NoteEn = "At combat start, {0}% chance to gain 1 Vulnerable", NoteZh = "战斗开始时，{0}%概率给予自己1易伤" },
        // Stat-down penalty fallbacks: the mirror of Honed (Strength) / Nimble (Dexterity). FallbackStat
        // carries the "Down" suffix so FallbackBuffPatch applies the power with a NEGATIVE amount (a combat-
        // long self Strength/Dexterity reduction) instead of buffing. FallbackAmount stays positive (the
        // magnitude); the sign lives in the patch.
        new Prefix { Name = "Enervated", Ko = "쇠약한", Zh = "衰弱的", Weight = 0, FallbackStat = "StrengthDown", FallbackAmount = 1, Penalty = true, Color = "#b0554d",
            NoteKo = "전투 시작 시 {0}% 확률로 자신에게 힘 1 감소", NoteEn = "At combat start, {0}% chance to lose 1 Strength", NoteZh = "战斗开始时，{0}%概率失去1点力量" },
        new Prefix { Name = "Sluggish", Ko = "굼뜬", Zh = "迟钝的", Weight = 0, FallbackStat = "DexterityDown", FallbackAmount = 1, Penalty = true, Color = "#9a7a5a",
            NoteKo = "전투 시작 시 {0}% 확률로 자신에게 민첩 1 감소", NoteEn = "At combat start, {0}% chance to lose 1 Dexterity", NoteZh = "战斗开始时，{0}%概率失去1点敏捷" },

        // ============================ Character-gated affixes ============================
        // Each rolls ONLY when the owner plays the named character (CharAffix + RequiredCharacter),
        // and reacts to that character's signature mechanic. See CharAffix / CharAffixPatches.

        // --- Universal Vigor family (see CharAffix.OnVigorConsumed + ForgeCombatAffixPatch) ---
        // Vigor is a VANILLA power (Akabeko, Regent's Patter/Terraforming, the shared Prep Time) and mod
        // characters very commonly build around it (the no-code character creator exposes Vigor as a
        // first-class status), so these are TRULY universal — every character, mod included, can roll them
        // and they work. NOT CharAffix → they live in the Custom pool's Effects tab and toggle per-entry.
        // Two SOURCES (grant Vigor) feed two REACTORS (pay off when Vigor is consumed), so even a character
        // with no native Vigor cards can assemble the loop from the forge alone.
        // NOTE: the Name doubles as the dispatch/descriptor key — must never collide with ANY existing entry
        // ("Tempered" is already the numeric magnitude tier above, hence these themed names).
        new Prefix { Name = "Roused", Ko = "북받친", Zh = "振奋的", Weight = 6, StartVigor = 3, Color = "#ff7a4d",
            NoteKo = "전투 시작 시 활력 3", NoteEn = "Vigor 3 at combat start", NoteZh = "战斗开始时获得3点活力" },
        new Prefix { Name = "Invigorated", Ko = "생기찬", Zh = "焕活的", Weight = 6, TurnVigor = 1, Color = "#ff9a4d",
            NoteKo = "매 턴 활력 1을 얻는다", NoteEn = "Gain 1 Vigor each turn", NoteZh = "每回合获得1点活力" },
        new Prefix { Name = "Quenched", Ko = "담금질한", Zh = "淬炼的", Weight = 7, VigorStrengthPct = 15, Color = "#e07a4d",
            NoteKo = "활력이 소모될 때 소모량 1마다 15% 확률로 힘 1을 얻는다",
            NoteEn = "When Vigor is consumed, each point consumed has a 15% chance to grant 1 Strength",
            NoteZh = "活力被消耗时，每消耗1点有15%概率获得1点力量" },
        new Prefix { Name = "Bracing", Ko = "다잡는", Zh = "坚毅的", Weight = 6, VigorBlockPct = 25, Color = "#5a9fd4",
            NoteKo = "활력이 소모될 때 소모량 1마다 25% 확률로 방어도 2를 얻는다",
            NoteEn = "When Vigor is consumed, each point consumed has a 25% chance to gain 2 Block",
            NoteZh = "活力被消耗时，每消耗1点有25%概率获得2点格挡" },
        new Prefix { Name = "Rending", Ko = "가르는", Zh = "撕裂的", Weight = 6, VigorVulnPct = 20, Color = "#e0904d",
            NoteKo = "활력이 소모될 때 소모량 1마다 20% 확률로 무작위 적에게 취약 1을 부여한다",
            NoteEn = "When Vigor is consumed, each point consumed has a 20% chance to apply 1 Vulnerable to a random enemy",
            NoteZh = "活力被消耗时，每消耗1点有20%概率对随机敌人施加1层易伤" },
        new Prefix { Name = "Withering", Ko = "시들리는", Zh = "凋敝的", Weight = 6, VigorWeakPct = 20, Color = "#c0a04d",
            NoteKo = "활력이 소모될 때 소모량 1마다 20% 확률로 무작위 적에게 약화 1을 부여한다",
            NoteEn = "When Vigor is consumed, each point consumed has a 20% chance to apply 1 Weak to a random enemy",
            NoteZh = "活力被消耗时，每消耗1点有20%概率对随机敌人施加1层虚弱" },
        new Prefix { Name = "Stoking", Ko = "불지피는", Zh = "助燃的", Weight = 5, VigorEnergyPct = 10, Color = "#ffcf3f",
            NoteKo = "활력이 소모될 때 소모량 1마다 10% 확률로 에너지 1을 얻는다",
            NoteEn = "When Vigor is consumed, each point consumed has a 10% chance to gain 1 Energy",
            NoteZh = "活力被消耗时，每消耗1点有10%概率获得1点能量" },
        // Vigor-consume AURA (the vigor-specific, curse-bundled cousin of Catalytic): doubles the OTHER
        // vigor-reactor procs, paid for with a built-in self-debuff on consume. Mixed (amber) — a strong
        // build-around with its own cost, the Echoing/Resonant idiom. Low weight (rare, like Catalytic).
        new Prefix { Name = "Frenzied", Ko = "광란의", Zh = "狂乱的", Weight = 4, Mixed = true, VigorProcDoubler = true, VigorSelfDebuffPct = 5, VigorStatDownPct = 1, Color = "#ff5c33",
            NoteKo = "활력 소모로 발동하는 다른 접두사의 확률이 2배가 된다. 활력을 소모할 때 5% 확률로 자신에게 손상·취약·약화 중 하나 1, 1% 확률로 힘 -1 또는 민첩 -1",
            NoteEn = "Your other Vigor-consume prefixes proc twice as often — but when you consume Vigor, 5% chance to give yourself Frail/Vulnerable/Weak 1, and 1% chance to lose 1 Strength or Dexterity",
            NoteZh = "你其他「消耗活力触发」词缀的触发几率翻倍——但消耗活力时，有5%概率给予自己1层脆弱/易伤/虚弱之一，1%概率失去1点力量或敏捷" },
        // Vigor CURSE (Penalty → rolls into the SelfCurse slot, shows in the Curses tab). Dispatched from
        // OnVigorConsumed's curse-slot pass (a relic can carry a boon prefix AND this curse). A nagging cost
        // on a vigor build: each consume event has a chance to shave 1 Strength (combat-long).
        new Prefix { Name = "Waning", Ko = "쇠하는", Zh = "衰减的", Weight = 6, Penalty = true, VigorStrDownPct = 10, Color = "#8a6a5a",
            NoteKo = "활력을 소모할 때 10% 확률로 자신의 힘 1이 감소한다",
            NoteEn = "When you consume Vigor, 10% chance to lose 1 Strength",
            NoteZh = "消耗活力时，有10%概率失去1点力量" },
        // Vigor GENERATION amplifier (the missing role): boosts how much Vigor you gain — feeds both the
        // sources above and any Vigor-heavy character's cards. Capped 3/turn (the Resonant idiom).
        new Prefix { Name = "Surging", Ko = "공진의", Zh = "共振的", Weight = 5, VigorGainAmplify = true, Color = "#ff9a4d",
            NoteKo = "활력을 얻을 때마다 활력 1을 추가로 얻는다 (턴당 3회)",
            NoteEn = "Whenever you gain Vigor, gain 1 more (up to 3 times per turn)",
            NoteZh = "每当你获得活力时，额外获得1点（每回合最多3次）" },
        new Prefix { Name = "Wellspring", Ko = "샘솟는", Zh = "涌泉的", Weight = 6, VigorRegenPct = 20, Color = "#7ee0a0",
            NoteKo = "활력이 소모될 때 소모량 1마다 20% 확률로 재생 1을 얻는다",
            NoteEn = "When Vigor is consumed, each point consumed has a 20% chance to gain 1 Regen",
            NoteZh = "活力被消耗时，每消耗1点有20%概率获得1点再生" },
        // Turn-end CONVERSION (the Evolution axis — rewards NOT spending Vigor). Consuming Vigor here does
        // NOT trigger the on-consume reactors (a suppression guard in CharAffix), by design — the leftover
        // Vigor is spent by the conversion itself, not by an attack.
        new Prefix { Name = "Congealing", Ko = "응결의", Zh = "凝结的", Weight = 5, VigorTurnEndBlock = true, Color = "#5a9fd4",
            NoteKo = "턴 종료 시 사용하지 않은 활력을 모두 같은 양의 방어도로 전환한다",
            NoteEn = "At the end of your turn, convert all unused Vigor into an equal amount of Block",
            NoteZh = "回合结束时，将所有未使用的活力转化为等量的格挡" },
        new Prefix { Name = "Evolving", Ko = "진화의", Zh = "进化的", Weight = 5, VigorTurnEndStrPct = 18, Color = "#c04dff",
            NoteKo = "턴 종료 시 사용하지 않은 활력을 소모하고, 소모량 1마다 18% 확률로 힘 1을 얻는다",
            NoteEn = "At the end of your turn, consume all unused Vigor; each point consumed has an 18% chance to grant 1 Strength",
            NoteZh = "回合结束时，消耗所有未使用的活力，每消耗1点有18%概率获得1点力量" },

        // --- Ironclad (exhaust / vulnerable / HP-loss — the pool's measured identity:
        //     53 exhaust-family axis tags, 25 vulnerable, 13 HP-loss across his 87 cards) ---
        new Prefix { Name = "Cindered", Ko = "잿불의", Zh = "烬火的", Weight = 7, CharAffix = true, RequiredCharacter = "IRONCLAD", Color = "#e0784d",
            NoteKo = "카드가 소멸될 때마다 방어도 2를 얻는다",
            NoteEn = "Whenever a card is exhausted, gain 2 Block",
            NoteZh = "每当有卡牌被消耗时，获得2点格挡" },
        new Prefix { Name = "Bloodforged", Ko = "혈철의", Zh = "血铁的", Weight = 7, CharAffix = true, RequiredCharacter = "IRONCLAD", Color = "#c05a4d",
            NoteKo = "전투 중 처음으로 HP를 잃을 때 힘 2를 얻는다",
            NoteEn = "The first time you lose HP each combat, gain 2 Strength",
            NoteZh = "每场战斗首次失去HP时，获得2点力量" },
        new Prefix { Name = "Gouging", Ko = "후벼파는", Zh = "剜挖的", Weight = 7, CharAffix = true, RequiredCharacter = "IRONCLAD", Color = "#d09a4d",
            NoteKo = "적에게 취약을 부여할 때 25% 확률로 취약 1 추가",
            NoteEn = "When you apply Vulnerable to an enemy, 25% chance to apply 1 more",
            NoteZh = "对敌人施加易伤时，25%概率额外施加1层" },
        new Prefix { Name = "Retaliating", Ko = "보복의", Zh = "报复的", Weight = 6, CharAffix = true, RequiredCharacter = "IRONCLAD", Color = "#b06a6a",
            NoteKo = "턴 시작 시 지난 턴 이후 잃은 HP의 절반만큼 활력을 얻는다 (잃었다면 최소 1)",
            NoteEn = "At turn start, gain Vigor equal to half the HP you lost since your last turn (minimum 1 if any)",
            NoteZh = "回合开始时，获得等同于上回合以来所失HP一半的活力（若有损失则至少1）" },
        new Prefix { Name = "Mirrored", Ko = "거울의", Zh = "镜映的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "IRONCLAD", Color = "#8a7a7a",
            NoteKo = "힘을 얻을 때 25% 확률로 무작위 적도 힘 1을 얻는다",
            NoteEn = "When you gain Strength, 25% chance a random enemy gains 1 Strength",
            NoteZh = "获得力量时，25%概率随机1名敌人也获得1点力量" },
        new Prefix { Name = "Lingering", Ko = "미련의", Zh = "留恋的", Weight = 6, CharAffix = true, Penalty = true, RequiredCharacter = "IRONCLAD", Color = "#7a6a8a",
            NoteKo = "카드를 소멸시킬 때 25% 확률로 소멸되지 않고 버려진다",
            NoteEn = "When a card would be exhausted, 25% chance it is discarded instead",
            NoteZh = "卡牌将被消耗时，25%概率改为弃置" },
        new Prefix { Name = "Karmic", Ko = "업보의", Zh = "业报的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "IRONCLAD", Color = "#9a6a5a",
            NoteKo = "적에게 취약을 부여할 때 25% 확률로 자신도 취약 1을 얻는다",
            NoteEn = "When you apply Vulnerable to an enemy, 25% chance to gain 1 Vulnerable yourself",
            NoteZh = "对敌人施加易伤时，25%概率自身也获得1层易伤" },

        // --- Silent (poison / shivs / discard) ---
        new Prefix { Name = "Envenomed", Ko = "맹독의", Zh = "剧毒的", Weight = 8, CharAffix = true, RequiredCharacter = "SILENT", Color = "#6ee07a",
            NoteKo = "적에게 중독을 부여할 때 50% 확률로 중독 1 추가",
            NoteEn = "When you apply Poison to an enemy, 50% chance to apply 1 more",
            NoteZh = "对敌人施加中毒时，50%概率额外施加1层" },
        new Prefix { Name = "Flurrying", Ko = "비수의", Zh = "飞刀的", Weight = 7, CharAffix = true, RequiredCharacter = "SILENT", Color = "#c0c8d8",
            NoteKo = "시브를 낼 때 25% 확률로 시브 1장을 손에 넣는다",
            NoteEn = "When you play a Shiv, 25% chance to add a Shiv to your hand",
            NoteZh = "打出小刀时，25%概率将1张小刀加入手牌" },
        new Prefix { Name = "Cycling", Ko = "순환의", Zh = "循环的", Weight = 5, CharAffix = true, RequiredCharacter = "SILENT", Color = "#ffd23f",
            NoteKo = "카드를 버릴 때 카드 1장을 뽑는다",
            NoteEn = "When you discard a card, draw a card",
            NoteZh = "弃牌时，抓1张牌" },
        new Prefix { Name = "Retrieving", Ko = "회수의", Zh = "回收的", Weight = 6, CharAffix = true, RequiredCharacter = "SILENT", Color = "#8fd0c8",
            NoteKo = "시브가 소멸될 때 25% 확률로 손으로 되돌아온다",
            NoteEn = "When a Shiv is exhausted, 25% chance it returns to your hand",
            NoteZh = "小刀被消耗时，25%概率返回你的手牌" },
        new Prefix { Name = "Slippery", Ko = "미끄러운", Zh = "滑手的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "SILENT", Color = "#5a7a8a",
            NoteKo = "매 턴 시작 시 무작위 카드 1장을 버린다",
            NoteEn = "At the start of each turn, discard a random card",
            NoteZh = "每回合开始时，随机弃置1张手牌" },
        new Prefix { Name = "Toxic", Ko = "자멸의", Zh = "自毒的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "SILENT", Color = "#7a8a5a",
            NoteKo = "전투 시작 시 자신에게 중독 3",
            NoteEn = "Poison 3 to yourself at combat start",
            NoteZh = "战斗开始时，给予自己3层中毒" },
        new Prefix { Name = "Dulled", Ko = "무뎌진", Zh = "钝毒的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "SILENT", Color = "#5a7a6a",
            NoteKo = "내가 부여하는 중독으로는 적의 중독이 6을 초과해 쌓이지 않는다",
            NoteEn = "Poison you apply cannot stack an enemy above 6 Poison",
            NoteZh = "你施加的中毒无法使敌人的中毒层数超过6" },

        // --- Defect (orbs / focus) ---
        new Prefix { Name = "Focused", Ko = "집속의", Zh = "集束的", Weight = 7, CharAffix = true, RequiredCharacter = "DEFECT", Color = "#4db8ff",
            NoteKo = "전투 중 처음으로 밀집을 얻을 때 밀집 1 추가",
            NoteEn = "The first time you gain Focus in combat, gain 1 more Focus",
            NoteZh = "战斗中首次获得集中时，额外获得1点集中" },
        new Prefix { Name = "Amplified", Ko = "증폭의", Zh = "增幅的", Weight = 6, CharAffix = true, RequiredCharacter = "DEFECT", Color = "#c04dff",
            NoteKo = "오브를 소환할 때 25% 확률로 무작위 오브 1개를 추가 소환",
            NoteEn = "When you Channel an orb, 25% chance to Channel a random orb",
            NoteZh = "引导充能球时，25%概率额外引导1个随机充能球" },
        new Prefix { Name = "Supercharged", Ko = "과충전의", Zh = "过载的", Weight = 6, CharAffix = true, RequiredCharacter = "DEFECT", Color = "#ffd23f",
            NoteKo = "오브 슬롯이 가득 찼을 때 밀집 1을 얻는다 (슬롯이 차지 않으면 잃는다)",
            NoteEn = "While your orb slots are full, gain 1 Focus (lost while not full)",
            NoteZh = "充能球槽位填满时，获得1点集中（未填满则失去）" },
        new Prefix { Name = "Shorted", Ko = "누전의", Zh = "短路的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "DEFECT", Color = "#6a5a8a",
            NoteKo = "전투 시작 시 밀집 2 감소",
            NoteEn = "Focus -2 at combat start",
            NoteZh = "战斗开始时，集中-2" },
        new Prefix { Name = "Polarized", Ko = "양극의", Zh = "两极的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "DEFECT", Color = "#5a6a8a",
            NoteKo = "오브 슬롯이 모두 비거나 모두 가득 찬 채 턴을 마치면 다음 턴에 에너지 1을 잃는다",
            NoteEn = "If you end your turn with your orb slots all empty or all full, lose 1 Energy next turn",
            NoteZh = "回合结束时充能球槽全空或全满，则下回合失去1点能量" },
        new Prefix { Name = "Preheated", Ko = "예열의", Zh = "预热的", Weight = 7, CharAffix = true, RequiredCharacter = "DEFECT", Color = "#ff9a4d",
            NoteKo = "전투 시작 시 무작위 오브 1개를 소환한다",
            NoteEn = "Channel a random orb at combat start",
            NoteZh = "战斗开始时，引导1个随机充能球" },
        new Prefix { Name = "Sealed", Ko = "봉인된", Zh = "封印的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "DEFECT", Color = "#5a5a7a",
            NoteKo = "전투 시작 시 오브 슬롯 1 감소 (최소 1)",
            NoteEn = "Orb slots -1 at combat start (minimum 1)",
            NoteZh = "战斗开始时，充能球槽位-1（至少保留1）" },
        new Prefix { Name = "Unstable", Ko = "불안정한", Zh = "失稳的", Weight = 6, CharAffix = true, Penalty = true, RequiredCharacter = "DEFECT", Color = "#8a5a6a",
            NoteKo = "턴 종료 시 25% 확률로 가장 오래된 오브가 다른 무작위 오브로 변한다",
            NoteEn = "At turn end, 25% chance your oldest orb becomes a different random orb",
            NoteZh = "回合结束时，25%概率最旧的充能球变为其他随机充能球" },

        // --- Necrobinder (summon / doom) ---
        new Prefix { Name = "Necromantic", Ko = "사령술의", Zh = "死灵的", Weight = 8, CharAffix = true, RequiredCharacter = "NECROBINDER", Color = "#7ed957",
            NoteKo = "소환할 때 블록 3을 얻는다",
            NoteEn = "When you Summon, gain 3 Block",
            NoteZh = "召唤时，获得3点格挡" },
        new Prefix { Name = "Dooming", Ko = "파멸의", Zh = "厄运的", Weight = 7, CharAffix = true, RequiredCharacter = "NECROBINDER", Color = "#a06fd0",
            NoteKo = "적에게 종말을 부여할 때 종말 1 추가",
            NoteEn = "When you apply Doom to an enemy, apply 1 more",
            NoteZh = "对敌人施加厄运时，额外施加1层" },
        new Prefix { Name = "Bonebound", Ko = "뼈엮인", Zh = "缚骨的", Weight = 6, CharAffix = true, RequiredCharacter = "NECROBINDER", Color = "#d9d9e0",
            NoteKo = "매 턴 소환 1 (오스티가 성장한다)",
            NoteEn = "Summon 1 each turn (grows your Osty)",
            NoteZh = "每回合召唤1（成长你的奥斯提）" },
        new Prefix { Name = "Undying", Ko = "불사의", Zh = "不死的", Weight = 6, CharAffix = true, RequiredCharacter = "NECROBINDER", Color = "#b9a3e0",
            NoteKo = "종말로 즉사하지 않는다. 대신 종말 상태에서 이후 HP를 잃으면 죽는다 (죽음 유예)",
            NoteEn = "Doom no longer kills you outright; once doomed, the next HP you lose kills you instead (death deferred)",
            NoteZh = "厄运不再直接杀死你；进入厄运状态后，之后失去HP时才会死亡（死亡延缓）" },
        new Prefix { Name = "Sacrificial", Ko = "희생의", Zh = "献祭的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "NECROBINDER", Color = "#8a6a4a",
            NoteKo = "소환수가 피해를 입으면 자신에게 약화·손상·취약 중 하나 1 (턴당 1회), 소환수가 죽으면 2",
            NoteEn = "When your summon takes damage, apply Weak / Frail / Vulnerable 1 to yourself (once per turn); 2 when it dies",
            NoteZh = "召唤物受到伤害时，给予自己1层虚弱/脆弱/易伤（每回合1次）；召唤物死亡时为2层" },
        new Prefix { Name = "Doombound", Ko = "잠식된", Zh = "被蚀的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "NECROBINDER", Color = "#5a4a7a",
            NoteKo = "매턴 자신에게 종말 1",
            NoteEn = "Each turn, apply 1 Doom to yourself",
            NoteZh = "每回合给予自己1层厄运" },
        new Prefix { Name = "Vengeful", Ko = "설욕의", Zh = "雪耻的", Weight = 6, CharAffix = true, RequiredCharacter = "NECROBINDER", Color = "#c06a8a",
            NoteKo = "턴 시작 시 지난 턴 이후 잃은 HP의 2배만큼 소환한다",
            NoteEn = "At turn start, Summon 2× the HP you lost since your last turn",
            NoteZh = "回合开始时，召唤量为上回合以来你失去HP的2倍" },
        new Prefix { Name = "Empathic", Ko = "감응의", Zh = "感应的", Weight = 6, CharAffix = true, RequiredCharacter = "NECROBINDER", Color = "#7ab8a0",
            NoteKo = "턴 시작 시 지난 턴 이후 소환수가 잃은 HP만큼 방어도를 얻는다",
            NoteEn = "At turn start, gain Block equal to the HP your summon lost since your last turn",
            NoteZh = "回合开始时，获得等同于上回合以来召唤物所失HP的格挡" },
        new Prefix { Name = "Famished", Ko = "굶주린", Zh = "饥馑的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "NECROBINDER", Color = "#6a5a4a",
            NoteKo = "소환하지 않고 턴을 마치면 다음 턴에 오스티가 1 줄어든다",
            NoteEn = "End your turn without Summoning and your Osty shrinks by 1 next turn",
            NoteZh = "回合结束时若未进行召唤，下回合奥斯提缩小1" },
        new Prefix { Name = "Levied", Ko = "징세의", Zh = "重税的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "REGENT", Color = "#8a7a4a",
            NoteKo = "별을 소모하는 카드의 별 소모량 1 증가",
            NoteEn = "Cards that cost Stars cost 1 more Star",
            NoteZh = "消耗星星的卡牌，星星费用+1" },

        // --- Regent (stars / forge) ---
        new Prefix { Name = "Starlit", Ko = "별빛의", Zh = "星耀的", Weight = 7, CharAffix = true, RequiredCharacter = "REGENT", Color = "#ffd23f",
            NoteKo = "매 턴 별 1을 얻는다",
            NoteEn = "Gain 1 Star each turn",
            NoteZh = "每回合获得1颗星" },
        new Prefix { Name = "Reforging", Ko = "재련의", Zh = "重铸的", Weight = 6, CharAffix = true, RequiredCharacter = "REGENT", Color = "#e0904d",
            NoteKo = "단조할 때 별 1을 얻는다",
            NoteEn = "When you Forge, gain 1 Star",
            NoteZh = "锻造卡牌时，获得1颗星" },
        new Prefix { Name = "Regal", Ko = "위엄의", Zh = "威严的", Weight = 6, CharAffix = true, RequiredCharacter = "REGENT", Color = "#4dd24d",
            NoteKo = "별을 소비할 때 50% 확률로 별 1을 돌려받는다",
            NoteEn = "When you spend Stars, 50% chance to refund 1 Star",
            NoteZh = "消耗星辰时，50%概率返还1颗星" },
        new Prefix { Name = "Bankrupt", Ko = "파산한", Zh = "破产的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "REGENT", Color = "#b0554d",
            NoteKo = "별을 4개 소비할 때마다 손에 든 무작위 카드 1장에 휘발성이 부여된다",
            NoteEn = "Every 4 Stars you spend, a random card in your hand becomes Ethereal",
            NoteZh = "每消耗4颗星，手牌中随机1张牌获得虚无" },
        new Prefix { Name = "Tributary", Ko = "헌상의", Zh = "献纳的", Weight = 6, CharAffix = true, RequiredCharacter = "REGENT", Color = "#d0b04d",
            NoteKo = "단조할 때 50% 확률로 카드 1장, 25% 확률로 2장을 뽑는다",
            NoteEn = "When you Forge, 50% chance to draw 1 card, 25% chance to draw 2",
            NoteZh = "锻造时，50%概率抓1张牌，25%概率抓2张" },
        new Prefix { Name = "Bountiful", Ko = "풍요의", Zh = "丰饶的", Weight = 7, CharAffix = true, RequiredCharacter = "REGENT", Color = "#ffd97a",
            NoteKo = "별을 얻을 때 33% 확률로 별 1을 추가로 얻는다",
            NoteEn = "When you gain Stars, 33% chance to gain 1 more",
            NoteZh = "获得星星时，33%概率额外获得1颗" },
        new Prefix { Name = "Prodigal", Ko = "사치의", Zh = "奢靡的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "REGENT", Color = "#8a6a5a",
            NoteKo = "별을 얻을 때 25% 확률로 1 덜 얻는다",
            NoteEn = "When you gain Stars, 25% chance to gain 1 less",
            NoteZh = "获得星星时，25%概率少获得1颗" },
        new Prefix { Name = "Tarnished", Ko = "빛바랜", Zh = "失泽的", Weight = 7, CharAffix = true, Penalty = true, RequiredCharacter = "REGENT", Color = "#7a7a5a",
            NoteKo = "턴 종료 시 별 1을 잃는다",
            NoteEn = "Lose 1 Star at the end of each turn",
            NoteZh = "每回合结束时，失去1颗星" },
    };

    // --- External prefix registration (public API via RelicForgeApi.RegisterPrefix) -----------------
    // Sister mods add DATA-DRIVEN numeric prefixes to the roll pool. Registered at THEIR mod init, so the
    // pool is fixed before any run starts. ★CO-OP CONTRACT: every peer must register the SAME prefixes in
    // the SAME order (holds when both players run the same extension mods + a load-order syncer) — the
    // weighted Roll is seed-deterministic over the pool, so a divergent pool desyncs.
    private static readonly List<Prefix> _external = new();
    // Combined pool (built-ins first, then externals in registration order). Roll / ByName / loc read this.
    private static Prefix[] _pool = All;

    /// <summary>The full prefix pool (built-ins + externally registered), for the roll, name lookup and loc.</summary>
    internal static IReadOnlyList<Prefix> Pool => _pool;

    /// <summary>Append an external numeric prefix to the roll pool. Rejected (logged) if the name is empty
    /// or collides with an existing prefix. Rebuilds the combined pool + invalidates the loc cache so the
    /// new prefix rolls and localizes. Intended for mod-init time only (a run-active call still works but
    /// would change the pool mid-run — logged by RelicForgeApi).</summary>
    internal static bool RegisterExternal(Prefix p)
    {
        if (p == null || string.IsNullOrEmpty(p.Name)) return false;
        // '|' is the forge-descriptor field delimiter (prefix|rider|self|fbStat|fbAmt|fbPct). A name
        // containing it would corrupt every save/wire descriptor of a relic that rolls this prefix —
        // on decode the leading token could even alias a REAL built-in prefix plus a phantom rider curse.
        if (p.Name.IndexOf('|') >= 0)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] RegisterPrefix: '{p.Name}' contains the descriptor delimiter '|' — rejected.");
            return false;
        }
        if (ByName(p.Name) != null)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] RegisterPrefix: '{p.Name}' collides with an existing prefix — ignored.");
            return false;
        }
        _external.Add(p);
        // Externals are ORDER-INSENSITIVE in the combined pool: Roll walks the pool in order, so two
        // peers whose sister mods initialized in a different order would otherwise pick different
        // prefixes from the same seeded roll. Sorting by name makes the pool a pure function of the
        // registered SET — only a genuinely different mod set can still diverge (→ ForgeSafeMode).
        _external.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        var combined = new Prefix[All.Length + _external.Count];
        All.CopyTo(combined, 0);
        for (int i = 0; i < _external.Count; i++) combined[All.Length + i] = _external[i];
        _pool = combined;                 // single assignment — Roll reads the field, never a half-built array
        ForgeLoc.Invalidate();            // so the new prefix's name localizes on next lookup
        return true;
    }

    // Rarities that can receive a prefix at all. Starter/Event/None never do. Ancient is included
    // here but can be opted out at runtime via ForgeConfig.ForgeAncientRelics (see RelicForgeService.Forge).
    public static readonly HashSet<RelicRarity> Eligible = new()
    {
        RelicRarity.Common, RelicRarity.Uncommon, RelicRarity.Rare, RelicRarity.Shop, RelicRarity.Ancient
    };

    /// <summary>Weighted, deterministic pick from the pool (Terraria reforge style). Character-gated
    /// prefixes (RequiredCharacter set) are only in the pool when <paramref name="character"/> — the
    /// owner's CharacterModel Id.Entry — matches; universal prefixes always are. Because the character
    /// is fixed for a whole run, the filtered pool is stable per run, so the roll stays
    /// seed-deterministic and reproduces on load.</summary>
    public static Prefix Roll(Rng rng, string? character = null)
    {
        var pool = _pool;                 // snapshot the field once (registration is init-time; still safe)
        double total = 0;
        foreach (var p in pool) if (InPool(p, character)) total += p.Weight;
        // A custom set that disabled EVERYTHING would leave an empty pool — fall back to the
        // UNfiltered pool (deterministic on every peer: the fallback trips from the same synced set)
        // rather than crash or return an arbitrary entry. The panel blocks this state, so this is a
        // belt-and-suspenders guard for hand-edited/stale custom_pool.json.
        bool filtered = total > 0;
        if (!filtered)
            foreach (var p in pool) if (BaseInPool(p, character)) total += p.Weight;
        double r = rng.NextFloat() * total;
        Prefix? last = null;
        foreach (var p in pool)
        {
            if (!(filtered ? InPool(p, character) : BaseInPool(p, character))) continue;
            last = p;
            r -= p.Weight;
            if (r < 0) return p;
        }
        return last ?? pool[pool.Length - 1];
    }

    /// <summary>Whether a prefix is eligible for the current character's roll pool: fallback prefixes
    /// are NEVER rolled (they only appear via substitution — see RelicForgeService.Forge); PENALTY
    /// prefixes are NEVER rolled either (their downside is re-homed onto the curse side — a penalty
    /// identity is drawn into rec.SelfCurse via <see cref="SelfCurseTable.PickCombined"/>, never into
    /// the beneficial prefix slot); universal (no RequiredCharacter) always, character-gated only when
    /// the character matches (case-insensitive on CharacterModel Id.Entry, e.g. "SILENT"); and the
    /// prefix-pool filter must allow it (<see cref="PoolAllows"/>).</summary>
    private static bool InPool(Prefix p, string? character)
        => BaseInPool(p, character) && PoolAllows(p);

    /// <summary>Eligibility WITHOUT the play-style pool filter — the empty-custom-set fallback.</summary>
    private static bool BaseInPool(Prefix p, string? character)
        => !p.IsFallback && !p.Penalty && CharacterMatches(p, character);

    /// <summary>The play-style prefix-pool filter (<see cref="ForgeConfig.PrefixPool"/>, host-authoritative
    /// in co-op): 1 = enhance-only keeps pure var-scaling prefixes, 2 = effects-only keeps the
    /// mechanic-adding ones. Classification is <see cref="Prefix.IsEnhance"/> (flags + empty-note
    /// invariant — coop-verify caught the keyword family leaking through a flags-only check). MUST
    /// read <see cref="HostForgeConfig"/> — a local read would let two peers roll from different
    /// pools off the same seed (instant desync).</summary>
    internal static bool PoolAllows(Prefix p)
        => HostForgeConfig.PrefixPool switch
        {
            1 => p.IsEnhance,
            2 => !p.IsEnhance,
            ForgeConfig.PoolCustom => !HostForgeConfig.IsPrefixDisabled(p.Name),
            _ => true,
        };

    /// <summary>Does the active play-style pool admit ANY effect (mechanic-adding) prefix? The
    /// combat-start fallback/tie-break GRAFTS (RelicForgeService: a fizzled magnitude prefix rescued
    /// into a "chance to gain N block" buff, a tier tie rider, a re-homed negative into a penalty
    /// fallback) are themselves EFFECT prefixes — they must not leak into a pool the player restricted
    /// to pure enhancements. False for 'Enhance only' (mode 1), and for a Custom pool where every
    /// rollable effect prefix is disabled (same intent, expressed per-entry); true for All / Effects.
    /// Host-authoritative (reads <see cref="HostForgeConfig"/>) so co-op peers agree — same discipline
    /// as <see cref="PoolAllows"/>.</summary>
    internal static bool PoolAllowsAnyEffect()
        => HostForgeConfig.PrefixPool switch
        {
            1 => false,
            2 => true,
            ForgeConfig.PoolCustom => AnyEffectPrefixEnabled(),
            _ => true,
        };

    private static bool AnyEffectPrefixEnabled()
    {
        foreach (var p in _pool)
            if (!p.IsEnhance && !p.IsFallback && !p.Penalty && !HostForgeConfig.IsPrefixDisabled(p.Name))
                return true;
        return false;
    }

    /// <summary>Character-eligibility half of <see cref="InPool"/>, shared with the curse pool: universal
    /// (no RequiredCharacter) always, character-gated only when <paramref name="character"/> matches. The
    /// curse-pool builder (<see cref="SelfCurseTable.PickCombined"/>) pre-filters on <c>Penalty &amp;&amp; !IsFallback</c>
    /// and uses this for the character gate, so a penalty like Toxic only rolls for its own character.</summary>
    public static bool CurseInPool(Prefix p, string? character) => CharacterMatches(p, character);

    private static bool CharacterMatches(Prefix p, string? character)
    {
        // Universal (no RequiredCharacter) rolls for EVERY character, mod characters included — this now
        // covers the Vigor family (Roused/Invigorated/Quenched/Bracing), which is deliberately cross-
        // character: Vigor is a vanilla power that mod characters commonly build around. Character-gated
        // prefixes roll only when the owner's CharacterModel Id.Entry matches (case-insensitive).
        if (p.RequiredCharacter.Length == 0) return true;
        return character != null && string.Equals(p.RequiredCharacter, character, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The fallback prefixes (out of the normal roll pool), used to REPLACE a magnitude prefix
    /// that scaled nothing on a relic — BUFF fallbacks for a positive prefix, PENALTY fallbacks for a
    /// negative one. Order is stable (source order), so the deterministic picks below reproduce across
    /// peers/loads.</summary>
    private static readonly Prefix[] Fallbacks = Array.FindAll(All, p => p.IsFallback && !p.Penalty);
    private static readonly Prefix[] FallbackPenalties = Array.FindAll(All, p => p.IsFallback && p.Penalty);

    /// <summary>Deterministically pick one BUFF fallback from a (mixed) seed. No RNG object / no stream
    /// consumption — a pure index off the seed — so substituting one relic never perturbs any other
    /// relic's roll, and co-op/load reproduce the same choice.</summary>
    public static Prefix PickFallback(uint seed) => Fallbacks[(int)(seed % (uint)Fallbacks.Length)];

    /// <summary>Deterministically pick one PENALTY fallback (mirror of <see cref="PickFallback"/>, for a
    /// negative magnitude prefix that scaled nothing).</summary>
    public static Prefix PickFallbackPenalty(uint seed) => FallbackPenalties[(int)(seed % (uint)FallbackPenalties.Length)];

    /// <summary>The positive, non-Amplify magnitude tiers' powers, ascending. Used to find the tier
    /// just below a rolled one for the tie-break check (see RelicForgeService.ApplyTierTiebreak).</summary>
    private static readonly double[] PositiveTiers = BuildPositiveTiers();
    private static double[] BuildPositiveTiers()
    {
        var list = new List<double>();
        foreach (var p in All)
            if (!p.IsCompanionPrefix && !p.Amplify && p.PowerPct > 0) list.Add(p.PowerPct);
        list.Sort();
        return list.ToArray();
    }

    /// <summary>The largest positive magnitude tier strictly below <paramref name="pct"/>, or 0 if it is
    /// already the lowest tier (nothing below to tie with).</summary>
    public static double NextLowerTierPct(double pct)
    {
        double lower = 0;
        foreach (double t in PositiveTiers) { if (t < pct - 1e-9) lower = t; else break; }
        return lower;
    }

    /// <summary>The fallback prefix that grants a given stat (e.g. "Strength" → Honed), for the tie-break
    /// tooltip note + amount; null if none.</summary>
    public static Prefix? FallbackByStat(string stat)
    {
        foreach (var p in Fallbacks)
            if (string.Equals(p.FallbackStat, stat, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>Find a prefix by name (case-insensitive) for the test console command.</summary>
    public static Prefix? ByName(string name)
    {
        foreach (var p in _pool)
            if (string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }

    /// <summary>Localized display name for a stored (English) prefix name.</summary>
    public static string Localize(string englishName) => ByName(englishName)?.Display ?? englishName;

    /// <summary>Tier tint (BBCode hex) for a stored (English) prefix name; gold fallback.</summary>
    public static string ColorOf(string englishName) => ByName(englishName)?.Color ?? "#e0b64d";
}

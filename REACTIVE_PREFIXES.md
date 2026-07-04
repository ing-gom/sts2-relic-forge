# Reactive Prefixes — design & spec

A new prefix family for Relic Forge: **reactive** affixes that respond in real time to the player's
power/energy changes, rather than the mostly-passive numeric / companion / penalty / gamble families.

**Status: scaffold committed; event hooks + damage command remain.** The data model, the three
prefix rows (at Weight 0), the `AlwaysCurse` curse hook, and the full reaction engine + guards
(`ReactiveAffix`) are in place. What's left needs the game / `Sts2.ModKit` assemblies: the three
event-interception Harmony patches that call the engine, and the damage command for 방전의. The
runtime cost is all deterministic (`PowerCmd` / damage commands), so — unlike the networked reforge
work — **multiplayer is free**: every client re-derives the same reactions.

**Remaining local steps:** (1) wire the three interception patches documented at the bottom of
`ReactiveAffix.cs`; (2) fill in `ReactiveAffix.DealDamage`; (3) set `ReactiveAffix.Enabled = true`
and give the three prefixes a real `Weight` in `PrefixTable.All`; (4) build + co-op test.

## Scope

Three prefixes are locked. Two idea threads are **deferred** to a later discussion and are NOT in
scope here:

- ⏸ **Next-turn settlement** (rhythm / echo / charge forms — e.g. `잔향의 / Lingering`, `예열의 / Charging`)
- ⏸ **New mechanisms** (`가속의`, `각인의`, `표식의`, `수확의`, `동조의`, …)

## The three confirmed prefixes

| Name (KO / EN / ZH) | Effect | Curse |
|---|---|---|
| **공명의 / Resonant / 共鸣的** | Each time you gain Strength or Dexterity, gain **+1 more** of that same power | **Always** ✅ |
| **완강한 / Obstinate / 顽固的** | An **enemy-applied** decrease to your Strength/Dexterity is **inverted into a gain** of the same amount | none |
| **방전의 / Discharging / 放电的** | Each time you gain **Energy**, deal **2 damage to all enemies** | none |

### 공명의 / Resonant — gain amplifier (always cursed)

- Applies to **both** Strength and Dexterity. When the player gains a positive amount of either,
  add **+1** (flat) of that same power.
- **Flat +1 per gain event** (not a percentage) is deliberate: a single large gain (e.g. a
  double-Strength effect) yields only +1, so it can't be exploited into a runaway.
- **Always carries an enemy-rider curse.** This replaces a per-trigger penalty — the mod's existing
  curse system *is* the cost. Force the rider on for this prefix (see "AlwaysCurse" below). Default
  curse flavor: **재앙 (enemy Strength)** — thematically "your growing power echoes into your enemies."
- Guards (all required): positive gains only; a **per-turn trigger cap** (e.g. 3); and a
  **reentrancy guard** so the +1 does not itself re-trigger the amplifier (infinite loop).

### 완강한 / Obstinate — loss inversion (enemy-only)

- When an **enemy** would reduce the player's Strength or Dexterity, apply the **positive** of that
  amount instead ("your resolve can't be broken").
- **Enemy-applied only.** Self-inflicted reductions (Flex-style "gain then lose") are left as
  reductions, so the give-then-take-becomes-permanent exploit never happens. Distinguish by applier
  (`applier != the player`).
- The earlier broader variant (`역류의 / Reversal`, invert *all* reductions) is **dropped**.

### 방전의 / Discharging — energy → AoE

- Each time the player gains Energy, deal **2 damage to all enemies** (flat; ignores Block/Vulnerable
  for simplicity).
- **Counts bonus energy only** — energy gained *beyond* the normal turn-start refill. This keeps it a
  build-around reward (energy-gen synergies) rather than a flat "~6 AoE every turn" engine. (If a
  stronger version is ever wanted, count the refill too and pair it with a curse — not the current
  design.)
- No curse. No loop risk: dealing damage does not grant energy.

## Implementation

### 1. `Prefix` struct — new flags (`PrefixTable.cs`)

Mirror the existing per-prefix flags (`EnemyStrip`, `SymPower`, `RandomDebuff`, `Mixed`, `Penalty`, …):

```csharp
public bool   GainAmplify;    // 공명의: on player Str/Dex gain, +1 that power
public bool   LossInvert;     // 완강한: enemy-applied Str/Dex loss -> gain
public int    EnergyDischarge;// 방전의: damage-to-all-enemies per bonus energy gained (0 = off)
public bool   AlwaysCurse;    // force the enemy-rider curse to 100% for this prefix
```

Add the three rows to `PrefixTable.All` with trilingual `Note*` text and a tier `Color`, and carry
the new flags into `ForgeRecord` where the dispatch reads them (as `Amplify`/companion fields already
are, see `RelicForgeService.Forge` ~line 346).

### 2. `AlwaysCurse` — force the enemy-rider curse (`RelicForgeService.Forge`)

`Forge` already rolls the rider: `riderRoll < EnemyRiderChance` and "penalty prefixes never roll it".
Add: **if `prefix.AlwaysCurse`, land the rider unconditionally** (bypass the chance), and pick the
default flavor (재앙 / enemy Strength) unless the seeded `suffixRoll` already chose one. Keep it inside
the same seeded draw order so determinism/save-load stays exact.

### 3. Reaction hooks — the one piece that needs the assemblies

The existing combat dispatch (`ForgeCombatAffixPatch`) fires on `Hook.AfterPlayerTurnStart` — that is
turn-scoped and **not** enough for these, which react to individual power/energy *events*. Each needs
an event interception point. Confirm which the game exposes (a `Hook.After…` event, or a Harmony
patch on the relevant `…Cmd` / stacking method):

- **`GainAmplify` (공명의)** — intercept a power being applied to the player. If target == player,
  power is Strength/Dexterity, and delta > 0, and the owner has this prefix → `PowerCmd.Apply<T>(+1)`.
  Wrap in the reentrancy guard + per-turn cap.
- **`LossInvert` (완강한)** — same interception; if delta < 0 and applier is an enemy → apply
  `+abs(delta)` instead (and cancel/skip the original reduction).
- **`EnergyDischarge` (방전의)** — intercept player energy gain; for bonus energy (beyond refill),
  deal `EnergyDischarge` damage to every enemy via the game's damage command (the AoE analogue of the
  patterns in `EnemyForge` / `ForgeCombatAffixPatch`).

All effects go through `PowerCmd` / damage commands, which are networked + deterministic → consistent
across co-op clients with no extra sync (same property the passive forge already relies on).

### 4. Guards (must-have)

- **Reentrancy** (공명의): a static/thread-local "applying bonus" flag so the +1 gain is not itself
  amplified — otherwise the first Strength gain loops forever.
- **Per-turn cap** (공명의): bound triggers per turn (e.g. 3) — tames Demon-Form / multi-source
  stacking and doubles as a loop backstop.
- **Positive/external only**: 공명의 reacts to gains > 0; 완강한 only to enemy-applied losses.
- **Flat, not %**: keeps single large gains from exploding.

## Balance summary

- 공명의 is the strongest → its cost is the **mandatory curse** (elites/bosses gain 재앙), self-scaling.
- 완강한 is **conditional** (dead without enemy Str/Dex shred) → naturally balanced, no curse needed.
- 방전의 counts **bonus energy only** at **2** damage → build-around, no curse needed.

## Files this will touch

- `Sts2RelicForgeCode/PrefixTable.cs` — new flags + three prefix rows.
- `Sts2RelicForgeCode/ForgeRecord.cs` — carry the new flags on the record.
- `Sts2RelicForgeCode/RelicForgeService.cs` — `AlwaysCurse` in the rider roll.
- **New** `Sts2RelicForgeCode/ReactiveAffixPatch.cs` — the power/energy event hooks + guards.
- `PREFIXES.*.md` / `prefix_dashboard.html` — document the new rows once shipped.

## Prototype order

1. **방전의** or **공명의** first — establishes the reactive event-hook + guard pattern the others reuse.
2. **완강한** — same interception point as 공명의, opposite sign.

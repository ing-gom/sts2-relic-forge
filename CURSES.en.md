# Relic Forge — Curse Guide

**Language:** English · [한국어](CURSES.ko.md) · [简体中文](CURSES.zh.md)

A forged relic can also roll **one curse**. There are two kinds, and a relic carries **only one of them**:

- **Enemy curse** — while you own it, **enemies grow stronger** (Strength, Plated Armor, Max HP, and more).
- **Self-curse** — **you** take a penalty each time you're hit by **unblocked** damage.

The relic's name gains a `(Cursed)` tag, and its tooltip states the exact effect.

> **Penalty prefixes are curses too.** A [penalty prefix](PREFIXES.en.md#3-penalty-prefixes--curses) (Cursed, Cumbersome, Tainted…) is the merged, unified downside: it counts as a curse everywhere below — it **ends a reforge** and is removed by **Cleanse** — the only difference is the penalty *is* the prefix, so cleansing it reverts the relic to no prefix rather than keeping one.

> **Weakening rolls are curses too.** A relic no longer rolls a *weaker* prefix (the old Damaged / Shoddy / Broken). That downside is expressed as a **curse** instead — the relic keeps its normal power and gains a curse, rather than having its numbers shaved. Every downside lives in the curse slot.

> **A curse tightens the forge — but doesn't always stop it.** Each curse that appears while reforging at one campfire or shop deepens the "curse aura." It ends reforging there with a **10% → 25% → 50% → 80%** chance for the 1st → 4th curse, and the **5th curse always ends it**. The tally is per **location** and resets at the next campfire / shop. Until it ends you can keep re-rolling (paying gold at a shop); once cold, only **Cleanse** at a merchant (default 100 gold) removes the curse.

> **On by default** — controlled by **"Enemy forge"** in ModConfig. Turn it **off** for a pure power fantasy.

---

## How it works

- **Stronger = cursed** — when a relic rolls a prefix, it may also carry a curse. The chance **scales with the prefix's power** — a weak prefix is rarely cursed, while a **Legendary (+60%)** is almost always cursed (reference ~33%, adjustable via a ModConfig slider).
- **One or the other** — an enemy curse and a self-curse **never appear together**. ModConfig **"Self-curse share"** sets the split between the two (default: 22% of curses are self-curses — the harsher kind is kept the minority).
- **No double-dipping** — a penalty prefix never *also* carries an enemy-rider / self-curse; it's already a curse in its own right.
- **Deterministic** — which curse lands, and how strong, is fixed by the run seed (same seed = same result, multiplayer-safe). Reforging re-rolls everything — but each curse that lands raises the chance the reforge **ends** (see below), so you can't endlessly spin the wheel to escape a curse.

## Enemy curses

A curse on a stronger relic empowers enemies more. The Strength/defense curses fire in **elite/boss fights**, and carrying several curses **spreads them across the pack** — each curse is assigned to a different enemy (a single enemy gets them all).

**Strength & defense** (elites/bosses)

| Curse | Enemies gain | Timing |
|---|---|---|
| of Wrath | Strength | combat start |
| of Malice | Plated Armor (block each turn) | combat start |
| of Spite | Thorns (retaliate) | combat start |
| of the Tyrant | Strength + Plated Armor + Thorns | combat start |
| of Bloodlust | Regen (heal each turn) | combat start |
| of Warding | Artifact (negate your debuffs) | combat start |
| of Shielding | Buffer (negate the next hits) | combat start |
| of Frenzy | Strength | **every 3rd turn** |

**Max HP** (all enemies in scope)

| Curse | Effect | Applies to |
|---|---|---|
| of Vigor | +20% Max HP | all enemies in normal fights |
| of Girth | +12% Max HP | all enemies in every fight |
| of the Titan | +30% Max HP | elites |
| of Eternity | +25% Max HP | bosses |

## Self-curses

Fires **on each unblocked hit**, so a multi-hit attack you don't fully block stacks the penalty **in proportion to the hit count** (block it all and it never fires — tighter play is rewarded).

| Curse | Effect (per unblocked hit) |
|---|---|
| Enfeebling | Weak 1 to self |
| Cracking | Frail 1 to self |
| Vulnerating | Vulnerable 1 to self |
| Bewildering | adds a Dazed to your draw pile |
| Wretched | a random one of Weak / Frail / Vulnerable |

**More curses are on the way.**

## Notes

- **Toggle** — on by default; turning off **"Enemy forge"** in ModConfig hides and disables all curses (the relic forging itself is unchanged).
- **Tuning** — **"Curse chance"** (reference chance), **"Self-curse share"** (the enemy-vs-self split), and **"Enemy balance strength"** (how hard enemies are buffed).
- **Reforge ends probabilistically on curses** — a curse no longer hard-stops reforging. Each curse that appears at a campfire / shop raises the chance the forge "goes cold": **10% / 25% / 50% / 80%** for the 1st–4th, and the **5th curse always ends it**. The tally is per location and resets when you leave. Remove a curse with **Cleanse** at a merchant (default 100 gold).
- **Cleanse** — strips an enemy-rider / self-curse while keeping the prefix; on a penalty prefix (which *is* the curse) it purges the prefix, reverting the relic to no prefix.

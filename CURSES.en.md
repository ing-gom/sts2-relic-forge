# Relic Forge — Curse Guide

**Language:** English · [한국어](CURSES.ko.md) · [简体中文](CURSES.zh.md)

A forged relic can also roll a **curse** — while you own it, **elites and bosses grow stronger**. The relic's name gains a `(Cursed)` tag, and its tooltip states exactly what the enemies gain.

> **On by default** — controlled by **"Enemy forge"** in ModConfig. Turn it **off** for a pure power fantasy.

---

## How it works

- **Curse chance** — when a relic rolls a prefix, it has roughly a **33%** chance to also carry a curse (adjustable via a ModConfig slider). Penalty prefixes never carry one.
- **Enemy buff** — in an elite/boss fight, the curse effect lands on **one enemy**, and carrying several curses **stacks their effects onto that one enemy**.
- **As strong as you are** — a curse on a stronger relic empowers the enemy more. Amounts are a **seed-fixed random** within a range (same seed = same result, multiplayer-safe).
- **Deterministic** — which enemy is buffed, and by how much, is fixed by the run seed.

## Curse list

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

**More curses are on the way.**

## Notes

- **Toggle** — on by default; turning off **"Enemy forge"** in ModConfig hides and disables all curses (the relic forging itself is unchanged).
- **Tuning** — **"Enemy balance strength"** scales how hard enemies are buffed; **"Enemy-rider chance"** sets the curse chance.
- **Reforge** — re-forging a relic re-rolls its curse too.

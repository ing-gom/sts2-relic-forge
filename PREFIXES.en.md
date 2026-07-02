# Relic Forge — Prefix Guide

**Language:** English · [한국어](PREFIXES.ko.md) · [简体中文](PREFIXES.zh.md)

Every prefix a forged relic can roll. Prefixes are **seed-locked** (same seed → same result); starter relics are excluded. By default only ~**40%** of relics get a prefix (tunable via a ModConfig slider).

> The **Share** column is each prefix's weight **within the prefix pool** (rolled when a relic does get a prefix). Actual per-relic chance ≈ 40% × Share.

---

## 1. Numeric prefixes

Scale a relic's numbers up or down; the tooltip shows the change in color (green up / red down).

| Prefix | Effect | Share |
|---|---|---:|
| Legendary | +60% | 0.8% |
| Godly | +35% | 1.5% |
| Demonic | +25% | 2.3% |
| Superior | +18% | 3.4% |
| Forceful | +12% | 4.6% |
| Hurtful | +8% | 5.7% |
| Zealous | +6% | 5.7% |
| Keen | +4% | 5.7% |
| Volatile | raises boons AND downsides (high risk / high reward) | 2.3% |
| Damaged | −12% | 5.3% |
| Shoddy | −18% | 3.0% |
| Broken | −25% | 1.9% |

Exact per-relic numbers are in the interactive [`prefix_dashboard.html`](prefix_dashboard.html).

---

## 2. Companion prefixes

Instead of scaling numbers, these graft another relic's effect onto yours — but as a **weaker version** than owning the real relic (reduced value, or a later/less frequent trigger), so the original relic is still worth getting. They can roll on **any** relic.

| Prefix | Effect | From | Share |
|---|---|---|---:|
| Thorned | Thorns 2 at combat start | Bronze Scales | 3.4% |
| Mighty | Strength +1 on turn 3 | Vajra | 2.3% |
| Quicksilver | 2 damage to all enemies each turn | Mercury Hourglass | 2.3% |
| Anchored | Block 6 at combat start | Anchor | 2.7% |
| Vital | Heal 1 on turn 1 | Blood Vial | 3.0% |
| Rhythmic | +1 energy every 4 turns | Happy Flower | 1.9% |
| Insightful | Draw 2 cards when first hit | Centennial Puzzle | 2.7% |
| Intimidating | Vulnerable 1 to all enemies on turn 2 | Bag of Marbles | 3.0% |
| Ferocious | Vigor 5 on turn 1 | Akabeko | 1.9% |
| Bladed | 3 damage to all enemies per 4 skills in one turn | Letter Opener | 2.3% |
| Relentless | Strength +1 per 4 attacks in one turn | Shuriken | 1.9% |
| Tempered | Block 4 if you end your turn with no Block | Orichalcum | 2.7% |
| Gusting | Block 2 per 4 attacks in one turn | Ornamental Fan | 2.7% |
| Darting | Dexterity +1 per 4 attacks in one turn | Kunai | 2.3% |
| Supple | Dexterity +1 on turn 2 | Oddly Smooth Stone | 2.7% |
| Accelerating | +1 energy every 12 attacks | Nunchaku | 1.9% |

> More companion prefixes are on the way.

---

## 3. Penalty prefixes

A minority of prefixes are pure downsides (Terraria-style bad rolls) — a small curse on the relic, no upside. Low roll rate.

| Prefix | Downside | Share |
|---|---|---:|
| Cursed | Weak 1 to self at combat start | 3.0% |
| Cumbersome | Dexterity -1 to self on turn 1 | 3.0% |
| Fickle | 25% each turn: a random debuff to self | 2.3% |
| Overloaded | Vulnerable 1 to self after 6 cards in one turn | 2.3% |
| Tainted | Adds a Dazed to your draw pile each turn | 1.9% |
| Festering | Adds 2 Wounds to your discard at combat start | 1.9% |
| Smoldering | Adds a Burn to your draw pile at combat start | 1.9% |
| Hollow | Adds a Void to your draw pile at combat start | 1.9% |

---

## Notes

- **Not a balance mod** — a power-fantasy add-on.
- **Survives save/load** (re-derived from the seed).
- **Numeric prefixes work on modded relics** automatically.
- **Disable** via the ModConfig "No-prefix chance" slider (100%).

---

*[Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3755793010) · Built on Slay the Spire 2 by MegaCrit · MIT*

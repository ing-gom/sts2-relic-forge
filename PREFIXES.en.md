# Relic Forge — Prefix Guide

**Language:** English · [한국어](PREFIXES.ko.md) · [简体中文](PREFIXES.zh.md)

Every prefix a forged relic can roll. Prefixes are **seed-locked** (same seed → same result); starter relics are excluded. By default only ~**40%** of relics get a prefix (tunable via a ModConfig slider).

> The **Share** column is each prefix's weight **within the prefix pool** (rolled when a relic does get a prefix). Actual per-relic chance ≈ 40% × Share.

---

## 1. Numeric prefixes

Scale a relic's numbers up or down; the tooltip shows the change in color (green up / red down).

| Prefix | Effect | Share |
|---|---|---:|
| Legendary | +60% | 0.9% |
| Godly | +35% | 1.9% |
| Demonic | +25% | 2.8% |
| Superior | +18% | 4.2% |
| Forceful | +12% | 5.6% |
| Hurtful | +8% | 7.0% |
| Zealous | +6% | 7.0% |
| Keen | +4% | 7.0% |
| Volatile | raises boons AND downsides (high risk / high reward) | 2.8% |
| Damaged | −12% | 6.5% |
| Shoddy | −18% | 3.7% |
| Broken | −25% | 2.3% |

Exact per-relic numbers are in the interactive [`prefix_dashboard.html`](prefix_dashboard.html).

---

## 2. Companion prefixes

Instead of scaling numbers, these graft another relic's effect onto yours — but as a **weaker version** than owning the real relic (reduced value, or a later/less frequent trigger), so the original relic is still worth getting. They can roll on **any** relic.

| Prefix | Effect | From | Share |
|---|---|---|---:|
| Thorned | Thorns 2 at combat start | Bronze Scales | 4.2% |
| Mighty | Strength +1 on turn 3 | Vajra | 2.8% |
| Quicksilver | 2 damage to all enemies each turn | Mercury Hourglass | 2.8% |
| Anchored | Block 6 at combat start | Anchor | 3.3% |
| Vital | Heal 1 on turn 1 | Blood Vial | 3.7% |
| Rhythmic | +1 energy every 4 turns | Happy Flower | 2.3% |
| Insightful | Draw 2 cards when first hit | Centennial Puzzle | 3.3% |
| Intimidating | Vulnerable 1 to all enemies on turn 2 | Bag of Marbles | 3.7% |
| Ferocious | Vigor 5 on turn 1 | Akabeko | 2.3% |
| Bladed | 3 damage to all enemies per 4 skills in one turn | Letter Opener | 2.8% |
| Relentless | Strength +1 per 4 attacks in one turn | Shuriken | 2.3% |
| Tempered | Block 4 if you end your turn with no Block | Orichalcum | 3.3% |
| Gusting | Block 2 per 4 attacks in one turn | Ornamental Fan | 3.3% |
| Darting | Dexterity +1 per 4 attacks in one turn | Kunai | 2.8% |
| Supple | Dexterity +1 on turn 2 | Oddly Smooth Stone | 3.3% |
| Accelerating | +1 energy every 12 attacks | Nunchaku | 2.3% |

> More companion prefixes are on the way.

---

## Notes

- **Not a balance mod** — a power-fantasy add-on.
- **Survives save/load** (re-derived from the seed).
- **Numeric prefixes work on modded relics** automatically.
- **Disable** via the ModConfig "No-prefix chance" slider (100%).

---

*[Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3755793010) · Built on Slay the Spire 2 by MegaCrit · MIT*

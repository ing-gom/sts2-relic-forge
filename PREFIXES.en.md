# Relic Forge — Prefix Guide

**Language:** English · [한국어](PREFIXES.ko.md) · [简体中文](PREFIXES.zh.md)

Every prefix a forged relic can roll. Prefixes are **seed-locked** (same seed → same result); starter relics are excluded. By default only ~**40%** of relics get a prefix (tunable via a ModConfig slider).

> The **Share** column is each prefix's weight **within the prefix pool** (rolled when a relic does get a prefix). Actual per-relic chance ≈ 40% × Share.

---

## 1. Numeric prefixes

Scale a relic's numbers up or down; the tooltip shows the change in color (green up / red down).

| Prefix | Effect | Share |
|---|---|---:|
| Legendary | +60% | 1.2% |
| Godly | +35% | 2.4% |
| Demonic | +25% | 3.6% |
| Superior | +18% | 5.4% |
| Forceful | +12% | 7.2% |
| Hurtful | +8% | 9.0% |
| Zealous | +6% | 9.0% |
| Keen | +4% | 9.0% |
| Volatile | raises boons AND downsides (high risk / high reward) | 3.6% |
| Damaged | −12% | 8.4% |
| Shoddy | −18% | 4.8% |
| Broken | −25% | 3.0% |

Exact per-relic numbers are in the interactive [`prefix_dashboard.html`](prefix_dashboard.html).

---

## 2. Companion prefixes

Instead of scaling numbers, these graft another relic's effect onto yours — but as a **weaker version** than owning the real relic (reduced value, or a later/less frequent trigger), so the original relic is still worth getting. They can roll on **any** relic.

| Prefix | Effect | From | Share |
|---|---|---|---:|
| Thorned | Thorns 2 at combat start | Bronze Scales | 5.4% |
| Mighty | Strength +1 on turn 3 | Vajra | 3.6% |
| Quicksilver | 2 damage to all enemies each turn | Mercury Hourglass | 3.6% |
| Anchored | Block 6 at combat start | Anchor | 4.2% |
| Vital | Heal 1 on turn 1 | Blood Vial | 4.8% |
| Rhythmic | +1 energy every 4 turns | Happy Flower | 3.0% |
| Insightful | Draw 2 cards when first hit | Centennial Puzzle | 4.2% |
| Intimidating | Vulnerable 1 to all enemies on turn 2 | Bag of Marbles | 4.8% |

> More companion prefixes are on the way.

---

## Notes

- **Not a balance mod** — a power-fantasy add-on.
- **Survives save/load** (re-derived from the seed).
- **Numeric prefixes work on modded relics** automatically.
- **Disable** via the ModConfig "No-prefix chance" slider (100%).

---

*[Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3755793010) · Built on Slay the Spire 2 by MegaCrit · MIT*

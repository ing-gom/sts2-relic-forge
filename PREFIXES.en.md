# Relic Forge — Prefix Guide

**Language:** English · [한국어](PREFIXES.ko.md) · [简体中文](PREFIXES.zh.md)

Every prefix a forged relic can roll. Prefixes are **seed-locked** (same seed → same result); starter relics are excluded. By default only ~**40%** of relics get a prefix (tunable via a ModConfig slider).

> The **Share** column is each prefix's weight **within the prefix pool** (rolled when a relic does get a prefix). Actual per-relic chance ≈ 40% × Share.

---

## 1. Numeric prefixes

Scale a relic's numbers up or down; the tooltip shows the change in color (green up / red down).

| Prefix | Effect | Share |
|---|---|---:|
| Legendary | +60% | 0.6% |
| Godly | +30% | 1.2% |
| Demonic | +25% | 1.8% |
| Superior | +18% | 2.7% |
| Forceful | +12% | 3.6% |
| Hurtful | +8% | 4.5% |
| Zealous | +6% | 4.5% |
| Keen | +4% | 4.5% |
| Volatile | raises boons AND downsides (high risk / high reward) | 1.8% |

Exact per-relic numbers are in the interactive [`prefix_dashboard.html`](prefix_dashboard.html).

> **Weakening rolls → curses.** About **8%** of rolls used to land a *weaker* prefix (Damaged −12% / Shoddy −18% / Broken −25%). Those rolls now yield a **[curse](CURSES.en.md)** instead — the relic keeps its normal numbers and gains a curse. Every downside lives in the curse slot.

---

## 2. Companion prefixes

Instead of scaling numbers, these graft another relic's effect onto yours — but as a **weaker version** than owning the real relic (reduced value, or a later/less frequent trigger), so the original relic is still worth getting. They can roll on **any** relic.

| Prefix | Effect | From | Share |
|---|---|---|---:|
| Thorned | Thorns 1 at combat start | Bronze Scales | 2.7% |
| Mighty | Strength +1 on turn 3 | Vajra | 1.8% |
| Quicksilver | 1 damage to all enemies each turn | Mercury Hourglass | 1.8% |
| Anchored | Block 3 at combat start | Anchor | 2.1% |
| Vital | Heal 1 on turn 1 | Blood Vial | 2.4% |
| Rhythmic | +1 energy every 4 turns | Happy Flower | 1.5% |
| Insightful | Draw 1 card when first hit | Centennial Puzzle | 2.1% |
| Intimidating | Vulnerable 1 to all enemies on turn 2 | Bag of Marbles | 2.4% |
| Ferocious | Vigor 2 on turn 1 | Akabeko | 1.5% |
| Bladed | 1 damage to all enemies per 4 skills in one turn | Letter Opener | 1.8% |
| Relentless | Strength +1 per 4 attacks in one turn | Shuriken | 1.5% |
| Tempered | Block 1 if you end your turn with no Block | Orichalcum | 2.1% |
| Gusting | Block 1 per 4 attacks in one turn | Ornamental Fan | 2.1% |
| Darting | Dexterity +1 per 4 attacks in one turn | Kunai | 1.8% |
| Supple | Dexterity +1 on turn 2 | Oddly Smooth Stone | 2.1% |
| Accelerating | +1 energy every 12 attacks | Nunchaku | 1.5% |

> More companion prefixes are on the way.

---

## 3. Penalty prefixes = curses

A minority of prefixes are pure downsides (Terraria-style bad rolls) — a small curse on the relic, no upside. Low roll rate. **These count as curses** (the merged concept): rolling one **ends the reforge** (campfire *and* shop — a curse can't be re-rolled away for cheap gold), and the only way to shed it is to **Cleanse** at a merchant, which reverts the relic to no prefix. See the [Curse Guide](CURSES.en.md).

| Prefix | Downside | Share |
|---|---|---:|
| Cursed | Weak 1 to self at combat start | 2.4% |
| Cumbersome | Dexterity -1 to self on turn 1 | 2.4% |
| Fickle | 25% each turn: a random debuff to self | 1.8% |
| Overloaded | Vulnerable 1 to self after 6 cards in one turn | 1.8% |
| Tainted | Adds a Dazed to your draw pile each turn | 1.5% |
| Festering | Adds 2 Wounds to your discard at combat start | 1.5% |
| Smoldering | Adds a Burn to your draw pile at combat start | 1.5% |
| Hollow | Adds a Void to your draw pile at combat start | 1.5% |

---

## 4. Gamble prefixes

Mixed effects that also reach the battlefield — they can help (debuff an enemy) or hurt (debuff you), a coin-flip each time. Enemy/player effects hit a single random target.

| Prefix | Effect | Share |
|---|---|---:|
| Eroding | Each turn, move one enemy power 1 toward zero (buffs and debuffs alike) | 1.8% |
| Exposing | Vulnerable 1 to one enemy and yourself at combat start | 1.8% |
| Enervating | Weak 1 to one enemy and yourself at combat start | 1.8% |
| Chaotic | Each turn, 50% chance: Vulnerable or Weak 1 to one enemy or one player | 1.8% |

---

## 5. Reactive prefixes

These react in real time to your power/energy events in combat — the moment you gain a stat or extra energy, or an enemy tries to shred you.

| Prefix | Effect | Share |
|---|---|---:|
| Resonant | When you gain Strength or Dexterity, gain 1 more of it (up to 3/turn) | 1.5% |
| Obstinate | When an enemy reduces your Strength or Dexterity, gain that amount instead | 1.8% |
| Discharging | Whenever you gain bonus Energy, deal 4 damage to all enemies | 1.8% |

---

## 6. Run-state prefixes

React to your RUN state — gold, deck, curses — instead of combat events.

| Prefix | Effect | Share |
|---|---|---:|
| Cursefed | When you draw a Curse card, gain 1 Strength or Dexterity (once per turn) | 1.5% |
| Gilded | At combat start, gain 1 Strength per 300 gold | 1.2% |
| Taxing *(curse)* | At combat start, lose 1 gold per card in your deck | 1.8% |

---

## 7. Keyword prefixes

Grant or inflict CARD KEYWORDS through the game's own systems — the keyword shows right on the card face.

| Prefix | Effect | Share |
|---|---|---:|
| Retaining | At the end of your turn, Retain a random card in your hand | 1.8% |
| Searing *(curse)* | When you play a card, 25% chance it gains Exhaust (takes effect from its next play) | 1.8% |
| Echoing | Each turn, a random card in your hand gains Replay 1. Playing that card gives you Vulnerable 1 and Frail 1 | 0.9% |

---

## 8. Character prefixes

Only roll while you PLAY that character, riding the character's signature mechanic (poison / orbs / summons / stars). Each character gets 3 boons and 2 curses.

| Character | Prefix | Effect | Share |
|---|---|---|---:|
| Silent | Envenomed | When you apply Poison to an enemy, 50% chance to apply 1 more | 2.1% |
| Silent | Flurrying | When you play a Shiv, 25% chance to add a Shiv to your hand | 1.8% |
| Silent | Cycling | When you discard a card, draw a card | 1.3% |
| Silent | Retrieving | When a Shiv is exhausted, 25% chance it returns to your hand | 1.6% |
| Silent | Slippery *(curse)* | At the start of each turn, discard a random card | 1.8% |
| Silent | Toxic *(curse)* | Poison 3 to yourself at combat start | 1.8% |
| Silent | Dulled *(curse)* | Poison you apply cannot stack an enemy above 6 Poison | 1.8% |
| Defect | Focused | The first time you gain Focus in combat, gain 1 more Focus | 1.8% |
| Defect | Amplified | When you Channel an orb, 25% chance to Channel a random orb | 1.6% |
| Defect | Supercharged | While your orb slots are full, gain 1 Focus (lost while not full) | 1.6% |
| Defect | Shorted *(curse)* | Focus -2 at combat start | 1.8% |
| Defect | Polarized *(curse)* | If you end your turn with your orb slots all empty or all full, lose 1 Energy next turn | 1.8% |
| Defect | Preheated | Channel a random orb at combat start | 1.8% |
| Defect | Sealed *(curse)* | Orb slots -1 at combat start (minimum 1) | 1.8% |
| Defect | Unstable *(curse)* | At turn end, 25% chance your oldest orb becomes a different random orb | 1.6% |
| Necrobinder | Necromantic | When you Summon, gain 3 Block | 2.1% |
| Necrobinder | Dooming | When you apply Doom to an enemy, apply 1 more | 1.8% |
| Necrobinder | Bonebound | Summon 1 each turn (grows your Osty) | 1.5% |
| Necrobinder | Sacrificial *(curse)* | When your summon takes damage, apply Weak / Frail / Vulnerable 1 to yourself (once per turn); 2 when it dies | 1.8% |
| Necrobinder | Doombound *(curse)* | Each turn, apply 1 Doom to yourself | 1.8% |
| Necrobinder | Vengeful | At turn start, Summon 2× the HP you lost since your last turn | 1.5% |
| Necrobinder | Empathic | At turn start, gain Block equal to the HP your summon lost since your last turn | 1.5% |
| Necrobinder | Famished *(curse)* | End your turn without Summoning and your Osty shrinks by 1 next turn | 1.8% |
| Regent | Levied *(curse)* | Cards that cost Stars cost 1 more Star | 1.8% |
| Regent | Starlit | Gain 1 Star each turn | 1.8% |
| Regent | Reforging | When you Forge, gain 1 Star | 1.5% |
| Regent | Regal | When you spend Stars, 50% chance to refund 1 Star | 1.5% |
| Regent | Bankrupt *(curse)* | Every 4 Stars you spend, a random card in your hand becomes Ethereal | 1.8% |
| Regent | Tributary | When you Forge, 50% chance to draw 1 card, 25% chance to draw 2 | 1.5% |
| Regent | Bountiful | When you gain Stars, 33% chance to gain 1 more | 1.8% |
| Regent | Prodigal *(curse)* | When you gain Stars, 25% chance to gain 1 less | 1.8% |
| Regent | Tarnished *(curse)* | Lose 1 Star at the end of each turn | 1.8% |

> Character-prefix Share is computed within that character's pool (universal pool + that character's five).

---

## Notes

- **Not a balance mod** — a power-fantasy add-on.
- **Survives save/load** (re-derived from the seed).
- **Numeric prefixes work on modded relics** automatically.
- **Disable** via the ModConfig "No-prefix chance" slider (100%).

---

*[Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3755793010) · Built on Slay the Spire 2 by MegaCrit · MIT*

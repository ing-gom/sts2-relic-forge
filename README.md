# StS2 Relic Forge

A **Slay the Spire 2** mod that gives the relics you find a **Terraria-style prefix** — your `Anchor` becomes a **Legendary Anchor** with more Block, or an **Anchored** one that also grafts another relic's effect. Then **reforge** them at campfires or merchants to chase the roll you want. A little power fantasy for your run.

[한국어 README](README.ko.md)

![Relic Forge tooltip](thumbnail.png)

---

## What it does

- **Prefixes on the relics you find** — most relics from shops, rewards, treasure and events roll a prefix. Seed-locked, so the same run seed always forges a relic the same way, and it survives save/load.
- **Four kinds of prefix** (see the [Prefix Guide](PREFIXES.md) for the full list):
  - **Numeric** — scale a relic's numbers up (or, rarely, down). Legendary Anchor = more Block.
  - **Companion** — graft a *weaker* version of another relic's whole effect onto yours. "Thorned" gives Thorns at combat start; works on any relic.
  - **Penalty** — a small curse (a minority of rolls, Terraria-style): a self-debuff or a status card shoved into your deck.
  - **Gamble** — mixed effects that also reach the battlefield: sap an enemy's stats each turn, or hit one enemy / one player with Vulnerable or Weak.
- **Reforge at campfires & shops** — re-roll a relic's prefix at a rest site (free, but a penalty prefix ends it) or at a merchant (pay gold, unlimited). See [Reforge](#reforge) below.
- **Tier-colored names** — a forged relic's name is tinted by its prefix (Legendary gold, Broken red …), like loot-rarity colors.
- **Configurable** — a ModConfig slider sets how often a relic gets NO prefix (default 60% vanilla, so ~40% are forged).

## The prefixes

A single weighted pool — any relic can roll any prefix. Full names, effects and drop rates are in the **[Prefix Guide](PREFIXES.md)** ([한국어](PREFIXES.ko.md) · [简体中文](PREFIXES.zh.md)) and the interactive [`prefix_dashboard.html`](prefix_dashboard.html).

- **Numeric** — Legendary (+60%) … Keen (+4%), the amplify prefix **Volatile**, and softened negatives (Damaged / Shoddy / Broken).
- **Companion** — themed prefixes that graft a reduced version of a donor relic's effect (Thorned, Mighty, Anchored, Vital, and more).
- **Penalty** — Cursed / Cumbersome / Fickle / Overloaded (self-debuffs) and Tainted / Festering / Smoldering / Hollow (curse-card generators).
- **Gamble** — mixed effects that also touch the battlefield: Eroding (saps an enemy's stats each turn), Exposing / Enervating (Vulnerable / Weak to one enemy + one player at combat start), and Chaotic (each turn, a coin-flip debuff on one enemy or one player).

**More prefixes are planned.**

## Curses (enemy forge) — opt-in

A forged relic can also roll a **curse** (default ~33% chance) — while you own it, **elites and bosses grow stronger** (the relic name gains a `(Cursed)` tag; the tooltip states the exact effect). A curse on a stronger relic empowers the enemy more, and multiple curses stack onto one enemy. Full list in the **[Curse Guide](CURSES.en.md)** ([한국어](CURSES.ko.md) · [简体中文](CURSES.zh.md)).

- Example curses: of Wrath (Strength), of Malice (Plated Armor), of Spite (Thorns), of the Tyrant (all three), of Bloodlust (Regen), of Warding, of Shielding, of Frenzy (Strength every 3rd turn).
- **ON by default** — controlled by **"Enemy forge"** in ModConfig. Turn it off for a pure power fantasy.
- Strength and curse chance are tunable via ModConfig sliders.

## Reforge

Re-roll a relic's prefix in two places:

### At campfires (free)

- Pick one of your relics and re-roll its prefix — even a relic that rolled "no prefix" or was never eligible on pickup will land one.
- It's **free and repeatable** (doesn't consume your rest), and stays available even after you Heal or Smith.
- The catch: if a reforge rolls a **penalty** prefix, an ill aura settles on the relic and the Reforge option ends for that campfire — so pushing your luck has a cost.

### At merchants (for gold)

- The shop offers a **Reforge** service next to the relics: pay a **fixed gold cost** (default 50, adjustable in ModConfig) to re-roll a relic's prefix, as often as you can afford.
- A **paid gamble** — a penalty prefix can still roll, but you can just pay to re-roll it away.
- Backing out of the relic pick is free.

## How it works

Every relic reads its effect magnitude from the same `DynamicVars` that also feed its tooltip, so scaling the base value updates both the effect and the displayed number in one place — no per-relic code, and modded relics with numeric values are supported automatically. Companion prefixes grant a hidden donor-relic instance whose native hooks fire; penalty and gamble effects apply via combat hooks.

## Notes

- **Not a balance mod** — it makes runs stronger (with a dash of risk); it's a power-fantasy / casual mod.
- **Disable prefixes** — set the ModConfig "No-prefix chance" slider to 100% for pure vanilla relics.
- Languages: English, 한국어, 简体中文.

## Installation

1. Subscribe on the Steam Workshop, or download the latest release.
2. If installing manually, place the `Sts2RelicForge/` folder into `<Slay the Spire 2 install>/mods/` (with `Sts2RelicForge.dll`, `.json`, and `.pck`).
3. Launch the game.

## Building from source

Requirements: .NET SDK, Godot.NET.Sdk 4.5.1 (auto-resolved), a local Slay the Spire 2 install, and the sibling `Sts2.ModKit` project alongside this one.

```sh
dotnet build Sts2RelicForge.csproj -c Debug
```

`prefix_dashboard.html` is a standalone reference that lists every relic × prefix result.

## License

MIT — see [LICENSE](LICENSE). Built on Slay the Spire 2 by MegaCrit.

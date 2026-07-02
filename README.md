# StS2 Relic Forge

A **Slay the Spire 2** mod that gives the relics you find a **Terraria-style prefix** — your `Anchor` becomes a **Legendary Anchor** with more Block, or an **Anchored** one that also grafts another relic's effect. Then **reforge** them at campfires to chase the roll you want. A little power fantasy for your run.

[한국어 README](README.ko.md)

![Relic Forge tooltip](thumbnail.png)

---

## What it does

- **Prefixes on the relics you find** — most relics from shops, rewards, treasure and events roll a prefix. Seed-locked, so the same run seed always forges a relic the same way, and it survives save/load.
- **Three kinds of prefix** (see the [Prefix Guide](PREFIXES.md) for the full list):
  - **Numeric** — scale a relic's numbers up (or, rarely, down). Legendary Anchor = more Block.
  - **Companion** — graft a *weaker* version of another relic's whole effect onto yours. "Thorned" gives Thorns at combat start; works on any relic.
  - **Penalty** — a small curse (a minority of rolls, Terraria-style): a self-debuff or a status card shoved into your deck.
- **Reforge at campfires** — a new **Reforge** rest-site option lets you re-roll a relic's prefix. It's free and repeatable and doesn't use up your rest… but if an *ill aura* (a penalty prefix) settles on a relic, reforging ends for that campfire.
- **Tier-colored names** — a forged relic's name is tinted by its prefix (Legendary gold, Broken red …), like loot-rarity colors.
- **Configurable** — a ModConfig slider sets how often a relic gets NO prefix (default 60% vanilla, so ~40% are forged).

## The prefixes

A single weighted pool — any relic can roll any prefix. Full names, effects and drop rates are in the **[Prefix Guide](PREFIXES.md)** ([한국어](PREFIXES.ko.md) · [简体中文](PREFIXES.zh.md)) and the interactive [`prefix_dashboard.html`](prefix_dashboard.html).

- **Numeric** — Legendary (+60%) … Keen (+4%), the amplify prefix **Volatile**, and softened negatives (Damaged / Shoddy / Broken).
- **Companion** — themed prefixes that graft a reduced version of a donor relic's effect (Thorned, Mighty, Anchored, Vital, and more).
- **Penalty** — Cursed / Cumbersome / Fickle / Overloaded (self-debuffs) and Tainted / Festering / Smoldering / Hollow (curse-card generators).

**More prefixes are planned.**

## Reforge

At any campfire you'll find a **Reforge** option:

- Pick one of your relics and re-roll its prefix — even a relic that rolled "no prefix" or was never eligible on pickup will land one.
- It's **free and repeatable** (doesn't consume your rest), and stays available even after you Heal or Smith.
- Each reforge is **deterministic** from the run seed + reforge count and is **persisted**, so a reload can't save-scum it.
- The catch: if a reforge rolls a **penalty** prefix, an ill aura settles on the relic and the Reforge option ends for that campfire — so pushing your luck has a cost.

## How it works

Every relic reads its effect magnitude from the same `DynamicVars` that also feed its tooltip, so scaling the base value updates both the effect and the displayed number in one place — no per-relic code, and modded relics with numeric values are supported automatically. Companion prefixes grant a hidden donor-relic instance whose native hooks fire; penalties apply via combat hooks.

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

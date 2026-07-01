# StS2 Relic Forge

A **Slay the Spire 2** mod that gives relics from shops and rewards a **Terraria-style prefix** that scales their numbers — your `Anchor` becomes a **Legendary Anchor** with more Block. A little power fantasy for your run.

[한국어 README](README.ko.md)

![Relic Forge tooltip](thumbnail.png)

---

## What it does

- **Prefixes scale relic values** — a rolled prefix raises (or lowers) the relic's numeric effect. The tooltip shows the change in color: green up, red down.
- **Rarity-graded & seed-locked** — the grade is deterministic from your run seed, so the same seed gives the same forge. Survives save/load.
- **Tier-colored names** — a forged relic's whole name is tinted by its prefix tier (Legendary, Godly, Demonic … Broken), like loot-rarity colors.
- **Smart exclusions** — starter relics and relics with no numeric value are skipped automatically.
- **Configurable** — a ModConfig slider sets how often a relic gets NO prefix (default 60% vanilla, so ~40% are forged).

## The prefixes

A single Terraria-style pool — any relic can roll any prefix:

| Tier | Prefixes | Effect |
|---|---|---|
| Strong | Legendary, Godly, Demonic, Superior, Forceful, Hurtful, Zealous, Keen | +60% … +4% |
| Volatile | Volatile | raises everything — boons *and* downsides (high risk / high reward) |
| Weak | Damaged, Shoddy, Broken | −12% … −25% (softened, ~26% of prefixed rolls) |

Grades are seed-locked, so the same run seed always forges a relic the same way. **More prefixes are planned.**

## How it works

Every relic reads its effect magnitude from the same `DynamicVars` that also feed its tooltip, so scaling the base value updates both the effect and the displayed number in one place — no per-relic code, and modded relics with numeric values are supported automatically. A couple of hardcoded reward relics (Lost Coffer, Neow's Talisman) get bespoke handling.

## Notes

- **Not a balance mod** — this makes relics stronger; it's a power-fantasy / casual mod.
- **Prototype** — feedback and reports welcome.
- **Disable** — set the ModConfig "No-prefix chance" slider to 100% for pure vanilla relics.
- Languages: English, 한국어, 简体中文.

## Installation

1. Download the latest release zip (or subscribe on the Steam Workshop).
2. Extract the `Sts2RelicForge/` folder into `<Slay the Spire 2 install>/mods/` so you end up with:
   ```
   <Slay the Spire 2>/mods/Sts2RelicForge/Sts2RelicForge.dll
   <Slay the Spire 2>/mods/Sts2RelicForge/Sts2RelicForge.json
   ```
3. Launch the game.

## Building from source

Requirements: .NET SDK, Godot.NET.Sdk 4.5.1 (auto-resolved), a local Slay the Spire 2 install, and the sibling `Sts2.ModKit` project alongside this one.

```sh
dotnet build Sts2RelicForge.csproj -c Debug
```

The build copies `Sts2RelicForge.dll` and `Sts2RelicForge.json` into `<sts2>/mods/Sts2RelicForge/`.

`prefix_dashboard.html` is a standalone reference that lists every relic × prefix result.

## License

MIT — see [LICENSE](LICENSE). Built on Slay the Spire 2 by MegaCrit.

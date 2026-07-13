# Relic Forge — Modding API

Sibling mods can integrate with **Relic Forge** through a small public surface:
`Sts2RelicForge.RelicForgeApi`. Two things you can do:

- **Read / inherit a relic's forge state** — carry a relic's enchantment onto another relic
  (this is how *Relic Transmute* keeps a transmuted relic's prefix, curse, reforge count and
  curse‑gauge).
- **Extend the forge** — register your own **data‑driven prefixes** and **self‑curses** so they roll
  on pickup and reforge like the built‑in ones (with your own name, %, weight, colour and localization).

> **Nothing here changes for players who don't have Relic Forge installed.** Your mod stays a clean
> no‑op when Relic Forge is absent — see *Access* below.

---

## Access

`RelicForgeApi` and its DTOs (`ForgePrefixDef`, `ForgeSelfCurseDef`) are **public**, but Relic Forge
ships as a standalone mod DLL that you normally do **not** build against. Two options:

### A. Reflection (recommended — optional dependency, degrades to no‑op)

Resolve the type from the loaded assembly and cache the `MethodInfo`s once. If Relic Forge isn't
installed, everything you call simply does nothing.

```csharp
// find the loaded Relic Forge assembly (null if not installed)
Type api = AppDomain.CurrentDomain.GetAssemblies()
    .Select(a => a.GetType("Sts2RelicForge.RelicForgeApi"))
    .FirstOrDefault(t => t != null);

if (api != null)
{
    var register = api.GetMethod("RegisterPrefix");           // bool RegisterPrefix(ForgePrefixDef)
    // build the DTO reflectively too (it's a public type on that same assembly):
    Type defT = api.Assembly.GetType("Sts2RelicForge.ForgePrefixDef");
    object def = Activator.CreateInstance(defT);
    defT.GetField("Name").SetValue(def, "Blazing");
    defT.GetField("PowerPct").SetValue(def, 0.25);
    defT.GetField("Weight").SetValue(def, 5.0);
    defT.GetField("Ko").SetValue(def, "작열의");
    register.Invoke(null, new[] { def });
}
```

### B. Direct reference (hard dependency)

If your mod *requires* Relic Forge, add an assembly reference to `Sts2RelicForge.dll` and call the
API directly:

```csharp
RelicForgeApi.RegisterPrefix(new ForgePrefixDef { Name = "Blazing", PowerPct = 0.25, Weight = 5, Ko = "작열의" });
```

> **Contract stability:** the method **names + signatures** in `RelicForgeApi` are stable across
> releases. Reflection binds by name, so a mismatch fails silently — pin to these names.

---

## Layer A — read / inherit forge state

| Method | Returns | Meaning |
|---|---|---|
| `GetDescriptor(RelicModel)` | `string?` | Compact forge descriptor `"prefix\|rider\|self\|fbStat\|fbAmt\|fbPct"`, or `null` if unforged. |
| `GetReforgeCount(RelicModel)` | `int` | Times re‑forged (0 = original grade). |
| `IsCleansed(RelicModel)` | `bool` | Whether the curse was cleansed. |
| `GetGaugeReduction(RelicModel)` | `int` | Cumulative curse‑gauge cleanse reduction (0 if never cleansed). |
| `IsEffectDisabled(RelicModel)` | `bool` | Relic's effect currently off (curse‑gauge saturated, etc.) — exclude it from your own actions. |
| `SetPendingForge(RelicModel, string? descriptor, int reforgeCount, bool cleansed, int gaugeReduction)` | `void` | Stash a forge state to be **restored verbatim the next time the relic is obtained** (`RelicCmd.Obtain`). No‑op if the relic is never re‑obtained. |

**Enchantment inheritance** (the Relic Transmute pattern): read the four values off the *source* relic,
then set them on the *replacement* relic **before** you `RelicCmd.Obtain` it:

```csharp
string desc = GetDescriptor(source);
if (desc != null)
    SetPendingForge(replacement, desc, GetReforgeCount(source), IsCleansed(source), GetGaugeReduction(source));
await RelicCmd.Obtain(replacement, player);   // Relic Forge restores the inherited enchantment here
```

---

## Layer B — extend the forge (data‑driven)

### `bool RegisterPrefix(ForgePrefixDef def)`

Adds a numeric prefix to the pickup/reforge roll pool. Returns `false` (logged) on an empty or
duplicate name.

`ForgePrefixDef` fields:

| Field | Default | Meaning |
|---|---|---|
| `Name` | `""` | English name / **unique stable key**. |
| `Ko`, `Zh` | `""` | Optional localized display names. |
| `PowerPct` | `0` | `0.30` = +30% stronger, `-0.10` = 10% weaker. Scales the relic's numeric vars. |
| `Weight` | `5` | Relative roll weight (higher = more common). Coerced to 1 if ≤ 0. |
| `Color` | `#e0b64d` | Tier tint (BBCode hex) for the tooltip header. |
| `Amplify` | `false` | If true, raises **every** var's magnitude regardless of benefit direction (high‑variance). |

### `bool RegisterSelfCurse(ForgeSelfCurseDef def)`

Adds a self‑curse that fires on the owner's **unblocked hits**. Set **exactly one** effect. Returns
`false` (logged) on empty/duplicate name or an unsupported effect.

`ForgeSelfCurseDef` fields:

| Field | Default | Meaning |
|---|---|---|
| `Name` | `""` | English name / **unique stable key**. |
| `Ko`, `Zh` | `""` | Optional localized display names. |
| `Color` | `#c0554d` | Tooltip tint (BBCode hex). |
| `OnHitPower` | `""` | Apply 1 of this power to the owner per unblocked hit: `"Weak"` / `"Frail"` / `"Vulnerable"`. |
| `OnHitCard` | `false` | Instead: add a **Dazed** to the owner's draw pile per unblocked hit. |
| `OnHitRandom` | `false` | Instead: a random one of Weak / Frail / Vulnerable per unblocked hit. |
| `EffEn`, `EffKo`, `EffZh` | `""` | Optional localized "on unblocked hit …" tooltip line. |

> Only these effect kinds are currently exposed. Behavioural prefixes (reactive / character‑gated /
> companion‑graft) are not registerable yet.

---

## ★ Co‑op contract (read this)

Relic Forge derivation is **seed‑deterministic over the prefix / curse pool**. So:

1. **Register at mod init**, before any run starts. (A registration during an active run still applies
   but is logged as unsafe — it changes the pool mid‑run.)
2. **Every co‑op peer must register the identical set of prefixes / curses, in the identical order.**
   This holds automatically when both players run the same extension mods with a load‑order syncer.
   A divergent pool (different mods, or a different registration order) desyncs the lockstep the same
   way mismatched settings would.
3. Names ride the save + the co‑op state sync as **strings**. If a peer/save is missing the extension
   mod, that prefix can't be resolved and the relic degrades gracefully (re‑rolls a valid prefix) — no
   crash, but the inheritance/enchantment for that entry is lost.

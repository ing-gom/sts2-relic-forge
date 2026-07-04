# Multiplayer Reforge — design & status

**Status: networked transport WIRED (v0.3.5); co-op smoke test pending.** Reforge (campfire + shop)
now rides the game's synchronized action queue and is offered in co-op. The dispatch reuses the
built-in console-command net action (`ConsoleCmdGameAction` / `NetConsoleCmdGameAction`) so the mod
adds no new `INetAction` subtype and never perturbs the net type-id ordering — see
`ReforgeNetConsoleCmd` and `ReforgeNet.DispatchNetworked`. This document is the design record; the
one remaining runtime gate is an actual two-client co-op verification (see §5).

**Lockstep requirement:** both connected clients must run this mod (same version). The net type-id
table is computed by sorting built-in + mod net types by short name; a peer without the mod, or a
different mod set, diverges. This is the normal co-op mod constraint.

## Why reforge is SP-only today

The two reforge UIs mutate relic state **locally** via `RelicForgeService.Reforge`:

- numeric prefixes change the relic's `DynamicVars` base values,
- companion prefixes add/remove a hidden donor **relic instance** on the player,
- the reforge count is stored on the relic's `SavedProperties` (`__rf_count`).

None of this rides a networked command, so only the acting client changes — the peer's replicated
copy of that relic drifts → **desync**. (`ReforgeKeyPacketGuardPatch` even has to strip `__rf_count`
from MP packets today, because the packet serializer throws on the unregistered property name.)

The vanilla card **upgrade** (smith) and card **removal** work in co-op precisely because they ride
the game's own **synchronized command** — the change replicates to every client. Reforge needs the
same treatment.

## Key insight: the forge is already deterministic

Passive prefixes (on pickup) are **already MP-safe** with no result transmission. `RelicCmd.Obtain`
is a networked command that runs on every client, and the forge Harmony prefix
(`RelicObtainPatch`) re-derives the prefix from `seed + id + floor` — identical on all clients.

Reforge is the same computation with one extra input, the **reforge count**. So the entire
networked payload needed is `(relic, newCount)`. Every client re-runs the deterministic
re-derivation and converges — no need to serialize the resulting stats/companions.

## The seam (already committed)

`ReforgeNet` centralizes the decision. Both reforge call sites and both availability gates route
through it:

| Concern | Old (SP-only) | Now (via seam) |
|---|---|---|
| Campfire option offered? | `IsSingleplayerOrFakeMultiplayer` | `ReforgeNet.Available()` |
| Shop button attached? | `IsSingleplayerOrFakeMultiplayer` | `ReforgeNet.Available()` |
| Campfire reforge | `RelicForgeService.Reforge` | `ReforgeNet.Reforge` |
| Shop reforge | `RelicForgeService.Reforge` | `ReforgeNet.Reforge` |

`ReforgeNet.TransportReady` is `false`, so behavior is **byte-identical to SP-only today**. Flipping
it on is gated behind the one unimplemented method.

## Remaining work (needs the game/ModKit command API)

### 1. Implement `ReforgeNet.DispatchNetworked(owner, relicEntry, targetCount)`

Enqueue a command into the game's synchronized command stream whose handler calls
`ReforgeNet.ApplyReforgeStepOnClient(owner, relicEntry, targetCount)` **on every client** (including
the initiator). `ApplyReforgeStepOnClient` is already written and pure-deterministic.

Candidate transports — confirm which the ModKit actually exposes to mods:

1. **Networked console-style command.** `AbstractConsoleCmd` already has `IsNetworked`; a networked
   variant issued programmatically is the smallest surface and reuses an existing synced channel.
2. **Custom `Cmd` type** carrying `(playerId, relicEntry, targetCount)`, mirroring `CardCmd.Upgrade`.
3. **Networked relic-selection command** — the analogue of `CardSelectCmd.FromSimpleGrid` the mod
   already uses for card selection. This also moves the picker itself onto the synced path (see §3).

### 2. `PredictOutcome` — real outcome for the initiator UI

The campfire ends its free reforge on a penalty roll, so the initiator needs the outcome. It is a
pure function of `(seed, id, floor, newCount)`. Either factor the roll out of
`RelicForgeService.Forge` into a no-mutation predictor, or read the record back after the synced
handler has run on the initiator's client. Replace the placeholder in `ReforgeNet.PredictOutcome`.

### 3. Picker: local vs synced

Reforge is a per-player action on your **own** relics, so no shared/consensus UI is required — the
picker can stay local; only the acting player picks. The chosen relic id is then carried by the
command in §1. (Option 3 above folds the pick into the synced command if a networked relic-select
exists; either is fine.)

### 4. `__rf_count` replication

`ReforgeKeyPacketGuardPatch` strips `__rf_count` from MP packets. With every client stepping the
count locally (§1) that is fine for a **live** session. For late joiners / mid-run state sync,
either register the property key properly so it survives packet serialization, or have the command
carry the count so a joining client can catch up via `ApplyReforgeStepOnClient`.

### 5. Flip the switch

Set `ReforgeNet.TransportReady = true`, build, and **co-op test**: both clients see the same prefix,
same companion graft/un-graft, same stats, no desync, survives save/load and a mid-run join.

## Files

- `Sts2RelicForgeCode/ReforgeNet.cs` — the seam (handler + stubbed dispatch).
- `Sts2RelicForgeCode/RestSiteReforgeOptionPatch.cs`, `MerchantReforgeButton.cs` — availability gates.
- `Sts2RelicForgeCode/ReforgeRestSiteOption.cs`, `MerchantReforgeButton.cs` — reforge call sites.
- `Sts2RelicForgeCode/ReforgeKeyPacketGuardPatch.cs` — the `__rf_count` packet guard to revisit (§4).

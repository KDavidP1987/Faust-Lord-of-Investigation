# Faust, Lord of Investigation

![Faust, Lord of Investigation](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/docs/img/faust-cover.jpg)

A **server-side** BepInEx IL2CPP mod for [V Rising](https://playvrising.com/) dedicated servers.
Its single purpose is **investigation / information**: answering on-demand queries about players,
castles, plots, objects, and server activity — delivered as `.faust` chat commands and, primarily,
as structured data consumed by the **BloodCraftHub** client mod and rendered in its UI.

> This is the **GitHub / developer** page. The player-facing mod page (what ships to Thunderstore)
> lives at [`Faust/Faust/README.md`](Faust/Faust/README.md).

## ⚠ Status: pre-1.0 — early data release (0.7.0)

Faust is **brand-new**, but moving fast. Confirmed working on a live server: the investigation
queries — **castle/plot info, plot availability, player info, online positions**. Added since:
the `FaustStore` persistence layer (real playtime/frequency/peak-hour in `pinfo`, plus a `stats`
playtime leaderboard and concurrency series), and the **complete administrative control surface**
([`docs/features/ADMIN_CONTROL.md`](docs/features/ADMIN_CONTROL.md)) — item cost (consumed), flat
cooldowns and window-per-period time-locks, PvP/PvE gating, live `.faust admin block/schedule`
overrides, and **unlock criteria** (a feature opens only after defeating a configured V Blood /
Dracula, or an admin grant), and a **proximity requirement** (usable only within range of a
configured object). 0.6.0 added **`castleresources` (#6)** — summing an enemy castle's total
contents. The persistence, admin-control, and resource paths compile clean but are **pending a live
in-game pass**. Remaining: `AllBosses`/`AllQuests` unlock auto-detection, and `objectscan` (#5) —
which the design keeps client-side (BloodCraftHub reads nearby entities; server only if an admin
prices it). The investigation feature set is otherwise feature-complete.

**Bug reports & feedback:** the **[The Shadow Realm Discord](https://discord.gg/usC9QgBrXK)** is
the primary channel; written-up GitHub issues are welcome too.

## The idea

The V Rising **client is spatially culled** — it only receives entities near the local player. The
**server** holds the authoritative, global, persistent world. Faust gathers that global view,
**gates** it (per feature, server-enforced), optionally **charges an item cost** for it (the
Faustian toll), and ships it to BloodCraftHub — exactly the integration pattern the author's
sibling server-side mods **Uriel** and **Beelzebub** use. Faust is **not** a dependency of any of
them; each is independent, and BloodCraftHub is an optional companion, never required.

## Features (see [`docs/FAUST_DESIGN.md`](docs/FAUST_DESIGN.md))

| # | Feature | Default access | Status |
|---|---------|----------------|--------|
| 7 | **Permission/cost/control gate** (`FaustAccessGate`) | — | ✅ all 7 admin-control axes — see [`docs/features/ADMIN_CONTROL.md`](docs/features/ADMIN_CONTROL.md) |
| 2 | Castle/plot info (owner, region, size, decay state & time, last-online) | Players | ✅ `castleinfo` |
| 4 | Plot availability (open plots by size) | Players | ✅ `plots` |
| — | Full server castle map (every territory, claimed + open) | AdminOnly | ✅ `castles` (`allcastles` — Raphael "All Plots") |
| 3 | Player info (online, last-online, playtime, frequency, peak hour) | AdminOnly (others) | ✅ `pinfo` (FaustStore persistence) |
| 1 | Online player positions (with region) | AdminOnly | ✅ `positions` (map rendering is BCH-side) |
| 6 | Enemy castle resource totals | AdminOnly | ✅ `resources` |
| 8 | Server stats (playtime leaderboard, concurrency series) | Players | ✅ `stats` (`kills`/`resources` leaderboards TBD) |
| 5 | Nearby object scan | Players (Free) | client-side by design — server only if priced |
| 9 | Visual graphs | — | rendered client-side in BloodCraftHub |

Pending: `AllBosses`/`AllQuests` unlock auto-detection (other unlock criteria live); live in-game
validation of the 0.3–0.7 paths.

## Architecture

- **Server-only** BepInEx IL2CPP plugin (`net6.0`, `kdpen.Faust`); `Plugin.Load` early-returns on
  anything that isn't `VRisingServer`.
- Every query enters through **one gatekeeper** (`FaustAccessGate`), evaluated in order: master-
  enabled → runtime block → schedule → feature-enabled → unlock criterion → access (Off / AdminOnly
  / Players) → PvP availability → proximity-to-object → usage (cooldown / window / period) → item
  cost. Cost/usage are reserved up front and committed only after a real result, so an empty query
  is never charged.
- Deferred initialization (`Core.TryInitialize`) — no game-type statics before the server world +
  prefab data exist.
- BCH-facing replies use the `[FAUST:*]` wire (one `ctx.Reply` per line). `.faust api version`
  advertises each feature's access + cost so BCH gates its UI without a round-trip.
- Commands via [VampireCommandFramework](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/).

## Project layout

```
Faust/
├── Faust.sln
└── Faust/                      ← C# project root
    ├── Faust.csproj            ← single version source (auto-generates MyPluginInfo)
    ├── Plugin.cs               ← entry point (BasePlugin.Load; server-only guard)
    ├── Core.cs                 ← deferred init hub (world/system handles; IsReady gate)
    ├── Patches/                ← Harmony patches (GameDataInitializedPatch triggers Core init)
    ├── Services/               ← FaustAccessGate (the gatekeeper); feature services land here
    ├── Commands/               ← VCF commands (.faust …); ApiCommands = the [FAUST:*] handshake
    ├── Config/Settings.cs      ← per-feature config schema (Access/Delivery/Cost/Cooldown)
    ├── README.md               ← THUNDERSTORE mod page (player-facing)
    ├── CHANGELOG.md            ← THUNDERSTORE changelog (concise, player-facing)
    └── thunderstore.toml       ← Thunderstore manifest (versionNumber synced to csproj)
docs/
├── FAUST_DESIGN.md             ← vision, feature evaluation, permission/cost layer, build order
├── BCH_INTEGRATION_CONTRACT.md ← the BloodCraftHub living contract (commands, wire, handshake)
├── PREFLIGHT.md                ← session-start checklist
└── DEV_REMINDERS.md            ← standing IL2CPP/ECS + process reminders
tools/
└── preflight.ps1              ← automated release-surface sync check (run before any release)
```

## Building

```powershell
cd Faust
dotnet restore Faust.sln
dotnet build Faust.sln -c Release
```

Game/IL2CPP types come from NuGet (`VampireReferenceAssemblies` + `BepInEx.Unity.IL2CPP`) — no
local interop folder needed. The build auto-deploys `Faust.dll` to a local V Rising dedicated
server at the default Steam path if present (override with `-p:VRisingServerPath=…`; point it at a
non-existent path to skip deployment). **Stop the server before redeploying** — it file-locks the
DLL.

## Release & changelog discipline

Faust keeps **separate GitHub and Thunderstore documents**; a version bump moves six surfaces
together in one `chore(release): vX.Y.Z` commit (csproj version, `thunderstore.toml` version, root
+ package CHANGELOG, root + package README). Run [`tools/preflight.ps1`](tools/preflight.ps1)
before any release commit — it verifies version parity and changelog entries. See
[`CLAUDE.md`](CLAUDE.md) → "Release & changelog discipline".

## Repository docs

- [`CLAUDE.md`](CLAUDE.md) — working agreements & architecture guide
- [`docs/FAUST_DESIGN.md`](docs/FAUST_DESIGN.md) — vision, feature evaluation, build order
- [`docs/BCH_INTEGRATION_CONTRACT.md`](docs/BCH_INTEGRATION_CONTRACT.md) — the BloodCraftHub living contract
- [`docs/features/ADMIN_CONTROL.md`](docs/features/ADMIN_CONTROL.md) — the admin control-surface spec (gating, cost, time-locks, unlocks, runtime control)
- [`docs/PREFLIGHT.md`](docs/PREFLIGHT.md) — session-start checklist
- [`docs/DEV_REMINDERS.md`](docs/DEV_REMINDERS.md) — IL2CPP/ECS gotchas & process rules
- [`CHANGELOG.md`](CHANGELOG.md) — full changelog (the Thunderstore package carries a condensed one)

## License

Licensed under the **GNU Affero General Public License v3.0 ([AGPL-3.0](LICENSE))** — copyright ©
2026 Kristopher Penland. Faust adapts server-side modding techniques from odjit's AGPL-licensed
[KindredCommands](https://github.com/Odjit/KindredCommands); in keeping with its copyleft, Faust is
released under the same license. The complete corresponding source is available in this repository.

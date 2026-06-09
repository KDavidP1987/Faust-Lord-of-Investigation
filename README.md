# Faust, Lord of Investigation

![Faust, Lord of Investigation](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/docs/img/faust-cover.jpg)

A **server-side** BepInEx IL2CPP mod for [V Rising](https://playvrising.com/) dedicated servers.
Its single purpose is **investigation / information**: answering on-demand queries about players,
castles, plots, objects, and server activity — delivered as `.faust` chat commands and, primarily,
as structured data consumed by the **BloodCraftHub** client mod and rendered in its UI.

> This is the **GitHub / developer** page. The player-facing mod page (what ships to Thunderstore)
> lives at [`Faust/Faust/README.md`](Faust/Faust/README.md).

## ⚠ Status: pre-1.0 — early data release (0.5.0)

Faust is **brand-new**, but moving fast. Confirmed working on a live server: the investigation
queries — **castle/plot info, plot availability, player info, online positions**. Added since:
the `FaustStore` persistence layer (real playtime/frequency/peak-hour in `pinfo`, plus a `stats`
playtime leaderboard and concurrency series), and the **complete administrative control surface**
([`docs/features/ADMIN_CONTROL.md`](docs/features/ADMIN_CONTROL.md)) — item cost (consumed), flat
cooldowns and window-per-period time-locks, PvP/PvE gating, live `.faust admin block/schedule`
overrides, and **unlock criteria** (a feature opens only after defeating a configured V Blood /
Dracula, or an admin grant). The persistence and admin-control paths compile clean but are
**pending a live in-game pass**. Remaining: `AllBosses`/`AllQuests` unlock auto-detection, and the
`objectscan` (#5) / `castleresources` (#6) queries.

**Bug reports & feedback:** the **[The Shadow Realm Discord](https://discord.gg/usC9QgBrXK)** is
the primary channel; written-up GitHub issues are welcome too.

## The idea

The V Rising **client is spatially culled** — it only receives entities near the local player. The
**server** holds the authoritative, global, persistent world. Faust gathers that global view,
**gates** it (per feature, server-enforced), optionally **charges an item cost** for it (the
Faustian toll), and ships it to BloodCraftHub — exactly the integration pattern the author's
sibling server-side mods **Uriel** and **Beelzebub** use. Faust is **not** a dependency of any of
them; each is independent, and BloodCraftHub is an optional companion, never required.

## Features (planned — see [`docs/FAUST_DESIGN.md`](docs/FAUST_DESIGN.md))

| # | Feature | Default access | Notes |
|---|---------|----------------|-------|
| 2 | Castle/plot info (owner, heart level, decay/raidable, last-online) | Players | reuses KindredCommands `CastleTerritoryService` |
| 4 | Plot availability (free plots by size) | Players | whole-map territory scan |
| 3 | Player info (last login, frequency, playtime, peak hours) | AdminOnly (others) | needs Faust persistence (net-new) |
| 1 | All-player map positions | AdminOnly | PvP-sensitive; rendering spike needed |
| 5 | Nearby object scan | Players (Free) | client-capable; server only if priced |
| 6 | Enemy castle resource totals | AdminOnly | PvP raid intel |
| 8 | Server stats (concurrency, leaderboards, time-series) | Players | needs Faust persistence |
| 7 | **Permission/cost gate** | — | **the foundation — shipped in 0.1.0** |
| 9 | Visual graphs | — | rendered client-side in BloodCraftHub |

## Architecture

- **Server-only** BepInEx IL2CPP plugin (`net6.0`, `kdpen.Faust`); `Plugin.Load` early-returns on
  anything that isn't `VRisingServer`.
- Every query enters through **one gatekeeper** (`FaustAccessGate`): feature-enabled → access
  level (Off / AdminOnly / Players) → cooldown → item cost. Cost is reserved up front and consumed
  only after a real result, so an empty query is never charged.
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

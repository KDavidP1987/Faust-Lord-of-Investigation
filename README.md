# Faust, Lord of Investigation

![Faust, Lord of Investigation](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/docs/img/faust-cover.jpg)

A **server-side** BepInEx IL2CPP mod that gives your [V Rising](https://playvrising.com/) dedicated
server its missing **information layer** — the authoritative, global view of players, castles, plots,
and activity that no game client can see on its own. Out of the box it's a powerful **moderation and
oversight** console for admins; from there, *you* decide how much of that knowledge to share — as
**PvP intel** (scout a rival's castle, resources, and activity windows — for a price), or as
**community features** on a PvE server (open plots, who's online, server stats, clan rosters). Every
capability is controlled per feature — Off, admin-only, or players — with an optional **item cost**
(the Faustian toll), cooldown, or unlock. Pairs with its companion client **Raphael, Lord of Wisdom**
(the `[FAUST:*]` wire is the BloodCraftHub-family integration contract Raphael implements), or works
from `.faust` chat. Pre-1.0 — published for testing.

> This is the **GitHub / developer** page. The player-facing mod page (what ships to Thunderstore)
> lives at [`Faust/Faust/README.md`](Faust/Faust/README.md).

## ⚠ Status: pre-1.0 — early data release (0.15.0)

Faust is **brand-new**, but moving fast. Confirmed working on a live server: the investigation
queries — **castle/plot info, plot availability, player info, online positions**. Added since:
the `FaustStore` persistence layer (real playtime/frequency/peak-hour in `pinfo`, plus a `stats`
playtime leaderboard and concurrency series); the **complete administrative control surface**
([`docs/features/ADMIN_CONTROL.md`](docs/features/ADMIN_CONTROL.md)) — item cost (consumed), flat
cooldowns and window-per-period time-locks, PvP/PvE gating, live `.faust admin block/schedule`
overrides, **unlock criteria** (a feature opens only after defeating a configured V Blood / Dracula,
or an admin grant), and a **proximity requirement** (usable only within range of a configured
object); the **full server castle map** (`castles`) + position **regions** (0.8.0); and a **decay
watch** (`decay`, claimed castles by soonest-to-decay) plus **passive-collection controls** (0.9.0 —
admins bound or switch off what Faust collects in the background, for performance); **activity
analytics** (0.10.0 — chart-ready `stats hours`/`daily`/`newplayers`/`sessions` over the session log);
**admin data management** (0.11.0 — `.faust admin data status/clear/wipe` + a `DataNamespace` for
per-world separation, since Faust's data is server-scoped and survives a world wipe by default); and
**weekday + per-player analytics**, **clan composition**, **population-health metrics**
(DAU/WAU/MAU + retention, recency, concurrency peak, per-region distribution), and **experimental
native-map player markers** (0.12.0 — `stats weekdays`/`pdaily`/`population`/`recency`/`peak`/`regions`,
`.faust api clans`, and a gated-off `.faust admin showpositions` using the game's own map-icon attach);
and (0.13.0) **all features now default to AdminOnly**, a per-player **query rate limit**
(`RateLimitSeconds`), a **layered-admin allowlist** for data resets (`[Faust.Data] ResetSteamIds`), and
the **player-activity roster** (`stats players`); (0.14.0) the **§8 tester batch** — richer
`castleinfo` (floors, owning clan, total item count), **prisoners** in `resources`, a `clanmembers`
roster endpoint, a **new-vs-returning** split on `stats daily`, and Faust **oversight** endpoints
(`access` / `usage`); and (0.15.0) the **§9 drill-down batch** — the detail behind the charts:
a **new-players roster** (`newplayers roster`), distinct-players-per-hour on `stats hours` (for
average-per-player), a **session timeline** (`sessions timeline`), and a per-player **active-days
grid** (`stats activegrid`); the **§10 region/roster batch** — a per-day per-region castle/plot/player
series (`stats regiondaily`), a castle **fill-%** denominator (`plots` on `stats regions`), and
playtime + castles on the new-players roster; and a **player-position heat map** (`heatmap`, ApiVersion 16)
— an opt-in timed sampler that bins online positions into a grid for **per-player and server-wide
density maps** (Raphael renders the heat map). 0.6.0 added
**`castleresources` (#6)**. The persistence, admin-control, and resource paths compile clean but are
**pending a live in-game pass** (the 0.14.0/0.15.0 tester-batch reads + the heat-map sampler especially). Remaining: `AllBosses`/`AllQuests` unlock auto-detection, and
`objectscan` (#5) — which the design keeps client-side (BloodCraftHub reads nearby entities; server
only if an admin prices it). See [`docs/FAUST_DESIGN.md`](docs/FAUST_DESIGN.md) §9 for the roadmap of
candidate features (clan info, server status, kills leaderboard, soul-shard tracker, …).

**Bug reports & feedback:** the **[The Shadow Realm Discord](https://discord.gg/usC9QgBrXK)** is
the primary channel; written-up GitHub issues are welcome too.

## The idea

The V Rising **client is spatially culled** — it only receives entities near the local player. The
**server** holds the authoritative, global, persistent world. Faust gathers that global view,
**gates** it (per feature, server-enforced), optionally **charges an item cost** for it (the
Faustian toll), and ships it to BloodCraftHub — exactly the integration pattern the author's
sibling server-side mods **Uriel** and **Beelzebub** use. Faust is **not** a dependency of any of
them; each is independent, and BloodCraftHub is an optional companion, never required.

## Philosophy: information under admin control

Faust is, first and foremost, an **administrative and moderation tool** — it gives the server team the
authoritative, global view of players, castles, plots and activity that the game itself never surfaces.
On top of that, admins can choose to **grant** parts of it to players: as a **strategic tool** on PvP
servers (intel that rewards engagement, optionally behind an item cost, cooldown, unlock, or location
requirement), and as a **community-building tool** on PvE servers (sharing useful, friendly information
that helps players coordinate and connect).

The defining principle is **admin control, feature by feature.** Nothing is exposed to players unless an
admin decides it should be. Sensitive intel (positions, enemy resources, other players' data) **defaults
to admin-only**; opening any of it to players is a deliberate, server-by-server choice.

Admins control Faust on **two independent axes**:

1. **Exposure** — per feature: who may read it (`Off` / `AdminOnly` / `Players`), at what cost, with
   what cooldown/window, behind what unlock or proximity requirement. (See
   [`docs/features/ADMIN_CONTROL.md`](docs/features/ADMIN_CONTROL.md).)
2. **Collection** — what Faust *passively gathers* in the background. Almost every query reads live
   state on demand (zero idle cost); the session/population time-series accumulates over time (event-
   driven, on connect/disconnect), and the **optional position heat map** (`[Faust.Heatmap]`, **off by
   default**) is the one *timer-driven* collector — it samples online positions every 30s–5min when an
   admin enables it. The `[Faust.Collection]`/`[Faust.Heatmap]` config lets admins bound each or switch
   it off entirely — so Faust never becomes a performance concern, regardless of how widely its data is
   exposed. (See [`docs/FAUST_DESIGN.md`](docs/FAUST_DESIGN.md) §10.)
3. **Data lifecycle** — Faust's stored data lives in `BepInEx/config/Faust/` (server-scoped, *not* in
   the world save), so it **persists across a world wipe** — intentional, since the same players return
   and their history stays relevant. Admins manage it explicitly with **`.faust admin data`**:
   `status` (footprint), `clear <days>` (prune old activity on demand), and
   `wipe <activity|unlocks|usage|heatmap|all> confirm` (reset a store — `unlocks` is the usual fresh-world
   reset; `heatmap` clears the position density grid). Set `DataNamespace` to keep each world's data fully separate instead. `SessionRetentionDays`
   auto-trims old activity on very busy / long-lived servers (default keeps everything).

## Features (see [`docs/FAUST_DESIGN.md`](docs/FAUST_DESIGN.md))

| # | Feature | Default access | Status |
|---|---------|----------------|--------|
| 7 | **Permission/cost/control gate** (`FaustAccessGate`) | — | ✅ all 7 admin-control axes — see [`docs/features/ADMIN_CONTROL.md`](docs/features/ADMIN_CONTROL.md) |
| 2 | Castle/plot info (owner, region, map location, size, decay state & time, last-online) | AdminOnly | ✅ `castleinfo` (+ `posx`/`posz` centroid on all castle/plot rows) |
| 4 | Plot availability (open plots by size) | AdminOnly | ✅ `plots` |
| — | Full server castle map (every territory, claimed + open) | AdminOnly | ✅ `castles` (`allcastles` — Raphael "All Plots") |
| — | Decay watch (claimed castles by soonest-to-decay) | AdminOnly | ✅ `decay` (`decaywatch`) |
| 3 | Player info (online, last-online, playtime, frequency, peak hour) | AdminOnly (others) | ✅ `pinfo` (FaustStore persistence) |
| 1 | Online player positions (with region) | AdminOnly | ✅ `positions` (map rendering is BCH-side) |
| 1 | Player-position heat map (per-player + server-wide density grid) | AdminOnly | ✅ `heatmap` (opt-in timed sampler → binned grid; Raphael renders) |
| 6 | Enemy castle resource totals | AdminOnly | ✅ `resources` |
| 8 | Server stats (playtime leaderboard, concurrency series) | AdminOnly | ✅ `stats` (`kills`/`resources` leaderboards TBD) |
| 8 | Activity analytics (hour-of-day, weekday, daily DAU/minutes, new-players, session-length, per-player daily) | AdminOnly | ✅ `stats hours\|weekdays\|daily\|newplayers\|sessions\|pdaily` (Raphael charts) |
| 8 | Population health (DAU/WAU/MAU + retention, recency, concurrency peak/avg, region distribution + buildable plots) | AdminOnly | ✅ `stats population\|recency\|peak\|regions` (+ `plots` fill-% denominator) + `pinfo daysidle` |
| 8 | Player-activity roster (per-player active-today/week, last-seen, sessions, playtime, idle) | AdminOnly | ✅ `stats players` (Raphael §7) |
| 8 | Activity drill-down (new-players roster, per-hour players, session timeline, active-days grid) | AdminOnly | ✅ `newplayers roster` / `stats hours` (`hoursplayers`) / `sessions timeline` / `stats activegrid` (Raphael §9) |
| 8 | Region time-series + roster extras (per-day per-region castles/plots/players; roster playtime + castles) | AdminOnly | ✅ `stats regiondaily` / `nprow` `playmins`+`castles` / `region` `plots` (Raphael §10) |
| — | Clan composition (clanned vs independent + per-clan roster, incl. castles held) | AdminOnly | ✅ `clans` (`ClanService`) |
| — | Native-map player markers (admin) | AdminOnly | 🚧 `showpositions` — experimental (real `AttachMapIconsToEntity` attach), off by default; admin-only visibility pending live tuning |
| 5 | Nearby object scan | AdminOnly (Free) | client-side by design — server only if priced |
| 9 | Visual graphs | — | rendered client-side in BloodCraftHub |

*"Default access" is the **recommended starting point** — every value is admin-configurable per
feature (`Off` / `AdminOnly` / `Players`), along with its cost, cooldown/window, unlock, and proximity
requirement. See the [Philosophy](#philosophy-information-under-admin-control) section above.*

Pending: `AllBosses`/`AllQuests` unlock auto-detection (other unlock criteria live); live in-game
validation of the 0.3–0.9 paths.

## Screenshots

*Every view below is rendered by the companion client **Raphael, Lord of Wisdom** from Faust's data.*

**Castle Info** — owner, region, map location, size, decay, floors, owning clan & total item count
![Castle Info](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/Screenshots/FAUST1-CastleInfo.png)

**Open Plots** — available building plots, largest first
![Open Plots](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/Screenshots/FAUST2-OpenPlots.png)

**All Plots** — the full server castle map (every territory, claimed + open)
![All Plots](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/Screenshots/FAUST3-AllPlotsInfo.png)

**Decay Watch** — claimed castles ranked by soonest-to-decay
![Decay Watch](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/Screenshots/FAUST4-DecayWatch.png)

**Castle Resources** — total resources stashed in a castle (+ prisoners)
![Castle Resources](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/Screenshots/FAUST5-CastleResources.png)

**Player Info** — online state, last-online, playtime, sessions, frequency, peak hour
![Player Info](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/Screenshots/FAUST6-PlayerInfo.png)

**Clans** — clanned vs independent, with per-clan rosters
![Clans](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/Screenshots/FAUST7-ClanInfo.png)

**Player Positions + Activity Heat Map** — live positions and the position-density heat map
![Player Positions and Heat Map](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/Screenshots/FAUST8-PlayerPositionsAndHeatMap.png)

**Nearby Objects** — in-world object labels
![Nearby Objects](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/Screenshots/FAUST9-NearbyObjects-InWorldLabels.png)
![Nearby Objects (labels in world)](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/Screenshots/FAUST9-NearbyObjects-InWorldLabels2.png)

**Server Stats** — new-player roster, new vs returning, day-of-week activity, session timelines, active-days grid
![New-player roster](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/Screenshots/FAUST10-ServerStats-NewPlayerInfo.png)
![New vs returning](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/Screenshots/FAUST10-ServerStats-NewVsReturningPlayers.png)
![Day-of-week activity](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/Screenshots/FAUST10-ServerStats-DayOfWeekActivity.png)
![Session timelines](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/Screenshots/FAUST10-ServerStats-SessionTimelines.png)
![Active-days grid](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/Screenshots/FAUST10-ServerStats-ActivityGrid.png)

**Admin — Faust usage & access oversight**
![Admin usage data](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/Screenshots/FAUST11-AdminFaustUsageData.png)

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

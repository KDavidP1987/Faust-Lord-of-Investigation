# Faust, Lord of Investigation

![Faust, Lord of Investigation](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/docs/img/faust-cover.jpg)

A **server-side** BepInEx IL2CPP mod that gives your [V Rising](https://playvrising.com/) dedicated
server the information layer the game never exposes: a global, authoritative view of players, castles,
plots, server entities (V Bloods, NPCs, resource nodes), and activity that no client can see on its own.
It's an admin moderation and oversight console first; from there you decide, feature by feature, how much
to share with players — as PvP intel (for a price) or as PvE community tools. Every capability is gated
per feature (Off / admin-only / players) with an optional item cost, cooldown, or unlock. Pairs with its
companion client **Raphael, Lord of Wisdom** (which implements the `[FAUST:*]` wire), or works from
`.faust` chat.

> This is the **GitHub / developer** page. The player-facing mod page (Thunderstore) lives at
> [`Faust/Faust/README.md`](Faust/Faust/README.md); the player-facing changelog is
> [`Faust/Faust/CHANGELOG.md`](Faust/Faust/CHANGELOG.md).

## Status: pre-1.0 (0.16.1) — in testing

The full investigation feature set and the per-feature admin-control surface are implemented; the wire
is at **ApiVersion 18**. Faust is in live testing — see the [changelog](CHANGELOG.md) for the per-version
history and the [feature table](#features-see-docsfaust_designmd) below for what's shipped vs pending.

The core queries (castle/plot info, plot availability, player info, positions) are confirmed on a live
server. The persistence, admin-control, boss, kill, and world-scan paths build clean and are being
validated in-game. Known remaining work: `AllBosses`/`AllQuests` unlock auto-detection, the deferred
item-collection leaderboard, and a roster for not-yet-defeated, not-spawned bosses. See
[`docs/FAUST_DESIGN.md`](docs/FAUST_DESIGN.md) §9 for the candidate-feature roadmap.

**Bug reports & feedback:** the [Shadow Realm Discord](https://discord.gg/usC9QgBrXK) is the primary
channel; GitHub issues welcome too.

## How it works

The V Rising client is spatially culled — it only receives entities near the local player. The server
holds the authoritative, global, persistent world. Faust gathers that view, gates it per feature
(server-enforced), optionally charges an item cost for it (the Faustian toll), and ships it to Raphael —
the same integration pattern the author's sibling mods Uriel and Beelzebub use. None of them depend on
each other; Raphael is an optional companion, never required.

Admins control Faust on two independent axes:

1. **Exposure** — per feature: who may read it (`Off` / `AdminOnly` / `Players`), at what cost, with what
   cooldown/window, behind what unlock or proximity requirement. Sensitive intel (positions, enemy
   resources, other players' data) defaults to admin-only. See
   [`docs/features/ADMIN_CONTROL.md`](docs/features/ADMIN_CONTROL.md).
2. **Collection** — what Faust gathers in the background. Almost every query reads live state on demand
   (zero idle cost); only the session/population history accumulates (event-driven), and the optional
   position heat map (`[Faust.Heatmap]`, off by default) is the one timer-driven collector. The
   `[Faust.Collection]` / `[Faust.Heatmap]` config bounds or disables each, so Faust never becomes a
   performance concern.

Stored data lives in `BepInEx/config/Faust/` (server-scoped, not in the world save), so it persists
across a world wipe — the same players return and their history stays relevant. Manage it with
`.faust admin data status` / `clear <days>` / `wipe <store> confirm`, set `DataNamespace` to separate
worlds, and `SessionRetentionDays` to auto-trim on long-lived servers.

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
| 8 | Server stats (playtime leaderboard, concurrency series) | AdminOnly | ✅ `stats` |
| — | **V Blood boss status board** (live position/region/health + up/down/defeated) | AdminOnly | ✅ `bosses` / `boss <name>` (`BossService`; static + roaming via `Translation`; not-spawned roster TBD) |
| 8 | **Kill leaderboards** (top killers +PvP; per-boss defeat counts) | AdminOnly | ✅ `kills` / `bosskills` (death-hook tally; `[Faust.Collection] KillTracking`) |
| 5 | **World-asset scan** (map of NPC units +blood, ores/trees/plants; filterable) | AdminOnly | ✅ `worldscan` (whitelisted; cached + rate-limited scan; `WorldScanService`) |
| 8 | Activity analytics (hour-of-day, weekday, daily DAU/minutes, new-players, session-length, per-player daily) | AdminOnly | ✅ `stats hours\|weekdays\|daily\|newplayers\|sessions\|pdaily` (Raphael charts) |
| 8 | Population health (DAU/WAU/MAU + retention, recency, concurrency peak/avg, region distribution + buildable plots) | AdminOnly | ✅ `stats population\|recency\|peak\|regions` (+ `plots` fill-% denominator) + `pinfo daysidle` |
| 8 | Player-activity roster (per-player active-today/week, last-seen, sessions, playtime, idle) | AdminOnly | ✅ `stats players` (Raphael §7) |
| 8 | Activity drill-down (new-players roster, per-hour players, session timeline, active-days grid) | AdminOnly | ✅ `newplayers roster` / `stats hours` (`hoursplayers`) / `sessions timeline` / `stats activegrid` (Raphael §9) |
| 8 | Region time-series + roster extras (per-day per-region castles/plots/players; roster playtime + castles) | AdminOnly | ✅ `stats regiondaily` / `nprow` `playmins`+`castles` / `region` `plots` (Raphael §10) |
| — | Clan composition (clanned vs independent + per-clan roster, incl. castles held) | AdminOnly | ✅ `clans` (`ClanService`) |
| — | Native-map player markers (admin) | AdminOnly | 🚧 `showpositions` — experimental (real `AttachMapIconsToEntity` attach), off by default; admin-only visibility pending live tuning |
| 5 | Nearby object scan | — | ❌ retired 0.16.0 (client scan removed; Raphael §14) |
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
- [`docs/DOC_STYLE.md`](docs/DOC_STYLE.md) — writing standard for the changelogs, READMEs & Thunderstore page (keep them clean)
- [`docs/PREFLIGHT.md`](docs/PREFLIGHT.md) — session-start checklist
- [`docs/DEV_REMINDERS.md`](docs/DEV_REMINDERS.md) — IL2CPP/ECS gotchas & process rules
- [`CHANGELOG.md`](CHANGELOG.md) — full changelog (the Thunderstore package carries a condensed one)

## License

Licensed under the **GNU Affero General Public License v3.0 ([AGPL-3.0](LICENSE))** — copyright ©
2026 Kristopher Penland. Faust adapts server-side modding techniques from odjit's AGPL-licensed
[KindredCommands](https://github.com/Odjit/KindredCommands); in keeping with its copyleft, Faust is
released under the same license. The complete corresponding source is available in this repository.

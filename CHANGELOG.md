# Changelog — Faust, Lord of Investigation (full)

This is the **complete** changelog (GitHub). The Thunderstore package carries a condensed,
player-facing changelog at `Faust/Faust/CHANGELOG.md`; every release updates **both** (see
CLAUDE.md → "Release & changelog discipline").

Format: [Keep a Changelog](https://keepachangelog.com/) flavored; versions follow the mod's own
incremental scheme (pre-1.0: minor = feature batch, patch = fixes).

## [0.11.0] - 2026-06-10

### Added — admin data management (`.faust admin data …`) + world-wipe story

Faust's collected data (the session log / playtime, concurrency, and the unlock-progress + usage state)
persists to `BepInEx/config/Faust/` — **server-scoped, and it survives a V Rising world wipe** (the same
folder isn't part of the world save). That's intentional: after a wipe the same players return, so their
activity history stays relevant. But admins now have explicit control to inspect, prune, separate, or
reset it. No wire change → **ApiVersion stays 10** (these are server-side `.faust admin` chat commands).

- **`.faust admin data status`** — footprint readout: session / concurrency / name counts, oldest record,
  on-disk size, the active data namespace, retention setting, collection switches, and unlock/usage counts.
- **`.faust admin data clear <days>`** — on-demand prune of activity (sessions + concurrency) older than N
  days, without changing the `SessionRetentionDays` config. Open sessions are never dropped.
- **`.faust admin data wipe <activity|unlocks|usage|all> confirm`** — erase a store. Requires a literal
  `confirm` token (a no-confirm call previews exactly what would be erased). `unlocks` is the typical
  fresh-world reset (clears V-blood-gated progression); `activity` resets playtime/charts; `all` does both
  plus usage locks. Wiping `activity` re-opens a live session for anyone currently online so tracking
  continues seamlessly.
- **New config `Faust.Collection.DataNamespace`** (default empty = one shared dataset). Set a per-world
  label (e.g. `season3`) ONLY if you want each world's data kept fully separate; changing it starts a
  fresh dataset and leaves the previous one on disk. All four stores now resolve their path through a
  single `FaustPaths.DataDir`.
- `SessionRetentionDays` documentation clarified (auto-prune for very busy / long-lived servers; default
  keeps everything; `.faust admin data clear` is the manual alternative).

## [0.10.0] - 2026-06-10

### Added — activity analytics for admin charts (#8)

Four new read-only `stats` sub-queries that turn Faust's session log into the time-resolved series an
admin "player activity" dashboard needs (requested by the Raphael client, `docs/FAUST_API_REQUESTS.md`
§4). All share the existing `stats` feature gate (Players-default; admins can lock it down) — no new
feature key. **ApiVersion → 10** (additive; an older BCH/Raphael simply hides any chart it can't read).

- **`.faust api stats hours [<name|steamId>]`** — accumulated playtime **minutes per UTC hour-of-day**
  (24 buckets), server-wide or for one player. Sessions are **sliced at hour boundaries**, so a session
  that straddles midnight feeds every hour it touches — a true activity profile, not a connect-time
  tally. One line: `[FAUST:hours] scope=<server|steamId> h00=<min> … h23=<min>`.
- **`.faust api stats daily [days=14]`** — per-day **DAU** + **play-minutes** for the last N days
  (clamped 1–90), oldest→newest. `[FAUST:daily] day=<unixUtcMidnight> dau=<int> minutes=<int>` rows
  followed by `[FAUST:end] cmd=daily count=<n>` (un-paged — the whole window ships at once).
- **`.faust api stats newplayers [days=30]`** — per-day **first-seen** counts (growth/retention).
  `[FAUST:newplayers] day=<unixUtcMidnight> new=<int>` rows + `[FAUST:end] cmd=newplayers count=<n>`.
- **`.faust api stats sessions [<name|steamId>]`** — **session-length distribution** in four buckets
  (`<15m` / `15–60m` / `1–3h` / `3h+`). One line:
  `[FAUST:sessions] scope=<server|steamId> lt15=<n> m15_60=<n> h1_3=<n> gt3h=<n>`.

The `stats` command's second positional arg is now parsed per-kind: a **page** (`playtime`/
`concurrency`), a **player scope** (`hours`/`sessions`), or a **day window** (`daily`/`newplayers`).
New aggregations live in `FaustStore` (`GetHourHistogram`, `GetDailySeries`, `GetNewPlayersSeries`,
`GetSessionLengthBuckets`); a new `Wire.SendList` emits an un-paged series + count trailer. Contract:
`docs/BCH_INTEGRATION_CONTRACT.md` §3 `stats`.

### Fixed — `positions` region now resolves for players off castle plots

`.faust api positions` was deriving each player's `region=` from the **castle territory** they stood
on, so anyone out in the open world (not on a buildable plot) came across with a blank region — only
players standing on a castle plot showed one. Region is now resolved from the player's **world
position** by point-in-polygon over the map's `WorldRegionPolygon` set (`CastleService.GetWorldRegionName`,
the KindredCommands `RegionService` model), with the old territory-region as a fallback. The wire
shape is unchanged (`region=` already existed), so **no ApiVersion bump** — existing BCH/Raphael
clients just start seeing a region for roaming players. A position outside every region polygon
(genuine void / out-of-bounds) still sends the `-` sentinel.

### Fixed — one canonical `region=` "no region" sentinel (`-`) across all features

Audit follow-up. `castleinfo` / `castles` / `decay` / `plots` previously emitted the literal `None`
(out-of-bounds territory whose `TerritoryWorldRegion` is `WorldRegionType.None`) or `Unknown` (missing
component), while `positions` used `-` — three different "no region" tokens. They now all funnel
through `Wire.Region(...)`, which emits **`-`** for any unmapped region and the wire-safe name
otherwise. Region resolution is normalized at the source (`CastleService.ResolveTerritoryRegion`):
`TerritoryWorldRegion` when it's a real region, else a point-in-polygon lookup at a sampled tile of
the plot (covers territories with an unset component), else null → `-`. The world-region bounds test
is now x/z-only (a plot's sampled `Y=0` no longer fails the region AABB). Wire shape unchanged → no
ApiVersion bump; the contract's §4 now documents `-` as the single unmapped sentinel.

### Changed — `stats concurrency` pages the full stored history

Was silently truncated to the most recent 200 samples on read; now exposes the **full stored series**
(bounded by `MaxConcurrencyPoints`, default 4000) and pages it like the other lists, so the population
graph isn't capped to a fixed recent window. Added contract caveats: all hour/day analytics are
**UTC**; `newplayers`/`firstseen` mean "first seen by Faust" (veterans look "new" right after install,
and the series is unreliable with `SessionRetentionDays` > 0); `peakhour` (by login count) and `stats
hours` (by playtime) measure different things.

## [0.9.0] - 2026-06-10

### Added — decay watch (#) + passive-collection controls

A new admin housekeeping query and a second admin-control axis (what Faust *collects*, not just who
*reads*). **ApiVersion → 9** (additive).

- **`.faust api decay [page]`** — claimed castles ordered **soonest-to-decay first**, with the owner's
  last-online — the abandoned-plot / cleanup view. New AdminOnly feature key **`decaywatch`**,
  advertised in the `[FAUST:version]` handshake and auto-enrolled in `.faust admin` grant/block/
  schedule. Reuses the `[FAUST:castle]` tag/field set (shared `CastleRow`) + a `[FAUST:end] cmd=decay`
  trailer; open plots excluded, sealed castles (`decay=-1`) sort last. On-demand — zero passive cost.
  - `CastleService.GetCastlesByDecay()` — claimed territories sorted ascending by fuel remaining.
- **`[Faust.Collection]` config block** — admins now control Faust's *passive* background collection,
  independent of feature access, so it never becomes a server-performance concern:
  - `SessionTracking` (default `true`) — master switch for connect/disconnect session logging. Off ⇒
    pinfo's playtime/sessions/frequency/peak-hour and the stats playtime leaderboard return the `-1`
    "not tracked" sentinel and nothing is written.
  - `ConcurrencySampling` (default `true`) — whether to sample the online count (population series).
  - `MaxConcurrencyPoints` (default `4000`, was a hardcoded const) — cap on retained samples; `0`
    disables sampling.
  - `SessionRetentionDays` (default `0` = forever) — prune sessions older than N days (on connect + at
    load) to bound long-term growth.
  - `FaustStore` reads these; the live online set is always maintained in-memory (drives concurrency),
    but **persistence** of sessions/concurrency now obeys the knobs.
- Docs: `FAUST_DESIGN.md` gains §9 (post-0.8 opportunity catalog — clan info, server status, kills
  leaderboard, soul-shard tracker, …) and §10 (the collection/performance-control axis + the design
  rule that every new passive collector ships with its own toggle). READMEs gain a **"information
  under admin control"** philosophy section. Contract + handshake updated (`decaywatch` token, `decay`
  section).

### Notes

- Compiles clean (0 warnings). Not yet validated on a live server — needs an in-game pass (`.faust api
  decay`; toggle `[Faust.Collection]` keys and confirm collection starts/stops and the sentinels).

## [0.8.0] - 2026-06-10

### Added — full server castle map + position regions (Raphael "All Plots")

Two BCH/Raphael-facing wire additions, one ApiVersion bump. **ApiVersion → 8** (additive; an older
client ignores the new field/endpoint).

- **`.faust api castles [page]`** — every territory, **claimed AND open**, largest first, paged
  (20/page). Reuses the exact `[FAUST:castle]` field set and tag as `castleinfo` (Raphael
  disambiguates a single lookup — one row, no trailer — from the list — N rows + `[FAUST:end]
  cmd=castles`). Powers Raphael's **All Plots** tab (the full-map owner/region/size/state/decay view).
  `plots` returns only *open* territories and `castleinfo` is one-at-a-time; this is the whole-map list.
  - New feature key **`allcastles`**, default **AdminOnly** — its own gateable/priceable unit,
    advertised in the `[FAUST:version]` handshake. Auto-enrolls in `.faust admin` grant/block/schedule.
  - `CastleService.GetAllTerritories()` enumerates all `CastleTerritory` entities via `BuildInfo`.
- **`region=` on `[FAUST:pos]`** — each online-player position row now carries the player's territory
  region (`Wire.Safe`; `-` in the open world). The client can't map a far-away player's
  territory→region itself (those entities aren't replicated to it), so the server supplies it; lights
  up Raphael's Player Positions **Region** column.
  - `CastleService` caches a territory-index→region map (built with the block map; region layout is
    static at runtime) and exposes `GetRegionForTerritory(int)`.
- Contract (`docs/BCH_INTEGRATION_CONTRACT.md`) updated: handshake `allcastles` token, `positions`
  `region=` field, and a new `allcastles`/`castles` section. Mirror: Raphael's `FAUST_API_REQUESTS.md`.

### Notes

- Compiles clean (0 warnings). Not yet validated on a live server — needs an in-game pass (`.faust api
  castles`, confirm claimed + open rows and the paging trailer; `.faust api positions`, confirm
  `region=` populates and reads `-` in the open world).

## [0.7.0] - 2026-06-09

### Added — proximity requirement (admin-control axis 7)

A feature can require the player to be **near a configured object** to use it. **ApiVersion → 7**;
new deny code `notnear` (no query reply-shape change).

- New per-feature config: `RequireNearPrefab` (an object's PrefabGUID; `0` = off) +
  `RequireNearDistance` (metres, default 5). The player may only run the query while within range
  of an instance of that object — e.g. an altar/station placed in a castle, or a world landmark —
  so an ability is tied to a place instead of usable anywhere.
- **`Proximity.PlayerNear`** — scans placed/world objects (those with a `TilePosition`) for one
  whose prefab matches and is within the radius (no value-indexed ECS query exists, hence a filtered
  scan; only runs when a feature configures a requirement, and these are on-demand queries).
- Gate denies `[FAUST:err] code=notnear feature=… item=<prefab> dist=<m>` when the player isn't
  near. Slots into the evaluation order after PvP, before usage/cost. Admins (`AdminsExempt`) bypass.
- Contract + `ADMIN_CONTROL.md` updated — all seven admin-control axes now implemented.

### Notes

- Compiles clean (0 warnings). Not yet validated on a live server — needs an in-game pass
  (configure `RequireNearPrefab` to a placed object, confirm the query works near it and denies away).

## [0.6.0] - 2026-06-09

### Added — castle resource intel (#6): sum an enemy castle's contents

The powerful PvP raid-intel query. **ApiVersion → 6.**

- **`.faust api resources <here|nearest|tindex> [page]`** — totals every container's contents in
  the castle on a territory (containers + stations connected to the heart). Emits a
  `[FAUST:res]` summary header (owner, steam, container count, total items, distinct types) then
  paged `[FAUST:item] guid= qty= name=` rows (quantity-descending).
- **`CastleService.TrySummarizeResources`** — resolves the territory's heart, enumerates entities
  via `CastleHeartConnection.CastleHeartEntity`, resolves each inventory
  (`InventoryUtilities.TryGetInventoryEntity`), and sums `InventoryBuffer` (`ItemType`/`Amount`).
  Unclaimed plot → `notfound`; a claimed-but-empty castle → header with zeros.
- Defaults to **AdminOnly** and is the natural feature to price (`CostItemGuid`) or PvP-gate
  (`Availability=PvPOnly`) via the admin-control axes. Routes through `FaustAccessGate` like every
  query (a resolved castle is a real result and commits cost even when empty).
- Contract updated (shape marked implemented). With this, the only remaining query is `objectscan`
  (#5), which the design keeps **client-side** (BCH reads nearby entities; server only if priced).

### Notes

- Compiles clean (0 warnings). The container scan + inventory sum are **not yet validated on a
  live server** — needs an in-game pass against a real castle. Note: it's a full heart-connected
  scan, so on very large servers it's an on-demand admin query, not a hot path.

## [0.5.0] - 2026-06-09

### Added — unlock criteria (the final admin-control axis)

A feature can require a **progression gate** before a player may use it (`ADMIN_CONTROL.md` axis 6).
**ApiVersion → 5**; new deny code `locked` (no query reply-shape change).

- **Per-feature `Unlock` config** — `None` (default) | `FinalBoss` (after defeating Dracula —
  game completion) | `BossKill:<vbloodGuid>` (after defeating that specific V Blood). `AllBosses`/
  `AllQuests` are reserved (parsed as grant-only) until a reliable full-set / achievement read.
- **`UnlockService`** (`feature_unlocks.json`) — tracks each player's V-Blood defeats and admin
  grants; evaluates the criterion. `FinalBoss` resolves Dracula by prefab name (no hardcoded GUID).
- **Death hook** (`Patches/DeathEventListenerSystemPatch.cs`) — postfix on
  `DeathEventListenerSystem`: when a player kills a `VBloodUnit`, records the defeat for that
  player. No-op unless some feature configures an `Unlock` (cheap gate); fully try/catch-guarded.
- **Gate** denies `[FAUST:err] code=locked feature=… need=<bosskill|finalboss|grant>` when the
  criterion isn't met. Admins (`AdminsExempt`) and self-queries bypass it.
- **Admin overrides** — `.faust admin grant <player> <feature>` / `revoke <player> <feature>` /
  `unlocks <player>` (show a player's V-blood defeats + granted features).
- Contract + `ADMIN_CONTROL.md` updated — all six control axes now implemented (AllBosses/AllQuests
  auto-detection is the only follow-up).

### Notes

- Compiles clean (0 warnings). The death hook + unlock evaluation are **not yet validated on a live
  server** — needs an in-game pass (configure an `Unlock`, defeat the boss, confirm the feature opens).

## [0.4.0] - 2026-06-09

### Added — admin control surface: cost consume, time-locks, PvP gating, runtime block/schedule

Completes five of the six `ADMIN_CONTROL.md` axes (all but unlock-criteria). **ApiVersion → 4**;
no query reply-shape changed — the controls surface to BCH as new `[FAUST:err]` deny codes
(`blocked`, `schedule`, `pvp`, `window`).

- **Item cost is now actually consumed** — `FaustAccessGate.Commit` draws
  `CostQuantity`×`CostItemGuid` from the requester's inventory after a real result
  (`InventoryUtilities.TryGetInventoryEntity` + `ServerGameManager.TryRemoveInventoryItem`; the
  proven Uriel/KindredLogistics pattern). Verified up front, never charged on an empty/notfound.
- **Usage rate/time-locks** (`UsageService`, `feature_usage.json`) — per (player, feature):
  - `CooldownSeconds` — flat lockout ("pay 100 of an item, then locked 30 min").
  - `WindowSeconds` + `PeriodSeconds` + `MaxUsesPerPeriod` — a burst **window** that opens on first
    use and locks for the rest of a recurring **period** ("free, a 10-minute window once per day" =
    `WindowSeconds=600`, `PeriodSeconds=86400`, `MaxUsesPerPeriod=1`). State persists across
    restarts, so a daily window doesn't reset on a reboot. Deny codes `cooldown` / `window` carry
    `secs` remaining.
- **PvP availability** (`Availability = Always | PvEOnly | PvPOnly`) — gates a feature on the
  server's `GameModeType` (e.g. enemy-resource intel `PvPOnly`). Deny code `pvp`.
- **Runtime operational control** (`FeatureControlService`, `feature_control.json`) — live admin
  overrides on top of the .cfg, persisted across restarts:
  - **`.faust admin block <feature|all> [minutes]`** — block now, optionally as a countdown.
  - **`.faust admin unblock <feature|all>`**.
  - **`.faust admin schedule <feature|all> <HH:MM-HH:MM|clear>`** — a server-local time-of-day window.
  - **`.faust admin status [feature]`** — effective control state per feature.
  Deny codes `blocked` (with `secs` left; `-1` = indefinite) and `schedule` (with `secs` to next open).
- **Gate evaluation order** (`ADMIN_CONTROL.md §4`): master-enabled → runtime block → schedule →
  feature-enabled → access → PvP → usage (cooldown/window/period) → cost. Admins with
  `AdminsExempt=true` skip access/PvP/usage/cost, but not a master-off, a deactivated feature, or
  an operational block/schedule.
- New per-feature config keys: `Availability`, `WindowSeconds`, `PeriodSeconds`, `MaxUsesPerPeriod`.
- Contract + `ADMIN_CONTROL.md` updated (axes 1–5 marked done; unlock-criteria #6 remains).

### Notes

- Compiles clean (0 warnings). The cost/usage/control paths are **not yet validated on a live
  server** — needs an in-game pass (charge an item, hit a cooldown/window, block + reopen).

## [0.3.0] - 2026-06-09

### Added — persistence (FaustStore): real player playtime/frequency + server stats

The session/time-series layer the game doesn't keep (it stores only the *last* connect time).
**ApiVersion → 3.**

- **`FaustStore`** (`Services/FaustStore.cs`) — logs every connect/disconnect to
  `BepInEx/config/Faust/sessions.json` (System.Text.Json), created at `Plugin.Load` (no ECS
  dependency, so it captures connects before game-data init). Derives per-player first-seen,
  session count, total playtime, login frequency (per week), and peak hour (UTC histogram); also
  records an online-count sample at each connect/disconnect for a concurrency series. Open sessions
  are closed on clean shutdown (`Plugin.Unload`) so the last session's playtime is kept; a hard
  crash closes them at boot (negligible-time, never infinite). Concurrency capped to 4000 points.
- **Connectivity patches** (`Patches/PlayerConnectivityPatches.cs`) — `ServerBootstrapSystem`
  `OnUserConnected` (postfix) / `OnUserDisconnected` (prefix), resolving the User from the
  `NetConnectionId` (KindredCommands pattern); fully try/catch-guarded.
- **`pinfo` time-series now live** — `firstseen`, `sessions`, `playmins`, `freq`, `peakhour` are
  real (were `-1` placeholders in 0.2.0). A player with no recorded sessions yet still returns `-1`
  for those (data accrues from install).
- **`.faust api stats <playtime|concurrency> [page]`** (#8) — `[FAUST:stat]` leaderboard rows
  (top players by total minutes) and concurrency time-series points (for BCH graphs, #9).
- BCH contract updated: pinfo marked fully implemented, `stats` shapes documented
  (`playtime`/`concurrency` live; `kills`/`resources` planned), ApiVersion 3.

### Added — admin-control design (docs/features/ADMIN_CONTROL.md)

Captured the full administrative-flexibility spec: per-feature axes (active toggle, audience, **PvP
availability**, item cost, **rate/time-lock windows**, **unlock criteria**), the target gate
evaluation order, runtime operational control (`.faust admin block/unblock/schedule/status` with
countdowns and time-of-day windows), and the build phases. The current `FaustStore` is the
persistence foundation that whole control surface builds on.

### Validated

- 0.2.0's read layer was confirmed working on a live server (castleinfo/plots/pinfo/positions all
  returned data and committed through the gate; per-feature admin gating enforced as designed).

### Notes

- Compiles clean (0 warnings). The persistence/connectivity path is **not yet validated on a live
  server** — first reconnect cycle will confirm session logging.

## [0.2.0] - 2026-06-09

### Added — first investigation queries: castle/plot info, plot availability, player info, positions

The "reuse wins" tier from the design build order — the global server view repackaged into the
`[FAUST:*]` wire so BloodCraftHub has real data to render and the features can be tested in game.
**ApiVersion → 2.**

- **Data layer** (ported from KindredCommands' proven territory/heart/owner model):
  - `EntityExtensions` (IL2CPP-safe `Exists`/`Has`/`Read`/`TryGetComponent`, `TryResolvePlayer`
    by name-fragment-or-steamId, prefab-name lookup) and `Query` (lazy `EntityQueryBuilder`
    helpers; global scans pass `IncludeDisabled` so distant/streamed-out entities are visible).
  - `Wire` — centralizes the two wire invariants: one `ctx.Reply` per `[FAUST:*]` line, and
    wire-safe token values (`Safe()`: spaces → `_`, strips `= ; :`). `SendPage` does 1-based
    paging (20/page) and the `[FAUST:end] cmd=… page=cur/total count=n` trailer.
  - `CastleService` — block-coordinate→territory map (for `here`), per-territory owner/region/
    size/state and fuel-decay math (`FuelEndTime`/`FuelQuantity` + `CastleBloodEssenceDrainModifier`
    + `ServerTime`), and the free-plot scan.
  - `PlayerInfoService` — last-online/online from `User` entities; online-player positions.
- **`.faust api castleinfo <here|nearest|tindex>`** (#2) → `[FAUST:castle]` with owner, steam,
  region, size (blocks), `state=unclaimed|sealed|fueled|decaying`, `decay` seconds (`-1` =
  sealed), online, last-online (Unix UTC).
- **`.faust api plots [page]`** (#4) → `[FAUST:plot]` rows (open territories, largest first) +
  `[FAUST:end]`.
- **`.faust api pinfo <name|steamId>`** (#3) → `[FAUST:player]`; `online`/`lastonline` live now;
  `firstseen`/`sessions`/`playmins`/`freq`/`peakhour` emitted as `-1` (pending the FaustStore
  time-series subsystem). **Self is always allowed**; querying others is gated (AdminOnly default).
- **`.faust api positions [page]`** (#1, admin-default) → `[FAUST:pos]` rows for online players
  (x/z + territory index). Map rendering is a BCH-side decision (design §8); the data is ready.
- All four route through `FaustAccessGate`, committing cooldown/cost only after a real result
  (`notfound`/`badtarget`/empty pages are never charged). In-game `.faust help` + `.faust`
  overview updated to list the live queries.
- BCH contract (`docs/BCH_INTEGRATION_CONTRACT.md`) updated: these shapes are now **implemented**
  (not proposed), with the `state`/`decay` vocabulary, the pending-field sentinels, and the
  `count=` trailer documented.

### Notes

- Reads use the typed `GetComponentData<T>` path the sibling mods ship on the same
  VampireReferenceAssemblies set. Compiles clean (0 warnings); **runtime not yet validated on a
  live server** — this release is for first in-game testing.

## [0.1.0] - 2026-06-09

### Added — project scaffold + the Foundation / permission-cost layer (design #7)

The first build target from `docs/FAUST_DESIGN.md`: the gatekeeper every future query flows
through, plus the BloodCraftHub (BCH) handshake — shipped with **zero data-gathering features
wired**, exactly as the design's build order prescribes.

- **Server-only BepInEx IL2CPP plugin** (`kdpen.Faust`, `Faust.dll`): `Plugin.Load`
  early-returns unless `Application.productName == "VRisingServer"`; VCF command registration;
  Harmony bootstrap; deferred `Core.TryInitialize` gated on the Server world + a populated
  `PrefabCollectionSystem` (via the `SpawnTeamSystem_OnPersistenceLoad` postfix —
  the Uriel/Beelzebub trigger).
- **`FaustAccessGate`** (`Services/FaustAccessGate.cs`) — the single gatekeeper (design §3):
  master-enabled → feature-enabled → access level (Off / AdminOnly / Players) → per-player
  cooldown → item-cost verify. Reserve/confirm split: `TryAuthorize` verifies and stamps
  nothing; `Commit` stamps the cooldown and consumes the cost only after a real result, so an
  empty/`notfound` query is never charged. Denies emit a ready-to-send `[FAUST:err] code=…`
  line. *Inventory verify/consume is stubbed* (the cost is advertised and reserved, but not yet
  drawn from inventory) — it lands with the first server-mediated feature.
- **Per-feature config schema** (`Config/Settings.cs`, design §4) — a global block
  (`Enabled`, `AuditQueries`) plus one block per feature for all seven gateable units
  (`playerpositions`, `castleinfo`, `playerinfo`, `plotavailability`, `objectscan`,
  `castleresources`, `stats`), each with `Access` / `Delivery` / `CostItemGuid` /
  `CostQuantity` / `CooldownSeconds` / `AdminsExempt`. Defaults follow the design: sensitive
  intel (positions, enemy resources, others' player info) → AdminOnly; benign/own-data →
  Players; everything cost-free on a fresh install.
- **BCH handshake** (`Commands/ApiCommands.cs`, contract §2): `.faust api version` advertises
  `api`, `ready`, and one `<feature>=<access>:<cost>` token per feature — access **resolved for
  the requesting player** (an admin sees `players` where a non-admin sees `admin`) and cost as
  `0` or `<guid>x<qty>[:cd=<secs>]` — so BCH gates its UI and shows prices without a round-trip.
  `.faust api ping` → `[FAUST:pong]` proves the round-trip.
- **`.faust` overview** + **`.faust help [players|castles|server|admin]`** topic tree (stubs
  naming the planned feature groups so help grows with the features).
- **ApiVersion 1**. Bumped whenever the `[FAUST:*]` wire grows (contract §6).

### Added — development process scaffolding

- `docs/PREFLIGHT.md` (session-start checklist), `docs/DEV_REMINDERS.md` (standing IL2CPP/ECS +
  process rules), `tools/preflight.ps1` (release-surface sync checker).
- Claude Code guard/reminder hooks (`.claude/hooks/`, local-only): session preflight pointer,
  release-surface sync reminder, reference-path guard (sibling workspaces are read-only), and a
  BCH-contract relevance reminder.
- Dual release surfaces: GitHub README/CHANGELOG (root) + Thunderstore README/CHANGELOG under
  `Faust/Faust/`, with a `thunderstore.toml` manifest (namespace `kdpen`; deps
  BepInExPack_V_Rising 1.733.2, VampireCommandFramework 0.10.4). Build auto-deploys `Faust.dll`
  to a local dedicated server; `BuildToDist` stages the package for `tcli`.

### Notes

- **Not yet published to Thunderstore**; no investigation queries yet. GitHub is the public home
  until the feature set is ready.
- The Thunderstore `icon.png` is the one asset still to add before any package publish (derive
  from the Faust cover art in the workspace root).
- License: **AGPL-3.0** — Faust reuses server-side patterns from odjit's AGPL-licensed
  KindredCommands (territory/heart/owner model, info commands, audit), matching sibling Uriel.

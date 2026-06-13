# Changelog — Faust, Lord of Investigation (full)

This is the **complete** changelog (GitHub). The Thunderstore package carries a condensed,
player-facing changelog at `Faust/Faust/CHANGELOG.md`; every release updates **both** (see
CLAUDE.md → "Release & changelog discipline").

Format: [Keep a Changelog](https://keepachangelog.com/) flavored; versions follow the mod's own
incremental scheme (pre-1.0: minor = feature batch, patch = fixes).

## [0.16.0] - 2026-06-12

In-game configuration, two new investigation features, and the open Raphael server-side items. **ApiVersion
→ 18.** Mirrored into `docs/BCH_INTEGRATION_CONTRACT.md`.

### Added
- **Live config editor (in-game `.cfg` editing).** Every Faust setting is now settable at runtime with no
  restart and no reload: `.faust admin set <feature> <setting=value[,setting=value,...]>` (one or many
  pairs, comma-joined, no spaces) / `get <feature> [setting]`, `.faust admin setglobal <setting=value,...>` /
  `getglobal [setting]`, and `.faust admin resetcfg <feature|global> [setting]`. Writes the BepInEx
  `ConfigEntry` directly, so changes take effect
  immediately (the gate reads every value live) **and** persist to `kdpen.Faust.cfg`. Per-feature settings:
  access, delivery, cost (item+qty), cooldown, window/period/maxuses, availability (PvP), unlock,
  proximity (nearprefab+neardist), adminsexempt; plus the full global block. Values are validated before
  they apply. Delivers Raphael **§15b**. (`Config/ConfigEditor.cs`.)
- **V Blood boss status board** (new `bosses` feature/handshake token, AdminOnly default — PvP-sensitive).
  `.faust api bosses [page]` and `.faust api boss <name|guid>` report each boss Faust can see: live bosses
  carry **position (x/z), region, current/max health + hp%, level, status=up**; bosses a player has defeated
  but that aren't currently spawned carry **status=down defeated=1**. Live world entities only (a boss has no
  entity while on its respawn timer); on-demand, zero passive cost. (`Services/BossService.cs`.) The
  placed-vs-pooled distance threshold is **live-tunable** (`[Faust.Bosses] MapLimit`, default 5000) — raise it
  (e.g. 6000–8000, below ~10000) if outer-region bosses you know are alive read `down` (Raphael §18); use
  `.faust admin bossdiag <name>` to see a boss's real parked position. Genuinely-parked bosses at the off-map
  sentinel remain `down` (locating them would need V Rising's spawn-zone data — a future spike).
- **Kill leaderboards** (new `kills` feature/handshake token, AdminOnly default). `.faust api kills [days=0]`
  (top players by kills, +PvP) and `.faust api bosskills [days=0]` (V Blood defeat counts) — `days 0` =
  all-time, else a rolling UTC-day window. Fed from the existing death hook (no new system), bucketed per
  UTC day, batched to disk (30s autosave + shutdown flush). A new **passive collector**: opt-out via
  `[Faust.Collection] KillTracking` (default on) and bounded by `SessionRetentionDays`; reset with
  `.faust admin data wipe kills`. (`Services/KillTrackingService.cs`.)
- **World-asset scan** (new `worldscan` feature/handshake token, AdminOnly default). `.faust api worldscan
  [type=units|nodes,id=<g>,bloodtype=<g>,bloodqmin=<0-100>] [page]` returns a filtered map of **NPC units**
  (with blood type + quality + health + position/region) and **resource nodes** (ores/trees/plants via
  `YieldResourcesOnDamageTaken`/`OnPickup`), for an in-game "find/filter assets" map. **Classification is
  authoritative:** anything that yields resources is a `node` even though many also carry `UnitLevel`/`Health`
  (so trees/plants no longer leak into `units`); a `unit` is a `CHAR_*` NPC that doesn't yield resources
  (players + V Bloods excluded). Rows now also carry the game's `EntityCategory` **subcategory** — `unittype`
  (UnitCategory) on units (filterable via `unittype=<int>`) and `restier` (ResourceLevel) on nodes. New
  audit command **`.faust admin worldscandiag <nameFragment>`** dumps each prefab's category numbers +
  component flags + Faust's verdict, to validate categorization against the live prefab database. Only admin-**whitelisted**
  prefabs are returned (seeded comprehensively on first run; managed live with `.faust admin worldscan
  list|add|remove|clear|seed`). The full-map scan is **cached + rate-limited** — it rebuilds at most once per
  `[Faust.WorldScan] ScanIntervalSeconds` (≥5s) server-wide, strictly on-demand (zero idle cost), bounded by
  `MaxResults`. V Bloods are excluded (they have the `bosses` feature). (`Services/WorldScanService.cs`.)
- **In-game prefab lookup.** `.faust admin prefab <id|nameFragment> [page]` resolves a PrefabGUID → dev-name,
  or searches the prefab catalog by (partial) name → matching `<guid> <name>` rows (paged). No more leaving
  the game to consult an external dump when setting up worldscan whitelist / item cost / proximity GUIDs.
  (`Services/PrefabLookup.cs`.)
- **§15a — full gate picture on `[FAUST:access]`.** The access row now reports the non-cost gates
  (`cd`/`window`/`period`/`maxuses`/`nearprefab`/`neardist`) so Raphael can display them, not just cost.

### Fixed
- **§13 — `.faust admin data status` no longer throws on populated servers.** The footprint readout built a
  single multi-line reply that overflowed VCF's 512-byte `FixedString512Bytes` (an `ArgumentException`, so no
  reply reached the client). It now sends one reply per line; `admin status` was hardened the same way.

### Changed
- **§14 — `objectscan` retired.** The client-side nearby-objects scan was removed/banned, so `objectscan` no
  longer appears in the `[FAUST:version]` handshake, the `[FAUST:access]` list, `.faust admin status`, or the
  config (its config section is dropped). No client dependency — Raphael already ignores the token.

### Verified
- **Admin controls / limiters are functional in-game.** The per-feature gate axes — access level
  (Off/AdminOnly/Players), item cost, cooldown, usage window/period/max-uses, proximity, PvP availability, and
  unlock — have been verified working on a live server. **Further testing is recommended before relying on
  these in production.** (The new boss/kills/worldscan reads and their categorization remain under ongoing
  in-game validation.)

## [0.15.0] - 2026-06-11

Two Raphael tester batches from v0.50.0 plus a new **player-position heat map**, all additive. The Raphael
batches are under the existing `stats` gate (admin-default; PvP-sensitive — they reveal who plays when): the
**§9 drill-down batch** (per-player / per-event detail behind the Server-Stats charts — identities and
timestamps the bucket-count endpoints can't carry) and the **§10 region/roster batch** (castle fill-% data +
roster extras). The **heat map** adds a new `heatmap` feature/handshake token. Plus the **§11 world-coordinate batch** and a
**clan-members bugfix**. **ApiVersion → 17** (§9→14, §10→15, heat map→16, §11→17; the handshake advertises 17).
Raphael gates each addition on the handshake `api`, so older servers degrade gracefully. Mirrored into
`docs/BCH_INTEGRATION_CONTRACT.md`.

### Added — New-players roster (§9a)

New `.faust api newplayers roster [days=30] [page]` → paged `[FAUST:nprow] steam=… name=… firstseen=<unixUtc>
clan=<wire|->` — the names behind the new-vs-returning counts: one row per player whose first-ever recorded
session falls in the window, newest-join-first. `clan` is resolved from the live ECS clan membership (a new
`ClanService.GetPlayerClanNames()` steam→clan map, offline members included). Same "first seen by Faust"
caveat as `newplayers`/`firstseen`.

### Added — Distinct-players-per-hour sibling line (§9b)

`stats hours` now emits a second line, `[FAUST:hoursplayers] scope=… p00=<n> … p23=<n>` — the **distinct
players** active in each UTC hour, the denominator for an Avg/Total toggle (`avg[h] = h[h] / p[h]`). Same
hour-slicing as `[FAUST:hours]`, counting unique steam IDs per bucket. Emitted right after `[FAUST:hours]`
in the same reply; older clients ignore it.

### Added — Session-interval timeline (§9c)

New `.faust api sessions timeline <all|name|steamId> [days=14] [page]` → paged `[FAUST:stl] steam=… name=…
start=<unixUtc> end=<unixUtc>` — individual online intervals for a per-player Gantt timeline. One row per
session that overlaps the window, start-ascending; `start`/`end` are the real connect→disconnect timestamps
(open sessions end at "now"), left for the client to clip to its render window. `all` = every player; a
named/ID target scopes to one (unresolvable → `notfound`).

### Added — Per-player active-days grid (§9d)

New `stats activegrid [days=30] [page]` → paged `[FAUST:agrow] steam=… name=… active=<int>
days=<dayNum:minutes,…>` — generalises `pdaily` (one player) to **all** players in one query. `active` =
days played in the window; `days` = a compact CSV of `dayNum:minutes` for each non-zero day, where `dayNum`
is the **UTC day number** (`unixMidnight / 86400`) to respect the 509-char wire cap. If a row's CSV would
overflow, the oldest days are dropped (recent-first) — so a row is truncated when its CSV entry-count is
below `active`, and Faust logs a server-side warning rather than silently capping.

### Added — New-player roster extras (§10a)

`[FAUST:nprow]` now appends `playmins=<int> castles=<int>` — the new player's lifetime playtime (same total
as `stats players`) and how many castle hearts they currently own (`0` if none). Raphael shows Playtime +
Castles columns when present and degrades to the name·joined·clan table when absent.

### Added — Region fill-% denominator (§10b)

`[FAUST:region]` (from `stats regions`) now carries `plots=<int>` — the total **buildable** territories in
the region (claimed + open, the same universe `castles` walks). Raphael charts `castles / plots` (%) per
region — a true "how popular is building here" signal instead of a raw count that ignores region size.

### Added — By-region over time (§10c)

New `.faust api stats regiondaily [days=30] [page]` → paged `[FAUST:rdrow] day=<unixUtcMidnight> region=…
castles=<n> plots=<n> players=<n>` — per-day per-region castle/plot/player snapshots for a per-region
fill-% trend + by-date table. Because Faust keeps **no historical castle data** (the map is read live), this
is a **forward-accumulating** series: a new region-snapshot store (`FaustStore`) samples **once per UTC day**
(piggybacked on the day's first connect/disconnect, guarded by `Core.IsReady`), persisted alongside the
session/concurrency data and bounded by `SessionRetentionDays`. Only sampled days appear (sparse, like
`pdaily`); history starts at install. The new samples participate in `.faust admin data status` / `clear` /
`wipe activity` and a shared `RegionStats.Gather()` backs both the live `regions` view and the sampler.

### Added — Player-position heat map (new `heatmap` feature)

`.faust api heatmap [<all|name|steamId>] [page]` returns a binned position-density grid — a **per-player**
heat map or the **aggregated server-wide** one (same data, summed). A new periodic collector
(`HeatmapSampler`, a Unity coroutine timer on the server's main thread — Bloodcraft's pattern, since
V Rising has no per-frame ECS tick safe to borrow) snapshots every online player's `(x,z)` every
`[Faust.Heatmap] SampleSeconds` (30–300s), bins it into a `CellSize×CellSize` grid,
and accumulates a per-(player, cell) count in a new `HeatmapStore` (`heatmap.json`). The reply is a
`[FAUST:hmhead]` header (scope / cell size / sample total / cell bounds / `collecting` flag) + **packed**
`[FAUST:hmrow] data=cx:cz:count,…` cell lines (many cells per line to keep a dense map small), paged. Its own
`heatmap` feature gate (AdminOnly default, advertised in the handshake) governs the read; **collection is
opt-in** (`[Faust.Heatmap] Enabled`, default off — the only collector that runs on a timer). Bounded by
`MaxCells`; resolution fixed once data exists (change `CellSize` ⇒ wipe first). Cumulative density (no time
axis yet). Participates in `.faust admin data status` and `.faust admin data wipe heatmap|all`. Raphael will
build the heat-map visualization on its side.

### Added — Territory world coordinates (§11a) + full-map heatmap bounds (§11b)

Every `[FAUST:castle]` row (`castleinfo`, `castles`, `decay`) and `[FAUST:plot]` row now carries optional
`posx`/`posz` — the territory's **centroid world coords** (mean of its block coordinates via KindredCommands'
`(10·block−6400)/2` transform, computed once in `CastleService.EnsureBlockMap`), the same space as `positions`
`x`/`z`, so a client can show *where on the map* a plot is. Omitted when a territory has no resolvable blocks.
The `[FAUST:hmhead]` heat-map header also gains optional `mapbounds` — the full buildable-map cell extent at the
current `CellSize` — so a sparse heat map can be drawn at true map scale instead of a tiny occupied-cells board.

### Fixed — `clanmembers` no longer times out on clan names with spaces

`.faust api clanmembers <clanName>` rejected any clan name containing a space (e.g. "Blood Lords"): VCF binds a
non-final `string` to one token, so the trailing `int page` parameter stole the second word and the whole call
failed to bind — VCF replied an error to chat (not a `[FAUST:*]` line), so BCH/Raphael saw a no-response timeout.
The page parameter was dropped from the signature (clan rosters fit one page; a trailing page integer is still
recovered manually), letting VCF capture the full multi-word name greedily. Names resolve raw or `_`-encoded.

### Notes

- **ApiVersion → 17** (the wire grew across the Raphael batches, the heat map, and §11); only the heat map adds a
  new handshake token (`heatmap`) — the §9/§10/§11 additions reuse existing gates. Plugin version stays
  **0.15.0** — all three fold into the same unreleased release. Verified by a clean Release build; in-game
  validation (alongside the still-pending §8/§9 reads, and the heat-map sampler/grid) is queued for a test
  server. **§3** (out-of-bounds region → real name or the `-` sentinel, never `none`) and **§5** (native-map
  markers via `.faust admin showpositions`) remain already-implemented items pending live validation.

## [0.14.0] - 2026-06-11

The **§8 tester batch** from Raphael (live tester feedback) — richer Castle Info, prisoners, clan
members, an activity breakdown, and Faust usage/access oversight. All additive; **ApiVersion → 13**.
Raphael gates each addition on the handshake `api`, so older servers degrade gracefully. Mirrored into
`docs/BCH_INTEGRATION_CONTRACT.md`.

### Added — Castle Info extras (§8a)

`castleinfo` now appends optional fields to its `[FAUST:castle]` row (single-lookup ONLY — the
`castles`/`decay` lists stay cheap): `floors` (building storeys), `clan` (owning clan name), and `items`
(the castle's grand-total item count — the single high-level number, NOT the per-item breakdown, so no
raid intel leaks). Each token is omitted when Faust can't resolve it. `heartlevel` and `claimed` (heart
placement time) are reserved but **not yet emitted** — the game exposes no confirmable numeric heart-level
field nor a reliable placement timestamp.

### Added — Prisoners in Castle Resources (§8b)

`resources` now reports a castle's prisoners: a `prisoners=<n>` count on the `[FAUST:res]` header and one
`[FAUST:prisoner] name=… bloodtype=… bloodquality=…` row per prisoner (appended after the item rows, paged
together under `cmd=resources`). Prisoners are resolved from the game's `ImprisonedBuff` targets attributed
to the castle's territory; blood fields sentinel (`-`/`-1`) when a unit carries no blood.

### Added — Clan members endpoint (§8c)

New `.faust api clanmembers <clanName> [page]` (under the `clans` gate) → paged
`[FAUST:clanmember] name=… online=<0|1> role=<leader|member>`. Name matches case-insensitively against the
clan name and its wire-safe form. Cleaner than stuffing a member list on the `[FAUST:clan]` row.

### Added — New-vs-returning split on the daily series (§8d)

`stats daily` rows now carry `new=<int> returning=<int>` (new = of that day's DAU, players whose first-ever
recorded session is that day; returning = `dau - new`) for a stacked activity breakdown. Same
"first-seen-by-Faust" caveat as `newplayers`.

### Added — Faust usage & access oversight (§8e)

Two admin-oversight endpoints (under the `stats` gate), both pure server-side accounting Faust already owns
— no client→server usage reporting:
- `.faust api access [page]` → `[FAUST:access] feature=… scope=<off|admin|players> cost=<guid>x<qty>
  granted=<n> unlocked=<n>` — who can use each feature (server-wide picture), how many players are
  granted/unlocked (`unlocked=-1` for features with no unlock criterion).
- `.faust api usage [days=7] [page]` → `[FAUST:usagerow] feature=… uses=<n> payers=<n> itemspent=<int>
  item=<guid> cooldownhits=<n>` — how often each feature is used and what it costs players, over a rolling
  window. Backed by a new per-(feature, UTC-day) tally store (`feature_usage_stats.json`), bounded by
  `SessionRetentionDays`, recorded from the gate's commit/deny paths. Surfaced in `.faust admin data
  status` and wiped via `.faust admin data wipe usage|all`.

### Notes

- **ApiVersion → 13** (the wire grew); no new handshake token (the new endpoints reuse the `clans`/`stats`
  gates). Verified by a clean Release build; in-game validation of the IL2CPP-dependent reads (8a floors,
  8b prisoner linkage + blood) is pending a test server.

## [0.13.0] - 2026-06-10

### Changed — every capability now ships **AdminOnly** by default

Faust is an administrative tool first: admins decide what, if anything, to grant players. The four
features that previously defaulted to `Players` (`castleinfo`, `plotavailability`, `objectscan`, `stats`)
now default to **AdminOnly** like the rest. Existing configs are unaffected (the default only applies to
freshly-generated keys); admins still set any feature to `Players`/`Off` in the `.cfg`. The handshake
reflects this (a non-admin sees `…=admin:…` for all tokens until granted).

### Added — per-player query rate limit (anti-spam / perf protection)

New global `[Faust] RateLimitSeconds` (default `0` = off) — a minimum number of seconds a player must
wait between **any** two Faust queries; a violation denies with `[FAUST:err] code=ratelimit secs=<n>`.
`RateLimitAdminsExempt` (default true) keeps admin dashboards/paging unthrottled. Enforced in
`FaustAccessGate` as a new axis (master → block → schedule → feature → **rate limit** → unlock → access →
…). Stops a player hammering a query and stressing the server. **ApiVersion → 12** (new deny code).

### Added — layered-admin control over data resets

New `[Faust.Data] ResetSteamIds` — a comma-separated SteamID allowlist of the admins permitted to run the
**destructive** data commands (`.faust admin data clear` / `data wipe`). Empty (default) = any admin, as
before; set it to lock data resets to senior admins on servers with tiered admin teams. Junior admins keep
every other `.faust admin …` command and `data status`. (`Settings.MayResetData`, checked in
`AdminDataCommands`.)

### Added — `.faust api stats players` player-activity roster (Raphael §7)

A single paged endpoint with one row per tracked player — the per-player data behind the aggregate
population/recency numbers, so a dashboard can show *who* is behind the totals without N round-trips.
```
[FAUST:prow] steam=<id> name=<wire_name> online=<0|1> lastonline=<unixUtc> \
    active24h=<0|1> active7d=<0|1> sessions=<n> playmins=<total> daysidle=<n>
[FAUST:end] cmd=players page=<cur>/<total> count=<n>
```
`active24h`/`active7d` are the per-player booleans behind DAU/WAU; same fields as `pinfo`, playtime-
descending. Under the `stats` gate (AdminOnly by default). New `FaustStore.GetPlayerRoster`.

### Confirmed / no-op

- **Per-capability admin controls (your audit):** enable/disable (`Access=Off` + master switch), item
  cost (`CostItemGuid`+`CostQuantity`), cooldown **or** usage-count-per-period
  (`CooldownSeconds` / `WindowSeconds`+`PeriodSeconds`+`MaxUsesPerPeriod`), and a proximity requirement
  (`RequireNearPrefab` + `RequireNearDistance`, default **5 m**) were already fully built and gate-enforced.
- **Raphael §3 (out-of-bounds region):** already resolved — the admin island's territory is genuinely
  `WorldRegionType.None` (confirmed against the enum; no admin-island region exists), so Faust emits the
  canonical `region=-` ("outside map" in Raphael). Open-world player regions resolve via the
  world-position polygon test (a live-verify item).

## [0.12.0] - 2026-06-10

### Added — weekday + per-player analytics (Raphael §6) and clan composition (#clans)

**ApiVersion → 11** (additive; older clients hide the new shapes/feature). Two requested Raphael
analytics shapes plus a new clan-composition feature.

- **`.faust api stats weekdays [<name|steamId>]`** — accumulated playtime **minutes per UTC weekday**
  (`d0`=Monday … `d6`=Sunday), server-wide or per player; sessions sliced at UTC midnight like `daily`.
  One line: `[FAUST:weekdays] scope=<server|steamId> d0=<min> … d6=<min>`. Makes the server "by day of
  week" view authoritative and adds it per player.
- **`.faust api stats pdaily <name|steamId> [days=90]`** — one player's UTC-day playtime for the last N
  days (clamped 1–90), one row per online day. `[FAUST:pdaily] steam=<id> day=<unixUtcMidnight>
  minutes=<int>` rows + `[FAUST:end] cmd=pdaily count=<n>`. The per-player analogue of `daily`
  (player is required). The `stats` command now takes an optional second arg for the day window.
- **`.faust api clans [page]`** — **clan composition**: a summary headline of clanned-vs-independent
  plus a per-clan roster. New AdminOnly feature key **`clans`**, advertised in the handshake and
  enrolled in `.faust admin` block/schedule/grant.
  - `[FAUST:clansummary] clans=<n> clanned=<n> independent=<n> online_clanned=<n> online_independent=<n>
    largest=<n> avg=<f>` (page 1) — counts are over **every known user** (online + offline); the
    `online_*` pair is the currently-connected split.
  - `[FAUST:clan] name=<wire> members=<n> online=<n> leader=<wire>` rows (members-descending) +
    `[FAUST:end] cmd=clans`. Built live from `ClanTeam` entities + the per-user `ClanEntity` link
    (`ClanService`); empty/disbanded clans skipped. A clanless server still sends the zeroed summary.

New `FaustStore` aggregations (`GetWeekdayHistogram`, `GetPlayerDailySeries`) + a new `ClanService`.
Contract: `docs/BCH_INTEGRATION_CONTRACT.md` (handshake `clans` token, §3 `clans` section, stats
`weekdays`/`pdaily` shapes). No wire change to existing shapes.

### Added — population-health metrics, recency, and region/clan distribution

A second analytics batch (admin-dashboard headline metrics + the spatial/clan distribution views).
Still ApiVersion 11; all under the existing `stats`/`clans` gates.

- **`.faust api stats population`** — `[FAUST:population] dau wau mau new_today returning_today stickiness
  d1 d7 d30`: active-player counts (DAU/WAU/MAU), new-vs-returning, DAU/MAU stickiness, and D1/D7/D30
  retention. The "is the server growing or bleeding" headline.
- **`.faust api stats recency`** — `[FAUST:recency] seen24h seen7d seen30d dormant total`: how many known
  players are recently active vs drifting away.
- **`.faust api stats peak [days=30]`** — `[FAUST:concsummary] peak peak_t avg p95 now`: concurrency
  summary (peak/avg/p95 + live count) instead of only the raw point series.
- **`.faust api stats regions [page]`** — `[FAUST:region] name players castles` rows: online-player and
  claimed-castle distribution per map region (now that region resolves by world position).
- **`pinfo` gains `daysidle`** — whole days since a player was last seen (0 online, -1 untracked): the
  per-player at-risk signal next to the holistic `recency`.
- **Clan rows gain `castles`** — territories each clan's team owns (which clans actually hold ground).

New `FaustStore` rollups (`GetPopulationStats`, `GetRecencyBuckets`, `GetConcurrencySummary`, plus
`DaysIdle` on `PlayerMetrics`); `ClanService` now counts castles per clan team.

### Added — EXPERIMENTAL `.faust admin showpositions` (native-map markers, §5)

Server-side admin command to put online players on the **native in-game map** (Raphael §5), via the
**game's own attach mechanism**: it adds the marker prefab to each player character's
`AttachMapIconsToEntity` buffer (confirmed in `ProjectM.Shared`, namespace `ProjectM`, field
`PrefabGUID Prefab`) and lets ProjectM's `InstantiateMapIconsSystem` spawn the real networked, replicated
icon — so the **server**, not the plugin, builds the network state (no hand-rolled `NetworkSnapshot`, so
the client-side-faking crash risk doesn't apply). Command + lifecycle (on/off/`status`, optional
duration auto-off, attach-on-connect, detach on disconnect/`off`/plugin-unload) are wired; the configured
marker prefab GUID is validated against the prefab map before any attach.

**Gated OFF by default** (`[Faust.MapMarkers] Enabled`) and **still unproven** — validate on a TEST
server. The open items are spawn behaviour for far/culled players and, above all, **admin-only
visibility**: the default `MapIcon_Player` is ally-visible, so strict admin-only viewing needs a
purpose-built marker prefab (`MarkerPrefabGuid`) or a post-spawn `MapIconData` edit — to be tuned live.
No wire shape; positions still come from `.faust api positions`.

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

# Faust ↔ BloodCraftHub — integration contract

> **Direction:** the seam between **Faust (server)** and **BloodCraftHub (client,
> "BCH")**. This is the living contract: when Faust changes anything BCH-facing
> here, update this doc in the same commit and mirror it into BCH's handoff doc.
> Modeled on the existing Beelzebub/Uriel integration (BCH already speaks both).
>
> **BCH already has the machinery** to probe, gate a tab group on presence, parse
> `[MOD:*]` wire lines, page, and handle `[MOD:err]`. Faust just has to answer in
> the shapes below; BCH adds a `FaustProtocolService` mirroring
> `BeelzProtocolService` / Uriel.

---

## 1. Transport

- BCH sends `.faust …` commands by silently injecting a chat message (the same
  path it uses for `.beelz` / `.uriel`).
- Faust replies with **VCF `ctx.Reply`** System-chat messages tagged `[FAUST:*]`.
  BCH intercepts those tags and removes them from the visible chat.
- **One `ctx.Reply` per `[FAUST:*]` wire line.** BCH reads one wire line per chat
  message and does **not** split on `\n`. A paged reply = N row-replies + a
  `[FAUST:end]` trailer reply. (This was the #1 Uriel integration bug — never
  `\n`-join a page into one reply.)
- Replies are plain System-chat (no HMAC required — that's only for Bloodcraft's
  Eclipse progress broadcast). If anti-spoofing is ever needed, add a signature
  field behind an ApiVersion bump.

---

## 2. Handshake (`.faust api version`)

BCH probes `.faust api version` with back-off on login (its existing probe loop,
~12 attempts) and gates the FAUST tab group on a `ready=1` ACK. Faust answers
with one line:

```
[FAUST:version] api=<int> plugin=<semver> ready=1 \
    playerpositions=<acc>:<cost> castleinfo=<acc>:<cost> playerinfo=<acc>:<cost> \
    plotavailability=<acc>:<cost> castleresources=<acc>:<cost> stats=<acc>:<cost> \
    allcastles=<acc>:<cost> decaywatch=<acc>:<cost> clans=<acc>:<cost> heatmap=<acc>:<cost> \
    bosses=<acc>:<cost> kills=<acc>:<cost> worldscan=<acc>:<cost>
```
(`objectscan` was RETIRED in ApiVersion 18 — no longer advertised; see §14 note below.)

(Single line — shown wrapped here for readability.)

- `api` — integer ApiVersion. **Bump whenever the wire grows.** BCH gates richer
  UI behind `api >= N`.
- `ready` — `1` once Faust is initialized and can answer queries (`0`/absent →
  BCH keeps the group greyed).
- One token **per feature**, value `=<access>:<cost>` so BCH can gate the UI and
  show the price WITHOUT a round-trip:
  - `<access>` ∈ `off | admin | players` (resolved for the *requesting* player —
    i.e. an admin sees `players` where a non-admin would see `admin`).
  - `<cost>` ∈ `0` (free) or `<itemGuid>x<qty>` (e.g. `576389135x1`). Optional
    `:cd=<seconds>` suffix if a cooldown applies.
- All feature tokens optional; absent → BCH treats the feature as unavailable.

BCH reads these into a feature→(access,cost,cooldown) map and renders each query
button with its price and enabled/greyed state.

---

## 3. Per-feature query commands & reply shapes

All under `[CommandGroup("faust api")]`, each `[Command(...)]` taking an optional
`int page = 1` where paged. Every command runs through `FaustAccessGate` first
(see `FAUST_DESIGN.md` §3); a denied call emits only a `[FAUST:err]` line.

> **Status (ApiVersion 18):** as of Faust 0.14.0, **every feature ships AdminOnly by default** (Faust is
> an admin tool first; admins grant pieces to players per server) — so a fresh handshake shows
> `…=admin:…` for all tokens to a non-admin. New in **18** (Faust 0.16.0): two new features each with its
> own handshake token + gate — **`bosses`** (`.faust api bosses [page]` / `boss <name|guid>` — V Blood
> status board: live position/region/health + up/down/defeated, §B1 below), **`kills`** (`.faust api
> kills [days]` / `bosskills [days]` — top-killer + boss-defeat leaderboards, §B2), and **`worldscan`**
> (`.faust api worldscan [spec] [page]` — a filtered map of NPC units + resource nodes, §C1). `[FAUST:access]` grows
> the non-cost gate tokens `cd=`/`window=`/`period=`/`maxuses=`/`nearprefab=`/`neardist=` so Raphael can
> display the full gate picture (§15a). **`objectscan` is RETIRED** — dropped from the handshake and the
> `access` list (§14). Plus the live config editor (chat commands, no wire — see §3b). New in **17** (§11 world coords, additive/omittable, no new
> token): every `[FAUST:castle]`/`[FAUST:plot]` row gains optional `posx=`/`posz=` (the territory's
> **centroid world coords** — where on the map it is, §11a), and `[FAUST:hmhead]` gains optional
> `mapbounds=` (the full buildable-map cell extent for true-scale heat maps, §11b). Also fixed in 0.15.0:
> **`clanmembers` now accepts clan names with spaces** (a no-response bug — the name was bound to only the
> first word). New in **16** (player-position heat map): a new **`heatmap`**
> feature/token — `.faust api heatmap [<all|name|steamId>]` returns a binned density grid (`[FAUST:hmhead]`
> header + packed `[FAUST:hmrow]` cells) for per-player and server-wide heat maps. Collection is **opt-in**
> (`[Faust.Heatmap] Enabled`, sampled on a timer); the read is gated like any feature. New in **15** (the
> §10 region/roster batch, additive, under
> the `stats` gate): `[FAUST:nprow]` grows `playmins=`/`castles=` (§10a); `[FAUST:region]` grows `plots=`
> (the castle fill-% denominator, §10b); and a new **`stats regiondaily`** endpoint (`[FAUST:rdrow]`:
> per-day per-region castles/plots/players — a forward-accumulating series, §10c). New in **14** (the §9
> drill-down batch, all additive — no new
> handshake token, all under the `stats` gate): a **`newplayers roster`** endpoint (`[FAUST:nprow]`: who
> joined + when + clan), a **`hoursplayers`** sibling line on `stats hours` (`[FAUST:hoursplayers]`:
> distinct players per UTC hour — the avg-per-player denominator), a **`sessions timeline`** endpoint
> (`[FAUST:stl]`: individual online intervals), and a **`stats activegrid`** kind (`[FAUST:agrow]`:
> per-player active-days grid). New in **13** (the §8 tester batch, all additive — no new
> handshake token): `castleinfo` grows optional `heartlevel`/`floors`/`claimed`/`clan`/`items`; `resources`
> reports `prisoners=` + `[FAUST:prisoner]` rows; a new **`clanmembers`** endpoint (under the `clans` gate);
> the `daily` series grows `new=`/`returning=`; and two admin-oversight endpoints **`access`** and
> **`usage`** (under the `stats` gate). New in 12: the `stats players` roster (§7 below) and a
> per-player `ratelimit` deny code (§4). The shapes below are **IMPLEMENTED** and live as of Faust 0.12.0 —
> castleinfo (#2), plots (#4), pinfo (#3, with FaustStore-derived playtime/frequency/peak-hour),
> positions (#1, carrying `region=`), resources (#6), stats (#8: `playtime` + `concurrency`, plus the
> activity-analytics charts `hours` / `daily` / `newplayers` / `sessions` from 0.10.0 and `weekdays` /
> per-player `pdaily` added in 0.12.0), `castles` (full-map list, `allcastles`), `decay` (claimed
> castles by soonest-decay, `decaywatch`), and `clans` (clan composition: clanned-vs-independent +
> rosters, new in 0.12.0). `objectscan` (#5) is
> still proposed and is largely **client-side** (BCH reads nearby entities itself; route through
> Faust only if an admin prices it). A `-1` in any numeric field is the "not tracked / none recorded
> yet" sentinel.
>
> 0.4.0–0.7.0 completed the **admin-control gate** (`docs/features/ADMIN_CONTROL.md`): per-feature
> item-cost is actually consumed, plus window/period time-locks, PvP-availability, live admin
> block/schedule overrides, **unlock criteria** (a feature opens only after a player defeats a
> configured V Blood / Dracula, or an admin grants it), and a **proximity requirement** (usable only
> within range of a configured object). These surface to BCH as new `[FAUST:err]` deny codes (§4) —
> no reply-shape change to the queries themselves. The admin operations (`.faust admin …`, incl.
> `grant`/`revoke` and the `data status`/`clear`/`wipe` data-management commands) are server-side chat
> commands, not wire — they don't affect the `[FAUST:*]` surface or ApiVersion. Note Faust's collected
> data is server-scoped (`BepInEx/config/Faust/`) and survives a world wipe; admins reset it explicitly
> via `.faust admin data wipe …` (so BCH/Raphael analytics may span worlds unless an admin wiped).
>
> **§5 native-map markers (status):** `.faust admin showpositions <on|off|status>` is a server-side
> admin command (no wire shape), **gated off by default** (`[Faust.MapMarkers] Enabled`). The attach is
> implemented via the game's own `AttachMapIconsToEntity` buffer (`ProjectM.Shared`) →
> `InstantiateMapIconsSystem`, so the server spawns the real networked icon. **Still being validated**
> on a live server — the open item is admin-only visibility (default marker is ally-visible). Positions
> still come from `.faust api positions`; Raphael can wire a "Show players on map" button that sends the
> command, but keep it behind a diagnostics/experimental flag until the visibility model is confirmed.

### `castleinfo` (#2) — IMPLEMENTED
`.faust api castleinfo <token>` — `<token>` = `here` (territory you're standing in, default) |
`nearest` (territory of the nearest castle heart) | a territory index `<int>`.
```
[FAUST:castle] tindex=<int> owner=<wire_name> steam=<id> region=<wire_name> size=<blocks> \
    state=<unclaimed|sealed|fueled|decaying> decay=<secondsLeft> online=<0|1> lastonline=<unixUtc> \
    [posx=<float>] [posz=<float>] [heartlevel=<int>] [floors=<int>] [claimed=<unixUtc>] [clan=<wire_name>] [items=<int>]
```
- `state`: `unclaimed` (no heart), `sealed` (frozen — never decays), `fueled` (has blood
  essence / time remaining), `decaying` (out of fuel, ticking down).
- `decay`: seconds of fuel remaining; **`-1` when `state=sealed`** (infinite). For an unclaimed
  plot, `owner=_ steam=0 online=0 lastonline=0`.
- `size`: territory block count (10×10 build blocks) — a proxy for plot size.
- `lastonline`: Unix UTC seconds (from `User.TimeLastConnected`); `0` = unknown.
- **`posx`/`posz` (ApiVersion ≥17, §11a) — on EVERY castle row** (`castleinfo`, `castles`, `decay`): the
  territory's **centroid world coords** (one decimal), the same coordinate space as `positions` `x`/`z` —
  i.e. *where on the map* the plot is. Omitted only when a territory has no resolvable blocks. (Cheap dict
  lookup, so unlike the §8a extras these ride on the list rows too.)
- **§8a extras (ApiVersion ≥13) — single `castleinfo` lookup ONLY** (NOT the `castles`/`decay` lists,
  which stay cheap). Each token is **OMITTED when Faust can't resolve it**, so Raphael shows each only
  when present (no sentinel to special-case):
  - `floors` — number of building storeys (distinct floor heights). `clan` — owning clan's wire-safe
    name (absent when the owner is solo). `items` — the castle's **grand total item count** only (the
    single `resources` header `totalitems` number — NOT the per-item breakdown, so no raid intel leaks).
  - `heartlevel` and `claimed` (heart placement time) are **not currently emitted** — the game exposes
    no confirmable numeric heart-level field and no reliable placement timestamp; the tokens are reserved
    and will appear if a source is found. (`floors`/`clan`/`items` are the live ones.)
- Errors: `badtarget` (token isn't here/nearest/int), `notfound` (no such territory).

### `plotavailability` (#4) — IMPLEMENTED
`.faust api plots [page]` — open (heart-less) territories, largest first.
```
[FAUST:plot] tindex=<int> size=<blocks> region=<wire_name> [posx=<float>] [posz=<float>]
…rows…
[FAUST:end] cmd=plots page=<cur>/<total> count=<n>
```
- `posx`/`posz` (ApiVersion ≥17, §11a): the territory's **centroid world coords** (same as `[FAUST:castle]`
  above) — where on the map the open plot is. Omitted when unresolvable.

### `allcastles` (full server map) — IMPLEMENTED (ApiVersion ≥8)
`.faust api castles [page]` — **every** territory (claimed AND open), largest first. Admin-default
(own feature key `allcastles`); the full-map view that powers BCH's "All Plots" tab. `plots` returns
only *open* territories and `castleinfo` is one-at-a-time — this is the paged whole-map list.
```
[FAUST:castle] tindex=<int> owner=<wire_name> steam=<id> region=<wire_name> size=<blocks> \
    state=<unclaimed|sealed|fueled|decaying> decay=<secondsLeft> online=<0|1> lastonline=<unixUtc>
…rows…
[FAUST:end] cmd=castles page=<cur>/<total> count=<n>
```
- **Reuses the `[FAUST:castle]` tag** (identical field set to `castleinfo`). BCH disambiguates by the
  in-flight query: a single `castleinfo` lookup emits one `[FAUST:castle]` with **no** end trailer
  and commits immediately; `castles` emits N rows **followed by** `[FAUST:end] cmd=castles`.
- Unclaimed (heart-less) territory row: `owner=_ steam=0 region=<name> size=<blocks> state=unclaimed
  decay=0 online=0 lastonline=0` (same convention as `castleinfo` for an open plot). `decay=-1` when
  `state=sealed`. An empty server still sends `[FAUST:end] cmd=castles page=1/1 count=0`.

### `decaywatch` (abandoned/decaying castles) — IMPLEMENTED (ApiVersion ≥9)
`.faust api decay [page]` — **claimed** castles only, ordered **soonest-to-decay first**, paged.
Admin-default (own feature key `decaywatch`); the housekeeping view (which castles are about to fall
/ look abandoned — pair `decay` with `lastonline`). Open plots are excluded; sealed castles
(`decay=-1`, never decay) sort to the end.
```
[FAUST:castle] tindex=<int> owner=<wire_name> steam=<id> region=<wire_name> size=<blocks> \
    state=<sealed|fueled|decaying> decay=<secondsLeft> online=<0|1> lastonline=<unixUtc>
…rows (ascending decay)…
[FAUST:end] cmd=decay page=<cur>/<total> count=<n>
```
- **Reuses the `[FAUST:castle]` tag** (identical fields to `castleinfo`/`castles`); BCH disambiguates
  by the `[FAUST:end] cmd=decay` trailer. No `unclaimed` rows here (claimed castles only). An empty
  server still sends `[FAUST:end] cmd=decay page=1/1 count=0`.

### `clans` (clan composition) — IMPLEMENTED (ApiVersion ≥11)
`.faust api clans [page]` — how the server population splits between clans and solo players, plus a
per-clan roster. Admin-default (own feature key `clans`; PvP-sensitive rosters). A summary line (page 1),
then one paged `[FAUST:clan]` row per non-empty clan (members-descending).
```
[FAUST:clansummary] clans=<n> clanned=<n> independent=<n> online_clanned=<n> online_independent=<n> largest=<n> avg=<f>
[FAUST:clan] name=<wire_name> members=<n> online=<n> castles=<n> leader=<wire_name>
…rows (members-descending)…
[FAUST:end] cmd=clans page=<cur>/<total> count=<n>
```
- **`clansummary`** (page 1 only): `clanned` / `independent` are counts over **every known user**
  (online + offline); `online_clanned` / `online_independent` are the currently-connected split;
  `largest` is the biggest clan's member count; `avg` is mean members per clan (one decimal, `0.0` if no
  clans). This is the "how many are clanned vs solo" headline.
- **`clan`** rows: `members` = total roster size, `online` = members currently connected, `castles` =
  territories the clan's team owns (ApiVersion ≥11), `leader` = the clan leader's wire-safe name (`_` if
  none resolved). Empty/disbanded clans are skipped.
- A clanless server still sends the summary (with zeros) + `[FAUST:end] cmd=clans page=1/1 count=0`.
- BCH disambiguates the `[FAUST:clan]` rows from the `clansummary` header by tag; paging is standard.

### `clanmembers` (one clan's roster) — IMPLEMENTED (ApiVersion ≥13, §8c)
`.faust api clanmembers <clanName> [page]` — the member roster of a single clan, paged. Shares the
**`clans`** feature gate. `<clanName>` matches case-insensitively against the clan's name **and** its
wire-safe (`_`→space) form, so the `_`-encoded name Raphael shows resolves directly.
- **Clan names with spaces are supported** (fixed 0.15.0): `clanName` is captured greedily (the whole
  remainder), so `clanmembers Blood Lords` works — send the raw display name *or* the `_`-encoded form.
  An optional trailing **page** integer is still honored (`clanmembers Blood Lords 2`). (Earlier builds
  bound only the first word and rejected the rest before replying — BCH saw a no-response timeout.)
```
[FAUST:clanmember] name=<wire_name> online=<0|1> role=<leader|member>
…rows (leader first, then online, then name)…
[FAUST:end] cmd=clanmembers page=<cur>/<total> count=<n>
```
- No clan matches the name → `[FAUST:err] code=notfound feature=clans`. A matched-but-empty clan still
  sends `[FAUST:end] cmd=clanmembers page=1/1 count=0`. Cleaner than stuffing a member list onto the
  `[FAUST:clan]` row (no 509-char line cap to worry about for big clans).

### `playerinfo` (#3) — IMPLEMENTED
`.faust api pinfo <steamIdOrName>` — **self always allowed**; querying *others* is gated by the
`playerinfo` feature access (AdminOnly by default). Name lookups must be unique (exact name wins).
```
[FAUST:player] steam=<id> name=<wire_name> online=<0|1> lastonline=<unixUtc> \
    firstseen=<unixUtc> sessions=<n> playmins=<total> freq=<perWeek> peakhour=<0-23> daysidle=<n>
```
- `daysidle` (ApiVersion ≥11): whole days since the player was last seen (**0** if online now, **-1** if
  untracked) — the per-player "at-risk / drifting away" signal that complements the holistic `recency`.
- All fields are **live**, derived from Faust's own session log (`FaustStore`) — the game only
  stores the *last* connect time, so Faust logs connect/disconnect over time. `firstseen` is Unix
  UTC; `playmins` is total minutes; `freq` is logins/week (one decimal); `peakhour` is the busiest
  hour-of-day (0–23, **UTC**). A player with **no recorded sessions yet** (e.g. hasn't reconnected
  since Faust was installed) returns `-1` for the time-series fields; `online`/`lastonline` still
  come straight off the User entity.
- Hover-to-identify is a **BCH client-side** mechanic (find the player entity under the cursor,
  read SteamID/name off replicated state) → then BCH calls this command.

### `playerpositions` (#1) — IMPLEMENTED (data); rendering is BCH-side
`.faust api positions [page]` — admin-default. One row per **online** player.
```
[FAUST:pos] steam=<id> name=<wire_name> x=<f> z=<f> tindex=<int> region=<wire_name>
…rows…
[FAUST:end] cmd=positions page=<cur>/<total> count=<n>
```
- `x`/`z` are world coordinates (one decimal). `tindex` is the territory the player is standing
  in, or `-1` in the open world.
- `region` (ApiVersion ≥8) is the player's **world-map region** at their position (`Wire.Safe`,
  `_`→space on display), or `-` when outside every region. **Resolved from world position by
  point-in-polygon over the map's `WorldRegionPolygon` set (fixed in 0.10.0)** — it no longer
  requires the player to be standing on a castle plot, so a player roaming the open world now reports
  their region (Farbane Woods, Dunley Farmlands, …) and not a blank. The client can't compute this
  itself (those region/territory entities aren't replicated to it), so the server supplies it.
- **Rendering is the open problem** (design §8): BCH draws these on its own map overlay, or Faust
  drives server-side MapIcon reveal — decide via spike. The data is ready either way.

### `heatmap` (player-position density) — IMPLEMENTED (ApiVersion ≥16)
A binned density grid built from periodic position snapshots — both a **per-player** heat map and the
**aggregated server-wide** one. Faust samples every online player's `(x,z)` on a timer (admin-configurable
`[Faust.Heatmap] SampleSeconds`, 30–300s), bins each into a `CellSize×CellSize` grid cell, and accumulates
a per-(player, cell) count. Its own feature gate (`heatmap`, AdminOnly default — PvP-sensitive: it reveals
where players spend time). **Collection is opt-in** (`[Faust.Heatmap] Enabled`, default off — the only
collector that runs on a timer); when off, the endpoint still answers but returns an empty grid.

`.faust api heatmap [<all|name|steamId>] [page]` — `all`/`server` (default) = the whole server summed; a
name/SteamID = that one player. A header line (page 1), then **packed** cell rows (paged):
```
[FAUST:hmhead] scope=<server|steamId> cell=<f> samples=<n> cells=<n> bounds=<minCx>:<minCz>:<maxCx>:<maxCz> [mapbounds=<minCx>:<minCz>:<maxCx>:<maxCz>] collecting=<0|1>
[FAUST:hmrow] data=<cx>:<cz>:<count>,<cx>:<cz>:<count>,...          ; many cells per line, paged
...rows (densest cells first)...
[FAUST:end] cmd=heatmap scope=<...> page=<cur>/<total> count=<totalCells>
```
- **`cell`** = grid resolution in world units; map a cell back to world space as `worldX ~= cx*cell` (+`cell/2`
  for the cell centre), `worldZ ~= cz*cell`. **`cx`/`cz` are signed** cell indices (`floor(x/cell)`,
  `floor(z/cell)`) — the map spans negative coordinates. `count` = times a player was sampled in that cell
  (intensity). **`samples`** = total samples in this scope (sum of counts) — use it to normalize. `cells` =
  distinct cells; `bounds` = the cell-index extent of the **occupied** cells. **`mapbounds` (ApiVersion ≥17,
  §11b, optional)** = the cell-index extent of the **whole buildable map** at the current `cell` size
  (constant per server) — draw the grid to `mapbounds` so a sparse map reads as a few dots on the real map
  outline rather than a tiny zoomed board; falls back to `bounds` if absent. **`collecting`** = whether
  sampling is currently on (so BCH distinguishes "off" from "on but no data yet").
- **Packed rows** keep a dense map to a handful of lines: each `[FAUST:hmrow] data=` carries many
  `cx:cz:count` triples (comma-separated) under the 509-char cap (same idea as `activegrid`). Split each row
  on `,` then `:`. Cells are densest-first, so an early page already gives the hotspots.
- **Cumulative density** (no time axis in v1) accumulated since install / last reset; resolution is fixed
  once data exists (changing `CellSize` needs a `.faust admin data wipe heatmap`). Bounded by
  `[Faust.Heatmap] MaxCells`. An empty/disabled grid still sends the header + `count=0` trailer.

### §B1 `bosses` — V Blood boss status board — IMPLEMENTED (ApiVersion ≥18)
`.faust api bosses [page]` (the board) and `.faust api boss <name|guid>` (one boss). Own feature key
`bosses` (AdminOnly default — PvP-sensitive: boss locations are intel). Faust is the authoritative global
view, so it can report bosses anywhere on the map (the client is spatially culled). A boss is a live world
entity ONLY while spawned; killed, it despawns and respawns on a timer (no entity between). So two layers:
```
[FAUST:boss] guid=<int> name=<wire_name> status=<up|down> defeated=<0|1> \
    [x=<float>] [z=<float>] [region=<wire_name>] [hp=<float>] [hpmax=<float>] [hppct=<int>] [level=<int>]
…rows (live bosses first, then by name)…
[FAUST:end] cmd=bosses page=<cur>/<total> count=<n>
```
- **`status=up`** — a live world entity exists right now: `x`/`z` (world coords, same space as `positions`),
  `region` (`Wire.Region`, `-` if none), `hp`/`hpmax` (current/max) + `hppct` (0–100), `level` are present.
- **`status=down`** — NOT placed in the world right now, so the live fields (`x`/`z`/`region`/`hp`/…) are
  **omitted**. Two sources: (a) a **pooled/staged** VBlood entity that exists but isn't on the map (it parks
  at the off-map spawn sentinel — Faust now reports these as `down` with no coords instead of the bogus
  `~10000,10000` limbo position, **Raphael §16 server-side fix, ApiVersion 18**), or (b) a boss a player has
  defeated (from the unlock store), which carries `defeated=1`. So Raphael no longer needs its ±5000 client
  guard — `up` rows always carry a real on-map position.
- **`defeated`** = has ANY player on the server ever killed this V Blood (server-wide, `0|1`); a live boss
  can be `status=up defeated=1` (up now, beaten before).
- **`name`** is the prefab dev-name (`CHAR_*_VBlood`); Raphael may prettify by `guid`.
- **Single `boss <name|guid>` lookup** emits ONE `[FAUST:boss]` with **no** end trailer (commit immediately,
  like a single `castleinfo`); the `bosses` list emits N rows **followed by** `[FAUST:end] cmd=bosses`. A
  matched-name-or-guid is required (`<name>` is greedy, so multi-word names like `Solarus the Immaculate`
  work); no match → `[FAUST:err] code=notfound feature=bosses`. Empty board still sends `count=0`.
- **Scope:** the board lists placed bosses (`up`) + any pooled/staged boss entities and defeated bosses
  (`down`). Because the spawn system pre-instantiates pooled VBlood entities, the `down` set often covers
  much of the boss roster — a placed instance for a given guid always wins over a pooled one. "dead"
  collapses into `down` (no on-map entity). On-demand; zero passive cost.
- **§18 — RESOLVED (server-side); ⚠️ needs a Raphael-side guard change.** Some bosses read `down` when the
  admin expected `up`. Root cause found via `.faust admin bossdiag`: the V Rising map extends **well past
  ±5000**, and streamed-out V Bloods keep their **real** positions (there is **no** sentinel-parking, contrary
  to the §16 assumption) — so the original ±5000 `up`/`down` cutoff wrongly classed every outer-region boss as
  `down`. Fix: the cutoff is now `[Faust.Bosses] MapLimit`, **default raised to 9000** (covers the whole map,
  still excludes a ~10000 off-map sentinel), live-tunable via `.faust admin setglobal bossmaplimit=…`. All live
  bosses now report `up` with coordinates.
  **⚠️ Raphael action:** the old **±5000 client-side coord guard** from §16 ("treat boss coords beyond ±5000 as
  no-position") must now be **raised to ~9000 or removed** — otherwise Raphael will re-hide the now-correct
  outer-region boss coordinates that Faust sends. (§16's guard was added when we wrongly believed >5000 = a
  bogus sentinel; that's no longer true.)

### §B2 `kills` — kill + boss-defeat leaderboards — IMPLEMENTED (ApiVersion ≥18)
Own feature key `kills` (AdminOnly default). Fed by the existing death hook (no new system), tallied per
UTC day, so any rolling window is cheap. Both endpoints take `[days=0]` (0 = all-time, else last N UTC days)
+ `[page]`, share the `kills` gate, and are **opt-in collection** via `[Faust.Collection] KillTracking`
(default on; when off the boards are empty).
```
.faust api kills [days=0] [page]
[FAUST:kill] rank=<n> steam=<id> name=<wire_name> kills=<n> pvp=<n>      ; top players by kills
[FAUST:end] cmd=kills page=<cur>/<total> count=<n>

.faust api bosskills [days=0] [page]
[FAUST:bosskill] rank=<n> guid=<int> name=<wire_name> count=<n>          ; V Blood defeat counts
[FAUST:end] cmd=bosskills page=<cur>/<total> count=<n>
```
- `kills` = total units the player killed in the window; `pvp` = of those, kills where the victim was a
  player. `bosskills` `count` = times that V Blood was defeated server-wide in the window (`name` = prefab
  dev-name; prettify by `guid`). Rows are descending (rank 1 = top); both paged, empty board → `count=0`.
- Tally bucketed per UTC day, bounded by `SessionRetentionDays`, persisted to `kills.json` (batched writes:
  30s autosave + shutdown flush, so a crash loses at most the last interval). Reset with `.faust admin data
  wipe kills`. *(The "items collected per day" leaderboard half is deferred — the obtain-vs-gather accuracy
  design; this batch ships kills + boss-defeats only.)*

### §C1 `worldscan` — map of in-game assets (units + resource nodes) — IMPLEMENTED (ApiVersion ≥18)
A filtered map of **NPC units** and **resource nodes** for an in-game "find/filter assets" map. Own feature
key `worldscan` (AdminOnly default — economy/PvP-sensitive: it reveals where every ore/NPC and every
high-quality-blood unit is). Faust is the global view, so it sees the whole map (the client is culled).

`.faust api worldscan [spec] [page]` — `spec` is a **single space-free** comma-joined `key=value` filter
(same form as the config editor, since VCF 0.10.4 tokenizes on spaces). A bare `units`/`nodes`/`all` is
shorthand for the kind. Keys:
- `type` = `units` | `nodes` | `all` (default `all`)
- `id=<prefabGuid>` — only that prefab
- `bloodtype=<prefabGuid>` — units whose blood matches (implies units)
- `bloodqmin=<0-100>` — units with blood quality ≥ this (implies units)

Examples: `worldscan type=nodes,id=<g>` · `worldscan type=units,bloodqmin=80` · `worldscan bloodtype=<g>`.
```
[FAUST:asset] guid=<int> name=<wire_name> kind=<unit|node> x=<float> z=<float> region=<wire_name> \
    [hp=<float> hpmax=<float>] [bloodtype=<wire_name> bloodq=<int>] [unittype=<int>] [restier=<int>]
…rows…
[FAUST:note] cmd=worldscan truncated=1 max=<n>          ; ONLY if the scan hit MaxResults (sent before [FAUST:end])
[FAUST:end] cmd=worldscan page=<cur>/<total> count=<n>
```
- **Classification (authoritative):** `kind=node` = a harvestable (`YieldResourcesOnDamageTaken` ores/trees/rocks
  or `YieldResourcesOnPickup` plants/flowers) — **even though many also have `UnitLevel`/`Health`** (you damage
  them to harvest), they are always `node`, never `unit`. `kind=unit` = an actual NPC/character (a `CHAR_*`
  prefab with `UnitLevel` that does **not** yield resources), excluding players + V Bloods. *(Fixed: pre-fix,
  resource nodes with `UnitLevel` leaked into `units` — now resolved server-side, so `type=units` returns only
  real NPCs and `type=nodes` returns harvestables.)*
- `kind=unit` rows carry the unit extras: `hp`/`hpmax` (omitted if the unit has no Health), **`bloodtype`**
  (blood prefab dev-name, `-` if none) + **`bloodq`** (quality 0–100, `-1` if none), and **`unittype`** =
  `EntityCategory.UnitCategory` (the NPC subcategory as an int — e.g. human/beast/undead/…; omitted if the
  unit has no category). `kind=node` rows carry **`restier`** = `EntityCategory.ResourceLevel` (the node's
  resource tier as an int; omitted if absent) plus guid/name/x/z/region. `x`/`z` are world coords (same space
  as `positions`); `region` uses the canonical `-` sentinel.
- **Subcategory filter:** `unittype=<int>` filters units to that `UnitCategory` (implies units). The integer
  values are the game's `EntityCategory.UnitCategory` enum — run **`.faust admin worldscandiag <fragment>`**
  on the server to see each prefab's `main`/`unit`/`res` category numbers + Faust's verdict (the authoritative
  audit against the live prefab database); Raphael can map the ints to labels once confirmed.
- **Only WHITELISTED prefabs are returned.** The whitelist is admin-curated (seeded comprehensively on first
  run); manage it server-side with `.faust admin worldscan <list|add|remove|clear|seed> [guid|page]` (chat,
  not wire). V Bloods are **excluded** (use the `bosses` feature).
- **Rate limited by a cached snapshot.** The full-map scan runs at most once per `[Faust.WorldScan]
  ScanIntervalSeconds` (≥5s) server-wide and is reused between — so querying faster just re-filters the cache
  (no extra scan), and there is zero cost when nobody queries. Bounded by `MaxResults`; on overflow a
  **`[FAUST:note] … truncated=1`** line precedes the end trailer (so Raphael can warn "filter to see more").
- Errors: standard gate denies (`noaccess`, `cost`, `cooldown`, …). An empty/over-filtered result still sends
  `[FAUST:end] cmd=worldscan page=1/1 count=0`.

### `objectscan` (#5) — RETIRED (ApiVersion 18, Raphael §14)
The client-side nearby-objects scan was removed/banned from Raphael, so `objectscan` is **no longer a
Faust feature**: it's dropped from the `[FAUST:version]` handshake, the `[FAUST:access]` list, `.faust
admin status`, and the config (its `Faust.objectscan` section is gone). No client dependency — Raphael
already ignores the token. (Historical: it was a Free/client-side feature with a never-implemented
`.faust api objects` server fallback.)

### `castleresources` (#6) — IMPLEMENTED
`.faust api resources <here|nearest|tindex> [page]` — admin-default, PvP-sensitive. Sums every
container's contents in the castle on the target territory (containers + stations connected to the
heart). A summary header (page 1), then one paged `[FAUST:item]` row per distinct item type.
```
[FAUST:res] tindex=<int> owner=<wire_name> steam=<id> containers=<n> totalitems=<n> distinct=<n> prisoners=<n>
[FAUST:item] guid=<int> qty=<n> name=<wire_name>
…item rows (qty-descending)…
[FAUST:prisoner] name=<wire_name> bloodtype=<wire_name> bloodquality=<int>
…prisoner rows…
[FAUST:end] cmd=resources page=<cur>/<total> count=<distinct+prisoners>
```
- `containers` = containers that held ≥1 item; `totalitems` = grand total; `distinct` = item types.
- An **unclaimed** territory (no heart) → `code=notfound`. An empty (claimed) castle → header with
  zeros + a `count=0` end. `name` is the item's prefab dev-name (BCH may prettify by `guid`).
- **`prisoners` (ApiVersion ≥13, §8b)** = count of prisoners held in the castle; the header carries the
  total, and one `[FAUST:prisoner]` row per prisoner is appended **after** the `[FAUST:item]` rows (both
  page together under `cmd=resources` — disambiguate by tag). `bloodtype` is the prisoner's blood-type
  prefab dev-name (`-` if none) and `bloodquality` is its quality 0–100 (`-1` if no blood). `count` on the
  `[FAUST:end]` is the total row count (items + prisoners).
- This is the powerful raid-intel feature: defaults to **AdminOnly**, and is a natural one to price
  (`CostItemGuid`) or PvP-gate (`Availability=PvPOnly`) via the admin-control axes.

### `stats` (#8) — IMPLEMENTED (`playtime`, `concurrency`, `hours`, `daily`, `newplayers`, `sessions`, `weekdays`, `pdaily`, `population`, `recency`, `peak`, `regions`, `players`, `activegrid`, `regiondaily`)
`.faust api stats <kind> [arg]` — the `<arg>` slot is a **page** (`playtime`/`concurrency`), a
**player** `<name|steamId>` scope (`hours`/`sessions`), or a **day window** (`daily`/`newplayers`),
parsed per-kind. All share the one `stats` feature gate. `kills` | `resources` are still planned.

**Leaderboard + concurrency series** (paged, ApiVersion ≥8):
```
[FAUST:stat] kind=playtime rank=<n> steam=<id> name=<wire_name> value=<minutes>   ; leaderboard rows
[FAUST:stat] kind=concurrency t=<unixUtc> avg=<count>                              ; time-series points (for #9 graphs)
[FAUST:end] cmd=stats kind=<kind> page=<cur>/<total> count=<n>
```
- `playtime`: total minutes per player (descending), from the session log.
- `concurrency`: an online-player-count sample recorded at **each connect/disconnect** (event-
  driven, not a fixed interval — flat periods produce no points, so the client must hold-last-value
  between samples), oldest→newest, the **full stored history** (bounded by `MaxConcurrencyPoints`,
  default 4000), **paged** like the other lists (page 1 = oldest). `avg` is the sampled count at time
  `t`. BCH renders leaderboards as tables and concurrency as **graphs** (#9). For long-range "how busy
  over time" the `daily` series below is the cleaner signal.

**Activity analytics** (ApiVersion ≥10) — time-resolved aggregations over the same session log, each
its own shape so BCH adds a chart per shape and **hides** any shape Faust doesn't emit:

`.faust api stats hours [<name|steamId>]` — accumulated playtime **minutes per UTC hour-of-day**
(24 buckets), server-wide or for one player. Now **two** single lines, no trailer:
```
[FAUST:hours] scope=<server|steamId> h00=<min> h01=<min> … h23=<min>
[FAUST:hoursplayers] scope=<server|steamId> p00=<n> p01=<n> … p23=<n>   ; ApiVersion ≥14 (§9b)
```
- Sessions are **sliced at hour boundaries**, so a session spanning 22:30→01:15 feeds hours 22/23/0/1
  — a true "when is the server / this player active" profile, not just a connect-time tally.
- **`[FAUST:hoursplayers]` (ApiVersion ≥14, §9b)** = **distinct players** active in each UTC hour, the
  denominator for an **Avg / Total** toggle on the chart: `avg[h] = h[h] / p[h]` (guard `p[h]=0`). Same
  `scope`, emitted in the same `stats hours` reply right after `[FAUST:hours]`. Older BCH ignores it.

`.faust api stats daily [days=14]` — per-day **DAU** (distinct players online) and **play-minutes**
for the last N days (clamped 1–90), oldest→newest, un-paged:
```
[FAUST:daily] day=<unixUtcMidnight> dau=<int> minutes=<int> new=<int> returning=<int>   ; one row per day
[FAUST:end] cmd=daily count=<n>
```
- Playtime is sliced at UTC midnight; a player counts toward a day's DAU if any session overlapped it.
  Every day in the window emits a row (zero-activity days included), so `count` = the day span.
- **`new`/`returning` (ApiVersion ≥13, §8d):** `new` = of that day's DAU, players whose **first-ever
  recorded session** is that same day; `returning` = `dau - new`. Lets Raphael draw a stacked
  new-vs-returning chart. Same "first seen by Faust" caveat as `newplayers` (bounded by retention; right
  after install, returning veterans register as `new`).

`.faust api stats newplayers [days=30]` — per-day count of players whose **first recorded session**
falls on that day (growth/retention), last N days (clamped 1–90), oldest→newest, un-paged:
```
[FAUST:newplayers] day=<unixUtcMidnight> new=<int>   ; one row per day in the window
[FAUST:end] cmd=newplayers count=<n>
```
- First-seen is bounded by retained data (`SessionRetentionDays`): on a pruned server, growth before
  the retention window is under-counted.

`.faust api stats sessions [<name|steamId>]` — **session-length distribution** (four bucket counts),
server-wide or for one player. Single line, no trailer:
```
[FAUST:sessions] scope=<server|steamId> lt15=<n> m15_60=<n> h1_3=<n> gt3h=<n>
```
- Buckets are `<15m` / `15–60m` / `1–3h` / `3h+` by session duration.

`.faust api stats weekdays [<name|steamId>]` (ApiVersion ≥11) — accumulated playtime **minutes per UTC
weekday**, server-wide or for one player. Single line, no trailer:
```
[FAUST:weekdays] scope=<server|steamId> d0=<min> d1=<min> … d6=<min>
```
- **`d0`=Monday … `d6`=Sunday, UTC** (consistent with `hours` h00…h23). Sessions sliced at UTC midnight
  (like `daily`), so a session that straddles midnight feeds both weekdays — authoritative "by day of
  week" for the **server** and now **per player** too.

`.faust api stats pdaily <name|steamId> [days=90]` (ApiVersion ≥11) — **one player's** UTC-day playtime
for the last N days (clamped 1–90), **one row per day the player was online** (zero-days omitted),
oldest→newest, un-paged:
```
[FAUST:pdaily] steam=<id> day=<unixUtcMidnight> minutes=<int>   ; one row per online day
[FAUST:end] cmd=pdaily count=<n>
```
- The per-player analogue of `daily` (no `dau` — it's a single player). `<name|steamId>` is **required**
  (an unresolvable/missing player → `[FAUST:err] code=notfound feature=stats`). Re-bucket client-side for
  a per-player weekly trend, the same way the server `daily` series is re-bucketed.

**Population health rollups** (ApiVersion ≥11) — single-line headline metrics, no trailer:

`.faust api stats population` — active-player counts + retention:
```
[FAUST:population] dau=<n> wau=<n> mau=<n> new_today=<n> returning_today=<n> stickiness=<f> d1=<f> d7=<f> d30=<f>
```
- `dau` = active in the current UTC day; `wau`/`mau` = active within the last 7 / 30 days. `new_today` =
  first seen today; `returning_today` = `dau - new_today`. `stickiness` = `dau/mau` (0..1). `dN` =
  retention: of players who first appeared ≥ N days ago, the fraction still active ≥ N days later (0..1).

`.faust api stats recency` — how many known players are recently active vs drifting away:
```
[FAUST:recency] seen24h=<n> seen7d=<n> seen30d=<n> dormant=<n> total=<n>
```
- Cumulative (`seen7d` includes `seen24h`); `dormant` = not seen in >30 days; `total` = all tracked players.

`.faust api stats peak [days=30]` — concurrency summary over the last N days (0 = all stored):
```
[FAUST:concsummary] peak=<n> peak_t=<unixUtc> avg=<f> p95=<n> now=<n>
```
- `peak`/`peak_t` = max concurrent + when; `p95` = 95th-percentile concurrent; `now` = live online count.
  **`avg` is sample-weighted** (samples are event-driven, not time-spaced) — `peak`/`p95` are the solid figures.

`.faust api stats regions [page]` — population + castle distribution by map region (paged):
```
[FAUST:region] name=<wire_name> players=<n> castles=<n> plots=<n>
…rows (castles-descending)…
[FAUST:end] cmd=regions page=<cur>/<total> count=<n>
```
- `players` = **online** players currently in that region (offline players have no position); `castles` =
  claimed castles in it. `name=-` is the open-world / no-region bucket (the canonical region sentinel).
- **`plots` (ApiVersion ≥15, §10b)** = total **buildable** territories in the region (claimed + open — the
  same universe `castles` walks), the fill-% denominator: BCH charts `castles / plots` (%) per region.

`.faust api stats regiondaily [days=30] [page]` (ApiVersion ≥15, §10c) — the **by-region view over time**:
per-day per-region castle/plot/player snapshots, oldest day first, paged:
```
[FAUST:rdrow] day=<unixUtcMidnight> region=<wire_name> castles=<n> plots=<n> players=<n>
…rows (one per region per sampled day; oldest day first)…
[FAUST:end] cmd=regiondaily days=<n> page=<cur>/<total> count=<n>
```
- `castles`/`plots` per region per day drive a per-day **fill-%** line/bar and a by-date table; `players`
  is the online count in that region at sample time. `region=-` is the open-world bucket.
- **Forward-accumulating + sparse.** Faust keeps **no historical castle data** (the map is read live), so
  this series is sampled **once per UTC day** (on that day's first connect/disconnect) and accumulates from
  install — there is no pre-install history. **Only sampled days appear** (a day with zero player activity
  has no row, like `pdaily` omitting zero days), and it's bounded by `SessionRetentionDays`. Treat a thin
  early series as "since Faust install," same caveat as `daily`/`newplayers`.

`.faust api stats players [page]` (ApiVersion ≥12) — the **per-player activity roster** (§7): one row per
tracked player, the data behind the aggregates, playtime-descending, paged:
```
[FAUST:prow] steam=<id> name=<wire_name> online=<0|1> lastonline=<unixUtc> \
    active24h=<0|1> active7d=<0|1> sessions=<n> playmins=<total> daysidle=<n>
…rows (playmins-descending)…
[FAUST:end] cmd=players page=<cur>/<total> count=<n>
```
- `active24h`/`active7d` are the per-player booleans behind DAU/WAU (render the ✓ "active today/this
  week"); `daysidle` matches `pinfo` (`0` online, whole days since last seen otherwise); `lastonline` is
  Faust's last-session-end (UTC). Same fields as `pinfo`, emitted for every tracked player in one list.
  Admin-default (under the `stats` gate); PvP-sensitive (reveals who plays when).

- An unresolvable player scope (`hours`/`sessions`) or unknown `<kind>` → `[FAUST:err]
  code=notfound|badtarget feature=stats`. The `daily`/`newplayers` end trailers carry **no `page=`
  token** (the whole fixed window ships at once).

**Interpretation caveats (BCH should label these for admins):**
- **All hour/day bucketing is UTC.** `hours` (h00…h23), `daily`/`newplayers` `day=` (UTC midnight),
  and `pinfo peakhour` are all **UTC** — the client should localize for display, or a US/EU admin will
  read "peak hour = 03:00" and be confused.
- **`newplayers` / `pinfo firstseen` mean "first seen by Faust," not true first join.** All series are
  derived from `FaustStore` and accumulate from the moment Faust was installed — there is no history
  before that. So **right after install, returning veterans register as "new"** (their first post-install
  session looks like a first-seen) and the new-players chart spikes artificially. Label it "new to
  tracking since <install>," not "account created."
- **`newplayers` is only reliable with session retention disabled.** If the server sets
  `SessionRetentionDays` > 0, pruning drops a veteran's early sessions, so their earliest *retained*
  session re-registers them as "new." Treat `newplayers` as meaningful only when retention is off
  (the default), and read all windows (`daily`/`hours`/`playtime`) as bounded by the retention window.
- **`peakhour` (pinfo) and `hours` (stats) measure different things.** `peakhour` is the hour with the
  most **logins** (by connect time); `hours` is accumulated **playtime minutes** per hour (sessions
  sliced at hour boundaries). They can legitimately disagree.
- **Activity data is server-lifetime, not world-lifetime — it can span multiple worlds.** Faust's
  session log lives in `BepInEx/config/Faust/` (server-scoped, *not* in the V Rising world save), so it
  **survives a world wipe** — by design, since the same players return and their history stays relevant.
  On a server that has wiped/reset its world, `playtime` / `daily` / `firstseen` / `newplayers` may
  therefore include **pre-wipe** history. There is **no wire signal** for a wipe (ApiVersion unaffected);
  treat all series as accumulated over the server's lifetime. An admin can deliberately reset them with
  `.faust admin data wipe activity` (or separate worlds up front with the `DataNamespace` config) — those
  are server-side admin actions, nothing for BCH/Raphael to implement, just to be aware of when labeling
  charts (e.g. "since Faust install / last reset," not "this world").

### `access` / `usage` (Faust oversight) — IMPLEMENTED (ApiVersion ≥13, §8e)
Two admin-oversight endpoints — "who can use Faust, and how it's being used." Both share the **`stats`**
feature gate (admin-default); both are pure server-side accounting Faust already keeps for its cost/
cooldown gates, so there is **no client→server usage reporting** (no perf cost on Raphael's side).

`.faust api access [page]` — per-feature access snapshot (one row per feature), paged:
```
[FAUST:access] feature=<name> scope=<off|admin|players> cost=<itemGuid>x<qty> \
    cd=<secs> window=<secs> period=<secs> maxuses=<n> nearprefab=<guid> neardist=<m> \
    granted=<n> unlocked=<n>
…rows…
[FAUST:end] cmd=access page=<cur>/<total> count=<n>
```
- `scope` is the feature's **configured** access (server-wide picture — NOT resolved per-requester like
  the handshake). `cost` = `0` (free) or `<itemGuid>x<qty>`. `granted` = players with an explicit admin
  grant for the feature; `unlocked` = tracked players who satisfy its unlock criterion, or **`-1`** when
  the feature has **no** unlock criterion (everyone qualifies — "N/A").
- **§15a gate tokens (ApiVersion ≥18) — between `cost` and `granted`:** the configured **non-cost** gates,
  all bare numbers (`0` = unset), so Raphael can display the full per-feature gate picture without a
  per-feature `get`: `cd` (flat cooldown seconds), `window`/`period`/`maxuses` (the burst-window usage
  policy — "`maxuses` uses per `period`s, each opening a `window`s grace"), `nearprefab` (proximity object
  PrefabGUID, `0` = none) + `neardist` (its radius in metres, one decimal). These mirror the
  `Faust.<feature>` config keys an admin can now also set live via `.faust admin set` (§3b). Older Raphael
  ignores the unknown tokens.

`.faust api usage [days=7] [page]` — per-feature usage over the last N UTC days (clamped 1–365), paged,
uses-descending; features with no activity in the window are omitted:
```
[FAUST:usagerow] feature=<name> uses=<n> payers=<n> itemspent=<int> item=<itemGuid> cooldownhits=<n>
…rows…
[FAUST:end] cmd=usage page=<cur>/<total> count=<n>
```
- `uses` = successful queries; `payers` = distinct players who paid an item cost; `itemspent` = total
  quantity of the cost item consumed; `item` = the feature's **currently-configured** cost item GUID
  (`0` if free); `cooldownhits` = denials that hit the per-feature cooldown/window. Tallied in per-(feature,
  UTC-day) buckets persisted server-side (`feature_usage_stats.json`), bounded by `SessionRetentionDays`.
  An empty window still sends `[FAUST:end] cmd=usage page=1/1 count=0`.

### §9 drill-down detail — IMPLEMENTED (ApiVersion ≥14)
The per-player / per-event detail behind the Server-Stats charts — identities and timestamps the
bucket-count endpoints above can't carry. All four share the **`stats`** feature gate (admin-default;
PvP-sensitive — they reveal who plays when), are additive, and degrade gracefully (older BCH just won't
query them). `[FAUST:hoursplayers]` (§9b) is documented with `stats hours` above.

`.faust api newplayers roster [days=30] [page]` (§9a) — the **names behind the new-vs-returning counts**:
one row per player whose **first-ever recorded session** falls in the last N days (clamped 1–90),
newest-join-first, paged:
```
[FAUST:nprow] steam=<id> name=<wire_name> firstseen=<unixUtc> clan=<wire_name|-> playmins=<int> castles=<int>
…rows (newest join first)…
[FAUST:end] cmd=newplayersroster days=<n> page=<cur>/<total> count=<n>
```
- `firstseen` = first-ever session (Unix UTC seconds) — the same "first seen by Faust" definition the
  `new` count uses (bounded by `SessionRetentionDays`; see the caveats above). `clan` = the player's
  current clan (`Wire.Safe`), or `-` if solo. The first arg is the literal sub-command `roster` (anything
  else → `[FAUST:err] code=badtarget feature=stats`). An empty window still sends a `count=0` trailer.
- **`playmins`/`castles` (ApiVersion ≥15, §10a):** `playmins` = the player's lifetime total play-minutes
  (same as `stats players`); `castles` = castle hearts they currently own (`0` if none). Appended to the
  row — BCH shows Playtime + Castles columns when present and degrades to name·joined·clan when absent.

`.faust api sessions timeline <all|name|steamId> [days=14] [page]` (§9c) — **individual online intervals**
for a per-player activity timeline (Gantt): one row per session that **overlaps** the last N days
(clamped 1–90), start-ascending, paged:
```
[FAUST:stl] steam=<id> name=<wire_name> start=<unixUtc> end=<unixUtc>
…rows (start-ascending)…
[FAUST:end] cmd=sessionstimeline days=<n> page=<cur>/<total> count=<n>
```
- `start`/`end` are the **real** connect→disconnect timestamps (an open session ends at "now"); BCH clips
  them to its render window. `all` = every player's sessions (paged); `<name|steamId>` = one player
  (unresolvable → `[FAUST:err] code=notfound feature=stats`). The first arg is the literal sub-command
  `timeline` (else `badtarget`). Window-bounded server-side; page for busy servers.

`.faust api stats activegrid [days=30] [page]` (§9d) — **per-player active-days grid**: one row per player
with any activity in the last N days (clamped 1–90), most-active-first, paged:
```
[FAUST:agrow] steam=<id> name=<wire_name> active=<int> days=<dayNum:minutes,dayNum:minutes,…>
…rows (active-days-descending)…
[FAUST:end] cmd=activegrid days=<n> page=<cur>/<total> count=<n>
```
- `active` = count of days the player was online in the window. `days` = compact CSV of `dayNum:minutes`
  for each **non-zero** day (zero-days omitted, recent-first ordering). **`dayNum` is the UTC day NUMBER —
  days since the Unix epoch (`unixMidnight / 86400`)** — kept compact to respect the 509-char wire cap;
  multiply by `86400` for the midnight timestamp. If a row's CSV would overflow the line budget the
  **oldest** days are dropped, so a row is **truncated** when its CSV entry-count is less than `active`
  (Faust also logs a warning server-side). Generalises `stats pdaily` (one player) to all players at once.

---

## 3b. Admin config editor — live `.cfg` editing (chat commands, NOT wire)

> **New (Faust 0.16.0).** Delivers Raphael's **§15b** ask (`FAUST_API_REQUESTS.md`): every Faust
> setting is now settable **in-game at runtime**, no `.cfg` edit and no restart. These are admin-only
> `.faust admin …` **chat commands** — *not* `[FAUST:*]` wire and **not** part of the handshake, so
> there is **no ApiVersion change**. Raphael drives them exactly like the existing
> `block`/`schedule`/`grant` controls (inject the command, read the plain-text ack). Writes hit the
> BepInEx `ConfigEntry` directly, so the change takes effect **immediately** (the gate reads every
> value live per query) **and** BepInEx persists it to `kdpen.Faust.cfg` (survives restart).

> **⚠️ Syntax changed in 0.16.0 (Raphael §17) — ACTION FOR RAPHAEL.** The earlier space-separated form
> (`set <feature> <setting> <value> …`) does **not** work: the server's VCF (**0.10.4**) tokenizes the chat
> line on spaces and matches by exact arg count, with no `[Remainder]`, so `set castleinfo costitem 862477668
> costqty 100` is rejected with **"Too many parameters: expected 2, got N."** The value list must arrive as a
> **single space-free token**: `setting=value`, comma-joined for multiples. Raphael must send the new form.

**Per-feature** (`<feature>` = any handshake feature key):
```
.faust admin set <feature> <setting=value[,setting=value,...]>   ; one OR MANY settings, NO spaces in the spec
.faust admin get <feature> [setting]             ; read current value(s) — all, or one (with its valid-range hint)
.faust admin resetcfg <feature> [setting]        ; restore one setting (or the whole feature block) to default
```
- **The spec is ONE comma-separated, space-free argument** of `setting=value` pairs — each validated/applied
  independently (a bad pair is reported but doesn't block the others), so Raphael pushes a whole panel in one
  send, e.g. `set castleinfo costitem=862477668,costqty=100,cooldown=30,maxuses=1,period=86400`. A single
  setting is just `set castleinfo cooldown=30`. **No spaces anywhere in the spec** (Faust values never contain
  spaces, `,`, or `=`). **Note:** several gates are two-part and only enforce when BOTH halves are set — a
  **cost** needs `costitem` (≠0) AND `costqty` (>0); a **use limit** needs `period` (>0), with `maxuses`/`window`
  on top (`maxuses` alone does nothing). Send them together in one spec to avoid a half-configured gate.
- **`setglobal`** takes the same spec with no feature: `setglobal heatmapenabled=true,heatmapsample=120`.
- **`adminsexempt`** (default **true**) makes admins skip this feature's cost/cooldown/limit/proximity/PvP —
  so an admin testing a gate sees it NOT enforced until `adminsexempt off` (or they test as a non-admin).

`<setting>` (aliases in parens) and accepted `<value>`:

| setting | values |
|---|---|
| `access` | `Off` \| `AdminOnly` \| `Players` |
| `delivery` | `ServerMediated` \| `Free` |
| `costitem` (`costguid`) | item PrefabGUID hash (`0` = free) |
| `costqty` | integer ≥ 0 |
| `cooldown` (`cd`) | seconds ≥ 0 |
| `adminsexempt` (`exempt`) | `on`\|`off` (also true/false, 1/0, yes/no) |
| `availability` (`pvp`) | `Always` \| `PvEOnly` \| `PvPOnly` |
| `window` | seconds ≥ 0 (burst window length) |
| `period` | seconds ≥ 0 (`86400` = daily) |
| `maxuses` (`max`) | integer ≥ 0 (`0` = unlimited) |
| `unlock` | `None` \| `FinalBoss` \| `BossKill:<guid>` \| `AllBosses` \| `AllQuests` |
| `nearprefab` (`near`) | object PrefabGUID (`0` = no proximity gate) |
| `neardist` (`dist`) | metres ≥ 0 |

**Global** (master / anti-spam / collection / heatmap / map-markers):
```
.faust admin setglobal <setting=value[,setting=value,...]>   ; one OR MANY globals, NO spaces in the spec
.faust admin getglobal [setting]                 ; read global value(s)
.faust admin resetcfg global [setting]           ; restore global default(s)
```
Global `<setting>` keys: `enabled`, `audit`, `verbose`, `ratelimit`, `ratelimitexempt`,
`resetsteamids`, `sessiontracking`, `concurrencysampling`, `maxconcurrencypoints`,
`sessionretentiondays`, `datanamespace`, `heatmapenabled`, `heatmapsample` (30–300),
`heatmapcellsize`, `heatmapmaxcells`, `mapmarkersenabled`, `mapmarkerprefab`.

**Acks (plain System-chat, not tagged — Raphael shows them as the command result):**
- Success: `Set [<scope>] <setting> = <value>` (`<scope>` = the feature key or `global`); a reserved
  unlock value adds `  (reserved — admin-grant-only …)`.
- Rejected value: `Rejected: <reason>` (e.g. the valid range) — the write did **not** happen.
- Unknown setting: `Unknown setting '<x>'. Settings: <list>`. Unknown feature: `Unknown feature '<x>'. Use one of: <keys>`.
- `get` with no `setting` returns the feature's/global's settings as `name = value` lines (chunked to
  stay under the VCF reply cap — see §13-style hazard note below).

> **Relationship to the `[FAUST:access]` row (Raphael §15a — DELIVERED, ApiVersion ≥18):** the editor
> lets Raphael **set** the gates; the `[FAUST:access]` row now also reports them
> (`cd`/`window`/`period`/`maxuses`/`nearprefab`/`neardist`, see the `access` section in §3), so Raphael
> can **display** the full per-feature gate picture from one `access` page — no per-feature `get`
> round-trip needed (use `.faust admin get <feature> <setting>` only for an ad-hoc single read). Mapping
> for Raphael's §15b verb inputs onto the editor (one `set` per feature, pairs comma-joined): `cost` →
> `set <f> costitem=<g>,costqty=<n>`; `cooldown` → `set <f> cooldown=<s>`; `limit` →
> `set <f> maxuses=<n>,period=<s>,window=<s>`; `near` → `set <f> nearprefab=<g>,neardist=<m>`.

---

## 4. Paging & errors (shared conventions)

- **Paging is 1-based.** BCH requests page 1, reads `page=cur/total` off
  `[FAUST:end]`, and auto-requests `cur+1` until `cur == total`. `[FAUST:end]`
  carries `cmd=` (+ `kind=` where relevant), `page=cur/total`, and `count=<total rows>`
  (the full unpaged row count, so BCH can show a total without walking every page).
  Page size is 20 rows. An empty result still sends `[FAUST:end] … page=1/1 count=0`.
- **Numbers are bare** — `pct=54.2` not `54.2%`; `decay=3600` seconds, not
  `"1h"`. BCH parses formatting client-side.
- **Wire-safe labels** — no spaces in token values; use `_`→space on display
  (the Uriel convention). Avoid `=`, `;`, `:` inside values.
- **`region=` has ONE canonical "no region" sentinel: `-`.** Every region-bearing line
  (`positions`, `castleinfo`, `castles`, `decay`, `plots`, plus the `stats regions` / `regiondaily`
  region buckets) emits `-` for the open world / out-of-bounds / unmapped (and otherwise the wire-safe
  region name). As of 0.10.0 the literal `None`/`Unknown` no longer leak through — test only for `-`
  (or empty) as "no region."
- **Errors:** `[FAUST:err] code=<code> [feature=<f>] [item=<guid>] [qty=<n>] [secs=<n>]`
  with `code` ∈ `disabled | noaccess | cooldown | cost | notready | notfound | badtarget |
  blocked | schedule | pvp | window | locked | notnear | ratelimit`. BCH surfaces a friendly message and the relevant detail:
  - `cost` → `item` + `qty` (the price). `cooldown` / `window` → `secs` (time until reusable;
    `window` = a per-period usage allowance is spent, locked until the period rolls over).
  - `blocked` → `secs` until an admin countdown expires (`secs=-1` = blocked indefinitely until an
    admin unblocks). `schedule` → `secs` until the next time-of-day window opens.
  - `pvp` → the feature is disabled for this server's game mode (PvE/PvP-only).
  - `ratelimit` → the requester hit the server's per-player anti-spam floor (`RateLimitSeconds`); `secs` =
    seconds until the next query is allowed. Global (not per-feature); admins are normally exempt.
  - `locked` → the player hasn't met the feature's unlock criterion; `need` ∈ `bosskill | finalboss
    | grant` hints what opens it (defeat a specific V Blood / defeat Dracula / be admin-granted).
  - `notnear` → the player isn't within range of the required object; `item` = the object's
    PrefabGUID, `dist` = the required radius in metres (the feature is tied to a place).
  These admin-control codes (ApiVersion ≥5) are additive; an older BCH can show a generic message.

---

## 5. What BCH builds on its side (for reference — lives in the BCH workspace)

- `Services/Faust/FaustProtocolService.cs` — probe `.faust api version`, presence
  gating, parse `[FAUST:version]` into a feature→(access,cost,cooldown) map.
  Mirror `BeelzProtocolService` / `UrielProtocolService`. Register its `Tick` on
  `CoreUpdateBehavior.Actions`.
- A **FAUST** tab group (gated on presence) + per-feature panels/overlays; admin
  sub-tabs disabled-not-hidden when `access=admin` and the player isn't admin.
- Per-query **cost display** on buttons (from the handshake map) + handling of
  `[FAUST:err] code=cost|cooldown|noaccess`.
- #8/#9: chart widgets in the UniverseLib framework consuming the Server-Stats series —
  `playtime`/`concurrency`, the activity-analytics (`hours` + `hoursplayers`, `daily`,
  `newplayers`, `sessions`, `weekdays`, `pdaily`), population rollups, the `players`
  roster, and the §9 drill-downs (`newplayers roster`, `sessions timeline` Gantt,
  `activegrid`). Consumed in Raphael v0.50.0.
- §10 (region/roster): pivot the By-region chart to **fill %** (`castles / plots`) and
  add the `regiondaily` by-date per-region table + per-region trend; the `newplayers roster`
  auto-gains Playtime + Castles columns from the new `nprow` tokens. Gate on `api ≥ 15`.
- **Heat map:** a player-position **heat-map viz** consuming `.faust api heatmap`
  (`[FAUST:hmhead]` header + packed `[FAUST:hmrow]` density cells) — per-player and
  server-wide, mapped from cell indices to world space via the header's `cell` size.
  Gate on `api ≥ 16` / the `heatmap` handshake token. (Raphael-side, in progress.)
- **ApiVersion 18 (Faust 0.16.0) — to build, gate each on `api ≥ 18` / its handshake token:**
  - **Boss board** (`bosses` token): a **Boss Status** tab/panel consuming `.faust api bosses`
    (paged `[FAUST:boss]` rows) + a single-boss lookup `.faust api boss <name|guid>` (one `[FAUST:boss]`,
    no end trailer — disambiguate by the in-flight query, like `castleinfo`). Render `status=up` bosses
    with location (x/z → reuse the map overlay), region, an **HP bar** (`hppct`, with `hp`/`hpmax` tooltip)
    and level; render `status=down defeated=1` as a "defeated / not spawned" row (no location). Prettify
    `name` by `guid`. §B1.
  - **Kill leaderboards** (`kills` token): two boards — `.faust api kills [days]` (`[FAUST:kill]`: top
    killers, `kills` + `pvp`) and `.faust api bosskills [days]` (`[FAUST:bosskill]`: per-boss defeat
    counts). Offer a **today / this week / all-time** toggle (`days=1` / `7` / `0`). Good source for the
    "interesting server leaderboards" idea. §B2.
  - **World-asset map** (`worldscan` token): a filterable **asset map/list** consuming `.faust api worldscan`
    (`[FAUST:asset]` rows). Build filters for type (units/nodes), prefab id, **blood type**, and a **blood
    quality ≥ N** slider; plot `x`/`z` on the map overlay (reuse the positions overlay) and show blood
    type/quality on unit pins. Honor the `[FAUST:note] truncated=1` line (warn "filter to see more"). Drive the
    whitelist from Faust → Admin via `.faust admin worldscan add/remove/list/clear/seed` (chat). Send the
    filter `spec` as one space-free `key=value,key=value` token (VCF 0.10.4).
  - **§15a gate display:** the `[FAUST:access]` row now carries `cd`/`window`/`period`/`maxuses`/
    `nearprefab`/`neardist` — surface them in the Faust → Admin: Oversight table (read-only) alongside cost.
  - **Live config editor (chat, no wire — §3b):** add cyclers/inputs in Faust → Admin that send
    `.faust admin set/setglobal/resetcfg …` (mirror the existing block/schedule/grant controls) and read
    back the plain-text ack. This is how Raphael actually **changes** the gates from the UI.

---

## 6. Change discipline (living-contract rule, both directions)

Whenever Faust changes a `.faust` command, a `[FAUST:*]` reply shape, a config
key BCH reflects, or the feature set: **bump `api` when the wire grows**, update
this doc in the same commit, and ping BCH so it can update its reader. BCH gates
new UI behind `api >= N` so version skew degrades gracefully rather than breaking.

---

## 7. 0.16.0 / ApiVersion 18 — Raphael migration checklist

Everything that changed in this batch, in one place. Gate new UI on `api ≥ 18` / the relevant handshake
token. Detailed shapes are in the sections referenced.

**🔴 Breaking — Raphael MUST change its sender:**
- **Config editor syntax (§17 / §3b).** `.faust admin set`/`setglobal` no longer take space-separated
  `<setting> <value>` args (the server's **VCF 0.10.4** has no `[Remainder]` and matches by exact arg count,
  so that form errors "Too many parameters"). Send the value list as **one space-free token** of
  `setting=value` pairs, comma-joined: `set castleinfo costitem=862477668,costqty=100,cooldown=30` /
  `setglobal heatmapenabled=true`. This is the *only* breaking change.

**🆕 New features to build (each its own handshake token + gate):**
- **`worldscan`** (§C1) — filterable map of NPC units (+ blood type/quality) and resource nodes; whitelisted,
  cached/rate-limited. Filter `spec` is the same space-free `key=value,…` token form. See the §5 build notes.
- **`bosses`** (§B1) and **`kills`** (§B2) — already delivered/consumed; no change beyond §16 below.
- **Prefab lookup helper (admin chat, no wire):** `.faust admin prefab <id|nameFragment> [page]` → resolves a
  GUID to its dev-name, or searches the catalog by partial name → `<guid> <name>` rows. Optional but handy —
  Raphael can add a **"prefab search"** box in the admin UI so admins find GUIDs for the worldscan whitelist /
  item cost / proximity fields without an external dump. Drive it like the other admin commands (inject,
  read the plain-text reply lines).

**🟢 Wire additions you can now consume (additive — older Raphael just ignores them):**
- **`[FAUST:access]` gate tokens (§15a):** `cd`/`window`/`period`/`maxuses`/`nearprefab`/`neardist` between
  `cost` and `granted` → show the full per-feature gate picture in Admin: Oversight.

**🔧 Server-side fixes — mostly no Raphael change, but note the behavior shifts:**
- **§13 — `.faust admin data status` works again.** It was throwing server-side (512-byte reply cap), so the
  "Data status" button got nothing; now chunked. Other admin readouts hardened the same way.
- **§14 — `objectscan` retired.** Gone from the handshake, `[FAUST:access]`, and `.faust admin status`.
  Raphael already ignores it.
- **§16 — boss board no longer emits limbo coords.** Pooled/off-map bosses now report `status=down` with **no**
  `x`/`z` (instead of the bogus `~10000,10000`). Raphael can **drop its ±5000 client guard** — every `up` row
  carries a real on-map position.
- **§18 — boss classification may shift.** Lazy-spawned bosses read `down` until placed; a server-side
  diagnostic `.faust admin bossdiag` exists to tune this. No wire-shape change.
- **`clanmembers` / `boss` arg form.** Both take a **single token** now (VCF 0.10.4): send the wire-safe
  (`_`-encoded) clan name with an optional **separate** `int page` (`clanmembers Blood_Lords 2`), and the boss
  GUID or wire-safe name for `boss`. (No more packing a page into the name string.)

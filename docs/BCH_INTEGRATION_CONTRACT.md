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
    plotavailability=<acc>:<cost> objectscan=<acc>:<cost> castleresources=<acc>:<cost> stats=<acc>:<cost> \
    allcastles=<acc>:<cost> decaywatch=<acc>:<cost> clans=<acc>:<cost>
```

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

> **Status (ApiVersion 12):** as of Faust 0.13.0, **every feature ships AdminOnly by default** (Faust is
> an admin tool first; admins grant pieces to players per server) — so a fresh handshake shows
> `…=admin:…` for all tokens to a non-admin. New in 12: the `stats players` roster (§7 below) and a
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
    state=<unclaimed|sealed|fueled|decaying> decay=<secondsLeft> online=<0|1> lastonline=<unixUtc>
```
- `state`: `unclaimed` (no heart), `sealed` (frozen — never decays), `fueled` (has blood
  essence / time remaining), `decaying` (out of fuel, ticking down).
- `decay`: seconds of fuel remaining; **`-1` when `state=sealed`** (infinite). For an unclaimed
  plot, `owner=_ steam=0 online=0 lastonline=0`.
- `size`: territory block count (10×10 build blocks) — a proxy for plot size.
- `lastonline`: Unix UTC seconds (from `User.TimeLastConnected`); `0` = unknown.
- Errors: `badtarget` (token isn't here/nearest/int), `notfound` (no such territory).

### `plotavailability` (#4) — IMPLEMENTED
`.faust api plots [page]` — open (heart-less) territories, largest first.
```
[FAUST:plot] tindex=<int> size=<blocks> region=<wire_name>
…rows…
[FAUST:end] cmd=plots page=<cur>/<total> count=<n>
```

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

### `objectscan` (#5)
Default **Free / client-side** (BCH reads nearby entities itself). Only implement
a server command if the admin prices it:
`.faust api objects <token>` →
```
[FAUST:object] guid=<prefab> kind=<resource|container|…> label=<wire_safe> dist=<m> [yield=<…>]
[FAUST:end] cmd=objects page=1/1
```
Chest **contents** only for own/clan containers; never enemy (not replicated).

### `castleresources` (#6) — IMPLEMENTED
`.faust api resources <here|nearest|tindex> [page]` — admin-default, PvP-sensitive. Sums every
container's contents in the castle on the target territory (containers + stations connected to the
heart). A summary header (page 1), then one paged `[FAUST:item]` row per distinct item type.
```
[FAUST:res] tindex=<int> owner=<wire_name> steam=<id> containers=<n> totalitems=<n> distinct=<n>
[FAUST:item] guid=<int> qty=<n> name=<wire_name>
…rows (qty-descending)…
[FAUST:end] cmd=resources page=<cur>/<total> count=<distinct>
```
- `containers` = containers that held ≥1 item; `totalitems` = grand total; `distinct` = item types.
- An **unclaimed** territory (no heart) → `code=notfound`. An empty (claimed) castle → header with
  zeros + a `count=0` end. `name` is the item's prefab dev-name (BCH may prettify by `guid`).
- This is the powerful raid-intel feature: defaults to **AdminOnly**, and is a natural one to price
  (`CostItemGuid`) or PvP-gate (`Availability=PvPOnly`) via the admin-control axes.

### `stats` (#8) — IMPLEMENTED (`playtime`, `concurrency`, `hours`, `daily`, `newplayers`, `sessions`)
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
(24 buckets), server-wide or for one player. Single line, no trailer:
```
[FAUST:hours] scope=<server|steamId> h00=<min> h01=<min> … h23=<min>
```
- Sessions are **sliced at hour boundaries**, so a session spanning 22:30→01:15 feeds hours 22/23/0/1
  — a true "when is the server / this player active" profile, not just a connect-time tally.

`.faust api stats daily [days=14]` — per-day **DAU** (distinct players online) and **play-minutes**
for the last N days (clamped 1–90), oldest→newest, un-paged:
```
[FAUST:daily] day=<unixUtcMidnight> dau=<int> minutes=<int>   ; one row per day in the window
[FAUST:end] cmd=daily count=<n>
```
- Playtime is sliced at UTC midnight; a player counts toward a day's DAU if any session overlapped it.
  Every day in the window emits a row (zero-activity days included), so `count` = the day span.

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
[FAUST:region] name=<wire_name> players=<n> castles=<n>
…rows (castles-descending)…
[FAUST:end] cmd=regions page=<cur>/<total> count=<n>
```
- `players` = **online** players currently in that region (offline players have no position); `castles` =
  claimed castles in it. `name=-` is the open-world / no-region bucket (the canonical region sentinel).

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
  (`positions`, `castleinfo`, `castles`, `decay`, `plots`) emits `region=-` for the open world /
  out-of-bounds / unmapped (and otherwise the wire-safe region name). As of 0.10.0 the literal
  `None`/`Unknown` no longer leak through — test only for `-` (or empty) as "no region."
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
- #5: read nearby objects **client-side** (reuse `SharedContainerDetector`
  pattern) when Free; call `.faust api objects` only if the server prices it.
- #9: a small chart widget in the UniverseLib framework consuming `[FAUST:stat]`
  time-series.

---

## 6. Change discipline (living-contract rule, both directions)

Whenever Faust changes a `.faust` command, a `[FAUST:*]` reply shape, a config
key BCH reflects, or the feature set: **bump `api` when the wire grows**, update
this doc in the same commit, and ping BCH so it can update its reader. BCH gates
new UI behind `api >= N` so version skew degrades gracefully rather than breaking.

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
    plotavailability=<acc>:<cost> objectscan=<acc>:<cost> castleresources=<acc>:<cost> stats=<acc>:<cost>
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

> **Status (ApiVersion 2):** the four shapes below are **IMPLEMENTED** and live as of
> Faust 0.2.0 — these are the contract, not a proposal. Fields flagged *(pending)* are emitted
> with a sentinel until the persistence subsystem lands; the token is already on the wire so
> BCH can build the full panel now. `objectscan` (#5), `castleresources` (#6), and `stats` (#8)
> are still proposed (later sections / design build order).

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

### `playerinfo` (#3) — IMPLEMENTED (time-series pending)
`.faust api pinfo <steamIdOrName>` — **self always allowed**; querying *others* is gated by the
`playerinfo` feature access (AdminOnly by default). Name lookups must be unique (exact name wins).
```
[FAUST:player] steam=<id> name=<wire_name> online=<0|1> lastonline=<unixUtc> \
    firstseen=<unixUtc> sessions=<n> playmins=<total> freq=<perWeek> peakhour=<0-23>
```
- `online`, `lastonline` are **live now**. `firstseen`, `sessions`, `playmins`, `freq`,
  `peakhour` are **`-1` (not yet tracked)** until the FaustStore session/time-series persistence
  lands (design §6) — the game only stores the *last* connect time. The fields are on the wire so
  BCH can render the panel against the final shape today.
- Hover-to-identify is a **BCH client-side** mechanic (find the player entity under the cursor,
  read SteamID/name off replicated state) → then BCH calls this command.

### `playerpositions` (#1) — IMPLEMENTED (data); rendering is BCH-side
`.faust api positions [page]` — admin-default. One row per **online** player.
```
[FAUST:pos] steam=<id> name=<wire_name> x=<f> z=<f> tindex=<int>
…rows…
[FAUST:end] cmd=positions page=<cur>/<total> count=<n>
```
- `x`/`z` are world coordinates (one decimal). `tindex` is the territory the player is standing
  in, or `-1` in the open world.
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

### `castleresources` (#6)
`.faust api resources <token>` — admin-default, PvP-sensitive.
```
[FAUST:res] tindex=<int> owner=<name> total=<n> \
    item=<guid>:<qty> item=<guid>:<qty> …    (or paged [FAUST:res] rows + [FAUST:end])
```

### `stats` (#8)
`.faust api stats <kind> <page>` — `<kind>` ∈ `players|kills|playtime|concurrency`.
```
[FAUST:stat] kind=<kind> rank=<n> name=<name> value=<num> [t=<unixUtc>]   ; leaderboard rows
[FAUST:stat] kind=concurrency t=<unixUtc> avg=<f>                          ; time-series points (for #9 graphs)
[FAUST:end] cmd=stats kind=<kind> page=<cur>/<total>
```
BCH renders leaderboards as tables and concurrency/time-series as **graphs** (#9).

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
- **Errors:** `[FAUST:err] code=<code> [feature=<f>] [item=<guid>] [qty=<n>] [secs=<n>]`
  with `code` ∈ `disabled | noaccess | cooldown | cost | notready | notfound |
  badtarget`. BCH surfaces a friendly message and (for `cost`/`cooldown`) the
  price / time left.

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

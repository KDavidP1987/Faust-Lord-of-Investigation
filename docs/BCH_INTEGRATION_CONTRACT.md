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

> These shapes are **proposed** — finalize each as you build the feature, and
> update this doc + bump `api`. Names/fields are the starting contract.

### `castleinfo` (#2)
`.faust api castleinfo <token>` — `<token>` = `here` (aimed/standing plot) | a
territory index | `nearest`.
```
[FAUST:castle] tindex=<int> owner=<name> steam=<id> heart=<lvl> \
    state=<sealed|decaying|raidable|unclaimed> decay=<secondsLeft> online=<0|1> lastonline=<unixUtc>
```

### `plotavailability` (#4)
`.faust api plots <page>` — free territories, largest first.
```
[FAUST:plot] tindex=<int> size=<blocks> region=<name> center=<x>,<z>
…rows…
[FAUST:end] cmd=plots page=<cur>/<total>
```

### `playerinfo` (#3)
`.faust api pinfo <steamIdOrName>` — self always allowed; others gated.
```
[FAUST:player] steam=<id> name=<name> online=<0|1> lastonline=<unixUtc> \
    firstseen=<unixUtc> sessions=<n> playmins=<total> freq=<perWeek> peakhour=<0-23>
```
Hover-to-identify is a **BCH client-side** mechanic (find the player entity under
the cursor, read SteamID/name off replicated state) → then BCH calls this command.

### `playerpositions` (#1)
`.faust api positions` — admin-default. One row per online player.
```
[FAUST:pos] steam=<id> name=<name> x=<f> z=<f> region=<name>
…rows…
[FAUST:end] cmd=positions page=1/1
```
**Rendering is the open problem** (see design §8). BCH may draw these on its own
map overlay, or Faust may drive MapIcon reveal server-side — decide via spike.

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
  MUST carry `cmd=` (+ `kind=` where relevant) and `page=cur/total`.
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

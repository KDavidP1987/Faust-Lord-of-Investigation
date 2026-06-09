# Faust, Lord of Investigation — Design

> Server-side V Rising mod that answers on-demand investigation queries (players,
> castles, plots, objects, server activity), gated per-feature and optionally
> charged an item cost, delivered as chat commands and as structured `[FAUST:*]`
> data for BloodCraftHub (BCH) to render.
>
> Companion: `BCH_INTEGRATION_CONTRACT.md` (the BCH-facing wire/command contract).
> Reference template: KindredCommands (`…\LearningMods\KindredCommands-main\`).

---

## 1. The lens — why most of this is server-side

The V Rising **client is spatially culled**: it only receives entities/data near
the local player. (Proven in BCH's own code — `SharedContainerDetector` scans by
distance from the local character; the player roster query is culled too.) The
**server** holds the authoritative, global, persistent world. So:

- **Global / other-player / cross-map / historical data** → only the server has
  it → Faust must gather and send it.
- **Near-player / on-screen / own data** → the client can read it locally (BCH
  already does this) → no server needed *unless the admin wants to gate/charge it*.
- **All rendering** (popups, overlays, map markers, graphs) → BCH, client-side.
- **All gating/cost** → server-enforced (a client can't be trusted to gate or
  charge itself).

### The cost mechanic reshapes the split

You **cannot make a client charge itself**. So any feature an admin wants to gate
or price **must be server-mediated**: BCH asks Faust, Faust verifies access +
cost + consumes the item, then answers. Each feature therefore has a **Delivery**
choice:

- **Free** — BCH reads replicated state locally. Instant, but **un-gateable and
  un-chargeable**. Only possible for near-player/own-data features.
- **Server-mediated** — routed through Faust. Gateable, chargeable, audit-able.
  Required for anything global, and for anything the admin wants to price.

---

## 2. Feature evaluation (the 9 ideas)

| # | Feature | Verdict | Notes |
|---|---------|---------|-------|
| 1 | Show ALL players on the map (not just clan) | **Server + hard client render** | Distant players aren't streamed. Biggest unknown is *rendering on the game map* — likely server-side MapIcon/map-reveal, not BCH drawing on the minimap. PvP-sensitive. **Spike before committing.** |
| 2 | Castle plot info (owner, last-online, heart level, decay/availability timer) | **Server** | Reuse KindredCommands `CastleTerritoryService` (territory↔heart↔owner) + `User.TimeLastConnected`. Client only sees nearby hearts. |
| 3 | Player info (last login, frequency, play timeframes, progress) + hover-to-identify | **Hybrid** | Last-online exists server-side (`playerinfo`). Frequency/timeframes/playtime need Faust **persistence** (net-new). "Identify player under cursor" is a **client-side** BCH mechanic; the data behind it is server-side. Progress lives in Bloodcraft etc. (cross-mod). |
| 4 | Available plots by size (e.g. largest free) | **Server** | Whole-map territory table. `CastleTerritoryService` already has territory blocks; compute free territories + sizes. |
| 5 | Nearby/on-screen object info (resource node types; chest contents) | **Split** | Resource-node / object types of nearby entities = **client-side** (BCH `SharedContainerDetector` pattern). Chest **contents** pre-open = mostly NOT replicated (enemy inventories hidden); own/clan only. |
| 6 | Enemy castle total resources (PvP) | **Server + policy call** | Enemy container contents are deliberately hidden. Powerful raid intel — almost certainly Admin-only or a deliberate server opt-in. Faust sums containers server-side. |
| 7 | Admin controls + per-feature info gating | **Server (FOUNDATIONAL)** | The permission/cost layer every other feature hangs off. **Build first.** |
| 8 | Server stats (avg players/hour/day; top players by resource/kills/playtime) | **Server + persistence** | Time-series Faust must track over time. Top-by-resource = scan all castles/inventories. |
| 9 | Visual graphs in BCH | **Client render** | New chart widget in BCH's UniverseLib framework. Data from Faust `[FAUST:stats]`. |

**Client-only quick win:** #5 (resource-node overlay) needs no Faust at all *if
left Free* — BCH can ship it standalone. It only needs Faust if the admin wants
to charge for it.

---

## 3. The Foundation + permission/cost layer (#7) — build this first

Every BCH-facing query passes through a single gatekeeper before any data is
gathered.

```
FaustAccessGate.TryAuthorize(ctx, feature) -> Allow | Deny(code)
  1. feature.Enabled?              no  -> Deny("disabled")
  2. feature.Access:
       Off                         -> Deny("disabled")
       AdminOnly  & !ctx.IsAdmin   -> Deny("noaccess")
       Players                     -> ok
  3. Cooldown (per player, per feature):
       within window               -> Deny("cooldown", secondsLeft)
  4. Cost (if feature.CostItem != 0 and !(ctx.IsAdmin && feature.AdminsExempt)):
       inventory lacks Qty         -> Deny("cost", item, qty)
       else                        -> RESERVE (do not consume yet)
  -> Allow
```

Then the feature service runs and emits `[FAUST:*]` rows. **Consume the reserved
cost only after the query produced a real result** (so an empty/notfound lookup
isn't charged — emit `[FAUST:err] code=notfound` and refund/skip the charge).
Stamp the cooldown on a successful, charged query.

**Gatekeeper responsibilities:** resolve requester → admin check → access →
cooldown → cost reserve/consume/refund → optional audit log (who queried what,
when, charged-or-not — reuse the KindredCommands `AuditService` pattern) → emit
`[FAUST:err]` on any deny with a clear `code`.

This layer ships with **zero features** wired — just the gate, the config, the
handshake (`.faust api version`), and one trivial probe feature to prove the BCH
round-trip end to end.

---

## 4. Config schema

A global block plus one block per feature (BepInEx config; server-owner editable).

```ini
[Faust]
Enabled = true
AuditQueries = true          ; log who asked what (privacy/abuse trail)

# Per-feature template. <Feature> ∈ playerpositions, castleinfo, playerinfo,
# plotavailability, objectscan, castleresources, stats
[Faust.<Feature>]
Access          = Off | AdminOnly | Players   ; sensitive features default AdminOnly
Delivery        = ServerMediated | Free       ; Free only where BCH can read locally
CostItemGuid    = 0                            ; PrefabGUID hash; 0 = no cost
CostQuantity    = 0
CooldownSeconds = 0
AdminsExempt    = true                         ; admins skip access/cost when true
```

Recommended defaults: sensitive intel (`playerpositions`, `castleresources`,
other players' `playerinfo`) → `Access = AdminOnly`; benign/own-data
(`castleinfo` for your own plot, `objectscan`) → `Players`; everything starts
cost-free so a fresh install is usable, and admins opt into the toll.

The handshake advertises each feature's resolved `Access` + `Cost` so BCH renders
the price on the button and greys out features the player can't use.

---

## 5. Feature registry (the gateable units)

| Key | Idea | Default Access | Delivery | Reuse / source |
|-----|------|----------------|----------|----------------|
| `playerpositions` | #1 | AdminOnly | ServerMediated | all-player `Translation`; map-reveal spike |
| `castleinfo`      | #2 | Players | ServerMediated | `CastleTerritoryService`, `CastleHeart`, `TimeLastConnected` |
| `playerinfo`      | #3 | AdminOnly (others) / Players (self) | ServerMediated | `InfoCommands.playerinfo` + Faust persistence |
| `plotavailability`| #4 | Players | ServerMediated | `CastleTerritoryService` free-territory scan |
| `objectscan`      | #5 | Players | Free (or ServerMediated if charged) | BCH client-side; Faust only if priced |
| `castleresources` | #6 | AdminOnly | ServerMediated | container-sum; PvP policy |
| `stats`           | #8 | Players (server) / AdminOnly (leaderboards?) | ServerMediated | Faust persistence + scans |

(#7 is the gate itself; #9 is BCH-side rendering of `stats` data.)

---

## 6. Persistence (needed for #3 frequency/playtime + #8 stats)

The game stores only the *last* connect time. Everything time-series is Faust's to
keep. Foundation should stand up the data dir + a small `FaustStore` even though
the foundation itself doesn't need it.

- **Session log** — hook server connect/disconnect; append `{steamId, connectUtc,
  disconnectUtc}` records to JSON (`System.Text.Json`, under the plugin config/data
  dir — Bloodcraft/KindredCommands pattern).
- **Derived metrics** — from the session log: last-online, login frequency, play
  timeframes (hour-of-day histogram), total/again playtime, concurrent-players
  time-series → avg players per hour/day.
- **Event counters** — kills, etc., for leaderboards (#8): increment per event,
  snapshot periodically.
- Keep writes batched/periodic; never block the server frame on disk.

---

## 7. Build order (value vs risk)

1. **Foundation + permission/cost layer (#7)** — scaffold, config, `FaustAccessGate`,
   `.faust api version` handshake, one probe feature. End-to-end BCH round-trip.
2. **Reuse wins** — #2 castle/plot info, #3 last-online, #4 plot availability.
   Mostly re-packaging KindredCommands-style server state into `[FAUST:*]`.
3. **Client-only parallel** — #5 resource-node overlay in BCH (Free; no Faust)
   — can proceed independently in the BCH workspace.
4. **Persistence subsystem** — #3 frequency/playtime + #8 stats (the `FaustStore`).
5. **Hard / sensitive** — #1 map positions (after a rendering spike), #6 enemy
   resources (policy), #9 graphs (new BCH widget).

---

## 8. Open questions to resolve as you build

- **#1 map rendering** — can a server mod reveal/spawn MapIcons for arbitrary
  players to a chosen client, or must BCH draw on the map canvas? Spike first; it
  gates the whole feature.
- **Cost UX edge cases** — refund-on-empty (decided: don't charge empty), partial
  pages (charge once per query, not per page), and what happens if the item is
  consumed but the reply fails to send (reserve→confirm ordering).
- **#6 / #1 policy defaults** — confirm the safe defaults per server type (PvP vs
  PvE) before shipping; these are balance decisions, not just config.
- **Cross-mod progress (#3)** — "progress in game" likely means Bloodcraft data;
  decide whether Faust reads it or BCH composes Faust + Bloodcraft client-side.

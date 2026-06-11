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

---

## 9. Opportunity catalog (post-0.8 audit)

A 2026-06-10 audit of the V Rising server-side data surface (cross-referenced against
KindredCommands + Bloodcraft) against Faust's shipped features. The **control engine is complete**
(§3 + ADMIN_CONTROL's seven axes); what's thin is the **catalog of information** flowing through it.
Everything below is **game-native** (any server mod can read it from ECS) — Bloodcraft-specific
systems (levels, classes, professions, familiars, legacies) are deliberately **out of scope** unless
a future cross-mod integration is chosen.

Each item carries a **suggested default access** and the **knob** that turns "too much information"
into a balanced tool — the configurability story is the answer to every sensitivity concern (a
sensitive feature ships AdminOnly; opening it to players is a deliberate, per-server balance choice,
optionally limited by cost / cooldown / window / unlock / proximity).

### Tier A — Administrative / moderation (broadest access; the admin team's domain)
- **Decay watch** *(SHIPPED 0.9.0 — `decay`)* — claimed castles by soonest-to-decay + owner
  last-online. Abandoned-plot cleanup. AdminOnly. **On-demand, zero passive cost.**
- **Castle/clan footprint** — castle count + total territory blocks per player/clan (land-hogging).
  AdminOnly. On-demand (`CastleTerritory` + `UserOwner`/`ClanTeam`).
- **Clan roster (`claninfo`)** — name, members, roles, allies (`ClanTeam`, `ClanMemberStatus`,
  `TeamAllies`). AdminOnly default; Players opt-in on social servers.
- **New-player / churn / retention report** — from the existing session log (first-seen, who hasn't
  returned). AdminOnly. **Reads existing collection — no new passive cost.**

### Tier B — Player gameplay tools (deliberate, gated value)
- **Server status (`serverinfo`)** — server time, day/night, blood-moon, game mode (PvP/PvE), online
  count, decay-rate. Low sensitivity → a natural **free, player-facing** feature.
- **Boss/progression lookup** — V Bloods a player has defeated + max level (`UnlockedProgressionElement`;
  the death hook already detects boss kills for unlocks). Self = benign; others = AdminOnly.
- **Own/clan castle decay timer** — the Tier-A decay watch scoped to the caller's own hearts. Players,
  free; pure QoL.

### Tier C — PvP strategic intel (high value, high sensitivity — the cost/time/unlock cases)
- **Kills/deaths leaderboard + K/D (`stats kills`)** — needs a `DeathEventListenerSystem` kill counter
  feeding FaustStore (the patch already exists for unlock detection; extend it). **This is a new
  PASSIVE collector → must ship with a collection toggle (see §10).** Players as a board.
- **Soul-shard / relic tracker** — who holds / where dropped (`Relic`, `SoulshardService`). The poster
  child for the admin-controlled, per-feature model: AdminOnly, or grant it as priced/cooldowned PvP intel.
- **Enemy power read** — target gear-score / blood quality. AdminOnly, or PvP-priced.
- **Live combat board** — who's in PvP combat right now (`Buff_InCombat_PvPVampire`). AdminOnly / PvPOnly.

### Tier D — Faust's persistence superpower (only Faust can answer)
The game forgets everything but last-login; lean into time-series derived from the session log:
- **Concurrency heatmap by hour / day-of-week** — aggregate the concurrency series Faust already keeps.
  Players (event scheduling / "when is it busy"). **Reads existing collection.**
- **Per-player play-pattern** — usual days/hours online. AdminOnly for others (moderation / raid-window
  awareness). Reads existing collection.

**Recommended next after 0.9.0:** the **kill-tracking hook → `stats kills`** (Tier C) — it reuses the
existing death patch, lights up the already-promised leaderboard, and is the first feature to exercise
the §10 collection controls (it's a *new passive collector*, so it must be admin-toggleable).

---

## 10. Collection / performance controls (the "what does Faust collect" axis)

Distinct from **who may READ** a feature (access/cost/etc., §3–§5), the admin team also controls
**what Faust passively COLLECTS in the background** — so Faust never becomes a server-performance
concern, independent of how widely its data is exposed.

**Key distinction:**
- **On-demand queries (the vast majority)** — castleinfo, plots, castles, decay, positions, resources,
  pinfo's live fields, claninfo, footprint, serverinfo, boss-lookup — read live ECS state when asked.
  They cost **nothing** when idle; no collection control is needed (or possible).
- **Passive collectors (the few)** — the session/time-series store (§6) is the one subsystem that
  *accumulates over time*: connect/disconnect logging + concurrency sampling (and, when built, the
  kill counter). These are the only things that consume CPU/memory/disk while nobody is querying, so
  they are the things admins must be able to **bound or switch off**.

**The `[Faust.Collection]` config block (0.9.0):**
- `SessionTracking` *(bool, default true)* — master switch for connect/disconnect session logging.
  Off ⇒ no sessions persisted; pinfo's playtime/sessions/frequency/peak-hour and the playtime
  leaderboard return the `-1` "not tracked" sentinel. (Faust becomes a pure live-query tool.)
- `ConcurrencySampling` *(bool, default true)* — whether to sample the online count on each
  connect/disconnect (the population series behind `stats concurrency`). Independent of session logging.
- `MaxConcurrencyPoints` *(int, default 4000)* — hard cap on retained samples (oldest trimmed); bounds
  memory + `sessions.json` size. `0` disables sampling.
- `SessionRetentionDays` *(int, default 0 = forever)* — prune sessions older than N days (on connect +
  at load); bounds long-term growth and keeps derived windows recent.

**Design rule for any NEW passive collector** (e.g. the kills counter): it MUST land with its own
`[Faust.Collection]` toggle (default on, cheaply off) and, where it accumulates unboundedly, a cap or
retention knob — so the admin retains full control over Faust's passive footprint as the feature set
grows. Per-frame work in a Harmony hook stays O(events), try/catch-guarded, and no-ops when its
collector is disabled (mirroring the unlock death-hook's `TracksUnlocks` gate).

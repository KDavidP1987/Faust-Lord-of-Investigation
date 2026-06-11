# Faust, Lord of Investigation

![Faust, Lord of Investigation](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/docs/img/faust-cover.jpg)

A **server-side** mod for V Rising dedicated servers that answers on-demand **investigation /
information** queries — about players, castles, plots, objects, and server activity — delivered as
structured data for its companion client **[Raphael, Lord of Wisdom](https://discord.gg/usC9QgBrXK)**
to render as in-game panels, overlays, dashboards, and graphs (and also available as plain `.faust`
chat commands).

> **Faust is designed to be run alongside Raphael.** Raphael is the client that turns Faust's data into
> a usable UI — the Player Positions map, the All-Plots castle table, the Server Stats dashboards, the
> Clans tab, and the player-activity charts. Faust is the **server brain**; Raphael is the **screen**.
> You *can* use Faust on its own from chat, but the experience these two are built for is the pair.

Faust is, first and foremost, an **administrative and moderation tool** — the global, authoritative
view of your server. On top of that, admins can **grant** parts of it to players: as a **strategic
tool** on PvP servers and a **community-building tool** on PvE servers. Everything is **controlled per
feature** — your server's admins decide what, if anything, players can see (Off / Admin-only / Players),
and may attach an optional **item cost** — the Faustian toll — a cooldown, an unlock, or a location
requirement when they want to. Sensitive intel defaults to admin-only. Admins also control what Faust
**collects in the background**, so it never costs server performance.

---

## ⚠ Pre-1.0 — for testing

**This is a pre-1.0 release, published for testing.** The full investigation feature set and the
per-feature admin-control surface are implemented and working in-game, but Faust is still being
validated and refined — **by running it, you're helping test it.** **Back up your server save** before
adding any mod, and expect commands, config keys, wire shapes, and behavior to keep evolving before 1.0.

**Run it with [Raphael, Lord of Wisdom](https://discord.gg/usC9QgBrXK).** Faust ships its data to
Raphael; that pairing is what's being tested, and it's how you get the UI Faust is built for.

### 💬 We want your feedback

Faust is **all about information** — so tell us whether the information is *right, useful, and
presented well.* Is a number wrong or confusing? A chart missing a cut you'd want? An intel view you
wish existed? A feature you'd like to grant players differently? **That feedback directly shapes 1.0.**

- **[The Shadow Realm Discord](https://discord.gg/usC9QgBrXK)** — the **primary** place for bug reports,
  feedback on the data, and **requests for new features / enhancements**. Come tell us what you need.
- **GitHub issues:** https://github.com/KDavidP1987/Faust-Lord-of-Investigation/issues — written-up
  bugs and ideas welcome too.

---

**Repo:** https://github.com/KDavidP1987/Faust-Lord-of-Investigation
**Status:** pre-1.0 · **server-side only** · investigation feature set complete, in live testing.

## What it does

Faust holds the **authoritative, global view** of the server that a game client can't see (clients
only receive what's near the local player). It gathers that view, **gates** it per feature,
optionally **charges an item cost** for it, and ships it to the BloodCraftHub UI (or answers in
chat). Best paired with **BloodCraftHub** for point-and-click panels, overlays, and graphs — but
every query also works from chat.

**Investigation queries:**

- **Castle & plot info** — a plot's owner, region, size, decay state & time, and the owner's
  online / last-online.
- **Plot availability** — open building plots across the whole map, largest first.
- **Full castle map** — every territory (claimed + open) in one list: owner, region, size, state,
  decay, online *(admin-default)*.
- **Decay watch** — claimed castles ranked by who's closest to decaying (with the owner's last-online):
  spot abandoned plots and cleanup targets *(admin-default)*.
- **Player info** — online state, last-online, and (tracked by Faust over time) total playtime,
  session count, logins per week, and peak play hour.
- **Online player positions** *(admin-default, PvP-sensitive)* — now with each player's region.
- **Enemy castle resource totals** — sum everything stashed in a castle *(admin-default; a natural
  one to price or restrict to PvP)*.
- **Server stats** — a playtime leaderboard and a server-population history, ready for graphs.
- **Activity analytics** — chart-ready breakdowns of when people play (by hour of day **and day of
  week**), daily active players + play-minutes, new arrivals per day, how long sessions last, and a
  **per-player daily trend** — server-wide or per player.
- **Clan composition** — how many players are **in clans vs going solo** (with the currently-online
  split), plus a per-clan roster: size, who's online, **how many castles they hold**, and the leader
  *(admin-default)*.
- **Population health** — active-player counts (DAU/WAU/MAU), retention, who's drifting away, the
  population peak/average, and where everyone is (players + castles **per region**).

**Every feature is independently controlled by the admin.** You decide, per feature, what (if anything)
players can see and on what terms. Per feature you can set:

- **Who** — off, admin-only, or all players.
- **A price** — an item cost per use (the Faustian toll).
- **A time-lock** — a flat cooldown, or a usage window per period (e.g. "a 10-minute window, once
  per day").
- **PvP/PvE** — usable only on the matching server mode.
- **A progression unlock** — opens only after a player defeats a configured V Blood or Dracula.
- **A place** — usable only within range of a configured object (an altar/station/landmark).

Admins can also **block or schedule** any feature live (a countdown or a daily time window) with no
restart.

## Installation

Faust is a **server-side** mod — install it on your **V Rising dedicated server**, not on player
clients.

### Via Thunderstore Mod Manager / r2modman (recommended)

1. Open the mod manager and select **V Rising → your dedicated-server profile**.
2. Search for **Faust** under the Mods tab → **Install**.
3. Ensure its dependencies are installed in the same profile (they install automatically):
   **BepInExPack_V_Rising** and **VampireCommandFramework**.
4. Launch the server through the mod manager's profile.

### Manual install

1. Install [**BepInExPack V Rising**](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/)
   (1.733.2 or compatible) into your dedicated server.
2. Install [**VampireCommandFramework**](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/)
   into `BepInEx/plugins/`.
3. Drop `Faust.dll` into `BepInEx/plugins/`.
4. Start the server once to generate `BepInEx/config/kdpen.Faust.cfg`, then edit and restart.

> **Stop the server before replacing `Faust.dll`** — a running server file-locks the DLL.

## Dependencies & compatibility

| Mod | Version | Role |
|---|---|---|
| [**BepInExPack V Rising**](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/) | 1.733.2 | Loader (hard dependency) |
| [**VampireCommandFramework**](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/) | 0.10.x | Chat-command framework (hard dependency) |

**Companion client — [Raphael, Lord of Wisdom](https://discord.gg/usC9QgBrXK):** the client-side mod
that renders Faust's data into in-game UI — the Player Positions map, the All-Plots castle table, the
Server Stats dashboards (population / recency / peak / regions), the Clans tab, and the per-player
activity charts. **Faust is built to be used with Raphael** — it's the screen for Faust's server brain.
Technically Raphael isn't a hard dependency (every Faust feature also works from `.faust` chat), but the
intended experience is the pair. Grab Raphael from **[The Shadow Realm](https://discord.gg/usC9QgBrXK)**.

Faust is also a sibling to the author's other server-side mods **Uriel, Lord of Hosts** and
**Beelzebub, Lord of Gluttony**; each is independent.

## Commands

All commands are chat commands prefixed with `.faust`. Type `.faust` for an overview or
`.faust help [players|castles|server|admin]` for a topic menu. Most queries are intended to be
driven by the BloodCraftHub UI, but each works from chat too.

| Command | What it does |
|---|---|
| `.faust api castleinfo <here\|nearest\|tindex>` | A plot's owner, region, size, decay state & time, owner online/last-online |
| `.faust api plots [page]` | Open building plots across the map, largest first |
| `.faust api castles [page]` | Every territory (claimed + open) — owner, region, size, state, decay, online *(admin-default)* |
| `.faust api decay [page]` | Claimed castles ranked by soonest-to-decay + owner last-online *(admin-default)* |
| `.faust api pinfo <name\|steamId>` | A player's online state, last-online, **playtime, sessions, logins/week & peak hour** (yourself always; others admin-gated) |
| `.faust api positions [page]` | Locations (and regions) of online players *(admin-default)* |
| `.faust api resources <here\|nearest\|tindex> [page]` | Total resources stashed in a castle *(admin-default; great to price/PvP-gate)* |
| `.faust api stats <playtime\|concurrency> [page]` | Playtime leaderboard / server population history |
| `.faust api stats hours [player]` | Activity by hour-of-day (24 buckets) — server-wide or one player |
| `.faust api stats daily [days]` | Daily active players + play-minutes for the last N days |
| `.faust api stats newplayers [days]` | New arrivals (first seen) per day |
| `.faust api stats weekdays [player]` | Activity by day-of-week (Mon–Sun) — server-wide or one player |
| `.faust api stats sessions [player]` | Session-length spread (`<15m` / `15–60m` / `1–3h` / `3h+`) |
| `.faust api stats pdaily <player> [days]` | One player's daily play-minutes trend (last N days) |
| `.faust api stats population` | Active players (DAU/WAU/MAU) + retention + new-vs-returning |
| `.faust api stats recency` | How many players seen in last 24h / 7d / 30d vs dormant |
| `.faust api stats peak [days]` | Concurrency summary — peak / average / p95 / live count |
| `.faust api stats regions [page]` | Online players + claimed castles per map region |
| `.faust api stats players [page]` | Per-player activity roster (active today/week, last-seen, sessions, playtime, idle) |
| `.faust api clans [page]` | Clan composition — clanned vs independent + per-clan roster (size, online, castles, leader) *(admin-default)* |
| `.faust api version` | BloodCraftHub handshake — each feature's access + price (machine-readable) |
| `.faust api ping` | Connection test (`[FAUST:pong]`) |
| `.faust` · `.faust help [topic]` | Overview / topic-by-topic help |

Playtime/frequency stats accrue from the moment Faust is installed (the game only remembers your
*last* login, so Faust logs sessions over time). Each query obeys its per-feature access, item
cost, and cooldown (config below); an empty or not-found lookup is never charged.

## Configuration

`BepInEx/config/kdpen.Faust.cfg` — a global block plus one block per feature. Config changes take
effect on server restart.

| Section | Key | Default | Effect |
|---|---|---|---|
| Faust | Enabled | `true` | Master switch for the whole mod |
| Faust | AuditQueries | `true` | Log who asked what, when, and whether they were charged |
| Faust | RateLimitSeconds | `0` | Anti-spam: min seconds between a player's queries (0 = off). Stops a player hammering a query and stressing the server |
| Faust | RateLimitAdminsExempt | `true` | Admins skip the rate limit (so admin dashboards/paging aren't throttled) |
| Faust.Data | ResetSteamIds | *(empty)* | SteamIDs allowed to run `.faust admin data clear`/`wipe` (empty = any admin). For tiered admin teams |
| Faust.\<feature\> | Access | `AdminOnly` | `Off` / `AdminOnly` / `Players` — who may run this query (admin-only by default; grant to players per feature) |
| Faust.\<feature\> | Delivery | varies | `ServerMediated` (gateable/chargeable) or `Free` (client-local) |
| Faust.\<feature\> | CostItemGuid / CostQuantity | `0` / `0` | Item + amount charged per query (0 = free) |
| Faust.\<feature\> | CooldownSeconds | `0` | Flat per-player lockout between runs (e.g. pay, then locked 30 min) |
| Faust.\<feature\> | WindowSeconds / PeriodSeconds / MaxUsesPerPeriod | `0` | A usage window per recurring period — e.g. `600` / `86400` / `1` = a 10-min window, once per day |
| Faust.\<feature\> | Availability | `Always` | `Always` / `PvEOnly` / `PvPOnly` — gate on the server's game mode |
| Faust.\<feature\> | Unlock | `None` | `None` / `FinalBoss` (defeat Dracula) / `BossKill:<vbloodGuid>` — progression gate before use |
| Faust.\<feature\> | RequireNearPrefab / RequireNearDistance | `0` / `5` | Require the player to be within N metres of an object (by PrefabGUID) — tie the ability to a place (0 = anywhere) |
| Faust.\<feature\> | AdminsExempt | `true` | Admins skip access / PvP / proximity / cost / cooldown / window / unlock |

Features (`<feature>`): `playerpositions`, `castleinfo`, `playerinfo`, `plotavailability`,
`allcastles`, `decaywatch`, `objectscan`, `castleresources`, `stats`, `clans`. **Every feature defaults
to AdminOnly** — Faust is an admin tool first; open up whichever ones you want to share, per server.

### Collection controls — what Faust gathers in the background

Separate from *who can read* a feature, you control *what Faust passively collects*, so it never
costs server performance. Almost every query reads live state on demand (zero idle cost); only the
session/population history accumulates over time, and you can bound or switch it off:

| Section | Key | Default | Effect |
|---|---|---|---|
| Faust.Collection | SessionTracking | `true` | Log connect/disconnect over time. **Off** ⇒ no history: playtime / sessions / frequency / peak-hour and the playtime leaderboard report "not tracked" |
| Faust.Collection | ConcurrencySampling | `true` | Sample the online-player count (powers the population graph). Independent of SessionTracking |
| Faust.Collection | MaxConcurrencyPoints | `4000` | Cap on stored population samples (oldest trimmed); bounds memory + file size. `0` disables sampling |
| Faust.Collection | SessionRetentionDays | `0` | Auto-prune sessions older than N days (`0` = keep forever) — bound long-term growth on busy/long-lived servers |
| Faust.Collection | DataNamespace | *(empty)* | Empty = one shared dataset (kept across world wipes). Set a per-world name (e.g. `season3`) to keep each world's data separate |

Faust's data is stored in `BepInEx/config/Faust/` — **on the server, not in the world save — so it
survives a world wipe.** That's deliberate: the same players return, so their playtime/stats stay
relevant. When you *do* want to reset it (or it grows too large), use the **data commands** below.

### Live admin controls (no restart)

Admins can override features at runtime — these persist across restarts:

| Command | What it does |
|---|---|
| `.faust admin block <feature\|all> [minutes]` | Disable a feature now; with `minutes`, a countdown that auto-reopens |
| `.faust admin unblock <feature\|all>` | Clear a block / countdown |
| `.faust admin schedule <feature\|all> <HH:MM-HH:MM\|clear>` | Only allow use within a daily time window (server local time) |
| `.faust admin status [feature]` | Show each feature's effective block/schedule state |
| `.faust admin grant\|revoke <player> <feature>` | Hand-unlock / re-lock a feature for a player (overrides its `Unlock` criterion) |
| `.faust admin unlocks <player>` | Show a player's V-blood defeats + granted features |
| `.faust admin data status` | Footprint of collected data (counts, oldest record, disk size, namespace, retention) |
| `.faust admin data clear <days>` | Prune activity (sessions + population) older than N days, on demand |
| `.faust admin data wipe <activity\|unlocks\|usage\|all> confirm` | Reset a store — `unlocks` for a fresh world, `activity` to reset playtime/charts (`confirm` required) |
| `.faust admin showpositions <on\|off\|status>` | *Experimental* — put online players on the native in-game map (off by default; verify admin-only visibility on a test server first) |

A feature can also require a **progression unlock** before players may use it — set
`Unlock = FinalBoss` (defeat Dracula) or `Unlock = BossKill:<vbloodGuid>` per feature in the config.

## Feedback & community

Built and tested alongside the **The Shadow Realm** V Rising community, hand-in-hand with its companion
client **Raphael, Lord of Wisdom**. This is a **pre-1.0 testing release**, and your feedback — on whether
the information is accurate, useful, and well-presented, and on what intel you wish Faust surfaced —
**directly shapes what 1.0 becomes.** Bug reports, ideas, and enhancement requests all welcome.

- **The Shadow Realm Discord (primary — bugs, feedback & feature requests):** https://discord.gg/usC9QgBrXK
- **Raphael (companion client):** available from **The Shadow Realm** (link above)
- **GitHub issues / source:** https://github.com/KDavidP1987/Faust-Lord-of-Investigation/issues

## Acknowledgements

- **[VampireCommandFramework](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/) — by deca** — the chat-command framework (hard dependency).
- **[BepInEx](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/)** — the loader that makes V Rising modding possible.
- **[KindredCommands](https://thunderstore.io/c/v-rising/p/odjit/) — by odjit** — open-source territory/heart/owner and info-command patterns Faust builds on.

## License

Licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)** — copyright © 2026
Kristopher Penland. Faust adapts AGPL-licensed server-side techniques from odjit's KindredCommands
and is released under the same copyleft license; full source is on
[GitHub](https://github.com/KDavidP1987/Faust-Lord-of-Investigation).

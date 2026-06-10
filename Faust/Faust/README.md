# Faust, Lord of Investigation

![Faust, Lord of Investigation](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/docs/img/faust-cover.jpg)

A **server-side** mod for V Rising dedicated servers that answers on-demand **investigation /
information** queries — about players, castles, plots, objects, and server activity — delivered
as `.faust` chat commands and as structured data for the **BloodCraftHub** companion UI to render.

Faust is **not all-or-nothing, and it's not a cheat.** It surfaces information; **your server's admins
decide how much of it you can see.** Sensitive intel is **gated per feature** (admins choose Off /
Admin-only / Players) and can carry an **item cost** — the Faustian toll — a cooldown, an unlock
requirement, or a place you must stand near, so on PvP/competitive servers knowledge isn't free.
Many servers run Faust admin-only; others hand players select intel as a gameplay tool. Admins also
control what Faust **collects in the background**, so it never costs server performance.

---

## ⚠ Heads-up before you install

**Pre-1.0 — in active testing.** The full investigation feature set and the per-feature
admin-control surface are implemented; this build is being validated in game. By running it you're
helping test it. **Back up your server save** before adding any mod. Commands, config keys, and
behavior may still change before 1.0.

**Found a bug or have an idea?** The fastest path to a fix is the
**[The Shadow Realm Discord](https://discord.gg/usC9QgBrXK)** — the primary bug-report and
feedback channel. Written-up GitHub issues are welcome too.

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

**Every feature is independently controlled by the admin** — Faust is never all-or-nothing, and
intel is never free unless the admin makes it so. Per feature you can set:

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

**Optional companion (not a dependency):** the client-side mod
[**BloodCraftHub**](https://thunderstore.io/c/v-rising/p/TheShadowRealm/BloodCraftHub/) renders
Faust's data as in-game UI panels, overlays, and graphs. It is **not required** — every Faust
feature also works through `.faust` chat commands. Faust is a sibling to the author's
server-side **Uriel, Lord of Hosts** and **Beelzebub, Lord of Gluttony**; each is independent
and integrates with BloodCraftHub the same way.

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
| Faust.\<feature\> | Access | varies | `Off` / `AdminOnly` / `Players` — who may run this query |
| Faust.\<feature\> | Delivery | varies | `ServerMediated` (gateable/chargeable) or `Free` (client-local) |
| Faust.\<feature\> | CostItemGuid / CostQuantity | `0` / `0` | Item + amount charged per query (0 = free) |
| Faust.\<feature\> | CooldownSeconds | `0` | Flat per-player lockout between runs (e.g. pay, then locked 30 min) |
| Faust.\<feature\> | WindowSeconds / PeriodSeconds / MaxUsesPerPeriod | `0` | A usage window per recurring period — e.g. `600` / `86400` / `1` = a 10-min window, once per day |
| Faust.\<feature\> | Availability | `Always` | `Always` / `PvEOnly` / `PvPOnly` — gate on the server's game mode |
| Faust.\<feature\> | Unlock | `None` | `None` / `FinalBoss` (defeat Dracula) / `BossKill:<vbloodGuid>` — progression gate before use |
| Faust.\<feature\> | RequireNearPrefab / RequireNearDistance | `0` / `5` | Require the player to be within N metres of an object (by PrefabGUID) — tie the ability to a place (0 = anywhere) |
| Faust.\<feature\> | AdminsExempt | `true` | Admins skip access / PvP / proximity / cost / cooldown / window / unlock |

Features (`<feature>`): `playerpositions`, `castleinfo`, `playerinfo`, `plotavailability`,
`allcastles`, `decaywatch`, `objectscan`, `castleresources`, `stats`. Sensitive ones default to **AdminOnly**.

### Collection controls — what Faust gathers in the background

Separate from *who can read* a feature, you control *what Faust passively collects*, so it never
costs server performance. Almost every query reads live state on demand (zero idle cost); only the
session/population history accumulates over time, and you can bound or switch it off:

| Section | Key | Default | Effect |
|---|---|---|---|
| Faust.Collection | SessionTracking | `true` | Log connect/disconnect over time. **Off** ⇒ no history: playtime / sessions / frequency / peak-hour and the playtime leaderboard report "not tracked" |
| Faust.Collection | ConcurrencySampling | `true` | Sample the online-player count (powers the population graph). Independent of SessionTracking |
| Faust.Collection | MaxConcurrencyPoints | `4000` | Cap on stored population samples (oldest trimmed); bounds memory + file size. `0` disables sampling |
| Faust.Collection | SessionRetentionDays | `0` | Prune sessions older than N days (`0` = keep forever) — bound long-term growth |

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

A feature can also require a **progression unlock** before players may use it — set
`Unlock = FinalBoss` (defeat Dracula) or `Unlock = BossKill:<vbloodGuid>` per feature in the config.

## Feedback & community

Built and tested alongside the **The Shadow Realm** V Rising community. As a pre-1.0 mod, bug
reports and feedback shape what 1.0 becomes.

- **The Shadow Realm Discord (primary):** https://discord.gg/usC9QgBrXK
- **Issues / source:** https://github.com/KDavidP1987/Faust-Lord-of-Investigation

## Acknowledgements

- **[VampireCommandFramework](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/) — by deca** — the chat-command framework (hard dependency).
- **[BepInEx](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/)** — the loader that makes V Rising modding possible.
- **[KindredCommands](https://thunderstore.io/c/v-rising/p/odjit/) — by odjit** — open-source territory/heart/owner and info-command patterns Faust builds on.

## License

Licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)** — copyright © 2026
Kristopher Penland. Faust adapts AGPL-licensed server-side techniques from odjit's KindredCommands
and is released under the same copyleft license; full source is on
[GitHub](https://github.com/KDavidP1987/Faust-Lord-of-Investigation).

# Faust, Lord of Investigation

![Faust, Lord of Investigation](https://raw.githubusercontent.com/KDavidP1987/Faust-Lord-of-Investigation/main/docs/img/faust-cover.jpg)

A **server-side** mod for V Rising dedicated servers that answers on-demand **investigation /
information** queries — about players, castles, plots, objects, and server activity — delivered
as `.faust` chat commands and as structured data for the **BloodCraftHub** companion UI to render.

Sensitive intel is **gated per feature** (admins choose Off / Admin-only / Players) and can carry
an **item cost** — the Faustian toll — so on PvP/competitive servers, knowledge isn't free.

---

## ⚠ Heads-up before you install

**Pre-1.0 — early development.** The investigation queries (castle/plot info, plot availability,
player info, online positions) are live and confirmed working in game, each gated per feature with
an optional item cost. 0.3.0 adds session tracking for real playtime/frequency stats and a server
`stats` view (that persistence path is still being tested). By running it you're helping test it.
**Back up your server save** before adding any mod. Commands, config keys, and behavior will change
before 1.0.

**Found a bug or have an idea?** The fastest path to a fix is the
**[The Shadow Realm Discord](https://discord.gg/usC9QgBrXK)** — the primary bug-report and
feedback channel. Written-up GitHub issues are welcome too.

---

**Repo:** https://github.com/KDavidP1987/Faust-Lord-of-Investigation
**Status:** pre-1.0 · **server-side only** · foundation in place, investigation features in development.

## What it will do

Faust holds the **authoritative, global view** of the server that a game client can't see (clients
only receive what's near the local player). It gathers that view, **gates** it per feature,
optionally **charges an item cost** for it, and ships it to the BloodCraftHub UI (or answers in
chat). Planned investigation features (see the roadmap):

- **Castle/plot info** — owner, heart level, decay/raidable state, last-online.
- **Plot availability** — free plots across the map, largest first.
- **Player info** — last login, login frequency, play timeframes, total playtime.
- **All-player map positions** *(admin-default, PvP-sensitive)*.
- **Nearby object scan** — resource-node / container types around you.
- **Enemy castle resource totals** *(admin-default, PvP raid intel)*.
- **Server stats** — average concurrency, leaderboards (kills / playtime / resources), and
  time-series for graphs.

Every feature is **independently exposed** by the admin and can be priced — Faust is never
all-or-nothing, and intel is never free unless the admin makes it so.

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
| `.faust api pinfo <name\|steamId>` | A player's online state, last-online, **playtime, sessions, logins/week & peak hour** (yourself always; others admin-gated) |
| `.faust api positions [page]` | Locations of online players *(admin-default)* |
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
| Faust.\<feature\> | AdminsExempt | `true` | Admins skip access / PvP / cost / cooldown / window / unlock |

Features (`<feature>`): `playerpositions`, `castleinfo`, `playerinfo`, `plotavailability`,
`objectscan`, `castleresources`, `stats`. Sensitive ones default to **AdminOnly**.

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

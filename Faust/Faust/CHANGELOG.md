# Changelog

Condensed, player-facing changelog. Full technical history:
[GitHub](https://github.com/KDavidP1987/Faust-Lord-of-Investigation/blob/main/CHANGELOG.md).

Every release below is **pre-1.0** and built to run with the companion client **Raphael, Lord of Wisdom**.
Faust is in active testing — feedback and feature requests at the
[Shadow Realm Discord](https://discord.gg/usC9QgBrXK) shape 1.0.

## 0.16.5 (2026-06-13)

- **Fixed: held prisoners now show their blood type and quality.** In the castle-resources view, prisoners
  were listed with no blood type and a blank quality. Faust now reads a captured unit's blood from the right
  place, so each prisoner shows its actual blood type and quality %.

## 0.16.4 (2026-06-13)

- **New: time-windowed heat maps.** The player-position heat map can now be filtered by time — today, this
  week, this month, or any number of recent days — instead of only the all-time map. Works together with the
  existing per-player filter, so you can see one player's activity for just this week. The all-time map is
  unchanged. A new `RetentionDays` setting (default 30) controls how much per-day history is kept.

## 0.16.3 (2026-06-13)

- **Fixed: more bosses show their real map location, and none show a bogus off-map point.** Many bosses on the
  "down" half of the board are actually alive but parked off-map while no one's nearby. Faust now reads their
  real lair location from the game's own V Blood objective map marker and reports them with coordinates, and it
  no longer mistakes the off-map parking spot for a real location (so a boss without a resolvable position
  shows as "down" instead of sitting at the edge of the map).
- **Fixed: a filtered world scan no longer skips matching assets.** When you searched the map with a narrow
  filter (say, NPCs with blood quality 99+), the result cap was being applied while gathering — before your
  filter ran — so on busy maps the scan filled up on common entities and never reached the rare matches you
  were looking for. The scan now gathers the whole map first and applies your result limit afterward, so a
  narrow search finds every match.

## 0.16.2 (2026-06-13)

- **Roaming bosses now show their real location on the map.** Bosses that wander were reporting their status
  but not a usable position — their combat data is parked off-map when no one's nearby. Faust now pulls the
  location from the game's own map tracking (the same data the blood-altar map uses) and combines it with the
  boss's status, so roaming bosses appear where they actually are. Static bosses are unchanged.

## 0.16.1 (2026-06-13)

- **Fixed: roaming bosses no longer go missing from the boss board.** V Bloods that wander the map (rather
  than sit in a fixed lair) were showing as "down" with no location — even the boss standing next to you —
  because their position was being read from a value that goes stale when no player is nearby. The board now
  reads each boss's live position correctly, so roaming bosses appear on the map where they actually are.
- Improved the admin `.faust admin bossdiag` tool with much more detail for diagnosing boss-location issues.
- **World scan sees more of the map.** The result cap rose from 2000 to 10000, and you can now set it to `0`
  for unlimited so a busy map's scan isn't cut short and miss matching objects. Change it any time with
  `.faust admin setglobal worldscanmaxresults=0` (or edit `[Faust.WorldScan] MaxResults`).

## 0.16.0 (2026-06-12)

Configure Faust in-game, plus two new things to investigate:

- **Change any setting from chat — no restart.** Admins can now set every per-feature gate (access, item
  cost, cooldown, daily/hourly use limits, PvP availability, unlock, proximity) and every global option live
  with `.faust admin set …` / `setglobal …` (and `get …` to read, `resetcfg …` to restore defaults). Changes
  apply instantly and are saved to the config file.
- **Boss status board.** `.faust api bosses` / `boss <name>` — see which V Bloods are up right now, **where
  they are** (coords + region), their **health**, level, and whether they've been defeated. (Admin-default;
  open it to players if you like.) Now covers the **whole map**, including outer-region bosses (the on-map
  range is tunable via `[Faust.Bosses] MapLimit`).
- **Kill leaderboards.** `.faust api kills` (top killers) and `.faust api bosskills` (how many times each
  boss has fallen) over today, this week, or all-time — for fun server leaderboards and achievements.
  Opt-out via `[Faust.Collection] KillTracking`.
- **In-game prefab lookup.** `.faust admin prefab <id or name>` finds prefab IDs and names right in chat —
  by ID, or by a partial name — so you don't have to leave the game to set up whitelists, item costs, etc.
- **World-asset map.** `.faust api worldscan` — a filterable map of **NPC units** (with blood type + quality)
  and **resource nodes** (ores, trees, plants), so a client can build a "find items on the map" tool and
  filter by type, blood type, or blood quality. Admins curate what shows with a **whitelist** (`.faust admin
  worldscan list|add|remove|clear|seed`). The whole-map scan is cached + rate-limited (configurable) to
  protect performance.
- **Fixes:** `.faust admin data status` no longer errors on busy servers; retired the unused `objectscan`
  feature.
- **Verified in-game:** the per-feature admin controls/limiters — access level, item cost, cooldown, usage
  limits, proximity, PvP availability, and unlock gates — are working (further testing recommended before
  production).

## 0.15.0 (2026-06-11)

Tester-feedback batch — the detail tables beneath the activity charts (who, and when):

- **New-players roster.** `.faust api newplayers roster` lists **who** joined recently, when, and their
  clan — the names behind the new-vs-returning chart.
- **Average-per-player hours.** The hour-of-day chart now also reports how many distinct players were on
  in each hour, so a client can show **average minutes per player** as well as the total.
- **Session timeline.** `.faust api sessions timeline <all|player>` returns each player's individual
  online sessions (start/end) for a per-player activity timeline.
- **Active-days grid.** `.faust api stats activegrid` shows, for every player, which days they played in
  the window — the all-players version of the per-player daily trend.
- **Roster extras.** The new-players roster now also shows each player's **total playtime** and **how many
  castles** they own.
- **Region "fill %".** The by-region view now reports each region's **total buildable plots**, so a client
  can show how *full* each region is (claimed ÷ buildable), not just a raw castle count.
- **Regions over time.** New `.faust api stats regiondaily` tracks castles/plots/players **per region per
  day**, so building popularity can be charted as a trend. (Faust starts collecting this from install — it
  has no back-history of the castle map — and samples once a day.)
- **NEW: player-position heat map.** `.faust api heatmap [all|player]` returns a density grid of where
  players spend time — **per player or server-wide** — ready for Raphael to draw as a heat map. Turn it on
  with `[Faust.Heatmap] Enabled=true` (off by default); it snapshots positions every 30s–5min (your choice),
  bins them into a grid, and you can reset it any time with `.faust admin data wipe heatmap`.
- **Castle/plot map coordinates.** Castle and plot listings now include each plot's **map location** (X,Z) —
  so the castle/decay/all-plots tables can show *where* a plot is, not just its owner.
- **Sharper heat maps.** The heat map now reports the **full map bounds**, so it draws at true map scale even
  when only a little data has been collected.
- **Fixed: clan members with spaces.** Looking up a clan whose name has a space (e.g. "Blood Lords") no longer
  fails with "server didn't respond" — multi-word clan names now resolve.
- The §9/§10/§11 stats additions are under the existing **stats** gate (admin-default); the heat map has its own
  **heatmap** gate. All additive — older clients simply don't query them.

## 0.14.0 (2026-06-11)

Tester-feedback batch — more castle detail, prisoners, clan rosters, and admin oversight:

- **More Castle Info.** `castleinfo` now also shows the castle's **floors** (storeys), the **owning clan**,
  and a **grand-total item count** (the single number — not a per-item raid list).
- **Prisoners in Castle Resources.** `resources` now reports how many prisoners a castle holds, with each
  prisoner's name and blood type/quality.
- **Clan rosters.** New `.faust api clanmembers <clan>` lists a clan's members (who's online, who leads).
- **New-vs-returning activity.** The daily activity series now splits each day into **new** vs
  **returning** players for a clearer growth picture.
- **Faust oversight for admins.** New `.faust api access` (who can use each feature) and
  `.faust api usage` (how often each feature is used and what it costs players) — pure server-side
  accounting, surfaced in `.faust admin data status` and resettable via `.faust admin data wipe usage`.

## 0.13.0 (2026-06-10)

- **All features now default to admin-only.** Faust is an admin tool first — admins choose what (if
  anything) to grant players. `castleinfo`, `plots`, `objectscan` and `stats` now start admin-only like
  everything else. (Existing configs are untouched; change any feature in the `.cfg`.)
- **New: anti-spam rate limit.** `[Faust] RateLimitSeconds` (default off) — minimum seconds between a
  player's queries, so nobody can hammer a query and stress the server. Admins exempt by default.
- **New: lock data resets to senior admins.** `[Faust.Data] ResetSteamIds` — only the listed admins can
  run `.faust admin data clear`/`wipe`. Empty = any admin (as before). For tiered admin teams.
- **New: `.faust api stats players`** — a full player-activity roster (active-today/this-week, last-seen,
  sessions, playtime, days-idle) behind the population dashboards.

## 0.12.0 (2026-06-10)

- **New: more activity charts.** **Day-of-week** playtime (`stats weekdays`, server-wide or per player)
  and a **per-player daily trend** (`stats pdaily <player>`) join the existing hour/daily/session charts.
- **New: clan composition (`.faust api clans`).** See at a glance **how many players are in clans vs
  going solo** (with the currently-online split), plus a per-clan roster — size, who's online, **how many
  castles the clan holds**, and the leader. Defaults to admin-only.
- **New: server-health metrics.** `stats population` (active players DAU/WAU/MAU + retention),
  `stats recency` (who's active vs drifting away), `stats peak` (population peak/average), and
  `stats regions` (players + castles per map region). Plus a per-player **days-idle** in `pinfo`.
- **Experimental: `.faust admin showpositions`** — put online players on the **native in-game map**,
  using the game's own map-icon attach system. **Off by default** and still being validated — when you
  enable it on a test server, double-check that the markers are visible only to admins before relying
  on it.

## 0.11.0 (2026-06-10)

- **New: admin data controls.** Faust's history (playtime/sessions, population, unlock progress) lives on
  the server and **survives a world wipe** — on purpose, since the same players come back and their stats
  stay relevant. You now control it directly:
  - **`.faust admin data status`** — see how much Faust has stored (counts, age, disk size).
  - **`.faust admin data clear <days>`** — drop activity older than N days on demand.
  - **`.faust admin data wipe <activity|unlocks|usage|all> confirm`** — reset a store. On a fresh world,
    `unlocks` resets boss-kill-gated features; `activity` resets playtime/charts. The `confirm` word is
    required so it can't fire by accident.
- **New config `DataNamespace`** — leave empty (default) for one shared dataset, or set a name per world
  to keep each world's data separate.
- Clearer guidance on `SessionRetentionDays` (auto-trim old data on big/long-running servers).

## 0.10.0 (2026-06-10)

- **New: activity charts for admins** — four new `.faust api stats` views that turn Faust's session
  history into graphs: **`hours`** (when is the server busy, by hour of day), **`daily`** (daily active
  players + play-minutes), **`newplayers`** (new arrivals per day), and **`sessions`** (how long people
  play per sit-down). Each works server-wide or for one player, and renders as a chart in compatible
  client UIs. Gated by the same `stats` permission you already control.
- **Fixed: player-map regions for everyone, not just players on a plot.** The online-player map now
  shows each player's map region (Farbane Woods, Dunley Farmlands, …) based on **where they actually
  are**, instead of only showing a region for players standing on a castle plot. Players out exploring
  no longer show a blank region.
- **Fixed: consistent "no region" for out-of-bounds plots.** Castle/plot lists used to show a raw
  `None`/`Unknown` for territories outside the normal map (e.g. the admin island); they now show the
  same "—" placeholder the rest of the UI uses.
- **Improved: full server-population history.** The population/concurrency graph is no longer capped to
  the most recent ~200 data points — it now serves the whole stored history (paged).

## 0.9.0 (2026-06-10)

- **New: `.faust api decay`** — claimed castles ranked by **soonest-to-decay**, with each owner's
  last-online. Spot abandoned plots and cleanup targets at a glance. Defaults to admin-only.
- **New: collection controls (`[Faust.Collection]`)** — you now control what Faust *gathers in the
  background*, not just who can read it. Switch off session/population history, cap how much is kept,
  or prune old data — so Faust never costs server performance. Almost every query already reads live
  data on demand (no background cost); these knobs bound the one part that accumulates over time.
- Both READMEs now spell out the philosophy: **Faust is an administrative tool and global server view;
  admins decide, per feature, what (if anything) players can see** — a strategic tool to grant in PvP, a
  community-building tool to share in PvE.

## 0.8.0 (2026-06-10)

- **New: `.faust api castles`** — the **full server castle map**: every territory, claimed and open,
  with owner, region, size, state, decay and online status, in one paged list. Defaults to admin-only.
  Powers BloodCraftHub's "All Plots" tab.
- **Player positions now include region** — the online-player map data now says which region each
  player is in (handy when they're far across the map).
## 0.7.0 (2026-06-09)

- **New: tie a feature to a place.** Admins can require players to be **near a specific object** to
  use a feature — set `RequireNearPrefab` (the object's id) + `RequireNearDistance` (metres) per
  feature. Put an altar/station in a castle (or pick a world landmark) and the ability only works
  when standing near it, instead of anywhere in the world.

## 0.6.0 (2026-06-09)

- **New: `.faust api resources <here|nearest|tindex>`** — see the **total resources stashed in a
  castle** (sums every chest and station). Defaults to admin-only and is a perfect candidate to
  charge an item for, or restrict to PvP servers, via the per-feature settings. Powerful raid intel
  for the BloodCraftHub UI.

## 0.5.0 (2026-06-09)

- **New: lock a feature behind beating a boss.** A feature can require defeating a specific V Blood
  (`BossKill:<id>`) or **Dracula** (`FinalBoss`, i.e. finishing the game) before a player can use
  it — set per feature with `Unlock` in the config. Admins can also hand-unlock for anyone:
  `.faust admin grant <player> <feature>` / `revoke` / `unlocks <player>`.
- Admins and a player querying themselves always bypass the lock.

## 0.4.0 (2026-06-09)

- **Admins now have fine-grained control over every Faust feature:**
  - **Item cost** is now actually charged from the player's inventory per use (e.g. 100 of an item
    to run a query) — and never charged if the query finds nothing.
  - **Time-locks:** a flat cooldown ("then locked 30 min"), or a window-per-period ("a free
    10-minute window, once per day"). Limits persist across restarts.
  - **PvP/PvE gating:** a feature can be set usable only on PvP or only on PvE servers.
  - **Live admin controls (no restart):** `.faust admin block <feature|all> [minutes]` (with an
    optional countdown), `unblock`, `schedule <feature|all> <HH:MM-HH:MM>` (a daily time window),
    and `status`.
- Configure it all per feature in `kdpen.Faust.cfg` (`Availability`, `WindowSeconds`,
  `PeriodSeconds`, `MaxUsesPerPeriod`, plus the existing cost/cooldown/access).

## 0.3.0 (2026-06-09)

- **Player stats are now real.** Faust now logs connect/disconnect over time, so `.faust api pinfo`
  reports actual **first-seen, session count, total playtime, logins/week, and peak hour** (the
  game itself only remembers your *last* login). Data accrues from the moment Faust is installed.
- **New: `.faust api stats <playtime|concurrency>`** — a playtime leaderboard and a server
  population history (for BloodCraftHub graphs).
- Sessions persist to `BepInEx/config/Faust/sessions.json`.

## 0.2.0 (2026-06-09)

- **First investigation queries are live** (for BloodCraftHub + testing):
  - `.faust api castleinfo <here|nearest|tindex>` — a plot's owner, region, size, decay state &
    time, and the owner's online/last-online.
  - `.faust api plots [page]` — open building plots across the map, largest first.
  - `.faust api pinfo <name|steamId>` — a player's online state & last-online (you can always
    look yourself up; looking up others is admin-gated by default). Playtime/frequency stats are
    coming once Faust starts tracking sessions over time.
  - `.faust api positions [page]` — where online players are (admin-default).
- Each query obeys its per-feature access/cost/cooldown, and an empty or not-found lookup is
  never charged.
- This is an early data release — built for first in-game testing; please report anything odd.

## 0.1.0 (2026-06-09)

- Initial scaffold — the mod loads server-side and registers its chat commands. No
  investigation queries are live yet; this release is the **foundation**: the per-feature
  permission/cost gate and the BloodCraftHub handshake that everything else will hang off.
- `.faust` overview command and `.faust help [players|castles|server|admin]` menu.
- `.faust api version` reports each planned feature's access (Off / Admin / Players) and its
  price, so the BloodCraftHub UI can show what's available and what it costs. `.faust api ping`
  is a connection test.
- Every feature is configured independently in `BepInEx/config/kdpen.Faust.cfg` — sensitive
  intel defaults to admin-only; nothing costs an item until an admin sets a price.

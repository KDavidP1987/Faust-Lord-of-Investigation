# Changelog

Condensed, player-facing changelog. Full technical history:
[GitHub](https://github.com/KDavidP1987/Faust-Lord-of-Investigation/blob/main/CHANGELOG.md)

## 0.7.0 (2026-06-09)

- **New: tie a feature to a place.** Admins can require players to be **near a specific object** to
  use a feature — set `RequireNearPrefab` (the object's id) + `RequireNearDistance` (metres) per
  feature. Put an altar/station in a castle (or pick a world landmark) and the ability only works
  when standing near it, instead of anywhere in the world.
- Note: still an early release — the proximity check hasn't been validated in a live session yet.

## 0.6.0 (2026-06-09)

- **New: `.faust api resources <here|nearest|tindex>`** — see the **total resources stashed in a
  castle** (sums every chest and station). Defaults to admin-only and is a perfect candidate to
  charge an item for, or restrict to PvP servers, via the per-feature settings. Powerful raid intel
  for the BloodCraftHub UI.
- Note: still an early release — the castle scan hasn't been validated against a live castle yet.

## 0.5.0 (2026-06-09)

- **New: lock a feature behind beating a boss.** A feature can require defeating a specific V Blood
  (`BossKill:<id>`) or **Dracula** (`FinalBoss`, i.e. finishing the game) before a player can use
  it — set per feature with `Unlock` in the config. Admins can also hand-unlock for anyone:
  `.faust admin grant <player> <feature>` / `revoke` / `unlocks <player>`.
- Admins and a player querying themselves always bypass the lock.
- Note: still an early release — the boss-kill detection hasn't been validated in a live session yet.

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
- Note: still an early release — these controls haven't been validated across a live session yet.

## 0.3.0 (2026-06-09)

- **Player stats are now real.** Faust now logs connect/disconnect over time, so `.faust api pinfo`
  reports actual **first-seen, session count, total playtime, logins/week, and peak hour** (the
  game itself only remembers your *last* login). Data accrues from the moment Faust is installed.
- **New: `.faust api stats <playtime|concurrency>`** — a playtime leaderboard and a server
  population history (for BloodCraftHub graphs).
- Sessions persist to `BepInEx/config/Faust/sessions.json`.
- Note: still an early release for testing — the session-logging path hasn't been validated across
  a live reconnect cycle yet.

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

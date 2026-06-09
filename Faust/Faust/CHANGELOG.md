# Changelog

Condensed, player-facing changelog. Full technical history:
[GitHub](https://github.com/KDavidP1987/Faust-Lord-of-Investigation/blob/main/CHANGELOG.md)

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

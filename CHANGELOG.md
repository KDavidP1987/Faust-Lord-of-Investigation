# Changelog — Faust, Lord of Investigation (full)

This is the **complete** changelog (GitHub). The Thunderstore package carries a condensed,
player-facing changelog at `Faust/Faust/CHANGELOG.md`; every release updates **both** (see
CLAUDE.md → "Release & changelog discipline").

Format: [Keep a Changelog](https://keepachangelog.com/) flavored; versions follow the mod's own
incremental scheme (pre-1.0: minor = feature batch, patch = fixes).

## [0.5.0] - 2026-06-09

### Added — unlock criteria (the final admin-control axis)

A feature can require a **progression gate** before a player may use it (`ADMIN_CONTROL.md` axis 6).
**ApiVersion → 5**; new deny code `locked` (no query reply-shape change).

- **Per-feature `Unlock` config** — `None` (default) | `FinalBoss` (after defeating Dracula —
  game completion) | `BossKill:<vbloodGuid>` (after defeating that specific V Blood). `AllBosses`/
  `AllQuests` are reserved (parsed as grant-only) until a reliable full-set / achievement read.
- **`UnlockService`** (`feature_unlocks.json`) — tracks each player's V-Blood defeats and admin
  grants; evaluates the criterion. `FinalBoss` resolves Dracula by prefab name (no hardcoded GUID).
- **Death hook** (`Patches/DeathEventListenerSystemPatch.cs`) — postfix on
  `DeathEventListenerSystem`: when a player kills a `VBloodUnit`, records the defeat for that
  player. No-op unless some feature configures an `Unlock` (cheap gate); fully try/catch-guarded.
- **Gate** denies `[FAUST:err] code=locked feature=… need=<bosskill|finalboss|grant>` when the
  criterion isn't met. Admins (`AdminsExempt`) and self-queries bypass it.
- **Admin overrides** — `.faust admin grant <player> <feature>` / `revoke <player> <feature>` /
  `unlocks <player>` (show a player's V-blood defeats + granted features).
- Contract + `ADMIN_CONTROL.md` updated — all six control axes now implemented (AllBosses/AllQuests
  auto-detection is the only follow-up).

### Notes

- Compiles clean (0 warnings). The death hook + unlock evaluation are **not yet validated on a live
  server** — needs an in-game pass (configure an `Unlock`, defeat the boss, confirm the feature opens).

## [0.4.0] - 2026-06-09

### Added — admin control surface: cost consume, time-locks, PvP gating, runtime block/schedule

Completes five of the six `ADMIN_CONTROL.md` axes (all but unlock-criteria). **ApiVersion → 4**;
no query reply-shape changed — the controls surface to BCH as new `[FAUST:err]` deny codes
(`blocked`, `schedule`, `pvp`, `window`).

- **Item cost is now actually consumed** — `FaustAccessGate.Commit` draws
  `CostQuantity`×`CostItemGuid` from the requester's inventory after a real result
  (`InventoryUtilities.TryGetInventoryEntity` + `ServerGameManager.TryRemoveInventoryItem`; the
  proven Uriel/KindredLogistics pattern). Verified up front, never charged on an empty/notfound.
- **Usage rate/time-locks** (`UsageService`, `feature_usage.json`) — per (player, feature):
  - `CooldownSeconds` — flat lockout ("pay 100 of an item, then locked 30 min").
  - `WindowSeconds` + `PeriodSeconds` + `MaxUsesPerPeriod` — a burst **window** that opens on first
    use and locks for the rest of a recurring **period** ("free, a 10-minute window once per day" =
    `WindowSeconds=600`, `PeriodSeconds=86400`, `MaxUsesPerPeriod=1`). State persists across
    restarts, so a daily window doesn't reset on a reboot. Deny codes `cooldown` / `window` carry
    `secs` remaining.
- **PvP availability** (`Availability = Always | PvEOnly | PvPOnly`) — gates a feature on the
  server's `GameModeType` (e.g. enemy-resource intel `PvPOnly`). Deny code `pvp`.
- **Runtime operational control** (`FeatureControlService`, `feature_control.json`) — live admin
  overrides on top of the .cfg, persisted across restarts:
  - **`.faust admin block <feature|all> [minutes]`** — block now, optionally as a countdown.
  - **`.faust admin unblock <feature|all>`**.
  - **`.faust admin schedule <feature|all> <HH:MM-HH:MM|clear>`** — a server-local time-of-day window.
  - **`.faust admin status [feature]`** — effective control state per feature.
  Deny codes `blocked` (with `secs` left; `-1` = indefinite) and `schedule` (with `secs` to next open).
- **Gate evaluation order** (`ADMIN_CONTROL.md §4`): master-enabled → runtime block → schedule →
  feature-enabled → access → PvP → usage (cooldown/window/period) → cost. Admins with
  `AdminsExempt=true` skip access/PvP/usage/cost, but not a master-off, a deactivated feature, or
  an operational block/schedule.
- New per-feature config keys: `Availability`, `WindowSeconds`, `PeriodSeconds`, `MaxUsesPerPeriod`.
- Contract + `ADMIN_CONTROL.md` updated (axes 1–5 marked done; unlock-criteria #6 remains).

### Notes

- Compiles clean (0 warnings). The cost/usage/control paths are **not yet validated on a live
  server** — needs an in-game pass (charge an item, hit a cooldown/window, block + reopen).

## [0.3.0] - 2026-06-09

### Added — persistence (FaustStore): real player playtime/frequency + server stats

The session/time-series layer the game doesn't keep (it stores only the *last* connect time).
**ApiVersion → 3.**

- **`FaustStore`** (`Services/FaustStore.cs`) — logs every connect/disconnect to
  `BepInEx/config/Faust/sessions.json` (System.Text.Json), created at `Plugin.Load` (no ECS
  dependency, so it captures connects before game-data init). Derives per-player first-seen,
  session count, total playtime, login frequency (per week), and peak hour (UTC histogram); also
  records an online-count sample at each connect/disconnect for a concurrency series. Open sessions
  are closed on clean shutdown (`Plugin.Unload`) so the last session's playtime is kept; a hard
  crash closes them at boot (negligible-time, never infinite). Concurrency capped to 4000 points.
- **Connectivity patches** (`Patches/PlayerConnectivityPatches.cs`) — `ServerBootstrapSystem`
  `OnUserConnected` (postfix) / `OnUserDisconnected` (prefix), resolving the User from the
  `NetConnectionId` (KindredCommands pattern); fully try/catch-guarded.
- **`pinfo` time-series now live** — `firstseen`, `sessions`, `playmins`, `freq`, `peakhour` are
  real (were `-1` placeholders in 0.2.0). A player with no recorded sessions yet still returns `-1`
  for those (data accrues from install).
- **`.faust api stats <playtime|concurrency> [page]`** (#8) — `[FAUST:stat]` leaderboard rows
  (top players by total minutes) and concurrency time-series points (for BCH graphs, #9).
- BCH contract updated: pinfo marked fully implemented, `stats` shapes documented
  (`playtime`/`concurrency` live; `kills`/`resources` planned), ApiVersion 3.

### Added — admin-control design (docs/features/ADMIN_CONTROL.md)

Captured the full administrative-flexibility spec: per-feature axes (active toggle, audience, **PvP
availability**, item cost, **rate/time-lock windows**, **unlock criteria**), the target gate
evaluation order, runtime operational control (`.faust admin block/unblock/schedule/status` with
countdowns and time-of-day windows), and the build phases. The current `FaustStore` is the
persistence foundation that whole control surface builds on.

### Validated

- 0.2.0's read layer was confirmed working on a live server (castleinfo/plots/pinfo/positions all
  returned data and committed through the gate; per-feature admin gating enforced as designed).

### Notes

- Compiles clean (0 warnings). The persistence/connectivity path is **not yet validated on a live
  server** — first reconnect cycle will confirm session logging.

## [0.2.0] - 2026-06-09

### Added — first investigation queries: castle/plot info, plot availability, player info, positions

The "reuse wins" tier from the design build order — the global server view repackaged into the
`[FAUST:*]` wire so BloodCraftHub has real data to render and the features can be tested in game.
**ApiVersion → 2.**

- **Data layer** (ported from KindredCommands' proven territory/heart/owner model):
  - `EntityExtensions` (IL2CPP-safe `Exists`/`Has`/`Read`/`TryGetComponent`, `TryResolvePlayer`
    by name-fragment-or-steamId, prefab-name lookup) and `Query` (lazy `EntityQueryBuilder`
    helpers; global scans pass `IncludeDisabled` so distant/streamed-out entities are visible).
  - `Wire` — centralizes the two wire invariants: one `ctx.Reply` per `[FAUST:*]` line, and
    wire-safe token values (`Safe()`: spaces → `_`, strips `= ; :`). `SendPage` does 1-based
    paging (20/page) and the `[FAUST:end] cmd=… page=cur/total count=n` trailer.
  - `CastleService` — block-coordinate→territory map (for `here`), per-territory owner/region/
    size/state and fuel-decay math (`FuelEndTime`/`FuelQuantity` + `CastleBloodEssenceDrainModifier`
    + `ServerTime`), and the free-plot scan.
  - `PlayerInfoService` — last-online/online from `User` entities; online-player positions.
- **`.faust api castleinfo <here|nearest|tindex>`** (#2) → `[FAUST:castle]` with owner, steam,
  region, size (blocks), `state=unclaimed|sealed|fueled|decaying`, `decay` seconds (`-1` =
  sealed), online, last-online (Unix UTC).
- **`.faust api plots [page]`** (#4) → `[FAUST:plot]` rows (open territories, largest first) +
  `[FAUST:end]`.
- **`.faust api pinfo <name|steamId>`** (#3) → `[FAUST:player]`; `online`/`lastonline` live now;
  `firstseen`/`sessions`/`playmins`/`freq`/`peakhour` emitted as `-1` (pending the FaustStore
  time-series subsystem). **Self is always allowed**; querying others is gated (AdminOnly default).
- **`.faust api positions [page]`** (#1, admin-default) → `[FAUST:pos]` rows for online players
  (x/z + territory index). Map rendering is a BCH-side decision (design §8); the data is ready.
- All four route through `FaustAccessGate`, committing cooldown/cost only after a real result
  (`notfound`/`badtarget`/empty pages are never charged). In-game `.faust help` + `.faust`
  overview updated to list the live queries.
- BCH contract (`docs/BCH_INTEGRATION_CONTRACT.md`) updated: these shapes are now **implemented**
  (not proposed), with the `state`/`decay` vocabulary, the pending-field sentinels, and the
  `count=` trailer documented.

### Notes

- Reads use the typed `GetComponentData<T>` path the sibling mods ship on the same
  VampireReferenceAssemblies set. Compiles clean (0 warnings); **runtime not yet validated on a
  live server** — this release is for first in-game testing.

## [0.1.0] - 2026-06-09

### Added — project scaffold + the Foundation / permission-cost layer (design #7)

The first build target from `docs/FAUST_DESIGN.md`: the gatekeeper every future query flows
through, plus the BloodCraftHub (BCH) handshake — shipped with **zero data-gathering features
wired**, exactly as the design's build order prescribes.

- **Server-only BepInEx IL2CPP plugin** (`kdpen.Faust`, `Faust.dll`): `Plugin.Load`
  early-returns unless `Application.productName == "VRisingServer"`; VCF command registration;
  Harmony bootstrap; deferred `Core.TryInitialize` gated on the Server world + a populated
  `PrefabCollectionSystem` (via the `SpawnTeamSystem_OnPersistenceLoad` postfix —
  the Uriel/Beelzebub trigger).
- **`FaustAccessGate`** (`Services/FaustAccessGate.cs`) — the single gatekeeper (design §3):
  master-enabled → feature-enabled → access level (Off / AdminOnly / Players) → per-player
  cooldown → item-cost verify. Reserve/confirm split: `TryAuthorize` verifies and stamps
  nothing; `Commit` stamps the cooldown and consumes the cost only after a real result, so an
  empty/`notfound` query is never charged. Denies emit a ready-to-send `[FAUST:err] code=…`
  line. *Inventory verify/consume is stubbed* (the cost is advertised and reserved, but not yet
  drawn from inventory) — it lands with the first server-mediated feature.
- **Per-feature config schema** (`Config/Settings.cs`, design §4) — a global block
  (`Enabled`, `AuditQueries`) plus one block per feature for all seven gateable units
  (`playerpositions`, `castleinfo`, `playerinfo`, `plotavailability`, `objectscan`,
  `castleresources`, `stats`), each with `Access` / `Delivery` / `CostItemGuid` /
  `CostQuantity` / `CooldownSeconds` / `AdminsExempt`. Defaults follow the design: sensitive
  intel (positions, enemy resources, others' player info) → AdminOnly; benign/own-data →
  Players; everything cost-free on a fresh install.
- **BCH handshake** (`Commands/ApiCommands.cs`, contract §2): `.faust api version` advertises
  `api`, `ready`, and one `<feature>=<access>:<cost>` token per feature — access **resolved for
  the requesting player** (an admin sees `players` where a non-admin sees `admin`) and cost as
  `0` or `<guid>x<qty>[:cd=<secs>]` — so BCH gates its UI and shows prices without a round-trip.
  `.faust api ping` → `[FAUST:pong]` proves the round-trip.
- **`.faust` overview** + **`.faust help [players|castles|server|admin]`** topic tree (stubs
  naming the planned feature groups so help grows with the features).
- **ApiVersion 1**. Bumped whenever the `[FAUST:*]` wire grows (contract §6).

### Added — development process scaffolding

- `docs/PREFLIGHT.md` (session-start checklist), `docs/DEV_REMINDERS.md` (standing IL2CPP/ECS +
  process rules), `tools/preflight.ps1` (release-surface sync checker).
- Claude Code guard/reminder hooks (`.claude/hooks/`, local-only): session preflight pointer,
  release-surface sync reminder, reference-path guard (sibling workspaces are read-only), and a
  BCH-contract relevance reminder.
- Dual release surfaces: GitHub README/CHANGELOG (root) + Thunderstore README/CHANGELOG under
  `Faust/Faust/`, with a `thunderstore.toml` manifest (namespace `kdpen`; deps
  BepInExPack_V_Rising 1.733.2, VampireCommandFramework 0.10.4). Build auto-deploys `Faust.dll`
  to a local dedicated server; `BuildToDist` stages the package for `tcli`.

### Notes

- **Not yet published to Thunderstore**; no investigation queries yet. GitHub is the public home
  until the feature set is ready.
- The Thunderstore `icon.png` is the one asset still to add before any package publish (derive
  from the Faust cover art in the workspace root).
- License: **AGPL-3.0** — Faust reuses server-side patterns from odjit's AGPL-licensed
  KindredCommands (territory/heart/owner model, info commands, audit), matching sibling Uriel.

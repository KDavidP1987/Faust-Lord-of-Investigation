# Changelog — Faust, Lord of Investigation (full)

This is the **complete** changelog (GitHub). The Thunderstore package carries a condensed,
player-facing changelog at `Faust/Faust/CHANGELOG.md`; every release updates **both** (see
CLAUDE.md → "Release & changelog discipline").

Format: [Keep a Changelog](https://keepachangelog.com/) flavored; versions follow the mod's own
incremental scheme (pre-1.0: minor = feature batch, patch = fixes).

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

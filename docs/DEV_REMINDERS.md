# DEV REMINDERS — standing rules for working on Faust

Hard-won rules, most inherited from Beelzebub/Uriel development. Add to this file whenever a new
gotcha costs real debugging time — that's the rule for what belongs here: things that bit once and
must not bite twice.

## IL2CPP / ECS

- **No game-type statics at Load.** `Il2CppType.Of<T>()`, `ComponentType.ReadOnly<T>()`, prefab
  lookups — all NRE before `TypeManager` is built. Initialize in `Core.TryInitialize`, which only
  runs once the Server world + `PrefabCollectionSystem` are populated.
- **Gate every patch on `Core.IsReady`.** Harmony patches fire during boot.
- **Never throw across a Harmony boundary.** try/catch + `Core.Log` inside every patch body. A
  leaked exception inside a server system update can corrupt the tick or crash Burst jobs.
- **Entity lifetime is not your call.** Cache `Entity` handles only with an `Exists()` check on
  every later use; entities despawn/restream between frames (a relog or area unload recreates a
  castle object's entity with a new handle — resolve from persisted keys, not cached handles).
- **Default EntityQueries skip Disabled entities.** World/castle objects carry
  `DisableWhenNoPlayersInRange` and sit `Disabled` whenever nobody is nearby — which is *always*
  the case for distant objects and during boot. Any global scan (the whole point of Faust) needs
  `EntityQueryOptions.IncludeDisabled | IncludeSpawnTag`.

## V Rising specifics

- **Server-only.** `Application.productName == "VRisingServer"` guard stays. Faust is the
  authoritative global view; it must not require a client-side counterpart to function (BCH is an
  optional renderer, never a dependency).
- **Prefab research before code.** Before referencing any prefab/component, confirm its layout in
  a prefab dump (the sibling Beelzebub workspace has one under `Reference Data\Prefabs\`). Items
  are `Item_*`, castle tiles `TM_*`, characters `CHAR_*`.
- **Pick patch targets by reading prior art.** KindredCommands / Bloodcraft have probably already
  hooked the system you need (player connectivity, territory, inventory); copy their target
  choice. KindredCommands' `CastleTerritoryService` / `InfoCommands` / `AuditService` are the
  direct models for Faust features #2/#3/#4/#8.

## Faust foundation invariants

- **One gate, always.** Every BCH-facing query goes through `FaustAccessGate.TryAuthorize` before
  gathering data. `Commit` (cooldown stamp + cost consume) is called ONLY after a real result —
  never charge an empty/`notfound` query (design §3).
- **Cost ⇒ server mediation.** A client can't charge itself. Anything gated or priced MUST be
  server-mediated (BCH → Faust → verify + consume → reply). `Delivery=Free` is only valid for
  near-player/own-data features that BCH can read locally.
- **Persistence is Faust's job.** The game stores only the *last* connect time
  (`User.TimeLastConnected`). Anything time-series (#3 frequency/playtime, #8 stats) needs Faust's
  own JSON store under `BepInEx/config/Faust/` — debounced save-on-change + save-on-unload, with a
  migration path whenever the schema changes.

## Process

- **Feature-flag everything.** Every feature has its own per-feature config block (Access /
  Delivery / Cost / Cooldown). Sensitive intel defaults to AdminOnly.
- **The BCH contract is living.** `docs/BCH_INTEGRATION_CONTRACT.md` is authoritative for the
  `.faust` surface and `[FAUST:*]` shapes. Change a BCH-facing thing → update the contract in the
  same commit and **bump `ApiVersion` when the wire grows**. (A `PostToolUse` hook reminds on
  edits to `Commands/`, `Config/Settings.cs`, `Services/FaustAccessGate.cs`.)
- **One `ctx.Reply` per `[FAUST:*]` line.** Never `\n`-join a page (the #1 Uriel integration bug).
- **Conventional Commits**, release commits `chore(release): vX.Y.Z`.
- **Six release surfaces move together** (see CLAUDE.md): csproj version, toml version, root
  CHANGELOG (full/GitHub), package CHANGELOG (concise/Thunderstore), root README (GitHub), package
  README (Thunderstore). `tools/preflight.ps1` verifies before the release commit.
- **Stop the server before deploying.** The running server file-locks the DLL.
- **Test on the live local server, then record results** in the changelog/feature doc (what was
  tested, what passed, what's unverified). Untested code is marked "experimental".

## Lessons learned (append below as they happen)

- *(none yet — 0.1.0 is the initial scaffold)*

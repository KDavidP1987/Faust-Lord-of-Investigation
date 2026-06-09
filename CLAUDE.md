# CLAUDE.md — Faust, Lord of Investigation

Guidance for Claude Code when working in this workspace.

## What this workspace is

**Faust, Lord of Investigation** is a **server-side** BepInEx IL2CPP plugin for
V Rising. Its single purpose is **investigation / information**: answering
on-demand queries about players, castles, plots, objects, and server activity —
delivered either as in-game chat commands (`.faust …`) or, primarily, as
structured data consumed by the **BloodCraftHub (BCH)** client mod and rendered
in its UI (panels, overlays, popups, and graphs).

Faust holds the **authoritative, global, persistent** view of the server that a
game client can't see (the client is spatially culled — it only receives
entities near the local player). Faust gathers that view, **gates** it (per
feature, server-enforced), optionally **charges an item cost** for it (the
Faustian toll), and ships it to BCH.

This is the same family of mods as the author's other work:
- **BloodCraftHub** — the *client* mod that renders Faust's data. Faust talks to
  it exactly like Beelzebub and Uriel do.
- **Beelzebub, Lord of Gluttony** and **Uriel, Lord of Oaths** — sibling
  *server-side* mods with the same BCH-integration pattern. Faust mirrors their
  architecture.

> **Status:** brand-new. This folder currently holds only design docs (`docs/`)
> and reference art. No project/code exists yet. The first build target is the
> **Foundation + permission/cost layer** — see `docs/FAUST_DESIGN.md` §"Build order".

## Sibling projects — DO NOT edit from this workspace

Each of these is a **separate project** with its own `CLAUDE.md`, its own
auto-memory namespace, and its own repo. Never edit their source from a Faust
session. Cross-project work flows through the **integration contract docs**, not
direct edits.

- **BloodCraftHub (client)** — `…\V Rising\BloodCraftUI 2\BloodCraftHub\`
- **Beelzebub (server)** — `…\V Rising\Beelzebub Lord of Gluttony\`
- **Uriel (server)** — `…\V Rising\Uriel Lord of Oaths\`

The BCH↔Faust seam is owned by `docs/BCH_INTEGRATION_CONTRACT.md` here and a
mirrored handoff doc on the BCH side. When Faust changes anything BCH-facing (a
command, a `[FAUST:*]` reply shape, a config key, the ApiVersion), update the
contract doc in the same commit and ping BCH — the **living-contract rule**.

## Reference-only source (read for patterns, do NOT edit)

These live under the BCH workspace's `LearningMods/` and are the best templates
for Faust's server-side code. Read them; don't modify them.

- **KindredCommands** — `…\BloodCraftUI 2\LearningMods\KindredCommands-main\`
  The closest model: a server-side info/admin command mod. Already has
  `Services/CastleTerritoryService.cs` (territory/heart/owner model),
  `Commands/InfoCommands.cs` (`playerinfo`/`pinfo`, `longestofflinecastles`/`loc`
  reading `User.TimeLastConnected`), `Services/AuditService.cs` (audit logging),
  and VCF command/converter patterns. **Faust reuses these patterns heavily for
  features #2/#3/#4/#8 — integrate, don't reinvent.**
- **Bloodcraft** — `…\BloodCraftUI 2\LearningMods\Bloodcraft-main\` — server-side
  mod with persistence (JSON player data), service patterns, the signed Eclipse
  broadcast protocol.
- **BloodCraftHub** — `…\BloodCraftUI 2\BloodCraftHub\` — the client. Its
  `Services/Beelzebub/BeelzProtocolService.cs` and `Services/Uriel/` show the
  exact handshake/probe/wire machinery Faust must answer to.

## Tech stack (match KindredCommands)

- **Server-side** BepInEx IL2CPP plugin (loads on the dedicated server / host —
  NOT the client).
- `net6.0`, `LangVersion latest`, `AllowUnsafeBlocks true`.
- `BepInEx.PluginInfoProps` 2.* (auto-generates `MyPluginInfo`; single version
  source in the csproj `<Version>` — the BCH lesson: never dual-maintain a
  version constant).
- `VRising.VampireCommandFramework` 0.10.* for `.faust …` chat commands.
- `System.Text.Json` 6.0.11 for persistence.
- Game/IL2CPP types via the shared `..\interop\` reference DLLs (same set
  KindredCommands references) + `VampireReferenceAssemblies` if preferred.
- Suggested plugin GUID: `kdpen.Faust`. AssemblyName `Faust`.

## Project layout

The buildable project lives under `Faust/` (mirrors Uriel's nested layout):

```
Faust/
├── Faust.sln
└── Faust/                      ← C# project root
    ├── Faust.csproj            ← single version source (auto-generates MyPluginInfo)
    ├── Plugin.cs               ← entry point (BasePlugin.Load; server-only guard)
    ├── Core.cs                 ← deferred init hub (world/system handles; IsReady gate)
    ├── Patches/                ← Harmony patches (GameDataInitializedPatch triggers Core init)
    ├── Services/               ← FaustAccessGate (the gatekeeper); feature services land here
    ├── Commands/               ← VCF commands (.faust …); ApiCommands = the [FAUST:*] handshake
    ├── Config/Settings.cs      ← per-feature config schema (Access/Delivery/Cost/Cooldown)
    ├── README.md               ← THUNDERSTORE mod page (player-facing)
    ├── CHANGELOG.md            ← THUNDERSTORE changelog (concise, player-facing)
    ├── thunderstore.toml       ← Thunderstore manifest (versionNumber synced to csproj)
    └── icon.png                ← Thunderstore icon (STILL TO ADD — derive from cover art)
docs/      FAUST_DESIGN.md · BCH_INTEGRATION_CONTRACT.md · PREFLIGHT.md · DEV_REMINDERS.md
tools/     preflight.ps1 (release-surface sync check)
.claude/   hooks/ + settings.local.json (local-only; gitignored)
```

## Build & local deploy

```powershell
cd Faust
dotnet restore Faust.sln
dotnet build Faust.sln -c Release
```

- **References come from NuGet**, not a local interop folder — `VampireReferenceAssemblies`
  (game/Unity/ProjectM types) + `BepInEx.Unity.IL2CPP` + `VRising.VampireCommandFramework`.
  This matches the **sibling Uriel/Beelzebub** approach and keeps the repo self-contained
  (no dependency on `..\interop\` sibling-folder layout). `RestoreSources` includes the
  BepInEx NuGet feed.
- **Deploy is automatic.** The `BuildToServer` target copies `Faust.dll` to
  `…\VRisingDedicatedServer\BepInEx\plugins` after every build when that folder exists.
  Compile-check without deploying: `dotnet build Faust.sln -c Release -p:VRisingServerPath=C:\__nodeploy__`.
  The `BuildToDist` target stages `Faust.dll` + CHANGELOG under `Faust/Faust/dist/` for `tcli`.
- **Stop the dedicated server before a deploying build** — it file-locks `Faust.dll`.
- **No unit tests / no lint.** Verification is **in-game on a dedicated server** — build,
  let the deploy copy land, launch the server, watch the BepInEx console, and exercise
  `.faust …` (start with `.faust api version` / `.faust api ping`). There is no `dotnet test`.
- **Version is single-sourced** in `Faust.csproj <Version>` (BepInEx.PluginInfoProps generates
  `MyPluginInfo` — never add a second version constant). The wire **ApiVersion is a separate
  number**; bump it whenever the `[FAUST:*]` wire grows.

## Release & changelog discipline — SIX surfaces move together

Faust keeps **separate GitHub and Thunderstore documents**. On every version bump, all of
these move together in one `chore(release): vX.Y.Z` commit:

1. **Version** — `Faust/Faust/Faust.csproj <Version>` **and** `Faust/Faust/thunderstore.toml
   versionNumber` (keep identical).
2. **`CHANGELOG.md` (root)** — the FULL GitHub changelog; every version gets an entry.
3. **`Faust/Faust/CHANGELOG.md`** — the concise, player-facing Thunderstore changelog.
4. **`README.md` (root)** — GitHub landing page (developer/admin facing).
5. **`Faust/Faust/README.md`** — Thunderstore mod page (player/admin facing).
6. **The BCH contract** — if a release changed a `.faust` command or `[FAUST:*]` shape,
   `docs/BCH_INTEGRATION_CONTRACT.md` reflects it and `ApiVersion` is bumped.

Run `tools/preflight.ps1` before any release commit — it checks version parity and that both
changelogs have an entry. A `PostToolUse` hook (`.claude/hooks/release-sync-reminder.ps1`)
surfaces this checklist on edits to the version files / changelogs. Hooks are backstops;
**this CLAUDE.md rule is the authoritative process.**

## Git workflow

- Conventional Commits: `feat|fix|chore|docs|refactor|test|build|ci|perf|style|revert(scope)?: subject`;
  release commits `chore(release): vX.Y.Z`.
- `gh` CLI is authenticated as `KDavidP1987`. Intended GitHub repo:
  `KDavidP1987/Faust-Lord-of-Investigation`.
- Thunderstore publication is deferred until the mod is ready (the `icon.png` is still to add);
  GitHub is the public home until then.

## Architecture in one paragraph

Every query enters through **one gatekeeper** (`FaustAccessGate`) that checks, in
order: feature **enabled** → **access level** (Off / Admin-only / Players) →
**cooldown** → **item cost** (verify the requester's inventory; consume on
success; admins optionally exempt). Only then does the feature's service gather
data and emit it. BCH-facing replies use the `[FAUST:*]` wire — VCF `ctx.Reply`
System-chat lines, **one wire line per `ctx.Reply`** (the Uriel lesson: BCH reads
one wire line per chat message; never `\n`-join a page). A `.faust api version`
handshake advertises the ApiVersion, each feature's access level, **and its
cost**, so BCH can gate its UI and show the price without a round-trip. Full
detail: `docs/FAUST_DESIGN.md` and `docs/BCH_INTEGRATION_CONTRACT.md`.

## Key decisions already made (2026-06-09, with the BCH author)

1. **Exposure is server-config PER FEATURE** — every capability has its own
   `Access` (Off / Admin-only / Players). Sensitive intel defaults to Admin-only.
2. **Per-query ITEM COST** (the signature mechanic) — admins can require an item
   (PrefabGUID + quantity, optional cooldown, admins optionally exempt) to run a
   query, so on PvP/competitive servers intel isn't free. On-theme for a Faustian
   bargain: knowledge has a price.
3. **Gating/cost ⇒ server mediation.** A client can't charge itself, so any gated
   or charged feature MUST be server-mediated (BCH→Faust→verify+consume→reply).
   The per-feature config therefore has a **Delivery** axis: *Free* (BCH reads
   replicated state locally, instant, un-chargeable) vs *Server-mediated*
   (gateable, chargeable). Even a "client-capable" feature (e.g. nearby-object
   scan) routes through Faust when the admin wants to price it.
4. **Start with the Foundation + permission/cost layer** — the gatekeeper every
   feature depends on — before any individual feature.

## Things to watch out for (inherited IL2CPP / ecosystem lessons)

- **One `ctx.Reply` per `[FAUST:*]` wire line.** BCH treats each System-chat
  message as one wire line and does not split on `\n`. Paged output = many
  replies + a `[FAUST:end] cmd=… page=cur/total` trailer.
- **Bump `ApiVersion` whenever the wire grows**, and gate richer replies behind
  `api >= N` so an older BCH degrades gracefully.
- **IL2CPP, not Mono.** Don't touch `ComponentType.ReadOnly(Il2CppType.Of<T>())`
  in a static field initializer — `TypeManager` isn't built at plugin load
  (NREs). Build queries lazily. Guard every entity access with `Exists()`.
- **Never charge for a failed/empty query.** Verify cost up front, but consume
  the item only once the query actually produced a result (see design doc).
- **Persistence is server-authoritative time-series** for the stats features
  (#3 frequency/playtime, #8 server stats) — the game only stores *last* connect
  time (`User.TimeLastConnected`). Faust must log connect/disconnect over time.

## Where the deeper context lives

- `docs/FAUST_DESIGN.md` — vision, the client-vs-server evaluation of all 9
  feature ideas, the permission/cost layer, the config schema, persistence, and
  the build order/roadmap.
- `docs/BCH_INTEGRATION_CONTRACT.md` — the living contract with BloodCraftHub:
  `.faust` command surface, `[FAUST:*]` wire shapes, the handshake, ApiVersion,
  paging, errors, and what BCH builds on its side.

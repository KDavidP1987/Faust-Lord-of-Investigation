# PREFLIGHT — session-start checklist

Work this list at the top of every working session, before making changes.

## 1. Workspace state

- [ ] `git status` — working tree clean? If not, understand what's pending before
      adding to it (finish/commit/stash deliberately, don't pile on).
- [ ] `git log --oneline -5` — re-orient on where the last session left off.
- [ ] Any `WIP`/`TODO` notes in the last commit message or open feature doc?

## 2. Boundaries

- [ ] The **sibling mod workspaces are read-only** from this session — Beelzebub,
      Uriel, and BloodCraftHub each have their own repo and session. Read them for
      patterns; never edit them from here. (A `PreToolUse` hook warns on this.)
- [ ] The BCH seam is owned by `docs/BCH_INTEGRATION_CONTRACT.md`. If a change
      touches anything BCH-facing (a `.faust` command, a `[FAUST:*]` reply shape, a
      config key, the ApiVersion), update the contract **in the same commit** and
      ping BCH — the living-contract rule.

## 3. Build & deploy safety

- [ ] Is the local V Rising dedicated server **running**? It file-locks `Faust.dll`
      — stop it before any build that deploys.
- [ ] Compile-check only (no deploy):
      `dotnet build Faust.sln -c Release -p:VRisingServerPath=C:\__nodeploy__`

## 4. Release intent

- [ ] If this session will end in a version bump: re-read CLAUDE.md →
      "Release & changelog discipline" (six surfaces move together) and run
      `tools/preflight.ps1` before the `chore(release)` commit.
- [ ] Touching a changelog or README? Follow `docs/DOC_STYLE.md` — say each cross-cutting
      fact once (no per-entry boilerplate footers), group features, keep the Status section
      pointing at the changelog. `preflight.ps1` emits doc-hygiene warnings for drift.

## 5. Foundation invariants (don't regress these)

- [ ] Every BCH-facing query routes through `FaustAccessGate.TryAuthorize` before
      gathering data, and calls `Commit` only after a real result (never charge an
      empty/`notfound` query).
- [ ] New wire shape or feature token? **Bump `ApiVersion`** and gate richer replies
      behind `api >= N` so an older BCH degrades gracefully.
- [ ] **One `ctx.Reply` per `[FAUST:*]` line** — never `\n`-join a page (the Uriel
      bug). Paged output = N rows + a `[FAUST:end] cmd=… page=cur/total` trailer.

## 6. Live-server data

- [ ] If a feature persists state (e.g. the session/time-series store under
      `BepInEx/config/Faust/`), check whether a schema change needs a migration path
      for data already on a live server. Never silently drop player state.

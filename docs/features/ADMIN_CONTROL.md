# Faust — Administrative Control (design)

> The admin-facing control surface over every Faust feature. Captures the full vision (2026-06-09)
> for how server owners gate, price, time-lock, unlock-gate, and operationally block features. The
> **foundation gate** (`FaustAccessGate`) already implements the first axes (enabled / access /
> cooldown / cost-verify); this doc is the spec for completing the rest. Status per axis is marked
> **[done]**, **[partial]**, or **[planned]**.

## 1. The control model — per-feature axes

Every gateable feature (`playerpositions`, `castleinfo`, `playerinfo`, `plotavailability`,
`objectscan`, `castleresources`, `stats`) carries an independent control block. The gate evaluates
these in order; the **first** failing axis denies with a specific `[FAUST:err] code=…`.

| # | Axis | Config key(s) | Status | Notes |
|---|------|---------------|--------|-------|
| 1 | **Active toggle** | `Access=Off` (+ master `Faust.Enabled`) | [done] | `Access=Off` deactivates a feature (hard stop even for admins); master `Faust.Enabled` kills the whole mod. |
| 2 | **Audience** | `Access = AdminOnly \| Players` | [done] | Who may run it. Resolved per requester in the handshake (admin sees `players` where a player sees `admin`). |
| 3 | **PvP availability** | `Availability = Always \| PvEOnly \| PvPOnly` | [done] | Gated on the server game mode (`ServerGameSettingsSystem.Settings.GameModeType`). e.g. enemy-resource intel `PvPOnly`, position intel `PvEOnly`. Deny code `pvp`. |
| 4 | **Item cost** | `CostItemGuid` + `CostQuantity` | [done] | Verified up front and **consumed in Commit** after a real result (`InventoryUtilities` + `ServerGameManager.TryRemoveInventoryItem`). e.g. 100× of an item per use. |
| 5 | **Rate / time lock** | `CooldownSeconds`, `WindowSeconds`, `PeriodSeconds`, `MaxUsesPerPeriod` | [done] | Flat cooldown + the window/period model below, persisted per (player, feature) in `feature_usage.json`. Deny codes `cooldown` / `window`. |
| 6 | **Unlock criterion** | `Unlock = None \| BossKill:<guid> \| FinalBoss` (+ `AllBosses`/`AllQuests` reserved) | [done*] | A feature opens only after a player meets a progression gate. `BossKill`/`FinalBoss` (defeat Dracula) auto-detect via the death hook; `AllBosses`/`AllQuests` are reserved (admin-grant only until reliable detection). Per-player progress in `feature_unlocks.json`. Deny `locked` (with `need=`). Admin override: `.faust admin grant/revoke`. |

### Rate / time-lock patterns (axis 5)

A single flexible usage policy expresses every example you gave:

- **`CooldownSeconds`** — simple lockout after each successful use.
  - *"pay 100 of an item, then can't use it again for 30 min"* → `CostQuantity=100`, `CooldownSeconds=1800`.
- **`WindowSeconds` + `PeriodSeconds` + `MaxUsesPerPeriod`** — a burst **window** that opens on first
  use, then locks for the rest of a recurring **period**.
  - *"free, once per day for a 10-minute window"* → `CostItemGuid=0`, `WindowSeconds=600`,
    `PeriodSeconds=86400`, `MaxUsesPerPeriod=1`. The player gets a strategic 10-minute window of
    free access per day; outside it, `[FAUST:err] code=window`/`cooldown` with the time remaining.
- Cost and time-lock compose freely: a feature can be priced **and** windowed **and** PvE-only
  **and** unlock-gated simultaneously — each axis is independent.

State for windows/periods is per-(player, feature) and persists in `FaustStore` so a restart
doesn't reset a daily window.

## 2. Unlock criteria (axis 6) — earn the ability

A feature can require a progression gate before it appears for a player:

- **`BossKill:<vbloodGuid>`** — defeat a specific V Blood. Detected via a death/kill hook
  (`DeathEventListenerSystem` postfix — the proven Uriel/Bloodcraft pattern), recorded per player.
- **`FinalBoss`** — defeat Dracula (`CHAR_Vampire_Dracula_VBlood`) = game completion.
- **`AllBosses`** — defeat every main V Blood.
- **`AllQuests`** — complete the achievement/quest set (V Rising's `AchievementsBuffer` /
  progression — needs a research spike to confirm the read).

Admin overrides: `.faust admin grant <player> <feature>` / `revoke` to hand-unlock for testing or
as a reward. Progress + grants persist in `FaustStore` (`feature_unlocks.json`).

## 3. Runtime operational control (axis 7) — block live, no restart

BepInEx reads the `.cfg` only at boot, so live control needs a **runtime override layer** the gate
consults **on top of** config. Overrides persist (`feature_control.json`) so they survive restarts.

Admin commands (`[CommandGroup("faust admin")]`, admin-only):

- **`.faust admin block <feature|all> [minutes]`** — disable now. With `[minutes]`, a **countdown**:
  auto-re-enables when it expires (`blockedUntil` timestamp). Without it, blocked until `unblock`.
- **`.faust admin unblock <feature|all>`** — clear a block / countdown.
- **`.faust admin schedule <feature|all> <HH:MM-HH:MM>`** — a **time-of-day window** when the
  feature is usable (e.g. `18:00-22:00`); outside it the gate denies `code=schedule`. `clear` removes it.
- **`.faust admin status [feature]`** — show each feature's **effective** state (config + runtime
  overrides + schedule), so an admin can see exactly what players can do right now.
- **`.faust admin reload`** — re-read the `.cfg` without a full restart (best-effort).

These mirror "toggle on/off, or a manually-started countdown, or a daily time-of-day window" — all
three are the same `feature_control.json` state with different expiry semantics.

## 4. Gate evaluation order (target)

```
FaustAccessGate.TryAuthorize(ctx, feature):
  1. master Faust.Enabled?                      no  -> code=disabled
  2. runtime override: blocked / countdown?     yes -> code=blocked  (+ secs left)
  3. runtime override: outside schedule window? yes -> code=schedule (+ next-open)
  4. feature Enabled?                           no  -> code=disabled
  5. Unlock criterion met (or admin-exempt)?    no  -> code=locked   (+ what unlocks it)
  6. Access: AdminOnly & !admin?                yes -> code=noaccess
  7. PvP availability vs server/requester mode? bad -> code=pvp
  8. Usage limit (cooldown / window / period)?  bad -> code=cooldown|window (+ secs)
  9. Cost: inventory has Qty?                    no  -> code=cost     (item, qty)
  -> Allow (RESERVE cost; consume + stamp usage in Commit after a real result)
```

Admins are exempt from 5–9 when the feature's `AdminsExempt=true` (default). New error codes to add
to the contract: `blocked`, `schedule`, `locked`, `pvp`, `window`.

## 5. Build phases

1. **[done] Foundation** — enabled / access / cooldown / cost-verify (gate + per-feature config + handshake).
2. **[done 0.4.0] Cost consume** — draw `CostQuantity`×`CostItemGuid` from inventory in `Commit`.
3. **[done 0.4.0] Usage policy** — `WindowSeconds`/`PeriodSeconds`/`MaxUsesPerPeriod`, persisted per (player, feature) in `feature_usage.json`.
4. **[done 0.4.0] PvP availability** — `Availability` axis vs server game mode.
5. **[done 0.4.0] Runtime control** — `.faust admin block/unblock/schedule/status`, persisted override layer (`feature_control.json`).
6. **[done* 0.5.0] Unlock criteria** — `BossKill:<guid>` / `FinalBoss` auto-detected via a
   `DeathEventListenerSystem` hook (V Bloods only), `feature_unlocks.json`, `.faust admin
   grant/revoke/unlocks`, deny code `locked`. *`AllBosses`/`AllQuests` are reserved (parsed as
   grant-only) pending a reliable full-set / achievement read.

All six axes are now implemented (AllBosses/AllQuests detection is the only follow-up). A future
ApiVersion bump can grow the handshake to advertise the availability/window/lock state per feature
so BCH greys buttons without waiting for a deny.

## 6. Persistence note

All of the above lean on `FaustStore` (the session/time-series persistence built for `playerinfo`
/`stats`). The same store hosts: `feature_control.json` (runtime overrides/schedules),
`feature_usage.json` (per-player window/period/cooldown state), and `feature_unlocks.json`
(progression + admin grants). Building the store first (the current phase) is therefore the
foundation for this whole control surface.

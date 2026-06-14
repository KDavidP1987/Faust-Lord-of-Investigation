using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Faust.Config;
using ProjectM;
using Stunlock.Core;
using Unity.Entities;
using Unity.Transforms;

namespace Faust.Services;

/// <summary>
/// V Blood boss intelligence (the boss-status board). Faust is the authoritative global view, so it can
/// answer "which bosses are alive right now, where, and at what health" — intel a spatially-culled client
/// can't see. A boss is a live world entity ONLY while spawned; when killed it despawns and respawns on a
/// timer (no entity in between). So this reports two layers, honestly:
///   • <b>up</b> — a live <see cref="VBloodUnit"/> + <see cref="VBloodConsumeSource"/> entity in the world
///     (real consumable boss, not a player shapeshift / bound familiar, which lack the consume source):
///     position, region, current/max health, level.
///   • <b>down</b> — a boss NOT placed in the world right now (no coords): either a pooled/staged VBlood
///     entity parked off-map at the spawn sentinel (it exists but isn't on the map), or a boss some player
///     has defeated (from the unlock store, Faust's own cross-player record). <c>defeated</c> reflects the
///     latter. (A boss reads "down" the moment it leaves the playable map — see <c>MapLimit</c>.)
/// A never-defeated, not-currently-spawned boss has no data source server-side yet (would need the prefab
/// spawn roster — a follow-up), so it simply isn't listed. On-demand: zero passive cost.
/// </summary>
internal sealed class BossService
{
    public readonly struct BossSnapshot
    {
        public int Guid { get; init; }
        public string Name { get; init; }
        public bool Alive { get; init; }     // a live world entity exists right now ("up")
        public bool Defeated { get; init; }  // any player has ever killed it (server-wide)
        public float X { get; init; }        // valid when Alive
        public float Z { get; init; }        // valid when Alive
        public string Region { get; init; }  // when Alive (else null)
        public float Hp { get; init; }       // current health, when Alive
        public float HpMax { get; init; }    // when Alive
        public int Level { get; init; }      // unit level, when Alive
    }

    /// <summary>All bosses Faust can currently see: live ones (with position/health) plus defeated bosses
    /// that aren't currently spawned. Live first, then by name.</summary>
    // The placed-vs-pooled distance threshold — live-tunable via [Faust.Bosses] MapLimit. The Vardoran
    // playable map sits well within ±this; pooled/parked boss entities that aren't on the map park at a far
    // off-map sentinel (observed ~10000,10000), so a position beyond this means "not really on the map"
    // (Raphael §16/§18). Raise it if outer-region bosses read 'down', but keep it below ~10000.
    static float MapLimit => Settings.BossMapLimit.Value;

    public List<BossSnapshot> GetBosses()
    {
        var byGuid = new Dictionary<int, BossSnapshot>();

        // The combine (Raphael's two-sides-of-the-coin): a ROAMING boss's combat entity is parked off-map
        // when no player is near, so its own transform isn't its real location — but the game still tracks
        // where it is for the map (blood-altar tracking + the map icon). Resolve those map-token positions,
        // keyed by V Blood prefab GUID, and use them for any boss whose combat entity reads off-map. The
        // boss's STATUS/health/level still come from the combat entity below.
        var tokenPos = BuildMapTokenPositions();

        // 1) Live world bosses.
        var ents = Query.GetEntitiesByComponentType<VBloodUnit>(includeDisabled: true);
        try
        {
            for (int i = 0; i < ents.Length; i++)
            {
                var e = ents[i];
                // Real world boss only: the consume source excludes player VBlood-form shapeshifts and
                // bound boss familiars (which carry VBloodUnit but are not a consumable source).
                if (!e.Has<VBloodConsumeSource>()) continue;
                if (!e.TryGetComponent<Health>(out var hp) || hp.Value <= 0) continue; // alive only

                int guid = e.GetPrefabGuid()._Value;
                if (guid == 0) continue;

                // Position source: prefer Translation (the sim-authoritative current position) over the
                // LocalToWorld render matrix, which can go STALE for a streamed-out/pooled entity — the reason
                // roaming bosses far from any player used to read off-map ("down") even though the server still
                // tracks where they are. Take whichever source reads on-map; if NEITHER is on-map the boss has
                // no usable live location right now (truly pooled / awaiting respawn) → report it as down.
                bool hasTr = e.TryGetComponent<Translation>(out var tr);
                bool hasLtw = e.TryGetComponent<LocalToWorld>(out var ltw);
                if (!hasTr && !hasLtw) continue;

                float px, pz; bool placed;
                if (hasTr && OnMap(tr.Value.x, tr.Value.z)) { px = tr.Value.x; pz = tr.Value.z; placed = true; }
                else if (hasLtw && OnMap(ltw.Position.x, ltw.Position.z)) { px = ltw.Position.x; pz = ltw.Position.z; placed = true; }
                else { px = hasTr ? tr.Value.x : ltw.Position.x; pz = hasTr ? tr.Value.z : ltw.Position.z; placed = false; }

                // Combat entity parked off-map (a roaming boss) → take its location from the map token.
                if (!placed && tokenPos.TryGetValue(guid, out var tp)) { px = tp.x; pz = tp.z; placed = true; }
                // Still off-map? A boss can be a Follower of an on-map parent (KindredCommands pattern).
                if (!placed && TryFollowerPosition(e, out var fx, out var fz)) { px = fx; pz = fz; placed = true; }
                var pos = new Unity.Mathematics.float3(px, hasLtw ? ltw.Position.y : 0f, pz);

                BossSnapshot snap;
                if (placed)
                {
                    int tindex = Core.Castle.GetTerritoryIndexAt(pos);
                    string region = Core.Castle.GetWorldRegionName(pos) ?? Core.Castle.GetRegionForTerritory(tindex);
                    snap = new BossSnapshot
                    {
                        Guid = guid,
                        Name = new PrefabGUID(guid).GetPrefabName(),
                        Alive = true,
                        Defeated = Core.Unlock.IsDefeatedAnywhere(guid),
                        X = pos.x,
                        Z = pos.z,
                        Region = region,
                        Hp = hp.Value,
                        HpMax = hp.MaxHealth._Value,
                        Level = e.TryGetComponent<UnitLevel>(out var ul) ? ul.Level._Value : 0,
                    };
                }
                else
                {
                    snap = new BossSnapshot
                    {
                        Guid = guid,
                        Name = new PrefabGUID(guid).GetPrefabName(),
                        Alive = false, // pooled/staged — present as a not-on-the-map (down) boss, no coords
                        Defeated = Core.Unlock.IsDefeatedAnywhere(guid),
                    };
                }

                // A genuinely-placed (up) instance wins over a pooled (down) one for the same boss guid.
                if (!byGuid.TryGetValue(guid, out var existing) || (!existing.Alive && snap.Alive))
                    byGuid[guid] = snap;
            }
        }
        finally { ents.Dispose(); }

        // 2) Defeated-but-not-currently-spawned bosses (from Faust's own cross-player defeat record).
        foreach (var guid in Core.Unlock.AllDefeatedGuids())
        {
            if (byGuid.ContainsKey(guid)) continue; // already listed as live
            byGuid[guid] = new BossSnapshot
            {
                Guid = guid,
                Name = new PrefabGUID(guid).GetPrefabName(),
                Alive = false,
                Defeated = true,
            };
        }

        var list = new List<BossSnapshot>(byGuid.Values);
        list.Sort((a, b) =>
        {
            if (a.Alive != b.Alive) return a.Alive ? -1 : 1;       // live first
            return string.CompareOrdinal(a.Name, b.Name);          // then by name
        });
        return list;
    }

    /// <summary>Map-token location sources for ROAMING bosses whose combat entity is parked off-map. The game
    /// tracks a roaming V Blood's real position separately so it can show it on the map without a player
    /// nearby; we read those, keyed by V Blood prefab GUID, and the caller uses them as the location while the
    /// boss's status/health still come from the combat entity. Each source is independent and guarded — a
    /// missing/odd one is skipped, never breaking the others. Returns guid → on-map (x,z).</summary>
    static Dictionary<int, (float x, float z)> BuildMapTokenPositions()
    {
        var map = new Dictionary<int, (float x, float z)>();

        // (a) Blood-altar V Blood tracking — Script_BloodAltar_TrackVBloodUnit_Shared { TrackedUnit,
        //     TrackPosition }: the live position the altar map shows for a tracked V Blood, keyed directly by
        //     prefab GUID. The most authoritative roamer source when present.
        try
        {
            var ents = Query.GetEntitiesByComponentType<Script_BloodAltar_TrackVBloodUnit_Shared>(includeDisabled: true);
            try
            {
                for (int i = 0; i < ents.Length; i++)
                {
                    if (!ents[i].TryGetComponent<Script_BloodAltar_TrackVBloodUnit_Shared>(out var t)) continue;
                    int g = t.TrackedUnit._Value;
                    if (g != 0 && OnMap(t.TrackPosition.x, t.TrackPosition.z))
                        map[g] = (t.TrackPosition.x, t.TrackPosition.z);
                }
            }
            finally { ents.Dispose(); }
        }
        catch (System.Exception ex) { Core.Log.LogWarning($"[FAUST BOSS] altar-track source failed: {ex.Message}"); }

        // (b) Map icons that target a V Blood unit (the "map token"). The icon entity sits at the boss's
        //     displayed position; MapIconData.TargetUser points at the unit. Altar-track wins if both exist.
        try
        {
            var icons = Query.GetEntitiesByComponentType<MapIconData>(includeDisabled: true);
            try
            {
                for (int i = 0; i < icons.Length; i++)
                {
                    var ic = icons[i];
                    if (!ic.TryGetComponent<MapIconData>(out var md)) continue;
                    var target = md.TargetUser;
                    if (target == Entity.Null || !target.Exists() || !target.Has<VBloodUnit>()) continue;
                    int g = target.GetPrefabGuid()._Value;
                    if (g == 0 || map.ContainsKey(g)) continue;
                    float x, z;
                    if (ic.TryGetComponent<LocalToWorld>(out var ltw)) { x = ltw.Position.x; z = ltw.Position.z; }
                    else if (ic.TryGetComponent<Translation>(out var tr)) { x = tr.Value.x; z = tr.Value.z; }
                    else continue;
                    if (OnMap(x, z)) map[g] = (x, z);
                }
            }
            finally { icons.Dispose(); }
        }
        catch (System.Exception ex) { Core.Log.LogWarning($"[FAUST BOSS] map-icon source failed: {ex.Message}"); }

        // (c) V Blood OBJECTIVE map icons — the world-map markers that show a boss's lair even when its
        //     combat entity is streamed out at the off-map sentinel (10000,10000). These link to the boss
        //     via MapIconTargetEntity.TargetEntity (a direct server-side Entity), NOT MapIconData.TargetUser
        //     (which is for player/user icons) — that mismatch is why (b) found nothing for parked bosses.
        //     The icon's own transform is the lair's on-map position; an attached icon that merely follows a
        //     sentinel boss reads off-map and is skipped by the OnMap guard, so only a real lair marker wins.
        try
        {
            var icons = Query.GetEntitiesByComponentType<MapIconTargetEntity>(includeDisabled: true);
            try
            {
                for (int i = 0; i < icons.Length; i++)
                {
                    var ic = icons[i];
                    if (!ic.TryGetComponent<MapIconTargetEntity>(out var mte)) continue;
                    var target = mte.TargetEntity._Entity;
                    if (target == Entity.Null || !target.Exists() || !target.Has<VBloodUnit>()) continue;
                    int g = target.GetPrefabGuid()._Value;
                    if (g == 0 || map.ContainsKey(g)) continue;
                    float x, z;
                    if (ic.TryGetComponent<LocalToWorld>(out var ltw)) { x = ltw.Position.x; z = ltw.Position.z; }
                    else if (ic.TryGetComponent<Translation>(out var tr)) { x = tr.Value.x; z = tr.Value.z; }
                    else continue;
                    if (OnMap(x, z)) map[g] = (x, z);
                }
            }
            finally { icons.Dispose(); }
        }
        catch (System.Exception ex) { Core.Log.LogWarning($"[FAUST BOSS] map-target source failed: {ex.Message}"); }

        return map;
    }

    /// <summary>A boss whose own transform is off-map may be a <see cref="Follower"/> of an on-map parent
    /// (the KindredCommands boss-locate pattern). Returns the followed entity's on-map position if so.</summary>
    static bool TryFollowerPosition(Entity e, out float x, out float z)
    {
        x = z = 0f;
        if (!e.TryGetComponent<Follower>(out var f)) return false;
        var followed = f.Followed._Value;
        if (followed == Entity.Null || !followed.Exists()) return false;
        if (followed.TryGetComponent<Translation>(out var tr) && OnMap(tr.Value.x, tr.Value.z)) { x = tr.Value.x; z = tr.Value.z; return true; }
        if (followed.TryGetComponent<LocalToWorld>(out var ltw) && OnMap(ltw.Position.x, ltw.Position.z)) { x = ltw.Position.x; z = ltw.Position.z; return true; }
        return false;
    }

    /// <summary>One boss by name fragment or prefab GUID. Returns false if nothing matches.</summary>
    public bool TryGetBoss(string nameOrGuid, out BossSnapshot snapshot)
    {
        snapshot = default;
        var all = GetBosses();
        bool wantGuid = int.TryParse(nameOrGuid, out int guid);

        BossSnapshot? exact = null, partial = null;
        int partialCount = 0;
        foreach (var b in all)
        {
            if (wantGuid && b.Guid == guid) { snapshot = b; return true; }
            if (b.Name is null) continue;
            if (string.Equals(b.Name, nameOrGuid, System.StringComparison.OrdinalIgnoreCase)) exact = b;
            else if (b.Name.Contains(nameOrGuid, System.StringComparison.OrdinalIgnoreCase)) { partial = b; partialCount++; }
        }
        if (exact.HasValue) { snapshot = exact.Value; return true; }
        if (partial.HasValue && partialCount == 1) { snapshot = partial.Value; return true; }
        return false;
    }

    /// <summary>Raw diagnostics for the boss board (Raphael §18 — "many bosses report down"). Scans every
    /// VBlood entity and reports the component/position picture so we can see, on a LIVE server, WHY a boss
    /// is classified down: is it pooled at the off-map sentinel, is MapLimit mis-tuned vs the real spawn
    /// coords, or is the placed instance excluded by the VBloodConsumeSource filter? Returns chat-ready
    /// lines: a summary, then (when a name/guid filter is given) one detail row per matching entity.</summary>
    public List<string> Diagnose(string filter)
    {
        // 'icons' routes to the map-icon picture (does the game's own boss map-icon carry a usable
        // on-map position for a roamer whose combat entity is parked off-map?).
        if (!string.IsNullOrWhiteSpace(filter) &&
            (filter.Equals("icons", System.StringComparison.OrdinalIgnoreCase)
             || filter.Equals("mapicons", System.StringComparison.OrdinalIgnoreCase)))
            return DiagnoseIcons();

        bool wantGuid = int.TryParse(filter, out int fGuid);
        bool hasFilter = !string.IsNullOrWhiteSpace(filter);
        int total = 0, withCs = 0, alive = 0, placedL = 0, limboL = 0, noLtw = 0, disabled = 0;
        int placedT = 0, limboT = 0, noTr = 0, differ = 0, rescued = 0;
        var rows = new List<string>();

        // The map-token positions (altar-track + map-icon) the combine uses for off-map roamers. Counting how
        // many off-by-both-components bosses a token rescues tells us directly whether the combine is working.
        var tokens = BuildMapTokenPositions();

        // Placed bounds (the real-map extent we observe) and limbo clusters (where the off-map entities
        // actually sit). These answer the open question: are the "down" bosses pooled at ONE sentinel point
        // (→ no real location on the live entity), or spread at plausible far coords (→ MapLimit too low)?
        // We measure position TWO ways — LocalToWorld (the render matrix, which can go stale for a
        // streamed-out/pooled entity) AND Translation (the value the sim updates each tick). If the
        // Translation reads on-map while LocalToWorld reads off-map, the board is simply reading the wrong
        // component — a one-line fix. Cluster only consume-source limbo entities (the ones that read "down").
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        var clusters = new Dictionary<(int, int), int>();     // off-map LocalToWorld clusters (CS only)
        var clustersT = new Dictionary<(int, int), int>();    // off-map Translation clusters (CS only)

        var ents = Query.GetEntitiesByComponentType<VBloodUnit>(includeDisabled: true);
        try
        {
            for (int i = 0; i < ents.Length; i++)
            {
                var e = ents[i];
                total++;
                int guid = e.GetPrefabGuid()._Value;
                string name = new PrefabGUID(guid).GetPrefabName();

                bool hasCs = e.Has<VBloodConsumeSource>();
                if (hasCs) withCs++;
                bool hasHp = e.TryGetComponent<Health>(out var h);
                if (hasHp && h.Value > 0) alive++;
                bool hasLtw = e.TryGetComponent<LocalToWorld>(out var ltw);
                bool hasTr = e.TryGetComponent<Translation>(out var tr);
                bool isDisabled = e.Has<Disabled>();
                if (isDisabled) disabled++;

                float x = hasLtw ? ltw.Position.x : 0f, z = hasLtw ? ltw.Position.z : 0f;
                float tx = hasTr ? tr.Value.x : 0f, tz = hasTr ? tr.Value.z : 0f;
                bool placedByLtw = hasLtw && OnMap(x, z);
                bool placedByTr = hasTr && OnMap(tx, tz);

                if (!hasLtw) noLtw++;
                else if (placedByLtw)
                {
                    placedL++;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                }
                else { limboL++; if (hasCs) Bump(clusters, x, z); }

                if (!hasTr) noTr++;
                else if (placedByTr) placedT++;
                else { limboT++; if (hasCs) Bump(clustersT, tx, tz); }

                // The money signal: entity off-map by LocalToWorld but ON-map by Translation.
                if (hasCs && hasLtw && hasTr && !placedByLtw && placedByTr) differ++;
                // A boss off-map by BOTH components but rescued by a map token — the combine in action.
                bool hasToken = tokens.TryGetValue(guid, out var tokPos);
                if (hasCs && !placedByLtw && !placedByTr && hasToken) rescued++;

                bool match = hasFilter && (wantGuid ? guid == fGuid
                    : name.Contains(filter, System.StringComparison.OrdinalIgnoreCase));
                if (match)
                    rows.Add($"{name} guid={guid} ltw=({F(x)},{F(z)}) tr=({F(tx)},{F(tz)}) " +
                             $"token={(hasToken ? $"({F(tokPos.x)},{F(tokPos.z)})" : "-")} cs={B(hasCs)} " +
                             $"hp={(hasHp ? F(h.Value) : "-")}/{(hasHp ? F(h.MaxHealth._Value) : "-")} " +
                             $"disabled={B(isDisabled)}");
            }
        }
        finally { ents.Dispose(); }

        var lines = new List<string>
        {
            $"VBlood: {total} total · {withCs} cs · {alive} alive · LTW[{placedL} on/{limboL} off/{noLtw} none] · " +
            $"TR[{placedT} on/{limboT} off/{noTr} none] · {disabled} disabled · off-LTW-but-on-TR(cs)={differ}.",
        };
        if (placedL > 0)
            lines.Add($"Placed bounds (LTW): x[{F(minX)}..{F(maxX)}] z[{F(minZ)}..{F(maxZ)}].");
        if (clusters.Count > 0) lines.Add(ClusterLine("Off-map LTW", clusters));
        if (clustersT.Count > 0) lines.Add(ClusterLine("Off-map TR", clustersT));
        if (differ > 0)
            lines.Add($"⇒ {differ} consume-source bosses are off-map by LocalToWorld but ON-map by Translation " +
                      "— the board should read Translation.");
        lines.Add($"Map tokens: {tokens.Count} V Blood position(s) resolved (altar-track + TargetUser-icon + " +
                  $"TargetEntity-icon); {rescued} off-map boss(es) rescued by a token (the combine). " +
                  $"{(tokens.Count == 0 ? "NONE found — no token source carries positions on this server; run '.faust admin bossdiag icons' for the breakdown." : "")}");
        if (hasFilter)
        {
            lines.Add($"Matches for '{filter}': {rows.Count}");
            lines.AddRange(rows);
        }
        else if (clusters.Count == 0 && limboL == 0)
            lines.Add("No off-map bosses (LTW). Add a name/guid to dump rows, or 'icons' for the map-icon picture.");
        return lines;
    }

    // The off-map PARKING SENTINEL: a streamed-out / not-currently-placed boss entity sits at ~(10000,10000)
    // (observed exactly 10000,10000 on a live server). It is junk, not a location, so it is NEVER "on the map"
    // regardless of MapLimit — otherwise raising MapLimit to 10000 surfaces the bogus coords (exactly what
    // happened). Keeping it off-map forces such bosses through the map-token combine, or to honest 'down'.
    const float Sentinel = 10000f;
    static bool IsSentinel(float x, float z) =>
        System.Math.Abs(System.Math.Abs(x) - Sentinel) < 250f && System.Math.Abs(System.Math.Abs(z) - Sentinel) < 250f;
    static bool OnMap(float x, float z) =>
        !IsSentinel(x, z) && System.Math.Abs(x) <= MapLimit && System.Math.Abs(z) <= MapLimit;
    static void Bump(Dictionary<(int, int), int> map, float x, float z)
    {
        var key = ((int)System.Math.Round(x / 500f) * 500, (int)System.Math.Round(z / 500f) * 500);
        map[key] = map.TryGetValue(key, out var c) ? c + 1 : 1;
    }
    static string ClusterLine(string label, Dictionary<(int, int), int> map)
    {
        var top = map.OrderByDescending(kv => kv.Value).Take(6).ToList();
        return $"{label} clusters (~500u, {map.Count} distinct): " +
               string.Join(" · ", top.Select(kv => $"({kv.Key.Item1},{kv.Key.Item2})x{kv.Value}"));
    }

    /// <summary>Map-icon picture: does the game's own map-icon for a VBlood carry an on-map position when the
    /// boss's combat entity is parked off-map? Reports how icons link to their unit (TargetUser entity) and
    /// where the icon sits, so we know whether the icon is a usable position source for roaming bosses.</summary>
    List<string> DiagnoseIcons()
    {
        int total = 0, withTarget = 0, vblood = 0, iconOnMap = 0, iconOffMap = 0, iconNoPos = 0, hasTile = 0, hasTargetEnt = 0;
        var rows = new List<string>();

        var icons = Query.GetEntitiesByComponentType<MapIconData>(includeDisabled: true);
        try
        {
            for (int i = 0; i < icons.Length; i++)
            {
                var ic = icons[i];
                total++;
                if (!ic.TryGetComponent<MapIconData>(out var md)) continue;
                if (ic.Has<MapIconTargetEntity>()) hasTargetEnt++;
                if (ic.Has<MapIconPosition>()) hasTile++;

                var target = md.TargetUser;
                bool tgtOk = target != Entity.Null && target.Exists();
                if (tgtOk) withTarget++;
                bool isVb = tgtOk && target.Has<VBloodUnit>();
                if (!isVb) continue;
                vblood++;

                float ix = 0f, iz = 0f; bool iconHasPos = false;
                if (ic.TryGetComponent<LocalToWorld>(out var ltw)) { ix = ltw.Position.x; iz = ltw.Position.z; iconHasPos = true; }
                else if (ic.TryGetComponent<Translation>(out var tr)) { ix = tr.Value.x; iz = tr.Value.z; iconHasPos = true; }

                if (!iconHasPos) iconNoPos++;
                else if (OnMap(ix, iz)) iconOnMap++;
                else iconOffMap++;

                if (rows.Count < 15)
                {
                    int guid = target.GetPrefabGuid()._Value;
                    float ux = 0f, uz = 0f;
                    if (target.TryGetComponent<LocalToWorld>(out var ul)) { ux = ul.Position.x; uz = ul.Position.z; }
                    rows.Add($"{new PrefabGUID(guid).GetPrefabName()} icon=({(iconHasPos ? $"{F(ix)},{F(iz)}" : "-")}) " +
                             $"unit=({F(ux)},{F(uz)})");
                }
            }
        }
        finally { icons.Dispose(); }

        var lines = new List<string>
        {
            $"MapIcons: {total} total · {withTarget} w/TargetUser · {vblood} target a VBlood · " +
            $"{hasTargetEnt} w/MapIconTargetEntity · {hasTile} w/TilePosition.",
            $"VBlood-icon positions (via TargetUser): {iconOnMap} on-map · {iconOffMap} off-map · {iconNoPos} no-position.",
        };

        // The MapIconTargetEntity link (source c) — the one boss objective icons actually use. This section
        // answers definitively: do on-map VBlood lair markers exist, and via which field do they resolve?
        lines.AddRange(DiagnoseTargetEntityIcons());

        if (vblood == 0)
            lines.Add("No VBlood-linked icons via TargetUser — boss icons link via MapIconTargetEntity (see below).");
        lines.AddRange(rows);
        return lines;
    }

    /// <summary>Definitive picture for the MapIconTargetEntity link that V Blood objective icons use: how
    /// many such icons resolve to a VBlood (via the direct Entity), how many carry an on-map world position
    /// (the lair marker that rescues a sentinel-parked boss), and — for calibration — the raw TilePosition
    /// alongside the icon's world transform so the tile→world formula can be confirmed against real data.</summary>
    List<string> DiagnoseTargetEntityIcons()
    {
        int total = 0, vb = 0, nullEnt = 0, onMap = 0, offMap = 0, noPos = 0, withTile = 0;
        var rows = new List<string>();
        try
        {
            var icons = Query.GetEntitiesByComponentType<MapIconTargetEntity>(includeDisabled: true);
            try
            {
                for (int i = 0; i < icons.Length; i++)
                {
                    var ic = icons[i];
                    total++;
                    if (!ic.TryGetComponent<MapIconTargetEntity>(out var mte)) continue;
                    var target = mte.TargetEntity._Entity;
                    if (target == Entity.Null || !target.Exists()) { nullEnt++; continue; }
                    if (!target.Has<VBloodUnit>()) continue;
                    vb++;

                    float wx = 0f, wz = 0f; bool hasPos = false;
                    if (ic.TryGetComponent<LocalToWorld>(out var ltw)) { wx = ltw.Position.x; wz = ltw.Position.z; hasPos = true; }
                    else if (ic.TryGetComponent<Translation>(out var tr)) { wx = tr.Value.x; wz = tr.Value.z; hasPos = true; }
                    if (!hasPos) noPos++;
                    else if (OnMap(wx, wz)) onMap++;
                    else offMap++;

                    bool hasTileP = ic.TryGetComponent<MapIconPosition>(out var mp);
                    if (hasTileP) withTile++;

                    if (rows.Count < 15)
                    {
                        int guid = target.GetPrefabGuid()._Value;
                        // tile→world estimate via the territory grid formula (world = (grid-6400)/2); shown so
                        // we can confirm whether TilePosition shares that space against a known on-map boss.
                        string tile = hasTileP
                            ? $"tile=({mp.TilePosition.x},{mp.TilePosition.y}) tilewc=({F((mp.TilePosition.x - 6400) / 2f)},{F((mp.TilePosition.y - 6400) / 2f)})"
                            : "tile=-";
                        rows.Add($"{new PrefabGUID(guid).GetPrefabName()} world={(hasPos ? $"({F(wx)},{F(wz)})" : "-")} {tile}");
                    }
                }
            }
            finally { icons.Dispose(); }
        }
        catch (System.Exception ex) { return new List<string> { $"MapIconTargetEntity scan failed: {ex.Message}" }; }

        var outp = new List<string>
        {
            $"MapIconTargetEntity: {total} total · {vb} resolve to a VBlood (direct Entity) · {nullEnt} null/unsynced Entity · {withTile} w/TilePosition.",
            $"VBlood target-icon positions: {onMap} ON-map (these rescue parked bosses) · {offMap} off-map · {noPos} no-world-pos.",
        };
        if (vb == 0 && nullEnt > 0)
            outp.Add("⇒ VBlood target-icons resolve with a NULL Entity — need NetworkId resolution (TargetNetworkId) to link them.");
        else if (vb == 0)
            outp.Add("⇒ No MapIconTargetEntity icons target a VBlood — boss lair positions are NOT in live map icons on this server.");
        else if (onMap == 0)
            outp.Add("⇒ VBlood target-icons exist but NONE carry an on-map world transform — lair pos may live in TilePosition only (see tilewc estimate).");
        outp.AddRange(rows);
        return outp;
    }

    static string F(float v) => v.ToString("0", CultureInfo.InvariantCulture);
    static string B(bool b) => b ? "1" : "0";
}

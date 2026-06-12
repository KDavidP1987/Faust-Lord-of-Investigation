using System.Collections.Generic;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Gameplay.Systems;
using ProjectM.Network;
using ProjectM.Terrain;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;

namespace Faust.Services;

/// <summary>
/// The authoritative, global castle/territory view — feature #2 (castle/plot info) and #4 (plot
/// availability). Repackages the proven KindredCommands territory/heart/owner model
/// (CastleTerritoryService + CastleCommands fuel math) into plain data structs the API layer turns
/// into [FAUST:*] lines. All reads are on-demand; nothing is cached except the static
/// block-coordinate -> territory-index map (the map's territory layout never changes at runtime).
/// </summary>
internal sealed class CastleService
{
    const float BLOCK_SIZE = 10f;

    public enum PlotState { Unclaimed, Sealed, Fueled, Decaying }

    public readonly struct TerritoryInfo
    {
        public int TerritoryIndex { get; init; }
        public bool HasHeart { get; init; }
        public ulong OwnerSteamId { get; init; }
        public string OwnerName { get; init; }
        public bool OwnerOnline { get; init; }
        public long OwnerLastConnected { get; init; } // DateTime binary; 0 if unknown/unclaimed
        public string Region { get; init; }
        public int SizeBlocks { get; init; }
        public PlotState State { get; init; }
        public long DecaySeconds { get; init; }       // seconds of fuel left; -1 = never decays

        // ---- §8a Castle-Info extras (populated only for the single castleinfo lookup; -1/null = absent) ----
        public int HeartLevel { get; init; }   // -1 = not resolvable (no confirmable level field)
        public int Floors { get; init; }        // distinct storey levels among the castle's floors; -1 = absent
        public long ClaimedUnix { get; init; }  // heart placement time; -1 = not available in exposed ECS
        public string ClanName { get; init; }   // owning clan name; null/empty = solo / none
        public long TotalItems { get; init; }   // grand total item count (the resources header total); -1 = absent
    }

    /// <summary>Summed contents of every container in a castle — feature #6 (PvP raid intel).</summary>
    public readonly struct ResourceSummary
    {
        public int TerritoryIndex { get; init; }
        public ulong OwnerSteamId { get; init; }
        public string OwnerName { get; init; }
        public int Containers { get; init; }   // containers that held at least one item
        public long TotalItems { get; init; }   // grand total item count
        public List<(int guid, long qty, string name)> Items { get; init; } // distinct, qty-descending
        public int Prisoners { get; init; }     // §8b: prisoners held in the castle
        public List<(string name, string bloodType, int bloodQuality)> PrisonerList { get; init; } // §8b rows
    }

    // Lazily-built block-coord -> territory-index map (CastleTerritoryService pattern).
    Dictionary<int2, int> _blockToTerritory;
    // Lazily-built territory-index -> region name (the map's region layout never changes at runtime).
    Dictionary<int, string> _territoryRegion;
    // §11a: territory-index -> centroid world position (mean of its block coords, KC's transform).
    Dictionary<int, float2> _territoryCenters;
    // §11b: world bounds of the whole buildable map (min/max over every territory block).
    float2 _mapWorldMin, _mapWorldMax;
    bool _mapBoundsValid;

    // Block-coordinate -> world transform (KindredCommands' TerritoryLocationService): a 10-unit grid
    // centred on the map (offset 6400, halved). Each axis maps independently; .y is the world Z axis.
    static float BlockToWorld(float block) => (10f * block - 6400f) / 2f;

    void EnsureBlockMap()
    {
        if (_blockToTerritory != null) return;
        _blockToTerritory = new Dictionary<int2, int>();
        _territoryRegion = new Dictionary<int, string>();
        _territoryCenters = new Dictionary<int, float2>();
        int2 gmin = new(int.MaxValue, int.MaxValue), gmax = new(int.MinValue, int.MinValue);
        var territories = Query.GetEntitiesByComponentType<CastleTerritory>(includeDisabled: true);
        try
        {
            for (int i = 0; i < territories.Length; i++)
            {
                var idx = territories[i].Read<CastleTerritory>().CastleTerritoryIndex;
                // Only cache a REAL region: a None/out-of-bounds value is left absent so the lookup
                // returns null (→ '-' sentinel on the wire) rather than the literal "None".
                if (territories[i].TryGetComponent<TerritoryWorldRegion>(out var twr) && twr.Region != WorldRegionType.None)
                    _territoryRegion[idx] = RegionName(twr.Region);
                var blocks = Core.EntityManager.GetBuffer<CastleTerritoryBlocks>(territories[i]);
                long sumX = 0, sumZ = 0;
                for (int b = 0; b < blocks.Length; b++)
                {
                    var bc = blocks[b].BlockCoordinate;
                    _blockToTerritory[bc] = idx;
                    sumX += bc.x; sumZ += bc.y;
                    if (bc.x < gmin.x) gmin.x = bc.x; if (bc.x > gmax.x) gmax.x = bc.x;
                    if (bc.y < gmin.y) gmin.y = bc.y; if (bc.y > gmax.y) gmax.y = bc.y;
                }
                if (blocks.Length > 0)
                    _territoryCenters[idx] = new float2(
                        BlockToWorld((float)sumX / blocks.Length),
                        BlockToWorld((float)sumZ / blocks.Length));
            }
        }
        finally { territories.Dispose(); }

        if (gmax.x >= gmin.x)
        {
            _mapWorldMin = new float2(BlockToWorld(gmin.x), BlockToWorld(gmin.y));
            _mapWorldMax = new float2(BlockToWorld(gmax.x), BlockToWorld(gmax.y));
            _mapBoundsValid = true;
        }
    }

    /// <summary>Region name of a territory index, or null for the open world / no region (-1).</summary>
    public string GetRegionForTerritory(int territoryIndex)
    {
        if (territoryIndex < 0) return null;
        EnsureBlockMap();
        return _territoryRegion.TryGetValue(territoryIndex, out var r) ? r : null;
    }

    /// <summary>§11a: a territory's centroid world coords (x, z), or false when unresolvable (no blocks).</summary>
    public bool TryGetTerritoryCenter(int territoryIndex, out float x, out float z)
    {
        x = 0f; z = 0f;
        if (territoryIndex < 0) return false;
        EnsureBlockMap();
        if (!_territoryCenters.TryGetValue(territoryIndex, out var c)) return false;
        x = c.x; z = c.y;
        return true;
    }

    /// <summary>§11b: world bounds of the whole buildable map (min/max over all territory blocks).</summary>
    public bool TryGetMapWorldBounds(out float minX, out float minZ, out float maxX, out float maxZ)
    {
        EnsureBlockMap();
        minX = _mapWorldMin.x; minZ = _mapWorldMin.y; maxX = _mapWorldMax.x; maxZ = _mapWorldMax.y;
        return _mapBoundsValid;
    }

    // ---- World-region-by-position (the map's named regions, NOT castle territories) ----
    // The territory map only covers castle-buildable plots, so a player out in the open world has
    // no territory and thus no region. The game also publishes WorldRegionPolygon entities — one
    // convex/concave polygon per named map region (Farbane Woods, Dunley Farmlands, …) — so any
    // position resolves to its region by point-in-polygon, plot or not. (KindredCommands RegionService.)

    struct RegionPolygon
    {
        public WorldRegionType Region;
        public Aabb Bounds;       // fast reject before the point-in-polygon test
        public float2[] Vertices; // world (x, z)
    }

    List<RegionPolygon> _regionPolygons; // lazily built; the map's region layout never changes at runtime

    void EnsureRegionPolygons()
    {
        if (_regionPolygons != null) return;
        _regionPolygons = new List<RegionPolygon>();
        var polys = Query.GetEntitiesByComponentType<WorldRegionPolygon>(includeDisabled: true);
        try
        {
            for (int i = 0; i < polys.Length; i++)
            {
                if (!polys[i].Has<WorldRegionPolygonVertex>()) continue;
                var wrp = polys[i].Read<WorldRegionPolygon>();
                var buf = Core.EntityManager.GetBuffer<WorldRegionPolygonVertex>(polys[i]);
                if (buf.Length < 3) continue;
                var verts = new float2[buf.Length];
                for (int v = 0; v < buf.Length; v++) verts[v] = buf[v].VertexPos;
                _regionPolygons.Add(new RegionPolygon { Region = wrp.WorldRegion, Bounds = wrp.PolygonBounds, Vertices = verts });
            }
        }
        finally { polys.Dispose(); }
        if (Faust.Config.Settings.VerboseLogging.Value)
            Core.Log.LogInfo($"[FAUST CASTLE] world-region polygons cached: {_regionPolygons.Count}");
    }

    /// <summary>
    /// The named world region a position falls in (point-in-polygon over the map's WorldRegionPolygon
    /// set), or null if outside every region (open world / void / out-of-bounds). Unlike
    /// <see cref="GetRegionForTerritory"/> this works ANYWHERE on the map — it does not require the
    /// player to be standing on a castle plot.
    /// </summary>
    public string GetWorldRegionName(float3 pos)
    {
        EnsureRegionPolygons();
        foreach (var rp in _regionPolygons)
        {
            // Fast-reject on the x/z bounds only — region is a 2D map zone, so the player's (or a
            // sampled plot's) Y must not gate it (a sample position may use Y=0).
            if (pos.x < rp.Bounds.Min.x || pos.x > rp.Bounds.Max.x ||
                pos.z < rp.Bounds.Min.z || pos.z > rp.Bounds.Max.z) continue;
            if (IsPointInPolygon(rp.Vertices, pos.x, pos.z))
                return RegionName(rp.Region);
        }
        return null; // WorldRegionType.None / outside all polygons -> no region
    }

    /// <summary>
    /// The region for a whole territory: its <c>TerritoryWorldRegion</c> when that resolves to a real
    /// region, else a point-in-polygon lookup at a sampled tile of the plot (covers territories whose
    /// component is unset), else null (genuine out-of-bounds → '-' on the wire). Works for claimed and
    /// open plots alike — no heart required.
    /// </summary>
    string ResolveTerritoryRegion(Entity territoryEntity)
    {
        if (territoryEntity.TryGetComponent<TerritoryWorldRegion>(out var twr) && twr.Region != WorldRegionType.None)
            return RegionName(twr.Region);
        if (TryGetTerritorySamplePosition(territoryEntity, out var pos))
            return GetWorldRegionName(pos);
        return null;
    }

    /// <summary>A representative world position for a territory (the centre of its first build tile),
    /// for region resolution. False if the territory has no blocks.</summary>
    bool TryGetTerritorySamplePosition(Entity territoryEntity, out float3 pos)
    {
        pos = default;
        if (!Core.EntityManager.HasComponent<CastleTerritoryBlocks>(territoryEntity)) return false;
        var blocks = Core.EntityManager.GetBuffer<CastleTerritoryBlocks>(territoryEntity);
        if (blocks.Length == 0) return false;
        pos = BlockCoordToWorldCentre(blocks[0].BlockCoordinate);
        return true;
    }

    /// <summary>Inverse of <see cref="ConvertPosToBlockCoord"/>: a block coord → the world position at
    /// that tile's centre (Y=0; region lookup is x/z only).</summary>
    static float3 BlockCoordToWorldCentre(int2 block)
    {
        float gridX = block.x * BLOCK_SIZE + BLOCK_SIZE / 2f;
        float gridZ = block.y * BLOCK_SIZE + BLOCK_SIZE / 2f;
        return new float3((gridX - 6400f) / 2f, 0f, (gridZ - 6400f) / 2f);
    }

    /// <summary>Standard ray-casting point-in-polygon (vertices are world x/z).</summary>
    static bool IsPointInPolygon(float2[] polygon, float px, float pz)
    {
        int intersections = 0;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            if ((polygon[i].y > pz) != (polygon[j].y > pz) &&
                px < (polygon[j].x - polygon[i].x) * (pz - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].x)
                intersections++;
        }
        return (intersections & 1) != 0;
    }

    static float3 ConvertPosToGrid(float3 pos) =>
        new(Mathf.FloorToInt(pos.x * 2) + 6400, pos.y, Mathf.FloorToInt(pos.z * 2) + 6400);

    static int2 ConvertPosToBlockCoord(float3 pos)
    {
        var grid = ConvertPosToGrid(pos);
        return new int2((int)math.floor(grid.x / BLOCK_SIZE), (int)math.floor(grid.z / BLOCK_SIZE));
    }

    /// <summary>Territory index containing a world position, or -1 if outside any territory.</summary>
    public int GetTerritoryIndexAt(float3 pos)
    {
        EnsureBlockMap();
        return _blockToTerritory.TryGetValue(ConvertPosToBlockCoord(pos), out var idx) ? idx : -1;
    }

    /// <summary>Territory index of the castle heart nearest a position, or -1 if none.</summary>
    public int GetNearestHeartTerritory(float3 pos)
    {
        var hearts = Query.GetEntitiesByComponentType<CastleHeart>(includeDisabled: true);
        int best = -1;
        float bestDistSq = float.MaxValue;
        try
        {
            for (int i = 0; i < hearts.Length; i++)
            {
                if (!hearts[i].Has<LocalToWorld>()) continue;
                var hp = hearts[i].Read<LocalToWorld>().Position;
                float d = math.distancesq(pos, hp);
                if (d >= bestDistSq) continue;
                var terr = hearts[i].Read<CastleHeart>().CastleTerritoryEntity;
                if (!terr.Exists()) continue;
                bestDistSq = d;
                best = terr.Read<CastleTerritory>().CastleTerritoryIndex;
            }
        }
        finally { hearts.Dispose(); }
        return best;
    }

    /// <summary>Full info for a single territory index. HasHeart=false means it isn't resolvable.
    /// <paramref name="extras"/> = compute the §8a Castle-Info extras (heart level / floors / clan /
    /// item-total) — a heart-connection scan, so only the one-at-a-time castleinfo lookup asks for it;
    /// the full-map/decay lists leave it off to stay cheap.</summary>
    public bool TryGetTerritory(int territoryIndex, out TerritoryInfo info, bool extras = false)
    {
        info = default;
        if (territoryIndex < 0) return false;

        var territories = Query.GetEntitiesByComponentType<CastleTerritory>(includeDisabled: true);
        try
        {
            for (int i = 0; i < territories.Length; i++)
            {
                var ct = territories[i].Read<CastleTerritory>();
                if (ct.CastleTerritoryIndex != territoryIndex) continue;
                info = BuildInfo(territories[i], ct, extras);
                return true;
            }
        }
        finally { territories.Dispose(); }
        return false;
    }

    /// <summary>All open (heart-less) plots, largest first — feature #4.</summary>
    public List<TerritoryInfo> GetFreePlots()
    {
        var result = new List<TerritoryInfo>();
        var territories = Query.GetEntitiesByComponentType<CastleTerritory>(includeDisabled: true);
        try
        {
            for (int i = 0; i < territories.Length; i++)
            {
                var ct = territories[i].Read<CastleTerritory>();
                if (ct.CastleHeart.Exists()) continue; // claimed -> not a free plot
                result.Add(BuildInfo(territories[i], ct));
            }
        }
        finally { territories.Dispose(); }
        result.Sort((a, b) => b.SizeBlocks.CompareTo(a.SizeBlocks));
        return result;
    }

    /// <summary>Every territory — claimed AND open — largest first. The full server castle map
    /// ("All Plots"); the API layer pages it into [FAUST:castle] rows for BCH.</summary>
    public List<TerritoryInfo> GetAllTerritories()
    {
        var result = new List<TerritoryInfo>();
        var territories = Query.GetEntitiesByComponentType<CastleTerritory>(includeDisabled: true);
        try
        {
            for (int i = 0; i < territories.Length; i++)
                result.Add(BuildInfo(territories[i], territories[i].Read<CastleTerritory>()));
        }
        finally { territories.Dispose(); }
        result.Sort((a, b) => b.SizeBlocks.CompareTo(a.SizeBlocks));
        return result;
    }

    /// <summary>Claimed castles ordered by soonest-to-decay first — the admin housekeeping view
    /// ("which castles are about to fall / abandoned"). Open plots are excluded (no heart to decay);
    /// sealed castles (never decay, DecaySeconds == -1) sort to the very end. Reads live heart fuel —
    /// no passive collection. Pair the row's decay + lastonline to spot abandoned plots.</summary>
    public List<TerritoryInfo> GetCastlesByDecay()
    {
        var result = new List<TerritoryInfo>();
        var territories = Query.GetEntitiesByComponentType<CastleTerritory>(includeDisabled: true);
        try
        {
            for (int i = 0; i < territories.Length; i++)
            {
                var ct = territories[i].Read<CastleTerritory>();
                if (!ct.CastleHeart.Exists()) continue; // open plot — nothing to decay
                result.Add(BuildInfo(territories[i], ct));
            }
        }
        finally { territories.Dispose(); }
        // Ascending by fuel remaining; sealed (-1 = infinite) treated as +infinity so it sorts last.
        result.Sort((a, b) =>
        {
            long da = a.DecaySeconds < 0 ? long.MaxValue : a.DecaySeconds;
            long db = b.DecaySeconds < 0 ? long.MaxValue : b.DecaySeconds;
            return da.CompareTo(db);
        });
        return result;
    }

    /// <summary>
    /// Sum every container's contents in the castle on a territory (feature #6). Returns false for
    /// an unclaimed/heart-less territory. Enumerates entities connected to the castle heart
    /// (CastleHeartConnection) — including stations — and totals their inventories. Admin/priced by
    /// default: this is a full heart-connected scan, so it's intended for an on-demand query.
    /// </summary>
    public bool TrySummarizeResources(int territoryIndex, out ResourceSummary summary)
    {
        summary = default;
        if (territoryIndex < 0) return false;

        Entity heart = Entity.Null;
        var territories = Query.GetEntitiesByComponentType<CastleTerritory>(includeDisabled: true);
        try
        {
            for (int i = 0; i < territories.Length; i++)
            {
                var ct = territories[i].Read<CastleTerritory>();
                if (ct.CastleTerritoryIndex != territoryIndex) continue;
                heart = ct.CastleHeart; break;
            }
        }
        finally { territories.Dispose(); }
        if (!heart.Exists()) return false; // unclaimed plot — no resources to report

        ulong steam = 0; string ownerName = "Unknown";
        if (heart.TryGetComponent<UserOwner>(out var uo) && uo.Owner.GetEntityOnServer().TryGetComponent<User>(out var u))
        {
            steam = u.PlatformId; ownerName = u.CharacterName.ToString();
        }

        var totals = new Dictionary<int, long>();
        int containers = 0; long totalItems = 0;
        var ents = Query.GetEntitiesByComponentType<CastleHeartConnection>(includeDisabled: true);
        try
        {
            for (int i = 0; i < ents.Length; i++)
            {
                var e = ents[i];
                if (!e.TryGetComponent<CastleHeartConnection>(out var c) || c.CastleHeartEntity.GetEntityOnServer() != heart) continue;
                if (!InventoryUtilities.TryGetInventoryEntity(Core.EntityManager, e, out Entity inv)) continue;
                if (!Core.EntityManager.HasComponent<InventoryBuffer>(inv)) continue;

                var buf = Core.EntityManager.GetBuffer<InventoryBuffer>(inv);
                bool counted = false;
                for (int b = 0; b < buf.Length; b++)
                {
                    var slot = buf[b];
                    int g = slot.ItemType.GuidHash;
                    if (g == 0 || slot.Amount <= 0) continue;
                    totals.TryGetValue(g, out var cur);
                    totals[g] = cur + slot.Amount;
                    totalItems += slot.Amount;
                    counted = true;
                }
                if (counted) containers++;
            }
        }
        finally { ents.Dispose(); }

        var items = new List<(int guid, long qty, string name)>(totals.Count);
        foreach (var kvp in totals)
            items.Add((kvp.Key, kvp.Value, new PrefabGUID(kvp.Key).GetPrefabName()));
        items.Sort((a, b) => b.qty.CompareTo(a.qty));

        var prisoners = CollectPrisoners(territoryIndex);

        summary = new ResourceSummary
        {
            TerritoryIndex = territoryIndex,
            OwnerSteamId = steam,
            OwnerName = ownerName,
            Containers = containers,
            TotalItems = totalItems,
            Items = items,
            Prisoners = prisoners.Count,
            PrisonerList = prisoners,
        };
        return true;
    }

    /// <summary>
    /// §8b: prisoners held in the castle on a territory. Prisoners are units carrying the game's
    /// <see cref="ImprisonedBuff"/> (a buff entity targeting the captured unit); we resolve each buff's
    /// target, attribute it by the unit's territory, and read its name + blood. Fully guarded — any
    /// resolution failure yields fewer/sentineled rows, never a throw (these IL2CPP reads are validated
    /// in-game). Blood fields sentinel to "-"/-1 when a unit carries no <see cref="Blood"/>.
    /// </summary>
    List<(string name, string bloodType, int bloodQuality)> CollectPrisoners(int territoryIndex)
    {
        var result = new List<(string, string, int)>();
        NativeArray<Entity> buffs = default;
        try
        {
            buffs = Query.GetEntitiesByComponentType<ImprisonedBuff>(includeDisabled: true);
            for (int i = 0; i < buffs.Length; i++)
            {
                Entity unit;
                try { unit = CreateGameplayEventServerUtility.GetBuffTarget(Core.EntityManager, buffs[i]); }
                catch { continue; }
                if (!unit.Exists() || !unit.Has<LocalToWorld>()) continue;
                if (GetTerritoryIndexAt(unit.Read<LocalToWorld>().Position) != territoryIndex) continue;

                string name = unit.Has<PrefabGUID>() ? unit.Read<PrefabGUID>().GetPrefabName() : "Prisoner";
                string bloodType = "-"; int bloodQuality = -1;
                if (unit.Has<Blood>())
                {
                    var blood = unit.Read<Blood>();
                    bloodType = blood.BloodType.GetPrefabName();
                    bloodQuality = (int)System.Math.Round(blood.Quality);
                }
                result.Add((name, bloodType, bloodQuality));
            }
        }
        catch (System.Exception ex)
        {
            if (Faust.Config.Settings.VerboseLogging.Value)
                Core.Log.LogWarning($"[FAUST CASTLE] CollectPrisoners failed: {ex.Message}");
        }
        finally { if (buffs.IsCreated) buffs.Dispose(); }
        return result;
    }

    TerritoryInfo BuildInfo(Entity territoryEntity, CastleTerritory ct, bool extras = false)
    {
        int size = 0;
        if (Core.EntityManager.HasComponent<CastleTerritoryBlocks>(territoryEntity))
            size = Core.EntityManager.GetBuffer<CastleTerritoryBlocks>(territoryEntity).Length;

        // Real region name, or null for genuine out-of-bounds (→ '-' sentinel on the wire via Wire.Region).
        string region = ResolveTerritoryRegion(territoryEntity);

        var heart = ct.CastleHeart;
        if (!heart.Exists())
        {
            return new TerritoryInfo
            {
                TerritoryIndex = ct.CastleTerritoryIndex,
                HasHeart = false,
                Region = region,
                SizeBlocks = size,
                State = PlotState.Unclaimed,
                DecaySeconds = 0,
                HeartLevel = -1, Floors = -1, ClaimedUnix = -1, ClanName = null, TotalItems = -1,
            };
        }

        var ch = heart.Read<CastleHeart>();
        ulong steam = 0; string ownerName = "Unknown"; bool online = false; long lastConnected = 0;
        Entity ownerUserEntity = Entity.Null;
        if (heart.TryGetComponent<UserOwner>(out var uo))
        {
            var ownerUser = uo.Owner.GetEntityOnServer();
            if (ownerUser.TryGetComponent<User>(out var u))
            {
                ownerUserEntity = ownerUser;
                steam = u.PlatformId;
                ownerName = u.CharacterName.ToString();
                online = u.IsConnected;
                lastConnected = u.TimeLastConnected;
            }
        }

        PlotState state;
        long decay;
        if (double.IsPositiveInfinity(ch.FuelEndTime))
        {
            state = PlotState.Sealed; decay = -1;
        }
        else
        {
            double remaining = FuelTimeRemaining(ch);
            bool fueled = (ch.FuelEndTime - Core.ServerTime) > 0 || ch.FuelQuantity > 0;
            state = fueled ? PlotState.Fueled : PlotState.Decaying;
            decay = (long)System.Math.Max(0, remaining);
        }

        // §8a extras — only for the single castleinfo lookup (one heart-connection scan). All sentineled
        // so a value Faust can't resolve degrades to "absent" on the wire (Raphael shows each only when present).
        int heartLevel = -1, floors = -1;
        long totalItems = -1;
        string clanName = null;
        if (extras)
        {
            ComputeHeartExtras(heart, out floors, out totalItems);
            heartLevel = ResolveHeartLevel(heart);
            clanName = ResolveOwnerClan(ownerUserEntity);
        }

        return new TerritoryInfo
        {
            TerritoryIndex = ct.CastleTerritoryIndex,
            HasHeart = true,
            OwnerSteamId = steam,
            OwnerName = ownerName,
            OwnerOnline = online,
            OwnerLastConnected = lastConnected,
            Region = region,
            SizeBlocks = size,
            State = state,
            DecaySeconds = decay,
            HeartLevel = heartLevel,
            Floors = floors,
            ClaimedUnix = -1, // no reliable heart-placement timestamp in exposed ECS (LastRelocationTime ≠ placement)
            ClanName = clanName,
            TotalItems = totalItems,
        };
    }

    /// <summary>§8a: one heart-connection scan computing the castle's floor storeys (distinct integer Y
    /// among connected <see cref="CastleFloor"/> tiles) and grand-total item count (every connected
    /// container's <see cref="InventoryBuffer"/>). Both sentinel to -1 on any failure.</summary>
    void ComputeHeartExtras(Entity heart, out int floors, out long totalItems)
    {
        floors = -1; totalItems = -1;
        try
        {
            var storeys = new HashSet<int>();
            long items = 0; bool sawContainer = false;
            var ents = Query.GetEntitiesByComponentType<CastleHeartConnection>(includeDisabled: true);
            try
            {
                for (int i = 0; i < ents.Length; i++)
                {
                    var e = ents[i];
                    if (!e.TryGetComponent<CastleHeartConnection>(out var c) || c.CastleHeartEntity.GetEntityOnServer() != heart) continue;

                    if (e.Has<CastleFloor>() && e.Has<LocalToWorld>())
                        storeys.Add((int)math.round(e.Read<LocalToWorld>().Position.y));

                    if (InventoryUtilities.TryGetInventoryEntity(Core.EntityManager, e, out Entity inv)
                        && Core.EntityManager.HasComponent<InventoryBuffer>(inv))
                    {
                        sawContainer = true;
                        var buf = Core.EntityManager.GetBuffer<InventoryBuffer>(inv);
                        for (int b = 0; b < buf.Length; b++)
                        {
                            var slot = buf[b];
                            if (slot.ItemType.GuidHash == 0 || slot.Amount <= 0) continue;
                            items += slot.Amount;
                        }
                    }
                }
            }
            finally { ents.Dispose(); }
            if (storeys.Count > 0) floors = storeys.Count;
            if (sawContainer) totalItems = items;
        }
        catch (System.Exception ex)
        {
            if (Faust.Config.Settings.VerboseLogging.Value)
                Core.Log.LogWarning($"[FAUST CASTLE] ComputeHeartExtras failed: {ex.Message}");
        }
    }

    /// <summary>§8a: the castle heart's level/tier, or -1 if no confirmable numeric level field is
    /// exposed. (Kept as a guarded stub — the game's <c>CastleHeartModelTier</c> is a visual marker with
    /// no documented numeric value; resolve in-game and wire a real value here when confirmed.)</summary>
    int ResolveHeartLevel(Entity heart) => -1;

    /// <summary>§8a: the owning clan's name from the owner User's clan link, or null if solo / unresolved.</summary>
    string ResolveOwnerClan(Entity ownerUserEntity)
    {
        try
        {
            if (!ownerUserEntity.Exists() || !ownerUserEntity.TryGetComponent<User>(out var u)) return null;
            if (u.ClanEntity.Equals(NetworkedEntity.Empty)) return null;
            var clan = u.ClanEntity.GetEntityOnServer();
            if (clan.Exists() && clan.Has<ClanTeam>())
            {
                var name = clan.Read<ClanTeam>().Name.ToString();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch (System.Exception ex)
        {
            if (Faust.Config.Settings.VerboseLogging.Value)
                Core.Log.LogWarning($"[FAUST CASTLE] ResolveOwnerClan failed: {ex.Message}");
        }
        return null;
    }

    static double FuelTimeRemaining(CastleHeart ch)
    {
        float drain = Mathf.Min(Core.ServerGameSettingsSystem.Settings.CastleBloodEssenceDrainModifier, 3f);
        if (drain <= 0f) drain = 1f;
        double secondsPerFuel = (8 * 60) / drain;
        return (ch.FuelEndTime - Core.ServerTime) + secondsPerFuel * ch.FuelQuantity;
    }

    /// <summary>"SilverlightHills" -> "Silverlight Hills" (KindredCommands' RegionName).</summary>
    public static string RegionName(WorldRegionType region) =>
        System.Text.RegularExpressions.Regex.Replace(region.ToString().Replace("_", ""), "(?<!^)([A-Z])", " $1");

    public static string StateWire(PlotState s) => s switch
    {
        PlotState.Sealed => "sealed",
        PlotState.Fueled => "fueled",
        PlotState.Decaying => "decaying",
        _ => "unclaimed",
    };
}

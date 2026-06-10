using System.Collections.Generic;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using ProjectM.Terrain;
using Stunlock.Core;
using Unity.Entities;
using Unity.Mathematics;
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
    }

    // Lazily-built block-coord -> territory-index map (CastleTerritoryService pattern).
    Dictionary<int2, int> _blockToTerritory;
    // Lazily-built territory-index -> region name (the map's region layout never changes at runtime).
    Dictionary<int, string> _territoryRegion;

    void EnsureBlockMap()
    {
        if (_blockToTerritory != null) return;
        _blockToTerritory = new Dictionary<int2, int>();
        _territoryRegion = new Dictionary<int, string>();
        var territories = Query.GetEntitiesByComponentType<CastleTerritory>(includeDisabled: true);
        try
        {
            for (int i = 0; i < territories.Length; i++)
            {
                var idx = territories[i].Read<CastleTerritory>().CastleTerritoryIndex;
                if (territories[i].TryGetComponent<TerritoryWorldRegion>(out var twr))
                    _territoryRegion[idx] = RegionName(twr.Region);
                var blocks = Core.EntityManager.GetBuffer<CastleTerritoryBlocks>(territories[i]);
                for (int b = 0; b < blocks.Length; b++)
                    _blockToTerritory[blocks[b].BlockCoordinate] = idx;
            }
        }
        finally { territories.Dispose(); }
    }

    /// <summary>Region name of a territory index, or null for the open world / no region (-1).</summary>
    public string GetRegionForTerritory(int territoryIndex)
    {
        if (territoryIndex < 0) return null;
        EnsureBlockMap();
        return _territoryRegion.TryGetValue(territoryIndex, out var r) ? r : null;
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

    /// <summary>Full info for a single territory index. HasHeart=false means it isn't resolvable.</summary>
    public bool TryGetTerritory(int territoryIndex, out TerritoryInfo info)
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
                info = BuildInfo(territories[i], ct);
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

        summary = new ResourceSummary
        {
            TerritoryIndex = territoryIndex,
            OwnerSteamId = steam,
            OwnerName = ownerName,
            Containers = containers,
            TotalItems = totalItems,
            Items = items,
        };
        return true;
    }

    TerritoryInfo BuildInfo(Entity territoryEntity, CastleTerritory ct)
    {
        int size = 0;
        if (Core.EntityManager.HasComponent<CastleTerritoryBlocks>(territoryEntity))
            size = Core.EntityManager.GetBuffer<CastleTerritoryBlocks>(territoryEntity).Length;

        string region = territoryEntity.TryGetComponent<TerritoryWorldRegion>(out var twr)
            ? RegionName(twr.Region) : "Unknown";

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
            };
        }

        var ch = heart.Read<CastleHeart>();
        ulong steam = 0; string ownerName = "Unknown"; bool online = false; long lastConnected = 0;
        if (heart.TryGetComponent<UserOwner>(out var uo))
        {
            var ownerUser = uo.Owner.GetEntityOnServer();
            if (ownerUser.TryGetComponent<User>(out var u))
            {
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
        };
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

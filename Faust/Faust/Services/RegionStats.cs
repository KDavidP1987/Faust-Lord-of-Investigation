using System;
using System.Collections.Generic;

namespace Faust.Services;

/// <summary>
/// Live per-region composition gathered from ECS — online players, claimed castles, and total buildable
/// plots per map region. Shared by the <c>stats regions</c> endpoint (§10b, the live view + the `plots`
/// fill-% denominator) and the §10c daily sampler (one snapshot/day persisted into <see cref="FaustStore"/>).
///
/// `plots` = every territory in the region (claimed + open) — the same universe <c>castles</c> walks — so
/// Raphael can chart <c>castles / plots</c> (fill %). `players` counts only ONLINE players (offline players
/// have no live position). Region <c>null</c> is the open-world / no-region bucket (wire sentinel '-').
/// </summary>
internal static class RegionStats
{
    /// <summary>One pass over online positions + all territories → (region, players, castles, plots),
    /// castles-descending. Region is null for open world. Safe to call only when <c>Core.IsReady</c>.</summary>
    public static List<(string region, int players, int castles, int plots)> Gather()
    {
        var map = new Dictionary<string, (int players, int castles, int plots)>(StringComparer.OrdinalIgnoreCase);

        foreach (var pos in Core.PlayerInfo.GetOnlinePositions())
        {
            string r = string.IsNullOrEmpty(pos.Region) ? "-" : pos.Region;
            map.TryGetValue(r, out var v);
            map[r] = (v.players + 1, v.castles, v.plots);
        }

        foreach (var t in Core.Castle.GetAllTerritories())
        {
            string r = string.IsNullOrEmpty(t.Region) ? "-" : t.Region;
            map.TryGetValue(r, out var v);
            map[r] = (v.players, v.castles + (t.HasHeart ? 1 : 0), v.plots + 1);
        }

        var list = new List<(string, int, int, int)>(map.Count);
        foreach (var kv in map)
            list.Add((kv.Key == "-" ? null : kv.Key, kv.Value.players, kv.Value.castles, kv.Value.plots));
        list.Sort((a, b) => b.Item3.CompareTo(a.Item3)); // castles-descending
        return list;
    }
}

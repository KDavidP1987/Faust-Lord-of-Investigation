using ProjectM;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Faust.Services;

/// <summary>
/// Proximity-to-object gate (ADMIN_CONTROL §1 axis 7): is the player within range of an instance of
/// a configured object? Scans placed/world tile objects (those with a <c>TilePosition</c>) for one
/// whose prefab matches and is within the radius. Only invoked when a feature actually configures a
/// proximity requirement, and these are on-demand queries — so a per-call scan of placed objects is
/// acceptable (not a hot path). ECS has no value-indexed query, hence the filtered scan.
/// </summary>
internal static class Proximity
{
    public static bool PlayerNear(Entity character, int prefabHash, float distance)
    {
        if (prefabHash == 0) return true;
        if (!character.TryGetComponent<LocalToWorld>(out var ltw)) return false;
        float3 origin = ltw.Position;
        float maxSq = distance * distance;

        var ents = Query.GetEntitiesByComponentType<TilePosition>(includeDisabled: true);
        try
        {
            for (int i = 0; i < ents.Length; i++)
            {
                var e = ents[i];
                if (e.GetPrefabGuid().GuidHash != prefabHash) continue;
                if (!e.TryGetComponent<LocalToWorld>(out var elt)) continue;
                if (math.distancesq(origin, elt.Position) <= maxSq) return true;
            }
        }
        finally { ents.Dispose(); }
        return false;
    }
}

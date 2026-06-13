using System;
using System.Collections.Generic;
using Stunlock.Core;

namespace Faust.Services;

/// <summary>
/// In-game prefab lookup — so an admin can find a prefab's ID or dev-name WITHOUT leaving the game to
/// consult an external dump. Powers '.faust admin prefab', the helper for every command that takes a
/// PrefabGUID (worldscan whitelist, item cost, proximity object, map-marker prefab, BossKill unlock, …).
/// Reads the runtime prefab catalog (<c>PrefabCollectionSystem._PrefabGuidToEntityMap</c> — the complete
/// set, the same source <see cref="EntityExtensions.GetPrefabName"/> resolves against).
/// </summary>
internal static class PrefabLookup
{
    /// <summary>Dev-name for a GUID (or the <c>PrefabGuid(N)</c> fallback when unknown).</summary>
    public static string Name(int guid) => new PrefabGUID(guid).GetPrefabName();

    /// <summary>True if the GUID resolves to a real, named prefab in the catalog.</summary>
    public static bool Exists(int guid)
    {
        var n = Name(guid);
        return !string.IsNullOrEmpty(n) && !n.StartsWith("PrefabGuid(", StringComparison.Ordinal);
    }

    /// <summary>All prefabs whose dev-name contains <paramref name="fragment"/> (case-insensitive), as
    /// (guid, name), name-sorted, capped at <paramref name="max"/> (the bool says whether the cap was hit).
    /// Iterates the full catalog — fine for an infrequent admin command, not a hot path.</summary>
    public static List<(int guid, string name)> Search(string fragment, int max, out bool capped)
    {
        capped = false;
        var results = new List<(int, string)>();
        if (string.IsNullOrWhiteSpace(fragment) || Core.PrefabCollectionSystem is null) return results;

        try
        {
            foreach (var kvp in Core.PrefabCollectionSystem._PrefabGuidToEntityMap)
            {
                var guid = kvp.Key;
                string name = guid.GetPrefabName();
                if (name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
                results.Add((guid._Value, name));
                if (results.Count >= max) { capped = true; break; }
            }
        }
        catch (Exception ex) { Core.Log.LogError($"[FAUST PREFAB] search failed: {ex.Message}"); }

        results.Sort((a, b) => string.CompareOrdinal(a.Item2, b.Item2));
        return results;
    }
}

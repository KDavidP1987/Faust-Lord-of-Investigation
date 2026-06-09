using System;
using ProjectM;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;

namespace Faust;

/// <summary>
/// IL2CPP-safe entity helpers. Mirrors the proven Uriel/Beelzebub/Bloodcraft extension patterns
/// (Exists guard before every EntityManager call). Read uses the typed GetComponentData path that
/// the sibling mods ship against the same VampireReferenceAssemblies set.
/// </summary>
internal static class EntityExtensions
{
    public static bool Exists(this Entity entity) =>
        entity != Entity.Null && Core.EntityManager.Exists(entity);

    public static bool Has<T>(this Entity entity) =>
        entity.Exists() && Core.EntityManager.HasComponent<T>(entity);

    public static T Read<T>(this Entity entity) where T : unmanaged =>
        Core.EntityManager.GetComponentData<T>(entity);

    public static bool TryGetComponent<T>(this Entity entity, out T component) where T : unmanaged
    {
        if (!entity.Exists() || !Core.EntityManager.HasComponent<T>(entity))
        {
            component = default;
            return false;
        }
        component = Core.EntityManager.GetComponentData<T>(entity);
        return true;
    }

    public static ulong GetSteamId(this Entity playerCharacter)
    {
        if (playerCharacter.TryGetComponent<PlayerCharacter>(out var pc)
            && pc.UserEntity.TryGetComponent<User>(out var user))
        {
            return user.PlatformId;
        }
        return 0;
    }

    public static PrefabGUID GetPrefabGuid(this Entity entity) =>
        entity.TryGetComponent<PrefabGUID>(out var g) ? g : default;

    /// <summary>
    /// Resolve a player by character-name fragment OR literal steamId. Walks all User entities
    /// (online + persisted offline). Name matches must be unique; an exact (case-insensitive)
    /// name wins over substring matches.
    /// </summary>
    public static bool TryResolvePlayer(string nameOrId, out ulong steamId, out string resolvedName, out Entity userEntity, out string error)
    {
        steamId = 0;
        resolvedName = null;
        userEntity = Entity.Null;
        error = null;
        if (string.IsNullOrWhiteSpace(nameOrId)) { error = "Provide a character name or steamId."; return false; }

        bool wantSteam = ulong.TryParse(nameOrId, out ulong literal) && literal > 1000;

        var users = Query.GetEntitiesByComponentType<User>(includeDisabled: true);
        int matches = 0;
        try
        {
            for (int i = 0; i < users.Length; i++)
            {
                if (!users[i].TryGetComponent<User>(out var u)) continue;

                if (wantSteam)
                {
                    if (u.PlatformId != literal) continue;
                    steamId = u.PlatformId; resolvedName = u.CharacterName.ToString(); userEntity = users[i];
                    return true;
                }

                string name = u.CharacterName.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                if (!name.Contains(nameOrId, StringComparison.OrdinalIgnoreCase)) continue;
                matches++;
                steamId = u.PlatformId; resolvedName = name; userEntity = users[i];
                if (string.Equals(name, nameOrId, StringComparison.OrdinalIgnoreCase)) { matches = 1; break; }
            }
        }
        finally { users.Dispose(); }

        if (wantSteam) { error = $"No player with steamId {literal}."; return false; }
        if (matches == 0) { error = $"No player matches '{nameOrId}'."; return false; }
        if (matches > 1) { error = $"'{nameOrId}' matches multiple players — be more specific or use the steamId."; steamId = 0; userEntity = Entity.Null; return false; }
        return true;
    }

    /// <summary>Resolve a prefab GUID to its dev name via the runtime prefab lookup map.</summary>
    public static string GetPrefabName(this PrefabGUID prefabGuid)
    {
        try
        {
            var map = Core.PrefabCollectionSystem._PrefabLookupMap;
            if (map.GuidToEntityMap.ContainsKey(prefabGuid))
                return map.GetName(prefabGuid);
        }
        catch { /* lookup map shape can vary; raw hash fallback below */ }
        return $"PrefabGuid({prefabGuid._Value})";
    }
}

using Il2CppInterop.Runtime;
using ProjectM.Network;
using Unity.Collections;
using Unity.Entities;

namespace Faust;

/// <summary>
/// Thin ECS query helpers (ported from KindredCommands' Helper). The IL2CPP query builder must be
/// constructed lazily (never in a static initializer — TypeManager isn't built at Plugin.Load).
/// Faust is the AUTHORITATIVE global view, so global scans that must see distant/streamed-out
/// world objects pass includeDisabled (DEV_REMINDERS: default queries skip Disabled entities).
/// </summary>
internal static class Query
{
    public static NativeArray<Entity> GetEntitiesByComponentType<T>(
        bool includeDisabled = false, bool includeSpawn = false, bool includePrefab = false)
    {
        EntityQueryOptions options = EntityQueryOptions.Default;
        if (includeDisabled) options |= EntityQueryOptions.IncludeDisabled;
        if (includeSpawn) options |= EntityQueryOptions.IncludeSpawnTag;
        if (includePrefab) options |= EntityQueryOptions.IncludePrefab;

        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<T>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(options);
        var query = Core.EntityManager.CreateEntityQuery(ref builder);
        var entities = query.ToEntityArray(Allocator.Temp);
        builder.Dispose();
        return entities;
    }

    /// <summary>All currently-connected User entities.</summary>
    public static System.Collections.Generic.IEnumerable<Entity> GetUsersOnline()
    {
        var users = GetEntitiesByComponentType<User>(includeDisabled: true);
        try
        {
            for (int i = 0; i < users.Length; i++)
            {
                if (users[i].TryGetComponent<User>(out var u) && u.IsConnected)
                    yield return users[i];
            }
        }
        finally { users.Dispose(); }
    }
}

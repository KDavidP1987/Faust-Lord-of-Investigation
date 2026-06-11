using BepInEx.Logging;
using ProjectM;
using ProjectM.Scripting;
using Unity.Entities;
using Faust.Services;

namespace Faust;

/// <summary>
/// Deferred-initialization hub. V Rising's ECS systems (TypeManager, PrefabCollectionSystem)
/// are NOT ready at Plugin.Load — anything that touches Il2CppType.Of&lt;T&gt; or prefab data
/// must wait until the server world exists and prefabs are populated. Patches call
/// <see cref="TryInitialize"/>; it no-ops until the world is actually ready.
/// (Pattern proven in Uriel / Beelzebub / KindredCommands.)
/// </summary>
internal static class Core
{
    public static World Server { get; private set; }
    public static EntityManager EntityManager { get; private set; }
    public static PrefabCollectionSystem PrefabCollectionSystem { get; private set; }
    public static ServerScriptMapper ServerScriptMapper { get; private set; }
    public static ServerGameSettingsSystem ServerGameSettingsSystem { get; private set; }
    public static ServerGameManager ServerGameManager => ServerScriptMapper.GetServerGameManager();
    public static double ServerTime => ServerGameManager.ServerTime;

    // ---- Feature services (data collection); built in TryInitialize after game data is loaded ----
    public static CastleService Castle { get; private set; }
    public static PlayerInfoService PlayerInfo { get; private set; }
    public static ClanService Clan { get; private set; }
    public static MapMarkerService MapMarkers { get; private set; }

    // ---- Persistence; created at Plugin.Load (no ECS dependency) so it captures connect events
    //      that can land before game-data init completes, and so admin overrides apply immediately. ----
    public static FaustStore Store { get; private set; }
    public static UsageService Usage { get; private set; }
    public static FeatureControlService Control { get; private set; }
    public static UnlockService Unlock { get; private set; }

    public static ManualLogSource Log => Plugin.PluginLog;
    public static bool IsReady { get; private set; }

    /// <summary>Stand up the persistence layers immediately at Plugin.Load (independent of the ECS world).</summary>
    internal static void InitPersistence()
    {
        Store = new FaustStore();
        Store.Load();
        Usage = new UsageService();
        Usage.Load();
        Control = new FeatureControlService();
        Control.Load();
        Unlock = new UnlockService();
        Unlock.Load();
    }

    static bool _initInProgress;
    static int _initAttempts;

    internal static void TryInitialize(string trigger)
    {
        if (IsReady || _initInProgress) return;
        _initInProgress = true;
        _initAttempts++;
        try
        {
            var server = FindServerWorld();
            if (server is null)
            {
                if (_initAttempts == 1)
                    Log.LogInfo($"Faust init ({trigger}): Server world not yet present; will retry.");
                return;
            }

            var prefabSystem = server.GetExistingSystemManaged<PrefabCollectionSystem>();
            if (prefabSystem is null || prefabSystem.SpawnableNameToPrefabGuidDictionary.Count == 0)
            {
                if (_initAttempts == 1)
                    Log.LogInfo($"Faust init ({trigger}): PrefabCollectionSystem not yet populated; will retry.");
                return;
            }

            Server = server;
            EntityManager = server.EntityManager;
            PrefabCollectionSystem = prefabSystem;
            ServerScriptMapper = server.GetExistingSystemManaged<ServerScriptMapper>();
            ServerGameSettingsSystem = server.GetExistingSystemManaged<ServerGameSettingsSystem>();

            // Feature services initialize here (after game data is loaded), in dependency order.
            // These are stateless readers over live ECS state (the global view BCH can't see).
            // FaustStore (session/time-series persistence for #3 frequency/playtime + #8 stats)
            // lands with the persistence subsystem; see docs/FAUST_DESIGN.md §6.
            Castle = new CastleService();
            PlayerInfo = new PlayerInfoService();
            Clan = new ClanService();
            MapMarkers = new MapMarkerService();

            IsReady = true;
            Log.LogInfo($"Faust initialized via {trigger} (attempt #{_initAttempts}). Prefab map has {prefabSystem.SpawnableNameToPrefabGuidDictionary.Count} entries.");
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Faust init ({trigger}) FAILED on attempt #{_initAttempts}: {ex}");
        }
        finally
        {
            _initInProgress = false;
        }
    }

    static World FindServerWorld()
    {
        foreach (var world in World.s_AllWorlds)
        {
            if (world.Name == "Server") return world;
        }
        return null;
    }
}

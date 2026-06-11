using System;
using HarmonyLib;
using ProjectM;
using ProjectM.Network;
using Stunlock.Network;

namespace Faust.Patches;

/// <summary>
/// Records server connect/disconnect into <see cref="Services.FaustStore"/> — the session log Faust
/// derives playtime/frequency/peak-hour from (the game only keeps the LAST connect time). Resolution
/// of the User entity from the NetConnectionId mirrors KindredCommands' connectivity patches.
/// Bodies are fully guarded: a throw across a Harmony boundary on a core server system can corrupt
/// the tick.
/// </summary>
[HarmonyPatch(typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.OnUserConnected))]
internal static class OnUserConnected_Patch
{
    public static void Postfix(ServerBootstrapSystem __instance, NetConnectionId netConnectionId)
    {
        try
        {
            if (Core.Store is null) return;
            var userIndex = __instance._NetEndPointToApprovedUserIndex[netConnectionId];
            var serverClient = __instance._ApprovedUsersLookup[userIndex];
            var user = __instance.EntityManager.GetComponentData<User>(serverClient.UserEntity);
            // Brand-new vampire with no character yet: skip. NOTE this means a player's very FIRST
            // connect (pre-character-creation) isn't logged — their first-seen / first session starts
            // at the next connect that has a name. Marginal effect on new-player/first-seen analytics.
            if (user.CharacterName.IsEmpty) return;
            Core.Store.OnConnect(user.PlatformId, user.CharacterName.ToString());
            Core.MapMarkers?.OnPlayerConnect(user.PlatformId); // experimental; no-op unless active
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[FAUST STORE] OnUserConnected failed: {ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.OnUserDisconnected))]
internal static class OnUserDisconnected_Patch
{
    public static void Prefix(ServerBootstrapSystem __instance, NetConnectionId netConnectionId,
        ConnectionStatusChangeReason connectionStatusReason, string extraData)
    {
        try
        {
            if (Core.Store is null) return;
            var userIndex = __instance._NetEndPointToApprovedUserIndex[netConnectionId];
            var serverClient = __instance._ApprovedUsersLookup[userIndex];
            var user = __instance.EntityManager.GetComponentData<User>(serverClient.UserEntity);
            if (user.CharacterName.IsEmpty) return;
            Core.Store.OnDisconnect(user.PlatformId);
            Core.MapMarkers?.OnPlayerDisconnect(user.PlatformId);
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[FAUST STORE] OnUserDisconnected failed: {ex.Message}");
        }
    }
}

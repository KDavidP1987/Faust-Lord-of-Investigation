using System;
using HarmonyLib;
using ProjectM;
using Unity.Collections;

namespace Faust.Patches;

/// <summary>
/// Unlock-criteria hook (ADMIN_CONTROL §1 axis 6): postfix on the death stream records each V Blood
/// a PLAYER defeats, so features gated on <c>BossKill:&lt;guid&gt;</c> / <c>FinalBoss</c> open for that
/// player. No-op unless some feature actually configures an Unlock criterion (cheap config gate),
/// so it costs nothing on a server that doesn't use unlocks. Fully try/catch-guarded — never throw
/// across a Harmony boundary on a core server system. (Pattern from Uriel's discovery hook.)
/// </summary>
[HarmonyPatch(typeof(DeathEventListenerSystem), nameof(DeathEventListenerSystem.OnUpdate))]
internal static class DeathEventListenerSystemPatch
{
    [HarmonyPostfix]
    public static void OnUpdatePostfix(DeathEventListenerSystem __instance)
    {
        if (!Core.IsReady || Core.Unlock is null || !Core.Unlock.TracksUnlocks) return;

        NativeArray<DeathEvent> deaths = default;
        try
        {
            deaths = __instance._DeathEventQuery.ToComponentDataArray<DeathEvent>(Allocator.Temp);
            for (int i = 0; i < deaths.Length; i++)
            {
                var death = deaths[i];
                if (!death.Killer.Has<PlayerCharacter>()) continue; // player-caused only
                if (!death.Died.Has<VBloodUnit>()) continue;        // V Bloods only
                ulong steam = death.Killer.GetSteamId();
                int guid = death.Died.GetPrefabGuid()._Value;
                if (steam != 0 && guid != 0) Core.Unlock.RecordBossDefeat(steam, guid);
            }
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[FAUST UNLOCK] death-hook failed: {ex.Message}");
        }
        finally
        {
            if (deaths.IsCreated) deaths.Dispose();
        }
    }
}

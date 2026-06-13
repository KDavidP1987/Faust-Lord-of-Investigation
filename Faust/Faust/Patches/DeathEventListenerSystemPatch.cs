using System;
using HarmonyLib;
using ProjectM;
using Unity.Collections;

namespace Faust.Patches;

/// <summary>
/// Death-stream hook feeding TWO collectors off the same pass (so kills add no extra system):
///   • Unlock criteria (ADMIN_CONTROL §1 axis 6): record each V Blood a PLAYER defeats, so features
///     gated on <c>BossKill:&lt;guid&gt;</c> / <c>FinalBoss</c> open for that player.
///   • Kill tally (kills leaderboards): per-player kill counts (+PvP) and per-boss defeat counts.
/// Both are config-gated — the hook no-ops unless some feature has an Unlock criterion OR kill tracking
/// is enabled — so it costs nothing when neither is in use. Fully try/catch-guarded — never throw across
/// a Harmony boundary on a core server system. (Pattern from Uriel's discovery hook.)
/// </summary>
[HarmonyPatch(typeof(DeathEventListenerSystem), nameof(DeathEventListenerSystem.OnUpdate))]
internal static class DeathEventListenerSystemPatch
{
    [HarmonyPostfix]
    public static void OnUpdatePostfix(DeathEventListenerSystem __instance)
    {
        if (!Core.IsReady) return;
        bool tracksUnlocks = Core.Unlock is not null && Core.Unlock.TracksUnlocks;
        bool tracksKills = Faust.Services.KillTrackingService.Enabled && Core.Kills is not null;
        if (!tracksUnlocks && !tracksKills) return;

        NativeArray<DeathEvent> deaths = default;
        try
        {
            deaths = __instance._DeathEventQuery.ToComponentDataArray<DeathEvent>(Allocator.Temp);
            for (int i = 0; i < deaths.Length; i++)
            {
                var death = deaths[i];
                if (!death.Killer.Has<PlayerCharacter>()) continue; // player-caused only
                ulong steam = death.Killer.GetSteamId();
                bool isVBlood = death.Died.Has<VBloodUnit>();
                int guid = isVBlood ? death.Died.GetPrefabGuid()._Value : 0;

                if (tracksKills)
                    Core.Kills.RecordKill(steam, death.Died.Has<PlayerCharacter>(), guid);

                if (tracksUnlocks && isVBlood && steam != 0 && guid != 0)
                    Core.Unlock.RecordBossDefeat(steam, guid);
            }
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[FAUST DEATH] death-hook failed: {ex.Message}");
        }
        finally
        {
            if (deaths.IsCreated) deaths.Dispose();
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using Faust.Config;
using UnityEngine;

namespace Faust.Services;

/// <summary>
/// The one truly PERIODIC collector in Faust: every <c>[Faust.Heatmap] SampleSeconds</c> (30–300s) it
/// snapshots every online player's (x,z) and feeds it into <see cref="HeatmapStore"/>. Runs as a Unity
/// coroutine on the server's main thread (started once from <see cref="Core.TryInitialize"/>) — V Rising's
/// dedicated server ticks coroutines every frame, so a <c>WaitForSeconds</c> loop is a reliable timer.
/// (Faust has no per-frame ECS system safe to borrow: the only one it patches,
/// <c>SpawnTeamSystem_OnPersistenceLoad.OnUpdate</c>, is a load-time one-shot, not a gameplay tick.)
///
/// Collection is opt-in via <c>[Faust.Heatmap] Enabled</c> — the loop always runs once Faust is ready, but
/// only records while collection is enabled (and at least one player is online), so it costs nothing idle.
/// </summary>
internal static class HeatmapSampler
{
    static bool _started;

    /// <summary>Start the sampler loop once (idempotent). Called when Faust becomes ready.</summary>
    public static void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            Core.StartCoroutine(SampleLoop());
            Core.Log.LogInfo("[FAUST HEATMAP] position sampler running (coroutine).");
        }
        catch (Exception ex)
        {
            _started = false;
            Core.Log.LogError($"[FAUST HEATMAP] failed to start sampler: {ex}");
        }
    }

    static IEnumerator SampleLoop()
    {
        yield return new WaitForSeconds(5f); // let the world settle after init
        while (true)
        {
            // Re-read the interval each loop so a config change takes effect on the next restart's first
            // pass without special handling; clamped to the documented 30–300s range.
            int interval = Math.Clamp(Settings.HeatmapSampleSeconds.Value, 30, 300);
            SampleOnce();
            yield return new WaitForSeconds(interval);
        }
    }

    static void SampleOnce()
    {
        try
        {
            if (!Core.IsReady || Core.Heatmap is null || !Settings.HeatmapEnabled.Value) return;

            var positions = Core.PlayerInfo.GetOnlinePositions();
            if (positions.Count == 0) return; // nobody online → nothing to sample

            var samples = new List<(ulong, float, float)>(positions.Count);
            foreach (var p in positions)
                if (p.SteamId != 0) samples.Add((p.SteamId, p.X, p.Z));

            Core.Heatmap.RecordSamples(samples, Settings.HeatmapCellSize.Value, Settings.HeatmapMaxCells.Value, Settings.HeatmapRetentionDays.Value);
            if (Settings.VerboseLogging.Value)
                Core.Log.LogInfo($"[FAUST HEATMAP] sampled {samples.Count} position(s).");
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[FAUST HEATMAP] sample failed: {ex.Message}");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Faust.Config;

namespace Faust.Services;

/// <summary>
/// Per-(player, feature) usage state for the rate/time-lock axis (ADMIN_CONTROL §1 axis 5):
/// a flat cooldown, and/or a window/period policy ("a 10-minute window, once per day"). State
/// persists to BepInEx/config/Faust/feature_usage.json so a daily window doesn't reset on restart.
///
/// Reserve/confirm: <see cref="Check"/> only tests (denies with a code + seconds remaining);
/// <see cref="Record"/> mutates + persists, and is called from the gate's Commit AFTER a real
/// result — so a denied or empty query never burns a use/window.
/// </summary>
internal sealed class UsageService
{
    sealed class Rec
    {
        public long LastUse { get; set; }       // unix; flat-cooldown anchor
        public long PeriodStart { get; set; }    // unix; when the current period began (0 = none)
        public int UsesThisPeriod { get; set; }
        public long WindowStart { get; set; }    // unix; when the current window opened (0 = none)
    }

    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, Rec> Usage { get; set; } = new(); // "steam|feature" -> Rec
    }

    readonly Dictionary<string, Rec> _usage = new();

    static string SaveDir => Path.Combine(BepInEx.Paths.ConfigPath, "Faust");
    static string SavePath => Path.Combine(SaveDir, "feature_usage.json");
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static string KeyOf(ulong steam, string feature) => $"{steam}|{feature}";

    public void Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            var file = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(SavePath));
            if (file?.Usage is null) return;
            _usage.Clear();
            foreach (var kvp in file.Usage) _usage[kvp.Key] = kvp.Value;
            Core.Log.LogInfo($"[FAUST USAGE] loaded usage state for {_usage.Count} (player,feature) pair(s).");
        }
        catch (Exception ex) { Core.Log.LogError($"[FAUST USAGE] failed loading {SavePath}: {ex}"); }
    }

    void SaveSync()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            var file = new SaveFile();
            foreach (var kvp in _usage) file.Usage[kvp.Key] = kvp.Value;
            File.WriteAllText(SavePath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex) { Core.Log.LogError($"[FAUST USAGE] failed saving {SavePath}: {ex}"); }
    }

    static bool WindowOpen(Rec r, FeatureConfig f, long now) =>
        f.WindowSeconds.Value > 0 && r.WindowStart > 0 && (now - r.WindowStart) < f.WindowSeconds.Value;

    /// <summary>
    /// Test cooldown + window/period for (player, feature). Returns false with a wire-ready deny
    /// code ("cooldown" or "window") and the seconds until it reopens.
    /// </summary>
    public bool Check(ulong steam, FeatureConfig f, out string denyCode, out int secsLeft)
    {
        denyCode = null; secsLeft = 0;
        if (!_usage.TryGetValue(KeyOf(steam, f.Key), out var r)) return true; // never used -> ok
        long now = Now;
        bool windowOpen = WindowOpen(r, f, now);

        // Flat cooldown (skipped while a window is open — the window IS the grace period).
        if (f.CooldownSeconds.Value > 0 && r.LastUse > 0 && !windowOpen)
        {
            int left = (int)(f.CooldownSeconds.Value - (now - r.LastUse));
            if (left > 0) { denyCode = "cooldown"; secsLeft = left; return false; }
        }

        // Window / period.
        if (f.HasWindowPolicy)
        {
            bool periodActive = r.PeriodStart > 0 && (now - r.PeriodStart) < f.PeriodSeconds.Value;
            if (!periodActive) return true;            // period rolled over -> fresh allowance
            if (windowOpen) return true;               // inside an open window -> ok
            int max = f.MaxUsesPerPeriod.Value;
            if (max > 0 && r.UsesThisPeriod >= max)
            {
                secsLeft = (int)(f.PeriodSeconds.Value - (now - r.PeriodStart));
                denyCode = "window"; if (secsLeft < 0) secsLeft = 0; return false;
            }
        }
        return true;
    }

    /// <summary>Record a successful use: stamp cooldown, roll the period if needed, open/keep the window.</summary>
    public void Record(ulong steam, FeatureConfig f)
    {
        if (f.CooldownSeconds.Value <= 0 && !f.HasWindowPolicy) return; // nothing to track
        long now = Now;
        var key = KeyOf(steam, f.Key);
        if (!_usage.TryGetValue(key, out var r)) _usage[key] = r = new Rec();
        r.LastUse = now;

        if (f.HasWindowPolicy)
        {
            bool periodActive = r.PeriodStart > 0 && (now - r.PeriodStart) < f.PeriodSeconds.Value;
            if (!periodActive) { r.PeriodStart = now; r.UsesThisPeriod = 0; r.WindowStart = 0; }

            if (f.WindowSeconds.Value > 0)
            {
                bool windowActive = r.WindowStart > 0 && (now - r.WindowStart) < f.WindowSeconds.Value;
                if (!windowActive) { r.WindowStart = now; r.UsesThisPeriod++; } // a new window counts as one use
            }
            else r.UsesThisPeriod++;
        }
        SaveSync();
    }
}

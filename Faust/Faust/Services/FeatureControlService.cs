using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Faust.Services;

/// <summary>
/// Runtime operational control over features (ADMIN_CONTROL §3) — the layer admins drive live with
/// '.faust admin …', on TOP of the static .cfg. Two override kinds per feature (plus the "all"
/// pseudo-feature that gates everything): a temporary/indefinite BLOCK (with optional countdown),
/// and a time-of-day SCHEDULE window. Persists to BepInEx/config/Faust/feature_control.json so
/// overrides survive a restart. Times of day are SERVER LOCAL time.
/// </summary>
internal sealed class FeatureControlService
{
    public const string All = "all";

    sealed class Rec
    {
        public long BlockedUntil { get; set; }  // 0 = not blocked, -1 = indefinite, >0 = unix expiry
        public int SchedStart { get; set; } = -1; // minutes-of-day open, -1 = no schedule
        public int SchedEnd { get; set; } = -1;
    }

    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, Rec> Control { get; set; } = new();
    }

    readonly Dictionary<string, Rec> _control = new();

    static string SaveDir => Path.Combine(BepInEx.Paths.ConfigPath, "Faust");
    static string SavePath => Path.Combine(SaveDir, "feature_control.json");
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static int NowMinuteOfDay => DateTime.Now.Hour * 60 + DateTime.Now.Minute;

    public void Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            var file = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(SavePath));
            if (file?.Control is null) return;
            _control.Clear();
            foreach (var kvp in file.Control) _control[kvp.Key] = kvp.Value;
            Core.Log.LogInfo($"[FAUST CONTROL] loaded runtime overrides for {_control.Count} feature(s).");
        }
        catch (Exception ex) { Core.Log.LogError($"[FAUST CONTROL] failed loading {SavePath}: {ex}"); }
    }

    void SaveSync()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            var file = new SaveFile();
            foreach (var kvp in _control) file.Control[kvp.Key] = kvp.Value;
            File.WriteAllText(SavePath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex) { Core.Log.LogError($"[FAUST CONTROL] failed saving {SavePath}: {ex}"); }
    }

    Rec Get(string key) => _control.TryGetValue(key, out var r) ? r : null;
    Rec GetOrAdd(string key) { if (!_control.TryGetValue(key, out var r)) _control[key] = r = new Rec(); return r; }

    // ---- gate evaluation ----

    /// <summary>
    /// True if the feature is operationally available right now (not blocked, inside any schedule).
    /// On deny, <paramref name="code"/> is "blocked" or "schedule" and <paramref name="secsLeft"/>
    /// is seconds until it reopens (-1 = indefinite / unknown).
    /// </summary>
    public bool IsAvailable(string feature, out string code, out int secsLeft)
    {
        code = null; secsLeft = 0;
        long now = Now;
        foreach (var key in new[] { All, feature })
        {
            var r = Get(key);
            if (r is null) continue;

            // Block / countdown
            if (r.BlockedUntil == -1) { code = "blocked"; secsLeft = -1; return false; }
            if (r.BlockedUntil > now) { code = "blocked"; secsLeft = (int)(r.BlockedUntil - now); return false; }

            // Schedule window (server-local time-of-day)
            if (r.SchedStart >= 0 && r.SchedEnd >= 0)
            {
                int cur = NowMinuteOfDay;
                bool open = r.SchedStart <= r.SchedEnd
                    ? cur >= r.SchedStart && cur < r.SchedEnd          // same-day window
                    : cur >= r.SchedStart || cur < r.SchedEnd;          // wraps midnight
                if (!open)
                {
                    int until = r.SchedStart - cur; if (until <= 0) until += 1440; // minutes to next open
                    code = "schedule"; secsLeft = until * 60; return false;
                }
            }
        }
        return true;
    }

    // ---- admin operations ----

    /// <summary>Block now. minutes &lt;= 0 = indefinite (until unblock); otherwise a countdown.</summary>
    public void Block(string feature, int minutes)
    {
        GetOrAdd(feature).BlockedUntil = minutes <= 0 ? -1 : Now + minutes * 60L;
        SaveSync();
    }

    public void Unblock(string feature)
    {
        if (_control.TryGetValue(feature, out var r)) { r.BlockedUntil = 0; SaveSync(); }
    }

    public void SetSchedule(string feature, int startMin, int endMin)
    {
        var r = GetOrAdd(feature); r.SchedStart = startMin; r.SchedEnd = endMin; SaveSync();
    }

    public void ClearSchedule(string feature)
    {
        if (_control.TryGetValue(feature, out var r)) { r.SchedStart = -1; r.SchedEnd = -1; SaveSync(); }
    }

    /// <summary>Human-readable effective control state for '.faust admin status'.</summary>
    public string Describe(string feature)
    {
        var r = Get(feature);
        if (r is null) return "open";
        long now = Now;
        var parts = new List<string>();
        if (r.BlockedUntil == -1) parts.Add("BLOCKED (indefinite)");
        else if (r.BlockedUntil > now) parts.Add($"BLOCKED ({(r.BlockedUntil - now) / 60}m left)");
        if (r.SchedStart >= 0 && r.SchedEnd >= 0)
            parts.Add($"schedule {Hhmm(r.SchedStart)}-{Hhmm(r.SchedEnd)}");
        return parts.Count == 0 ? "open" : string.Join(", ", parts);
    }

    static string Hhmm(int min) => $"{min / 60:00}:{min % 60:00}";
}

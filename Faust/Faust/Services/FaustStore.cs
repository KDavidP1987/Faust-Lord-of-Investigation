using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Faust.Config;

namespace Faust.Services;

/// <summary>
/// Server-authoritative time-series persistence (design §6) — the data the game itself does NOT
/// keep. V Rising stores only the LAST connect time (User.TimeLastConnected); Faust logs every
/// connect/disconnect so it can derive first-seen, session count, total playtime, login frequency,
/// and peak hour (feature #3), plus a concurrency series for server stats / graphs (#8).
///
/// Persisted to BepInEx/config/Faust/sessions.json (System.Text.Json), mirroring the Uriel
/// PlayerUnlockService idiom: load on boot, save synchronously on each event (connect/disconnect
/// frequency is low). The persistence layer is independent of the ECS world, so it is created at
/// Plugin.Load — connect events can land before game-data init completes.
/// </summary>
internal sealed class FaustStore
{
    sealed class Session
    {
        public ulong Steam { get; set; }
        public long Connect { get; set; }      // Unix UTC seconds
        public long Disconnect { get; set; }    // Unix UTC seconds; 0 = still open
    }

    sealed class ConcPoint
    {
        public long T { get; set; }             // Unix UTC seconds
        public int Count { get; set; }          // online player count at T
    }

    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<Session> Sessions { get; set; } = new();
        public List<ConcPoint> Concurrency { get; set; } = new();
        public Dictionary<string, string> Names { get; set; } = new(); // steamId -> last-known name
    }

    public readonly struct PlayerMetrics
    {
        public long FirstSeenUnix { get; init; } // -1 if no sessions recorded
        public int SessionCount { get; init; }
        public long PlayMinutes { get; init; }
        public int PeakHour { get; init; }       // 0-23 (UTC); -1 if none
        public double FreqPerWeek { get; init; } // -1 if unknown
    }

    readonly List<Session> _sessions = new();
    readonly List<ConcPoint> _concurrency = new();
    readonly Dictionary<ulong, string> _names = new();
    readonly HashSet<ulong> _online = new();

    static string SaveDir => Path.Combine(BepInEx.Paths.ConfigPath, "Faust");
    static string SavePath => Path.Combine(SaveDir, "sessions.json");

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public void Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            var file = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(SavePath));
            if (file is null) return;

            _sessions.Clear();
            foreach (var s in file.Sessions ?? new())
            {
                // A session left open by a hard crash is closed at its connect time (negligible
                // playtime) so it never counts as an infinite/online session after a restart.
                if (s.Disconnect == 0) s.Disconnect = s.Connect;
                _sessions.Add(s);
            }
            _concurrency.Clear();
            if (file.Concurrency is not null) _concurrency.AddRange(file.Concurrency);
            _names.Clear();
            if (file.Names is not null)
                foreach (var kvp in file.Names)
                    if (ulong.TryParse(kvp.Key, out var id)) _names[id] = kvp.Value;

            PruneOldSessions(); // honour SessionRetentionDays on every boot

            Core.Log.LogInfo($"[FAUST STORE] loaded {_sessions.Count} session(s), {_concurrency.Count} concurrency point(s), {_names.Count} name(s).");
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[FAUST STORE] failed loading {SavePath}: {ex}");
        }
    }

    void SaveSync()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            var file = new SaveFile { Sessions = _sessions, Concurrency = _concurrency };
            foreach (var kvp in _names) file.Names[kvp.Key.ToString()] = kvp.Value;
            File.WriteAllText(SavePath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[FAUST STORE] failed saving {SavePath}: {ex}");
        }
    }

    // ---- event recording (called from the connectivity patches) ----

    public void OnConnect(ulong steam, string name)
    {
        if (steam == 0) return;
        // The live online set is always maintained (trivial in-memory cost, drives OnlineCount and
        // the concurrency sample). Whether anything is PERSISTED is governed by the collection knobs.
        _online.Add(steam);

        bool changed = false;
        if (Settings.SessionTracking.Value)
        {
            if (!string.IsNullOrEmpty(name)) _names[steam] = name;
            // Close any stale open session for this steam, then open a fresh one.
            foreach (var s in _sessions) if (s.Steam == steam && s.Disconnect == 0) s.Disconnect = Now;
            _sessions.Add(new Session { Steam = steam, Connect = Now, Disconnect = 0 });
            PruneOldSessions();
            changed = true;
        }
        if (RecordConcurrency()) changed = true;
        if (changed) SaveSync();
    }

    public void OnDisconnect(ulong steam)
    {
        if (steam == 0) return;
        _online.Remove(steam);

        bool changed = false;
        if (Settings.SessionTracking.Value)
        {
            long now = Now;
            foreach (var s in _sessions) if (s.Steam == steam && s.Disconnect == 0) s.Disconnect = now;
            changed = true;
        }
        if (RecordConcurrency()) changed = true;
        if (changed) SaveSync();
    }

    /// <summary>Close every open session (clean shutdown) so the last session's playtime is kept.</summary>
    public void CloseAllOpen()
    {
        long now = Now;
        bool changed = false;
        foreach (var s in _sessions) if (s.Disconnect == 0) { s.Disconnect = now; changed = true; }
        _online.Clear();
        if (changed) SaveSync();
    }

    /// <summary>Sample the online count, honouring the ConcurrencySampling + MaxConcurrencyPoints
    /// collection knobs. Returns true if a point was recorded (so the caller knows to persist).</summary>
    bool RecordConcurrency()
    {
        int cap = Settings.MaxConcurrencyPoints.Value;
        if (!Settings.ConcurrencySampling.Value || cap <= 0) return false; // sampling disabled
        _concurrency.Add(new ConcPoint { T = Now, Count = _online.Count });
        if (_concurrency.Count > cap)
            _concurrency.RemoveRange(0, _concurrency.Count - cap);
        return true;
    }

    /// <summary>Drop sessions older than SessionRetentionDays (0 = keep forever). Bounds long-term
    /// growth of sessions.json and keeps the derived playtime/frequency windows recent.</summary>
    void PruneOldSessions()
    {
        int days = Settings.SessionRetentionDays.Value;
        if (days <= 0) return;
        long cutoff = Now - (long)days * 86400L;
        // Prune by session END (a still-open session, Disconnect==0, is never pruned).
        _sessions.RemoveAll(s => s.Disconnect != 0 && s.Disconnect < cutoff);
    }

    // ---- derived metrics (feature #3 playerinfo) ----

    public PlayerMetrics GetMetrics(ulong steam)
    {
        long now = Now;
        long first = long.MaxValue, totalSecs = 0;
        int count = 0;
        var hourHist = new int[24];

        foreach (var s in _sessions)
        {
            if (s.Steam != steam) continue;
            count++;
            if (s.Connect < first) first = s.Connect;
            long end = s.Disconnect == 0 ? now : s.Disconnect;
            if (end > s.Connect) totalSecs += end - s.Connect;
            int hour = DateTimeOffset.FromUnixTimeSeconds(s.Connect).UtcDateTime.Hour;
            if (hour >= 0 && hour < 24) hourHist[hour]++;
        }

        if (count == 0)
            return new PlayerMetrics { FirstSeenUnix = -1, SessionCount = 0, PlayMinutes = -1, PeakHour = -1, FreqPerWeek = -1 };

        int peakHour = 0;
        for (int h = 1; h < 24; h++) if (hourHist[h] > hourHist[peakHour]) peakHour = h;

        double weeks = Math.Max(1.0, (now - first) / 604800.0);
        return new PlayerMetrics
        {
            FirstSeenUnix = first,
            SessionCount = count,
            PlayMinutes = totalSecs / 60,
            PeakHour = peakHour,
            FreqPerWeek = Math.Round(count / weeks, 1),
        };
    }

    // ---- stats (feature #8) ----

    /// <summary>Top players by total playtime (minutes), descending.</summary>
    public List<(ulong steam, string name, long minutes)> GetPlaytimeLeaderboard()
    {
        long now = Now;
        var totals = new Dictionary<ulong, long>();
        foreach (var s in _sessions)
        {
            long end = s.Disconnect == 0 ? now : s.Disconnect;
            if (end <= s.Connect) continue;
            totals.TryGetValue(s.Steam, out var cur);
            totals[s.Steam] = cur + (end - s.Connect);
        }
        return totals
            .Select(kvp => (kvp.Key, _names.TryGetValue(kvp.Key, out var n) ? n : kvp.Key.ToString(), kvp.Value / 60))
            .OrderByDescending(t => t.Item3)
            .ToList();
    }

    /// <summary>Recent concurrency points (oldest→newest), capped to <paramref name="max"/>.</summary>
    public List<(long t, int count)> GetConcurrency(int max = 200)
    {
        int start = Math.Max(0, _concurrency.Count - max);
        var result = new List<(long, int)>(_concurrency.Count - start);
        for (int i = start; i < _concurrency.Count; i++) result.Add((_concurrency[i].T, _concurrency[i].Count));
        return result;
    }

    public int OnlineCount => _online.Count;
}

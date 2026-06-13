using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

    // §10c: one per-region daily snapshot (UTC). Faust has no historical castle data (the map is read
    // live), so this is a forward-accumulating series — one sample per region per UTC day, taken on the
    // day's first connect/disconnect, persisted like the concurrency series.
    sealed class RegionSample
    {
        public long Day { get; set; }           // Unix UTC midnight
        public string Region { get; set; }       // "-" = open world / no region
        public int Castles { get; set; }         // claimed hearts in the region that day
        public int Plots { get; set; }           // total buildable territories in the region
        public int Players { get; set; }         // online players in the region at sample time
    }

    sealed class SaveFile
    {
        // Reserved for a future on-load migration; currently written but not yet read/branched on.
        public int SchemaVersion { get; set; } = 1;
        public List<Session> Sessions { get; set; } = new();
        public List<ConcPoint> Concurrency { get; set; } = new();
        public List<RegionSample> RegionDaily { get; set; } = new(); // §10c (absent in pre-0.15 files → empty)
        public Dictionary<string, string> Names { get; set; } = new(); // steamId -> last-known name
    }

    public readonly struct PlayerMetrics
    {
        public long FirstSeenUnix { get; init; } // -1 if no sessions recorded
        public int SessionCount { get; init; }
        public long PlayMinutes { get; init; }
        public int PeakHour { get; init; }       // 0-23 (UTC); -1 if none
        public double FreqPerWeek { get; init; } // -1 if unknown
        public int DaysIdle { get; init; }       // whole days since last seen; 0 if online; -1 if untracked
    }

    readonly List<Session> _sessions = new();
    readonly List<ConcPoint> _concurrency = new();
    readonly List<RegionSample> _regionDaily = new();
    readonly Dictionary<ulong, string> _names = new();
    readonly HashSet<ulong> _online = new();
    long _lastRegionSampleDay; // UTC midnight of the most recent region snapshot (0 = none yet)

    static string SaveDir => FaustPaths.DataDir;
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
            _regionDaily.Clear();
            if (file.RegionDaily is not null) _regionDaily.AddRange(file.RegionDaily);
            _names.Clear();
            if (file.Names is not null)
                foreach (var kvp in file.Names)
                    if (ulong.TryParse(kvp.Key, out var id)) _names[id] = kvp.Value;

            PruneOldSessions();      // honour SessionRetentionDays on every boot
            PruneOldRegionDaily();
            // Resume the once-per-day region cadence where we left off (don't re-sample today after a restart).
            _lastRegionSampleDay = 0;
            foreach (var r in _regionDaily) if (r.Day > _lastRegionSampleDay) _lastRegionSampleDay = r.Day;

            Core.Log.LogInfo($"[FAUST STORE] loaded {_sessions.Count} session(s), {_concurrency.Count} concurrency point(s), {_regionDaily.Count} region sample(s), {_names.Count} name(s).");
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
            var file = new SaveFile { Sessions = _sessions, Concurrency = _concurrency, RegionDaily = _regionDaily };
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
        long first = long.MaxValue, lastEnd = 0, totalSecs = 0;
        int count = 0;
        bool online = false;
        var hourHist = new int[24];

        foreach (var s in _sessions)
        {
            if (s.Steam != steam) continue;
            count++;
            if (s.Connect < first) first = s.Connect;
            long end = s.Disconnect == 0 ? now : s.Disconnect;
            if (s.Disconnect == 0) online = true;
            if (end > lastEnd) lastEnd = end;
            if (end > s.Connect) totalSecs += end - s.Connect;
            int hour = DateTimeOffset.FromUnixTimeSeconds(s.Connect).UtcDateTime.Hour;
            if (hour >= 0 && hour < 24) hourHist[hour]++;
        }

        if (count == 0)
            return new PlayerMetrics { FirstSeenUnix = -1, SessionCount = 0, PlayMinutes = -1, PeakHour = -1, FreqPerWeek = -1, DaysIdle = -1 };

        int peakHour = 0;
        for (int h = 1; h < 24; h++) if (hourHist[h] > hourHist[peakHour]) peakHour = h;

        double weeks = Math.Max(1.0, (now - first) / 604800.0);
        int daysIdle = online ? 0 : (int)((now - lastEnd) / Day);
        return new PlayerMetrics
        {
            FirstSeenUnix = first,
            SessionCount = count,
            PlayMinutes = totalSecs / 60,
            PeakHour = peakHour,
            FreqPerWeek = Math.Round(count / weeks, 1),
            DaysIdle = daysIdle,
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

    // ---- activity analytics (feature #8: time-resolved charts the client can't derive) ----
    //
    // All four are aggregations over the same session log. Playtime is attributed by SLICING each
    // session at the relevant boundary (UTC hour / UTC midnight) so a session that straddles a
    // boundary contributes to both buckets — a true activity profile, not just a connect-time tally.

    const long Hour = 3600L;
    const long Day = 86400L;
    // Safety bound on the per-session slice walk (one open session left running for years would
    // otherwise spin): cap any single session's contribution to the most recent ~2 years.
    const int MaxSliceIterations = 24 * 731;

    /// <summary>UTC midnight (Unix seconds) at or before <paramref name="t"/>.</summary>
    static long DayFloor(long t) => t - (t % Day);

    /// <summary>
    /// Accumulated playtime MINUTES per UTC hour-of-day (24 buckets, h[0]=00:00–01:00 … h[23]).
    /// Server-wide when <paramref name="steam"/> is null, else just that player's sessions.
    /// Sessions are sliced at hour boundaries so a session spanning 22:30→01:15 feeds hours 22/23/0/1.
    /// </summary>
    public long[] GetHourHistogram(ulong? steam)
    {
        long now = Now;
        var secs = new long[24];
        foreach (var s in _sessions)
        {
            if (steam.HasValue && s.Steam != steam.Value) continue;
            long t = s.Connect, end = s.Disconnect == 0 ? now : s.Disconnect;
            int guard = 0;
            while (t < end && guard++ < MaxSliceIterations)
            {
                int hour = DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime.Hour;
                long nextBoundary = (t - (t % Hour)) + Hour; // next exact UTC hour (epoch aligns to hours)
                long sliceEnd = Math.Min(end, nextBoundary);
                secs[hour] += sliceEnd - t;
                t = sliceEnd;
            }
        }
        var mins = new long[24];
        for (int h = 0; h < 24; h++) mins[h] = secs[h] / 60;
        return mins;
    }

    /// <summary>
    /// Per-day distinct online players (DAU), total play-minutes, and a new-vs-returning split for the
    /// last <paramref name="days"/> days (oldest→newest, today last). Playtime is sliced at UTC midnight;
    /// a player counts toward a day's DAU if any session overlapped that day. <c>newCount</c> = of that
    /// day's DAU, those whose FIRST recorded session is that same day (§8d); returning = <c>dau - new</c>.
    /// Like <see cref="GetNewPlayersSeries"/>, "new" means first-seen-by-Faust (bounded by retention).
    /// </summary>
    public List<(long dayMidnightUtc, int dau, long minutes, int newCount)> GetDailySeries(int days)
    {
        long now = Now;
        long today = DayFloor(now);
        long start = today - (long)(days - 1) * Day;

        // First-seen connect time per player across ALL retained sessions (so "new" is the player's
        // first-ever day, not merely their first day inside the window).
        var firstSeen = new Dictionary<ulong, long>();
        foreach (var s in _sessions)
            if (!firstSeen.TryGetValue(s.Steam, out var cur) || s.Connect < cur)
                firstSeen[s.Steam] = s.Connect;

        var secs = new long[days];
        var dau = new HashSet<ulong>[days];
        var newOnDay = new int[days];
        for (int i = 0; i < days; i++) dau[i] = new HashSet<ulong>();

        foreach (var s in _sessions)
        {
            long end = s.Disconnect == 0 ? now : s.Disconnect;
            if (end <= start) continue;                  // entirely before the window
            long t = Math.Max(s.Connect, start);
            int guard = 0;
            while (t < end && guard++ < days + 1)
            {
                long dayMid = DayFloor(t);
                int idx = (int)((dayMid - start) / Day);
                long sliceEnd = Math.Min(end, dayMid + Day);
                if (idx >= 0 && idx < days)
                {
                    secs[idx] += sliceEnd - t;
                    // First time we attribute this player to this day: tally DAU, and tally "new" if
                    // the player's first-ever session day equals this day.
                    if (dau[idx].Add(s.Steam)
                        && firstSeen.TryGetValue(s.Steam, out var first) && DayFloor(first) == dayMid)
                        newOnDay[idx]++;
                }
                t = sliceEnd;
            }
        }

        var result = new List<(long, int, long, int)>(days);
        for (int i = 0; i < days; i++)
            result.Add((start + (long)i * Day, dau[i].Count, secs[i] / 60, newOnDay[i]));
        return result;
    }

    /// <summary>
    /// Per-day count of players whose FIRST recorded session falls on that day, for the last
    /// <paramref name="days"/> days (oldest→newest). First-seen is bounded by retained data
    /// (SessionRetentionDays), so a pruned server can under-count growth before the retention window.
    /// </summary>
    public List<(long dayMidnightUtc, int newPlayers)> GetNewPlayersSeries(int days)
    {
        long today = DayFloor(Now);
        long start = today - (long)(days - 1) * Day;

        // First-seen connect time per player across ALL retained sessions.
        var firstSeen = new Dictionary<ulong, long>();
        foreach (var s in _sessions)
        {
            if (!firstSeen.TryGetValue(s.Steam, out var cur) || s.Connect < cur)
                firstSeen[s.Steam] = s.Connect;
        }

        var counts = new int[days];
        foreach (var first in firstSeen.Values)
        {
            long dayMid = DayFloor(first);
            int idx = (int)((dayMid - start) / Day);
            if (idx >= 0 && idx < days) counts[idx]++;
        }

        var result = new List<(long, int)>(days);
        for (int i = 0; i < days; i++) result.Add((start + (long)i * Day, counts[i]));
        return result;
    }

    /// <summary>
    /// Session-length distribution as four bucket counts: &lt;15m, 15–60m, 1–3h, 3h+.
    /// Server-wide when <paramref name="steam"/> is null, else that player's sessions.
    /// </summary>
    public (int lt15, int m15to60, int h1to3, int gt3h) GetSessionLengthBuckets(ulong? steam)
    {
        long now = Now;
        int b0 = 0, b1 = 0, b2 = 0, b3 = 0;
        foreach (var s in _sessions)
        {
            if (steam.HasValue && s.Steam != steam.Value) continue;
            long end = s.Disconnect == 0 ? now : s.Disconnect;
            long dur = end - s.Connect;
            if (dur <= 0) continue;
            if (dur < 900) b0++;            // < 15 min
            else if (dur < 3600) b1++;      // 15–60 min
            else if (dur < 10800) b2++;     // 1–3 h
            else b3++;                       // 3 h+
        }
        return (b0, b1, b2, b3);
    }

    /// <summary>
    /// Accumulated playtime MINUTES per UTC weekday (Monday=d[0] … Sunday=d[6]), server-wide or for
    /// one player. Sessions are sliced at UTC midnight (like the daily series) so a session that
    /// straddles midnight feeds both weekdays — the authoritative "by day of week" profile.
    /// </summary>
    public long[] GetWeekdayHistogram(ulong? steam)
    {
        long now = Now;
        var secs = new long[7];
        foreach (var s in _sessions)
        {
            if (steam.HasValue && s.Steam != steam.Value) continue;
            long t = s.Connect, end = s.Disconnect == 0 ? now : s.Disconnect;
            int guard = 0;
            while (t < end && guard++ < MaxSliceIterations)
            {
                int wd = WeekdayMon0(t);
                long sliceEnd = Math.Min(end, DayFloor(t) + Day);
                secs[wd] += sliceEnd - t;
                t = sliceEnd;
            }
        }
        var mins = new long[7];
        for (int i = 0; i < 7; i++) mins[i] = secs[i] / 60;
        return mins;
    }

    /// <summary>.NET DayOfWeek (Sunday=0…Saturday=6) → Monday=0…Sunday=6 (UTC), matching the wire's d0=Mon.</summary>
    static int WeekdayMon0(long unix)
    {
        int dow = (int)DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.DayOfWeek;
        return (dow + 6) % 7;
    }

    /// <summary>
    /// One player's UTC-day playtime MINUTES for the last <paramref name="days"/> days — one entry per
    /// day the player was actually online (zero-days omitted), oldest→newest. Playtime is sliced at
    /// UTC midnight (same convention as the server daily series). Powers a per-player daily/weekly trend.
    /// </summary>
    public List<(long dayMidnightUtc, long minutes)> GetPlayerDailySeries(ulong steam, int days)
    {
        long now = Now;
        long start = DayFloor(now) - (long)(days - 1) * Day;
        var secs = new long[days];
        foreach (var s in _sessions)
        {
            if (s.Steam != steam) continue;
            long end = s.Disconnect == 0 ? now : s.Disconnect;
            if (end <= start) continue;
            long t = Math.Max(s.Connect, start);
            int guard = 0;
            while (t < end && guard++ < days + 1)
            {
                long dayMid = DayFloor(t);
                int idx = (int)((dayMid - start) / Day);
                long sliceEnd = Math.Min(end, dayMid + Day);
                if (idx >= 0 && idx < days) secs[idx] += sliceEnd - t;
                t = sliceEnd;
            }
        }
        var result = new List<(long, long)>();
        for (int i = 0; i < days; i++)
            if (secs[i] > 0) result.Add((start + (long)i * Day, secs[i] / 60));
        return result;
    }

    // ---- §9 drill-down detail (the per-player / per-event data behind the aggregate charts) ----
    //
    // These four back the v0.50.0 tester batch: rosters and timelines that need IDENTITIES and
    // TIMESTAMPS the bucket-count endpoints above can't carry, so Raphael genuinely can't derive them.

    /// <summary>
    /// §9a: every player whose FIRST-ever recorded session falls within the last <paramref name="days"/>
    /// days — the names behind the new-vs-returning counts — most-recent join first. First-seen is bounded
    /// by retained data (SessionRetentionDays), the same definition the <c>new</c> count uses.
    /// </summary>
    public List<(ulong steam, string name, long firstSeen, long playMinutes)> GetNewPlayersRoster(int days)
    {
        long now = Now;
        long start = DayFloor(now) - (long)(days - 1) * Day;

        var firstSeen = new Dictionary<ulong, long>();
        var totalSecs = new Dictionary<ulong, long>();
        foreach (var s in _sessions)
        {
            if (!firstSeen.TryGetValue(s.Steam, out var cur) || s.Connect < cur)
                firstSeen[s.Steam] = s.Connect;
            long end = s.Disconnect == 0 ? now : s.Disconnect;   // §10a: lifetime playtime (same total as `stats players`)
            if (end > s.Connect) { totalSecs.TryGetValue(s.Steam, out var t); totalSecs[s.Steam] = t + (end - s.Connect); }
        }

        var result = new List<(ulong, string, long, long)>();
        foreach (var kv in firstSeen)
            if (kv.Value >= start)
            {
                totalSecs.TryGetValue(kv.Key, out var secs);
                result.Add((kv.Key, _names.TryGetValue(kv.Key, out var n) ? n : kv.Key.ToString(), kv.Value, secs / 60));
            }
        result.Sort((a, b) => b.Item3.CompareTo(a.Item3)); // newest join first
        return result;
    }

    /// <summary>
    /// §9b: DISTINCT players active in each UTC hour-of-day (24 buckets), the denominator for the
    /// "average minutes per active player" toggle on the hours chart. Same hour-slicing as
    /// <see cref="GetHourHistogram"/>, but counting unique steam IDs per bucket instead of summing minutes.
    /// </summary>
    public int[] GetHourPlayerCounts(ulong? steam)
    {
        long now = Now;
        var sets = new HashSet<ulong>[24];
        for (int i = 0; i < 24; i++) sets[i] = new HashSet<ulong>();
        foreach (var s in _sessions)
        {
            if (steam.HasValue && s.Steam != steam.Value) continue;
            long t = s.Connect, end = s.Disconnect == 0 ? now : s.Disconnect;
            int guard = 0;
            while (t < end && guard++ < MaxSliceIterations)
            {
                int hour = DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime.Hour;
                sets[hour].Add(s.Steam);
                t = (t - (t % Hour)) + Hour; // advance to the next exact UTC hour
            }
        }
        var counts = new int[24];
        for (int h = 0; h < 24; h++) counts[h] = sets[h].Count;
        return counts;
    }

    /// <summary>
    /// §9c: individual online intervals (true connect→disconnect, open sessions end at "now") for one
    /// player or all, over the last <paramref name="days"/> days — a session is included if it OVERLAPS the
    /// window; start/end are the real timestamps (Raphael clips them to its render window). Start-ascending.
    /// </summary>
    public List<(ulong steam, string name, long start, long end)> GetSessionTimeline(ulong? steam, int days)
    {
        long now = Now;
        long windowStart = DayFloor(now) - (long)(days - 1) * Day;
        var result = new List<(ulong, string, long, long)>();
        foreach (var s in _sessions)
        {
            if (steam.HasValue && s.Steam != steam.Value) continue;
            long end = s.Disconnect == 0 ? now : s.Disconnect;
            if (end <= windowStart) continue; // entirely before the window
            result.Add((s.Steam, _names.TryGetValue(s.Steam, out var n) ? n : s.Steam.ToString(), s.Connect, end));
        }
        result.Sort((a, b) => a.Item3.CompareTo(b.Item3)); // start ascending
        return result;
    }

    /// <summary>
    /// §9d: per-player active-days grid over the last <paramref name="days"/> days — for every player with
    /// any activity in the window, the count of days played plus a compact CSV of <c>day:minutes</c> pairs
    /// (zero-days omitted). <c>day</c> is the UTC day NUMBER (days since epoch = unixMidnight/86400) to keep
    /// the line under the 509-char wire cap; multiply by 86400 for the midnight timestamp. If a row's CSV
    /// would overflow the budget the OLDEST days are dropped (recent-first), so a consumer can detect a
    /// truncated row when its CSV entry-count is less than <c>active</c>. Most-active player first.
    /// </summary>
    public List<(ulong steam, string name, int activeDays, string daysCsv)> GetActiveGrid(int days)
    {
        long now = Now;
        long start = DayFloor(now) - (long)(days - 1) * Day;

        var perPlayer = new Dictionary<ulong, long[]>();
        foreach (var s in _sessions)
        {
            long end = s.Disconnect == 0 ? now : s.Disconnect;
            if (end <= start) continue;
            if (!perPlayer.TryGetValue(s.Steam, out var arr)) { arr = new long[days]; perPlayer[s.Steam] = arr; }
            long t = Math.Max(s.Connect, start);
            int guard = 0;
            while (t < end && guard++ < days + 1)
            {
                long dayMid = DayFloor(t);
                int idx = (int)((dayMid - start) / Day);
                long sliceEnd = Math.Min(end, dayMid + Day);
                if (idx >= 0 && idx < days) arr[idx] += sliceEnd - t;
                t = sliceEnd;
            }
        }

        const int CsvBudget = 460; // keep the whole [FAUST:agrow] line under the 509-char wire cap
        var result = new List<(ulong, string, int, string)>(perPlayer.Count);
        foreach (var kv in perPlayer)
        {
            var arr = kv.Value;
            int active = 0, emitted = 0;
            var sb = new StringBuilder();
            // Walk most-recent-first so an over-budget row drops its OLDEST days, not its newest.
            for (int i = days - 1; i >= 0; i--)
            {
                long mins = arr[i] / 60;
                if (mins <= 0) continue;
                active++;
                long dayNum = (start + (long)i * Day) / Day; // days since epoch (compact, UTC)
                string entry = (emitted == 0 ? "" : ",") + dayNum + ":" + mins;
                if (sb.Length + entry.Length <= CsvBudget) { sb.Append(entry); emitted++; }
            }
            if (active == 0) continue;
            if (emitted < active)
                Core.Log.LogWarning($"[FAUST STORE] activegrid: steam {kv.Key} had {active} active day(s), emitted {emitted} (line cap).");
            result.Add((kv.Key, _names.TryGetValue(kv.Key, out var n) ? n : kv.Key.ToString(), active, sb.ToString()));
        }
        result.Sort((a, b) => b.Item3.CompareTo(a.Item3)); // most-active first
        return result;
    }

    // ---- §10c per-region daily series (forward-accumulating; Faust keeps no historical castle data) ----

    /// <summary>True once per UTC day — gate the (ECS-touching) region snapshot so we sample at most once
    /// per day, on that day's first connect/disconnect. Returns false until the day rolls over again.</summary>
    public bool ShouldSampleRegions() => DayFloor(Now) > _lastRegionSampleDay;

    /// <summary>
    /// Record today's per-region snapshot (castles / buildable plots / online players), upserting the
    /// (today, region) rows. The caller gathers the live counts from ECS (see <c>RegionStats.Gather</c>)
    /// and passes them in, keeping this store ECS-free. Throttled by <see cref="ShouldSampleRegions"/>;
    /// bounded by SessionRetentionDays. History starts at install — there is no pre-install region data.
    /// </summary>
    public void RecordRegionSnapshot(IReadOnlyList<(string region, int players, int castles, int plots)> rows)
    {
        if (rows is null || rows.Count == 0) return;
        long day = DayFloor(Now);
        foreach (var r in rows)
        {
            string region = string.IsNullOrEmpty(r.region) ? "-" : r.region;
            var existing = _regionDaily.Find(x => x.Day == day && string.Equals(x.Region, region, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { existing.Castles = r.castles; existing.Plots = r.plots; existing.Players = r.players; }
            else _regionDaily.Add(new RegionSample { Day = day, Region = region, Castles = r.castles, Plots = r.plots, Players = r.players });
        }
        _lastRegionSampleDay = day;
        PruneOldRegionDaily();
        SaveSync();
    }

    /// <summary>Drop region samples older than SessionRetentionDays (0 = keep forever).</summary>
    void PruneOldRegionDaily()
    {
        int days = Settings.SessionRetentionDays.Value;
        if (days <= 0) return;
        long cutoff = DayFloor(Now) - (long)days * Day;
        _regionDaily.RemoveAll(s => s.Day < cutoff);
    }

    /// <summary>
    /// The per-region daily snapshots within the last <paramref name="days"/> days (oldest day first,
    /// then region name). Sparse: only days that were actually sampled appear (a day with no player
    /// activity has no row), accumulated from install — consistent with the other server-lifetime series.
    /// </summary>
    public List<(long day, string region, int castles, int plots, int players)> GetRegionDaily(int days)
    {
        long start = DayFloor(Now) - (long)(days - 1) * Day;
        var result = new List<(long, string, int, int, int)>();
        foreach (var s in _regionDaily)
            if (s.Day >= start) result.Add((s.Day, s.Region, s.Castles, s.Plots, s.Players));
        result.Sort((a, b) => a.Item1 != b.Item1
            ? a.Item1.CompareTo(b.Item1)
            : string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    // ---- holistic population health (retention / active counts / recency / concurrency summary) ----

    /// <summary>Per-player (firstSeen, lastActivity) across the whole retained log — the basis for the
    /// population, retention and recency rollups. Open sessions extend lastActivity to "now".</summary>
    Dictionary<ulong, (long first, long last)> PlayerSpans()
    {
        long now = Now;
        var d = new Dictionary<ulong, (long, long)>();
        foreach (var s in _sessions)
        {
            long end = s.Disconnect == 0 ? now : s.Disconnect;
            if (!d.TryGetValue(s.Steam, out var span)) d[s.Steam] = (s.Connect, end);
            else d[s.Steam] = (Math.Min(span.Item1, s.Connect), Math.Max(span.Item2, end));
        }
        return d;
    }

    public readonly struct PopulationStats
    {
        public int Dau { get; init; } public int Wau { get; init; } public int Mau { get; init; }
        public int NewToday { get; init; } public int ReturningToday { get; init; }
        public double Stickiness { get; init; }                 // DAU/MAU, 0..1
        public double D1 { get; init; } public double D7 { get; init; } public double D30 { get; init; } // retention rates
    }

    /// <summary>Active-population + retention headline. DAU = active in the current UTC day; WAU/MAU =
    /// active within the last 7/30 days. Retention dN = of players who first appeared ≥ N days ago (so
    /// they had the chance to return), the fraction still active ≥ N days after their first session.</summary>
    public PopulationStats GetPopulationStats()
    {
        long now = Now, today = DayFloor(now);
        var spans = PlayerSpans();
        int dau = 0, wau = 0, mau = 0, newToday = 0;
        int c1 = 0, r1 = 0, c7 = 0, r7 = 0, c30 = 0, r30 = 0;
        foreach (var (first, last) in spans.Values)
        {
            if (last >= today) dau++;
            if (last >= now - 7 * Day) wau++;
            if (last >= now - 30 * Day) mau++;
            if (first >= today) newToday++;
            if (first <= now - 1 * Day) { c1++; if (last >= first + 1 * Day) r1++; }
            if (first <= now - 7 * Day) { c7++; if (last >= first + 7 * Day) r7++; }
            if (first <= now - 30 * Day) { c30++; if (last >= first + 30 * Day) r30++; }
        }
        int returning = Math.Max(0, dau - newToday);
        return new PopulationStats
        {
            Dau = dau, Wau = wau, Mau = mau, NewToday = newToday, ReturningToday = returning,
            Stickiness = mau > 0 ? Math.Round((double)dau / mau, 2) : 0,
            D1 = Rate(r1, c1), D7 = Rate(r7, c7), D30 = Rate(r30, c30),
        };
    }

    static double Rate(int n, int d) => d > 0 ? Math.Round((double)n / d, 2) : 0;

    /// <summary>How many known players were last seen within 24h / 7d / 30d (cumulative), plus the
    /// dormant remainder (&gt;30d) and the total tracked. The "who's drifting away" view.</summary>
    public (int seen24h, int seen7d, int seen30d, int dormant, int total) GetRecencyBuckets()
    {
        long now = Now;
        var spans = PlayerSpans();
        int s24 = 0, s7 = 0, s30 = 0;
        foreach (var (_, last) in spans.Values)
        {
            if (last >= now - 1 * Day) s24++;
            if (last >= now - 7 * Day) s7++;
            if (last >= now - 30 * Day) s30++;
        }
        return (s24, s7, s30, spans.Count - s30, spans.Count);
    }

    /// <summary>Peak / average / p95 of the concurrency samples in the last <paramref name="days"/> days
    /// (0 = all stored), plus the live online count. NOTE: samples are event-driven (taken on
    /// connect/disconnect), so `avg` is sample-weighted, not time-weighted — peak/p95 are the solid figures.</summary>
    public (int peak, long peakT, double avg, int p95, int now) GetConcurrencySummary(int days)
    {
        long cutoff = days > 0 ? Now - (long)days * Day : 0;
        var counts = new List<int>();
        int peak = 0; long peakT = 0, sum = 0;
        foreach (var c in _concurrency)
        {
            if (c.T < cutoff) continue;
            counts.Add(c.Count); sum += c.Count;
            if (c.Count > peak) { peak = c.Count; peakT = c.T; }
        }
        int n = counts.Count;
        double avg = n > 0 ? Math.Round((double)sum / n, 1) : 0;
        int p95 = 0;
        if (n > 0)
        {
            counts.Sort();
            int idx = (int)Math.Ceiling(0.95 * n) - 1;
            p95 = counts[Math.Clamp(idx, 0, n - 1)];
        }
        return (peak, peakT, avg, p95, _online.Count);
    }

    public readonly struct PlayerRow
    {
        public ulong Steam { get; init; }
        public string Name { get; init; }
        public bool Online { get; init; }
        public long LastOnlineUnix { get; init; }
        public bool Active24h { get; init; }
        public bool Active7d { get; init; }
        public int Sessions { get; init; }
        public long PlayMinutes { get; init; }
        public int DaysIdle { get; init; }
    }

    /// <summary>
    /// Per-player activity snapshot for EVERY tracked player, playtime-descending — the roster behind the
    /// aggregate population/recency numbers (Raphael §7). Same fields Faust derives for one player in
    /// <c>pinfo</c>, emitted for all in one paged list so a dashboard can show who's behind the totals.
    /// </summary>
    public List<PlayerRow> GetPlayerRoster()
    {
        long now = Now;
        var agg = new Dictionary<ulong, (long first, long last, long secs, int count, bool open)>();
        foreach (var s in _sessions)
        {
            long end = s.Disconnect == 0 ? now : s.Disconnect;
            long dur = Math.Max(0, end - s.Connect);
            bool open = s.Disconnect == 0;
            if (!agg.TryGetValue(s.Steam, out var a))
                agg[s.Steam] = (s.Connect, end, dur, 1, open);
            else
                agg[s.Steam] = (Math.Min(a.first, s.Connect), Math.Max(a.last, end), a.secs + dur, a.count + 1, a.open || open);
        }

        var rows = new List<PlayerRow>(agg.Count);
        foreach (var kv in agg)
        {
            var a = kv.Value;
            bool online = _online.Contains(kv.Key) || a.open;
            rows.Add(new PlayerRow
            {
                Steam = kv.Key,
                Name = _names.TryGetValue(kv.Key, out var n) ? n : kv.Key.ToString(),
                Online = online,
                LastOnlineUnix = online ? now : a.last,
                Active24h = a.last >= now - 1 * Day,
                Active7d = a.last >= now - 7 * Day,
                Sessions = a.count,
                PlayMinutes = a.secs / 60,
                DaysIdle = online ? 0 : (int)((now - a.last) / Day),
            });
        }
        rows.Sort((x, y) => y.PlayMinutes.CompareTo(x.PlayMinutes));
        return rows;
    }

    public int OnlineCount => _online.Count;

    /// <summary>Last-known character name for a SteamID (falls back to the bare id). Shared by other
    /// services that report player leaderboards for offline players (e.g. the kills board).</summary>
    public string GetName(ulong steam) => _names.TryGetValue(steam, out var n) && !string.IsNullOrEmpty(n) ? n : steam.ToString();

    // ---- admin data management (manual status / clear / wipe; design: collection control §10) ----

    /// <summary>Footprint of the collected activity, for the <c>.faust admin data status</c> readout.</summary>
    public (int sessions, int concurrency, int regionSamples, int names, long oldestConnectUnix) GetStorageStats()
    {
        long oldest = 0;
        foreach (var s in _sessions) if (oldest == 0 || s.Connect < oldest) oldest = s.Connect;
        return (_sessions.Count, _concurrency.Count, _regionDaily.Count, _names.Count, oldest);
    }

    /// <summary>Prune CLOSED sessions, concurrency points, and region samples older than
    /// <paramref name="days"/> days on demand (independent of the SessionRetentionDays config). Open
    /// sessions are never dropped. Returns the number of records removed.</summary>
    public int ClearOlderThan(int days)
    {
        if (days <= 0) return 0;
        long cutoff = Now - (long)days * 86400L;
        int before = _sessions.Count + _concurrency.Count + _regionDaily.Count;
        _sessions.RemoveAll(s => s.Disconnect != 0 && s.Disconnect < cutoff);
        _concurrency.RemoveAll(c => c.T < cutoff);
        _regionDaily.RemoveAll(r => r.Day < cutoff);
        int removed = before - (_sessions.Count + _concurrency.Count + _regionDaily.Count);
        if (removed > 0) SaveSync();
        return removed;
    }

    /// <summary>Erase ALL collected activity (sessions, concurrency, region samples, name cache). Players
    /// currently online get a fresh session re-opened so live tracking continues seamlessly. Returns
    /// records erased.</summary>
    public int WipeAll()
    {
        int count = _sessions.Count + _concurrency.Count + _regionDaily.Count + _names.Count;
        _sessions.Clear();
        _concurrency.Clear();
        _regionDaily.Clear();
        _lastRegionSampleDay = 0;
        _names.Clear();
        if (Settings.SessionTracking.Value)
            foreach (var steam in _online)
                _sessions.Add(new Session { Steam = steam, Connect = Now, Disconnect = 0 });
        SaveSync();
        return count;
    }
}

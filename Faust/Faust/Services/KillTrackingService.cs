using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Faust.Config;

namespace Faust.Services;

/// <summary>
/// Kill tally — the passive collector behind the kills leaderboards (design §9 Tier C "stats kills",
/// and the boss-defeat counts for server-messaging leaderboards). Fed from the existing
/// <c>DeathEventListenerSystem</c> hook (the same one that records unlock boss-kills), so it adds only
/// O(deaths) work and nothing periodic. Two tallies, both per UTC day (so any rolling window is cheap):
///   • per-player kills (and of those, PvP kills — victim was a player),
///   • per-VBlood defeat counts (how many times each boss has fallen, server-wide).
///
/// A NEW passive collector, so it obeys design §10: opt-out via <c>[Faust.Collection] KillTracking</c>
/// (the hook no-ops when off) and is bounded by the shared <c>SessionRetentionDays</c> retention knob.
/// Persists to BepInEx/config/Faust/kills.json (mirrors <see cref="UsageStatsService"/>'s day-bucket idiom).
/// </summary>
internal sealed class KillTrackingService
{
    sealed class KillBucket { public int Kills { get; set; } public int Pvp { get; set; } }

    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, KillBucket> PlayerKills { get; set; } = new(); // "steam|day" -> bucket
        public Dictionary<string, int> BossKills { get; set; } = new();          // "guid|day"  -> count
    }

    sealed class MemKill { public int Kills, Pvp; }

    // steam -> day -> kills, and bossGuid -> day -> count
    readonly Dictionary<ulong, Dictionary<long, MemKill>> _players = new();
    readonly Dictionary<int, Dictionary<long, int>> _bosses = new();
    bool _dirty; // kills are high-volume — we batch writes (see Flush), never save per kill

    static string SaveDir => FaustPaths.DataDir;
    static string SavePath => Path.Combine(SaveDir, "kills.json");

    const long Day = 86400L;
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static long DayFloor(long t) => t - (t % Day);

    /// <summary>True if kill collection is on (the cheap gate the death hook checks before tallying).</summary>
    public static bool Enabled => Settings.KillTracking.Value;

    public void Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            var file = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(SavePath));
            if (file is null) return;
            _players.Clear(); _bosses.Clear();
            foreach (var kvp in file.PlayerKills ?? new())
            {
                if (!SplitKey(kvp.Key, out var bar)) continue;
                if (!ulong.TryParse(kvp.Key.Substring(0, bar), out var steam)) continue;
                if (!long.TryParse(kvp.Key.Substring(bar + 1), out var day)) continue;
                var mb = PlayerBucket(steam, day);
                mb.Kills = kvp.Value.Kills; mb.Pvp = kvp.Value.Pvp;
            }
            foreach (var kvp in file.BossKills ?? new())
            {
                if (!SplitKey(kvp.Key, out var bar)) continue;
                if (!int.TryParse(kvp.Key.Substring(0, bar), out var guid)) continue;
                if (!long.TryParse(kvp.Key.Substring(bar + 1), out var day)) continue;
                BossDays(guid)[day] = kvp.Value;
            }
            Prune();
            Core.Log.LogInfo($"[FAUST KILLS] loaded kill tallies for {_players.Count} player(s), {_bosses.Count} boss(es).");
        }
        catch (Exception ex) { Core.Log.LogError($"[FAUST KILLS] failed loading {SavePath}: {ex}"); }
    }

    static bool SplitKey(string key, out int bar) { bar = key.LastIndexOf('|'); return bar > 0; }

    void SaveSync()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            var file = new SaveFile();
            foreach (var (steam, days) in _players)
                foreach (var (day, mb) in days)
                    file.PlayerKills[$"{steam}|{day}"] = new KillBucket { Kills = mb.Kills, Pvp = mb.Pvp };
            foreach (var (guid, days) in _bosses)
                foreach (var (day, count) in days)
                    file.BossKills[$"{guid}|{day}"] = count;
            File.WriteAllText(SavePath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex) { Core.Log.LogError($"[FAUST KILLS] failed saving {SavePath}: {ex}"); }
    }

    MemKill PlayerBucket(ulong steam, long day)
    {
        if (!_players.TryGetValue(steam, out var days)) _players[steam] = days = new Dictionary<long, MemKill>();
        if (!days.TryGetValue(day, out var mb)) days[day] = mb = new MemKill();
        return mb;
    }

    Dictionary<long, int> BossDays(int guid)
    {
        if (!_bosses.TryGetValue(guid, out var days)) _bosses[guid] = days = new Dictionary<long, int>();
        return days;
    }

    /// <summary>Honour SessionRetentionDays (shared collection knob): drop day-buckets older than the
    /// window. 0 = keep forever.</summary>
    void Prune()
    {
        int days = Settings.SessionRetentionDays.Value;
        if (days <= 0) return;
        long cutoff = DayFloor(Now) - (long)days * Day;
        foreach (var d in _players.Values) RemoveOld(d, cutoff);
        foreach (var d in _bosses.Values) RemoveOld(d, cutoff);
    }

    static void RemoveOld<T>(Dictionary<long, T> byDay, long cutoff)
    {
        List<long> stale = null;
        foreach (var day in byDay.Keys) if (day < cutoff) (stale ??= new()).Add(day);
        if (stale != null) foreach (var d in stale) byDay.Remove(d);
    }

    // ---- recording (called from the death hook) ----

    /// <summary>Record one player-caused kill: +1 to the killer's daily total, +1 PvP if the victim was a
    /// player, and — when the victim was a V Blood — +1 to that boss's daily defeat count. In-memory only;
    /// the write is batched (see <see cref="Flush"/>) because kills are a high-frequency event.</summary>
    public void RecordKill(ulong killerSteam, bool victimWasPlayer, int vbloodGuid)
    {
        if (!Enabled) return;
        long day = DayFloor(Now);

        if (killerSteam != 0)
        {
            var mb = PlayerBucket(killerSteam, day);
            mb.Kills++;
            if (victimWasPlayer) mb.Pvp++;
            _dirty = true;
        }
        if (vbloodGuid != 0)
        {
            var days = BossDays(vbloodGuid);
            days.TryGetValue(day, out var c);
            days[day] = c + 1;
            _dirty = true;
        }
    }

    /// <summary>Persist accumulated kills if anything changed (called on a timer + at shutdown). Pruning
    /// piggybacks here, not on the hot per-kill path.</summary>
    public void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        Prune();
        SaveSync();
    }

    // ---- reporting ----

    public readonly struct KillRow { public ulong Steam { get; init; } public int Kills { get; init; } public int Pvp { get; init; } }
    public readonly struct BossKillRow { public int Guid { get; init; } public int Count { get; init; } }

    static long CutoffFor(int days) => days <= 0 ? long.MinValue : DayFloor(Now) - (long)(days - 1) * Day;

    /// <summary>Top players by kills over the last <paramref name="days"/> UTC days (0 = all-time),
    /// kills-descending. Players with no kills in the window are omitted.</summary>
    public List<KillRow> GetTopKillers(int days)
    {
        long cutoff = CutoffFor(days);
        var rows = new List<KillRow>();
        foreach (var (steam, byDay) in _players)
        {
            int kills = 0, pvp = 0;
            foreach (var (day, mb) in byDay) { if (day < cutoff) continue; kills += mb.Kills; pvp += mb.Pvp; }
            if (kills > 0) rows.Add(new KillRow { Steam = steam, Kills = kills, Pvp = pvp });
        }
        rows.Sort((a, b) => b.Kills.CompareTo(a.Kills));
        return rows;
    }

    /// <summary>Per-boss defeat counts over the last <paramref name="days"/> UTC days (0 = all-time),
    /// count-descending. Bosses not defeated in the window are omitted.</summary>
    public List<BossKillRow> GetBossKills(int days)
    {
        long cutoff = CutoffFor(days);
        var rows = new List<BossKillRow>();
        foreach (var (guid, byDay) in _bosses)
        {
            int count = 0;
            foreach (var (day, c) in byDay) { if (day < cutoff) continue; count += c; }
            if (count > 0) rows.Add(new BossKillRow { Guid = guid, Count = count });
        }
        rows.Sort((a, b) => b.Count.CompareTo(a.Count));
        return rows;
    }

    // ---- admin data management ----

    /// <summary>(playerDayBuckets, bossDayBuckets) — for the data-status footprint.</summary>
    public (int playerBuckets, int bossBuckets) BucketCounts
    {
        get
        {
            int p = 0; foreach (var d in _players.Values) p += d.Count;
            int b = 0; foreach (var d in _bosses.Values) b += d.Count;
            return (p, b);
        }
    }

    /// <summary>Erase all kill tallies. Returns the number of (entity, day) buckets removed.</summary>
    public int WipeAll()
    {
        var (p, b) = BucketCounts;
        _players.Clear(); _bosses.Clear();
        SaveSync();
        return p + b;
    }
}

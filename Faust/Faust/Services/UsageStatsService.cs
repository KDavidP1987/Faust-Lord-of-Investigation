using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Faust.Config;

namespace Faust.Services;

/// <summary>
/// §8e usage accounting — the server-side record of HOW Faust features are actually used, for the admin
/// oversight endpoint <c>.faust api usage [days]</c>. Faust already enforces costs/cooldowns
/// server-side, so it owns this data first-hand (no client round-trip): on every successful query the
/// gate's <see cref="FaustAccessGate.Commit"/> calls <see cref="RecordUse"/>, and a cooldown/window deny
/// calls <see cref="RecordCooldownHit"/>.
///
/// Stored as per-(feature, UTC-day) buckets in BepInEx/config/Faust/feature_usage_stats.json, mirroring
/// <see cref="UsageService"/>'s load / synchronous-save idiom (query volume is low). Daily buckets let
/// <see cref="GetUsage"/> answer any rolling window cheaply; <see cref="SessionRetentionDays"/> bounds growth.
/// Distinct from <see cref="UsageService"/>, which holds the live cooldown/window LOCK state — this is the
/// historical tally for reporting.
/// </summary>
internal sealed class UsageStatsService
{
    sealed class Bucket
    {
        public int Uses { get; set; }
        public long ItemSpent { get; set; }
        public int CooldownHits { get; set; }
        public List<ulong> Payers { get; set; } = new(); // distinct payers that UTC day (serialized as a list)
    }

    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 1;
        // "feature|dayMidnightUnix" -> bucket
        public Dictionary<string, Bucket> Stats { get; set; } = new();
    }

    // feature -> dayMidnightUnix -> bucket (payers held as a set in memory for cheap distinct adds)
    sealed class MemBucket
    {
        public int Uses, CooldownHits;
        public long ItemSpent;
        public readonly HashSet<ulong> Payers = new();
    }

    readonly Dictionary<string, Dictionary<long, MemBucket>> _byFeature = new();

    static string SaveDir => FaustPaths.DataDir;
    static string SavePath => Path.Combine(SaveDir, "feature_usage_stats.json");

    const long Day = 86400L;
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static long DayFloor(long t) => t - (t % Day);

    public void Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            var file = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(SavePath));
            if (file?.Stats is null) return;
            _byFeature.Clear();
            foreach (var kvp in file.Stats)
            {
                int bar = kvp.Key.LastIndexOf('|');
                if (bar <= 0 || !long.TryParse(kvp.Key.Substring(bar + 1), out var day)) continue;
                string feature = kvp.Key.Substring(0, bar);
                var mb = GetBucket(feature, day);
                mb.Uses = kvp.Value.Uses;
                mb.ItemSpent = kvp.Value.ItemSpent;
                mb.CooldownHits = kvp.Value.CooldownHits;
                foreach (var p in kvp.Value.Payers ?? new()) mb.Payers.Add(p);
            }
            Prune();
            Core.Log.LogInfo($"[FAUST USAGESTATS] loaded usage tallies for {_byFeature.Count} feature(s).");
        }
        catch (Exception ex) { Core.Log.LogError($"[FAUST USAGESTATS] failed loading {SavePath}: {ex}"); }
    }

    void SaveSync()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            var file = new SaveFile();
            foreach (var (feature, days) in _byFeature)
                foreach (var (day, mb) in days)
                    file.Stats[$"{feature}|{day}"] = new Bucket
                    {
                        Uses = mb.Uses,
                        ItemSpent = mb.ItemSpent,
                        CooldownHits = mb.CooldownHits,
                        Payers = new List<ulong>(mb.Payers),
                    };
            File.WriteAllText(SavePath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex) { Core.Log.LogError($"[FAUST USAGESTATS] failed saving {SavePath}: {ex}"); }
    }

    MemBucket GetBucket(string feature, long day)
    {
        if (!_byFeature.TryGetValue(feature, out var days)) _byFeature[feature] = days = new Dictionary<long, MemBucket>();
        if (!days.TryGetValue(day, out var mb)) days[day] = mb = new MemBucket();
        return mb;
    }

    /// <summary>Honour SessionRetentionDays (shared collection knob): drop day-buckets older than the
    /// window. 0 = keep forever.</summary>
    void Prune()
    {
        int days = Settings.SessionRetentionDays.Value;
        if (days <= 0) return;
        long cutoff = DayFloor(Now) - (long)days * Day;
        foreach (var fkv in _byFeature)
        {
            var stale = new List<long>();
            foreach (var d in fkv.Value.Keys) if (d < cutoff) stale.Add(d);
            foreach (var d in stale) fkv.Value.Remove(d);
        }
    }

    // ---- recording (called from FaustAccessGate) ----

    /// <summary>Record a successful query: +1 use, the payer (if a cost was owed) and the quantity spent.</summary>
    public void RecordUse(string feature, ulong steam, bool paid, int qtySpent)
    {
        if (string.IsNullOrEmpty(feature)) return;
        var mb = GetBucket(feature, DayFloor(Now));
        mb.Uses++;
        if (paid)
        {
            if (steam != 0) mb.Payers.Add(steam);
            if (qtySpent > 0) mb.ItemSpent += qtySpent;
        }
        Prune();
        SaveSync();
    }

    /// <summary>Record a cooldown/window denial for a feature (the requester was rate-limited).</summary>
    public void RecordCooldownHit(string feature)
    {
        if (string.IsNullOrEmpty(feature)) return;
        GetBucket(feature, DayFloor(Now)).CooldownHits++;
        Prune();
        SaveSync();
    }

    // ---- reporting (`.faust api usage [days]`) ----

    public readonly struct UsageRow
    {
        public string Feature { get; init; }
        public int Uses { get; init; }
        public int Payers { get; init; }       // distinct paying players over the window
        public long ItemSpent { get; init; }
        public int CooldownHits { get; init; }
    }

    /// <summary>Per-feature usage aggregated over the last <paramref name="days"/> UTC days (clamped
    /// 1–365), uses-descending. Features with no recorded activity in the window are omitted.</summary>
    public List<UsageRow> GetUsage(int days)
    {
        days = Math.Clamp(days, 1, 365);
        long cutoff = DayFloor(Now) - (long)(days - 1) * Day;
        var rows = new List<UsageRow>();
        foreach (var (feature, byDay) in _byFeature)
        {
            int uses = 0, cdHits = 0; long spent = 0;
            var payers = new HashSet<ulong>();
            foreach (var (day, mb) in byDay)
            {
                if (day < cutoff) continue;
                uses += mb.Uses; cdHits += mb.CooldownHits; spent += mb.ItemSpent;
                foreach (var p in mb.Payers) payers.Add(p);
            }
            if (uses == 0 && cdHits == 0 && spent == 0) continue;
            rows.Add(new UsageRow { Feature = feature, Uses = uses, Payers = payers.Count, ItemSpent = spent, CooldownHits = cdHits });
        }
        rows.Sort((a, b) => b.Uses.CompareTo(a.Uses));
        return rows;
    }

    // ---- admin data management ----

    /// <summary>Tracked (feature, day) buckets — for the data-status readout.</summary>
    public int BucketCount
    {
        get { int n = 0; foreach (var d in _byFeature.Values) n += d.Count; return n; }
    }

    /// <summary>Erase all usage tallies. Returns the number of (feature, day) buckets removed.</summary>
    public int WipeAll()
    {
        int n = BucketCount;
        _byFeature.Clear();
        SaveSync();
        return n;
    }
}

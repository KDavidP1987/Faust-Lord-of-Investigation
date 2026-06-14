using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Faust.Config;

namespace Faust.Services;

/// <summary>
/// Player-position heat-map accumulation (an admin-configurable density grid). The position sampler
/// (<see cref="HeatmapSampler"/>, on a throttled server-tick) bins each online player's (x,z) into a
/// grid cell and increments a per-(player, cell) counter here. From that single structure we serve both
/// the **per-player** heat map (filter by steam) and the **aggregated server** heat map (sum over all
/// players) — feature #—, Raphael renders the visualization.
///
/// Persisted to BepInEx/config/Faust/heatmap.json (separate from the session store: different shape,
/// and it's opt-in via <c>[Faust.Heatmap] Enabled</c>). Reset with <c>.faust admin data wipe heatmap</c>.
///
/// TWO layers from one sampler (so windowed queries don't cost the all-time map its exactness):
///   • the <b>cumulative</b> grid (<c>_grid</c>) — density since install/last reset; serves all-time
///     (<c>days=0</c>) queries cheaply and exactly, bounded by <c>MaxCells</c>.
///   • a <b>per-UTC-day</b> grid (<c>_daily</c>) — the same bins stamped by day, pruned to
///     <c>[Faust.Heatmap] RetentionDays</c>; serves windowed queries (today / last N days). Bounded by the
///     same <c>MaxCells</c> cap on distinct (day, player, cell) entries, plus retention pruning.
///
/// The grid resolution (<c>CellSize</c>) is fixed once data exists: the stored cells were binned at a
/// specific size, so the store keeps using that size and ignores a changed config until the data is
/// wiped (mixing resolutions would corrupt the map).
/// </summary>
internal sealed class HeatmapStore
{
    sealed class CellRow
    {
        public ulong Steam { get; set; }
        public int Cx { get; set; }
        public int Cz { get; set; }
        public int Count { get; set; }
    }

    sealed class DailyCellRow
    {
        public int Day { get; set; }   // UTC days since the Unix epoch
        public ulong Steam { get; set; }
        public int Cx { get; set; }
        public int Cz { get; set; }
        public int Count { get; set; }
    }

    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 2;
        public float CellSize { get; set; }    // resolution the stored cells were binned at
        public long Samples { get; set; }       // total player-position samples accumulated (all-time)
        public List<CellRow> Cells { get; set; } = new();          // cumulative all-time grid
        public List<DailyCellRow> Daily { get; set; } = new();      // per-UTC-day grid (v2+)
    }

    // steam -> packed(cx,cz) -> count (cumulative all-time)
    readonly Dictionary<ulong, Dictionary<long, int>> _grid = new();
    // day -> steam -> packed(cx,cz) -> count (per-UTC-day, retention-pruned). Day-keyed at the top so a
    // window query iterates only the relevant days and pruning drops a whole day in one step.
    readonly Dictionary<int, Dictionary<ulong, Dictionary<long, int>>> _daily = new();
    long _samples;          // total recorded player-positions (for normalization / "based on N samples")
    int _cellCount;         // distinct (steam, cell) entries in the all-time grid — bounded by MaxCells
    int _dailyCellCount;    // distinct (day, steam, cell) entries in the daily grid — bounded by MaxCells
    float _cellSize;        // the resolution the current grid is binned at (0 = no data yet)
    int _dirtyTicks;        // sample-ticks since the last save (we batch writes)
    bool _cappedLogged;
    bool _dailyCappedLogged;

    const int SaveEveryTicks = 5; // persist every ~5 sample ticks (plus a flush on shutdown)

    static string SavePath => Path.Combine(FaustPaths.DataDir, "heatmap.json");

    /// <summary>Current UTC day as a day-number (days since the Unix epoch).</summary>
    static int Today => (int)(DateTime.UtcNow - DateTime.UnixEpoch).TotalDays;

    static long Pack(int cx, int cz) => ((long)cx << 32) | (uint)cz;
    static int Cx(long key) => (int)(key >> 32);
    static int Cz(long key) => (int)(uint)key;

    public void Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            var file = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(SavePath));
            if (file is null) return;

            _grid.Clear();
            _daily.Clear();
            _cellCount = 0;
            _dailyCellCount = 0;
            _cellSize = file.CellSize;
            _samples = file.Samples;
            foreach (var c in file.Cells ?? new())
            {
                if (!_grid.TryGetValue(c.Steam, out var inner)) { inner = new Dictionary<long, int>(); _grid[c.Steam] = inner; }
                long key = Pack(c.Cx, c.Cz);
                if (!inner.ContainsKey(key)) _cellCount++;
                inner[key] = c.Count;
            }
            // Per-day layer (v2+; a v1 file has none — all-time still works, windows fill as data accrues).
            foreach (var d in file.Daily ?? new())
            {
                if (!_daily.TryGetValue(d.Day, out var byPlayer)) { byPlayer = new Dictionary<ulong, Dictionary<long, int>>(); _daily[d.Day] = byPlayer; }
                if (!byPlayer.TryGetValue(d.Steam, out var inner)) { inner = new Dictionary<long, int>(); byPlayer[d.Steam] = inner; }
                long key = Pack(d.Cx, d.Cz);
                if (!inner.ContainsKey(key)) _dailyCellCount++;
                inner[key] = d.Count;
            }
            PruneDaily(Settings.HeatmapRetentionDays.Value); // drop days now beyond the retention window
            Core.Log.LogInfo($"[FAUST HEATMAP] loaded {_cellCount} all-time cell(s) over {_grid.Count} player(s), " +
                             $"{_dailyCellCount} daily cell(s) over {_daily.Count} day(s), {_samples} sample(s) @ cell={_cellSize}.");
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[FAUST HEATMAP] failed loading {SavePath}: {ex}");
        }
    }

    /// <summary>Persist now (batched normally; called on shutdown and after a wipe).</summary>
    public void Flush()
    {
        _dirtyTicks = 0;
        try
        {
            Directory.CreateDirectory(FaustPaths.DataDir);
            var file = new SaveFile { CellSize = _cellSize, Samples = _samples };
            foreach (var kv in _grid)
                foreach (var cell in kv.Value)
                    file.Cells.Add(new CellRow { Steam = kv.Key, Cx = Cx(cell.Key), Cz = Cz(cell.Key), Count = cell.Value });
            foreach (var day in _daily)
                foreach (var byPlayer in day.Value)
                    foreach (var cell in byPlayer.Value)
                        file.Daily.Add(new DailyCellRow { Day = day.Key, Steam = byPlayer.Key, Cx = Cx(cell.Key), Cz = Cz(cell.Key), Count = cell.Value });
            File.WriteAllText(SavePath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[FAUST HEATMAP] failed saving {SavePath}: {ex}");
        }
    }

    /// <summary>
    /// Record one tick's worth of online player positions. Bins each (x,z) at the grid resolution and
    /// increments its per-(player, cell) counter. Adopts <paramref name="configCellSize"/> only when the
    /// grid is empty (otherwise keeps the stored resolution — a changed CellSize needs a wipe to take
    /// effect). New cells stop being added once <paramref name="maxCells"/> is reached (existing cells
    /// keep incrementing); this is logged once.
    /// </summary>
    public void RecordSamples(IReadOnlyList<(ulong steam, float x, float z)> samples, float configCellSize, int maxCells, int retentionDays)
    {
        if (samples is null || samples.Count == 0) return;
        if (_cellSize <= 0f) _cellSize = configCellSize > 0f ? configCellSize : 25f;
        float cs = _cellSize;

        int today = Today;
        PruneDaily(retentionDays); // keep the per-day layer within the retention window before adding to it
        if (!_daily.TryGetValue(today, out var todayByPlayer)) { todayByPlayer = new Dictionary<ulong, Dictionary<long, int>>(); _daily[today] = todayByPlayer; }

        foreach (var s in samples)
        {
            if (s.steam == 0) continue;
            int cx = (int)Math.Floor(s.x / cs);
            int cz = (int)Math.Floor(s.z / cs);
            long key = Pack(cx, cz);

            // All-time cumulative grid (bounded by maxCells).
            if (!_grid.TryGetValue(s.steam, out var inner)) { inner = new Dictionary<long, int>(); _grid[s.steam] = inner; }
            if (inner.TryGetValue(key, out var cur)) inner[key] = cur + 1;
            else if (maxCells <= 0 || _cellCount < maxCells) { inner[key] = 1; _cellCount++; }
            else
            {
                if (!_cappedLogged) { Core.Log.LogWarning($"[FAUST HEATMAP] cell cap ({maxCells}) reached — only existing cells keep counting."); _cappedLogged = true; }
                continue; // capped: don't add a new cell, and don't count this as a sample
            }
            _samples++;

            // Per-day grid (same bin, stamped today; bounded by the same maxCells on distinct (day,player,cell)).
            if (!todayByPlayer.TryGetValue(s.steam, out var dInner)) { dInner = new Dictionary<long, int>(); todayByPlayer[s.steam] = dInner; }
            if (dInner.TryGetValue(key, out var dCur)) dInner[key] = dCur + 1;
            else if (maxCells <= 0 || _dailyCellCount < maxCells) { dInner[key] = 1; _dailyCellCount++; }
            else if (!_dailyCappedLogged) { Core.Log.LogWarning($"[FAUST HEATMAP] daily cell cap ({maxCells}) reached — windowed maps stop adding new cells until older days prune out."); _dailyCappedLogged = true; }
        }

        if (++_dirtyTicks >= SaveEveryTicks) Flush();
    }

    /// <summary>Drop per-day buckets older than the retention window (keep the most recent
    /// <paramref name="retentionDays"/> days, including today). 0 or less = keep all retained days.</summary>
    void PruneDaily(int retentionDays)
    {
        if (retentionDays <= 0) return;
        int cutoff = Today - retentionDays + 1; // oldest day still kept
        if (_daily.Count == 0) return;
        List<int> drop = null;
        foreach (var day in _daily.Keys)
            if (day < cutoff) (drop ??= new List<int>()).Add(day);
        if (drop is null) return;
        foreach (var day in drop)
        {
            if (_daily.TryGetValue(day, out var byPlayer))
                foreach (var inner in byPlayer.Values) _dailyCellCount -= inner.Count;
            _daily.Remove(day);
        }
        if (_dailyCellCount < 0) _dailyCellCount = 0;
        _dailyCappedLogged = false; // pruning freed capacity; allow the cap warning to fire again if re-hit
    }

    public readonly struct HeatmapView
    {
        public List<(int cx, int cz, int count)> Cells { get; init; }
        public long Samples { get; init; }   // sample total in this scope (sum of cell counts)
        public float CellSize { get; init; }
        public int MinCx { get; init; } public int MinCz { get; init; }
        public int MaxCx { get; init; } public int MaxCz { get; init; }
    }

    /// <summary>The density grid for one player (<paramref name="steam"/>) or, when null, the whole server
    /// (summed over all players), over a time window. <paramref name="days"/> &lt;= 0 = all-time (the cumulative
    /// grid); &gt; 0 = the last N UTC days from the per-day layer (bounded by RetentionDays — a window past
    /// retention simply sums what's kept). Cells are count-descending (densest first), with the cell bounds.</summary>
    public HeatmapView GetHeatmap(ulong? steam, int days = 0)
    {
        var agg = new Dictionary<long, int>();
        if (days <= 0)
        {
            // All-time: the cumulative grid.
            if (steam.HasValue)
            {
                if (_grid.TryGetValue(steam.Value, out var inner))
                    foreach (var c in inner) agg[c.Key] = c.Value;
            }
            else
            {
                foreach (var inner in _grid.Values)
                    foreach (var c in inner) { agg.TryGetValue(c.Key, out var v); agg[c.Key] = v + c.Value; }
            }
        }
        else
        {
            // Windowed: sum the per-day layer over [today - days + 1 .. today].
            int from = Today - days + 1;
            foreach (var day in _daily)
            {
                if (day.Key < from) continue;
                if (steam.HasValue)
                {
                    if (day.Value.TryGetValue(steam.Value, out var inner))
                        foreach (var c in inner) { agg.TryGetValue(c.Key, out var v); agg[c.Key] = v + c.Value; }
                }
                else
                {
                    foreach (var inner in day.Value.Values)
                        foreach (var c in inner) { agg.TryGetValue(c.Key, out var v); agg[c.Key] = v + c.Value; }
                }
            }
        }

        long total = 0;
        int minCx = 0, minCz = 0, maxCx = 0, maxCz = 0; bool first = true;
        var cells = new List<(int, int, int)>(agg.Count);
        foreach (var c in agg)
        {
            int cx = Cx(c.Key), cz = Cz(c.Key);
            cells.Add((cx, cz, c.Value));
            total += c.Value;
            if (first) { minCx = maxCx = cx; minCz = maxCz = cz; first = false; }
            else { if (cx < minCx) minCx = cx; if (cx > maxCx) maxCx = cx; if (cz < minCz) minCz = cz; if (cz > maxCz) maxCz = cz; }
        }
        cells.Sort((a, b) => b.Item3.CompareTo(a.Item3)); // densest first
        return new HeatmapView
        {
            Cells = cells, Samples = total, CellSize = _cellSize <= 0f ? Settings.HeatmapCellSize.Value : _cellSize,
            MinCx = minCx, MinCz = minCz, MaxCx = maxCx, MaxCz = maxCz,
        };
    }

    /// <summary>Footprint for the <c>.faust admin data status</c> readout.</summary>
    public (int cells, long samples, int players) GetStats() => (_cellCount, _samples, _grid.Count);

    /// <summary>Erase the whole heat map. Returns distinct cells erased.</summary>
    public int WipeAll()
    {
        int count = _cellCount;
        _grid.Clear();
        _daily.Clear();
        _cellCount = 0;
        _dailyCellCount = 0;
        _samples = 0;
        _cellSize = 0f;
        _cappedLogged = false;
        _dailyCappedLogged = false;
        Flush();
        return count;
    }
}

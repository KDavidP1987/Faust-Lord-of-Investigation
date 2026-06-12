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
/// and it's opt-in via <c>[Faust.Heatmap] Enabled</c>). Cumulative since install/last reset — there is
/// no time dimension in v1 (density only); reset with <c>.faust admin data wipe heatmap</c>.
///
/// The grid resolution (<c>CellSize</c>) is fixed once data exists: the stored cells were binned at a
/// specific size, so the store keeps using that size and ignores a changed config until the data is
/// wiped (mixing resolutions would corrupt the map). Bounded by <c>MaxCells</c> (a cap on distinct
/// (player, cell) entries) so it can't grow without limit.
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

    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 1;
        public float CellSize { get; set; }    // resolution the stored cells were binned at
        public long Samples { get; set; }       // total player-position samples accumulated
        public List<CellRow> Cells { get; set; } = new();
    }

    // steam -> packed(cx,cz) -> count
    readonly Dictionary<ulong, Dictionary<long, int>> _grid = new();
    long _samples;          // total recorded player-positions (for normalization / "based on N samples")
    int _cellCount;         // distinct (steam, cell) entries — bounded by MaxCells
    float _cellSize;        // the resolution the current grid is binned at (0 = no data yet)
    int _dirtyTicks;        // sample-ticks since the last save (we batch writes)
    bool _cappedLogged;

    const int SaveEveryTicks = 5; // persist every ~5 sample ticks (plus a flush on shutdown)

    static string SavePath => Path.Combine(FaustPaths.DataDir, "heatmap.json");

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
            _cellCount = 0;
            _cellSize = file.CellSize;
            _samples = file.Samples;
            foreach (var c in file.Cells ?? new())
            {
                if (!_grid.TryGetValue(c.Steam, out var inner)) { inner = new Dictionary<long, int>(); _grid[c.Steam] = inner; }
                long key = Pack(c.Cx, c.Cz);
                if (!inner.ContainsKey(key)) _cellCount++;
                inner[key] = c.Count;
            }
            Core.Log.LogInfo($"[FAUST HEATMAP] loaded {_cellCount} cell(s) over {_grid.Count} player(s), {_samples} sample(s) @ cell={_cellSize}.");
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
    public void RecordSamples(IReadOnlyList<(ulong steam, float x, float z)> samples, float configCellSize, int maxCells)
    {
        if (samples is null || samples.Count == 0) return;
        if (_cellSize <= 0f) _cellSize = configCellSize > 0f ? configCellSize : 25f;
        float cs = _cellSize;

        foreach (var s in samples)
        {
            if (s.steam == 0) continue;
            int cx = (int)Math.Floor(s.x / cs);
            int cz = (int)Math.Floor(s.z / cs);
            long key = Pack(cx, cz);

            if (!_grid.TryGetValue(s.steam, out var inner)) { inner = new Dictionary<long, int>(); _grid[s.steam] = inner; }
            if (inner.TryGetValue(key, out var cur)) inner[key] = cur + 1;
            else if (maxCells <= 0 || _cellCount < maxCells) { inner[key] = 1; _cellCount++; }
            else
            {
                if (!_cappedLogged) { Core.Log.LogWarning($"[FAUST HEATMAP] cell cap ({maxCells}) reached — only existing cells keep counting."); _cappedLogged = true; }
                continue; // capped: don't add a new cell, and don't count this as a sample
            }
            _samples++;
        }

        if (++_dirtyTicks >= SaveEveryTicks) Flush();
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
    /// (summed over all players). Cells are count-descending (densest first), with the cell bounds.</summary>
    public HeatmapView GetHeatmap(ulong? steam)
    {
        var agg = new Dictionary<long, int>();
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
        _cellCount = 0;
        _samples = 0;
        _cellSize = 0f;
        _cappedLogged = false;
        Flush();
        return count;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Faust.Config;
using ProjectM;
using Stunlock.Core;
using Unity.Entities;
using Unity.Transforms;

namespace Faust.Services;

/// <summary>
/// World-asset scan (the map of in-game assets — the `worldscan` feature). Faust is the authoritative
/// global view, so it can answer "where is every &lt;ore/tree/plant/NPC&gt; on the map", which a
/// spatially-culled client can't. Two asset kinds, each enumerated by a purpose-built component so we scan
/// only relevant archetypes (not every entity):
///   • <b>node</b>  — harvestables, classified FIRST (authoritative): <see cref="YieldResourcesOnDamageTaken"/>
///     (ores/trees/rocks you hit) and <see cref="YieldResourcesOnPickup"/> (grass/flowers/plants you gather).
///     NOTE many nodes are *also* "units" (they have <see cref="UnitLevel"/>/<see cref="Health"/> so you can
///     damage them) — they are still nodes here, never NPCs.
///   • <b>unit</b>  — actual NPCs/characters: a <c>CHAR_*</c> prefab with <see cref="UnitLevel"/> that does
///     NOT yield resources, excluding players and V Bloods (those have the dedicated `bosses` feature).
///     Carry blood type + quality (the game's <see cref="Blood"/> component) and health, so a client can
///     filter "units with blood quality &gt; N" or by blood type.
///
/// Only WHITELISTED prefab GUIDs are returned (admin-curated — there are far too many prefabs to show all;
/// see the `.faust admin worldscan` commands). Scanning the whole map is EXPENSIVE, so a result is CACHED
/// and rebuilt at most once per <see cref="Settings.WorldScanInterval"/> (the cache TTL is the de-facto
/// rate limit), strictly ON DEMAND — zero cost when nobody queries. Bounded by <see cref="Settings.WorldScanMaxResults"/>.
/// </summary>
internal sealed class WorldScanService
{
    public readonly struct AssetSnapshot
    {
        public int Guid { get; init; }
        public string Name { get; init; }
        public bool IsUnit { get; init; }      // true = NPC unit, false = resource node
        public float X { get; init; }
        public float Z { get; init; }
        public string Region { get; init; }
        // ---- unit-only (sentineled for nodes) ----
        public float Hp { get; init; }
        public float HpMax { get; init; }
        public int BloodTypeGuid { get; init; }
        public string BloodType { get; init; } // wire-safe dev-name, "-" if none
        public int BloodQuality { get; init; } // 0–100, -1 if none
        public int UnitCategory { get; init; } // EntityCategory.UnitCategory (unit subtype), -1 if unknown
        public int ResourceTier { get; init; } // EntityCategory.ResourceLevel (node tier), -1 if unknown
    }

    public readonly struct Filter
    {
        public string Kind { get; init; }      // "units" | "nodes" | "all"
        public int Id { get; init; }           // prefab guid, 0 = any
        public int BloodType { get; init; }    // blood prefab guid, 0 = any (implies units)
        public int BloodQMin { get; init; }    // minimum blood quality, -1 = any (implies units)
        public int UnitType { get; init; }     // EntityCategory.UnitCategory, int.MinValue = any (implies units)
    }

    // Positions beyond this magnitude are off-map/pooled sentinels (see BossService.MapLimit) — skip them.
    const float MapLimit = 6000f;

    readonly Dictionary<int, string> _whitelist = new(); // guid -> last-known dev-name (for `list`)
    readonly List<AssetSnapshot> _cache = new();
    double _lastScan = double.MinValue;
    bool _truncated;

    static string SaveDir => FaustPaths.DataDir;
    static string SavePath => Path.Combine(SaveDir, "worldscan_whitelist.json");
    static double NowSeconds => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, string> Whitelist { get; set; } = new(); // guid -> name
    }

    // ---- whitelist persistence ----

    /// <summary>Load the whitelist; if there's no file yet, seed a comprehensive default from the prefab
    /// catalog (admins trim from there) so the feature works out of the box.</summary>
    public void Load()
    {
        bool existed = File.Exists(SavePath);
        try
        {
            if (existed)
            {
                var file = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(SavePath));
                if (file?.Whitelist != null)
                    foreach (var kvp in file.Whitelist)
                        if (int.TryParse(kvp.Key, out var g)) _whitelist[g] = kvp.Value;
                Core.Log.LogInfo($"[FAUST WORLDSCAN] loaded {_whitelist.Count} whitelisted prefab(s).");
            }
        }
        catch (Exception ex) { Core.Log.LogError($"[FAUST WORLDSCAN] failed loading {SavePath}: {ex}"); }

        if (!existed || _whitelist.Count == 0)
        {
            int seeded = Seed();
            Core.Log.LogInfo($"[FAUST WORLDSCAN] no whitelist found — seeded {seeded} prefab(s) from the catalog. " +
                             "Trim with '.faust admin worldscan remove <guid>' / 'clear'.");
        }
    }

    void SaveSync()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            var file = new SaveFile();
            foreach (var kvp in _whitelist) file.Whitelist[kvp.Key.ToString()] = kvp.Value;
            File.WriteAllText(SavePath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex) { Core.Log.LogError($"[FAUST WORLDSCAN] failed saving {SavePath}: {ex}"); }
    }

    public int WhitelistCount => _whitelist.Count;

    public bool AddToWhitelist(int guid)
    {
        if (_whitelist.ContainsKey(guid)) return false;
        _whitelist[guid] = new PrefabGUID(guid).GetPrefabName();
        _lastScan = double.MinValue; // force the next query to rescan against the new whitelist
        SaveSync();
        return true;
    }

    public bool RemoveFromWhitelist(int guid)
    {
        if (!_whitelist.Remove(guid)) return false;
        _lastScan = double.MinValue;
        SaveSync();
        return true;
    }

    public int ClearWhitelist()
    {
        int n = _whitelist.Count;
        _whitelist.Clear();
        _lastScan = double.MinValue;
        SaveSync();
        return n;
    }

    /// <summary>Whitelist entries (guid, name) for the admin `list` readout — name-sorted.</summary>
    public List<(int guid, string name)> ListWhitelist()
    {
        var list = new List<(int, string)>(_whitelist.Count);
        foreach (var kvp in _whitelist) list.Add((kvp.Key, kvp.Value));
        list.Sort((a, b) => string.CompareOrdinal(a.Item2, b.Item2));
        return list;
    }

    /// <summary>(Re)seed the whitelist from the prefab catalog: every harvestable node prefab + every
    /// CHAR_ unit prefab (excluding V Bloods). Comprehensive by design — admins trim. Returns the count.</summary>
    public int Seed()
    {
        try
        {
            SeedFrom<UnitLevel>(unitsOnly: true);
            SeedFrom<YieldResourcesOnDamageTaken>(unitsOnly: false);
            SeedFrom<YieldResourcesOnPickup>(unitsOnly: false);
            _lastScan = double.MinValue;
            SaveSync();
        }
        catch (Exception ex) { Core.Log.LogError($"[FAUST WORLDSCAN] seed failed: {ex.Message}"); }
        return _whitelist.Count;
    }

    void SeedFrom<T>(bool unitsOnly) where T : unmanaged
    {
        var ents = Query.GetEntitiesByComponentType<T>(includeDisabled: true, includePrefab: true);
        try
        {
            for (int i = 0; i < ents.Length; i++)
            {
                var e = ents[i];
                if (unitsOnly)
                {
                    if (e.Has<VBloodUnit>()) continue; // V Bloods are the dedicated `bosses` feature
                    if (e.Has<YieldResourcesOnDamageTaken>() || e.Has<YieldResourcesOnPickup>()) continue; // node, not a unit
                }
                int guid = e.GetPrefabGuid()._Value;
                if (guid == 0 || _whitelist.ContainsKey(guid)) continue;
                string name = new PrefabGUID(guid).GetPrefabName();
                if (unitsOnly && !name.StartsWith("CHAR_", StringComparison.Ordinal)) continue; // characters/NPCs only
                _whitelist[guid] = name;
            }
        }
        finally { ents.Dispose(); }
    }

    // ---- query (cached snapshot + on-demand rescan) ----

    /// <summary>Filtered, paged assets. Rebuilds the snapshot only if the cache is older than the configured
    /// interval (clamped to a 5s floor) — otherwise it re-filters the cache. <paramref name="truncated"/> is
    /// true if the last scan hit MaxResults.</summary>
    public List<AssetSnapshot> GetAssets(Filter filter, out bool truncated)
    {
        int interval = Math.Max(5, Settings.WorldScanInterval.Value);
        if (NowSeconds - _lastScan >= interval) Rescan();
        truncated = _truncated;

        var result = new List<AssetSnapshot>();
        foreach (var a in _cache) if (Matches(a, filter)) result.Add(a);
        return result;
    }

    static bool Matches(AssetSnapshot a, Filter f)
    {
        bool wantUnitOnly = f.BloodType != 0 || f.BloodQMin >= 0 || f.UnitType != int.MinValue;
        if (wantUnitOnly && !a.IsUnit) return false;              // blood/unittype filters imply units
        if (f.Kind == "units" && !a.IsUnit) return false;
        if (f.Kind == "nodes" && a.IsUnit) return false;
        if (f.Id != 0 && a.Guid != f.Id) return false;
        if (f.BloodType != 0 && a.BloodTypeGuid != f.BloodType) return false;
        if (f.BloodQMin >= 0 && a.BloodQuality < f.BloodQMin) return false;
        if (f.UnitType != int.MinValue && a.UnitCategory != f.UnitType) return false;
        return true;
    }

    void Rescan()
    {
        _cache.Clear();
        _truncated = false;
        int cap = Settings.WorldScanMaxResults.Value;
        if (cap <= 0) cap = int.MaxValue;
        try
        {
            ScanUnits(cap);
            ScanNodes<YieldResourcesOnDamageTaken>(cap);
            ScanNodes<YieldResourcesOnPickup>(cap);
        }
        catch (Exception ex) { Core.Log.LogError($"[FAUST WORLDSCAN] scan failed: {ex.Message}"); }
        _lastScan = NowSeconds;
        if (_truncated)
            Core.Log.LogWarning($"[FAUST WORLDSCAN] hit MaxResults={cap}; snapshot truncated (filter the query or raise MaxResults).");
    }

    void ScanUnits(int cap)
    {
        var ents = Query.GetEntitiesByComponentType<UnitLevel>(includeDisabled: true);
        try
        {
            for (int i = 0; i < ents.Length; i++)
            {
                if (_cache.Count >= cap) { _truncated = true; return; }
                var e = ents[i];
                if (e.Has<PlayerCharacter>() || e.Has<VBloodUnit>()) continue; // not players, not bosses
                // Many resource nodes (trees/plants/ore) are ALSO "units" (UnitLevel+Health) — classify them
                // as nodes, never NPCs. A real character is a CHAR_* prefab that doesn't yield resources.
                if (e.Has<YieldResourcesOnDamageTaken>() || e.Has<YieldResourcesOnPickup>()) continue;
                int guid = e.GetPrefabGuid()._Value;
                if (guid == 0 || !_whitelist.ContainsKey(guid)) continue;
                string name = new PrefabGUID(guid).GetPrefabName();
                if (!name.StartsWith("CHAR_", StringComparison.Ordinal)) continue; // characters/NPCs only
                if (!e.TryGetComponent<LocalToWorld>(out var ltw)) continue;
                var pos = ltw.Position;
                if (Math.Abs(pos.x) > MapLimit || Math.Abs(pos.z) > MapLimit) continue; // off-map/pooled

                // A WORLD unit's feedable blood is in BloodConsumeSource (UnitBloodType + BloodQuality) — NOT
                // the Blood component (that's the PLAYER's blood pool; only players/stored prisoners carry it).
                // Read the consume source first, fall back to Blood so a captured prisoner still resolves.
                int btGuid = 0; string btName = "-"; int bq = -1;
                if (e.TryGetComponent<BloodConsumeSource>(out var bcs))
                {
                    var bloodPrefab = bcs.UnitBloodType._Value; // ModifiablePrefabGUID._Value -> PrefabGUID
                    btGuid = bloodPrefab._Value;
                    btName = bloodPrefab.GetPrefabName();
                    bq = (int)Math.Round(bcs.BloodQuality);
                }
                else if (e.TryGetComponent<Blood>(out var blood))
                {
                    btGuid = blood.BloodType._Value;
                    btName = blood.BloodType.GetPrefabName();
                    bq = (int)Math.Round(blood.Quality);
                }
                bool hasHp = e.TryGetComponent<Health>(out var h);
                int unitCat = e.TryGetComponent<EntityCategory>(out var ec) ? (int)ec.UnitCategory : -1;
                _cache.Add(new AssetSnapshot
                {
                    Guid = guid,
                    Name = name,
                    IsUnit = true,
                    X = pos.x, Z = pos.z,
                    Region = Core.Castle.GetWorldRegionName(pos) ?? Core.Castle.GetRegionForTerritory(Core.Castle.GetTerritoryIndexAt(pos)),
                    Hp = hasHp ? h.Value : 0f,
                    HpMax = hasHp ? h.MaxHealth._Value : 0f,
                    BloodTypeGuid = btGuid,
                    BloodType = btName,
                    BloodQuality = bq,
                    UnitCategory = unitCat,
                    ResourceTier = -1,
                });
            }
        }
        finally { ents.Dispose(); }
    }

    /// <summary>Categorization audit: for each prefab whose name contains <paramref name="fragment"/>, dump
    /// the raw classification signals straight off the prefab archetype — the game's authoritative
    /// <see cref="EntityCategory"/> (main/unit/resource) plus the component flags Faust keys on — and Faust's
    /// resulting verdict. Lets us confirm units-vs-nodes against the REAL prefab database and surface the
    /// actual subcategory values. Capped; run with a narrow fragment.</summary>
    public List<string> Diagnose(string fragment)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(fragment) || Core.PrefabCollectionSystem is null)
        { lines.Add("Usage: .faust admin worldscandiag <nameFragment>"); return lines; }

        const int cap = 30;
        int matched = 0, shown = 0;
        try
        {
            foreach (var kvp in Core.PrefabCollectionSystem._PrefabGuidToEntityMap)
            {
                var guid = kvp.Key;
                string name = guid.GetPrefabName();
                if (name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
                matched++;
                if (shown >= cap) continue;

                var pe = kvp.Value; // prefab archetype entity
                bool ul = pe.Has<UnitLevel>(), yd = pe.Has<YieldResourcesOnDamageTaken>(),
                     yp = pe.Has<YieldResourcesOnPickup>(), bcs = pe.Has<BloodConsumeSource>(), hp = pe.Has<Health>();
                string cat = pe.TryGetComponent<EntityCategory>(out var ec)
                    ? $"main={(int)ec.MainCategory} unit={(int)ec.UnitCategory} res={ec.ResourceLevel._Value}"
                    : "no-EntityCategory";
                string verdict = (yd || yp) ? "node"
                    : (ul && name.StartsWith("CHAR_", StringComparison.Ordinal)) ? "unit" : "skip";
                bool wl = _whitelist.ContainsKey(guid._Value);
                lines.Add($"{guid._Value} {name} | ul={B(ul)} yDmg={B(yd)} yPick={B(yp)} blood={B(bcs)} hp={B(hp)} | {cat} | {verdict}{(wl ? " [whitelisted]" : "")}");
                shown++;
            }
        }
        catch (Exception ex) { Core.Log.LogError($"[FAUST WORLDSCAN] diag failed: {ex.Message}"); }

        lines.Insert(0, $"'{fragment}': {matched} prefab(s){(matched > cap ? $", showing {cap}" : "")}. " +
                        "Columns: guid name | flags | EntityCategory(main/unit/res) | verdict.");
        return lines;
    }

    static string B(bool b) => b ? "1" : "0";

    void ScanNodes<T>(int cap) where T : unmanaged
    {
        var ents = Query.GetEntitiesByComponentType<T>(includeDisabled: true);
        try
        {
            for (int i = 0; i < ents.Length; i++)
            {
                if (_cache.Count >= cap) { _truncated = true; return; }
                var e = ents[i];
                int guid = e.GetPrefabGuid()._Value;
                if (guid == 0 || !_whitelist.ContainsKey(guid)) continue;
                if (!e.TryGetComponent<LocalToWorld>(out var ltw)) continue;
                var pos = ltw.Position;
                if (Math.Abs(pos.x) > MapLimit || Math.Abs(pos.z) > MapLimit) continue;
                int tier = e.TryGetComponent<EntityCategory>(out var ec) ? ec.ResourceLevel._Value : -1;
                _cache.Add(new AssetSnapshot
                {
                    Guid = guid,
                    Name = new PrefabGUID(guid).GetPrefabName(),
                    IsUnit = false,
                    X = pos.x, Z = pos.z,
                    Region = Core.Castle.GetWorldRegionName(pos) ?? Core.Castle.GetRegionForTerritory(Core.Castle.GetTerritoryIndexAt(pos)),
                    BloodType = "-",
                    BloodQuality = -1,
                    UnitCategory = -1,
                    ResourceTier = tier,
                });
            }
        }
        finally { ents.Dispose(); }
    }
}

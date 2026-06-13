using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Faust.Config;
using Stunlock.Core;

namespace Faust.Services;

/// <summary>
/// Per-player progression gate for the unlock-criteria axis (ADMIN_CONTROL §1 axis 6). Tracks which
/// V Bloods each player has defeated (fed from the death hook) and which features an admin has
/// hand-granted, then evaluates a feature's <c>Unlock</c> criterion. Persists to
/// BepInEx/config/Faust/feature_unlocks.json.
///
/// Auto-detected criteria: <c>BossKill:&lt;guid&gt;</c> and <c>FinalBoss</c> (defeating Dracula).
/// <c>AllBosses</c>/<c>AllQuests</c> are reserved (parsed as GrantOnly) — admin-grant only until a
/// reliable detection lands. <c>None</c> features are always open. Admins skip this (AdminsExempt).
/// </summary>
internal sealed class UnlockService
{
    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, List<int>> Defeated { get; set; } = new(); // steam -> vblood guids
        public Dictionary<string, List<string>> Grants { get; set; } = new(); // steam -> feature keys
    }

    readonly Dictionary<ulong, HashSet<int>> _defeated = new();
    readonly Dictionary<ulong, HashSet<string>> _grants = new();
    bool _tracks;

    static string SaveDir => FaustPaths.DataDir;
    static string SavePath => Path.Combine(SaveDir, "feature_unlocks.json");

    /// <summary>True if ANY feature has an Unlock criterion configured (cheap gate for the death hook).</summary>
    public bool TracksUnlocks => _tracks;

    public void Load()
    {
        // Settings is initialized before this (Plugin.Load order), so the gate is known now.
        _tracks = false;
        foreach (var f in Settings.Features.Values) if (f.HasUnlock) { _tracks = true; break; }

        try
        {
            if (!File.Exists(SavePath)) return;
            var file = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(SavePath));
            if (file is null) return;
            _defeated.Clear();
            foreach (var kvp in file.Defeated ?? new())
                if (ulong.TryParse(kvp.Key, out var id)) _defeated[id] = new HashSet<int>(kvp.Value);
            _grants.Clear();
            foreach (var kvp in file.Grants ?? new())
                if (ulong.TryParse(kvp.Key, out var id)) _grants[id] = new HashSet<string>(kvp.Value);
            Core.Log.LogInfo($"[FAUST UNLOCK] loaded defeats for {_defeated.Count} player(s), grants for {_grants.Count}.");
        }
        catch (Exception ex) { Core.Log.LogError($"[FAUST UNLOCK] failed loading {SavePath}: {ex}"); }
    }

    void SaveSync()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            var file = new SaveFile();
            foreach (var kvp in _defeated) file.Defeated[kvp.Key.ToString()] = new List<int>(kvp.Value);
            foreach (var kvp in _grants) file.Grants[kvp.Key.ToString()] = new List<string>(kvp.Value);
            File.WriteAllText(SavePath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex) { Core.Log.LogError($"[FAUST UNLOCK] failed saving {SavePath}: {ex}"); }
    }

    // ---- death-hook recording ----

    public void RecordBossDefeat(ulong steam, int vbloodGuid)
    {
        if (steam == 0 || vbloodGuid == 0) return;
        if (!_defeated.TryGetValue(steam, out var set)) _defeated[steam] = set = new HashSet<int>();
        if (set.Add(vbloodGuid)) SaveSync();
    }

    public bool HasDefeatedBoss(ulong steam, int vbloodGuid) =>
        _defeated.TryGetValue(steam, out var set) && set.Contains(vbloodGuid);

    /// <summary>Has ANY player on the server defeated this V Blood? (Server-wide "has it ever fallen",
    /// for the boss board's <c>defeated</c> flag — Faust is the authoritative cross-player view.)</summary>
    public bool IsDefeatedAnywhere(int vbloodGuid)
    {
        foreach (var set in _defeated.Values) if (set.Contains(vbloodGuid)) return true;
        return false;
    }

    /// <summary>Union of every V Blood guid any player has defeated (the server-wide defeated roster).</summary>
    public HashSet<int> AllDefeatedGuids()
    {
        var all = new HashSet<int>();
        foreach (var set in _defeated.Values) all.UnionWith(set);
        return all;
    }

    /// <summary>Has the player defeated Dracula? Resolved by prefab name so no GUID is hardcoded.</summary>
    public bool BeatFinalBoss(ulong steam)
    {
        if (!_defeated.TryGetValue(steam, out var set)) return false;
        foreach (var guid in set)
            if (new PrefabGUID(guid).GetPrefabName().IndexOf("Dracula", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    // ---- admin grants ----

    public bool IsGranted(ulong steam, string feature) =>
        _grants.TryGetValue(steam, out var set) && set.Contains(feature);

    public bool Grant(ulong steam, string feature)
    {
        if (!_grants.TryGetValue(steam, out var set)) _grants[steam] = set = new HashSet<string>();
        if (!set.Add(feature)) return false;
        SaveSync();
        return true;
    }

    public bool Revoke(ulong steam, string feature)
    {
        if (!_grants.TryGetValue(steam, out var set) || !set.Remove(feature)) return false;
        SaveSync();
        return true;
    }

    // ---- evaluation ----

    /// <summary>True if the player has met the feature's unlock criterion. <paramref name="need"/>
    /// is a short hint for the deny line (bosskill | finalboss | grant).</summary>
    public bool IsUnlocked(ulong steam, FeatureConfig f, out string need)
    {
        var (kind, guid) = f.Unlock;
        need = null;
        if (kind == UnlockKind.None) return true;
        if (IsGranted(steam, f.Key)) return true;
        switch (kind)
        {
            case UnlockKind.BossKill: need = "bosskill"; return HasDefeatedBoss(steam, guid);
            case UnlockKind.FinalBoss: need = "finalboss"; return BeatFinalBoss(steam);
            default: need = "grant"; return false; // GrantOnly (AllBosses/AllQuests/unknown)
        }
    }

    /// <summary>§8e access: how many players have an explicit admin grant for this feature.</summary>
    public int GrantedCount(string feature)
    {
        int n = 0;
        foreach (var set in _grants.Values) if (set.Contains(feature)) n++;
        return n;
    }

    /// <summary>
    /// §8e access: how many tracked players satisfy this feature's unlock criterion (grant OR the
    /// configured boss/final-boss kill). Evaluated over every player Faust knows a defeat or grant for
    /// (defeated ∪ grants). Returns -1 when the feature has no unlock criterion (everyone qualifies —
    /// "N/A" rather than a misleading count).
    /// </summary>
    public int UnlockedCount(FeatureConfig f)
    {
        if (f.Unlock.Kind == UnlockKind.None) return -1;
        var players = new HashSet<ulong>(_defeated.Keys);
        foreach (var k in _grants.Keys) players.Add(k);
        int n = 0;
        foreach (var steam in players) if (IsUnlocked(steam, f, out _)) n++;
        return n;
    }

    public string Describe(ulong steam)
    {
        int defeats = _defeated.TryGetValue(steam, out var d) ? d.Count : 0;
        var grants = _grants.TryGetValue(steam, out var g) ? string.Join(", ", g) : "(none)";
        return $"V-blood defeats: {defeats}; granted features: {grants}";
    }

    // ---- admin data management ----

    /// <summary>Footprint for the data-status readout: players tracked, total defeats, total grants.</summary>
    public (int players, int defeats, int grants) GetUnlockStats()
    {
        int defeats = 0; foreach (var s in _defeated.Values) defeats += s.Count;
        int grants = 0; foreach (var s in _grants.Values) grants += s.Count;
        var players = new HashSet<ulong>(_defeated.Keys);
        foreach (var k in _grants.Keys) players.Add(k);
        return (players.Count, defeats, grants);
    }

    /// <summary>Erase ALL unlock progress (every player's V-blood defeats AND admin grants) — the
    /// world-wipe reset for progression-gated features. Returns the number of players cleared.</summary>
    public int WipeAll()
    {
        int players = GetUnlockStats().players;
        _defeated.Clear();
        _grants.Clear();
        SaveSync();
        return players;
    }
}

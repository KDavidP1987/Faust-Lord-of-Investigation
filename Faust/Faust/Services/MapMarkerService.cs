using System;
using System.Collections.Generic;
using Faust.Config;
using ProjectM;          // AttachMapIconsToEntity (ProjectM.Shared.dll, namespace ProjectM)
using ProjectM.Network;
using Stunlock.Core;
using Unity.Entities;

namespace Faust.Services;

/// <summary>
/// EXPERIMENTAL (Raphael §5) — server-driven native-map player markers. The server owns the
/// authoritative <c>MapIcon</c> entities and replicates them to clients normally, so attaching a
/// marker to each online player's character makes them appear on the in-game map with zero client
/// code. This is the SAFE home for the feature (vs. faking client-side icons, which corrupts the
/// client network-snapshot state).
///
/// Implemented via the game's own attach mechanism — the <c>AttachMapIconsToEntity</c> buffer
/// (ProjectM.Shared), which <c>InstantiateMapIconsSystem</c> turns into a real networked icon — so the
/// server, not us, builds the network state. It remains gated behind <c>Faust.MapMarkers.Enabled</c>
/// (default OFF) and UNPROVEN until validated in-game: the open questions are spawn behaviour for
/// far/culled players and, crucially, making the marker **admin-only** (the default MapIcon_Player is
/// ally-visible; strict admin-only needs a purpose-built marker prefab or a post-spawn MapIconData edit).
/// Validate on a TEST server before production use.
///
/// Lifecycle: on → attach a marker to every online player (and to anyone who connects while active);
/// off / duration-expiry / plugin-unload → detach. Markers are attach-based, so they follow the
/// player automatically — no per-tick position push needed.
/// </summary>
internal sealed class MapMarkerService
{
    readonly HashSet<ulong> _marked = new();
    bool _active;
    long _expiresAt; // unix seconds; 0 = no expiry

    public bool Active => _active;
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Turn markers on (optionally for a duration). Returns an admin-facing status string.</summary>
    public string Enable(int minutes)
    {
        if (!Settings.MapMarkersEnabled.Value)
            return "Map markers are EXPERIMENTAL and disabled. Set [Faust.MapMarkers] Enabled=true in the " +
                   "config (a TEST server is strongly recommended — this can crash a live server) and restart.";

        _active = true;
        _expiresAt = minutes > 0 ? Now + minutes * 60L : 0;
        int online = 0, marked = 0;
        foreach (var steam in OnlineSteamIds()) { online++; if (TryAttach(steam)) marked++; }
        string dur = minutes > 0 ? $", auto-off in {minutes} min" : "";
        return $"Player map markers ON{dur}. Attached {marked}/{online} online player(s). " +
               "EXPERIMENTAL — open the map to verify, watch the console, and confirm visibility is admin-only " +
               "before relying on it (default marker is ally-visible — see [Faust.MapMarkers] MarkerPrefabGuid).";
    }

    public string Disable()
    {
        int n = _marked.Count;
        foreach (var steam in new List<ulong>(_marked)) TryDetach(steam);
        _marked.Clear();
        _active = false; _expiresAt = 0;
        return $"Player map markers OFF; cleared {n} marker(s).";
    }

    public string Describe() =>
        !Settings.MapMarkersEnabled.Value ? "disabled (set [Faust.MapMarkers] Enabled=true to use)"
        : _active ? $"ON, {_marked.Count} marked{(_expiresAt > 0 ? $", expires in {Math.Max(0, _expiresAt - Now) / 60}m" : "")}"
        : "off";

    /// <summary>Cheap expiry check — called from the connectivity hooks (no dedicated tick loop).</summary>
    public void TickExpiry() { if (_active && _expiresAt > 0 && Now >= _expiresAt) Disable(); }

    public void OnPlayerConnect(ulong steam) { TickExpiry(); if (_active) TryAttach(steam); }
    public void OnPlayerDisconnect(ulong steam) { _marked.Remove(steam); }

    /// <summary>Detach everything (clean plugin unload).</summary>
    public void Shutdown() { if (_active || _marked.Count > 0) Disable(); }

    // ---- internals ----

    static IEnumerable<ulong> OnlineSteamIds()
    {
        foreach (var userEntity in Query.GetUsersOnline())
            if (userEntity.TryGetComponent<User>(out var u) && u.PlatformId != 0)
                yield return u.PlatformId;
    }

    static bool TryResolveCharacter(ulong steam, out Entity character)
    {
        character = Entity.Null;
        foreach (var userEntity in Query.GetUsersOnline())
        {
            if (!userEntity.TryGetComponent<User>(out var u) || u.PlatformId != steam) continue;
            var c = u.LocalCharacter.GetEntityOnServer();
            if (c.Exists()) { character = c; return true; }
            return false;
        }
        return false;
    }

    // Attach uses the GAME-INTENDED mechanism: add the marker prefab to the player character's
    // AttachMapIconsToEntity buffer (ProjectM.Shared) and let ProjectM's InstantiateMapIconsSystem spawn
    // the networked, replicated icon. We never hand-build NetworkSnapshot state, so the client-side-faking
    // crash risk does NOT apply here. STILL EXPERIMENTAL: spawn behaviour for far/culled players and, above
    // all, ADMIN-ONLY VISIBILITY (the spawned icon inherits the marker prefab's MapIconData visibility —
    // MapIcon_Player is ally-visible, so strict admin-only needs a purpose-built marker prefab or a
    // post-spawn MapIconData tweak) must be validated/tuned on a live server before production use.

    bool TryAttach(ulong steam)
    {
        if (_marked.Contains(steam)) return false;
        try
        {
            if (!TryResolveCharacter(steam, out var character)) return false;
            var prefab = new PrefabGUID(Settings.MapMarkerPrefabGuid.Value);
            // Refuse a GUID the server doesn't know — a bogus prefab could make the spawn system choke.
            if (!Core.PrefabCollectionSystem._PrefabGuidToEntityMap.TryGetValue(prefab, out _))
            {
                Core.Log.LogWarning($"[FAUST MAP] marker prefab {prefab.GuidHash} not found in the prefab map; skipping {steam}.");
                return false;
            }

            var buffer = Core.EntityManager.HasBuffer<AttachMapIconsToEntity>(character)
                ? Core.EntityManager.GetBuffer<AttachMapIconsToEntity>(character)
                : Core.EntityManager.AddBuffer<AttachMapIconsToEntity>(character);
            for (int i = 0; i < buffer.Length; i++)
                if (buffer[i].Prefab.Equals(prefab)) { _marked.Add(steam); return false; } // already attached
            buffer.Add(new AttachMapIconsToEntity { Prefab = prefab });

            _marked.Add(steam);
            if (Settings.VerboseLogging.Value) Core.Log.LogInfo($"[FAUST MAP] attached marker to {steam}.");
            return true;
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[FAUST MAP] attach failed for {steam} (experimental): {ex.Message}");
            return false;
        }
    }

    void TryDetach(ulong steam)
    {
        _marked.Remove(steam);
        try
        {
            if (!TryResolveCharacter(steam, out var character)) return;
            if (!Core.EntityManager.HasBuffer<AttachMapIconsToEntity>(character)) return;
            var prefab = new PrefabGUID(Settings.MapMarkerPrefabGuid.Value);
            var buffer = Core.EntityManager.GetBuffer<AttachMapIconsToEntity>(character);
            for (int i = buffer.Length - 1; i >= 0; i--)
                if (buffer[i].Prefab.Equals(prefab)) buffer.RemoveAt(i);
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[FAUST MAP] detach failed for {steam} (experimental): {ex.Message}");
        }
    }
}

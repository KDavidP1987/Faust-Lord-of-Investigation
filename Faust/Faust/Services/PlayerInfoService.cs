using System.Collections.Generic;
using ProjectM.Network;
using Unity.Entities;
using Unity.Transforms;

namespace Faust.Services;

/// <summary>
/// Player intelligence — feature #3 (player info) and #1 (online positions). Last-online and
/// connection state come straight off the server's User entities (the game only persists the LAST
/// connect time — <see cref="PlayerSnapshot.LastConnected"/>). The time-series fields (sessions,
/// total playtime, login frequency, peak hour, first-seen) require Faust's own persistence and are
/// emitted as -1 ("not yet tracked") until the FaustStore subsystem lands (design §6).
/// </summary>
internal sealed class PlayerInfoService
{
    public readonly struct PlayerSnapshot
    {
        public ulong SteamId { get; init; }
        public string Name { get; init; }
        public bool Online { get; init; }
        public long LastConnected { get; init; } // DateTime binary
        // ---- time-series, derived from FaustStore session log (-1 = none recorded yet) ----
        public long FirstSeenUnix { get; init; }
        public int Sessions { get; init; }
        public long PlayMinutes { get; init; }
        public int PeakHour { get; init; }
        public double FreqPerWeek { get; init; }
    }

    public readonly struct PlayerPosition
    {
        public ulong SteamId { get; init; }
        public string Name { get; init; }
        public float X { get; init; }
        public float Z { get; init; }
        public int TerritoryIndex { get; init; } // -1 if in open world
    }

    public bool TryGetPlayer(ulong steamId, out PlayerSnapshot snapshot)
    {
        snapshot = default;
        var users = Query.GetEntitiesByComponentType<User>(includeDisabled: true);
        try
        {
            for (int i = 0; i < users.Length; i++)
            {
                if (!users[i].TryGetComponent<User>(out var u) || u.PlatformId != steamId) continue;
                snapshot = Build(u);
                return true;
            }
        }
        finally { users.Dispose(); }
        return false;
    }

    static PlayerSnapshot Build(User u)
    {
        var m = Core.Store.GetMetrics(u.PlatformId);
        return new PlayerSnapshot
        {
            SteamId = u.PlatformId,
            Name = u.CharacterName.ToString(),
            Online = u.IsConnected,
            LastConnected = u.TimeLastConnected,
            FirstSeenUnix = m.FirstSeenUnix,
            Sessions = m.SessionCount,
            PlayMinutes = m.PlayMinutes,
            PeakHour = m.PeakHour,
            FreqPerWeek = m.FreqPerWeek,
        };
    }

    /// <summary>Positions of all online players — feature #1 (admin-default; rendering is BCH-side).</summary>
    public List<PlayerPosition> GetOnlinePositions()
    {
        var result = new List<PlayerPosition>();
        foreach (var userEntity in Query.GetUsersOnline())
        {
            var u = userEntity.Read<User>();
            var charEntity = u.LocalCharacter.GetEntityOnServer();
            if (!charEntity.TryGetComponent<LocalToWorld>(out var ltw)) continue;
            var pos = ltw.Position;
            result.Add(new PlayerPosition
            {
                SteamId = u.PlatformId,
                Name = u.CharacterName.ToString(),
                X = pos.x,
                Z = pos.z,
                TerritoryIndex = Core.Castle.GetTerritoryIndexAt(pos),
            });
        }
        return result;
    }
}

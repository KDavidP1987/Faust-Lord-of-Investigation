using System.Collections.Generic;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using Unity.Entities;

namespace Faust.Services;

/// <summary>
/// Holistic clan composition — how the server population splits between clans and solo players, plus a
/// per-clan roster (size / online / leader). All read live off ECS (ClanTeam entities + the per-user
/// ClanEntity link); nothing persisted. The "clanned vs independent" intel admins asked for, and a
/// natural PvP-sensitive feature (defaults to AdminOnly).
///
/// Mirrors the KindredCommands clan model: clans are <see cref="ClanTeam"/> entities carrying a
/// <see cref="ClanMemberStatus"/> buffer (roles) paired with a <see cref="SyncToUserBuffer"/> (the
/// member User entities); a user is independent when <c>User.ClanEntity == NetworkedEntity.Empty</c>.
/// </summary>
internal sealed class ClanService
{
    public readonly struct ClanInfo
    {
        public string Name { get; init; }
        public int Members { get; init; }
        public int Online { get; init; }
        public int Castles { get; init; }   // territories the clan's team owns
        public string Leader { get; init; }
    }

    public readonly struct Composition
    {
        public int Clans { get; init; }              // non-empty clans
        public int Clanned { get; init; }            // players in a clan (all known users)
        public int Independent { get; init; }        // players with no clan
        public int OnlineClanned { get; init; }
        public int OnlineIndependent { get; init; }
        public int Largest { get; init; }            // biggest clan's member count
        public double AvgSize { get; init; }         // mean members per clan (0 if no clans)
        public List<ClanInfo> ClanList { get; init; } // members-descending
    }

    public readonly struct ClanMember
    {
        public string Name { get; init; }
        public bool Online { get; init; }
        public bool Leader { get; init; }
    }

    /// <summary>
    /// §8c: the member roster of a single clan, matched by name (case-insensitive, against both the raw
    /// <see cref="ClanTeam.Name"/> and its <see cref="Wire.Safe"/> form so a `_`-encoded name from Raphael
    /// resolves). Returns null when no clan matches; an empty list only for a matched-but-empty clan.
    /// Leader-first, then online, then name.
    /// </summary>
    public List<ClanMember> GetClanMembers(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        List<ClanMember> found = null;
        var clans = Query.GetEntitiesByComponentType<ClanTeam>(includeDisabled: true);
        try
        {
            for (int i = 0; i < clans.Length; i++)
            {
                var clan = clans[i];
                if (!Core.EntityManager.HasComponent<ClanMemberStatus>(clan)) continue;
                var clanName = clan.Read<ClanTeam>().Name.ToString();
                if (!string.Equals(clanName, name, System.StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Wire.Safe(clanName), name, System.StringComparison.OrdinalIgnoreCase))
                    continue;

                var members = Core.EntityManager.GetBuffer<ClanMemberStatus>(clan);
                var userBuf = Core.EntityManager.HasComponent<SyncToUserBuffer>(clan)
                    ? Core.EntityManager.GetBuffer<SyncToUserBuffer>(clan) : default;

                var list = new List<ClanMember>(members.Length);
                for (int m = 0; m < members.Length; m++)
                {
                    bool leader = members[m].ClanRole == ClanRoleEnum.Leader;
                    string mName = null; bool online = false;
                    if (userBuf.IsCreated && m < userBuf.Length
                        && userBuf[m].UserEntity.TryGetComponent<User>(out var mu))
                    {
                        mName = mu.CharacterName.ToString();
                        online = mu.IsConnected;
                    }
                    list.Add(new ClanMember { Name = mName, Online = online, Leader = leader });
                }
                list.Sort((a, b) =>
                {
                    if (a.Leader != b.Leader) return a.Leader ? -1 : 1;
                    if (a.Online != b.Online) return a.Online ? -1 : 1;
                    return string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase);
                });
                found = list;
                break;
            }
        }
        finally { clans.Dispose(); }
        return found;
    }

    /// <summary>
    /// §9a: steam-id → owning clan name, over every clan's member buffer (offline members included, since the
    /// ClanTeam entities and their <see cref="SyncToUserBuffer"/> persist for offline users). Used to tag the
    /// new-players roster with each player's current clan; absent from the map ⇒ solo / no clan.
    /// </summary>
    public Dictionary<ulong, string> GetPlayerClanNames()
    {
        var map = new Dictionary<ulong, string>();
        var clans = Query.GetEntitiesByComponentType<ClanTeam>(includeDisabled: true);
        try
        {
            for (int i = 0; i < clans.Length; i++)
            {
                var clan = clans[i];
                if (!Core.EntityManager.HasComponent<ClanMemberStatus>(clan)
                    || !Core.EntityManager.HasComponent<SyncToUserBuffer>(clan)) continue;
                string name = clan.Read<ClanTeam>().Name.ToString();
                var userBuf = Core.EntityManager.GetBuffer<SyncToUserBuffer>(clan);
                for (int m = 0; m < userBuf.Length; m++)
                    if (userBuf[m].UserEntity.TryGetComponent<User>(out var mu))
                        map[mu.PlatformId] = name;
            }
        }
        finally { clans.Dispose(); }
        return map;
    }

    public Composition GetComposition()
    {
        // ---- per-player split (authoritative count over every known user, online + offline) ----
        int clanned = 0, independent = 0, onlineClanned = 0, onlineIndependent = 0;
        var users = Query.GetEntitiesByComponentType<User>(includeDisabled: true);
        try
        {
            for (int i = 0; i < users.Length; i++)
            {
                if (!users[i].TryGetComponent<User>(out var u)) continue;
                bool solo = u.ClanEntity.Equals(NetworkedEntity.Empty);
                if (solo) { independent++; if (u.IsConnected) onlineIndependent++; }
                else { clanned++; if (u.IsConnected) onlineClanned++; }
            }
        }
        finally { users.Dispose(); }

        // ---- castle count per clan team (one pass over hearts: teamValue -> count) ----
        var castlesByTeam = new Dictionary<int, int>();
        var hearts = Query.GetEntitiesByComponentType<CastleHeart>(includeDisabled: true);
        try
        {
            for (int i = 0; i < hearts.Length; i++)
            {
                if (!hearts[i].TryGetComponent<Team>(out var team)) continue;
                castlesByTeam.TryGetValue(team.Value, out var cnt);
                castlesByTeam[team.Value] = cnt + 1;
            }
        }
        finally { hearts.Dispose(); }

        // ---- per-clan roster (size / online / castles / leader) ----
        var list = new List<ClanInfo>();
        int largest = 0;
        var clans = Query.GetEntitiesByComponentType<ClanTeam>(includeDisabled: true);
        try
        {
            for (int i = 0; i < clans.Length; i++)
            {
                var clan = clans[i];
                if (!Core.EntityManager.HasComponent<ClanMemberStatus>(clan)) continue;
                var members = Core.EntityManager.GetBuffer<ClanMemberStatus>(clan);
                if (members.Length == 0) continue; // disbanded/empty clan — skip

                var userBuf = Core.EntityManager.HasComponent<SyncToUserBuffer>(clan)
                    ? Core.EntityManager.GetBuffer<SyncToUserBuffer>(clan) : default;

                int online = 0; string leader = "";
                for (int m = 0; m < members.Length; m++)
                {
                    if (userBuf.IsCreated && m < userBuf.Length)
                    {
                        var ue = userBuf[m].UserEntity;
                        if (ue.TryGetComponent<User>(out var mu))
                        {
                            if (mu.IsConnected) online++;
                            if (members[m].ClanRole == ClanRoleEnum.Leader) leader = mu.CharacterName.ToString();
                        }
                    }
                }

                int castles = castlesByTeam.TryGetValue(clan.Read<ClanTeam>().TeamValue, out var cc) ? cc : 0;
                list.Add(new ClanInfo
                {
                    Name = clan.Read<ClanTeam>().Name.ToString(),
                    Members = members.Length,
                    Online = online,
                    Castles = castles,
                    Leader = leader,
                });
                if (members.Length > largest) largest = members.Length;
            }
        }
        finally { clans.Dispose(); }

        list.Sort((a, b) => b.Members.CompareTo(a.Members));

        return new Composition
        {
            Clans = list.Count,
            Clanned = clanned,
            Independent = independent,
            OnlineClanned = onlineClanned,
            OnlineIndependent = onlineIndependent,
            Largest = largest,
            AvgSize = list.Count == 0 ? 0 : (double)clanned / list.Count,
            ClanList = list,
        };
    }
}

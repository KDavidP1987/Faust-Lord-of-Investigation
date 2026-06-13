using System.Collections.Generic;
using System.Globalization;
using Faust.Config;
using ProjectM;
using Stunlock.Core;
using Unity.Entities;
using Unity.Transforms;

namespace Faust.Services;

/// <summary>
/// V Blood boss intelligence (the boss-status board). Faust is the authoritative global view, so it can
/// answer "which bosses are alive right now, where, and at what health" — intel a spatially-culled client
/// can't see. A boss is a live world entity ONLY while spawned; when killed it despawns and respawns on a
/// timer (no entity in between). So this reports two layers, honestly:
///   • <b>up</b> — a live <see cref="VBloodUnit"/> + <see cref="VBloodConsumeSource"/> entity in the world
///     (real consumable boss, not a player shapeshift / bound familiar, which lack the consume source):
///     position, region, current/max health, level.
///   • <b>down</b> — a boss NOT placed in the world right now (no coords): either a pooled/staged VBlood
///     entity parked off-map at the spawn sentinel (it exists but isn't on the map), or a boss some player
///     has defeated (from the unlock store, Faust's own cross-player record). <c>defeated</c> reflects the
///     latter. (A boss reads "down" the moment it leaves the playable map — see <c>MapLimit</c>.)
/// A never-defeated, not-currently-spawned boss has no data source server-side yet (would need the prefab
/// spawn roster — a follow-up), so it simply isn't listed. On-demand: zero passive cost.
/// </summary>
internal sealed class BossService
{
    public readonly struct BossSnapshot
    {
        public int Guid { get; init; }
        public string Name { get; init; }
        public bool Alive { get; init; }     // a live world entity exists right now ("up")
        public bool Defeated { get; init; }  // any player has ever killed it (server-wide)
        public float X { get; init; }        // valid when Alive
        public float Z { get; init; }        // valid when Alive
        public string Region { get; init; }  // when Alive (else null)
        public float Hp { get; init; }       // current health, when Alive
        public float HpMax { get; init; }    // when Alive
        public int Level { get; init; }      // unit level, when Alive
    }

    /// <summary>All bosses Faust can currently see: live ones (with position/health) plus defeated bosses
    /// that aren't currently spawned. Live first, then by name.</summary>
    // The placed-vs-pooled distance threshold — live-tunable via [Faust.Bosses] MapLimit. The Vardoran
    // playable map sits well within ±this; pooled/parked boss entities that aren't on the map park at a far
    // off-map sentinel (observed ~10000,10000), so a position beyond this means "not really on the map"
    // (Raphael §16/§18). Raise it if outer-region bosses read 'down', but keep it below ~10000.
    static float MapLimit => Settings.BossMapLimit.Value;

    public List<BossSnapshot> GetBosses()
    {
        var byGuid = new Dictionary<int, BossSnapshot>();

        // 1) Live world bosses.
        var ents = Query.GetEntitiesByComponentType<VBloodUnit>(includeDisabled: true);
        try
        {
            for (int i = 0; i < ents.Length; i++)
            {
                var e = ents[i];
                // Real world boss only: the consume source excludes player VBlood-form shapeshifts and
                // bound boss familiars (which carry VBloodUnit but are not a consumable source).
                if (!e.Has<VBloodConsumeSource>()) continue;
                if (!e.TryGetComponent<Health>(out var hp) || hp.Value <= 0) continue; // alive only
                if (!e.TryGetComponent<LocalToWorld>(out var ltw)) continue;

                int guid = e.GetPrefabGuid()._Value;
                if (guid == 0) continue;
                var pos = ltw.Position;

                // A pooled/staged boss exists as an entity but isn't placed on the map (it parks at the
                // off-map sentinel). Report it as NOT placed (status=down, no coords) rather than emitting
                // the bogus limbo coordinates — a genuinely-placed instance is the one with a real position.
                bool placed = System.Math.Abs(pos.x) <= MapLimit && System.Math.Abs(pos.z) <= MapLimit;

                BossSnapshot snap;
                if (placed)
                {
                    int tindex = Core.Castle.GetTerritoryIndexAt(pos);
                    string region = Core.Castle.GetWorldRegionName(pos) ?? Core.Castle.GetRegionForTerritory(tindex);
                    snap = new BossSnapshot
                    {
                        Guid = guid,
                        Name = new PrefabGUID(guid).GetPrefabName(),
                        Alive = true,
                        Defeated = Core.Unlock.IsDefeatedAnywhere(guid),
                        X = pos.x,
                        Z = pos.z,
                        Region = region,
                        Hp = hp.Value,
                        HpMax = hp.MaxHealth._Value,
                        Level = e.TryGetComponent<UnitLevel>(out var ul) ? ul.Level._Value : 0,
                    };
                }
                else
                {
                    snap = new BossSnapshot
                    {
                        Guid = guid,
                        Name = new PrefabGUID(guid).GetPrefabName(),
                        Alive = false, // pooled/staged — present as a not-on-the-map (down) boss, no coords
                        Defeated = Core.Unlock.IsDefeatedAnywhere(guid),
                    };
                }

                // A genuinely-placed (up) instance wins over a pooled (down) one for the same boss guid.
                if (!byGuid.TryGetValue(guid, out var existing) || (!existing.Alive && snap.Alive))
                    byGuid[guid] = snap;
            }
        }
        finally { ents.Dispose(); }

        // 2) Defeated-but-not-currently-spawned bosses (from Faust's own cross-player defeat record).
        foreach (var guid in Core.Unlock.AllDefeatedGuids())
        {
            if (byGuid.ContainsKey(guid)) continue; // already listed as live
            byGuid[guid] = new BossSnapshot
            {
                Guid = guid,
                Name = new PrefabGUID(guid).GetPrefabName(),
                Alive = false,
                Defeated = true,
            };
        }

        var list = new List<BossSnapshot>(byGuid.Values);
        list.Sort((a, b) =>
        {
            if (a.Alive != b.Alive) return a.Alive ? -1 : 1;       // live first
            return string.CompareOrdinal(a.Name, b.Name);          // then by name
        });
        return list;
    }

    /// <summary>One boss by name fragment or prefab GUID. Returns false if nothing matches.</summary>
    public bool TryGetBoss(string nameOrGuid, out BossSnapshot snapshot)
    {
        snapshot = default;
        var all = GetBosses();
        bool wantGuid = int.TryParse(nameOrGuid, out int guid);

        BossSnapshot? exact = null, partial = null;
        int partialCount = 0;
        foreach (var b in all)
        {
            if (wantGuid && b.Guid == guid) { snapshot = b; return true; }
            if (b.Name is null) continue;
            if (string.Equals(b.Name, nameOrGuid, System.StringComparison.OrdinalIgnoreCase)) exact = b;
            else if (b.Name.Contains(nameOrGuid, System.StringComparison.OrdinalIgnoreCase)) { partial = b; partialCount++; }
        }
        if (exact.HasValue) { snapshot = exact.Value; return true; }
        if (partial.HasValue && partialCount == 1) { snapshot = partial.Value; return true; }
        return false;
    }

    /// <summary>Raw diagnostics for the boss board (Raphael §18 — "many bosses report down"). Scans every
    /// VBlood entity and reports the component/position picture so we can see, on a LIVE server, WHY a boss
    /// is classified down: is it pooled at the off-map sentinel, is MapLimit mis-tuned vs the real spawn
    /// coords, or is the placed instance excluded by the VBloodConsumeSource filter? Returns chat-ready
    /// lines: a summary, then (when a name/guid filter is given) one detail row per matching entity.</summary>
    public List<string> Diagnose(string filter)
    {
        bool wantGuid = int.TryParse(filter, out int fGuid);
        bool hasFilter = !string.IsNullOrWhiteSpace(filter);
        int total = 0, withCs = 0, alive = 0, placed = 0, limbo = 0, noLtw = 0, disabled = 0;
        var rows = new List<string>();

        var ents = Query.GetEntitiesByComponentType<VBloodUnit>(includeDisabled: true);
        try
        {
            for (int i = 0; i < ents.Length; i++)
            {
                var e = ents[i];
                total++;
                int guid = e.GetPrefabGuid()._Value;
                string name = new PrefabGUID(guid).GetPrefabName();

                bool hasCs = e.Has<VBloodConsumeSource>();
                if (hasCs) withCs++;
                bool hasHp = e.TryGetComponent<Health>(out var h);
                if (hasHp && h.Value > 0) alive++;
                bool hasLtw = e.TryGetComponent<LocalToWorld>(out var ltw);
                bool isDisabled = e.Has<Disabled>();
                if (isDisabled) disabled++;

                float x = hasLtw ? ltw.Position.x : 0f, z = hasLtw ? ltw.Position.z : 0f;
                if (!hasLtw) noLtw++;
                else if (System.Math.Abs(x) <= MapLimit && System.Math.Abs(z) <= MapLimit) placed++;
                else limbo++;

                bool match = hasFilter && (wantGuid ? guid == fGuid
                    : name.Contains(filter, System.StringComparison.OrdinalIgnoreCase));
                if (match)
                    rows.Add($"{name} guid={guid} pos=({F(x)},{F(z)}) cs={B(hasCs)} " +
                             $"hp={(hasHp ? F(h.Value) : "-")}/{(hasHp ? F(h.MaxHealth._Value) : "-")} " +
                             $"ltw={B(hasLtw)} disabled={B(isDisabled)}");
            }
        }
        finally { ents.Dispose(); }

        var lines = new List<string>
        {
            $"VBlood entities: {total} total · {withCs} consume-source · {alive} alive(hp>0) · " +
            $"{placed} placed(<=±{(int)MapLimit}) · {limbo} limbo(off-map) · {noLtw} no-position · {disabled} disabled.",
        };
        if (hasFilter)
        {
            lines.Add($"Matches for '{filter}': {rows.Count}");
            lines.AddRange(rows);
        }
        else
        {
            lines.Add("Add a name fragment or guid to dump matching entities, e.g. '.faust admin bossdiag Wolf'.");
        }
        return lines;
    }

    static string F(float v) => v.ToString("0", CultureInfo.InvariantCulture);
    static string B(bool b) => b ? "1" : "0";
}

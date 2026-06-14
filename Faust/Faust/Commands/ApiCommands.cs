using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Faust.Config;
using Faust.Services;
using ProjectM;
using Unity.Transforms;
using VampireCommandFramework;

namespace Faust.Commands;

/// <summary>
/// Structured chat output for BloodCraftHub (BCH) — the [FAUST:*] wire API
/// (docs/BCH_INTEGRATION_CONTRACT.md, mirroring Uriel's [URIEL:*] / Beelzebub's [BEELZ:*]).
///
/// Wire rule: ONE ctx.Reply per [FAUST:*] line — BCH reads each System-chat message as a single
/// wire line and does NOT split on '\n'. Paged lists go through <see cref="Wire.SendPage"/>.
///
/// Every query runs through <see cref="FaustAccessGate"/> first; a denied call emits only its
/// [FAUST:err] line. The reserved cost/cooldown is committed via gate.Commit ONLY after a real
/// result, so an empty/notfound lookup is never charged.
///
/// ApiVersion 12: the investigation queries — castleinfo (#2), plots (#4), pinfo (#3), positions
/// (#1, with region=), resources (#6), the full-map castles list (allcastles), the decay-watch list
/// (decaywatch), clan composition (clans: clanned vs independent + rosters) — server stats (#8:
/// playtime + concurrency, the activity analytics hours/daily/newplayers/sessions/weekdays/pdaily,
/// the population/recency/peak/regions rollups, and the ApiVersion 12 player-activity roster
/// `stats players`) — and the full admin-control gate (block/schedule/PvP/window-period/cost + unlock
/// criteria + proximity-to-object + a per-player rate limit). Deny codes: blocked, schedule, pvp,
/// window, locked, notnear, ratelimit.
///
/// ApiVersion 13 (§8 tester batch): castleinfo grows optional heartlevel/floors/claimed/clan/items;
/// resources reports prisoners (header count + [FAUST:prisoner] rows); a new `clanmembers` endpoint
/// (under the clans gate); `daily` grows new=/returning=; and two admin-oversight endpoints `access`
/// and `usage` (under the stats gate) report per-feature access + usage. Bump whenever the wire grows.
///
/// ApiVersion 14 (§9 drill-down batch): per-player/per-event detail behind the charts, all under the
/// stats gate — `newplayers roster` ([FAUST:nprow]: who joined + when + clan), a `hoursplayers` sibling
/// line on `stats hours` ([FAUST:hoursplayers]: distinct players per UTC hour, the avg-per-player
/// denominator), `sessions timeline` ([FAUST:stl]: individual online intervals), and `stats activegrid`
/// ([FAUST:agrow]: per-player active-days grid). All additive; older BCH simply won't query them.
///
/// ApiVersion 15 (§10 region/roster batch): `[FAUST:nprow]` grows `playmins=`/`castles=` (§10a);
/// `[FAUST:region]` grows `plots=` (total buildable territories → Raphael's castle fill %, §10b); and a
/// new `stats regiondaily` endpoint ([FAUST:rdrow]: per-day per-region castles/plots/players, §10c) — a
/// forward-accumulating series (Faust keeps no historical castle data; it samples once per UTC day).
///
/// ApiVersion 16 (player-position heat map): a new `heatmap` feature (advertised in the handshake) — a
/// `.faust api heatmap [<all|name|steamId>]` endpoint returning a binned density grid ([FAUST:hmhead]
/// header + packed [FAUST:hmrow] cells) for per-player and server-wide heat maps. Collection is opt-in
/// (`[Faust.Heatmap] Enabled`), sampled on a timer; the read is gated like the other features.
///
/// ApiVersion 17 (§11 world coords): every `[FAUST:castle]`/`[FAUST:plot]` row gains optional
/// `posx=`/`posz=` (the territory's centroid world coords — where on the map it is, §11a), and the
/// `[FAUST:hmhead]` header gains optional `mapbounds=` (the full buildable-map cell extent, so a sparse
/// heat map can be drawn at true map scale, §11b). Both additive/omittable.
///
/// ApiVersion 18 (0.16.0 — admin gates + bosses + kills): `[FAUST:access]` grows the non-cost gate
/// tokens `cd=`/`window=`/`period=`/`maxuses=`/`nearprefab=`/`neardist=` so Raphael can DISPLAY the full
/// per-feature gate picture (Raphael §15a); `objectscan` is RETIRED from the handshake + access list
/// (its client scan was removed — Raphael §14). Two new features, each its own handshake token + gate:
/// **`bosses`** (`.faust api bosses [page]` / `boss <name>` — VBlood status: position/region/health +
/// up/down/defeated) and the **`kills`** leaderboards (`.faust api kills [page]` / `bosskills [days]` —
/// from a new death-hook tally; opt-in collector `[Faust.Collection] KillTracking`). Also new:
/// **`worldscan`** (`.faust api worldscan [type=…,id=…,bloodtype=…,bloodqmin=…] [page]`) — a filtered map of
/// NPC units (with blood type/quality) + resource nodes (ores/trees/plants), from an admin-curated prefab
/// whitelist (`.faust admin worldscan …`), cached + rate-limited via `[Faust.WorldScan] ScanIntervalSeconds`.
///
/// ApiVersion 19 (heatmap time windows): `.faust api heatmap` gains an optional `days` arg
/// (`.faust api heatmap [scope] [days] [page]`, days 0 = all-time, N = last N UTC days — today/week/month),
/// and `[FAUST:hmhead]` grows `days=`/`retentiondays=`. Backed by a retention-pruned per-day layer
/// (`[Faust.Heatmap] RetentionDays`, default 30); the all-time map (days=0) is unchanged and never pruned.
/// Additive — an older Raphael omits `days` and still reads the all-time map exactly as before.
/// </summary>
[CommandGroup("faust api")]
internal static class ApiCommands
{
    const int ApiVersion = 19;

    static readonly string[] FeatureOrder =
    {
        Settings.PlayerPositions, Settings.CastleInfo, Settings.PlayerInfo,
        Settings.PlotAvailability, Settings.CastleResources, Settings.Stats,
        Settings.AllCastles, Settings.DecayWatch, Settings.Clans, Settings.Heatmap,
        Settings.Bosses, Settings.Kills, Settings.WorldScan,
    };

    // ---- Foundation: handshake + probe ----

    [Command("version", description: "BCH handshake: ApiVersion, ready flag, and each feature's resolved access + cost.")]
    public static void Version(ChatCommandContext ctx)
    {
        bool ready = Core.IsReady && Settings.Enabled.Value;
        var isAdmin = ctx.Event.User.IsAdmin;

        var sb = new StringBuilder();
        sb.Append("[FAUST:version] ")
          .Append($"api={ApiVersion} ")
          .Append($"plugin={MyPluginInfo.PLUGIN_VERSION} ")
          .Append($"ready={(ready ? 1 : 0)}");

        foreach (var key in FeatureOrder)
        {
            var f = Settings.Feature(key);
            if (f is null) continue;
            sb.Append(' ').Append(key).Append('=')
              .Append(FaustAccessGate.AccessToken(f, isAdmin)).Append(':')
              .Append(FaustAccessGate.CostToken(f));
        }

        ctx.Reply(sb.ToString());
    }

    [Command("ping", description: "BCH probe: proves the Faust->BCH round-trip end to end.")]
    public static void Ping(ChatCommandContext ctx)
    {
        if (!Core.IsReady) { ctx.Reply("[FAUST:err] code=notready"); return; }
        ctx.Reply($"[FAUST:pong] api={ApiVersion} plugin={MyPluginInfo.PLUGIN_VERSION}");
    }

    // ---- #2 Castle / plot info ----

    [Command("castleinfo", description: "BCH: castle/plot info. Usage: .faust api castleinfo <here|nearest|tindex>")]
    public static void CastleInfo(ChatCommandContext ctx, string token = "here")
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.CastleInfo);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        int tindex;
        if (string.Equals(token, "here", StringComparison.OrdinalIgnoreCase))
            tindex = Core.Castle.GetTerritoryIndexAt(SenderPos(ctx));
        else if (string.Equals(token, "nearest", StringComparison.OrdinalIgnoreCase))
            tindex = Core.Castle.GetNearestHeartTerritory(SenderPos(ctx));
        else if (!int.TryParse(token, out tindex))
        { ctx.Reply($"[FAUST:err] code=badtarget feature={Settings.CastleInfo}"); return; }

        if (tindex < 0 || !Core.Castle.TryGetTerritory(tindex, out var t, extras: true))
        { ctx.Reply($"[FAUST:err] code=notfound feature={Settings.CastleInfo}"); return; }

        // A single lookup emits one [FAUST:castle] with NO end trailer (BCH commits immediately);
        // the castles list reuses the same row + a [FAUST:end] cmd=castles trailer. Only the single
        // lookup carries the §8a extras (heartlevel/floors/claimed/clan/items) — the lists stay cheap.
        ctx.Reply(CastleRow(t, extras: true));
        FaustAccessGate.Commit(ctx, gate);
    }

    // ---- Full server castle map: every territory (claimed + open), paged ----

    [Command("castles", description: "BCH: every territory (claimed + open), full map (paged). Usage: .faust api castles [page]")]
    public static void Castles(ChatCommandContext ctx, int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.AllCastles);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        var all = Core.Castle.GetAllTerritories();
        var rows = new List<string>(all.Count);
        foreach (var t in all) rows.Add(CastleRow(t));

        // One ctx.Reply per row + a [FAUST:end] cmd=castles trailer (never \n-join a page).
        if (Wire.SendPage(ctx, "castles", rows, page)) FaustAccessGate.Commit(ctx, gate);
    }

    // ---- Decay watch: claimed castles by soonest-to-decay (admin housekeeping / abandoned-plot intel) ----

    [Command("decay", description: "BCH: claimed castles by soonest decay (paged). Usage: .faust api decay [page]")]
    public static void Decay(ChatCommandContext ctx, int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.DecayWatch);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        var castles = Core.Castle.GetCastlesByDecay();
        var rows = new List<string>(castles.Count);
        foreach (var t in castles) rows.Add(CastleRow(t)); // reuse [FAUST:castle]; cmd=decay disambiguates

        if (Wire.SendPage(ctx, "decay", rows, page)) FaustAccessGate.Commit(ctx, gate);
    }

    // ---- #4 Plot availability ----

    [Command("plots", description: "BCH: free plots, largest first (paged). Usage: .faust api plots [page]")]
    public static void Plots(ChatCommandContext ctx, int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.PlotAvailability);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        var plots = Core.Castle.GetFreePlots();
        var rows = new List<string>(plots.Count);
        foreach (var p in plots)
        {
            var plotRow = $"[FAUST:plot] tindex={p.TerritoryIndex} size={p.SizeBlocks} region={Wire.Region(p.Region)}";
            if (Core.Castle.TryGetTerritoryCenter(p.TerritoryIndex, out var px, out var pz)) // §11a centroid
                plotRow += $" posx={F(px)} posz={F(pz)}";
            rows.Add(plotRow);
        }

        // One ctx.Reply per wire line + a [FAUST:end] trailer (never \n-join a page).
        if (Wire.SendPage(ctx, "plots", rows, page)) FaustAccessGate.Commit(ctx, gate);
    }

    // ---- #3 Player info (self always allowed) ----

    [Command("pinfo", description: "BCH: player info. Usage: .faust api pinfo <name|steamId>")]
    public static void PlayerInfo(ChatCommandContext ctx, string nameOrId)
    {
        if (!EntityExtensions.TryResolvePlayer(nameOrId, out var steamId, out _, out _, out var err))
        {
            // Run the gate first so access/cost still governs even a failed lookup's visibility.
            var g0 = FaustAccessGate.TryAuthorize(ctx, Settings.PlayerInfo);
            if (!g0.Allowed) { ctx.Reply(g0.DenyWire); return; }
            ctx.Reply($"[FAUST:err] code=notfound feature={Settings.PlayerInfo}");
            return;
        }

        bool self = steamId == ctx.Event.User.PlatformId;
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.PlayerInfo, bypassAccess: self);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        if (!Core.PlayerInfo.TryGetPlayer(steamId, out var s))
        { ctx.Reply($"[FAUST:err] code=notfound feature={Settings.PlayerInfo}"); return; }

        ctx.Reply(
            $"[FAUST:player] steam={s.SteamId} name={Wire.Safe(s.Name)} online={(s.Online ? 1 : 0)} " +
            $"lastonline={ToUnix(s.LastConnected)} firstseen={s.FirstSeenUnix} " +
            $"sessions={s.Sessions} playmins={s.PlayMinutes} freq={Freq(s.FreqPerWeek)} peakhour={s.PeakHour} " +
            $"daysidle={s.DaysIdle}");
        FaustAccessGate.Commit(ctx, gate);
    }

    // ---- #6 Enemy castle resource totals (admin-default, PvP-sensitive) ----

    [Command("resources", description: "BCH: sum a castle's container contents. Usage: .faust api resources <here|nearest|tindex> [page]")]
    public static void Resources(ChatCommandContext ctx, string token = "here", int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.CastleResources);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        int tindex;
        if (string.Equals(token, "here", StringComparison.OrdinalIgnoreCase))
            tindex = Core.Castle.GetTerritoryIndexAt(SenderPos(ctx));
        else if (string.Equals(token, "nearest", StringComparison.OrdinalIgnoreCase))
            tindex = Core.Castle.GetNearestHeartTerritory(SenderPos(ctx));
        else if (!int.TryParse(token, out tindex))
        { ctx.Reply($"[FAUST:err] code=badtarget feature={Settings.CastleResources}"); return; }

        if (tindex < 0 || !Core.Castle.TrySummarizeResources(tindex, out var sum))
        { ctx.Reply($"[FAUST:err] code=notfound feature={Settings.CastleResources}"); return; }

        if (page <= 1)
            ctx.Reply($"[FAUST:res] tindex={sum.TerritoryIndex} owner={Wire.Safe(sum.OwnerName)} steam={sum.OwnerSteamId} " +
                      $"containers={sum.Containers} totalitems={sum.TotalItems} distinct={sum.Items.Count} " +
                      $"prisoners={sum.Prisoners}");

        // Item rows, then §8b prisoner rows — both page together under cmd=resources (Raphael
        // disambiguates by the [FAUST:item] / [FAUST:prisoner] tag).
        var rows = new List<string>(sum.Items.Count + sum.Prisoners);
        foreach (var it in sum.Items)
            rows.Add($"[FAUST:item] guid={it.guid} qty={it.qty} name={Wire.Safe(it.name)}");
        foreach (var p in sum.PrisonerList)
            rows.Add($"[FAUST:prisoner] name={Wire.Safe(p.name)} bloodtype={Wire.Safe(p.bloodType)} " +
                     $"bloodquality={p.bloodQuality}");

        Wire.SendPage(ctx, "resources", rows, page);
        FaustAccessGate.Commit(ctx, gate); // a resolved castle is a real result, even if empty
    }

    // ---- #8 Server stats: leaderboard + concurrency series + activity analytics (charts) ----
    //
    // <kind> ∈ playtime | concurrency (paged leaderboard/series) | hours | sessions (single-line,
    // optional <name|steamId> scope) | daily | newplayers (un-paged day series, optional days count).
    // <arg> carries the page (playtime/concurrency), the player scope (hours/sessions), or the day
    // window (daily/newplayers), parsed per-kind so one gate governs the whole stats surface.

    [Command("stats", description: "BCH: server stats. Usage: .faust api stats <playtime|concurrency|hours|daily|newplayers|sessions|weekdays|pdaily|population|recency|peak|regions|players|activegrid|regiondaily> [page|player|days] [days|page]")]
    public static void Stats(ChatCommandContext ctx, string kind, string arg = null, string arg2 = null)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.Stats);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        switch (kind?.ToLowerInvariant())
        {
            case "playtime":
            {
                var rows = new List<string>();
                int rank = 1;
                foreach (var e in Core.Store.GetPlaytimeLeaderboard())
                    rows.Add($"[FAUST:stat] kind=playtime rank={rank++} steam={e.steam} name={Wire.Safe(e.name)} value={e.minutes}");
                if (Wire.SendPage(ctx, "stats kind=playtime", rows, ArgPage(arg))) FaustAccessGate.Commit(ctx, gate);
                break;
            }
            case "concurrency":
            {
                // Full stored history (bounded by MaxConcurrencyPoints), oldest→newest, PAGED — so the
                // population series isn't silently truncated to a fixed recent window.
                var rows = new List<string>();
                foreach (var p in Core.Store.GetConcurrency(int.MaxValue))
                    rows.Add($"[FAUST:stat] kind=concurrency t={p.t} avg={p.count}");
                if (Wire.SendPage(ctx, "stats kind=concurrency", rows, ArgPage(arg))) FaustAccessGate.Commit(ctx, gate);
                break;
            }
            case "hours":
            {
                if (!TryScope(ctx, arg, out var scope, out var scopeTok)) return;
                var h = Core.Store.GetHourHistogram(scope);
                var sb = new StringBuilder($"[FAUST:hours] scope={scopeTok}");
                for (int i = 0; i < 24; i++) sb.Append($" h{i:00}={h[i]}");
                ctx.Reply(sb.ToString());
                // §9b sibling line: distinct players active per UTC hour — the denominator for Raphael's
                // "average minutes per active player" toggle (avg[h] = h[h] / p[h], guarding p=0).
                var hp = Core.Store.GetHourPlayerCounts(scope);
                var sbp = new StringBuilder($"[FAUST:hoursplayers] scope={scopeTok}");
                for (int i = 0; i < 24; i++) sbp.Append($" p{i:00}={hp[i]}");
                ctx.Reply(sbp.ToString());
                FaustAccessGate.Commit(ctx, gate);
                break;
            }
            case "sessions":
            {
                if (!TryScope(ctx, arg, out var scope, out var scopeTok)) return;
                var b = Core.Store.GetSessionLengthBuckets(scope);
                ctx.Reply($"[FAUST:sessions] scope={scopeTok} lt15={b.lt15} m15_60={b.m15to60} h1_3={b.h1to3} gt3h={b.gt3h}");
                FaustAccessGate.Commit(ctx, gate);
                break;
            }
            case "daily":
            {
                int days = ArgDays(arg, 14);
                var rows = new List<string>(days);
                foreach (var d in Core.Store.GetDailySeries(days))
                    rows.Add($"[FAUST:daily] day={d.dayMidnightUtc} dau={d.dau} minutes={d.minutes} " +
                             $"new={d.newCount} returning={d.dau - d.newCount}");
                if (Wire.SendList(ctx, "daily", rows)) FaustAccessGate.Commit(ctx, gate);
                break;
            }
            case "newplayers":
            {
                int days = ArgDays(arg, 30);
                var rows = new List<string>(days);
                foreach (var d in Core.Store.GetNewPlayersSeries(days))
                    rows.Add($"[FAUST:newplayers] day={d.dayMidnightUtc} new={d.newPlayers}");
                if (Wire.SendList(ctx, "newplayers", rows)) FaustAccessGate.Commit(ctx, gate);
                break;
            }
            case "weekdays":
            {
                if (!TryScope(ctx, arg, out var scope, out var scopeTok)) return;
                var d = Core.Store.GetWeekdayHistogram(scope);
                var sb = new StringBuilder($"[FAUST:weekdays] scope={scopeTok}");
                for (int i = 0; i < 7; i++) sb.Append($" d{i}={d[i]}");
                ctx.Reply(sb.ToString());
                FaustAccessGate.Commit(ctx, gate);
                break;
            }
            case "pdaily":
            {
                // Per-player daily series: requires a player; arg2 is the optional day window.
                if (string.IsNullOrWhiteSpace(arg) ||
                    !EntityExtensions.TryResolvePlayer(arg, out var steamId, out _, out _, out _))
                { ctx.Reply($"[FAUST:err] code=notfound feature={Settings.Stats}"); return; }

                int days = ArgDays(arg2, 90);
                var rows = new List<string>();
                foreach (var p in Core.Store.GetPlayerDailySeries(steamId, days))
                    rows.Add($"[FAUST:pdaily] steam={steamId} day={p.dayMidnightUtc} minutes={p.minutes}");
                if (Wire.SendList(ctx, "pdaily", rows)) FaustAccessGate.Commit(ctx, gate);
                break;
            }
            case "population":
            {
                var p = Core.Store.GetPopulationStats();
                ctx.Reply($"[FAUST:population] dau={p.Dau} wau={p.Wau} mau={p.Mau} new_today={p.NewToday} " +
                          $"returning_today={p.ReturningToday} stickiness={Dec(p.Stickiness)} " +
                          $"d1={Dec(p.D1)} d7={Dec(p.D7)} d30={Dec(p.D30)}");
                FaustAccessGate.Commit(ctx, gate);
                break;
            }
            case "recency":
            {
                var r = Core.Store.GetRecencyBuckets();
                ctx.Reply($"[FAUST:recency] seen24h={r.seen24h} seen7d={r.seen7d} seen30d={r.seen30d} " +
                          $"dormant={r.dormant} total={r.total}");
                FaustAccessGate.Commit(ctx, gate);
                break;
            }
            case "peak":
            {
                var c = Core.Store.GetConcurrencySummary(ArgDays(arg, 30));
                ctx.Reply($"[FAUST:concsummary] peak={c.peak} peak_t={c.peakT} avg={Dec(c.avg)} " +
                          $"p95={c.p95} now={c.now}");
                FaustAccessGate.Commit(ctx, gate);
                break;
            }
            case "players":
            {
                // §7 roster: one row per tracked player (the per-player data behind the aggregates), paged.
                var roster = Core.Store.GetPlayerRoster();
                var rows = new List<string>(roster.Count);
                foreach (var r in roster)
                    rows.Add($"[FAUST:prow] steam={r.Steam} name={Wire.Safe(r.Name)} online={(r.Online ? 1 : 0)} " +
                             $"lastonline={r.LastOnlineUnix} active24h={(r.Active24h ? 1 : 0)} active7d={(r.Active7d ? 1 : 0)} " +
                             $"sessions={r.Sessions} playmins={r.PlayMinutes} daysidle={r.DaysIdle}");
                if (Wire.SendPage(ctx, "players", rows, ArgPage(arg))) FaustAccessGate.Commit(ctx, gate);
                break;
            }
            case "regions":
            {
                // §10b: `plots` (total buildable territories) is the fill-% denominator (claimed ÷ buildable).
                var comp = RegionStats.Gather();
                var rows = new List<string>(comp.Count);
                foreach (var r in comp)
                    rows.Add($"[FAUST:region] name={Wire.Region(r.region)} players={r.players} " +
                             $"castles={r.castles} plots={r.plots}");
                if (Wire.SendPage(ctx, "regions", rows, ArgPage(arg))) FaustAccessGate.Commit(ctx, gate);
                break;
            }
            case "regiondaily":
            {
                // §10c: per-day per-region snapshots (arg = day window, arg2 = page). Forward-accumulating
                // series — sparse (only sampled days appear), starts at install. See FaustStore.GetRegionDaily.
                int days = ArgDays(arg, 30);
                var series = Core.Store.GetRegionDaily(days);
                var rows = new List<string>(series.Count);
                foreach (var d in series)
                    rows.Add($"[FAUST:rdrow] day={d.day} region={Wire.Region(d.region == "-" ? null : d.region)} " +
                             $"castles={d.castles} plots={d.plots} players={d.players}");
                if (Wire.SendPage(ctx, $"regiondaily days={days}", rows, ArgPage(arg2))) FaustAccessGate.Commit(ctx, gate);
                break;
            }
            case "activegrid":
            {
                // §9d: per-player active-days grid (arg = day window, arg2 = page). Each row is one
                // player's days-played count + a compact day:minutes CSV (see GetActiveGrid).
                int days = ArgDays(arg, 30);
                var grid = Core.Store.GetActiveGrid(days);
                var rows = new List<string>(grid.Count);
                foreach (var g in grid)
                    rows.Add($"[FAUST:agrow] steam={g.steam} name={Wire.Safe(g.name)} active={g.activeDays} days={g.daysCsv}");
                if (Wire.SendPage(ctx, $"activegrid days={days}", rows, ArgPage(arg2))) FaustAccessGate.Commit(ctx, gate);
                break;
            }
            default:
                ctx.Reply($"[FAUST:err] code=badtarget feature={Settings.Stats}");
                break;
        }
    }

    // ---- Clan composition: clanned vs independent + per-clan roster (admin-default, PvP-sensitive) ----

    [Command("clans", description: "BCH: clan composition — clanned vs independent + per-clan roster (paged). Usage: .faust api clans [page]")]
    public static void Clans(ChatCommandContext ctx, int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.Clans);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        var c = Core.Clan.GetComposition();
        if (page <= 1)
            ctx.Reply($"[FAUST:clansummary] clans={c.Clans} clanned={c.Clanned} independent={c.Independent} " +
                      $"online_clanned={c.OnlineClanned} online_independent={c.OnlineIndependent} " +
                      $"largest={c.Largest} avg={c.AvgSize.ToString("0.0", CultureInfo.InvariantCulture)}");

        var rows = new List<string>(c.ClanList.Count);
        foreach (var ci in c.ClanList)
            rows.Add($"[FAUST:clan] name={Wire.Safe(ci.Name)} members={ci.Members} online={ci.Online} " +
                     $"castles={ci.Castles} leader={Wire.Safe(ci.Leader)}");

        Wire.SendPage(ctx, "clans", rows, page);
        FaustAccessGate.Commit(ctx, gate); // composition is a real result even on a clanless server
    }

    // ---- §8c Clan members: one clan's roster (under the clans gate) ----

    [Command("clanmembers", description: "BCH: a clan's member roster (paged). Usage: .faust api clanmembers <clanName> [page]")]
    public static void ClanMembers(ChatCommandContext ctx, string clanName, int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.Clans);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        // VCF 0.10.4 tokenizes on spaces and has no [Remainder], so a clan name with spaces can't arrive as
        // one token — Raphael sends the **wire-safe** (`_`-encoded) name, a single token, with an optional
        // int page after it (clean 2-arg form). GetClanMembers matches the raw name OR its Wire.Safe form.
        string name = (clanName ?? string.Empty).Trim();
        var members = Core.Clan.GetClanMembers(name);
        if (members is null) { ctx.Reply($"[FAUST:err] code=notfound feature={Settings.Clans}"); return; }

        var rows = new List<string>(members.Count);
        foreach (var m in members)
            rows.Add($"[FAUST:clanmember] name={Wire.Safe(m.Name)} online={(m.Online ? 1 : 0)} " +
                     $"role={(m.Leader ? "leader" : "member")}");

        Wire.SendPage(ctx, "clanmembers", rows, page);
        FaustAccessGate.Commit(ctx, gate); // a matched clan is a real result even with an empty roster
    }

    // ---- §8e Faust oversight: per-feature access + usage reporting (under the stats gate) ----

    [Command("access", description: "BCH: per-feature access snapshot (paged). Usage: .faust api access [page]")]
    public static void Access(ChatCommandContext ctx, int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.Stats);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        var rows = new List<string>(FeatureOrder.Length);
        foreach (var key in FeatureOrder)
        {
            var f = Settings.Feature(key);
            if (f is null) continue;
            string scope = f.Access switch
            {
                AccessLevel.Players => "players",
                AccessLevel.AdminOnly => "admin",
                _ => "off",
            };
            string cost = f.HasCost ? $"{f.CostItemGuid.Value}x{f.CostQuantity.Value}" : "0";
            // §15a (ApiVersion ≥18): report the non-cost gates so Raphael can DISPLAY the full picture
            // (it already shows cost). All bare numbers; 0 = unset. neardist is invariant-formatted.
            string near = f.HasProximity
                ? $"{f.RequireNearPrefab.Value} neardist={f.RequireNearDistance.Value.ToString("0.#", CultureInfo.InvariantCulture)}"
                : "0 neardist=0";
            rows.Add($"[FAUST:access] feature={key} scope={scope} cost={cost} " +
                     $"cd={f.CooldownSeconds.Value} window={f.WindowSeconds.Value} period={f.PeriodSeconds.Value} " +
                     $"maxuses={f.MaxUsesPerPeriod.Value} nearprefab={near} " +
                     $"granted={Core.Unlock.GrantedCount(key)} unlocked={Core.Unlock.UnlockedCount(f)}");
        }
        if (Wire.SendPage(ctx, "access", rows, page)) FaustAccessGate.Commit(ctx, gate);
    }

    [Command("usage", description: "BCH: per-feature usage over the last N days (paged). Usage: .faust api usage [days=7] [page]")]
    public static void Usage(ChatCommandContext ctx, int days = 7, int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.Stats);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        var rows = new List<string>();
        foreach (var u in Core.UsageStats.GetUsage(days))
        {
            var f = Settings.Feature(u.Feature);
            int item = f?.CostItemGuid.Value ?? 0;
            rows.Add($"[FAUST:usagerow] feature={u.Feature} uses={u.Uses} payers={u.Payers} " +
                     $"itemspent={u.ItemSpent} item={item} cooldownhits={u.CooldownHits}");
        }
        // Always commit: an empty window (no usage yet) is still a valid, real answer.
        Wire.SendPage(ctx, "usage", rows, page);
        FaustAccessGate.Commit(ctx, gate);
    }

    // ---- §9a New-players roster: who joined in the window (under the stats gate) ----

    [Command("newplayers", description: "BCH: roster of players who first joined in the window (paged). Usage: .faust api newplayers roster [days=30] [page]")]
    public static void NewPlayers(ChatCommandContext ctx, string sub = "roster", int days = 30, int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.Stats);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }
        if (!string.Equals(sub, "roster", StringComparison.OrdinalIgnoreCase))
        { ctx.Reply($"[FAUST:err] code=badtarget feature={Settings.Stats}"); return; }

        days = Math.Clamp(days, 1, 90);
        var clans = Core.Clan.GetPlayerClanNames();
        var castleCounts = CastleCountsBySteam(); // §10a: owned castle hearts per player
        var roster = Core.Store.GetNewPlayersRoster(days);
        var rows = new List<string>(roster.Count);
        foreach (var r in roster)
        {
            string clan = clans.TryGetValue(r.steam, out var c) && !string.IsNullOrEmpty(c) ? Wire.Safe(c) : "-";
            castleCounts.TryGetValue(r.steam, out var castles);
            rows.Add($"[FAUST:nprow] steam={r.steam} name={Wire.Safe(r.name)} firstseen={r.firstSeen} clan={clan} " +
                     $"playmins={r.playMinutes} castles={castles}");
        }
        // Always commit: an empty window (no new joins) is still a real, valid answer.
        Wire.SendPage(ctx, $"newplayersroster days={days}", rows, page);
        FaustAccessGate.Commit(ctx, gate);
    }

    // ---- §9c Sessions timeline: individual online intervals, per player or all (under the stats gate) ----

    [Command("sessions", description: "BCH: per-session online intervals (paged). Usage: .faust api sessions timeline <all|name|steamId> [days=14] [page]")]
    public static void Sessions(ChatCommandContext ctx, string sub = "timeline", string target = "all", int days = 14, int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.Stats);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }
        if (!string.Equals(sub, "timeline", StringComparison.OrdinalIgnoreCase))
        { ctx.Reply($"[FAUST:err] code=badtarget feature={Settings.Stats}"); return; }

        days = Math.Clamp(days, 1, 90);
        ulong? scope = null;
        if (!string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (!EntityExtensions.TryResolvePlayer(target, out var sid, out _, out _, out _))
            { ctx.Reply($"[FAUST:err] code=notfound feature={Settings.Stats}"); return; }
            scope = sid;
        }

        var tl = Core.Store.GetSessionTimeline(scope, days);
        var rows = new List<string>(tl.Count);
        foreach (var s in tl)
            rows.Add($"[FAUST:stl] steam={s.steam} name={Wire.Safe(s.name)} start={s.start} end={s.end}");
        // Always commit: a window with no sessions is still a real answer (only a bad player target is notfound).
        Wire.SendPage(ctx, $"sessionstimeline days={days}", rows, page);
        FaustAccessGate.Commit(ctx, gate);
    }

    // ---- #1 Online positions (admin-default) ----

    [Command("positions", description: "BCH: positions of online players (paged). Usage: .faust api positions [page]")]
    public static void Positions(ChatCommandContext ctx, int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.PlayerPositions);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        var positions = Core.PlayerInfo.GetOnlinePositions();
        var rows = new List<string>(positions.Count);
        foreach (var p in positions)
            rows.Add($"[FAUST:pos] steam={p.SteamId} name={Wire.Safe(p.Name)} " +
                     $"x={F(p.X)} z={F(p.Z)} tindex={p.TerritoryIndex} region={Wire.Region(p.Region)}");

        if (Wire.SendPage(ctx, "positions", rows, page)) FaustAccessGate.Commit(ctx, gate);
    }

    // ---- V Blood boss status board (ApiVersion ≥18) ----

    [Command("bosses", description: "BCH: V Blood boss status board (paged). Usage: .faust api bosses [page]")]
    public static void Bosses(ChatCommandContext ctx, int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.Bosses);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        var bosses = Core.Boss.GetBosses();
        var rows = new List<string>(bosses.Count);
        foreach (var b in bosses) rows.Add(BossRow(b));

        // Always commit: "no bosses currently spawned and none defeated yet" is a valid, real answer.
        Wire.SendPage(ctx, "bosses", rows, page);
        FaustAccessGate.Commit(ctx, gate);
    }

    [Command("boss", description: "BCH: one V Blood boss by name or GUID. Usage: .faust api boss <name|guid>")]
    public static void Boss(ChatCommandContext ctx, string nameOrGuid)
    {
        // Single token (VCF 0.10.4 has no [Remainder] + matches by exact arg count): Raphael sends the
        // prefab GUID or the wire-safe (`_`-joined) boss name — both single tokens. TryGetBoss also does a
        // case-insensitive substring match, so a one-word fragment (e.g. "Wolf") resolves too.
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.Bosses);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        if (!Core.Boss.TryGetBoss(nameOrGuid?.Trim(), out var b))
        { ctx.Reply($"[FAUST:err] code=notfound feature={Settings.Bosses}"); return; }

        ctx.Reply(BossRow(b));          // single row, no end trailer (BCH disambiguates by the in-flight query)
        FaustAccessGate.Commit(ctx, gate);
    }

    /// <summary>One [FAUST:boss] wire row. Position/region/health/level are emitted ONLY when the boss is
    /// alive (a live entity); a not-spawned boss carries status=down + defeated and omits the live fields.</summary>
    static string BossRow(BossService.BossSnapshot b)
    {
        var sb = new StringBuilder();
        sb.Append($"[FAUST:boss] guid={b.Guid} name={Wire.Safe(b.Name)} ")
          .Append($"status={(b.Alive ? "up" : "down")} defeated={(b.Defeated ? 1 : 0)}");
        if (b.Alive)
        {
            int pct = b.HpMax > 0 ? (int)System.Math.Round(b.Hp / b.HpMax * 100f) : 0;
            sb.Append($" x={F(b.X)} z={F(b.Z)} region={Wire.Region(b.Region)} ")
              .Append($"hp={F(b.Hp)} hpmax={F(b.HpMax)} hppct={pct} level={b.Level}");
        }
        return sb.ToString();
    }

    // ---- Kill leaderboards (ApiVersion ≥18) ----

    [Command("kills", description: "BCH: top players by kills (paged). Usage: .faust api kills [days=0] [page] (days 0 = all-time)")]
    public static void Kills(ChatCommandContext ctx, int days = 0, int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.Kills);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        var board = Core.Kills.GetTopKillers(days);
        var rows = new List<string>(board.Count);
        for (int i = 0; i < board.Count; i++)
        {
            var r = board[i];
            rows.Add($"[FAUST:kill] rank={i + 1} steam={r.Steam} name={Wire.Safe(Core.Store.GetName(r.Steam))} " +
                     $"kills={r.Kills} pvp={r.Pvp}");
        }
        // Always commit: an empty board (tracking on but nobody's killed yet) is a valid answer.
        Wire.SendPage(ctx, "kills", rows, page);
        FaustAccessGate.Commit(ctx, gate);
    }

    [Command("bosskills", description: "BCH: V Blood defeat counts (paged). Usage: .faust api bosskills [days=0] [page] (days 0 = all-time)")]
    public static void BossKills(ChatCommandContext ctx, int days = 0, int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.Kills);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        var board = Core.Kills.GetBossKills(days);
        var rows = new List<string>(board.Count);
        for (int i = 0; i < board.Count; i++)
        {
            var r = board[i];
            rows.Add($"[FAUST:bosskill] rank={i + 1} guid={r.Guid} " +
                     $"name={Wire.Safe(new Stunlock.Core.PrefabGUID(r.Guid).GetPrefabName())} count={r.Count}");
        }
        Wire.SendPage(ctx, "bosskills", rows, page);
        FaustAccessGate.Commit(ctx, gate);
    }

    // ---- World-asset scan (map of NPC units + resource nodes; ApiVersion ≥18) ----

    [Command("worldscan", description: "BCH: map assets (units + resource nodes), filtered. Usage: .faust api worldscan [type=units|nodes|all,id=<g>,bloodtype=<g>,bloodqmin=<0-100>] [page]")]
    public static void WorldScan(ChatCommandContext ctx, string spec = "all", int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.WorldScan);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        var filter = ParseScanFilter(spec);
        var assets = Core.WorldScan.GetAssets(filter, out bool truncated);
        var rows = new List<string>(assets.Count);
        foreach (var a in assets)
        {
            var sb = new StringBuilder();
            sb.Append($"[FAUST:asset] guid={a.Guid} name={Wire.Safe(a.Name)} kind={(a.IsUnit ? "unit" : "node")} ")
              .Append($"x={F(a.X)} z={F(a.Z)} region={Wire.Region(a.Region)}");
            if (a.IsUnit)
            {
                if (a.HpMax > 0) sb.Append($" hp={F(a.Hp)} hpmax={F(a.HpMax)}");
                sb.Append($" bloodtype={Wire.Safe(a.BloodType)} bloodq={a.BloodQuality}");
                if (a.UnitCategory != -1) sb.Append($" unittype={a.UnitCategory}");
            }
            else if (a.ResourceTier != -1)
            {
                sb.Append($" restier={a.ResourceTier}");
            }
            rows.Add(sb.ToString());
        }

        if (truncated)
            ctx.Reply($"[FAUST:note] cmd=worldscan truncated=1 max={Settings.WorldScanMaxResults.Value}");
        // Always commit: an empty/over-filtered result is still a valid answer.
        Wire.SendPage(ctx, "worldscan", rows, page);
        FaustAccessGate.Commit(ctx, gate);
    }

    /// <summary>Parse the worldscan filter spec — a single no-space token of comma-separated key=value
    /// pairs (VCF 0.10.4 form), e.g. `type=units,bloodqmin=60`. A bare `units`/`nodes`/`all` is shorthand
    /// for the kind. Unknown tokens are ignored (default = everything).</summary>
    static WorldScanService.Filter ParseScanFilter(string spec)
    {
        string kind = "all"; int id = 0, bt = 0, bqmin = -1, unittype = int.MinValue;
        foreach (var tok in (spec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = tok.IndexOf('=');
            if (eq < 0)
            {
                kind = NormalizeKind(tok, kind);
                continue;
            }
            string k = tok.Substring(0, eq).Trim().ToLowerInvariant();
            string v = tok.Substring(eq + 1).Trim();
            switch (k)
            {
                case "type": case "kind": kind = NormalizeKind(v, kind); break;
                case "id": int.TryParse(v, out id); break;
                case "bloodtype": case "bt": int.TryParse(v, out bt); break;
                case "bloodqmin": case "bloodq": case "quality":
                    if (int.TryParse(v, out var q)) bqmin = System.Math.Clamp(q, 0, 100);
                    break;
                case "unittype": case "unitcat": case "category":
                    int.TryParse(v, out unittype);
                    break;
            }
        }
        return new WorldScanService.Filter { Kind = kind, Id = id, BloodType = bt, BloodQMin = bqmin, UnitType = unittype };
    }

    static string NormalizeKind(string v, string fallback) => v.ToLowerInvariant() switch
    {
        "unit" or "units" or "npc" or "npcs" => "units",
        "node" or "nodes" or "resource" or "resources" => "nodes",
        "all" or "any" or "*" => "all",
        _ => fallback,
    };

    // ---- Player-position heat map (density grid; per-player or server-wide) ----

    [Command("heatmap", description: "BCH: player-position heat map (density grid). Usage: .faust api heatmap [<all|name|steamId>] [days=0] [page] (days 0 = all-time)")]
    public static void Heatmap(ChatCommandContext ctx, string target = "all", int days = 0, int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.Heatmap);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }
        if (days < 0) days = 0;

        ulong? scope = null; string scopeTok = "server";
        if (!string.Equals(target, "all", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(target, "server", StringComparison.OrdinalIgnoreCase))
        {
            if (!EntityExtensions.TryResolvePlayer(target, out var sid, out _, out _, out _))
            { ctx.Reply($"[FAUST:err] code=notfound feature={Settings.Heatmap}"); return; }
            scope = sid; scopeTok = sid.ToString();
        }

        var hm = Core.Heatmap.GetHeatmap(scope, days);

        // Header (page 1): scope, the time window (days; 0 = all-time), grid resolution, sample total, distinct
        // cells, the OCCUPIED cell bounds, the FULL-MAP cell extent (§11b — so BCH can draw at a consistent map
        // scale even with sparse data), and whether collection is currently on (so BCH tells "off" from "on but
        // no data yet"). days= is additive: an older Raphael ignores it and still reads the all-time map.
        if (page <= 1)
        {
            string mapbounds = "";
            if (hm.CellSize > 0 && Core.Castle.TryGetMapWorldBounds(out var mnx, out var mnz, out var mxx, out var mxz))
                mapbounds = $" mapbounds={CellIdx(mnx, hm.CellSize)}:{CellIdx(mnz, hm.CellSize)}:" +
                            $"{CellIdx(mxx, hm.CellSize)}:{CellIdx(mxz, hm.CellSize)}";
            ctx.Reply($"[FAUST:hmhead] scope={scopeTok} days={days} retentiondays={Settings.HeatmapRetentionDays.Value} " +
                      $"cell={F(hm.CellSize)} samples={hm.Samples} " +
                      $"cells={hm.Cells.Count} bounds={hm.MinCx}:{hm.MinCz}:{hm.MaxCx}:{hm.MaxCz}{mapbounds} " +
                      $"collecting={(Settings.HeatmapEnabled.Value ? 1 : 0)}");
        }

        // Cells packed compactly (cx:cz:count, …) so a dense map isn't thousands of chat lines.
        var rows = PackCells(hm.Cells);
        if (Wire.SendPage(ctx, $"heatmap scope={scopeTok} days={days}", rows, page)) FaustAccessGate.Commit(ctx, gate);
    }

    // ---- helpers ----

    /// <summary>Pack heat-map cells into compact "cx:cz:count,…" lines under the wire cap (mirrors the
    /// activegrid CSV idea), so a dense grid pages as a handful of [FAUST:hmrow] lines, not one per cell.</summary>
    static List<string> PackCells(List<(int cx, int cz, int count)> cells)
    {
        const int Budget = 480;
        var rows = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in cells)
        {
            string e = c.cx + ":" + c.cz + ":" + c.count;
            if (sb.Length > 0 && sb.Length + 1 + e.Length > Budget) { rows.Add($"[FAUST:hmrow] data={sb}"); sb.Clear(); }
            if (sb.Length > 0) sb.Append(',');
            sb.Append(e);
        }
        if (sb.Length > 0) rows.Add($"[FAUST:hmrow] data={sb}");
        return rows;
    }

    static Unity.Mathematics.float3 SenderPos(ChatCommandContext ctx) =>
        ctx.Event.SenderCharacterEntity.Read<LocalToWorld>().Position;

    /// <summary>§10a: owned castle-heart count per steam id (one pass over all territories).</summary>
    static Dictionary<ulong, int> CastleCountsBySteam()
    {
        var counts = new Dictionary<ulong, int>();
        foreach (var t in Core.Castle.GetAllTerritories())
        {
            if (!t.HasHeart || t.OwnerSteamId == 0) continue;
            counts.TryGetValue(t.OwnerSteamId, out var c);
            counts[t.OwnerSteamId] = c + 1;
        }
        return counts;
    }

    /// <summary>Parse the stats <arg> as a 1-based page (playtime/concurrency); default/invalid → 1.</summary>
    static int ArgPage(string arg) =>
        int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : 1;

    /// <summary>Parse the stats <arg> as a day window (daily/newplayers), clamped to [1, 90].</summary>
    static int ArgDays(string arg, int fallback)
    {
        int days = int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : fallback;
        return Math.Clamp(days, 1, 90);
    }

    /// <summary>
    /// Resolve a stats scope from <arg>: empty → server-wide (scope=server); else a player. On an
    /// unresolvable player, emits the notfound err and returns false (the caller stops, no commit).
    /// </summary>
    static bool TryScope(ChatCommandContext ctx, string arg, out ulong? scope, out string scopeToken)
    {
        scope = null; scopeToken = "server";
        if (string.IsNullOrWhiteSpace(arg)) return true;
        if (!EntityExtensions.TryResolvePlayer(arg, out var steamId, out _, out _, out _))
        {
            ctx.Reply($"[FAUST:err] code=notfound feature={Settings.Stats}");
            return false;
        }
        scope = steamId; scopeToken = steamId.ToString();
        return true;
    }

    /// <summary>One [FAUST:castle] row — shared by single castleinfo lookups and the castles/decay lists.
    /// Unclaimed (heart-less) territory yields owner=_ steam=0 online=0 lastonline=0 (BuildInfo defaults).
    /// When <paramref name="extras"/> (single castleinfo only), append the §8a optional fields — each is
    /// omitted at its sentinel so Raphael only sees the ones Faust could resolve.</summary>
    static string CastleRow(in CastleService.TerritoryInfo t, bool extras = false)
    {
        var sb = new StringBuilder(
            $"[FAUST:castle] tindex={t.TerritoryIndex} owner={Wire.Safe(t.HasHeart ? t.OwnerName : "")} " +
            $"steam={t.OwnerSteamId} region={Wire.Region(t.Region)} size={t.SizeBlocks} " +
            $"state={CastleService.StateWire(t.State)} decay={t.DecaySeconds} " +
            $"online={(t.OwnerOnline ? 1 : 0)} lastonline={ToUnix(t.OwnerLastConnected)}");
        // §11a: territory centroid (where on the map it is) — on EVERY castle row (cheap dict lookup),
        // omitted when unresolvable. Same world coord space as positions' x/z.
        if (Core.Castle.TryGetTerritoryCenter(t.TerritoryIndex, out var px, out var pz))
            sb.Append($" posx={F(px)} posz={F(pz)}");
        if (extras && t.HasHeart)
        {
            if (t.HeartLevel >= 0) sb.Append($" heartlevel={t.HeartLevel}");
            if (t.Floors >= 0) sb.Append($" floors={t.Floors}");
            if (t.ClaimedUnix >= 0) sb.Append($" claimed={t.ClaimedUnix}");
            if (!string.IsNullOrEmpty(t.ClanName)) sb.Append($" clan={Wire.Safe(t.ClanName)}");
            if (t.TotalItems >= 0) sb.Append($" items={t.TotalItems}");
        }
        return sb.ToString();
    }

    static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>World coord → heat-map cell index at <paramref name="cell"/> size (same floor binning the
    /// store uses), so §11b mapbounds line up exactly with the [FAUST:hmrow] cell indices.</summary>
    static int CellIdx(float world, float cell) => (int)Math.Floor(world / cell);

    /// <summary>A bare invariant decimal (ratios/averages): 2 dp, e.g. stickiness/retention/avg-concurrency.</summary>
    static string Dec(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Logins/week with one decimal; the -1 "not tracked" sentinel stays a bare -1.</summary>
    static string Freq(double v) => v < 0 ? "-1" : v.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>DateTime.ToBinary() value -> Unix seconds (UTC); 0/-1 pass through unchanged.</summary>
    static long ToUnix(long binary)
    {
        if (binary <= 0) return binary; // 0 = unknown, -1 = not tracked
        try { return ((DateTimeOffset)DateTime.FromBinary(binary).ToUniversalTime()).ToUnixTimeSeconds(); }
        catch { return 0; }
    }
}

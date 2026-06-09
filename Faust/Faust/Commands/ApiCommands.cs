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
/// ApiVersion 6: the investigation queries — castleinfo (#2), plots (#4), pinfo (#3), positions
/// (#1), resources (#6) — server stats (#8: playtime + concurrency), and the full admin-control
/// gate (block/schedule/PvP/window-period/cost + unlock criteria). Deny codes: blocked, schedule,
/// pvp, window, locked. Bump whenever the wire grows.
/// </summary>
[CommandGroup("faust api")]
internal static class ApiCommands
{
    const int ApiVersion = 6;

    static readonly string[] FeatureOrder =
    {
        Settings.PlayerPositions, Settings.CastleInfo, Settings.PlayerInfo,
        Settings.PlotAvailability, Settings.ObjectScan, Settings.CastleResources, Settings.Stats,
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

        if (tindex < 0 || !Core.Castle.TryGetTerritory(tindex, out var t))
        { ctx.Reply($"[FAUST:err] code=notfound feature={Settings.CastleInfo}"); return; }

        ctx.Reply(
            $"[FAUST:castle] tindex={t.TerritoryIndex} owner={Wire.Safe(t.HasHeart ? t.OwnerName : "")} " +
            $"steam={t.OwnerSteamId} region={Wire.Safe(t.Region)} size={t.SizeBlocks} " +
            $"state={CastleService.StateWire(t.State)} decay={t.DecaySeconds} " +
            $"online={(t.OwnerOnline ? 1 : 0)} lastonline={ToUnix(t.OwnerLastConnected)}");
        FaustAccessGate.Commit(ctx, gate);
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
            rows.Add($"[FAUST:plot] tindex={p.TerritoryIndex} size={p.SizeBlocks} region={Wire.Safe(p.Region)}");

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
            $"sessions={s.Sessions} playmins={s.PlayMinutes} freq={Freq(s.FreqPerWeek)} peakhour={s.PeakHour}");
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
                      $"containers={sum.Containers} totalitems={sum.TotalItems} distinct={sum.Items.Count}");

        var rows = new List<string>(sum.Items.Count);
        foreach (var it in sum.Items)
            rows.Add($"[FAUST:item] guid={it.guid} qty={it.qty} name={Wire.Safe(it.name)}");

        Wire.SendPage(ctx, "resources", rows, page);
        FaustAccessGate.Commit(ctx, gate); // a resolved castle is a real result, even if empty
    }

    // ---- #8 Server stats (playtime leaderboard + concurrency series) ----

    [Command("stats", description: "BCH: server stats. Usage: .faust api stats <playtime|concurrency> [page]")]
    public static void Stats(ChatCommandContext ctx, string kind, int page = 1)
    {
        var gate = FaustAccessGate.TryAuthorize(ctx, Settings.Stats);
        if (!gate.Allowed) { ctx.Reply(gate.DenyWire); return; }

        kind = kind?.ToLowerInvariant();
        List<string> rows;
        if (kind == "playtime")
        {
            rows = new List<string>();
            int rank = 1;
            foreach (var e in Core.Store.GetPlaytimeLeaderboard())
                rows.Add($"[FAUST:stat] kind=playtime rank={rank++} steam={e.steam} name={Wire.Safe(e.name)} value={e.minutes}");
        }
        else if (kind == "concurrency")
        {
            rows = new List<string>();
            foreach (var p in Core.Store.GetConcurrency(200))
                rows.Add($"[FAUST:stat] kind=concurrency t={p.t} avg={p.count}");
        }
        else { ctx.Reply($"[FAUST:err] code=badtarget feature={Settings.Stats}"); return; }

        if (Wire.SendPage(ctx, $"stats kind={kind}", rows, page)) FaustAccessGate.Commit(ctx, gate);
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
                     $"x={F(p.X)} z={F(p.Z)} tindex={p.TerritoryIndex}");

        if (Wire.SendPage(ctx, "positions", rows, page)) FaustAccessGate.Commit(ctx, gate);
    }

    // ---- helpers ----

    static Unity.Mathematics.float3 SenderPos(ChatCommandContext ctx) =>
        ctx.Event.SenderCharacterEntity.Read<LocalToWorld>().Position;

    static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);

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

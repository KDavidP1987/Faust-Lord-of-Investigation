using System.Text;
using Faust.Config;
using Faust.Services;
using VampireCommandFramework;

namespace Faust.Commands;

/// <summary>
/// Runtime operational control over Faust features (ADMIN_CONTROL §3). Admin-only chat commands
/// (NOT the [FAUST:*] wire) that drive the persisted <see cref="FeatureControlService"/> override
/// layer on top of the static .cfg: block/countdown, unblock, time-of-day schedule, and a status
/// readout. Use the literal feature key (playerpositions, castleinfo, playerinfo, plotavailability,
/// objectscan, castleresources, stats) or "all" to gate everything at once.
/// </summary>
[CommandGroup("faust admin")]
internal static class AdminCommands
{
    static bool ValidFeature(string key) =>
        key == FeatureControlService.All || Settings.Features.ContainsKey(key);

    static bool CheckFeature(ChatCommandContext ctx, string feature)
    {
        if (ValidFeature(feature)) return true;
        ctx.Reply($"Unknown feature '{feature}'. Use one of: all, {string.Join(", ", Settings.Features.Keys)}.");
        return false;
    }

    [Command("block", description: "Block a feature now. Usage: .faust admin block <feature|all> [minutes] (no minutes = until unblocked)", adminOnly: true)]
    public static void Block(ChatCommandContext ctx, string feature, int minutes = 0)
    {
        feature = feature.ToLowerInvariant();
        if (!CheckFeature(ctx, feature)) return;
        Core.Control.Block(feature, minutes);
        ctx.Reply(minutes <= 0
            ? $"Faust '{feature}' BLOCKED until you unblock it."
            : $"Faust '{feature}' BLOCKED for {minutes} minute(s) (auto-reopens after).");
    }

    [Command("unblock", description: "Clear a block/countdown. Usage: .faust admin unblock <feature|all>", adminOnly: true)]
    public static void Unblock(ChatCommandContext ctx, string feature)
    {
        feature = feature.ToLowerInvariant();
        if (!CheckFeature(ctx, feature)) return;
        Core.Control.Unblock(feature);
        ctx.Reply($"Faust '{feature}' unblocked.");
    }

    [Command("schedule", description: "Time-of-day window (server local). Usage: .faust admin schedule <feature|all> <HH:MM-HH:MM|clear>", adminOnly: true)]
    public static void Schedule(ChatCommandContext ctx, string feature, string window)
    {
        feature = feature.ToLowerInvariant();
        if (!CheckFeature(ctx, feature)) return;

        if (string.Equals(window, "clear", System.StringComparison.OrdinalIgnoreCase))
        {
            Core.Control.ClearSchedule(feature);
            ctx.Reply($"Faust '{feature}' schedule cleared (always open).");
            return;
        }

        if (!TryParseWindow(window, out int startMin, out int endMin))
        {
            ctx.Reply("Bad window. Use HH:MM-HH:MM (24h, server local), e.g. 18:00-22:00, or 'clear'.");
            return;
        }
        Core.Control.SetSchedule(feature, startMin, endMin);
        ctx.Reply($"Faust '{feature}' usable only {window} (server local time).");
    }

    [Command("status", description: "Show each feature's effective runtime control state. Usage: .faust admin status [feature]", adminOnly: true)]
    public static void Status(ChatCommandContext ctx, string feature = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(feature))
        {
            feature = feature.ToLowerInvariant();
            if (!CheckFeature(ctx, feature)) return;
            ctx.Reply($"Faust '{feature}': {Core.Control.Describe(feature)}");
            return;
        }

        sb.AppendLine($"Faust control — all: {Core.Control.Describe(FeatureControlService.All)}");
        foreach (var key in Settings.Features.Keys)
            sb.AppendLine($"  {key}: {Core.Control.Describe(key)}");
        ctx.Reply(sb.ToString());
    }

    [Command("grant", description: "Unlock a feature for a player (overrides its Unlock criterion). Usage: .faust admin grant <player> <feature>", adminOnly: true)]
    public static void Grant(ChatCommandContext ctx, string player, string feature)
    {
        feature = feature.ToLowerInvariant();
        if (!Settings.Features.ContainsKey(feature))
        { ctx.Reply($"Unknown feature '{feature}'. Use one of: {string.Join(", ", Settings.Features.Keys)}."); return; }
        if (!EntityExtensions.TryResolvePlayer(player, out var steam, out var name, out _, out var err))
        { ctx.Reply(err); return; }
        ctx.Reply(Core.Unlock.Grant(steam, feature)
            ? $"Granted '{feature}' to {name}."
            : $"{name} already had '{feature}' granted.");
    }

    [Command("revoke", description: "Remove a feature grant from a player. Usage: .faust admin revoke <player> <feature>", adminOnly: true)]
    public static void Revoke(ChatCommandContext ctx, string player, string feature)
    {
        feature = feature.ToLowerInvariant();
        if (!Settings.Features.ContainsKey(feature))
        { ctx.Reply($"Unknown feature '{feature}'. Use one of: {string.Join(", ", Settings.Features.Keys)}."); return; }
        if (!EntityExtensions.TryResolvePlayer(player, out var steam, out var name, out _, out var err))
        { ctx.Reply(err); return; }
        ctx.Reply(Core.Unlock.Revoke(steam, feature)
            ? $"Revoked '{feature}' grant from {name}."
            : $"{name} had no '{feature}' grant.");
    }

    [Command("unlocks", description: "Show a player's unlock progress (V-blood defeats + granted features). Usage: .faust admin unlocks <player>", adminOnly: true)]
    public static void Unlocks(ChatCommandContext ctx, string player)
    {
        if (!EntityExtensions.TryResolvePlayer(player, out var steam, out var name, out _, out var err))
        { ctx.Reply(err); return; }
        ctx.Reply($"{name} — {Core.Unlock.Describe(steam)}");
    }

    [Command("showpositions", description: "EXPERIMENTAL: show online players on the native map (admins). Usage: .faust admin showpositions <on|off|status> [minutes]", adminOnly: true)]
    public static void ShowPositions(ChatCommandContext ctx, string mode = "status", int minutes = 0)
    {
        if (!Core.IsReady || Core.MapMarkers is null) { ctx.Reply("Faust isn't ready yet."); return; }
        switch (mode?.ToLowerInvariant())
        {
            case "on": ctx.Reply(Core.MapMarkers.Enable(minutes)); break;
            case "off": ctx.Reply(Core.MapMarkers.Disable()); break;
            case "status": ctx.Reply($"Map markers: {Core.MapMarkers.Describe()}"); break;
            default: ctx.Reply("Usage: .faust admin showpositions <on|off|status> [minutes]"); break;
        }
    }

    static bool TryParseWindow(string window, out int startMin, out int endMin)
    {
        startMin = endMin = -1;
        var halves = window.Split('-');
        if (halves.Length != 2) return false;
        return TryParseHhmm(halves[0], out startMin) && TryParseHhmm(halves[1], out endMin);
    }

    static bool TryParseHhmm(string s, out int minutes)
    {
        minutes = -1;
        var parts = s.Trim().Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m)) return false;
        if (h < 0 || h > 23 || m < 0 || m > 59) return false;
        minutes = h * 60 + m;
        return true;
    }
}

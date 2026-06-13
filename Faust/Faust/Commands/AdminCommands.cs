using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faust.Config;
using Faust.Services;
using Stunlock.Core;
using VampireCommandFramework;

namespace Faust.Commands;

/// <summary>
/// Runtime operational control over Faust features (ADMIN_CONTROL §3). Admin-only chat commands
/// (NOT the [FAUST:*] wire) that drive the persisted <see cref="FeatureControlService"/> override
/// layer on top of the static .cfg: block/countdown, unblock, time-of-day schedule, and a status
/// readout. Use the literal feature key (playerpositions, castleinfo, playerinfo, plotavailability,
/// castleresources, stats, …) or "all" to gate everything at once.
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
        if (!string.IsNullOrEmpty(feature))
        {
            feature = feature.ToLowerInvariant();
            if (!CheckFeature(ctx, feature)) return;
            ctx.Reply($"Faust '{feature}': {Core.Control.Describe(feature)}");
            return;
        }

        // Chunked: keep each ctx.Reply under VCF's 512-byte FixedString512Bytes cap (Raphael §13) —
        // a full feature list with block/schedule descriptions can otherwise overflow a single reply.
        var lines = new List<string>();
        foreach (var key in Settings.Features.Keys)
            lines.Add($"  {key}: {Core.Control.Describe(key)}");
        ReplyLines(ctx, $"Faust control — all: {Core.Control.Describe(FeatureControlService.All)}", lines);
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

    // ---- Live config editor (ADMIN_CONTROL §3): change ANY .cfg setting in-game. Writes the BepInEx
    //      ConfigEntry directly, so the change takes effect immediately (the gate reads .Value live) and
    //      BepInEx auto-persists it to kdpen.Faust.cfg — no restart, no reload. ----

    [Command("set", description: "Change one or MORE feature settings live (immediate + persisted). Usage: .faust admin set <feature> <setting=value[,setting=value,...]> (NO spaces)", adminOnly: true)]
    public static void Set(ChatCommandContext ctx, string feature, string spec)
    {
        feature = feature.ToLowerInvariant();
        if (!Settings.Features.TryGetValue(feature, out var f))
        { ctx.Reply($"Unknown feature '{feature}'. Use one of: {string.Join(", ", Settings.Features.Keys)}."); return; }
        ApplySpec(ctx, feature, ConfigEditor.FeatureFields(f), spec);
    }

    [Command("get", description: "Show a feature's live settings. Usage: .faust admin get <feature> [setting]", adminOnly: true)]
    public static void Get(ChatCommandContext ctx, string feature, string setting = null)
    {
        feature = feature.ToLowerInvariant();
        if (!Settings.Features.TryGetValue(feature, out var f))
        { ctx.Reply($"Unknown feature '{feature}'. Use one of: {string.Join(", ", Settings.Features.Keys)}."); return; }
        ShowSettings(ctx, feature, ConfigEditor.FeatureFields(f), setting);
    }

    [Command("setglobal", description: "Change one or MORE global settings live (immediate + persisted). Usage: .faust admin setglobal <setting=value[,setting=value,...]> (NO spaces)", adminOnly: true)]
    public static void SetGlobal(ChatCommandContext ctx, string spec)
        => ApplySpec(ctx, "global", ConfigEditor.GlobalFields(), spec);

    [Command("getglobal", description: "Show Faust's global settings. Usage: .faust admin getglobal [setting]", adminOnly: true)]
    public static void GetGlobal(ChatCommandContext ctx, string setting = null)
        => ShowSettings(ctx, "global", ConfigEditor.GlobalFields(), setting);

    [Command("resetcfg", description: "Reset settings to defaults. Usage: .faust admin resetcfg <feature|global> [setting]", adminOnly: true)]
    public static void ResetCfg(ChatCommandContext ctx, string target, string setting = null)
    {
        target = target.ToLowerInvariant();
        List<ConfigField> fields;
        if (target == "global") fields = ConfigEditor.GlobalFields();
        else if (Settings.Features.TryGetValue(target, out var f)) fields = ConfigEditor.FeatureFields(f);
        else { ctx.Reply($"Unknown target '{target}'. Use 'global' or one of: {string.Join(", ", Settings.Features.Keys)}."); return; }

        if (!string.IsNullOrEmpty(setting))
        {
            var field = ConfigEditor.Find(fields, setting);
            if (field is null) { ctx.Reply($"Unknown setting '{setting}'. Settings: {ConfigEditor.NameList(fields)}."); return; }
            field.Reset();
            Core.Log.LogInfo($"[FAUST CONFIG] steamId={ctx.Event.User.PlatformId} reset {target}.{field.Name} -> {field.Show()}");
            ctx.Reply($"Reset [{target}] {field.Name} = {field.Show()} (default).");
            return;
        }
        foreach (var x in fields) x.Reset();
        Core.Log.LogInfo($"[FAUST CONFIG] steamId={ctx.Event.User.PlatformId} reset ALL {target} settings to defaults");
        ctx.Reply($"Reset all {fields.Count} '{target}' settings to defaults.");
    }

    /// <summary>Apply one OR MANY <c>setting=value</c> pairs from a single comma-separated, space-free arg
    /// (e.g. <c>costitem=12345,costqty=1,cooldown=30</c>). One arg is required because VCF 0.10.4 tokenizes
    /// the chat line on spaces and matches commands by exact arg count (no <c>[Remainder]</c> in this VCF) —
    /// so the whole pair-list must arrive as a single space-free token. Each pair is validated and applied
    /// independently; a bad pair is reported but does NOT stop the others. Faust setting values never
    /// contain spaces, <c>,</c> or <c>=</c>, so the split is unambiguous.</summary>
    static void ApplySpec(ChatCommandContext ctx, string scope, List<ConfigField> fields, string spec)
    {
        var pairs = (spec ?? string.Empty).Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        if (pairs.Length == 0)
        {
            ctx.Reply($"Give setting=value pair(s), comma-separated, NO spaces (e.g. 'costitem=12345,costqty=1,cooldown=30'). " +
                      $"Settings: {ConfigEditor.NameList(fields)}.");
            return;
        }

        var applied = new List<string>();
        var rejected = new List<string>();
        foreach (var raw in pairs)
        {
            int eq = raw.IndexOf('=');
            if (eq <= 0 || eq == raw.Length - 1) { rejected.Add($"'{raw}': use setting=value"); continue; }
            string sName = raw.Substring(0, eq).Trim();
            string sVal = raw.Substring(eq + 1).Trim();
            var field = ConfigEditor.Find(fields, sName);
            if (field is null) { rejected.Add($"{sName}: unknown setting"); continue; }
            var (ok, note) = field.Set(sVal);
            if (!ok) { rejected.Add($"{field.Name}: {note}"); continue; }
            applied.Add($"{field.Name}={field.Show()}{(note != null ? $" ({note})" : "")}");
        }

        if (applied.Count > 0)
            Core.Log.LogInfo($"[FAUST CONFIG] steamId={ctx.Event.User.PlatformId} set {scope}: {string.Join(", ", applied)}");

        var lines = new List<string>();
        if (applied.Count > 0) lines.Add($"Set [{scope}]: {string.Join(", ", applied)}");
        if (rejected.Count > 0) lines.Add($"Rejected: {string.Join("; ", rejected)}");
        if (lines.Count == 0) lines.Add("Nothing changed.");
        ReplyLines(ctx, null, lines);
    }

    static void ShowSettings(ChatCommandContext ctx, string scope, List<ConfigField> fields, string setting)
    {
        if (!string.IsNullOrEmpty(setting))
        {
            var field = ConfigEditor.Find(fields, setting);
            if (field is null) { ctx.Reply($"Unknown setting '{setting}'. Settings: {ConfigEditor.NameList(fields)}."); return; }
            ctx.Reply($"[{scope}] {field.Name} = {field.Show()}  (valid: {field.Hint})");
            return;
        }
        ReplyLines(ctx, $"[{scope}] settings:", fields.Select(x => $"  {x.Name} = {x.Show()}"));
    }

    /// <summary>Emit a header + lines as chat replies, batching so no single message gets too long.</summary>
    static void ReplyLines(ChatCommandContext ctx, string header, IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        if (header != null) sb.AppendLine(header);
        foreach (var line in lines)
        {
            if (sb.Length + line.Length > 400) { ctx.Reply(sb.ToString().TrimEnd()); sb.Clear(); }
            sb.AppendLine(line);
        }
        if (sb.Length > 0) ctx.Reply(sb.ToString().TrimEnd());
    }

    [Command("prefab", description: "Look up prefab IDs/names for commands (no external dump needed). Usage: .faust admin prefab <id|nameFragment> [page]", adminOnly: true)]
    public static void Prefab(ChatCommandContext ctx, string query, int page = 1)
    {
        if (!Core.IsReady) { ctx.Reply("Faust isn't ready yet."); return; }
        if (string.IsNullOrWhiteSpace(query)) { ctx.Reply("Usage: .faust admin prefab <id|nameFragment> [page]"); return; }

        // A whole-number query is treated as a GUID lookup (prefab hashes are commonly negative).
        if (int.TryParse(query, out int guid))
        {
            ctx.Reply(PrefabLookup.Exists(guid)
                ? $"{guid} = {PrefabLookup.Name(guid)}"
                : $"No prefab with ID {guid} (it may exist but be unnamed).");
            return;
        }

        const int max = 300;
        var matches = PrefabLookup.Search(query, max, out bool capped);
        if (matches.Count == 0) { ctx.Reply($"No prefab name contains '{query}'."); return; }

        const int per = 20;
        int total = System.Math.Max(1, (matches.Count + per - 1) / per);
        if (page < 1) page = 1;
        if (page > total) page = total;
        var lines = new List<string>();
        for (int i = (page - 1) * per; i < System.Math.Min(page * per, matches.Count); i++)
            lines.Add($"  {matches[i].guid}  {matches[i].name}");
        ReplyLines(ctx, $"Prefabs matching '{query}': {matches.Count}{(capped ? "+" : "")} (page {page}/{total}):", lines);
    }

    [Command("worldscandiag", description: "Audit prefab categorization (units vs nodes + EntityCategory). Usage: .faust admin worldscandiag <nameFragment>", adminOnly: true)]
    public static void WorldScanDiag(ChatCommandContext ctx, string fragment)
    {
        if (!Core.IsReady || Core.WorldScan is null) { ctx.Reply("Faust isn't ready yet."); return; }
        ReplyLines(ctx, null, Core.WorldScan.Diagnose(fragment));
    }

    [Command("bossdiag", description: "Diagnostics for the boss board (Raphael §18): dump VBlood entity positions/components. Usage: .faust admin bossdiag [name|guid]", adminOnly: true)]
    public static void BossDiag(ChatCommandContext ctx, string filter = null)
    {
        if (!Core.IsReady || Core.Boss is null) { ctx.Reply("Faust isn't ready yet."); return; }
        ReplyLines(ctx, null, Core.Boss.Diagnose(filter));
    }

    [Command("worldscan", description: "Manage the world-scan asset whitelist. Usage: .faust admin worldscan <list|add|remove|clear|seed> [guid|page]", adminOnly: true)]
    public static void WorldScanAdmin(ChatCommandContext ctx, string mode, string arg = null)
    {
        if (!Core.IsReady || Core.WorldScan is null) { ctx.Reply("Faust isn't ready yet."); return; }
        switch (mode?.ToLowerInvariant())
        {
            case "add":
                if (!int.TryParse(arg, out var ag)) { ctx.Reply("Usage: .faust admin worldscan add <prefabGuid>"); return; }
                ctx.Reply(Core.WorldScan.AddToWhitelist(ag)
                    ? $"Added {ag} ({new PrefabGUID(ag).GetPrefabName()}) to the world-scan whitelist ({Core.WorldScan.WhitelistCount} total)."
                    : $"{ag} is already whitelisted.");
                break;
            case "remove": case "rem": case "del":
                if (!int.TryParse(arg, out var rg)) { ctx.Reply("Usage: .faust admin worldscan remove <prefabGuid>"); return; }
                ctx.Reply(Core.WorldScan.RemoveFromWhitelist(rg)
                    ? $"Removed {rg} from the world-scan whitelist ({Core.WorldScan.WhitelistCount} left)."
                    : $"{rg} wasn't whitelisted.");
                break;
            case "clear":
                ctx.Reply($"Cleared {Core.WorldScan.ClearWhitelist()} entr(ies). Nothing will show until you 'add' or 'seed'.");
                break;
            case "seed":
                ctx.Reply($"Seeded the world-scan whitelist from the prefab catalog — now {Core.WorldScan.Seed()} entr(ies). Trim with 'remove'/'clear'.");
                break;
            case "list":
                int page = 1; int.TryParse(arg, out page); if (page < 1) page = 1;
                var all = Core.WorldScan.ListWhitelist();
                const int per = 20;
                int total = System.Math.Max(1, (all.Count + per - 1) / per);
                if (page > total) page = total;
                var lines = new List<string>();
                for (int i = (page - 1) * per; i < System.Math.Min(page * per, all.Count); i++)
                    lines.Add($"  {all[i].guid}  {all[i].name}");
                ReplyLines(ctx, $"World-scan whitelist: {all.Count} entr(ies) (page {page}/{total}):", lines);
                break;
            default:
                ctx.Reply("Usage: .faust admin worldscan <list|add|remove|clear|seed> [guid|page]");
                break;
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

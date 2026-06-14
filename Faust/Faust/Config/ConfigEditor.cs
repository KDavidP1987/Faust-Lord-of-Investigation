using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;

namespace Faust.Config;

/// <summary>
/// One editable setting: a friendly name (+ aliases), the backing BepInEx entry, a valid-values hint,
/// and a parse/validate setter. Used by the '.faust admin set/get/resetcfg' surface to mutate config
/// live. Writing <see cref="ConfigEntryBase.BoxedValue"/> takes effect IMMEDIATELY — the gate reads
/// every <c>ConfigEntry.Value</c> on each query — and BepInEx auto-persists the write to the .cfg, so
/// an in-game change both applies now and survives a restart (no reload needed).
/// </summary>
internal sealed class ConfigField
{
    public string Name { get; init; }
    public string[] Aliases { get; init; } = Array.Empty<string>();
    public string Hint { get; init; }
    public ConfigEntryBase Entry { get; init; }
    public Func<string, (bool ok, string note)> Setter { get; init; }

    public bool Matches(string token) =>
        Name.Equals(token, StringComparison.OrdinalIgnoreCase)
        || Aliases.Any(a => a.Equals(token, StringComparison.OrdinalIgnoreCase));

    /// <summary>Current value as a stable, culture-invariant string (floats never localize the decimal).</summary>
    public string Show() => Entry.BoxedValue switch
    {
        float fl => fl.ToString("0.###", CultureInfo.InvariantCulture),
        null => "",
        var v => v.ToString(),
    };

    public (bool ok, string note) Set(string raw) => Setter(raw);

    /// <summary>Restore the setting to its compiled-in default (and persist).</summary>
    public void Reset() => Entry.BoxedValue = Entry.DefaultValue;
}

/// <summary>
/// The live-edit registry behind '.faust admin set/get/resetcfg' (ADMIN_CONTROL §3). Maps every
/// configurable Faust setting — per-feature and global — to a <see cref="ConfigField"/> that knows
/// how to validate, set, and render it. Single source of truth for the in-game config editor so a new
/// setting is exposed by adding one line here, not a bespoke command.
/// </summary>
internal static class ConfigEditor
{
    const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    public static ConfigField Find(IEnumerable<ConfigField> fields, string token) =>
        fields.FirstOrDefault(f => f.Matches(token));

    public static string NameList(IEnumerable<ConfigField> fields) =>
        string.Join(", ", fields.Select(f => f.Name));

    // ---- per-feature settings (every axis the FaustAccessGate enforces) ----
    public static List<ConfigField> FeatureFields(FeatureConfig f) => new()
    {
        Enum("access", f.AccessRaw, new[] { "Off", "AdminOnly", "Players" }),
        Enum("delivery", f.DeliveryRaw, new[] { "ServerMediated", "Free" }),
        Int("costitem", f.CostItemGuid, int.MinValue, int.MaxValue, "PrefabGUID hash (0 = free)", "costguid", "costitemguid"),
        Int("costqty", f.CostQuantity, 0, int.MaxValue, ">= 0", "costquantity"),
        Int("cooldown", f.CooldownSeconds, 0, int.MaxValue, "seconds, >= 0", "cooldownseconds", "cd"),
        Bool("adminsexempt", f.AdminsExempt, "exempt"),
        Enum("availability", f.AvailabilityRaw, new[] { "Always", "PvEOnly", "PvPOnly" }, "pvp"),
        Int("window", f.WindowSeconds, 0, int.MaxValue, "seconds, >= 0", "windowseconds"),
        Int("period", f.PeriodSeconds, 0, int.MaxValue, "seconds, >= 0 (86400 = daily)", "periodseconds"),
        Int("maxuses", f.MaxUsesPerPeriod, 0, int.MaxValue, ">= 0 (0 = unlimited)", "maxusesperperiod", "max"),
        UnlockField(f.UnlockRaw),
        Int("nearprefab", f.RequireNearPrefab, int.MinValue, int.MaxValue, "PrefabGUID (0 = off)", "requirenearprefab", "near"),
        Float("neardist", f.RequireNearDistance, 0f, float.MaxValue, "metres, >= 0", "requireneardistance", "dist"),
    };

    // ---- global settings (master, anti-spam, collection, heatmap, map-markers) ----
    public static List<ConfigField> GlobalFields() => new()
    {
        Bool("enabled", Settings.Enabled),
        Bool("audit", Settings.AuditQueries, "auditqueries"),
        Bool("verbose", Settings.VerboseLogging, "verboselogging"),
        Int("ratelimit", Settings.RateLimitSeconds, 0, int.MaxValue, "seconds, >= 0", "ratelimitseconds"),
        Bool("ratelimitexempt", Settings.RateLimitAdminsExempt, "ratelimitadminsexempt"),
        Str("resetsteamids", Settings.DataResetSteamIds, "dataresetsteamids"),
        Bool("sessiontracking", Settings.SessionTracking),
        Bool("concurrencysampling", Settings.ConcurrencySampling),
        Int("maxconcurrencypoints", Settings.MaxConcurrencyPoints, 0, int.MaxValue, ">= 0"),
        Int("sessionretentiondays", Settings.SessionRetentionDays, 0, int.MaxValue, ">= 0 (0 = forever)"),
        Str("datanamespace", Settings.DataNamespace),
        Bool("heatmapenabled", Settings.HeatmapEnabled),
        Int("heatmapsample", Settings.HeatmapSampleSeconds, 30, 300, "30..300 seconds", "heatmapsampleseconds"),
        Float("heatmapcellsize", Settings.HeatmapCellSize, 1f, float.MaxValue, "world units, > 0"),
        Int("heatmapmaxcells", Settings.HeatmapMaxCells, 0, int.MaxValue, ">= 0 (0 = unlimited)"),
        Int("heatmapretentiondays", Settings.HeatmapRetentionDays, 0, int.MaxValue, ">= 0 (0 = keep all days)"),
        Bool("mapmarkersenabled", Settings.MapMarkersEnabled, "markersenabled"),
        Int("mapmarkerprefab", Settings.MapMarkerPrefabGuid, int.MinValue, int.MaxValue, "PrefabGUID", "mapmarkerprefabguid"),
        Int("worldscaninterval", Settings.WorldScanInterval, 5, int.MaxValue, "seconds, >= 5"),
        Int("worldscanmaxresults", Settings.WorldScanMaxResults, 0, int.MaxValue, ">= 0 (0 = unlimited)"),
        Float("bossmaplimit", Settings.BossMapLimit, 100f, 20000f, "world units (boss on-map threshold; default 9000, keep < ~10000)"),
    };

    // ---- field factories ----

    static ConfigField Bool(string name, ConfigEntry<bool> e, params string[] aliases) => new()
    {
        Name = name, Aliases = aliases, Entry = e, Hint = "on | off",
        Setter = raw =>
        {
            if (!TryBool(raw, out var b)) return (false, $"'{raw}' is not on/off (on|off|true|false|1|0|yes|no).");
            e.Value = b; return (true, null);
        },
    };

    static ConfigField Int(string name, ConfigEntry<int> e, long min, long max, string hint, params string[] aliases) => new()
    {
        Name = name, Aliases = aliases, Entry = e, Hint = hint,
        Setter = raw =>
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return (false, $"'{raw}' is not a whole number.");
            if (v < min || v > max) return (false, $"{name} must be {min}..{max}.");
            e.Value = v; return (true, null);
        },
    };

    static ConfigField Float(string name, ConfigEntry<float> e, float min, float max, string hint, params string[] aliases) => new()
    {
        Name = name, Aliases = aliases, Entry = e, Hint = hint,
        Setter = raw =>
        {
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return (false, $"'{raw}' is not a number.");
            if (v < min || v > max) return (false, $"{name} must be {min.ToString("0.###", CultureInfo.InvariantCulture)}..{max.ToString("0.###", CultureInfo.InvariantCulture)}.");
            e.Value = v; return (true, null);
        },
    };

    static ConfigField Str(string name, ConfigEntry<string> e, params string[] aliases) => new()
    {
        Name = name, Aliases = aliases, Entry = e, Hint = "text ('empty' or \"\" clears it)",
        Setter = raw =>
        {
            e.Value = (raw.Equals("empty", OIC) || raw == "\"\"" || raw == "-") ? "" : raw;
            return (true, null);
        },
    };

    static ConfigField Enum(string name, ConfigEntry<string> e, string[] allowed, params string[] aliases) => new()
    {
        Name = name, Aliases = aliases, Entry = e, Hint = string.Join(" | ", allowed),
        Setter = raw =>
        {
            foreach (var a in allowed)
                if (a.Equals(raw, OIC)) { e.Value = a; return (true, null); } // store canonical casing
            return (false, $"{name} must be one of: {string.Join(", ", allowed)}.");
        },
    };

    /// <summary>The Unlock setting — same grammar the gate parses (None | FinalBoss | BossKill:&lt;guid&gt;,
    /// plus the reserved AllBosses/AllQuests which are accepted but admin-grant-only for now).</summary>
    static ConfigField UnlockField(ConfigEntry<string> e)
    {
        const string hint = "None | FinalBoss | BossKill:<guid> | AllBosses | AllQuests";
        return new ConfigField
        {
            Name = "unlock", Entry = e, Hint = hint,
            Setter = raw =>
            {
                raw = raw.Trim();
                if (raw.Equals("None", OIC)) { e.Value = "None"; return (true, null); }
                if (raw.Equals("FinalBoss", OIC)) { e.Value = "FinalBoss"; return (true, null); }
                if (raw.Equals("AllBosses", OIC)) { e.Value = "AllBosses"; return (true, "reserved — admin-grant-only (no auto-detect yet)"); }
                if (raw.Equals("AllQuests", OIC)) { e.Value = "AllQuests"; return (true, "reserved — admin-grant-only (no auto-detect yet)"); }
                if (raw.StartsWith("BossKill:", OIC))
                {
                    var rest = raw.Substring("BossKill:".Length);
                    if (int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g))
                    { e.Value = $"BossKill:{g}"; return (true, null); }
                    return (false, "BossKill needs a numeric V-Blood PrefabGUID, e.g. BossKill:-1905691330.");
                }
                return (false, $"unlock must be: {hint}.");
            },
        };
    }

    static bool TryBool(string s, out bool v)
    {
        v = false;
        switch (s.Trim().ToLowerInvariant())
        {
            case "on": case "true": case "1": case "yes": case "enable": case "enabled": v = true; return true;
            case "off": case "false": case "0": case "no": case "disable": case "disabled": v = false; return true;
            default: return false;
        }
    }
}

using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace Faust.Config;

/// <summary>How exposed a feature is, server-enforced (design §1/§4).</summary>
internal enum AccessLevel { Off, AdminOnly, Players }

/// <summary>
/// Free = BCH reads replicated state locally (un-gateable, un-chargeable);
/// ServerMediated = routed through Faust so it can be gated, charged, audited (design §1).
/// </summary>
internal enum DeliveryMode { ServerMediated, Free }

/// <summary>Whether a feature is usable given the server's game mode (ADMIN_CONTROL §1 axis 3).</summary>
internal enum PvpAvailability { Always, PvEOnly, PvPOnly }

/// <summary>Progression gate a player must satisfy before a feature opens (ADMIN_CONTROL §1 axis 6).
/// GrantOnly = a recognized-but-not-auto-detected criterion (AllBosses/AllQuests) — admin grant only for now.</summary>
internal enum UnlockKind { None, BossKill, FinalBoss, GrantOnly }

/// <summary>
/// The per-feature gateable unit: its resolved access, delivery, item cost, cooldown,
/// and whether admins skip access/cost. Reads live config so admins can retune without
/// a recompile (BepInEx reads the .cfg at boot — changes take effect on server restart).
/// </summary>
internal sealed class FeatureConfig
{
    public string Key { get; }
    public ConfigEntry<string> AccessRaw { get; init; }
    public ConfigEntry<string> DeliveryRaw { get; init; }
    public ConfigEntry<int> CostItemGuid { get; init; }
    public ConfigEntry<int> CostQuantity { get; init; }
    public ConfigEntry<int> CooldownSeconds { get; init; }
    public ConfigEntry<bool> AdminsExempt { get; init; }
    // ---- ADMIN_CONTROL axes 3 + 5 ----
    public ConfigEntry<string> AvailabilityRaw { get; init; }
    public ConfigEntry<int> WindowSeconds { get; init; }
    public ConfigEntry<int> PeriodSeconds { get; init; }
    public ConfigEntry<int> MaxUsesPerPeriod { get; init; }
    public ConfigEntry<string> UnlockRaw { get; init; }

    public FeatureConfig(string key) { Key = key; }

    /// <summary>Parsed unlock criterion: None | FinalBoss | BossKill:&lt;guid&gt; | GrantOnly (anything else).</summary>
    public (UnlockKind Kind, int Guid) Unlock
    {
        get
        {
            var v = UnlockRaw.Value;
            if (string.IsNullOrWhiteSpace(v) || v.Equals("None", StringComparison.OrdinalIgnoreCase))
                return (UnlockKind.None, 0);
            if (v.Equals("FinalBoss", StringComparison.OrdinalIgnoreCase))
                return (UnlockKind.FinalBoss, 0);
            if (v.StartsWith("BossKill:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(v.Substring("BossKill:".Length), out var g))
                return (UnlockKind.BossKill, g);
            return (UnlockKind.GrantOnly, 0); // AllBosses / AllQuests / unrecognized -> admin-grant only
        }
    }

    public bool HasUnlock => Unlock.Kind != UnlockKind.None;

    public AccessLevel Access => AccessRaw.Value switch
    {
        "Players" => AccessLevel.Players,
        "AdminOnly" => AccessLevel.AdminOnly,
        _ => AccessLevel.Off,
    };

    public DeliveryMode Delivery =>
        DeliveryRaw.Value == "Free" ? DeliveryMode.Free : DeliveryMode.ServerMediated;

    public PvpAvailability Availability => AvailabilityRaw.Value switch
    {
        "PvEOnly" => PvpAvailability.PvEOnly,
        "PvPOnly" => PvpAvailability.PvPOnly,
        _ => PvpAvailability.Always,
    };

    public bool HasCost => CostItemGuid.Value != 0 && CostQuantity.Value > 0;

    /// <summary>True if a window/period rate-limit (not just a flat cooldown) is configured.</summary>
    public bool HasWindowPolicy => PeriodSeconds.Value > 0;
}

/// <summary>
/// BepInEx config bindings — generated to BepInEx/config/kdpen.Faust.cfg on the server.
/// One global block plus one block per investigation feature (design §4). Every feature is
/// independently exposed (Off / AdminOnly / Players), with its own delivery, item cost, and
/// cooldown, so admins price/gate intel per-feature (sensitive intel defaults to AdminOnly).
/// </summary>
internal static class Settings
{
    // ---- Feature keys (the gateable units; design §5). Used as config section names and
    //      as the tokens the .faust api version handshake advertises to BloodCraftHub. ----
    public const string PlayerPositions = "playerpositions"; // #1 all-player map positions
    public const string CastleInfo = "castleinfo";           // #2 plot owner/heart/decay/last-online
    public const string PlayerInfo = "playerinfo";           // #3 last login, frequency, playtime
    public const string PlotAvailability = "plotavailability"; // #4 free plots by size
    public const string ObjectScan = "objectscan";           // #5 nearby object info
    public const string CastleResources = "castleresources"; // #6 enemy castle resource totals (PvP)
    public const string Stats = "stats";                     // #8 server stats / leaderboards

    public static ConfigEntry<bool> Enabled { get; private set; }
    public static ConfigEntry<bool> AuditQueries { get; private set; }
    public static ConfigEntry<bool> VerboseLogging { get; private set; }

    static readonly Dictionary<string, FeatureConfig> _features = new();
    public static IReadOnlyDictionary<string, FeatureConfig> Features => _features;
    public static FeatureConfig Feature(string key) => _features.TryGetValue(key, out var f) ? f : null;

    public static void Initialize(ConfigFile config)
    {
        Enabled = config.Bind(
            "Faust", "Enabled", true,
            "Master switch for the whole mod. When false, every query refuses with [FAUST:err] code=disabled.");

        AuditQueries = config.Bind(
            "Faust", "AuditQueries", true,
            "Log who asked what, when, and whether they were charged — a privacy/abuse trail for admins.");

        VerboseLogging = config.Bind(
            "Diagnostics", "VerboseLogging", false,
            "Emit detailed per-query log lines (useful when testing; noisy in production).");

        // Per-feature blocks. Defaults follow design §4/§5: sensitive intel (positions, enemy
        // resources, other players' info) defaults to AdminOnly; benign/own-data is Players;
        // every feature starts cost-free and admin-exempt so a fresh install is usable.
        Bind(config, PlayerPositions, AccessLevel.AdminOnly, DeliveryMode.ServerMediated);
        Bind(config, CastleInfo, AccessLevel.Players, DeliveryMode.ServerMediated);
        Bind(config, PlayerInfo, AccessLevel.AdminOnly, DeliveryMode.ServerMediated);
        Bind(config, PlotAvailability, AccessLevel.Players, DeliveryMode.ServerMediated);
        Bind(config, ObjectScan, AccessLevel.Players, DeliveryMode.Free);
        Bind(config, CastleResources, AccessLevel.AdminOnly, DeliveryMode.ServerMediated);
        Bind(config, Stats, AccessLevel.Players, DeliveryMode.ServerMediated);
    }

    static void Bind(ConfigFile config, string key, AccessLevel defaultAccess, DeliveryMode defaultDelivery)
    {
        var section = $"Faust.{key}";
        var fc = new FeatureConfig(key)
        {
            AccessRaw = config.Bind(section, "Access", defaultAccess.ToString(),
                "Off | AdminOnly | Players. Who may run this query. Sensitive intel defaults to AdminOnly."),
            DeliveryRaw = config.Bind(section, "Delivery", defaultDelivery.ToString(),
                "ServerMediated (gateable/chargeable) | Free (BCH reads replicated state locally; un-gateable). " +
                "Free is only meaningful for near-player/own-data features."),
            CostItemGuid = config.Bind(section, "CostItemGuid", 0,
                "Item PrefabGUID hash the requester must spend to run this query. 0 = free (the Faustian toll)."),
            CostQuantity = config.Bind(section, "CostQuantity", 0,
                "How many of CostItemGuid to charge per query. Ignored when CostItemGuid = 0."),
            CooldownSeconds = config.Bind(section, "CooldownSeconds", 0,
                "Per-player flat lockout between runs of this query, in seconds. 0 = no cooldown. " +
                "(Use this for 'pay a cost, then locked N seconds'. For 'open a window once per period', " +
                "use WindowSeconds + PeriodSeconds below.)"),
            AdminsExempt = config.Bind(section, "AdminsExempt", true,
                "When true, admins skip this feature's access check, cost, cooldown, window/period, and PvP gate."),
            AvailabilityRaw = config.Bind(section, "Availability", PvpAvailability.Always.ToString(),
                "Always | PvEOnly | PvPOnly. Gate the feature on the server's game mode (e.g. enemy-resource " +
                "intel PvPOnly, or position intel PvEOnly). Always = no game-mode restriction."),
            WindowSeconds = config.Bind(section, "WindowSeconds", 0,
                "Rate-limit: once the feature is first used in a period, it stays OPEN for this many seconds " +
                "(repeated uses within the window are free). 0 = no window (each use is discrete). " +
                "Example: 600 with PeriodSeconds=86400 = 'a 10-minute window, once per day'."),
            PeriodSeconds = config.Bind(section, "PeriodSeconds", 0,
                "Rate-limit recurrence in seconds (e.g. 86400 = daily). 0 = no period (only CooldownSeconds " +
                "applies). After MaxUsesPerPeriod uses/windows in a period, the feature locks until the period rolls over."),
            MaxUsesPerPeriod = config.Bind(section, "MaxUsesPerPeriod", 0,
                "How many uses (or window-opens, if WindowSeconds>0) are allowed per period. 0 = unlimited within " +
                "the period. Example: 1 with PeriodSeconds=86400 = once per day."),
            UnlockRaw = config.Bind(section, "Unlock", "None",
                "Progression gate before a player may use this feature. 'None' = always available. 'FinalBoss' = " +
                "after defeating Dracula (game completion). 'BossKill:<vbloodGuid>' = after defeating that specific " +
                "V Blood. Admins are exempt (AdminsExempt); '.faust admin grant <player> " + key +
                "' overrides for any player. (AllBosses/AllQuests are reserved — admin-grant only for now.)"),
        };
        _features[key] = fc;
    }
}

using Faust.Config;
using ProjectM;
using Stunlock.Core;
using Unity.Entities;
using VampireCommandFramework;

namespace Faust.Services;

/// <summary>
/// The single gatekeeper every BCH-facing query passes through before any data is gathered
/// (design §3, ADMIN_CONTROL §4). Evaluation order — first failing axis denies with a specific
/// <c>[FAUST:err] code=…</c>:
///   master-enabled → runtime block → schedule → feature-enabled → per-player rate limit →
///   unlock → access → PvP availability → proximity → usage (cooldown / window / period) → item cost.
///
/// Admins with <c>AdminsExempt=true</c> skip access / PvP / usage / cost (and the rate limit, when
/// RateLimitAdminsExempt), but NOT a master-off, a deactivated feature, or an operational
/// block/schedule (those are server state, not a price).
///
/// Reserve/confirm (the "never charge an empty query" rule): <see cref="TryAuthorize"/> only
/// VERIFIES; <see cref="Commit"/> records the usage (cooldown/window) and consumes the item, and is
/// called only after the feature produced a real result.
/// </summary>
internal static class FaustAccessGate
{
    // Per-player last-query time (seconds) for the RateLimitSeconds anti-spam floor. In-memory only —
    // a short window doesn't need persistence; cleared on restart.
    static readonly System.Collections.Generic.Dictionary<ulong, double> _lastQuery = new();
    static double NowSeconds => System.DateTime.UtcNow.Ticks / (double)System.TimeSpan.TicksPerSecond;

    internal readonly struct GateResult
    {
        public bool Allowed { get; init; }
        public string DenyWire { get; init; }
        public FeatureConfig Feature { get; init; }
        public bool CostOwed { get; init; }

        public static GateResult Deny(string wire) => new() { Allowed = false, DenyWire = wire };
        public static GateResult Allow(FeatureConfig f, bool costOwed) =>
            new() { Allowed = true, Feature = f, CostOwed = costOwed };
    }

    public static GateResult TryAuthorize(ChatCommandContext ctx, string featureKey, bool bypassAccess = false)
    {
        if (!Core.IsReady)
            return GateResult.Deny("[FAUST:err] code=notready");
        if (!Settings.Enabled.Value)
            return GateResult.Deny($"[FAUST:err] code=disabled feature={featureKey}");

        var f = Settings.Feature(featureKey);
        if (f is null)
            return GateResult.Deny($"[FAUST:err] code=disabled feature={featureKey}");

        // Runtime operational control (block / countdown / schedule) — applies to everyone.
        if (!Core.Control.IsAvailable(featureKey, out var ctlCode, out var ctlSecs))
            return GateResult.Deny($"[FAUST:err] code={ctlCode} feature={featureKey} secs={ctlSecs}");

        // Feature deactivated in config — a hard stop even for admins.
        if (f.Access == AccessLevel.Off)
            return GateResult.Deny($"[FAUST:err] code=disabled feature={featureKey}");

        var user = ctx.Event.User;
        bool isAdmin = user.IsAdmin;
        bool exempt = isAdmin && f.AdminsExempt.Value;

        // Global per-player anti-spam floor (perf protection) — independent of any per-feature cooldown.
        // Admins are exempt by default so dashboards/paging aren't throttled.
        if (Settings.RateLimitSeconds.Value > 0 && !(isAdmin && Settings.RateLimitAdminsExempt.Value))
        {
            double now = NowSeconds;
            if (_lastQuery.TryGetValue(user.PlatformId, out var last))
            {
                double wait = Settings.RateLimitSeconds.Value - (now - last);
                if (wait > 0)
                    return GateResult.Deny($"[FAUST:err] code=ratelimit feature={featureKey} secs={(int)System.Math.Ceiling(wait)}");
            }
            _lastQuery[user.PlatformId] = now;
        }

        // Unlock criterion (progression gate). Skipped for admins (exempt) and self-queries.
        if (!exempt && !bypassAccess && !Core.Unlock.IsUnlocked(user.PlatformId, f, out var need))
            return GateResult.Deny($"[FAUST:err] code=locked feature={featureKey} need={need}");

        // Access level (a self-query may bypass this; see ApiCommands.pinfo).
        if (!bypassAccess && !exempt && f.Access == AccessLevel.AdminOnly && !isAdmin)
            return GateResult.Deny($"[FAUST:err] code=noaccess feature={featureKey}");

        // PvP availability vs the server's game mode.
        if (!exempt && !PvpAllowed(f))
            return GateResult.Deny($"[FAUST:err] code=pvp feature={featureKey}");

        // Proximity: must be near a configured object to use the feature.
        if (!exempt && f.HasProximity
            && !Proximity.PlayerNear(ctx.Event.SenderCharacterEntity, f.RequireNearPrefab.Value, f.RequireNearDistance.Value))
        {
            var dist = f.RequireNearDistance.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            return GateResult.Deny($"[FAUST:err] code=notnear feature={featureKey} item={f.RequireNearPrefab.Value} dist={dist}");
        }

        // Usage rate-limit (flat cooldown and/or window/period).
        if (!exempt && !Core.Usage.Check(user.PlatformId, f, out var useCode, out var useSecs))
        {
            Core.UsageStats?.RecordCooldownHit(featureKey); // §8e usage accounting (admin oversight)
            return GateResult.Deny($"[FAUST:err] code={useCode} feature={featureKey} secs={useSecs}");
        }

        // Item cost (verify; reserved, consumed in Commit after a real result).
        bool costOwed = !exempt && f.HasCost;
        if (costOwed && !RequesterHasItem(ctx, f))
            return GateResult.Deny($"[FAUST:err] code=cost feature={featureKey} item={f.CostItemGuid.Value} qty={f.CostQuantity.Value}");

        return GateResult.Allow(f, costOwed);
    }

    /// <summary>Call ONLY after the feature produced a real result: record usage + consume the cost.</summary>
    public static void Commit(ChatCommandContext ctx, GateResult gate)
    {
        if (!gate.Allowed || gate.Feature is null) return;
        var user = ctx.Event.User;

        Core.Usage.Record(user.PlatformId, gate.Feature);
        if (gate.CostOwed) ConsumeItem(ctx, gate.Feature);

        // §8e usage accounting: tally the successful use (+ payer/quantity when a cost was owed).
        Core.UsageStats?.RecordUse(gate.Feature.Key, user.PlatformId, gate.CostOwed,
            gate.CostOwed ? gate.Feature.CostQuantity.Value : 0);

        if (Settings.AuditQueries.Value)
            Core.Log.LogInfo($"[FAUST AUDIT] steamId={user.PlatformId} ran '{gate.Feature.Key}' charged={gate.CostOwed}");
    }

    // ---- cost (inventory verify/consume; the proven Uriel/KindredLogistics pattern) ----

    static bool RequesterHasItem(ChatCommandContext ctx, FeatureConfig f)
    {
        var character = ctx.Event.SenderCharacterEntity;
        if (!InventoryUtilities.TryGetInventoryEntity(Core.EntityManager, character, out Entity inv)) return false;
        return Core.ServerGameManager.GetInventoryItemCount(inv, new PrefabGUID(f.CostItemGuid.Value)) >= f.CostQuantity.Value;
    }

    static void ConsumeItem(ChatCommandContext ctx, FeatureConfig f)
    {
        var character = ctx.Event.SenderCharacterEntity;
        if (InventoryUtilities.TryGetInventoryEntity(Core.EntityManager, character, out Entity inv))
            Core.ServerGameManager.TryRemoveInventoryItem(inv, new PrefabGUID(f.CostItemGuid.Value), f.CostQuantity.Value);
    }

    // ---- PvP availability ----

    static bool PvpAllowed(FeatureConfig f)
    {
        if (f.Availability == PvpAvailability.Always) return true;
        bool serverPvE = Core.ServerGameSettingsSystem.Settings.GameModeType == GameModeType.PvE;
        return f.Availability == PvpAvailability.PvEOnly ? serverPvE : !serverPvE;
    }

    // ---- Handshake helpers (used by .faust api version; design §2) ----

    /// <summary>Access token resolved for the requesting player: an admin sees "players" where a
    /// non-admin would see "admin".</summary>
    public static string AccessToken(FeatureConfig f, bool isAdmin) => f.Access switch
    {
        AccessLevel.Off => "off",
        AccessLevel.Players => "players",
        AccessLevel.AdminOnly => isAdmin ? "players" : "admin",
        _ => "off",
    };

    /// <summary>Cost token: "0" (free) or "&lt;itemGuid&gt;x&lt;qty&gt;" with an optional ":cd=&lt;seconds&gt;".</summary>
    public static string CostToken(FeatureConfig f)
    {
        if (!f.HasCost) return f.CooldownSeconds.Value > 0 ? $"0:cd={f.CooldownSeconds.Value}" : "0";
        var token = $"{f.CostItemGuid.Value}x{f.CostQuantity.Value}";
        return f.CooldownSeconds.Value > 0 ? $"{token}:cd={f.CooldownSeconds.Value}" : token;
    }
}

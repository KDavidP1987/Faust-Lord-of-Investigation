using System;
using System.Collections.Generic;
using Faust.Config;
using VampireCommandFramework;

namespace Faust.Services;

/// <summary>
/// The single gatekeeper every BCH-facing query passes through before any data is gathered
/// (design §3). Checks, in order: master-enabled → feature-enabled → access level → cooldown →
/// item cost. A deny returns a ready-to-send <c>[FAUST:err] code=…</c> wire line; an allow lets
/// the feature service gather data and emit its <c>[FAUST:*]</c> rows.
///
/// Reserve/confirm split (the "never charge for an empty query" rule): <see cref="TryAuthorize"/>
/// only VERIFIES the cost and stamps nothing. After the feature actually produced a result, the
/// command calls <see cref="Commit"/> to stamp the cooldown and consume the reserved item. An
/// empty/notfound lookup emits <c>[FAUST:err] code=notfound</c> and never calls Commit.
/// </summary>
internal static class FaustAccessGate
{
    static readonly Dictionary<(ulong steamId, string feature), DateTime> _lastRun = new();

    internal readonly struct GateResult
    {
        public bool Allowed { get; init; }
        public string DenyWire { get; init; }   // a complete [FAUST:err] line when !Allowed
        public FeatureConfig Feature { get; init; }
        public bool CostOwed { get; init; }      // a cost is configured and the requester is not exempt

        public static GateResult Deny(string wire) => new() { Allowed = false, DenyWire = wire };
        public static GateResult Allow(FeatureConfig f, bool costOwed) =>
            new() { Allowed = true, Feature = f, CostOwed = costOwed };
    }

    /// <summary>Resolve → enabled → access → cooldown → cost-verify. Stamps/consumes nothing.</summary>
    public static GateResult TryAuthorize(ChatCommandContext ctx, string featureKey)
    {
        if (!Core.IsReady)
            return GateResult.Deny("[FAUST:err] code=notready");

        if (!Settings.Enabled.Value)
            return GateResult.Deny($"[FAUST:err] code=disabled feature={featureKey}");

        var f = Settings.Feature(featureKey);
        if (f is null)
            return GateResult.Deny($"[FAUST:err] code=disabled feature={featureKey}");

        var user = ctx.Event.User;
        bool isAdmin = user.IsAdmin;
        bool exempt = isAdmin && f.AdminsExempt.Value;

        // ---- Access ----
        switch (f.Access)
        {
            case AccessLevel.Off:
                return GateResult.Deny($"[FAUST:err] code=disabled feature={featureKey}");
            case AccessLevel.AdminOnly when !exempt && !isAdmin:
                return GateResult.Deny($"[FAUST:err] code=noaccess feature={featureKey}");
        }

        // ---- Cooldown ----
        if (!exempt && f.CooldownSeconds.Value > 0)
        {
            var key = (user.PlatformId, featureKey);
            if (_lastRun.TryGetValue(key, out var last))
            {
                var secsLeft = (int)(f.CooldownSeconds.Value - (DateTime.UtcNow - last).TotalSeconds);
                if (secsLeft > 0)
                    return GateResult.Deny($"[FAUST:err] code=cooldown feature={featureKey} secs={secsLeft}");
            }
        }

        // ---- Cost (verify only; consume happens in Commit after a real result) ----
        bool costOwed = !exempt && f.HasCost;
        if (costOwed && !RequesterHasItem(ctx, f))
            return GateResult.Deny($"[FAUST:err] code=cost feature={featureKey} item={f.CostItemGuid.Value} qty={f.CostQuantity.Value}");

        return GateResult.Allow(f, costOwed);
    }

    /// <summary>
    /// Call ONLY after the feature produced a real result. Stamps the per-player cooldown and
    /// consumes the reserved item cost. Keeps the "never charge for an empty query" guarantee.
    /// </summary>
    public static void Commit(ChatCommandContext ctx, GateResult gate)
    {
        if (!gate.Allowed || gate.Feature is null) return;
        var user = ctx.Event.User;

        if (gate.Feature.CooldownSeconds.Value > 0)
            _lastRun[(user.PlatformId, gate.Feature.Key)] = DateTime.UtcNow;

        if (gate.CostOwed)
            ConsumeItem(ctx, gate.Feature);

        if (Settings.AuditQueries.Value)
            Core.Log.LogInfo($"[FAUST AUDIT] steamId={user.PlatformId} ran '{gate.Feature.Key}' charged={gate.CostOwed}");
    }

    // TODO(foundation): inventory verify/consume lands with the first server-mediated feature.
    // The cost mechanic's config + handshake advertisement are wired now; the actual
    // inventory check/consume reuses KindredCommands' item helpers (design §3, "Things to
    // watch out for"). Until implemented, a configured cost is advertised but treated as
    // satisfied so the foundation stays usable — see CHANGELOG 0.1.0.
    static bool RequesterHasItem(ChatCommandContext ctx, FeatureConfig f) => true;
    static void ConsumeItem(ChatCommandContext ctx, FeatureConfig f) { /* TODO: consume f.CostQuantity of f.CostItemGuid */ }

    // ---- Handshake helpers (used by .faust api version; design §2) ----

    /// <summary>Access token resolved for the requesting player: an admin sees "players"
    /// where a non-admin would see "admin".</summary>
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

using System;
using System.Text;
using Faust.Config;
using Faust.Services;
using VampireCommandFramework;

namespace Faust.Commands;

/// <summary>
/// Admin-only management of the data Faust COLLECTS (not the live-query features): inspect the
/// footprint, prune old activity on demand, and wipe a store. The companion to the passive-collection
/// config (design §10) and the world-wipe story.
///
/// Faust's data is server-scoped (BepInEx/config/Faust/[&lt;namespace&gt;]/) and survives a world wipe by
/// design — the same players return, so their activity history stays relevant. Resetting is therefore
/// an explicit admin action here, never automatic:
///   • activity (sessions/playtime/concurrency) is usually KEPT across a world reset;
///   • unlock progress (V-blood-gated features) is the one most admins reset on a fresh world.
/// Destructive wipes require a literal "confirm" token so they can't fire by accident.
/// </summary>
[CommandGroup("faust admin data")]
internal static class AdminDataCommands
{
    [Command("status", description: "Show Faust's collected-data footprint (counts, age, disk, namespace, retention).", adminOnly: true)]
    public static void Status(ChatCommandContext ctx)
    {
        var (sessions, conc, names, oldest) = Core.Store.GetStorageStats();
        var (players, defeats, grants) = Core.Unlock.GetUnlockStats();
        long kb = FaustPaths.TotalDataBytes() / 1024;
        string ns = FaustPaths.IsNamespaced ? Settings.DataNamespace.Value : "(shared)";
        int ret = Settings.SessionRetentionDays.Value;

        var sb = new StringBuilder();
        sb.AppendLine($"Faust data — namespace: {ns}; on disk ~{kb} KB; retention: {(ret <= 0 ? "keep forever" : ret + " day(s)")}.");
        sb.AppendLine($"  Activity: {sessions} session(s), {conc} concurrency point(s), {names} name(s); " +
                      $"oldest record {(oldest <= 0 ? "—" : FmtDate(oldest))}. Online now: {Core.Store.OnlineCount}.");
        sb.AppendLine($"  Collection: SessionTracking={(Settings.SessionTracking.Value ? "on" : "off")}, " +
                      $"ConcurrencySampling={(Settings.ConcurrencySampling.Value ? "on" : "off")}.");
        sb.AppendLine($"  Unlocks: {players} player(s), {defeats} V-blood defeat(s), {grants} grant(s). " +
                      $"Usage locks: {Core.Usage.UsageCount} pair(s).");
        sb.Append("Manage: '.faust admin data clear <days>' (prune old activity) · " +
                  "'.faust admin data wipe <activity|unlocks|usage|all> confirm'.");
        ctx.Reply(sb.ToString());
    }

    [Command("clear", description: "Prune collected ACTIVITY older than N days (sessions + concurrency). Usage: .faust admin data clear <days>", adminOnly: true)]
    public static void Clear(ChatCommandContext ctx, int days)
    {
        if (days <= 0)
        {
            ctx.Reply("Give a positive day count, e.g. '.faust admin data clear 90'. " +
                      "(To erase everything, use '.faust admin data wipe activity confirm'.)");
            return;
        }
        int removed = Core.Store.ClearOlderThan(days);
        ctx.Reply($"Pruned {removed} activity record(s) older than {days} day(s). " +
                  "Playtime / first-seen / charts now reflect the trimmed window.");
    }

    [Command("wipe", description: "Erase a collected-data store. Usage: .faust admin data wipe <activity|unlocks|usage|all> confirm", adminOnly: true)]
    public static void Wipe(ChatCommandContext ctx, string store, string confirm = null)
    {
        store = store?.ToLowerInvariant();
        if (store is not ("activity" or "unlocks" or "usage" or "all"))
        {
            ctx.Reply("Specify what to wipe: activity | unlocks | usage | all.");
            return;
        }

        bool confirmed = string.Equals(confirm, "confirm", StringComparison.OrdinalIgnoreCase);
        if (!confirmed)
        {
            ctx.Reply($"This ERASES the '{store}' store and cannot be undone — " +
                      $"{PreviewFor(store)}. Re-run as '.faust admin data wipe {store} confirm' to proceed.");
            return;
        }

        var sb = new StringBuilder("Wiped");
        if (store is "activity" or "all")
            sb.Append($" {Core.Store.WipeAll()} activity record(s);");
        if (store is "unlocks" or "all")
            sb.Append($" unlock progress for {Core.Unlock.WipeAll()} player(s);");
        if (store is "usage" or "all")
            sb.Append($" {Core.Usage.WipeAll()} usage lock(s);");
        sb.Append(" done.");
        ctx.Reply(sb.ToString());
    }

    static string PreviewFor(string store)
    {
        var (sessions, conc, names, _) = Core.Store.GetStorageStats();
        var (players, _, grants) = Core.Unlock.GetUnlockStats();
        return store switch
        {
            "activity" => $"{sessions} session(s) + {conc} concurrency point(s) + {names} name(s) (playtime/charts reset)",
            "unlocks" => $"{players} player(s)' V-blood defeats + {grants} grant(s) (progression gates reset)",
            "usage" => $"{Core.Usage.UsageCount} cooldown/window lock(s)",
            _ => "ALL collected data: activity, unlock progress, and usage locks",
        };
    }

    static string FmtDate(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd") + " UTC";
}

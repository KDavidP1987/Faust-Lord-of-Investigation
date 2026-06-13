using VampireCommandFramework;

namespace Faust.Commands;

/// <summary>
/// Nested in-game help so players without the BloodCraftHub UI can discover features without a
/// wall of text. Topic tree mirrors the feature groups (design §5). Most features are not yet
/// implemented — this stub names the foundation surface and the planned groups so the help tree
/// grows alongside the features rather than being bolted on later.
/// </summary>
[CommandGroup("faust")]
internal static class HelpCommands
{
    [Command("help", description: "Faust help menu. Usage: .faust help [players|castles|server|admin]")]
    public static void Help(ChatCommandContext ctx, string topic = null)
    {
        switch (topic?.ToLowerInvariant())
        {
            case "players":
                ctx.Reply("PLAYERS: '.faust api pinfo <name|steamId>' — online state & last-online (yourself " +
                          "always; others admin-gated). Playtime/frequency stats arrive with session tracking. " +
                          "'.faust api positions' — online players' locations (admin-default).");
                break;
            case "castles":
                ctx.Reply("CASTLES: '.faust api castleinfo <here|nearest|tindex>' — owner, region, size, decay " +
                          "state & time, owner online/last-online. '.faust api plots [page]' — open plots, largest " +
                          "first. '.faust api castles [page]' — every territory, full map (admin-default). " +
                          "'.faust api decay [page]' — claimed castles by soonest decay (admin-default). " +
                          "'.faust api resources <here|nearest|tindex>' — total resources in a castle (admin-default).");
                break;
            case "server":
                ctx.Reply("SERVER: '.faust api stats playtime' — top players by total playtime; " +
                          "'.faust api stats concurrency' — population history; '.faust api stats hours " +
                          "[player]' — activity by hour-of-day; 'stats weekdays [player]' — by day of week; " +
                          "'stats daily [days]' — DAU + play-minutes; 'stats newplayers [days]' — first-seen " +
                          "growth; 'stats sessions [player]' — session-length spread; 'stats pdaily <player> " +
                          "[days]' — one player's daily trend; 'stats population' — DAU/WAU/MAU + retention; " +
                          "'stats recency' — active vs dormant; 'stats peak' — concurrency peak/avg; " +
                          "'stats regions' — players+castles per region; 'stats players' — per-player " +
                          "activity roster; '.faust api clans' — clanned vs independent + rosters (BCH charts). " +
                          "'.faust api kills [days]' — top killers; 'bosskills [days]' — V Blood defeat counts " +
                          "(days 0 = all-time). '.faust api worldscan [type=units|nodes,bloodqmin=N]' — map of " +
                          "NPC units (with blood type/quality) + resource nodes, filtered (admin-curated whitelist).");
                break;
            case "admin":
                ctx.Reply("ADMIN — live config (immediate + persisted, no restart): '.faust admin set " +
                          "<feature> <setting=value[,setting=value]>' (no spaces) & 'get <feature> [setting]' for per-feature " +
                          "access/delivery/costitem/costqty/cooldown/window/period/maxuses/availability/" +
                          "unlock/nearprefab/neardist/adminsexempt; 'setglobal/getglobal' for global " +
                          "settings; 'resetcfg <feature|global> [setting]' restores defaults.\n" +
                          "Operational (no restart): '.faust admin block <feature|all> [minutes]', 'unblock', " +
                          "'schedule <feature|all> <HH:MM-HH:MM|clear>', 'status', 'grant|revoke <player> " +
                          "<feature>', 'unlocks <player>'. Helpers: 'prefab <id|nameFragment>' looks up prefab " +
                          "IDs/names for commands; 'worldscan <list|add|remove|seed>' curates the asset map. " +
                          "Data: '.faust admin data status', 'data clear <days>', " +
                          "'data wipe <activity|unlocks|usage|kills|heatmap|all> confirm'. '.faust api version' reports each " +
                          "feature's resolved access + price.");
                break;
            default:
                ctx.Reply("Faust help — topics: '.faust help players | castles | server | admin'.\n" +
                          "Live queries: '.faust api castleinfo|plots|pinfo|positions|bosses', plus the BCH " +
                          "handshake '.faust api version' / 'ping'. '.faust api bosses' / 'boss <name>' — V Blood " +
                          "status (where they are, health, up/down/defeated). More per docs/FAUST_DESIGN.md.");
                break;
        }
    }
}

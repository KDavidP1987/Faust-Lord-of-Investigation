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
                          "state & time, owner online/last-online. '.faust api plots [page]' — open plots, largest first.");
                break;
            case "server":
                ctx.Reply("SERVER: '.faust api stats playtime' — top players by total playtime; " +
                          "'.faust api stats concurrency' — server population history (BCH graphs). " +
                          "Kills/resource leaderboards are planned.");
                break;
            case "admin":
                ctx.Reply("ADMIN: per-feature exposure (Off/AdminOnly/Players), item cost, and cooldown live in " +
                          "BepInEx/config/kdpen.Faust.cfg. '.faust api version' reports each feature's resolved " +
                          "access + price.");
                break;
            default:
                ctx.Reply("Faust help — topics: '.faust help players | castles | server | admin'.\n" +
                          "Live queries: '.faust api castleinfo|plots|pinfo|positions', plus the BCH handshake " +
                          "'.faust api version' / 'ping'. More per docs/FAUST_DESIGN.md build order.");
                break;
        }
    }
}

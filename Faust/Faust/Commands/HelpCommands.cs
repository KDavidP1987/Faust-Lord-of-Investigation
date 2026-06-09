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
                ctx.Reply("PLAYERS (planned): '.faust api pinfo <name|steamId>' — last login, frequency, " +
                          "playtime, peak hours. Position intel is admin-default.");
                break;
            case "castles":
                ctx.Reply("CASTLES (planned): '.faust api castleinfo <here|nearest|tindex>' — owner, heart " +
                          "level, decay/raidable state, last-online. '.faust api plots' — free plots, largest first.");
                break;
            case "server":
                ctx.Reply("SERVER (planned): '.faust api stats <players|kills|playtime|concurrency>' — " +
                          "leaderboards & time-series for BloodCraftHub graphs.");
                break;
            case "admin":
                ctx.Reply("ADMIN: per-feature exposure (Off/AdminOnly/Players), item cost, and cooldown live in " +
                          "BepInEx/config/kdpen.Faust.cfg. '.faust api version' reports each feature's resolved " +
                          "access + price.");
                break;
            default:
                ctx.Reply("Faust help — topics: '.faust help players | castles | server | admin'.\n" +
                          "Foundation stage: only the access gate + BCH handshake are live " +
                          "('.faust api version', '.faust api ping'). Feature queries are being built per " +
                          "docs/FAUST_DESIGN.md build order.");
                break;
        }
    }
}

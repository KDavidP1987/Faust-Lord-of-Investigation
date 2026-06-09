using VampireCommandFramework;

namespace Faust.Commands;

/// <summary>
/// Top-level `.faust` command (no subcommand) prints a short overview so players who don't know
/// the syntax aren't dropped into a wall of help text. Lives outside any [CommandGroup] so VCF
/// resolves bare `.faust` here while `.faust <subcommand>` still routes into the feature groups.
/// </summary>
internal static class RootCommands
{
    [Command("faust", description: "Faust overview — what the mod does and how to get help.")]
    public static void Faust(ChatCommandContext ctx)
    {
        ctx.Reply(
            $"Faust, Lord of Investigation - on-demand intel about players, castles, plots, and the " +
            $"server (v{MyPluginInfo.PLUGIN_VERSION}). Knowledge has a price: admins gate each query and " +
            "may charge an item cost.\n" +
            "Best used through the BloodCraftHub UI; also works as chat commands.\n" +
            "Type '.faust help' for the command menu. Foundation stage: '.faust api version' + '.faust api ping'.");
    }
}

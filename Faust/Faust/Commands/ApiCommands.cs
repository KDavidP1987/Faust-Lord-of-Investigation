using System.Text;
using Faust.Config;
using Faust.Services;
using VampireCommandFramework;

namespace Faust.Commands;

/// <summary>
/// Structured chat output for BloodCraftHub (BCH) — the [FAUST:*] wire API
/// (docs/BCH_INTEGRATION_CONTRACT.md, mirroring Uriel's [URIEL:*] / Beelzebub's [BEELZ:*]).
///
/// Wire rule: ONE ctx.Reply per [FAUST:*] line — BCH reads each System-chat message as a single
/// wire line and does NOT split on '\n'. Paged lists = N row-replies + a [FAUST:end] trailer.
///
/// ApiVersion 1 (foundation): the handshake (.faust api version) advertises each feature's
/// resolved access + cost so BCH can gate its UI and show the price without a round-trip, plus a
/// trivial ping probe to prove the round-trip end to end. Feature query commands land as their
/// services are built (design build order); bump ApiVersion whenever the wire grows.
/// </summary>
[CommandGroup("faust api")]
internal static class ApiCommands
{
    const int ApiVersion = 1;

    // The feature tokens advertised in the handshake, in a stable order (design §2/§5).
    static readonly string[] FeatureOrder =
    {
        Settings.PlayerPositions, Settings.CastleInfo, Settings.PlayerInfo,
        Settings.PlotAvailability, Settings.ObjectScan, Settings.CastleResources, Settings.Stats,
    };

    [Command("version", description: "BCH handshake: ApiVersion, ready flag, and each feature's resolved access + cost.")]
    public static void Version(ChatCommandContext ctx)
    {
        bool ready = Core.IsReady && Settings.Enabled.Value;
        var isAdmin = ctx.Event.User.IsAdmin;

        var sb = new StringBuilder();
        sb.Append("[FAUST:version] ")
          .Append($"api={ApiVersion} ")
          .Append($"plugin={MyPluginInfo.PLUGIN_VERSION} ")
          .Append($"ready={(ready ? 1 : 0)}");

        foreach (var key in FeatureOrder)
        {
            var f = Settings.Feature(key);
            if (f is null) continue;
            sb.Append(' ').Append(key).Append('=')
              .Append(FaustAccessGate.AccessToken(f, isAdmin)).Append(':')
              .Append(FaustAccessGate.CostToken(f));
        }

        ctx.Reply(sb.ToString());
    }

    [Command("ping", description: "BCH probe: proves the Faust->BCH round-trip end to end.")]
    public static void Ping(ChatCommandContext ctx)
    {
        if (!Core.IsReady) { ctx.Reply("[FAUST:err] code=notready"); return; }
        ctx.Reply($"[FAUST:pong] api={ApiVersion} plugin={MyPluginInfo.PLUGIN_VERSION}");
    }
}

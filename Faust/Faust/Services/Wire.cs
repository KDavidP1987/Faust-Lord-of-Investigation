using System.Collections.Generic;
using System.Text;
using VampireCommandFramework;

namespace Faust.Services;

/// <summary>
/// Helpers for the [FAUST:*] wire (docs/BCH_INTEGRATION_CONTRACT.md). Two hard rules live here so
/// every feature obeys them by construction:
///   1. ONE ctx.Reply per wire line — BCH reads each System-chat message as a single line and does
///      NOT split on '\n'. <see cref="SendPage"/> emits each row as its own reply.
///   2. Token values are wire-safe — no spaces (use '_'), no '=' ';' ':' inside a value.
///      <see cref="Safe"/> sanitizes free text (names, regions) before it goes on the wire.
/// </summary>
internal static class Wire
{
    /// <summary>Make a free-text value wire-safe: spaces -> '_', strip delimiter chars.</summary>
    public static string Safe(string value)
    {
        if (string.IsNullOrEmpty(value)) return "_";
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == ' ') sb.Append('_');
            else if (c == '=' || c == ';' || c == ':' || c == '\n' || c == '\r' || c == '\t') { /* drop */ }
            else sb.Append(c);
        }
        return sb.Length == 0 ? "_" : sb.ToString();
    }

    /// <summary>
    /// Page a list of pre-built row strings (1-based paging) and send the page: one ctx.Reply per
    /// row, then a "[FAUST:end] cmd=&lt;cmd&gt; page=&lt;cur&gt;/&lt;total&gt; count=&lt;n&gt;" trailer. Returns true if the
    /// page produced at least one row (so the caller can Commit the gate only on a real result).
    /// </summary>
    public static bool SendPage(ChatCommandContext ctx, string cmd, IReadOnlyList<string> rows, int page, int pageSize = 20)
    {
        int total = rows.Count == 0 ? 1 : (rows.Count + pageSize - 1) / pageSize;
        if (page < 1) page = 1;
        if (page > total) page = total;

        int start = (page - 1) * pageSize;
        int end = System.Math.Min(start + pageSize, rows.Count);
        for (int i = start; i < end; i++) ctx.Reply(rows[i]);

        ctx.Reply($"[FAUST:end] cmd={cmd} page={page}/{total} count={rows.Count}");
        return rows.Count > 0;
    }
}

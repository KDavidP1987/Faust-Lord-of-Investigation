using System.IO;
using System.Text;
using Faust.Config;

namespace Faust.Services;

/// <summary>
/// Single source of truth for where Faust keeps its persisted data. Everything lives under
/// BepInEx/config/Faust/, optionally scoped to a per-world subfolder via the <c>DataNamespace</c>
/// config (design: data is server-scoped and survives a world wipe by default — the same players
/// return — so separation is opt-in, and clearing is an explicit admin action, not automatic).
///
/// Read at service Load() time, which runs after Settings.Initialize (Plugin.Load order), so the
/// namespace is known. Changing it starts a fresh dataset; the old folder is left on disk.
/// </summary>
internal static class FaustPaths
{
    static string BaseDir => Path.Combine(BepInEx.Paths.ConfigPath, "Faust");

    /// <summary>The active data directory: the base folder, or a namespaced subfolder when set.</summary>
    public static string DataDir
    {
        get
        {
            var ns = Settings.DataNamespace?.Value;
            return string.IsNullOrWhiteSpace(ns) ? BaseDir : Path.Combine(BaseDir, SafeFolder(ns));
        }
    }

    /// <summary>True if a per-world DataNamespace is in effect (status readout / docs).</summary>
    public static bool IsNamespaced => !string.IsNullOrWhiteSpace(Settings.DataNamespace?.Value);

    /// <summary>Sanitize a namespace into a safe folder name (letters/digits/-/_, else '_').</summary>
    static string SafeFolder(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.Length == 0 ? "data" : sb.ToString();
    }

    /// <summary>Approx total size of Faust's JSON data files (for the data-status readout). Best-effort.</summary>
    public static long TotalDataBytes()
    {
        long total = 0;
        try
        {
            var dir = DataDir;
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                    total += new FileInfo(f).Length;
        }
        catch { /* status is best-effort */ }
        return total;
    }
}

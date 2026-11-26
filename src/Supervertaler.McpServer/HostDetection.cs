namespace Supervertaler.McpServer;

/// <summary>
/// Which application this exe serves (Trados, memoQ or Workbench), and where
/// the shared data folder is, without depending on the environment alone.
///
/// <para>Why: on 26 Nov 2026 ChatGPT desktop (Windows, Codex-based) started the
/// memoQ entry WITHOUT the <c>SUPERVERTALER_HOST=memoq</c> its config gave it,
/// so the exe served Trados: it offered the Trados tools and answered "Trados
/// Studio is not running" while memoQ was open. Seen on an ARM machine and,
/// in another form, on an x64 one; both have ChatGPT's elevated Windows
/// sandbox on. The exe now also works it out from where it is installed.
/// ChatGptMcpSetup installs it into &lt;data folder&gt;\&lt;host&gt;\mcp\, and the
/// path ChatGPT launches it by cannot be lost on the way.</para>
///
/// <para>Order: <c>--host</c> on the command line, then the environment
/// variable, then the install folder, then Trados. An explicit setting
/// always beats an inference. An exe somewhere else, such as a Claude Desktop
/// extension folder, a build output or a test, never matches a folder and
/// behaves exactly as before.</para>
/// </summary>
public static class HostDetection
{
    public static readonly string[] KnownHosts = { "trados", "memoq", "workbench" };

    /// <summary>Set once from <c>--host</c> at startup; null when not given.</summary>
    public static string? HostArgument { get; set; }

    /// <summary>The host and what decided it, for the startup diagnostic.</summary>
    public static (string Host, string Source) Decide(string? argument, string? environment, string? exePath)
    {
        if (Normalise(argument) is { } a) return (a, "--host argument");
        if (Normalise(environment) is { } e) return (e, "SUPERVERTALER_HOST");
        if (FromInstallFolder(exePath) is { } f) return (f.Host, "install folder");
        return ("trados", "default");
    }

    /// <summary>
    /// The host and data folder implied by an exe at &lt;root&gt;\&lt;host&gt;\mcp\,
    /// or null when the exe is anywhere else.
    /// </summary>
    public static (string Host, string Root)? FromInstallFolder(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        try
        {
            var mcpDir = Path.GetDirectoryName(Path.GetFullPath(exePath));
            if (mcpDir == null || !string.Equals(Path.GetFileName(mcpDir), "mcp", StringComparison.OrdinalIgnoreCase))
                return null;

            var hostDir = Path.GetDirectoryName(mcpDir);
            var host = Normalise(hostDir == null ? null : Path.GetFileName(hostDir));
            var root = hostDir == null ? null : Path.GetDirectoryName(hostDir);
            return host != null && !string.IsNullOrEmpty(root) ? (host, root!) : null;
        }
        catch
        {
            // A path Windows cannot parse is simply not an install folder.
            return null;
        }
    }

    /// <summary>A known host name in lower case, or null for anything else.</summary>
    private static string? Normalise(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return !string.IsNullOrEmpty(v) && KnownHosts.Contains(v) ? v : null;
    }
}

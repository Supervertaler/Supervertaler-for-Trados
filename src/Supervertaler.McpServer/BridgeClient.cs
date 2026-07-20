using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Supervertaler.McpServer;

/// <summary>
/// Talks to the Supervertaler for Trados bridge (localhost HTTP inside the
/// Trados Studio process). Discovery mirrors the plugin's UserDataPath:
///   1. Resolve the shared user-data root from %APPDATA%\Supervertaler\config.json
///      (key "user_data_path"); fall back to %USERPROFILE%\Supervertaler.
///   2. Read the handshake file at &lt;root&gt;\trados\runtime\bridge.json
///      ({version, port, token, pid, startedAt}).
///   3. Verify the PID is alive (stale handshakes survive hard kills).
///   4. Send requests to http://127.0.0.1:&lt;port&gt; with the bearer token.
///
/// The handshake is re-read on every call: Trados may start/stop between
/// tool calls, and ports/tokens change per session.
/// </summary>
public sealed class BridgeClient
{
    /// <summary>
    /// Exe protocol level, sent to the bridge on every request so the plugin can
    /// tell whether this exe supports the features it needs. NOT the marketing
    /// version: bump only when the exe's own machinery changes (forwarding
    /// semantics, MCP capabilities, discovery/auth). History:
    ///   (no header) = pre-handshake exes (dynamic tools + list_changed or older)
    ///   2 = adds this version header
    /// The plugin compares against its RequiredExeVersion and, when this exe is
    /// too old, tells the AI to tell the user to update the extension.
    /// </summary>
    public const int ExeProtocolVersion = 2;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Set by BridgeLocator when a custom handshake path is passed via SUPERVERTALER_BRIDGE_FILE (tests).</summary>
    private static string? HandshakeOverride => Environment.GetEnvironmentVariable("SUPERVERTALER_BRIDGE_FILE");

    private sealed record Handshake(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("pid")] int Pid,
        [property: JsonPropertyName("startedAt")] string? StartedAt);

    public async Task<string> GetAsync(string path, CancellationToken ct = default)
    {
        var (baseUrl, token) = Discover();
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Supervertaler-Mcp-Exe-Version", ExeProtocolVersion.ToString());
        return await SendAsync(req, ct);
    }

    public async Task<string> PostAsync(string path, object body, CancellationToken ct = default)
    {
        var (baseUrl, token) = Discover();
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Supervertaler-Mcp-Exe-Version", ExeProtocolVersion.ToString());
        return await SendAsync(req, ct);
    }

    private static async Task<string> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new BridgeUnavailableException(
                "Could not reach the Supervertaler bridge inside Trados Studio " +
                $"({ex.Message}). Is Trados Studio running with the Supervertaler plugin enabled?");
        }

        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new BridgeUnavailableException(
                $"Bridge returned HTTP {(int)resp.StatusCode}: {Truncate(text, 500)}");
        }
        return text;
    }

    private (string BaseUrl, string Token) Discover()
    {
        var path = ResolveHandshakePath();
        if (path == null || !File.Exists(path))
        {
            throw new BridgeUnavailableException(
                "Supervertaler bridge handshake file not found. Start Trados Studio, open a project " +
                "in the editor, and make sure the Supervertaler for Trados plugin is installed with " +
                "the bridge enabled (Supervertaler settings > AI Assistant).");
        }

        Handshake? hs;
        try
        {
            hs = JsonSerializer.Deserialize<Handshake>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            throw new BridgeUnavailableException($"Bridge handshake file is unreadable: {ex.Message}");
        }

        if (hs == null || hs.Port <= 0 || string.IsNullOrEmpty(hs.Token))
            throw new BridgeUnavailableException("Bridge handshake file is malformed.");

        if (!IsPidAlive(hs.Pid))
        {
            throw new BridgeUnavailableException(
                "Found a bridge handshake file, but the Trados Studio process that wrote it is no longer " +
                "running (stale handshake). Start Trados Studio and try again.");
        }

        return ($"http://127.0.0.1:{hs.Port}", hs.Token);
    }

    private static string? ResolveHandshakePath()
    {
        if (!string.IsNullOrEmpty(HandshakeOverride))
            return HandshakeOverride;

        // Default root matches the plugin's UserDataPath.DefaultRoot (~\Supervertaler);
        // %APPDATA%\Supervertaler\config.json may point elsewhere via "user_data_path".
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, "Supervertaler");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configPath = Path.Combine(appData, "Supervertaler", "config.json");
        if (File.Exists(configPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("user_data_path", out var loc)
                    && loc.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(loc.GetString()))
                {
                    root = loc.GetString()!;
                }
            }
            catch
            {
                // Unreadable config – stay on the default root.
            }
        }

        return Path.Combine(root, "trados", "runtime", "bridge.json");
    }

    private static bool IsPidAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Thrown when the bridge can't be reached; the message is user-facing (shown to the AI).</summary>
public sealed class BridgeUnavailableException : Exception
{
    public BridgeUnavailableException(string message) : base(message) { }
}

using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Supervertaler.Core;

namespace Supervertaler.Trados.Licensing
{
    /// <summary>
    /// Server-side trial registration (public issue #47) – observe-only phase.
    ///
    /// On startup without a licence key, one lightweight call registers this
    /// machine's trial with the licence server. The server records the
    /// authoritative trial start date on FIRST contact (its own clock) and
    /// returns that same original date on every later contact, so a wiped
    /// data folder or registry cannot invent a fresh start date server-side.
    ///
    /// In this release the server's answer is only LOGGED, not enforced –
    /// local trial behaviour is completely unchanged (the registry anchor
    /// from v4.20.34–36 remains the enforcement mechanism). A later release
    /// flips to "server value → signed local cache → license.json, earliest
    /// wins" with a 14-day offline-grace window, once real-world behaviour
    /// has been observed on the dashboard.
    ///
    /// Privacy: the only identifier sent is the same SHA-256 machine
    /// fingerprint already used for licence activation. No document content,
    /// no account data, no raw hardware identifiers. Fire-and-forget with a
    /// short timeout; every failure is silent – a legitimate user is never
    /// blocked, delayed, or nagged by this call.
    /// </summary>
    internal static class TrialRegistration
    {
        private static readonly HttpClient _http = new HttpClient();
        private const string RegisterUrl =
            "https://supervertaler-stats.michaelbeijer-co-uk.workers.dev/trial/register";

        static TrialRegistration()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Supervertaler-Trados/1.0");
            _http.Timeout = TimeSpan.FromSeconds(10);
        }

        /// <summary>
        /// Registers this trial install with the licence server. Call from a
        /// background task on startup when no licence key is present. Never
        /// throws; never blocks the UI.
        /// </summary>
        /// <param name="afterFirstAiRequest">This check-in is the one sent after
        /// the session's first successful AI request (see
        /// <see cref="LicenseManager.NoteAiRequest"/>), not the one at startup.
        /// The server does not count it as another start of Studio.</param>
        public static async Task RegisterAsync(DateTime trialStartedAtUtc, bool trialActive, bool afterFirstAiRequest = false)
        {
            try
            {
                // Whether the AI has been used is analytics, not part of how the
                // trial works, so it goes only with the anonymous usage statistics
                // switched on (Settings > General). Off: nothing about it is sent.
                var settings = Settings.SettingsService.Current;
                int? aiRequests = null;
                string firstAiDay = null;
                if (settings?.UsageStatisticsEnabled == true)
                {
                    aiRequests = TrialAiUse.Count(Settings.UserDataPath.UsageDir,
                        settings.AiSettings?.IsUsageLogEnabled ?? true, out firstAiDay);
                    // Sent straight after a successful request whose ledger line may
                    // not be written yet (the ledger has its own handler): at least one.
                    if (afterFirstAiRequest && aiRequests < 1)
                    {
                        aiRequests = 1;
                        firstAiDay = firstAiDay ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    }
                }
                var payload = new TrialRegisterPayload
                {
                    AiRequests = aiRequests,
                    FirstAiDay = firstAiDay,
                    Event = afterFirstAiRequest ? "ai" : null,
                    Fingerprint = MachineId.GetFingerprint(),
                    PluginVersion = GetPluginVersion(),
                    StudioVersion = GetStudioVersion(),
                    Locale = CultureInfo.CurrentUICulture.Name,
                    ClaimedStart = trialStartedAtUtc == DateTime.MinValue
                        ? null
                        : trialStartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    Status = trialStartedAtUtc == DateTime.MinValue
                        ? "new"
                        : (trialActive ? "trial" : "expired"),
                };

                string json;
                using (var stream = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(TrialRegisterPayload)).WriteObject(stream, payload);
                    json = Encoding.UTF8.GetString(stream.ToArray());
                }

                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(RegisterUrl, content).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                // Observe-only: record the server's authoritative answer in the
                // bridge log for diagnostics. Not used for trial computation yet.
                var serverStart = ExtractJsonField(body, "trial_started_at");
                if (!string.IsNullOrEmpty(serverStart))
                    Core.BridgeLog.Write($"[TrialRegistration] server trial_started_at={serverStart} (local claimed={payload.ClaimedStart ?? "none"})");
            }
            catch
            {
                // Silent failure – no retries, no queuing, no error messages.
                // The trial keeps working exactly as before (fails open).
            }
        }

        private static string GetPluginVersion()
        {
            try { return Core.UpdateChecker.GetCurrentVersion() ?? "unknown"; }
            catch { return "unknown"; }
        }

        private static string GetStudioVersion()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetEntryAssembly();
                var v = asm?.GetName().Version;
                return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "unknown";
            }
            catch { return "unknown"; }
        }

        private static string ExtractJsonField(string json, string field)
        {
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    json ?? "", "\"" + field + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        [DataContract]
        private class TrialRegisterPayload
        {
            [DataMember(Name = "fingerprint")]
            public string Fingerprint { get; set; }

            [DataMember(Name = "plugin_version")]
            public string PluginVersion { get; set; }

            [DataMember(Name = "studio_version")]
            public string StudioVersion { get; set; }

            [DataMember(Name = "locale")]
            public string Locale { get; set; }

            [DataMember(Name = "claimed_start", EmitDefaultValue = false)]
            public string ClaimedStart { get; set; }

            [DataMember(Name = "status")]
            public string Status { get; set; }

            /// <summary>Successful AI requests in the usage ledger, up to 100;
            /// -1 when unknown (ledger switched off). Null, and not sent, with the
            /// usage statistics switched off. See <see cref="TrialAiUse"/>.</summary>
            [DataMember(Name = "ai_requests", EmitDefaultValue = false)]
            public int? AiRequests { get; set; }

            /// <summary>The UTC day of the first one (yyyy-MM-dd), if any.</summary>
            [DataMember(Name = "first_ai_day", EmitDefaultValue = false)]
            public string FirstAiDay { get; set; }

            /// <summary>"ai" for the check-in after the session's first AI request.</summary>
            [DataMember(Name = "event", EmitDefaultValue = false)]
            public string Event { get; set; }
        }
    }
}

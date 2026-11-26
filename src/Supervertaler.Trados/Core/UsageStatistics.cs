using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using Supervertaler.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Minimal anonymous usage statistics: on by default, switched off by the
    /// first-start notice's "Turn it off" or in Settings (see
    /// UsageStatisticsDialog).
    ///
    /// While on, a single lightweight ping is sent once per session on plugin
    /// startup. The payload (UsagePing) contains only:
    ///   - A random anonymous ID (UUID, generated locally, not tied to any account)
    ///   - The product ("trados")
    ///   - Plugin version, OS version, Trados Studio version
    ///   - System locale
    ///   - The VM host, if any ("parallels" means a Mac), and the process architecture
    ///   - Windows display scaling, Windows text size and the Supervertaler UI scale
    ///
    /// A field added here must also be added to the notice's text, the help
    /// page (trados/settings/usage-statistics) and supervertaler.com/privacy -
    /// until 18/19.20.198 all three left out the last two lines.
    ///
    /// No personal data, no translation content, no termbase info, no tracking.
    /// Silent failure – if the ping fails, nothing happens.
    /// </summary>
    internal static class UsageStatistics
    {
        private static readonly HttpClient _http = new HttpClient();
        private const string PingUrl = "https://supervertaler-stats.michaelbeijer-co-uk.workers.dev/ping";

        static UsageStatistics()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Supervertaler-Trados/1.0");
            _http.Timeout = TimeSpan.FromSeconds(10);
        }

        /// <summary>
        /// Sends the anonymous usage ping if the user has opted in.
        /// Call from a background task – never blocks the UI.
        /// Swallows all exceptions silently.
        /// </summary>
        public static async Task SendPingAsync()
        {
            try
            {
                // Only send if explicitly opted in
                if (!SettingsService.Current.UsageStatisticsEnabled)
                    return;

                // Get or create the anonymous ID, deciding and writing under one
                // lock. Read-test-write across a separate load and save let two
                // callers both find it missing and mint a different id each,
                // which would have split one install across two identities.
                string anonymousId = null;
                SettingsService.UpdateIf(s =>
                {
                    if (!string.IsNullOrEmpty(s.UsageStatisticsId))
                    {
                        anonymousId = s.UsageStatisticsId;
                        return false;
                    }
                    anonymousId = Guid.NewGuid().ToString("D");
                    s.UsageStatisticsId = anonymousId;
                    return true;
                });

                var payload = new UsagePing
                {
                    AnonymousId = anonymousId,
                    Product = "trados",
                    PluginVersion = GetPluginVersion(),
                    OsVersion = GetOsVersion(),
                    TradosVersion = GetTradosVersion(),
                    Locale = CultureInfo.CurrentUICulture.Name,
                    VirtualizationHost = MachineInfo.VirtualizationHost(),
                    // The computer's processor, not Studio's: under Windows on
                    // ARM (an Apple Silicon Mac) Studio is emulated and would
                    // be told x86 or amd64.
                    ProcessArch = MachineInfo.NativeArchitecture(),
                    DisplayScale = GetDisplayScale(),
                    TextScale = GetTextScale(),
                    UiScalePercent = FormatScalePercent(SettingsService.Current.UiScaleFactor)
                };

                var json = SerializePayload(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Fire and forget – we don't care about the response
                await _http.PostAsync(PingUrl, content).ConfigureAwait(false);
            }
            catch
            {
                // Silent failure – no retries, no queuing, no error messages
            }
        }

        /// <summary>
        /// Returns the plugin version from the assembly's InformationalVersion attribute.
        /// </summary>
        private static string GetPluginVersion()
        {
            try
            {
                return UpdateChecker.GetCurrentVersion() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>
        /// Returns a human-readable OS version string, e.g. "Windows 11 23H2 (10.0.22631)".
        /// </summary>
        private static string GetOsVersion()
        {
            try
            {
                var os = Environment.OSVersion;
                return $"{os.Platform} {os.Version} ({os.ServicePack})".Trim();
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>
        /// Attempts to detect the Trados Studio version from the host process.
        /// Falls back to "unknown" if detection fails.
        /// </summary>
        private static string GetTradosVersion()
        {
            try
            {
                // Try to get the Trados Studio main assembly version
                var tradosAsm = Assembly.GetEntryAssembly();
                if (tradosAsm != null)
                {
                    var v = tradosAsm.GetName().Version;
                    if (v != null)
                        return $"{v.Major}.{v.Minor}.{v.Build}";
                }

                // Fallback: check loaded assemblies for Sdl.Desktop.IntegrationApi
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "Sdl.Desktop.IntegrationApi")
                    {
                        var v = asm.GetName().Version;
                        if (v != null)
                            return $"Studio {v.Major}.{v.Minor}.{v.Build}";
                    }
                }
            }
            catch { }

            return "unknown";
        }

        /// <summary>
        /// Windows display scale as a percentage string (e.g. "100", "150", "175"),
        /// from the primary-screen DPI. Lets us see what share of users run high-DPI
        /// scaling without sending anything identifying. "unknown" if it can't be read.
        /// </summary>
        private static string GetDisplayScale()
        {
            try
            {
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                {
                    var pct = (int)Math.Round(g.DpiX / 96f * 100f);
                    return (pct < 50 || pct > 600) ? "unknown" : pct.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch { return "unknown"; }
        }

        /// <summary>
        /// Windows accessibility "Text size" as a percentage string (e.g. "100",
        /// "175"), from HKCU\Software\Microsoft\Accessibility\TextScaleFactor.
        /// Absent = 100 (the default). A separate knob from display scaling.
        /// </summary>
        private static string GetTextScale()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility"))
                {
                    var val = key?.GetValue("TextScaleFactor");
                    if (val == null) return "100";
                    var pct = Convert.ToInt32(val);
                    return (pct < 50 || pct > 600) ? "unknown" : pct.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch { return "unknown"; }
        }

        /// <summary>
        /// The in-app Supervertaler UI-scale slider as a percentage string
        /// (e.g. "70", "100", "150").
        /// </summary>
        private static string FormatScalePercent(float factor)
        {
            var pct = (int)Math.Round(factor * 100f);
            return (pct < 10 || pct > 600) ? "unknown" : pct.ToString(CultureInfo.InvariantCulture);
        }

        private static string SerializePayload(UsagePing ping)
        {
            using (var stream = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(UsagePing));
                serializer.WriteObject(stream, ping);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        [DataContract]
        private class UsagePing
        {
            [DataMember(Name = "id")]
            public string AnonymousId { get; set; }

            [DataMember(Name = "product")]
            public string Product { get; set; }

            [DataMember(Name = "plugin_version")]
            public string PluginVersion { get; set; }

            [DataMember(Name = "os_version")]
            public string OsVersion { get; set; }

            [DataMember(Name = "trados_version")]
            public string TradosVersion { get; set; }

            [DataMember(Name = "locale")]
            public string Locale { get; set; }

            [DataMember(Name = "vm")]
            public string VirtualizationHost { get; set; }

            [DataMember(Name = "arch")]
            public string ProcessArch { get; set; }

            [DataMember(Name = "display_scale")]
            public string DisplayScale { get; set; }

            [DataMember(Name = "text_scale")]
            public string TextScale { get; set; }

            [DataMember(Name = "ui_scale")]
            public string UiScalePercent { get; set; }
        }
    }
}

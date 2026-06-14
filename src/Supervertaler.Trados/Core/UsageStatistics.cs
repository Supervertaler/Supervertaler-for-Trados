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
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Minimal, opt-in anonymous usage statistics.
    ///
    /// When the user opts in, a single lightweight ping is sent once per session
    /// on plugin startup. The payload contains only:
    ///   - A random anonymous ID (UUID, generated locally, not tied to any account)
    ///   - Plugin version
    ///   - OS version
    ///   - Trados Studio version
    ///   - System locale
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
                var settings = TermLensSettings.Load();

                // Only send if explicitly opted in
                if (!settings.UsageStatisticsEnabled)
                    return;

                // Get or create the anonymous ID
                var anonymousId = settings.UsageStatisticsId;
                if (string.IsNullOrEmpty(anonymousId))
                {
                    anonymousId = Guid.NewGuid().ToString("D");
                    settings.UsageStatisticsId = anonymousId;
                    settings.Save();
                }

                var payload = new UsagePing
                {
                    AnonymousId = anonymousId,
                    Product = "trados",
                    PluginVersion = GetPluginVersion(),
                    OsVersion = GetOsVersion(),
                    TradosVersion = GetTradosVersion(),
                    Locale = CultureInfo.CurrentUICulture.Name,
                    VirtualizationHost = DetectVirtualization(),
                    ProcessArch = (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "unknown").ToLowerInvariant(),
                    DisplayScale = GetDisplayScale()
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
        /// Detects if the Windows instance is running inside a VM, and which hypervisor.
        /// Returns "parallels", "vmware", "virtualbox", "hyper-v", or "none".
        /// Parallels detection indicates the user is running on a Mac.
        /// </summary>
        private static string DetectVirtualization()
        {
            try
            {
                // Check SMBIOS system manufacturer / product name via registry
                // These are populated by the hypervisor's virtual BIOS
                using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                {
                    if (key != null)
                    {
                        var manufacturer = (key.GetValue("SystemManufacturer") as string ?? "").ToLowerInvariant();
                        var product = (key.GetValue("SystemProductName") as string ?? "").ToLowerInvariant();
                        var combined = manufacturer + " " + product;

                        if (combined.Contains("parallels"))
                            return "parallels";  // Running on macOS via Parallels
                        if (combined.Contains("vmware"))
                            return "vmware";
                        if (combined.Contains("virtualbox"))
                            return "virtualbox";
                        if (combined.Contains("microsoft") && combined.Contains("virtual"))
                            return "hyper-v";
                        if (combined.Contains("qemu"))
                            return "qemu";
                    }
                }

                // Secondary check: Parallels Tools service
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Parallels\Parallels Tools"))
                    {
                        if (key != null) return "parallels";
                    }
                }
                catch { }
            }
            catch { }

            return "none";
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
        }
    }
}

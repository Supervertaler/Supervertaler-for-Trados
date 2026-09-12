using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.VoiceControl
{
    /// <summary>
    /// Downloads the voice runtime on first activation so the plugin package
    /// stays small and voice costs nothing for users who never enable it:
    ///
    ///   1. libvosk.dll for the current process architecture
    ///        x64: upstream v0.3.45 (vosk-win64-0.3.45.zip)
    ///        x86: upstream v0.3.42 (vosk-win32-0.3.42.zip) – the last
    ///             release with a 32-bit build; the C API surface we bind
    ///             is unchanged between the two.
    ///   2. The small English Vosk model (~40 MB unzipped).
    ///
    /// Everything lands under &lt;UserData&gt;/trados/voice/ – shared per-user,
    /// survives plugin updates, downloaded exactly once.
    /// </summary>
    internal static class VoiceRuntimeInstaller
    {
        private const string Win64VoskUrl = "https://github.com/alphacep/vosk-api/releases/download/v0.3.45/vosk-win64-0.3.45.zip";
        private const string Win32VoskUrl = "https://github.com/alphacep/vosk-api/releases/download/v0.3.42/vosk-win32-0.3.42.zip";
        private const string ModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip";
        private const string ModelDirName = "vosk-model-small-en-us-0.15";

        public static string VoiceDir => Path.Combine(UserDataPath.TradosDir, "voice");
        public static string NativeDir => Path.Combine(VoiceDir, "native", Environment.Is64BitProcess ? "win-x64" : "win-x86");
        public static string LibVoskPath => Path.Combine(NativeDir, "libvosk.dll");
        public static string ModelDir => Path.Combine(VoiceDir, "models", ModelDirName);

        public static bool IsInstalled =>
            File.Exists(LibVoskPath) && Directory.Exists(ModelDir) &&
            Directory.EnumerateFiles(ModelDir, "*", SearchOption.AllDirectories).Any();

        /// <summary>
        /// #127: models for SOURCE-side recognition, keyed by two-letter language.
        ///
        /// <para>Grammar mode can only use words in the model's own lexicon, so the
        /// English model cannot hear Dutch source text at all - it drops the words and
        /// says nothing (measured, .dev/oov-grammar-probe.ps1). Selecting in the
        /// source therefore needs a model for the source language.</para>
        ///
        /// <para>Downloaded only when "select source" is first used, because most
        /// installations never will and 40 MB is not worth taking on spec.</para>
        /// </summary>
        private static readonly Dictionary<string, string> SourceModelUrls =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "nl", "https://alphacephei.com/vosk/models/vosk-model-small-nl-0.22.zip" },
            };

        /// <summary>Two-letter code from a culture name ("nl-NL" - "nl"), or null.</summary>
        public static string LanguageKey(string cultureName)
        {
            if (string.IsNullOrWhiteSpace(cultureName)) return null;
            var key = cultureName.Trim();
            var dash = key.IndexOfAny(new[] { '-', '_' });
            if (dash > 0) key = key.Substring(0, dash);
            return key.Length == 0 ? null : key.ToLowerInvariant();
        }

        public static bool HasSourceModelFor(string cultureName)
        {
            var key = LanguageKey(cultureName);
            return key != null && SourceModelUrls.ContainsKey(key);
        }

        /// <summary>Where a source-language model lives, or null for a language we have no model for.</summary>
        public static string SourceModelDir(string cultureName)
        {
            var key = LanguageKey(cultureName);
            if (key == null) return null;
            string url;
            if (!SourceModelUrls.TryGetValue(key, out url)) return null;
            // The zip's own folder name, which is what it extracts to.
            var name = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
            return Path.Combine(VoiceDir, "models", name);
        }

        public static bool IsSourceModelInstalled(string cultureName)
        {
            var dir = SourceModelDir(cultureName);
            return dir != null && Directory.Exists(dir)
                && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();
        }

        /// <summary>
        /// Downloads the source-language model if it is missing. Blocking - call from
        /// a background thread. Returns the model directory, or null when there is no
        /// model for that language.
        /// </summary>
        public static string EnsureSourceModel(string cultureName, Action<string> status)
        {
            var key = LanguageKey(cultureName);
            if (key == null) return null;
            string url;
            if (!SourceModelUrls.TryGetValue(key, out url)) return null;

            var dir = SourceModelDir(cultureName);
            if (IsSourceModelInstalled(cultureName)) return dir;

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            status?.Invoke("Downloading " + key + " voice model (one-time, ~40 MB)…");
            var zipPath = DownloadToTemp(url, status, "source voice model");
            try
            {
                var modelsRoot = Path.Combine(VoiceDir, "models");
                Directory.CreateDirectory(modelsRoot);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                ZipFile.ExtractToDirectory(zipPath, modelsRoot);
            }
            finally
            {
                try { File.Delete(zipPath); } catch { }
            }
            return Directory.Exists(dir) ? dir : null;
        }

        /// <summary>
        /// Ensures libvosk + model are present, downloading whatever is
        /// missing. Blocking – call from a background thread. Reports
        /// human-readable progress via <paramref name="status"/>.
        /// </summary>
        public static void EnsureInstalled(Action<string> status)
        {
            // GitHub and alphacephei both require TLS 1.2; net48 doesn't
            // enable it by default on older Windows configurations.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            if (!File.Exists(LibVoskPath))
            {
                status?.Invoke("Downloading voice engine (one-time, ~9 MB)…");
                var url = Environment.Is64BitProcess ? Win64VoskUrl : Win32VoskUrl;
                var zipPath = DownloadToTemp(url, status, "voice engine");
                try
                {
                    Directory.CreateDirectory(NativeDir);
                    // The zip nests everything in a vosk-winXX-x.x.x/ folder –
                    // flatten all DLLs into NativeDir.
                    using (var zip = ZipFile.OpenRead(zipPath))
                    {
                        foreach (var entry in zip.Entries)
                        {
                            if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                            var target = Path.Combine(NativeDir, entry.Name);
                            entry.ExtractToFile(target, overwrite: true);
                        }
                    }
                }
                finally
                {
                    try { File.Delete(zipPath); } catch { }
                }
                if (!File.Exists(LibVoskPath))
                    throw new InvalidOperationException("The voice engine download did not contain libvosk.dll.");
            }

            if (!Directory.Exists(ModelDir) ||
                !Directory.EnumerateFiles(ModelDir, "*", SearchOption.AllDirectories).Any())
            {
                status?.Invoke("Downloading voice model (one-time, ~40 MB)…");
                var zipPath = DownloadToTemp(ModelUrl, status, "voice model");
                try
                {
                    var modelsRoot = Path.Combine(VoiceDir, "models");
                    Directory.CreateDirectory(modelsRoot);
                    // The zip contains the vosk-model-small-en-us-0.15/ folder itself.
                    if (Directory.Exists(ModelDir))
                        Directory.Delete(ModelDir, recursive: true);
                    ZipFile.ExtractToDirectory(zipPath, modelsRoot);
                }
                finally
                {
                    try { File.Delete(zipPath); } catch { }
                }
                if (!Directory.Exists(ModelDir))
                    throw new InvalidOperationException("The voice model download did not contain the expected model folder.");
            }

            status?.Invoke("Voice runtime ready.");
        }

        private static string DownloadToTemp(string url, Action<string> status, string label)
        {
            var tmp = Path.Combine(Path.GetTempPath(),
                "supervertaler_voice_" + Guid.NewGuid().ToString("N") + ".zip");
            using (var client = new WebClient())
            {
                int lastPct = -1;
                client.DownloadProgressChanged += (s, e) =>
                {
                    // DownloadFileTaskAsync progress – throttle to 5% steps
                    if (e.ProgressPercentage != lastPct && e.ProgressPercentage % 5 == 0)
                    {
                        lastPct = e.ProgressPercentage;
                        status?.Invoke($"Downloading {label}… {e.ProgressPercentage}%");
                    }
                };
                try
                {
                    client.DownloadFileTaskAsync(new Uri(url), tmp).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    try { File.Delete(tmp); } catch { }
                    throw new InvalidOperationException(
                        $"Could not download the {label}. Check your internet connection and try again.\n({ex.Message})");
                }
            }
            return tmp;
        }
    }
}

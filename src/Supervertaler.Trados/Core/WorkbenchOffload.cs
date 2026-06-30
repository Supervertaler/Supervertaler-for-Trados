using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Drives the 64-bit Supervertaler Workbench as a headless engine, so large jobs
    /// that would exhaust 32-bit Trados Studio 2024 are translated out of process.
    /// Workbench half: <c>modules/batch_offload.py</c> (Workbench#230).
    ///
    /// Preferred mode (Design B, Supervertaler-for-Trados#42): hand a whole
    /// <c>.sdlxliff</c> to Workbench, which translates it round-trip and writes a
    /// translated <c>.sdlxliff</c> the plugin swaps back in – Trados does no heavy
    /// work. See <see cref="RunSdlxliff"/>. The segment-list/TMX mode (<see cref="Run"/>)
    /// is kept for non-file callers.
    /// </summary>
    internal static class WorkbenchOffload
    {
        // ── Job / config wire model (matches modules/batch_offload.py) ──

        [DataContract]
        internal class OffloadSegment
        {
            [DataMember(Name = "id")] public int Id { get; set; }
            [DataMember(Name = "source")] public string Source { get; set; }
        }

        [DataContract]
        internal class OffloadJob
        {
            [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; } = 1;
            [DataMember(Name = "sourceLang")] public string SourceLang { get; set; }
            [DataMember(Name = "targetLang")] public string TargetLang { get; set; }
            [DataMember(Name = "provider")] public string Provider { get; set; }
            [DataMember(Name = "model")] public string Model { get; set; }
            [DataMember(Name = "baseUrl")] public string BaseUrl { get; set; }
            [DataMember(Name = "apiKey")] public string ApiKey { get; set; }
            [DataMember(Name = "settingsPath")] public string SettingsPath { get; set; }
            [DataMember(Name = "httpProxy")] public string HttpProxy { get; set; }
            [DataMember(Name = "systemPrompt")] public string SystemPrompt { get; set; }
            [DataMember(Name = "scope")] public string Scope { get; set; } = "EmptyOnly";
            [DataMember(Name = "batchSize")] public int BatchSize { get; set; } = 20;
            [DataMember(Name = "maxTokens")] public int MaxTokens { get; set; } = 16384;
            [DataMember(Name = "segments")] public List<OffloadSegment> Segments { get; set; } = new List<OffloadSegment>();
        }

        [DataContract]
        internal class OffloadResult
        {
            [DataMember(Name = "ok")] public bool Ok { get; set; }
            [DataMember(Name = "translated")] public int Translated { get; set; }
            [DataMember(Name = "failed")] public int Failed { get; set; }
            [DataMember(Name = "out")] public string Out { get; set; }
            [DataMember(Name = "errors")] public string[] Errors { get; set; }
        }

        // ── Engine discovery ──

        /// <summary>
        /// Locate the Workbench executable. Order: explicit override -> a console entry
        /// point on PATH ("supervertaler" / "supervertaler-debug"). Null if not found.
        /// </summary>
        public static string ResolveWorkbenchExe(string overridePath = null)
        {
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
                return overridePath;
            foreach (var name in new[] { "supervertaler-debug", "supervertaler" })
            {
                var found = WhichOnPath(name);
                if (!string.IsNullOrEmpty(found)) return found;
            }
            return ProbeCommonLocations();
        }

        /// <summary>
        /// Best-effort probe of common install locations for the bundled Workbench
        /// desktop app and the pip --user console scripts. Returns null if none found.
        /// </summary>
        private static string ProbeCommonLocations()
        {
            try
            {
                var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                // Bundled desktop app (the Windows zip extracts to a Supervertaler.exe).
                foreach (var c in new[]
                {
                    Path.Combine(localApp, "Programs", "Supervertaler", "Supervertaler.exe"),
                    Path.Combine(localApp, "Supervertaler", "Supervertaler.exe"),
                    Path.Combine(progFiles, "Supervertaler", "Supervertaler.exe"),
                    Path.Combine(userProfile, "Supervertaler", "Supervertaler.exe"),
                })
                {
                    if (File.Exists(c)) return c;
                }

                // pip --user console scripts: %APPDATA%\Python\Python3xx\Scripts\supervertaler*.exe
                var pyRoot = Path.Combine(appData, "Python");
                if (Directory.Exists(pyRoot))
                {
                    foreach (var dir in Directory.GetDirectories(pyRoot))
                    {
                        foreach (var n in new[] { "supervertaler-debug.exe", "supervertaler.exe" })
                        {
                            var p = Path.Combine(dir, "Scripts", n);
                            if (File.Exists(p)) return p;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string WhichOnPath(string exeName)
        {
            try
            {
                var psi = new ProcessStartInfo("where.exe", exeName)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    var stdout = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(4000);
                    foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var path = line.Trim();
                        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                            return path;
                    }
                }
            }
            catch { }
            return null;
        }

        // ── Run: SDLXLIFF round-trip (Design B) ──

        /// <summary>
        /// Translate a whole <paramref name="inSdlxliff"/> in the 64-bit Workbench and
        /// write the translated file to <paramref name="outSdlxliff"/>. Blocking; call
        /// from a background thread. Cancellation kills the engine process. Returns null
        /// if the engine could not be started.
        /// </summary>
        public static OffloadResult RunSdlxliff(
            string exePath, string inSdlxliff, string outSdlxliff,
            OffloadJob config, string workDir, Action<string> onProgress, CancellationToken ct)
        {
            Directory.CreateDirectory(workDir);
            var cfgPath = Path.Combine(workDir, "job.json");
            var resPath = Path.Combine(workDir, "result.json");
            WriteJson(config, cfgPath);

            var args = "--translate-sdlxliff \"" + inSdlxliff + "\" --out \"" + outSdlxliff +
                       "\" --config \"" + cfgPath + "\" --result \"" + resPath + "\"";
            return RunProcess(exePath, args, resPath, onProgress, ct);
        }

        // ── Run: segment-list -> TMX (non-file callers) ──

        public static OffloadResult Run(
            string exePath, OffloadJob job, string workDir, Action<string> onProgress, CancellationToken ct)
        {
            Directory.CreateDirectory(workDir);
            var jobPath = Path.Combine(workDir, "job.json");
            var tmxPath = Path.Combine(workDir, "result.tmx");
            var resPath = Path.Combine(workDir, "result.json");
            WriteJson(job, jobPath);

            var args = "--batch \"" + jobPath + "\" --out \"" + tmxPath +
                       "\" --result \"" + resPath + "\"";
            var res = RunProcess(exePath, args, resPath, onProgress, ct);
            if (res != null && string.IsNullOrEmpty(res.Out) && File.Exists(tmxPath))
                res.Out = tmxPath;
            return res;
        }

        // ── Shared process plumbing ──

        private static OffloadResult RunProcess(
            string exePath, string arguments, string resultPath, Action<string> onProgress, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exePath)
            {
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            Process proc;
            try { proc = Process.Start(psi); }
            catch { return null; }
            if (proc == null) return null;

            using (proc)
            {
                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null && onProgress != null)
                    {
                        try { onProgress(e.Data); } catch { }
                    }
                };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                while (!proc.WaitForExit(250))
                {
                    if (ct.IsCancellationRequested)
                    {
                        try { proc.Kill(); } catch { }
                        return null;
                    }
                }
                proc.WaitForExit(); // flush async readers
            }

            if (File.Exists(resultPath))
            {
                try { return ReadJson<OffloadResult>(resultPath); }
                catch { }
            }
            return null;
        }

        // ── JSON helpers (DataContractJsonSerializer, matching the rest of the plugin) ──

        private static void WriteJson<T>(T obj, string path)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var fs = File.Create(path))
                ser.WriteObject(fs, obj);
        }

        private static T ReadJson<T>(string path)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var fs = File.OpenRead(path))
                return (T)ser.ReadObject(fs);
        }
    }
}

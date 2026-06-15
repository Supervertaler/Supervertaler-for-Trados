using System;
using System.IO;
using System.Text;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Opt-in diagnostic logger. When <see cref="Enabled"/> is true (driven by the
    /// "Enable diagnostic logging" setting on the General tab), it appends timestamped
    /// lines to <c>&lt;UserDataRoot&gt;/trados/logs/diagnostic.log</c> — a stable,
    /// user-visible location (unlike the legacy %LocalAppData%\Supervertaler.Trados\
    /// folder, which the startup migration deletes).
    ///
    /// The whole point is troubleshooting that's invisible by default: the user flips
    /// the switch, reproduces the problem, then sends the file. Logging must never
    /// affect normal operation, so every method swallows IO errors and is a no-op when
    /// disabled.
    /// </summary>
    public static class DiagnosticLog
    {
        private static readonly object _lock = new object();

        /// <summary>Set from the persisted setting at startup and whenever the user toggles it.</summary>
        public static bool Enabled { get; set; }

        /// <summary>Folder holding the diagnostic log (created on demand).</summary>
        public static string LogDir => Path.Combine(UserDataPath.TradosDir, "logs");

        /// <summary>Absolute path to the diagnostic log file.</summary>
        public static string LogFilePath => Path.Combine(LogDir, "diagnostic.log");

        /// <summary>Append one timestamped, categorised line. No-op when disabled.</summary>
        public static void Log(string category, string message)
        {
            if (!Enabled) return;
            try
            {
                Directory.CreateDirectory(LogDir);
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}{Environment.NewLine}";
                lock (_lock) File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
            catch { /* logging must never throw */ }
        }

        /// <summary>
        /// Write a session banner (plugin/OS/Studio info). Called when logging is turned
        /// on and at startup if it was already on, so each run is easy to find in the file.
        /// </summary>
        public static void WriteSessionHeader(string versionInfo = null)
        {
            if (!Enabled) return;
            try
            {
                Directory.CreateDirectory(LogDir);
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("========================================================");
                sb.AppendLine($"  Diagnostic session — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                if (!string.IsNullOrWhiteSpace(versionInfo))
                    sb.AppendLine("  " + versionInfo.Replace("\n", "\n  "));
                sb.AppendLine("========================================================");
                lock (_lock) File.AppendAllText(LogFilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Empty the log file (kept, just truncated).</summary>
        public static void Clear()
        {
            try
            {
                lock (_lock)
                {
                    if (File.Exists(LogFilePath))
                        File.WriteAllText(LogFilePath, string.Empty, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}

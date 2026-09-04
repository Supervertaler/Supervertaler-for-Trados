using System;
using System.Collections.Generic;
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

        /// <summary>Path to the single retained previous log.</summary>
        public static string PreviousLogFilePath => Path.Combine(LogDir, "diagnostic.previous.log");

        /// <summary>
        /// Roll the log once it passes this. Left generous because the file exists
        /// to be reproduced into and sent, and a truncated log is worth less than a
        /// large one — but unbounded is not a third option: this file was found at
        /// 148 MB, sitting in the user's shared data folder, which for many people
        /// is synced to OneDrive or Google Drive.
        /// </summary>
        private const long MaxBytes = 8L * 1024 * 1024;

        /// <summary>
        /// Bytes appended since the last size check. Checking the file length on
        /// every write would put a stat call in front of every log line; this keeps
        /// it to roughly one per 64 KB written.
        /// </summary>
        private static long _bytesSinceCheck;
        private const long CheckEvery = 64L * 1024;

        /// <summary>Append one timestamped, categorised line. No-op when disabled.</summary>
        public static void Log(string category, string message)
        {
            if (!Enabled) return;
            Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}{Environment.NewLine}");
        }

        /// <summary>
        /// The last message written per category through <see cref="LogIfChanged"/>.
        /// One string per category is the whole of the state; it exists so a poll
        /// can report its outcome without repeating it every tick.
        /// </summary>
        private static readonly Dictionary<string, string> _lastByCategory =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Like <see cref="Log"/>, but writes only when <paramref name="message"/>
        /// differs from the last message logged in this category. For code that runs
        /// on a timer and reports the same state each time: the 2-second MultiTerm
        /// poll wrote "no termbases" 43,560 times into one file (#99). A real change
        /// still logs, once, when it happens.
        /// </summary>
        public static void LogIfChanged(string category, string message)
        {
            if (!Enabled) return;
            lock (_lock)
            {
                if (_lastByCategory.TryGetValue(category, out var last) && last == message) return;
                _lastByCategory[category] = message;
            }
            Log(category, message);
        }

        /// <summary>
        /// The single write path, so rotation cannot be bypassed by one caller.
        /// </summary>
        private static void Append(string line)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                lock (_lock)
                {
                    _bytesSinceCheck += line.Length;
                    if (_bytesSinceCheck >= CheckEvery)
                    {
                        _bytesSinceCheck = 0;
                        RotateIfOversizedNoLock();
                    }
                    File.AppendAllText(LogFilePath, line, Encoding.UTF8);
                }
            }
            catch { /* logging must never throw */ }
        }

        /// <summary>
        /// Moves the log aside once it exceeds <see cref="MaxBytes"/>, keeping one
        /// previous generation. Two files bounded at 8 MB each, rather than one
        /// unbounded — a rolled log still covers the recent past, which is what
        /// troubleshooting actually needs.
        ///
        /// <para>Caller must hold <see cref="_lock"/>.</para>
        /// </summary>
        private static void RotateIfOversizedNoLock()
        {
            try
            {
                var info = new FileInfo(LogFilePath);
                if (!info.Exists || info.Length < MaxBytes) return;

                // Delete-then-move rather than File.Move(overwrite:) — that overload
                // does not exist on .NET Framework 4.8.
                if (File.Exists(PreviousLogFilePath)) File.Delete(PreviousLogFilePath);
                File.Move(LogFilePath, PreviousLogFilePath);

                File.AppendAllText(LogFilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [Log] Previous log reached "
                    + $"{info.Length / (1024 * 1024)} MB and was rolled to "
                    + $"{Path.GetFileName(PreviousLogFilePath)}.{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch { /* a failed rotation must not cost the caller their log line */ }
        }

        /// <summary>
        /// Append one timestamped line REGARDLESS of <see cref="Enabled"/>. Used for
        /// crash/fatal reporting so it is captured to disk even when verbose
        /// diagnostic logging is switched off.
        /// </summary>
        public static void WriteAlways(string category, string message)
        {
            Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}{Environment.NewLine}");
        }

        /// <summary>
        /// Write a prominent crash banner with the exception/stack trace, regardless
        /// of <see cref="Enabled"/>. Accepts an object so it can take
        /// <c>UnhandledExceptionEventArgs.ExceptionObject</c> directly.
        /// </summary>
        public static void WriteCrash(string source, object exceptionObj)
        {
            try
            {
                var version = typeof(DiagnosticLog).Assembly.GetName().Version?.ToString();
                var ex = exceptionObj as Exception;
                var detail = (ex != null ? ex.ToString() : (exceptionObj?.ToString() ?? "(no exception object)"))
                    .Replace("\n", "\n  ");
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("########################################################");
                sb.AppendLine($"  CRASH — {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                sb.AppendLine("  Plugin: v" + (version ?? "?"));
                sb.AppendLine("  Source: " + source);
                sb.AppendLine("  " + detail);
                sb.AppendLine("########################################################");
                Append(sb.ToString());
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
                Append(sb.ToString());
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
                    // Otherwise "clear the log" leaves the rolled generation behind,
                    // which is the larger of the two files.
                    if (File.Exists(PreviousLogFilePath))
                        File.Delete(PreviousLogFilePath);
                    _bytesSinceCheck = 0;
                }
            }
            catch { }
        }
    }
}

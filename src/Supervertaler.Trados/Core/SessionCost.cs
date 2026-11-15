using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Supervertaler.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// The running AI cost at the top of the Reports tab (item 7, 18.20.198):
    /// what this Studio session has spent, and what today has, whether or not
    /// prompt logging is on. Written after a day's API bill went unnoticed until
    /// the invoice: a figure passed while working catches a runaway cost the same
    /// day, where a monthly report only explains it afterwards.
    ///
    /// <para><b>This session</b> is counted in memory from every completed AI
    /// call (LlmClient.PromptCompleted), from when Studio loaded the plugin.
    /// <b>Today</b> is read from the usage log instead, so it also holds earlier
    /// Studio sessions and the other Studio version, which write the same file.
    /// Two sources, never added together: the log also holds this session's
    /// calls, and adding the two would count them twice.</para>
    ///
    /// <para>Today's file is read incrementally: each look reads only the bytes
    /// appended since the last one, and skips lines from before today without
    /// parsing them. The monthly file of a heavy user runs to tens of thousands
    /// of lines, and this is refreshed after every call.</para>
    ///
    /// <para>A model without a price is counted at the most it can have cost, as
    /// everywhere else, and the figure then reads "up to".</para>
    /// </summary>
    public static class SessionCost
    {
        public sealed class Figures
        {
            public DateTime SessionStartedLocal;
            public decimal Session;
            public bool SessionUpTo;
            /// <summary>False when the usage log is switched off: there is nothing to read today's figure from.</summary>
            public bool TodayAvailable;
            public decimal Today;
            public bool TodayUpTo;
        }

        private static readonly object _lock = new object();
        private static bool _subscribed;
        private static DateTime _startedLocal = DateTime.Now;
        private static decimal _session;
        private static bool _sessionUpTo;

        // Today, from the usage log.
        private static DateTime _todayLocal;
        private static readonly Dictionary<string, long> _offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static decimal _today;
        private static bool _todayUpTo;

        /// <summary>Raised after every counted AI call, on whatever thread made it.</summary>
        public static event EventHandler Changed;

        /// <summary>
        /// Starts counting. Called once at Studio start-up (AppInitializer), after
        /// the usage logger, so a call is in the log before anyone is told about it.
        /// </summary>
        public static void EnsureSubscribed()
        {
            lock (_lock)
            {
                if (_subscribed) return;
                _subscribed = true;
                _startedLocal = DateTime.Now;
            }
            LlmClient.PromptCompleted += (s, e) =>
            {
                try { Add(e); } catch { /* a cost display must never disturb a call */ }
            };
        }

        /// <summary>Counts one completed call. Internal for the harness.</summary>
        internal static void Add(PromptLogEntry e)
        {
            // Connection tests are not usage (UsageLogger skips them too).
            if (e == null || e.Feature == PromptLogFeature.ConnectionTest) return;
            var cost = CostOf(e, out var ceiling);
            lock (_lock)
            {
                _session += cost;
                if (ceiling) _sessionUpTo = true;
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>What a call cost - what the provider reported where it did - or, for a model without a price, the most it can have cost.</summary>
        private static decimal CostOf(PromptLogEntry e, out bool ceiling)
        {
            ceiling = !e.IsCostKnown;
            if (!ceiling) return e.ActualCost ?? e.EstimatedCost;
            var c = TokenEstimator.CostCeiling(e.Provider, e.Model,
                e.ActualRegularInputTokens ?? e.EstimatedInputTokens,
                e.ActualCacheReadTokens ?? 0, e.ActualCacheWriteTokens ?? 0,
                e.ActualOutputTokens ?? e.EstimatedOutputTokens,
                e.ActualCacheWrite1hTokens ?? 0);
            ceiling = c > 0;   // a free local model is not "up to" anything
            return c;
        }

        /// <summary>The figures now, today's read up to date from the usage log.</summary>
        public static Figures Read()
        {
            var logOn = SettingsService.Current?.AiSettings?.IsUsageLogEnabled != false;
            lock (_lock)
            {
                if (logOn) RefreshToday(DateTime.Now);
                return new Figures
                {
                    SessionStartedLocal = _startedLocal,
                    Session = _session,
                    SessionUpTo = _sessionUpTo,
                    TodayAvailable = logOn,
                    Today = _today,
                    TodayUpTo = _todayUpTo
                };
            }
        }

        /// <summary>
        /// Brings today's total up to date: the bytes appended to the day's log
        /// file(s) since the last look, complete lines only. A new day, or a file
        /// that has shrunk, starts again from nothing. Under _lock.
        /// </summary>
        private static void RefreshToday(DateTime nowLocal)
        {
            if (nowLocal.Date != _todayLocal)
            {
                _todayLocal = nowLocal.Date;
                _offsets.Clear();
                _today = 0m;
                _todayUpTo = false;
            }

            var dayStartUtc = _todayLocal.ToUniversalTime();
            // ISO timestamps compare as strings, so a line from before today is
            // skipped without being parsed.
            var dayStartTs = dayStartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            // A local day can start in the previous UTC month (just after midnight
            // on the 1st, in summer time), so it can span two monthly files.
            var files = new List<string> { UserDataPath.UsageLogFilePath(dayStartUtc) };
            var current = UserDataPath.UsageLogFilePath(nowLocal.ToUniversalTime());
            if (!string.Equals(files[0], current, StringComparison.OrdinalIgnoreCase)) files.Add(current);

            foreach (var path in files)
            {
                long offset;
                _offsets.TryGetValue(path, out offset);
                string appended;
                try
                {
                    if (!File.Exists(path)) continue;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (fs.Length < offset)
                        {
                            // Truncated or replaced: count the day again from the start.
                            _offsets.Clear();
                            _today = 0m;
                            _todayUpTo = false;
                            RefreshToday(nowLocal);
                            return;
                        }
                        if (fs.Length == offset) continue;
                        fs.Seek(offset, SeekOrigin.Begin);
                        var bytes = new byte[fs.Length - offset];
                        var read = 0;
                        while (read < bytes.Length)
                        {
                            var n = fs.Read(bytes, read, bytes.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        // Complete lines only: a line still being written is read next time.
                        var end = Array.LastIndexOf(bytes, (byte)'\n', read - 1);
                        if (end < 0) continue;
                        appended = Encoding.UTF8.GetString(bytes, 0, end + 1);
                        _offsets[path] = offset + end + 1;
                    }
                }
                catch { continue; }   // locked for a moment, say: the next look catches up

                foreach (var raw in appended.Split('\n'))
                {
                    var line = raw.TrimStart('﻿').Trim();   // a byte-order mark opens a new file
                    if (line.Length == 0) continue;
                    var ts = TsOf(line);
                    // No timestamp: it cannot be placed on a day, so it is not today's.
                    if (ts == null || string.CompareOrdinal(ts, dayStartTs) < 0) continue;
                    var rec = UsageReader.Deserialize(line);
                    if (rec == null) continue;
                    var cost = RecordCost(rec, out var ceiling);
                    _today += cost;
                    if (ceiling) _todayUpTo = true;
                }
            }
        }

        /// <summary>The "ts" value of a usage-log line, found without parsing the JSON, or null.</summary>
        private static string TsOf(string line)
        {
            var i = line.IndexOf("\"ts\":\"", StringComparison.Ordinal);
            if (i < 0) return null;
            i += 6;
            var j = line.IndexOf('"', i);
            return j > i ? line.Substring(i, j - i) : null;
        }

        /// <summary>
        /// A logged call's cost. Priced by the log where it was; a call to a model
        /// the price list does not know is logged at $0, and is counted at the most
        /// it can have cost, as the session figure counts it. Decided from today's
        /// price list rather than the log's cost_known flag, which older records lack.
        /// </summary>
        private static decimal RecordCost(UsageRecord r, out bool ceiling)
        {
            ceiling = false;
            if (r.CostUsd > 0m || TokenEstimator.HasPricing(r.Model)) return r.CostUsd;
            var c = TokenEstimator.CostCeiling(r.Provider, r.Model,
                r.InputRegular, r.InputCacheRead, r.InputCacheWrite, r.Output, r.InputCacheWrite1h);
            ceiling = c > 0;
            return c;
        }

        /// <summary>The line the Reports tab shows.</summary>
        public static string Format(Figures f)
        {
            var sb = new StringBuilder("AI cost: ");
            sb.Append(Money(f.Session, f.SessionUpTo)).Append(" this session (since ")
              .Append(f.SessionStartedLocal.ToString("HH:mm", CultureInfo.InvariantCulture)).Append(")");
            if (f.TodayAvailable)
                sb.Append("  ·  ").Append(Money(f.Today, f.TodayUpTo)).Append(" today");
            return sb.ToString();
        }

        private static string Money(decimal amount, bool upTo)
        {
            var s = amount > 0m && amount < 0.005m
                ? "less than $0.01"
                : "$" + amount.ToString("0.00", CultureInfo.InvariantCulture);
            return upTo ? "up to " + s : s;
        }

        /// <summary>Starts again from nothing: the harness only.</summary>
        internal static void ResetForTest(DateTime startedLocal)
        {
            lock (_lock)
            {
                _startedLocal = startedLocal;
                _session = 0m;
                _sessionUpTo = false;
                _todayLocal = default(DateTime);
                _offsets.Clear();
                _today = 0m;
                _todayUpTo = false;
            }
        }
    }
}

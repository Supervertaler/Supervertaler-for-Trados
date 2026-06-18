using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>The dimension a usage report is grouped by.</summary>
    public enum UsageDimension
    {
        Project,
        Client,
        Model,
        Provider,
        Task,
        Day,
        Month
    }

    /// <summary>
    /// One aggregated row of a usage report (a group total). Computed columns are
    /// read-only so the row binds cleanly to a DataGridView.
    /// </summary>
    public class UsageReportRow
    {
        public string Group { get; set; }
        public int Calls { get; set; }
        public long InputTokens { get; set; }     // regular + cache-read + cache-write
        public long OutputTokens { get; set; }
        public long TotalTokens => InputTokens + OutputTokens;
        public decimal CostUsd { get; set; }
        /// <summary>Number of calls whose tokens came from the provider (not estimated).</summary>
        public int ActualCalls { get; set; }
        /// <summary>Share of calls in this group backed by provider-reported usage.</summary>
        public string ActualShare => Calls > 0 ? (100 * ActualCalls / Calls) + "%" : "—";
    }

    /// <summary>Reads UsageRecords back from the JSONL ledger files.</summary>
    public static class UsageReader
    {
        /// <summary>
        /// Loads all usage records whose timestamp falls in [fromUtc, toUtc].
        /// Scans every usage-*.jsonl file under {Trados}/usage/. Robust to partial
        /// or corrupt lines (they are skipped).
        /// </summary>
        public static List<UsageRecord> Load(DateTime fromUtc, DateTime toUtc)
        {
            var result = new List<UsageRecord>();
            try
            {
                var dir = UserDataPath.UsageDir;
                if (!Directory.Exists(dir)) return result;

                foreach (var file in Directory.GetFiles(dir, "usage-*.jsonl"))
                {
                    string[] lines;
                    try { lines = File.ReadAllLines(file, Encoding.UTF8); }
                    catch { continue; }

                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var rec = Deserialize(line);
                        if (rec == null) continue;

                        if (TryParseTs(rec.Ts, out var ts) && (ts < fromUtc || ts > toUtc))
                            continue;

                        result.Add(rec);
                    }
                }
            }
            catch { /* return whatever parsed */ }
            return result;
        }

        private static UsageRecord Deserialize(string line)
        {
            try
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(line)))
                {
                    var ser = new DataContractJsonSerializer(typeof(UsageRecord));
                    return (UsageRecord)ser.ReadObject(ms);
                }
            }
            catch { return null; }
        }

        public static bool TryParseTs(string ts, out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (string.IsNullOrEmpty(ts)) return false;
            return DateTime.TryParse(ts,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out utc);
        }
    }

    /// <summary>Groups usage records into report rows by a chosen dimension.</summary>
    public static class UsageAggregator
    {
        public static List<UsageReportRow> Group(IEnumerable<UsageRecord> records, UsageDimension dim)
        {
            var rows = new Dictionary<string, UsageReportRow>(StringComparer.Ordinal);

            foreach (var r in records ?? Enumerable.Empty<UsageRecord>())
            {
                if (r == null) continue;
                var key = KeyFor(r, dim);

                if (!rows.TryGetValue(key, out var row))
                {
                    row = new UsageReportRow { Group = key };
                    rows[key] = row;
                }

                row.Calls += 1;
                row.InputTokens += (long)r.InputRegular + r.InputCacheRead + r.InputCacheWrite;
                row.OutputTokens += r.Output;
                row.CostUsd += r.CostUsd;
                if (string.Equals(r.Source, "actual", StringComparison.OrdinalIgnoreCase))
                    row.ActualCalls += 1;
            }

            // Largest cost first.
            return rows.Values.OrderByDescending(x => x.CostUsd).ThenByDescending(x => x.TotalTokens).ToList();
        }

        /// <summary>A single grand-total row across all records.</summary>
        public static UsageReportRow Total(IEnumerable<UsageRecord> records)
        {
            var total = new UsageReportRow { Group = "TOTAL" };
            foreach (var r in records ?? Enumerable.Empty<UsageRecord>())
            {
                if (r == null) continue;
                total.Calls += 1;
                total.InputTokens += (long)r.InputRegular + r.InputCacheRead + r.InputCacheWrite;
                total.OutputTokens += r.Output;
                total.CostUsd += r.CostUsd;
                if (string.Equals(r.Source, "actual", StringComparison.OrdinalIgnoreCase))
                    total.ActualCalls += 1;
            }
            return total;
        }

        private static string KeyFor(UsageRecord r, UsageDimension dim)
        {
            switch (dim)
            {
                case UsageDimension.Project: return Blank(r.Project);
                case UsageDimension.Client: return Blank(r.Client);
                case UsageDimension.Model: return Blank(r.Model);
                case UsageDimension.Provider: return Blank(r.Provider);
                case UsageDimension.Task: return Blank(r.Task);
                case UsageDimension.Day:
                    return UsageReader.TryParseTs(r.Ts, out var d) ? d.ToString("yyyy-MM-dd") : "(unknown)";
                case UsageDimension.Month:
                    return UsageReader.TryParseTs(r.Ts, out var m) ? m.ToString("yyyy-MM") : "(unknown)";
                default: return "(all)";
            }
        }

        private static string Blank(string s) => string.IsNullOrEmpty(s) ? "(none)" : s;
    }
}

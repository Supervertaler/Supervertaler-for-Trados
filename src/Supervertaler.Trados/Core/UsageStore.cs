using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Appends <see cref="UsageRecord"/>s as JSON Lines (one compact JSON object
    /// per line) to a monthly-rotated file under {Trados}/usage/. Plain JSONL so
    /// it opens in a spreadsheet or is parsed with a script. Best-effort: any
    /// write failure is swallowed so token logging never disrupts a translation.
    /// </summary>
    public static class UsageStore
    {
        private static readonly object _lock = new object();

        public static void Append(UsageRecord record)
        {
            if (record == null) return;
            try
            {
                var line = Serialize(record);
                if (string.IsNullOrEmpty(line)) return;

                var dir = UserDataPath.UsageDir;
                Directory.CreateDirectory(dir);
                var path = UserDataPath.UsageLogFilePath(DateTime.UtcNow);

                lock (_lock)
                {
                    File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
                }
            }
            catch { /* never let logging disrupt translation */ }
        }

        private static string Serialize(UsageRecord record)
        {
            try
            {
                using (var ms = new MemoryStream())
                {
                    var ser = new DataContractJsonSerializer(typeof(UsageRecord));
                    ser.WriteObject(ms, record);
                    // DataContractJsonSerializer emits a single line (control chars
                    // in strings are escaped), so this is JSONL-safe.
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch { return null; }
        }
    }
}

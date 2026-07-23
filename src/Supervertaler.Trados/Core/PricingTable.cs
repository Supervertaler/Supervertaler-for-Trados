using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// The canonical LLM price list (USD per 1,000,000 tokens), shared verbatim
    /// with Supervertaler Workbench. Resolution order:
    ///   1. &lt;home&gt;/Supervertaler/pricing.json — the shared user override.
    ///      Edit this one file to re-price BOTH Supervertaler products at once.
    ///   2. the pricing.json embedded in this assembly (bundled default).
    ///   3. a minimal hardcoded table — used only if both above fail to load,
    ///      so cost estimation never throws.
    /// Loaded once on first use. Cache-discount multipliers live in
    /// TokenEstimator (they are per-provider, not per-model).
    /// </summary>
    public static class PricingTable
    {
        [DataContract]
        private class PricingFile
        {
            [DataMember(Name = "version")] public string Version { get; set; }
            [DataMember(Name = "models")] public Dictionary<string, ModelPrice> Models { get; set; }
        }

        [DataContract]
        private class ModelPrice
        {
            [DataMember(Name = "input")] public decimal Input { get; set; }
            [DataMember(Name = "output")] public decimal Output { get; set; }
        }

        private static readonly Dictionary<string, (decimal input, decimal output)> _prices = Load();

        /// <summary>Look up (input, output) per-1M rates for a model id. False if unknown.</summary>
        public static bool TryGet(string model, out (decimal input, decimal output) rates)
        {
            if (!string.IsNullOrEmpty(model) && _prices.TryGetValue(model, out rates))
                return true;
            rates = (0m, 0m);
            return false;
        }

        private static Dictionary<string, (decimal, decimal)> Load()
        {
            // 1. Shared user override (re-prices Trados AND Workbench in one place).
            try
            {
                var overridePath = Path.Combine(UserDataPath.Root, "pricing.json");
                if (File.Exists(overridePath))
                {
                    using (var fs = File.OpenRead(overridePath))
                    {
                        var parsed = Parse(fs);
                        if (parsed != null && parsed.Count > 0) return parsed;
                    }
                }
            }
            catch { /* fall through to bundled */ }

            // 2. Bundled embedded resource (the default canonical list).
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string resName = null;
                foreach (var n in asm.GetManifestResourceNames())
                {
                    if (n.EndsWith("pricing.json", StringComparison.OrdinalIgnoreCase)) { resName = n; break; }
                }
                if (resName != null)
                {
                    using (var s = asm.GetManifestResourceStream(resName))
                    {
                        var parsed = Parse(s);
                        if (parsed != null && parsed.Count > 0) return parsed;
                    }
                }
            }
            catch { /* fall through to hardcoded safety net */ }

            // 3. Minimal safety net — only reached if both files fail to load.
            return new Dictionary<string, (decimal, decimal)>(StringComparer.OrdinalIgnoreCase)
            {
                { "gpt-5.5", (5.00m, 30.00m) },
                { "gpt-5.4-mini", (0.75m, 4.50m) },
                { "claude-fable-5", (10.00m, 50.00m) },
                { "claude-opus-4-8", (5.00m, 25.00m) },
                { "claude-sonnet-5", (3.00m, 15.00m) },
                { "claude-sonnet-4-6", (3.00m, 15.00m) },
                { "claude-haiku-4-5-20251001", (1.00m, 5.00m) },
                { "gemini-3.1-flash-lite", (0.25m, 1.50m) },
            };
        }

        private static Dictionary<string, (decimal, decimal)> Parse(Stream s)
        {
            if (s == null) return null;
            var serSettings = new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };
            var ser = new DataContractJsonSerializer(typeof(PricingFile), serSettings);
            var file = (PricingFile)ser.ReadObject(s);
            var dict = new Dictionary<string, (decimal, decimal)>(StringComparer.OrdinalIgnoreCase);
            if (file?.Models != null)
            {
                foreach (var kv in file.Models)
                {
                    if (kv.Key != null && kv.Value != null)
                        dict[kv.Key] = (kv.Value.Input, kv.Value.Output);
                }
            }
            return dict;
        }
    }
}

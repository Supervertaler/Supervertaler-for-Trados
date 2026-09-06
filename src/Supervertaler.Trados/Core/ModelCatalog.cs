using System;
using System.Collections.Generic;
using System.Linq;
using Supervertaler.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// The models a provider offers, as the plugin shows them (#106): the curated
    /// list compiled into <see cref="LlmModels"/> - with descriptions and per-model
    /// defaults - followed by whatever the provider's own list added the last time
    /// it was fetched. Fetched entries are cached in <see cref="AiSettings.FetchedModels"/>
    /// so they survive a restart. Curated entries win on a duplicate id, since they
    /// carry the description and the token defaults.
    /// </summary>
    internal static class ModelCatalog
    {
        public static LlmModelInfo[] ModelsFor(string providerKey, AiSettings settings)
        {
            var curated = LlmModels.GetModelsForProvider(providerKey) ?? new LlmModelInfo[0];
            var fetched = settings?.FetchedModels;
            if (fetched == null || fetched.Count == 0) return curated;

            var known = new HashSet<string>(curated.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            var extras = new List<LlmModelInfo>();
            string when = null;
            foreach (var f in fetched)
            {
                if (f == null || !string.Equals(f.Provider, providerKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(f.Id) || !known.Add(f.Id)) continue;
                if (when == null && !string.IsNullOrEmpty(f.FetchedAt)) when = f.FetchedAt;
                extras.Add(new LlmModelInfo
                {
                    Id = f.Id,
                    DisplayName = string.IsNullOrWhiteSpace(f.DisplayName) ? f.Id : f.DisplayName,
                    Description = "from the provider's model list" + (when != null ? " (" + when + ")" : ""),
                    Provider = ProviderOf(providerKey),
                    IsReasoningModel = false,
                    SupportsTemperature = true,
                });
            }
            if (extras.Count == 0) return curated;
            return curated.Concat(extras).ToArray();
        }

        /// <summary>Replaces the cached list for one provider with a fresh fetch.</summary>
        public static void RecordFetched(AiSettings settings, string providerKey,
            IEnumerable<LlmModelCatalog.FetchedModel> models)
        {
            if (settings == null) return;
            if (settings.FetchedModels == null) settings.FetchedModels = new List<FetchedModelEntry>();
            settings.FetchedModels.RemoveAll(f => f == null || string.Equals(f.Provider, providerKey, StringComparison.OrdinalIgnoreCase));
            var stamp = DateTime.Now.ToString("yyyy-MM-dd");
            foreach (var m in models ?? Enumerable.Empty<LlmModelCatalog.FetchedModel>())
            {
                if (m == null || string.IsNullOrWhiteSpace(m.Id)) continue;
                settings.FetchedModels.Add(new FetchedModelEntry
                {
                    Provider = providerKey, Id = m.Id, DisplayName = m.DisplayName, FetchedAt = stamp
                });
            }
        }

        /// <summary>When the provider's list was last fetched, or null.</summary>
        public static string FetchedOn(AiSettings settings, string providerKey)
        {
            return settings?.FetchedModels?
                .FirstOrDefault(f => f != null && string.Equals(f.Provider, providerKey, StringComparison.OrdinalIgnoreCase))
                ?.FetchedAt;
        }

        private static LlmProvider ProviderOf(string providerKey)
        {
            foreach (LlmProvider p in Enum.GetValues(typeof(LlmProvider)))
                if (LlmModels.GetProviderKey(p) == providerKey) return p;
            return LlmProvider.OpenAi;
        }
    }
}

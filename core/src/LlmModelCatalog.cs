using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Supervertaler.Core
{
    /// <summary>
    /// Asks a provider for its current model list (Trados #106). The curated list in
    /// <see cref="LlmModels"/> is what the plugin knows at release time; a model
    /// released this week is only in the provider's own list. One GET per provider,
    /// the list endpoints they all publish:
    ///
    ///   Anthropic  GET /v1/models              data[].id, display_name, created_at (newest first)
    ///   OpenAI     GET /v1/models              data[].id, created - also lists embeddings, audio,
    ///                                          images; only the chat families are kept
    ///   Gemini     GET /v1beta/models          models[].name, displayName, supportedGenerationMethods
    ///   Mistral, DeepSeek, xAI, OpenRouter, custom OpenAI-compatible: /models, data[].id
    ///   Ollama     GET /api/tags               models[].name
    ///
    /// Returns ids and, where the provider gives one, a display name. Never
    /// filters by anything but "can this be chatted with": which of these to show
    /// is the caller's business. Throws on failure with a message fit for a status
    /// line; the caller decides what a failed fetch means (usually: keep the old list).
    /// </summary>
    public static class LlmModelCatalog
    {
        public sealed class FetchedModel
        {
            public string Id;
            public string DisplayName;
            public override string ToString() => string.IsNullOrEmpty(DisplayName) ? Id : DisplayName + " (" + Id + ")";
        }

        private static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        private const int TimeoutMs = 20_000;

        /// <summary>True when this provider publishes a model list we know how to read.</summary>
        public static bool CanFetch(string providerKey)
        {
            switch (providerKey)
            {
                case LlmModels.ProviderClaude:
                case LlmModels.ProviderOpenAi:
                case LlmModels.ProviderGemini:
                case LlmModels.ProviderGrok:
                case LlmModels.ProviderMistral:
                case LlmModels.ProviderDeepSeek:
                case LlmModels.ProviderOpenRouter:
                case LlmModels.ProviderOllama:
                case LlmModels.ProviderCustomOpenAi:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The provider's current list. <paramref name="apiKey"/> may be the one in
        /// the settings box, saved or not; <paramref name="baseUrl"/> is used by
        /// Ollama and custom endpoints only.
        /// </summary>
        public static async Task<List<FetchedModel>> FetchAsync(string providerKey, string apiKey, string baseUrl,
            CancellationToken ct)
        {
            if (!CanFetch(providerKey))
                throw new NotSupportedException("This provider does not publish a model list.");

            var request = BuildRequest(providerKey, apiKey, baseUrl);
            string body;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeoutMs);
                try
                {
                    using (var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false))
                    {
                        body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException(DescribeFailure((int)response.StatusCode, body));
                    }
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("No reply within " + (TimeoutMs / 1000) + " s.");
                }
            }

            var models = Parse(providerKey, body);
            if (models.Count == 0)
                throw new InvalidOperationException("The provider answered, but its list held no usable models.");
            return models;
        }

        private static HttpRequestMessage BuildRequest(string providerKey, string apiKey, string baseUrl)
        {
            string url;
            var req = new HttpRequestMessage(HttpMethod.Get, "http://placeholder/");
            switch (providerKey)
            {
                case LlmModels.ProviderClaude:
                    url = "https://api.anthropic.com/v1/models?limit=100";
                    req.Headers.TryAddWithoutValidation("x-api-key", apiKey ?? "");
                    req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                    break;
                case LlmModels.ProviderOpenAi:
                    url = "https://api.openai.com/v1/models";
                    Bearer(req, apiKey);
                    break;
                case LlmModels.ProviderGemini:
                    url = "https://generativelanguage.googleapis.com/v1beta/models?pageSize=200&key=" + Uri.EscapeDataString(apiKey ?? "");
                    break;
                case LlmModels.ProviderGrok:
                    url = "https://api.x.ai/v1/models";
                    Bearer(req, apiKey);
                    break;
                case LlmModels.ProviderMistral:
                    url = "https://api.mistral.ai/v1/models";
                    Bearer(req, apiKey);
                    break;
                case LlmModels.ProviderDeepSeek:
                    url = "https://api.deepseek.com/models";
                    Bearer(req, apiKey);
                    break;
                case LlmModels.ProviderOpenRouter:
                    url = "https://openrouter.ai/api/v1/models";
                    if (!string.IsNullOrEmpty(apiKey)) Bearer(req, apiKey);
                    break;
                case LlmModels.ProviderOllama:
                    url = (string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:11434" : baseUrl.Trim()).TrimEnd('/') + "/api/tags";
                    break;
                case LlmModels.ProviderCustomOpenAi:
                    if (string.IsNullOrWhiteSpace(baseUrl))
                        throw new InvalidOperationException("Enter the endpoint's base URL first.");
                    url = baseUrl.Trim().TrimEnd('/') + "/models";
                    if (!string.IsNullOrEmpty(apiKey)) Bearer(req, apiKey);
                    break;
                default:
                    throw new NotSupportedException(providerKey);
            }
            req.RequestUri = new Uri(url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            return req;
        }

        private static void Bearer(HttpRequestMessage req, string apiKey)
        {
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (apiKey ?? ""));
        }

        private static string DescribeFailure(int status, string body)
        {
            var snippet = "";
            try
            {
                var m = Regex.Match(body ?? "", @"""message""\s*:\s*""((?:[^""\\]|\\.)*)""");
                if (m.Success) snippet = ": " + m.Groups[1].Value;
            }
            catch { }
            switch (status)
            {
                case 401: return "The API key was refused (401)" + snippet;
                case 403: return "Access denied (403)" + snippet;
                case 404: return "No model list at that address (404)" + snippet;
                case 429: return "Rate limited (429) - try again in a moment";
                default: return "HTTP " + status + snippet;
            }
        }

        // ─── Parsing: one DTO per list shape ────────────────────────────────────────
        //
        // Every field below is written by the deserialiser, which the compiler
        // cannot see, so all of them would otherwise raise CS0649. Twelve
        // permanent false warnings is how a real one goes unread.
#pragma warning disable 0649

        [DataContract] private class OpenAiList { [DataMember(Name = "data")] public OpenAiEntry[] Data; }
        [DataContract] private class OpenAiEntry
        {
            [DataMember(Name = "id")] public string Id;
            [DataMember(Name = "display_name")] public string DisplayName;   // Anthropic
            [DataMember(Name = "name")] public string Name;                  // OpenRouter
            [DataMember(Name = "created")] public long Created;              // OpenAI (epoch seconds)
            [DataMember(Name = "created_at")] public string CreatedAt;       // Anthropic (ISO)
        }
        [DataContract] private class GeminiList { [DataMember(Name = "models")] public GeminiEntry[] Models; }
        [DataContract] private class GeminiEntry
        {
            [DataMember(Name = "name")] public string Name;
            [DataMember(Name = "displayName")] public string DisplayName;
            [DataMember(Name = "supportedGenerationMethods")] public string[] Methods;
        }
        [DataContract] private class OllamaList { [DataMember(Name = "models")] public OllamaEntry[] Models; }
        [DataContract] private class OllamaEntry { [DataMember(Name = "name")] public string Name; }
#pragma warning restore 0649

        private static T Read<T>(string json) where T : class
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json ?? "")))
                return new DataContractJsonSerializer(typeof(T)).ReadObject(ms) as T;
        }

        private static readonly Regex OpenAiChatFamilies = new Regex(@"^(gpt|o[0-9]|chatgpt)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Variants of a chat model that are not for translating text: speech, realtime
        // voice, image, web search, code, legacy instruct, moderation, embeddings.
        private static readonly Regex OpenAiNotForText = new Regex(@"(audio|realtime|transcribe|tts|image|search|codex|instruct|moderation|embedding)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Gemini's list marks image, speech and transcription models as supporting
        // generateContent, so the method check alone lets Nano Banana through.
        private static readonly Regex GeminiNotForText = new Regex(@"(tts|image|imagen|veo|embed|transcribe|aqa|learnlm|audio|nano-banana|lyria|robotics|computer-use|antigravity|deep-research)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // "gpt-5.4-mini-2026-03-17" beside "gpt-5.4-mini": the snapshot adds nothing
        // to a picker, and the undated id is the one that keeps working.
        private static readonly Regex DatedSnapshot = new Regex(@"^(?<base>.+?)-(\d{4}-\d{2}-\d{2}|\d{8})$", RegexOptions.Compiled);

        /// <summary>Removes dated snapshots whose undated model is also in the list.</summary>
        private static List<FetchedModel> DropDatedDuplicates(List<FetchedModel> list)
        {
            var ids = new HashSet<string>(list.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            return list.Where(m =>
            {
                var d = DatedSnapshot.Match(m.Id);
                return !d.Success || !ids.Contains(d.Groups["base"].Value);
            }).ToList();
        }

        private static List<FetchedModel> Parse(string providerKey, string body)
        {
            var list = new List<FetchedModel>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string id, string display)
            {
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id.Trim())) return;
                list.Add(new FetchedModel { Id = id.Trim(), DisplayName = string.IsNullOrWhiteSpace(display) ? null : display.Trim() });
            }

            switch (providerKey)
            {
                case LlmModels.ProviderGemini:
                {
                    var g = Read<GeminiList>(body);
                    foreach (var m in g?.Models ?? new GeminiEntry[0])
                    {
                        if (m == null || string.IsNullOrEmpty(m.Name)) continue;
                        // Only models that can be chatted with; the list also carries
                        // embedding and image models.
                        if (m.Methods != null && !m.Methods.Any(x => string.Equals(x, "generateContent", StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var id = m.Name.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? m.Name.Substring(7) : m.Name;
                        if (GeminiNotForText.IsMatch(id)) continue;
                        Add(id, m.DisplayName);
                    }
                    break;
                }
                case LlmModels.ProviderOllama:
                {
                    var o = Read<OllamaList>(body);
                    foreach (var m in o?.Models ?? new OllamaEntry[0]) Add(m?.Name, null);
                    list.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
                    break;
                }
                default:
                {
                    var d = Read<OpenAiList>(body);
                    var entries = (d?.Data ?? new OpenAiEntry[0]).Where(e => e != null && !string.IsNullOrEmpty(e.Id)).ToList();
                    if (providerKey == LlmModels.ProviderOpenAi)
                    {
                        // The list holds embeddings, audio, images, moderation... keep the
                        // chat families, newest first (the API returns them unordered).
                        entries = entries.Where(e => OpenAiChatFamilies.IsMatch(e.Id) && !OpenAiNotForText.IsMatch(e.Id))
                                         .OrderByDescending(e => e.Created).ToList();
                    }
                    else if (providerKey == LlmModels.ProviderClaude)
                    {
                        // Anthropic returns newest first already; keep that order.
                    }
                    else
                    {
                        entries = entries.OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToList();
                    }
                    foreach (var e in entries) Add(e.Id, e.DisplayName ?? e.Name);
                    break;
                }
            }
            return DropDatedDuplicates(list);
        }
    }
}

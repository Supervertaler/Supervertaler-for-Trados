using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Universal LLM client for all supported providers.
    /// Uses System.Net.Http.HttpClient (built into .NET 4.8) — no external dependencies.
    /// Ported from Python Supervertaler's LLMClient in modules/llm_clients.py.
    /// </summary>
    public class LlmClient : IDisposable
    {
        // ─── Prompt Logging ────────────────────────────────────────

        /// <summary>
        /// Fires after every AI API call (success or failure) with full prompt details.
        /// Subscribe to this event to log prompts to the Reports tab.
        /// </summary>
        public static event EventHandler<PromptLogEntry> PromptCompleted;

        // HttpClient is designed to be reused across the app lifetime
        private static readonly HttpClient Http;

        static LlmClient()
        {
            var handler = new HttpClientHandler();
            Http = new HttpClient(handler);
            Http.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // We manage timeouts via CancellationToken
            Http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }

        private readonly string _provider;
        private readonly string _model;
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly bool _isReasoningModel;
        private readonly int _defaultTimeoutMs;
        private readonly int _maxTokens;
        private readonly int _ollamaTimeoutMinutes;

        public LlmClient(string provider, string model, string apiKey,
                          string baseUrl = null, int maxTokens = 16384,
                          int ollamaTimeoutMinutes = 0)
        {
            _provider = provider ?? LlmModels.ProviderOpenAi;
            _model = model ?? "gpt-5.4-mini";
            _apiKey = apiKey ?? "";
            _baseUrl = baseUrl;
            _maxTokens = maxTokens;
            _ollamaTimeoutMinutes = ollamaTimeoutMinutes;

            var modelInfo = LlmModels.FindModel(_model);
            _isReasoningModel = modelInfo?.IsReasoningModel ?? IsReasoningModel(_model);
            _defaultTimeoutMs = modelInfo?.DefaultTimeoutMs ?? 120_000;
        }

        /// <summary>
        /// Translates text using the configured LLM provider.
        /// </summary>
        public async Task<string> TranslateAsync(
            string text,
            string sourceLang,
            string targetLang,
            string systemPrompt = null,
            int? maxTokens = null,
            CancellationToken cancellationToken = default)
        {
            var prompt = $"Translate the following text from {sourceLang} to {targetLang}:\n\n{text}";
            var result = await SendPromptAsync(prompt, systemPrompt, maxTokens, cancellationToken);
            return CleanTranslationResponse(result);
        }

        /// <summary>
        /// Sends a raw prompt to the LLM. Used by TranslateAsync and available
        /// for the AI Assistant tab.
        /// </summary>
        /// <param name="suppressLog">
        /// When true, the PromptCompleted event is NOT fired for this call.
        /// Use this when the caller (e.g. BatchTranslator) will aggregate multiple
        /// calls and fire a single consolidated log entry itself.
        /// </param>
        public async Task<string> SendPromptAsync(
            string prompt,
            string systemPrompt = null,
            int? maxTokens = null,
            CancellationToken cancellationToken = default,
            PromptLogFeature? feature = null,
            string promptName = null,
            bool suppressLog = false)
        {
            var sw = Stopwatch.StartNew();
            string result = null;
            string errorMsg = null;

            try
            {
                switch (_provider)
                {
                    case LlmModels.ProviderOpenAi:
                    case LlmModels.ProviderGrok:
                    case LlmModels.ProviderMistral:
                    case LlmModels.ProviderOpenRouter:
                    case LlmModels.ProviderCustomOpenAi:
                        result = await CallOpenAiAsync(prompt, systemPrompt, maxTokens, cancellationToken);
                        break;
                    case LlmModels.ProviderClaude:
                        result = await CallClaudeAsync(prompt, systemPrompt, maxTokens, cancellationToken);
                        break;
                    case LlmModels.ProviderGemini:
                        result = await CallGeminiAsync(prompt, systemPrompt, maxTokens, cancellationToken);
                        break;
                    case LlmModels.ProviderOllama:
                        result = await CallOllamaAsync(prompt, systemPrompt, maxTokens, cancellationToken);
                        break;
                    default:
                        throw new ArgumentException($"Unsupported provider: {_provider}");
                }

                return result;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                throw;
            }
            finally
            {
                sw.Stop();
                if (!suppressLog && feature.HasValue && feature.Value != PromptLogFeature.ConnectionTest)
                    RaisePromptCompleted(feature.Value, systemPrompt, prompt, null, result, errorMsg, sw.Elapsed, promptName);
            }
        }

        /// <summary>
        /// Fires the PromptCompleted event with a pre-built entry.
        /// Used by BatchTranslator to emit a single aggregated log entry
        /// after all sub-batches have completed.
        /// </summary>
        public static void FirePromptCompleted(PromptLogEntry entry)
        {
            var handler = PromptCompleted;
            if (handler == null || entry == null) return;
            try { handler(null, entry); }
            catch { /* never let logging break the caller */ }
        }

        /// <summary>
        /// Sends a multi-turn chat conversation to the LLM.
        /// Used by the AI Assistant for conversation continuity.
        /// </summary>
        public async Task<string> SendChatAsync(
            List<ChatMessage> messages,
            string systemPrompt = null,
            int? maxTokens = null,
            CancellationToken cancellationToken = default,
            PromptLogFeature? feature = null,
            string promptName = null)
        {
            var sw = Stopwatch.StartNew();
            string result = null;
            string errorMsg = null;

            try
            {
                switch (_provider)
                {
                    case LlmModels.ProviderOpenAi:
                    case LlmModels.ProviderGrok:
                    case LlmModels.ProviderMistral:
                    case LlmModels.ProviderOpenRouter:
                    case LlmModels.ProviderCustomOpenAi:
                        result = await CallOpenAiChatAsync(messages, systemPrompt, maxTokens, cancellationToken);
                        break;
                    case LlmModels.ProviderClaude:
                        result = await CallClaudeChatAsync(messages, systemPrompt, maxTokens, cancellationToken);
                        break;
                    case LlmModels.ProviderGemini:
                        result = await CallGeminiChatAsync(messages, systemPrompt, maxTokens, cancellationToken);
                        break;
                    case LlmModels.ProviderOllama:
                        result = await CallOllamaChatAsync(messages, systemPrompt, maxTokens, cancellationToken);
                        break;
                    default:
                        throw new ArgumentException($"Unsupported provider: {_provider}");
                }

                return result;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                throw;
            }
            finally
            {
                sw.Stop();
                if (feature.HasValue && feature.Value != PromptLogFeature.ConnectionTest)
                    RaisePromptCompleted(feature.Value, systemPrompt, null, messages, result, errorMsg, sw.Elapsed, promptName);
            }
        }

        /// <summary>
        /// Tests the connection to the configured provider.
        /// Returns null on success, or an error message on failure.
        /// </summary>
        public async Task<string> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (_provider == LlmModels.ProviderOllama)
                {
                    return await TestOllamaConnectionAsync(cancellationToken);
                }

                // For cloud providers: send a trivial translation request
                var result = await TranslateAsync("Hello", "English", "Dutch",
                    maxTokens: 100, cancellationToken: cancellationToken);

                return string.IsNullOrWhiteSpace(result)
                    ? "Empty response from provider"
                    : null; // success
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // ─── Prompt Logging Helper ───────────────────────────────────

        private void RaisePromptCompleted(
            PromptLogFeature feature,
            string systemPrompt,
            string userPrompt,
            List<ChatMessage> messages,
            string response,
            string errorMessage,
            TimeSpan duration,
            string promptName = null)
        {
            var handler = PromptCompleted;
            if (handler == null) return;

            try
            {
                int inputTokens = messages != null
                    ? TokenEstimator.EstimateInputTokens(messages, systemPrompt)
                    : TokenEstimator.EstimateInputTokens(userPrompt, systemPrompt);
                int outputTokens = TokenEstimator.EstimateTokens(response);

                var modelInfo = LlmModels.FindModel(_model);

                var entry = new PromptLogEntry
                {
                    Timestamp = DateTime.Now,
                    Feature = feature,
                    PromptName = promptName,
                    Provider = _provider,
                    Model = _model,
                    DisplayModel = modelInfo?.DisplayName ?? _model,
                    SystemPrompt = systemPrompt,
                    UserPrompt = userPrompt,
                    Messages = messages,
                    Response = response,
                    EstimatedInputTokens = inputTokens,
                    EstimatedOutputTokens = outputTokens,
                    EstimatedCost = TokenEstimator.EstimateCost(_model, inputTokens, outputTokens),
                    Duration = duration,
                    IsError = errorMessage != null,
                    ErrorMessage = errorMessage
                };

                handler(this, entry);
            }
            catch
            {
                // Never let logging errors break the AI call
            }
        }

        // ─── API Key Fallback Chain ──────────────────────────────────

        /// <summary>
        /// Resolves the API key for a provider using the fallback chain:
        /// 1. Plugin-local settings (aiSettings.apiKeys)
        /// 2. Supervertaler desktop app settings (~/Supervertaler/settings/settings.json)
        /// 3. Supervertaler config pointer (%APPDATA%\Supervertaler\config.json → user_data_path)
        /// </summary>
        public static string ResolveApiKey(string providerKey, AiApiKeys localKeys)
        {
            // 1. Check plugin-local keys
            var localKey = GetKeyFromLocal(providerKey, localKeys);
            if (!string.IsNullOrEmpty(localKey))
                return localKey;

            // 2. Check Supervertaler desktop settings
            return ReadKeyFromSupervertalerSettings(providerKey);
        }

        private static string GetKeyFromLocal(string providerKey, AiApiKeys keys)
        {
            if (keys == null) return null;
            switch (providerKey)
            {
                case LlmModels.ProviderOpenAi: return keys.OpenAi;
                case LlmModels.ProviderClaude: return keys.Claude;
                case LlmModels.ProviderGemini: return keys.Gemini;
                case LlmModels.ProviderGrok: return keys.Grok;
                case LlmModels.ProviderMistral: return keys.Mistral;
                case LlmModels.ProviderOpenRouter: return keys.OpenRouter;
                case LlmModels.ProviderCustomOpenAi: return keys.CustomOpenAi;
                default: return null;
            }
        }

        private static string ReadKeyFromSupervertalerSettings(string providerKey)
        {
            // Try multiple paths in order
            var paths = new List<string>();

            // Path 1: Check config pointer for user_data_path
            var configPointer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Supervertaler", "config.json");
            if (File.Exists(configPointer))
            {
                try
                {
                    var configJson = File.ReadAllText(configPointer, Encoding.UTF8);
                    var userDataPath = ExtractJsonString(configJson, "user_data_path");
                    if (!string.IsNullOrEmpty(userDataPath))
                    {
                        // New layout: workbench/settings/settings.json
                        paths.Add(Path.Combine(userDataPath, "workbench", "settings", "settings.json"));
                        // Old layout fallback: settings/settings.json
                        paths.Add(Path.Combine(userDataPath, "settings", "settings.json"));
                    }
                }
                catch { }
            }

            // Path 2: ~/Supervertaler/workbench/settings/settings.json (new layout)
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            paths.Add(Path.Combine(userProfile, "Supervertaler", "workbench", "settings", "settings.json"));
            // Old layout fallback
            paths.Add(Path.Combine(userProfile, "Supervertaler", "settings", "settings.json"));

            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;

                try
                {
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    // Parse api_keys section — look for the provider key
                    var key = ExtractNestedJsonString(json, "api_keys", providerKey);
                    if (!string.IsNullOrEmpty(key))
                        return key;

                    // Try "google" alias for gemini
                    if (providerKey == LlmModels.ProviderGemini)
                    {
                        key = ExtractNestedJsonString(json, "api_keys", "google");
                        if (!string.IsNullOrEmpty(key))
                            return key;
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Returns a human-readable label for the active OpenAI-compatible provider.
        /// </summary>
        private string OpenAiProviderLabel()
        {
            if (_provider == LlmModels.ProviderGrok) return "Grok";
            if (_provider == LlmModels.ProviderMistral) return "Mistral";
            if (_provider == LlmModels.ProviderCustomOpenAi) return "Custom OpenAI";
            return "OpenAI";
        }

        /// <summary>
        /// Resolves the base URL for OpenAI-compatible providers.
        /// Grok uses xAI's API; Custom OpenAI uses user-configured endpoint.
        /// </summary>
        private string ResolveOpenAiBaseUrl()
        {
            if (_provider == LlmModels.ProviderGrok)
                return "https://api.x.ai/v1";
            if (_provider == LlmModels.ProviderMistral)
                return "https://api.mistral.ai/v1";
            if (_provider == LlmModels.ProviderOpenRouter)
                return "https://openrouter.ai/api/v1";
            if (_provider == LlmModels.ProviderCustomOpenAi && !string.IsNullOrEmpty(_baseUrl))
                return _baseUrl.TrimEnd('/');
            return "https://api.openai.com/v1";
        }

        /// <summary>
        /// Adds HTTP-Referer and X-Title headers required by OpenRouter's ToS.
        /// No-op for other providers.
        /// </summary>
        private void AddOpenRouterHeaders(HttpRequestMessage request)
        {
            if (_provider != LlmModels.ProviderOpenRouter) return;
            request.Headers.Add("HTTP-Referer", "https://supervertaler.com");
            request.Headers.Add("X-Title", "Supervertaler for Trados");
        }

        // ─── Provider Implementations ────────────────────────────────

        private async Task<string> CallOpenAiAsync(
            string prompt, string systemPrompt, int? maxTokens,
            CancellationToken ct)
        {
            var baseUrl = ResolveOpenAiBaseUrl();

            var url = $"{baseUrl}/chat/completions";
            var tokens = maxTokens ?? _maxTokens;
            var timeoutMs = _isReasoningModel ? 600_000 : _defaultTimeoutMs;

            // Build request JSON manually for precise control
            var sb = new StringBuilder();
            sb.Append("{\"model\":").Append(JsonString(_model));
            sb.Append(",\"messages\":[");

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                sb.Append("{\"role\":\"system\",\"content\":").Append(JsonString(systemPrompt)).Append("},");
            }
            sb.Append("{\"role\":\"user\",\"content\":").Append(JsonString(prompt)).Append("}");
            sb.Append("]");

            if (UsesMaxCompletionTokens(_model))
            {
                sb.Append(",\"max_completion_tokens\":").Append(tokens);
                // No temperature for reasoning models
                if (!_isReasoningModel)
                    sb.Append(",\"temperature\":0.3");
            }
            else
            {
                sb.Append(",\"max_tokens\":").Append(tokens);
                sb.Append(",\"temperature\":0.3");
            }

            sb.Append("}");

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                AddOpenRouterHeaders(request);

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(timeoutMs);
                    var response = await Http.SendAsync(request, cts.Token);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException(EnrichErrorMessage(OpenAiProviderLabel(), (int)response.StatusCode, body, _model));

                    return ExtractOpenAiContent(body);
                }
            }
        }

        private async Task<string> CallClaudeAsync(
            string prompt, string systemPrompt, int? maxTokens,
            CancellationToken ct)
        {
            var url = "https://api.anthropic.com/v1/messages";
            var tokens = maxTokens ?? _maxTokens;

            // Timeout scales with prompt size and output token limit
            var promptLen = (prompt?.Length ?? 0) + (systemPrompt?.Length ?? 0);
            int timeoutMs;
            if (promptLen > 50000) timeoutMs = 300_000;
            else if (promptLen > 20000) timeoutMs = 180_000;
            else timeoutMs = 120_000;
            // Large output requests (e.g. prompt generation) need more time
            if (tokens > 8192) timeoutMs = Math.Max(timeoutMs, 600_000);

            var sb = new StringBuilder();
            sb.Append("{\"model\":").Append(JsonString(_model));
            sb.Append(",\"max_tokens\":").Append(tokens);

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                sb.Append(",\"system\":").Append(JsonString(systemPrompt));
            }

            sb.Append(",\"messages\":[{\"role\":\"user\",\"content\":").Append(JsonString(prompt)).Append("}]");
            sb.Append("}");

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
                request.Headers.Add("x-api-key", _apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(timeoutMs);
                    var response = await Http.SendAsync(request, cts.Token);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException(EnrichErrorMessage("Claude", (int)response.StatusCode, body, _model));

                    return ExtractClaudeContent(body);
                }
            }
        }

        private async Task<string> CallGeminiAsync(
            string prompt, string systemPrompt, int? maxTokens,
            CancellationToken ct)
        {
            var tokens = maxTokens ?? _maxTokens;
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent";

            var sb = new StringBuilder();
            sb.Append("{\"contents\":[{\"parts\":[{\"text\":").Append(JsonString(prompt)).Append("}]}]");

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                sb.Append(",\"systemInstruction\":{\"parts\":[{\"text\":").Append(JsonString(systemPrompt)).Append("}]}");
            }

            sb.Append(",\"generationConfig\":{\"maxOutputTokens\":").Append(tokens);
            sb.Append(",\"temperature\":0.3}");
            sb.Append("}");

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.Add("x-goog-api-key", _apiKey);
                request.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(_defaultTimeoutMs);
                    var response = await Http.SendAsync(request, cts.Token);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException(EnrichErrorMessage("Gemini", (int)response.StatusCode, body, _model));

                    return ExtractGeminiContent(body);
                }
            }
        }

        private async Task<string> CallOllamaAsync(
            string prompt, string systemPrompt, int? maxTokens,
            CancellationToken ct)
        {
            var endpoint = _baseUrl ?? "http://localhost:11434";
            var url = $"{endpoint.TrimEnd('/')}/api/chat";
            var tokens = maxTokens ?? 8192;

            var sb = new StringBuilder();
            sb.Append("{\"model\":").Append(JsonString(_model));
            sb.Append(",\"messages\":[");

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                sb.Append("{\"role\":\"system\",\"content\":").Append(JsonString(systemPrompt)).Append("},");
            }
            sb.Append("{\"role\":\"user\",\"content\":").Append(JsonString(prompt)).Append("}");
            sb.Append("]");

            sb.Append(",\"stream\":false");
            sb.Append(",\"options\":{\"temperature\":0.3,\"num_predict\":").Append(tokens).Append("}");
            sb.Append("}");

            // Timeout based on model size
            var timeoutMs = GetOllamaTimeout();

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(timeoutMs);
                    var response = await Http.SendAsync(request, cts.Token);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException(EnrichErrorMessage("Ollama", (int)response.StatusCode, body, _model));

                    return ExtractOllamaContent(body);
                }
            }
        }

        private async Task<string> TestOllamaConnectionAsync(CancellationToken ct)
        {
            var endpoint = _baseUrl ?? "http://localhost:11434";
            var url = $"{endpoint.TrimEnd('/')}/api/tags";

            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(10_000);
                    var response = await Http.GetAsync(url, cts.Token);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                        return $"Ollama returned status {(int)response.StatusCode}";

                    // Check if the selected model is available
                    if (!body.Contains(_model))
                        return $"Ollama is running but model '{_model}' is not installed. Run: ollama pull {_model}";

                    return null; // success
                }
            }
            catch (TaskCanceledException)
            {
                return $"Could not connect to Ollama at {endpoint} (timeout)";
            }
            catch (HttpRequestException ex)
            {
                return $"Could not connect to Ollama at {endpoint}: {ex.Message}";
            }
        }

        // ─── Multi-Turn Chat Implementations ──────────────────────────

        private async Task<string> CallOpenAiChatAsync(
            List<ChatMessage> messages, string systemPrompt, int? maxTokens,
            CancellationToken ct)
        {
            var baseUrl = ResolveOpenAiBaseUrl();

            var url = $"{baseUrl}/chat/completions";
            var tokens = maxTokens ?? _maxTokens;
            var timeoutMs = _isReasoningModel ? 600_000 : _defaultTimeoutMs;

            var sb = new StringBuilder();
            sb.Append("{\"model\":").Append(JsonString(_model));
            sb.Append(",\"messages\":[");

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                sb.Append("{\"role\":\"system\",\"content\":").Append(JsonString(systemPrompt)).Append("},");
            }

            for (int i = 0; i < messages.Count; i++)
            {
                var role = messages[i].Role == ChatRole.User ? "user" : "assistant";
                sb.Append("{\"role\":").Append(JsonString(role));

                if (messages[i].HasImages && role == "user")
                {
                    // Multimodal: content is an array of text + image_url parts
                    sb.Append(",\"content\":[");
                    sb.Append("{\"type\":\"text\",\"text\":").Append(JsonString(messages[i].Content)).Append("}");
                    foreach (var img in messages[i].Images)
                    {
                        sb.Append(",{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:")
                          .Append(img.MimeType).Append(";base64,").Append(ToBase64(img.Data)).Append("\"}}");
                    }
                    sb.Append("]");
                }
                else
                {
                    sb.Append(",\"content\":").Append(JsonString(messages[i].Content));
                }

                sb.Append("}");
                if (i < messages.Count - 1) sb.Append(",");
            }

            sb.Append("]");

            if (UsesMaxCompletionTokens(_model))
            {
                sb.Append(",\"max_completion_tokens\":").Append(tokens);
                if (!_isReasoningModel)
                    sb.Append(",\"temperature\":0.3");
            }
            else
            {
                sb.Append(",\"max_tokens\":").Append(tokens);
                sb.Append(",\"temperature\":0.3");
            }

            sb.Append("}");

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                AddOpenRouterHeaders(request);

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(timeoutMs);
                    var response = await Http.SendAsync(request, cts.Token);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException(EnrichErrorMessage(OpenAiProviderLabel(), (int)response.StatusCode, body, _model));

                    return ExtractOpenAiContent(body);
                }
            }
        }

        private async Task<string> CallClaudeChatAsync(
            List<ChatMessage> messages, string systemPrompt, int? maxTokens,
            CancellationToken ct)
        {
            var url = "https://api.anthropic.com/v1/messages";
            var tokens = maxTokens ?? _maxTokens;

            var totalLen = (systemPrompt?.Length ?? 0);
            foreach (var m in messages) totalLen += m.Content?.Length ?? 0;
            int timeoutMs;
            if (totalLen > 50000) timeoutMs = 300_000;
            else if (totalLen > 20000) timeoutMs = 180_000;
            else timeoutMs = 120_000;
            // Large output requests (e.g. prompt generation) need more time
            if (tokens > 8192) timeoutMs = Math.Max(timeoutMs, 600_000);

            var sb = new StringBuilder();
            sb.Append("{\"model\":").Append(JsonString(_model));
            sb.Append(",\"max_tokens\":").Append(tokens);

            if (!string.IsNullOrEmpty(systemPrompt))
                sb.Append(",\"system\":").Append(JsonString(systemPrompt));

            // Claude requires alternating user/assistant messages
            sb.Append(",\"messages\":[");
            for (int i = 0; i < messages.Count; i++)
            {
                var role = messages[i].Role == ChatRole.User ? "user" : "assistant";
                sb.Append("{\"role\":").Append(JsonString(role));

                if (messages[i].HasImages && role == "user")
                {
                    // Multimodal: content is an array of text + image blocks
                    sb.Append(",\"content\":[");
                    foreach (var img in messages[i].Images)
                    {
                        sb.Append("{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":")
                          .Append(JsonString(img.MimeType))
                          .Append(",\"data\":\"").Append(ToBase64(img.Data)).Append("\"}},");
                    }
                    sb.Append("{\"type\":\"text\",\"text\":").Append(JsonString(messages[i].Content)).Append("}");
                    sb.Append("]");
                }
                else
                {
                    sb.Append(",\"content\":").Append(JsonString(messages[i].Content));
                }

                sb.Append("}");
                if (i < messages.Count - 1) sb.Append(",");
            }
            sb.Append("]");
            sb.Append("}");

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
                request.Headers.Add("x-api-key", _apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(timeoutMs);
                    var response = await Http.SendAsync(request, cts.Token);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException(EnrichErrorMessage("Claude", (int)response.StatusCode, body, _model));

                    return ExtractClaudeContent(body);
                }
            }
        }

        private async Task<string> CallGeminiChatAsync(
            List<ChatMessage> messages, string systemPrompt, int? maxTokens,
            CancellationToken ct)
        {
            var tokens = maxTokens ?? _maxTokens;
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent";

            var sb = new StringBuilder();
            // Gemini uses "contents" array with "user" / "model" roles
            sb.Append("{\"contents\":[");
            for (int i = 0; i < messages.Count; i++)
            {
                var role = messages[i].Role == ChatRole.User ? "user" : "model";
                sb.Append("{\"role\":").Append(JsonString(role));
                sb.Append(",\"parts\":[");

                if (messages[i].HasImages && role == "user")
                {
                    // Multimodal: inline_data parts for images + text part
                    foreach (var img in messages[i].Images)
                    {
                        sb.Append("{\"inline_data\":{\"mime_type\":")
                          .Append(JsonString(img.MimeType))
                          .Append(",\"data\":\"").Append(ToBase64(img.Data)).Append("\"}},");
                    }
                }

                sb.Append("{\"text\":").Append(JsonString(messages[i].Content)).Append("}");
                sb.Append("]}");
                if (i < messages.Count - 1) sb.Append(",");
            }
            sb.Append("]");

            if (!string.IsNullOrEmpty(systemPrompt))
                sb.Append(",\"systemInstruction\":{\"parts\":[{\"text\":").Append(JsonString(systemPrompt)).Append("}]}");

            sb.Append(",\"generationConfig\":{\"maxOutputTokens\":").Append(tokens);
            sb.Append(",\"temperature\":0.3}");
            sb.Append("}");

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.Add("x-goog-api-key", _apiKey);
                request.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(_defaultTimeoutMs);
                    var response = await Http.SendAsync(request, cts.Token);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException(EnrichErrorMessage("Gemini", (int)response.StatusCode, body, _model));

                    return ExtractGeminiContent(body);
                }
            }
        }

        private async Task<string> CallOllamaChatAsync(
            List<ChatMessage> messages, string systemPrompt, int? maxTokens,
            CancellationToken ct)
        {
            var endpoint = _baseUrl ?? "http://localhost:11434";
            var url = $"{endpoint.TrimEnd('/')}/api/chat";
            var tokens = maxTokens ?? 8192;

            var sb = new StringBuilder();
            sb.Append("{\"model\":").Append(JsonString(_model));
            sb.Append(",\"messages\":[");

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                sb.Append("{\"role\":\"system\",\"content\":").Append(JsonString(systemPrompt)).Append("},");
            }

            for (int i = 0; i < messages.Count; i++)
            {
                var role = messages[i].Role == ChatRole.User ? "user" : "assistant";
                sb.Append("{\"role\":").Append(JsonString(role));
                sb.Append(",\"content\":").Append(JsonString(messages[i].Content));

                // Ollama uses a top-level "images" array on the message for vision
                if (messages[i].HasImages && role == "user")
                {
                    sb.Append(",\"images\":[");
                    for (int j = 0; j < messages[i].Images.Count; j++)
                    {
                        if (j > 0) sb.Append(",");
                        sb.Append("\"").Append(ToBase64(messages[i].Images[j].Data)).Append("\"");
                    }
                    sb.Append("]");
                }

                sb.Append("}");
                if (i < messages.Count - 1) sb.Append(",");
            }

            sb.Append("]");
            sb.Append(",\"stream\":false");
            sb.Append(",\"options\":{\"temperature\":0.3,\"num_predict\":").Append(tokens).Append("}");
            sb.Append("}");

            var timeoutMs = GetOllamaTimeout();

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(timeoutMs);
                    var response = await Http.SendAsync(request, cts.Token);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException(EnrichErrorMessage("Ollama", (int)response.StatusCode, body, _model));

                    return ExtractOllamaContent(body);
                }
            }
        }

        // ─── Response Extraction ─────────────────────────────────────

        private static string ExtractOpenAiContent(string json)
        {
            // Extract choices[0].message.content
            var contentMatch = Regex.Match(json,
                @"""choices""\s*:\s*\[\s*\{[^}]*""message""\s*:\s*\{[^}]*""content""\s*:\s*""((?:[^""\\]|\\.)*)""",
                RegexOptions.Singleline);
            if (contentMatch.Success)
                return UnescapeJson(contentMatch.Groups[1].Value);

            throw new InvalidOperationException("Could not parse OpenAI response");
        }

        private static string ExtractClaudeContent(string json)
        {
            // Extract content[0].text
            var textMatch = Regex.Match(json,
                @"""content""\s*:\s*\[\s*\{[^}]*""text""\s*:\s*""((?:[^""\\]|\\.)*)""",
                RegexOptions.Singleline);
            if (textMatch.Success)
                return UnescapeJson(textMatch.Groups[1].Value);

            throw new InvalidOperationException("Could not parse Claude response");
        }

        private static string ExtractGeminiContent(string json)
        {
            // Extract candidates[0].content.parts[0].text — anchored to Gemini response structure
            var textMatch = Regex.Match(json,
                @"""candidates""\s*:\s*\[\s*\{[^}]*""content""\s*:\s*\{[^}]*""parts""\s*:\s*\[\s*\{[^}]*""text""\s*:\s*""((?:[^""\\]|\\.)*)""",
                RegexOptions.Singleline);
            if (textMatch.Success)
                return UnescapeJson(textMatch.Groups[1].Value);

            throw new InvalidOperationException("Could not parse Gemini response");
        }

        private static string ExtractOllamaContent(string json)
        {
            // Extract message.content
            var contentMatch = Regex.Match(json,
                @"""message""\s*:\s*\{[^}]*""content""\s*:\s*""((?:[^""\\]|\\.)*)""",
                RegexOptions.Singleline);
            if (contentMatch.Success)
                return UnescapeJson(contentMatch.Groups[1].Value);

            throw new InvalidOperationException("Could not parse Ollama response");
        }

        // ─── Response Cleaning ───────────────────────────────────────

        /// <summary>
        /// Removes prompt remnants that LLMs sometimes include in translations.
        /// Ported from Python Supervertaler's _clean_translation_response().
        /// </summary>
        internal static string CleanTranslationResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return response;

            var original = response.Trim();

            // 1. Check for delimiter markers
            var delimiters = new[]
            {
                "**YOUR TRANSLATION**", "**JOUW VERTALING**",
                "**TRANSLATION**", "**VERTALING**",
                "Translation:", "Vertaling:"
            };

            foreach (var delim in delimiters)
            {
                var idx = original.IndexOf(delim, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var after = original.Substring(idx + delim.Length).TrimStart(':', ' ', '\n', '\r');
                    if (after.Length > 0 && after.Length < original.Length * 0.9)
                        return after.Trim();
                }
            }

            // 2. Check for prompt pattern leakage (only for long responses)
            if (original.Length > 300)
            {
                var patternCount = 0;
                foreach (var pattern in PromptPatterns)
                {
                    if (original.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        patternCount++;
                }

                if (patternCount >= 3)
                {
                    // Try line-by-line filtering
                    var lines = original.Split(new[] { '\n' }, StringSplitOptions.None);
                    var cleanLines = new List<string>();
                    foreach (var line in lines)
                    {
                        bool isPrompt = false;
                        foreach (var pattern in PromptPatterns)
                        {
                            if (line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                isPrompt = true;
                                break;
                            }
                        }
                        if (!isPrompt)
                            cleanLines.Add(line);
                    }

                    var cleaned = string.Join("\n", cleanLines).Trim();
                    if (cleaned.Length > 0 && cleaned.Length < original.Length * 0.7)
                        return cleaned;
                }
            }

            return original;
        }

        private static readonly string[] PromptPatterns =
        {
            "As a professional", "Als een professionele",
            "You are an expert", "U bent een expert",
            "Your task is to", "Uw taak is om",
            "During the translation process", "Tijdens het vertaalproces",
            "The output must consist exclusively", "De output moet",
            "PROFESSIONAL TRANSLATION CONTEXT", "PROFESSIONELE VERTAALCONTEXT",
            "professional translation", "technical manuals",
            "regulatory compliance", "medical devices",
            "CAT tool tags", "memoQ tags", "Trados Studio tags"
        };

        // ─── Image Helpers ───────────────────────────────────────────

        private static string ToBase64(byte[] data) => Convert.ToBase64String(data);

        // ─── JSON Helpers ────────────────────────────────────────────

        /// <summary>
        /// Encodes a string as a JSON string literal (with quotes).
        /// </summary>
        private static string JsonString(string value)
        {
            if (value == null) return "null";

            var sb = new StringBuilder(value.Length + 8);
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.AppendFormat("\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Unescapes a JSON string value (without surrounding quotes).
        /// </summary>
        private static string UnescapeJson(string escaped)
        {
            if (escaped == null) return null;

            // Handle \uXXXX sequences first, before simple replacements
            if (escaped.Contains("\\u"))
            {
                escaped = Regex.Replace(escaped, @"\\u([0-9A-Fa-f]{4})", m =>
                {
                    var code = Convert.ToInt32(m.Groups[1].Value, 16);
                    return ((char)code).ToString();
                });
            }

            return escaped
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
        }

        /// <summary>
        /// Extracts a string value from a simple JSON object: {"key": "value"}.
        /// </summary>
        private static string ExtractJsonString(string json, string key)
        {
            var pattern = $"\"{Regex.Escape(key)}\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"";
            var match = Regex.Match(json, pattern);
            return match.Success ? UnescapeJson(match.Groups[1].Value) : null;
        }

        /// <summary>
        /// Extracts a string from a nested JSON: {"outer": {"inner": "value"}}.
        /// </summary>
        private static string ExtractNestedJsonString(string json, string outerKey, string innerKey)
        {
            // Find the outer object
            var outerPattern = $"\"{Regex.Escape(outerKey)}\"\\s*:\\s*\\{{";
            var outerMatch = Regex.Match(json, outerPattern);
            if (!outerMatch.Success) return null;

            // Search within a reasonable range after the outer key
            var startIdx = outerMatch.Index + outerMatch.Length;
            var searchLen = Math.Min(2000, json.Length - startIdx);
            var section = json.Substring(startIdx, searchLen);

            return ExtractJsonString(section, innerKey);
        }

        private static string TruncateError(string body)
        {
            if (body == null) return "";
            return body.Length > 500 ? body.Substring(0, 500) + "..." : body;
        }

        /// <summary>
        /// Enriches common API errors with user-friendly guidance.
        /// </summary>
        private static string EnrichErrorMessage(string provider, int statusCode, string body, string model)
        {
            var raw = $"{provider} API error {statusCode}: {TruncateError(body)}";

            // OpenAI 403 "model_not_found" — model not enabled in project
            if (statusCode == 403 && body != null &&
                (body.Contains("model_not_found") || body.Contains("does not have access to model")))
            {
                return raw + $"\n\nThe model \"{model}\" is not enabled in your OpenAI project. " +
                       "To fix this, go to platform.openai.com \u2192 Settings \u2192 your project \u2192 Model access, " +
                       "and enable the model.";
            }

            // 401 Unauthorized — invalid or missing API key
            if (statusCode == 401)
            {
                return raw + $"\n\nYour {provider} API key appears to be invalid or expired. " +
                       "Check Settings \u2192 AI Settings to verify your API key.";
            }

            // 429 Rate limit
            if (statusCode == 429)
            {
                return raw + "\n\nYou have hit the API rate limit. Wait a moment and try again, " +
                       "or check your usage limits on your provider's dashboard.";
            }

            // 402 / insufficient funds
            if (statusCode == 402 || (body != null && body.Contains("insufficient_quota")))
            {
                return raw + $"\n\nYour {provider} account has insufficient credit. " +
                       "Add funds on your provider's billing page.";
            }

            return raw;
        }

        private static bool IsReasoningModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            var info = LlmModels.FindModel(model);
            if (info != null) return info.IsReasoningModel;
            // Fallback heuristic for custom/unknown model IDs
            var lower = model.ToLowerInvariant();
            return lower.Contains("reasoning") || lower.StartsWith("o1") || lower.StartsWith("o3") || lower.StartsWith("o4");
        }

        /// <summary>
        /// Returns true if the model requires max_completion_tokens instead of max_tokens.
        /// GPT-5.x and reasoning models all use the newer parameter.
        /// </summary>
        private static bool UsesMaxCompletionTokens(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            if (IsReasoningModel(model)) return true;
            var lower = model.ToLowerInvariant();
            return lower.StartsWith("gpt-5");
        }

        private int GetOllamaTimeout()
        {
            if (_ollamaTimeoutMinutes > 0)
                return _ollamaTimeoutMinutes * 60_000;

            var lower = _model.ToLowerInvariant();
            if (lower.Contains("14b") || lower.Contains("13b") || lower.Contains("20b"))
                return 600_000;
            if (lower.Contains("7b") || lower.Contains("8b") || lower.Contains("9b")
                || lower.Contains("12b") || lower.Contains("11b") || lower.Contains("10b"))
                return 300_000;
            return 180_000;
        }

        // ─── Tool Use Support ────────────────────────────────────────

        /// <summary>
        /// Callback delegate for tool execution. The caller provides a function
        /// that takes a tool name and input JSON, and returns a result JSON string.
        /// </summary>
        public delegate string ToolExecutor(string toolName, string inputJson);

        /// <summary>
        /// Sends a multi-turn chat to Claude with tool definitions.
        /// When Claude returns a tool_use response, the executor callback is invoked
        /// and the result is sent back automatically. This loops until Claude returns
        /// a final text response (max 5 tool rounds to prevent infinite loops).
        ///
        /// Only supported for the Claude provider — other providers fall back to
        /// regular SendChatAsync (no tool use).
        /// </summary>
        /// <param name="toolStatusCallback">Optional callback invoked on the UI thread
        /// when a tool is being called, with the tool name as argument.</param>
        public async Task<string> SendChatWithToolsAsync(
            List<ChatMessage> messages,
            string systemPrompt,
            string toolDefinitionsJson,
            ToolExecutor executor,
            int? maxTokens = null,
            CancellationToken cancellationToken = default,
            PromptLogFeature? feature = null,
            string promptName = null,
            Action<string> toolStatusCallback = null)
        {
            // Tool use is only supported for Claude
            if (_provider != LlmModels.ProviderClaude)
                return await SendChatAsync(messages, systemPrompt, maxTokens, cancellationToken, feature, promptName);

            var sw = Stopwatch.StartNew();
            string result = null;
            string errorMsg = null;

            try
            {
                result = await CallClaudeChatWithToolsAsync(
                    messages, systemPrompt, toolDefinitionsJson, executor,
                    maxTokens, cancellationToken, toolStatusCallback);
                return result;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                throw;
            }
            finally
            {
                sw.Stop();
                if (feature.HasValue && feature.Value != PromptLogFeature.ConnectionTest)
                    RaisePromptCompleted(feature.Value, systemPrompt, null, messages, result, errorMsg, sw.Elapsed, promptName);
            }
        }

        private async Task<string> CallClaudeChatWithToolsAsync(
            List<ChatMessage> messages,
            string systemPrompt,
            string toolDefinitionsJson,
            ToolExecutor executor,
            int? maxTokens,
            CancellationToken ct,
            Action<string> toolStatusCallback)
        {
            var url = "https://api.anthropic.com/v1/messages";
            var tokens = maxTokens ?? _maxTokens;
            const int maxToolRounds = 5;

            // Build mutable conversation for tool loop
            var conversation = new List<ConversationTurn>();
            foreach (var m in messages)
            {
                conversation.Add(new ConversationTurn
                {
                    Role = m.Role == ChatRole.User ? "user" : "assistant",
                    ContentJson = m.HasImages && m.Role == ChatRole.User
                        ? BuildClaudeImageContent(m)
                        : JsonString(m.Content)
                });
            }

            for (int round = 0; round < maxToolRounds; round++)
            {
                // Build request JSON
                var sb = new StringBuilder(4096);
                sb.Append("{\"model\":").Append(JsonString(_model));
                sb.Append(",\"max_tokens\":").Append(tokens);

                if (!string.IsNullOrEmpty(systemPrompt))
                    sb.Append(",\"system\":").Append(JsonString(systemPrompt));

                // Tools
                sb.Append(",\"tools\":").Append(toolDefinitionsJson);

                // Messages
                sb.Append(",\"messages\":[");
                for (int i = 0; i < conversation.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"role\":").Append(JsonString(conversation[i].Role));
                    sb.Append(",\"content\":").Append(conversation[i].ContentJson);
                    sb.Append("}");
                }
                sb.Append("]}");

                // Calculate timeout
                var totalLen = (systemPrompt?.Length ?? 0) + sb.Length;
                int timeoutMs;
                if (totalLen > 50000) timeoutMs = 300_000;
                else if (totalLen > 20000) timeoutMs = 180_000;
                else timeoutMs = 120_000;
                if (tokens > 8192) timeoutMs = Math.Max(timeoutMs, 600_000);

                string body;
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
                    request.Headers.Add("x-api-key", _apiKey);
                    request.Headers.Add("anthropic-version", "2023-06-01");

                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        cts.CancelAfter(timeoutMs);
                        var response = await Http.SendAsync(request, cts.Token);
                        body = await response.Content.ReadAsStringAsync();

                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException(EnrichErrorMessage("Claude", (int)response.StatusCode, body, _model));
                    }
                }

                // Check stop_reason
                var stopReason = ExtractJsonFieldStatic(body, "stop_reason");

                if (stopReason == "tool_use")
                {
                    // Extract tool calls from content array
                    var toolCalls = ExtractToolUseCalls(body);
                    if (toolCalls.Count == 0)
                        throw new InvalidOperationException("Claude indicated tool_use but no tool calls found in response.");

                    // Add assistant message with full content (text + tool_use blocks)
                    var assistantContent = ExtractContentArray(body);
                    conversation.Add(new ConversationTurn
                    {
                        Role = "assistant",
                        ContentJson = assistantContent
                    });

                    // Execute each tool and build tool_result content blocks
                    var resultSb = new StringBuilder();
                    resultSb.Append("[");
                    for (int t = 0; t < toolCalls.Count; t++)
                    {
                        var tc = toolCalls[t];

                        // Notify UI about tool execution
                        toolStatusCallback?.Invoke(tc.Name);

                        // Execute the tool
                        var toolResult = executor(tc.Name, tc.InputJson);

                        if (t > 0) resultSb.Append(",");
                        resultSb.Append("{\"type\":\"tool_result\"");
                        resultSb.Append(",\"tool_use_id\":").Append(JsonString(tc.Id));
                        resultSb.Append(",\"content\":").Append(JsonString(toolResult ?? ""));
                        resultSb.Append("}");
                    }
                    resultSb.Append("]");

                    // Add user message with tool results
                    conversation.Add(new ConversationTurn
                    {
                        Role = "user",
                        ContentJson = resultSb.ToString()
                    });

                    // Continue the loop — Claude will process the tool results
                    continue;
                }

                // stop_reason is "end_turn" or similar — extract final text
                return ExtractClaudeTextFromContent(body);
            }

            throw new InvalidOperationException("Tool use loop exceeded maximum rounds.");
        }

        /// <summary>
        /// Builds a Claude content array JSON for a message with images.
        /// </summary>
        private static string BuildClaudeImageContent(ChatMessage msg)
        {
            var sb = new StringBuilder();
            sb.Append("[");
            if (msg.HasImages)
            {
                foreach (var img in msg.Images)
                {
                    sb.Append("{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":")
                      .Append(JsonString(img.MimeType))
                      .Append(",\"data\":\"").Append(ToBase64(img.Data)).Append("\"}},");
                }
            }
            sb.Append("{\"type\":\"text\",\"text\":").Append(JsonString(msg.Content)).Append("}");
            sb.Append("]");
            return sb.ToString();
        }

        /// <summary>
        /// Extracts tool_use blocks from Claude's response content array.
        /// Uses bracket-counting to find each object in the content array,
        /// then checks for "tool_use" type and extracts fields by name
        /// (order-independent).
        /// </summary>
        private static List<ToolCall> ExtractToolUseCalls(string json)
        {
            var calls = new List<ToolCall>();

            // First, extract the content array
            var contentArray = ExtractContentArray(json);
            if (string.IsNullOrEmpty(contentArray) || contentArray == "[]")
                return calls;

            // Find each top-level object in the content array
            var objects = ExtractTopLevelObjects(contentArray);
            foreach (var obj in objects)
            {
                // Check if this is a tool_use block
                var type = ExtractJsonFieldStatic(obj, "type");
                if (type != "tool_use") continue;

                var id = ExtractJsonFieldStatic(obj, "id");
                var name = ExtractJsonFieldStatic(obj, "name");

                // Extract "input" as a JSON object (may contain nested braces)
                var inputJson = ExtractJsonObject(obj, "input");

                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                {
                    calls.Add(new ToolCall
                    {
                        Id = id,
                        Name = name,
                        InputJson = inputJson ?? "{}"
                    });
                }
            }

            return calls;
        }

        /// <summary>
        /// Extracts the raw "content" JSON array from a Claude response.
        /// Uses bracket-counting to handle nested objects correctly.
        /// </summary>
        private static string ExtractContentArray(string json)
        {
            // Find "content" followed by : and [
            var keyIdx = json.IndexOf("\"content\"");
            if (keyIdx < 0) return "[]";

            var bracketStart = json.IndexOf('[', keyIdx + 9);
            if (bracketStart < 0) return "[]";

            // Count brackets to find matching close
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = bracketStart; i < json.Length; i++)
            {
                var c = json[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '[') depth++;
                else if (c == ']') depth--;
                if (depth == 0)
                    return json.Substring(bracketStart, i - bracketStart + 1);
            }

            return "[]";
        }

        /// <summary>
        /// Extracts top-level JSON objects from a JSON array string.
        /// E.g. from '[{"a":1},{"b":2}]' returns ["{"a":1}", "{"b":2}"].
        /// </summary>
        private static List<string> ExtractTopLevelObjects(string arrayJson)
        {
            var results = new List<string>();
            int depth = 0;
            int objStart = -1;
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < arrayJson.Length; i++)
            {
                var c = arrayJson[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;

                if (c == '{')
                {
                    if (depth == 0) objStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        results.Add(arrayJson.Substring(objStart, i - objStart + 1));
                        objStart = -1;
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Extracts a JSON object value for a given key from a JSON string.
        /// E.g. from '{"input": {"foo": "bar"}}' with key "input" returns '{"foo": "bar"}'.
        /// Uses bracket-counting to handle nested objects.
        /// </summary>
        private static string ExtractJsonObject(string json, string key)
        {
            var keyPattern = "\"" + key + "\"";
            var keyIdx = json.IndexOf(keyPattern);
            if (keyIdx < 0) return null;

            // Find the opening brace after the colon
            var afterKey = keyIdx + keyPattern.Length;
            var braceStart = json.IndexOf('{', afterKey);
            if (braceStart < 0) return null;

            // Verify there's only whitespace and colon between key and brace
            var between = json.Substring(afterKey, braceStart - afterKey).Trim();
            if (!between.StartsWith(":")) return null;

            // Count braces to find matching close
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = braceStart; i < json.Length; i++)
            {
                var c = json[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') depth++;
                else if (c == '}') depth--;
                if (depth == 0)
                    return json.Substring(braceStart, i - braceStart + 1);
            }

            return null;
        }

        /// <summary>
        /// Extracts text content from Claude's content array, skipping tool_use blocks.
        /// </summary>
        private static string ExtractClaudeTextFromContent(string json)
        {
            // Find all text blocks in the content array
            var pattern = @"\{""type""\s*:\s*""text""\s*,\s*""text""\s*:\s*""((?:[^""\\]|\\.)*)""";
            var matches = Regex.Matches(json, pattern, RegexOptions.Singleline);

            if (matches.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (Match m in matches)
                {
                    if (sb.Length > 0) sb.Append("\n");
                    sb.Append(UnescapeJson(m.Groups[1].Value));
                }
                return sb.ToString();
            }

            // Fallback to standard extraction
            return ExtractClaudeContent(json);
        }

        private static string ExtractJsonFieldStatic(string json, string fieldName)
        {
            var pattern = $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"";
            var match = Regex.Match(json, pattern);
            return match.Success ? match.Groups[1].Value : null;
        }

        private class ConversationTurn
        {
            public string Role;
            /// <summary>
            /// Either a JSON string literal (for text messages) or a JSON array
            /// (for tool_use/tool_result content blocks).
            /// </summary>
            public string ContentJson;
        }

        private class ToolCall
        {
            public string Id;
            public string Name;
            public string InputJson;
        }

        public void Dispose()
        {
            // HttpClient is static and shared — do not dispose it here
        }
    }
}

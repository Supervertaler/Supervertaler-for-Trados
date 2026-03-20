using System;
using System.Collections.Generic;
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
        // HttpClient is designed to be reused across the app lifetime
        private static readonly HttpClient Http;

        static LlmClient()
        {
            var handler = new HttpClientHandler();
            Http = new HttpClient(handler);
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

        public LlmClient(string provider, string model, string apiKey,
                          string baseUrl = null, int maxTokens = 16384)
        {
            _provider = provider ?? LlmModels.ProviderOpenAi;
            _model = model ?? "gpt-4o";
            _apiKey = apiKey ?? "";
            _baseUrl = baseUrl;
            _maxTokens = maxTokens;

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
        public async Task<string> SendPromptAsync(
            string prompt,
            string systemPrompt = null,
            int? maxTokens = null,
            CancellationToken cancellationToken = default)
        {
            switch (_provider)
            {
                case LlmModels.ProviderOpenAi:
                case LlmModels.ProviderGrok:
                case LlmModels.ProviderCustomOpenAi:
                    return await CallOpenAiAsync(prompt, systemPrompt, maxTokens, cancellationToken);
                case LlmModels.ProviderClaude:
                    return await CallClaudeAsync(prompt, systemPrompt, maxTokens, cancellationToken);
                case LlmModels.ProviderGemini:
                    return await CallGeminiAsync(prompt, systemPrompt, maxTokens, cancellationToken);
                case LlmModels.ProviderOllama:
                    return await CallOllamaAsync(prompt, systemPrompt, maxTokens, cancellationToken);
                default:
                    throw new ArgumentException($"Unsupported provider: {_provider}");
            }
        }

        /// <summary>
        /// Sends a multi-turn chat conversation to the LLM.
        /// Used by the AI Assistant for conversation continuity.
        /// </summary>
        public async Task<string> SendChatAsync(
            List<ChatMessage> messages,
            string systemPrompt = null,
            int? maxTokens = null,
            CancellationToken cancellationToken = default)
        {
            switch (_provider)
            {
                case LlmModels.ProviderOpenAi:
                case LlmModels.ProviderGrok:
                case LlmModels.ProviderCustomOpenAi:
                    return await CallOpenAiChatAsync(messages, systemPrompt, maxTokens, cancellationToken);
                case LlmModels.ProviderClaude:
                    return await CallClaudeChatAsync(messages, systemPrompt, maxTokens, cancellationToken);
                case LlmModels.ProviderGemini:
                    return await CallGeminiChatAsync(messages, systemPrompt, maxTokens, cancellationToken);
                case LlmModels.ProviderOllama:
                    return await CallOllamaChatAsync(messages, systemPrompt, maxTokens, cancellationToken);
                default:
                    throw new ArgumentException($"Unsupported provider: {_provider}");
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
                        paths.Add(Path.Combine(userDataPath, "settings", "settings.json"));
                }
                catch { }
            }

            // Path 2: ~/Supervertaler/settings/settings.json
            paths.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Supervertaler", "settings", "settings.json"));

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
            if (_provider == LlmModels.ProviderCustomOpenAi && !string.IsNullOrEmpty(_baseUrl))
                return _baseUrl.TrimEnd('/');
            return "https://api.openai.com/v1";
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

            if (_isReasoningModel)
            {
                sb.Append(",\"max_completion_tokens\":").Append(tokens);
                // No temperature for reasoning models
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

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(timeoutMs);
                    var response = await Http.SendAsync(request, cts.Token);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException($"{OpenAiProviderLabel()} API error {(int)response.StatusCode}: {TruncateError(body)}");

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
                        throw new HttpRequestException($"Claude API error {(int)response.StatusCode}: {TruncateError(body)}");

                    return ExtractClaudeContent(body);
                }
            }
        }

        private async Task<string> CallGeminiAsync(
            string prompt, string systemPrompt, int? maxTokens,
            CancellationToken ct)
        {
            var tokens = maxTokens ?? _maxTokens;
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

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
                request.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(_defaultTimeoutMs);
                    var response = await Http.SendAsync(request, cts.Token);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException($"Gemini API error {(int)response.StatusCode}: {TruncateError(body)}");

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
                        throw new HttpRequestException($"Ollama API error {(int)response.StatusCode}: {TruncateError(body)}");

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

            if (_isReasoningModel)
                sb.Append(",\"max_completion_tokens\":").Append(tokens);
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

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(timeoutMs);
                    var response = await Http.SendAsync(request, cts.Token);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException($"{OpenAiProviderLabel()} API error {(int)response.StatusCode}: {TruncateError(body)}");

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
                        throw new HttpRequestException($"Claude API error {(int)response.StatusCode}: {TruncateError(body)}");

                    return ExtractClaudeContent(body);
                }
            }
        }

        private async Task<string> CallGeminiChatAsync(
            List<ChatMessage> messages, string systemPrompt, int? maxTokens,
            CancellationToken ct)
        {
            var tokens = maxTokens ?? _maxTokens;
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

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
                request.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(_defaultTimeoutMs);
                    var response = await Http.SendAsync(request, cts.Token);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException($"Gemini API error {(int)response.StatusCode}: {TruncateError(body)}");

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
                        throw new HttpRequestException($"Ollama API error {(int)response.StatusCode}: {TruncateError(body)}");

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
            // Extract candidates[0].content.parts[0].text
            var textMatch = Regex.Match(json,
                @"""text""\s*:\s*""((?:[^""\\]|\\.)*)""",
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

        private static bool IsReasoningModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            var lower = model.ToLowerInvariant();
            return lower.Contains("gpt-5") || lower.StartsWith("o1") || lower.StartsWith("o3");
        }

        private int GetOllamaTimeout()
        {
            var lower = _model.ToLowerInvariant();
            if (lower.Contains("14b") || lower.Contains("13b") || lower.Contains("20b"))
                return 600_000;
            if (lower.Contains("7b") || lower.Contains("8b") || lower.Contains("9b"))
                return 300_000;
            return 180_000;
        }

        public void Dispose()
        {
            // HttpClient is static and shared — do not dispose it here
        }
    }
}

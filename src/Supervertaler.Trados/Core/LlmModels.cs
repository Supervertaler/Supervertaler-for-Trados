using System;
using System.Collections.Generic;

namespace Supervertaler.Trados.Core
{
    public enum LlmProvider
    {
        OpenAi,
        Claude,
        Gemini,
        Ollama,
        CustomOpenAi
    }

    public class LlmModelInfo
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public LlmProvider Provider { get; set; }
        public bool IsReasoningModel { get; set; }
        public int DefaultTimeoutMs { get; set; } = 120_000;
        public int DefaultMaxTokens { get; set; } = 16384;
    }

    /// <summary>
    /// Static catalog of all supported LLM models and provider metadata.
    /// Mirrors Python Supervertaler's model definitions in modules/llm_clients.py.
    /// </summary>
    public static class LlmModels
    {
        // Provider key strings — match Python Supervertaler and JSON settings
        public const string ProviderOpenAi = "openai";
        public const string ProviderClaude = "claude";
        public const string ProviderGemini = "gemini";
        public const string ProviderOllama = "ollama";
        public const string ProviderCustomOpenAi = "custom_openai";

        public static readonly LlmModelInfo[] OpenAiModels =
        {
            new LlmModelInfo
            {
                Id = "gpt-4o", DisplayName = "GPT-4o",
                Description = "Best balance of speed, quality, and cost",
                Provider = LlmProvider.OpenAi
            },
            new LlmModelInfo
            {
                Id = "gpt-4o-mini", DisplayName = "GPT-4o Mini",
                Description = "Fast and affordable for simpler tasks",
                Provider = LlmProvider.OpenAi
            },
            new LlmModelInfo
            {
                Id = "gpt-5", DisplayName = "GPT-5",
                Description = "Advanced reasoning model — slower, higher quality",
                Provider = LlmProvider.OpenAi,
                IsReasoningModel = true,
                DefaultTimeoutMs = 600_000,
                DefaultMaxTokens = 32768
            },
            new LlmModelInfo
            {
                Id = "o1", DisplayName = "o1",
                Description = "Reasoning model for complex tasks",
                Provider = LlmProvider.OpenAi,
                IsReasoningModel = true,
                DefaultTimeoutMs = 600_000,
                DefaultMaxTokens = 32768
            },
            new LlmModelInfo
            {
                Id = "o3", DisplayName = "o3",
                Description = "Latest reasoning model",
                Provider = LlmProvider.OpenAi,
                IsReasoningModel = true,
                DefaultTimeoutMs = 600_000,
                DefaultMaxTokens = 32768
            }
        };

        public static readonly LlmModelInfo[] ClaudeModels =
        {
            new LlmModelInfo
            {
                Id = "claude-sonnet-4-6", DisplayName = "Claude Sonnet 4.6",
                Description = "Recommended — best balance of speed, quality, and cost",
                Provider = LlmProvider.Claude
            },
            new LlmModelInfo
            {
                Id = "claude-haiku-4-5-20251001", DisplayName = "Claude Haiku 4.5",
                Description = "Fast and affordable — good for large batch jobs",
                Provider = LlmProvider.Claude
            },
            new LlmModelInfo
            {
                Id = "claude-opus-4-6", DisplayName = "Claude Opus 4.6",
                Description = "Highest quality — best for specialized legal/technical translation",
                Provider = LlmProvider.Claude
            }
        };

        public static readonly LlmModelInfo[] GeminiModels =
        {
            new LlmModelInfo
            {
                Id = "gemini-2.5-flash", DisplayName = "Gemini 2.5 Flash",
                Description = "Fast and affordable",
                Provider = LlmProvider.Gemini
            },
            new LlmModelInfo
            {
                Id = "gemini-2.5-pro", DisplayName = "Gemini 2.5 Pro",
                Description = "Higher quality, supports complex translation",
                Provider = LlmProvider.Gemini
            },
            new LlmModelInfo
            {
                Id = "gemini-3-pro-preview", DisplayName = "Gemini 3 Pro Preview",
                Description = "Latest preview model",
                Provider = LlmProvider.Gemini
            }
        };

        public static readonly LlmModelInfo[] OllamaModels =
        {
            new LlmModelInfo
            {
                Id = "translategemma:12b", DisplayName = "TranslateGemma 12B",
                Description = "Best translation quality/size ratio (12 GB RAM)",
                Provider = LlmProvider.Ollama
            },
            new LlmModelInfo
            {
                Id = "translategemma:4b", DisplayName = "TranslateGemma 4B",
                Description = "Lightweight translation model (6 GB RAM)",
                Provider = LlmProvider.Ollama
            },
            new LlmModelInfo
            {
                Id = "qwen3:8b", DisplayName = "Qwen 3 8B",
                Description = "General-purpose, 100+ languages (8 GB RAM)",
                Provider = LlmProvider.Ollama
            },
            new LlmModelInfo
            {
                Id = "aya-expanse:8b", DisplayName = "Aya Expanse 8B",
                Description = "Top Dutch support, high fidelity (8 GB RAM)",
                Provider = LlmProvider.Ollama
            }
        };

        /// <summary>
        /// Returns the model array for a given provider key string.
        /// </summary>
        public static LlmModelInfo[] GetModelsForProvider(string providerKey)
        {
            switch (providerKey)
            {
                case ProviderOpenAi: return OpenAiModels;
                case ProviderClaude: return ClaudeModels;
                case ProviderGemini: return GeminiModels;
                case ProviderOllama: return OllamaModels;
                case ProviderCustomOpenAi: return new LlmModelInfo[0]; // Custom models are user-defined
                default: return new LlmModelInfo[0];
            }
        }

        /// <summary>
        /// Looks up a model by ID across all providers. Returns null if not found.
        /// </summary>
        public static LlmModelInfo FindModel(string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return null;

            var allArrays = new[] { OpenAiModels, ClaudeModels, GeminiModels, OllamaModels };
            foreach (var arr in allArrays)
            {
                foreach (var m in arr)
                {
                    if (string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))
                        return m;
                }
            }
            return null;
        }

        /// <summary>
        /// Returns the JSON-compatible provider key string for an enum value.
        /// </summary>
        public static string GetProviderKey(LlmProvider provider)
        {
            switch (provider)
            {
                case LlmProvider.OpenAi: return ProviderOpenAi;
                case LlmProvider.Claude: return ProviderClaude;
                case LlmProvider.Gemini: return ProviderGemini;
                case LlmProvider.Ollama: return ProviderOllama;
                case LlmProvider.CustomOpenAi: return ProviderCustomOpenAi;
                default: return ProviderOpenAi;
            }
        }

        /// <summary>
        /// Returns the display name for a provider key.
        /// </summary>
        public static string GetProviderDisplayName(string providerKey)
        {
            switch (providerKey)
            {
                case ProviderOpenAi: return "OpenAI";
                case ProviderClaude: return "Claude (Anthropic)";
                case ProviderGemini: return "Gemini (Google)";
                case ProviderOllama: return "Ollama (Local)";
                case ProviderCustomOpenAi: return "Custom (OpenAI-compatible)";
                default: return providerKey;
            }
        }

        /// <summary>
        /// All provider keys in display order.
        /// </summary>
        public static readonly string[] AllProviderKeys =
        {
            ProviderOpenAi, ProviderClaude, ProviderGemini, ProviderOllama, ProviderCustomOpenAi
        };
    }
}

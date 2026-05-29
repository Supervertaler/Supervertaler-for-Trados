using System.Collections.Generic;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Estimates token counts and API costs for AI calls.
    /// Uses chars/4 heuristic for token estimation (no external library).
    /// Pricing table based on official provider rates as of March 2026.
    /// </summary>
    public static class TokenEstimator
    {
        // Per-million-token pricing: (input, output)
        private static readonly Dictionary<string, (decimal inputPer1M, decimal outputPer1M)> Pricing
            = new Dictionary<string, (decimal, decimal)>
        {
            // OpenAI (current)
            { "gpt-5.5",                   (5.00m,   30.00m) },
            { "gpt-5.4-mini",              (0.75m,   4.50m)  },
            // OpenAI (legacy – kept for cost estimates if users still have these selected)
            { "gpt-5.4",                   (10.00m,  30.00m) },
            { "gpt-4.1",                   (2.00m,   8.00m)  },
            { "gpt-4.1-mini",              (0.40m,   1.60m)  },
            { "o4-mini",                   (1.10m,   4.40m)  },

            // Claude (Anthropic)
            { "claude-sonnet-4-6",         (3.00m,   15.00m) },
            { "claude-haiku-4-5-20251001", (1.00m,   5.00m)  },
            { "claude-opus-4-8",           (5.00m,   25.00m) },

            // Google Gemini
            { "gemini-3.1-flash-lite",     (0.25m,   1.50m)  },
            { "gemini-3.5-flash",          (1.50m,   9.00m)  },
            { "gemini-2.5-pro",            (1.25m,   10.00m) },
            { "gemini-3.1-pro-preview",    (2.00m,   12.00m) },

            // Grok (xAI)
            { "grok-4.3",                     (1.25m,  2.50m)  },

            // Mistral AI
            { "mistral-large-latest",         (2.00m,  6.00m)  },
            { "mistral-small-latest",         (0.10m,  0.30m)  },

            // Ollama (local) – free
            { "translategemma:12b",        (0m, 0m) },
            { "translategemma:4b",         (0m, 0m) },
            { "qwen3:14b",                (0m, 0m) },
            { "aya-expanse:8b",            (0m, 0m) },
        };

        /// <summary>
        /// Estimates token count from a string using chars/4 heuristic.
        /// Returns 0 for null/empty strings.
        /// </summary>
        public static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (text.Length + 3) / 4; // ceil division
        }

        /// <summary>
        /// Estimates total input tokens for a SendPromptAsync call.
        /// </summary>
        public static int EstimateInputTokens(string userPrompt, string systemPrompt)
        {
            return EstimateTokens(userPrompt) + EstimateTokens(systemPrompt);
        }

        /// <summary>
        /// Estimates total input tokens for a SendChatAsync call.
        /// </summary>
        public static int EstimateInputTokens(List<ChatMessage> messages, string systemPrompt)
        {
            int total = EstimateTokens(systemPrompt);
            if (messages != null)
            {
                foreach (var msg in messages)
                    total += EstimateTokens(msg.Content);
            }
            return total;
        }

        /// <summary>
        /// Estimates the cost of an API call in USD.
        /// Returns 0 for unknown models or Ollama (local).
        /// </summary>
        public static decimal EstimateCost(string model, int inputTokens, int outputTokens)
        {
            if (string.IsNullOrEmpty(model)) return 0m;

            (decimal inputPer1M, decimal outputPer1M) rates;
            if (!Pricing.TryGetValue(model, out rates))
                return 0m;

            return (inputTokens * rates.inputPer1M / 1_000_000m)
                 + (outputTokens * rates.outputPer1M / 1_000_000m);
        }

        /// <summary>
        /// Estimates input-only cost in USD for a given model and token count.
        /// Useful for pre-send cost warnings.
        /// </summary>
        public static decimal EstimateInputCost(string model, int inputTokens)
        {
            if (string.IsNullOrEmpty(model)) return 0m;

            (decimal inputPer1M, decimal outputPer1M) rates;
            if (!Pricing.TryGetValue(model, out rates))
                return 0m;

            return inputTokens * rates.inputPer1M / 1_000_000m;
        }

        /// <summary>
        /// Returns true if pricing information is available for the given model.
        /// </summary>
        public static bool HasPricing(string model)
        {
            return !string.IsNullOrEmpty(model) && Pricing.ContainsKey(model);
        }

        /// <summary>
        /// Returns the per-provider cache discount multipliers for a given model:
        /// (cacheReadMultiplier, cacheWriteMultiplier), both relative to the regular
        /// input rate. Multiply input rate by these for the effective cached rate.
        /// </summary>
        private static (decimal readMultiplier, decimal writeMultiplier) GetCacheMultipliers(string model)
        {
            if (string.IsNullOrEmpty(model)) return (1m, 1m);
            var lc = model.ToLowerInvariant();

            // Anthropic native + OpenRouter→Anthropic
            if (lc.Contains("claude") || lc.StartsWith("anthropic/"))
                return (0.1m, 1.25m);

            // OpenAI auto-cache: 50% off cache reads, no separate cache-write surcharge
            if (lc.StartsWith("gpt-") || lc.StartsWith("openai/") || lc.StartsWith("o4-"))
                return (0.5m, 1m);

            // DeepSeek automatic disk caching: ~90% off cache reads
            if (lc.StartsWith("deepseek") || lc.Contains("deepseek/"))
                return (0.1m, 1m);

            // Gemini 2.5+ implicit caching: 75% off cache reads
            if (lc.StartsWith("gemini-2.5") || lc.StartsWith("gemini-3")
                || lc.StartsWith("google/gemini-2.5") || lc.StartsWith("google/gemini-3"))
                return (0.25m, 1m);

            // No documented caching for this provider/model
            return (1m, 1m);
        }

        /// <summary>
        /// Computes the actual API cost in USD from real token counts returned by
        /// the provider's response usage block, applying per-provider cache
        /// discount rates. Use this when ApiUsage is available; falls back to
        /// EstimateCost when only chars/4 estimates exist.
        /// Returns 0 for unknown models or Ollama (local).
        /// </summary>
        public static decimal ComputeActualCost(string model,
            int regularInputTokens, int cacheReadTokens, int cacheWriteTokens, int outputTokens)
        {
            if (string.IsNullOrEmpty(model)) return 0m;

            (decimal inputPer1M, decimal outputPer1M) rates;
            if (!Pricing.TryGetValue(model, out rates))
                return 0m;

            var (readMul, writeMul) = GetCacheMultipliers(model);

            return (regularInputTokens * rates.inputPer1M / 1_000_000m)
                 + (cacheReadTokens * rates.inputPer1M * readMul / 1_000_000m)
                 + (cacheWriteTokens * rates.inputPer1M * writeMul / 1_000_000m)
                 + (outputTokens * rates.outputPer1M / 1_000_000m);
        }
    }
}

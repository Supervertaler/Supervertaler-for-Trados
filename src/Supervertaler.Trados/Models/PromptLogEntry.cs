using System;
using System.Collections.Generic;
using System.Text;

namespace Supervertaler.Trados.Models
{
    public enum PromptLogFeature
    {
        Chat,
        Translate,
        BatchTranslate,
        Proofread,
        QuickLauncher,
        PromptGeneration,
        ConnectionTest,
        SuperMemory,
        AutoTag
    }

    /// <summary>
    /// Captures a single AI API call for the prompt inspector.
    /// Stored in-memory only (not persisted to disk).
    /// </summary>
    public class PromptLogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public PromptLogFeature Feature { get; set; }
        public string PromptName { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }
        public string DisplayModel { get; set; }
        public string SystemPrompt { get; set; }
        public string UserPrompt { get; set; }
        public List<ChatMessage> Messages { get; set; }
        public string Response { get; set; }
        public int EstimatedInputTokens { get; set; }
        public int EstimatedOutputTokens { get; set; }
        public decimal EstimatedCost { get; set; }

        /// <summary>
        /// When set, these are the real token counts and cost reported by the
        /// provider's API response (with cache pricing applied). When null, the
        /// chars/4 + list-price estimate above is used instead. Populated via
        /// LlmClient.LastUsage on the per-call path and aggregated across batches
        /// by BatchTranslator/BatchProofreader.
        /// </summary>
        public int? ActualRegularInputTokens { get; set; }
        public int? ActualCacheReadTokens { get; set; }
        public int? ActualCacheWriteTokens { get; set; }
        public int? ActualOutputTokens { get; set; }
        public decimal? ActualCost { get; set; }

        /// <summary>True when actual API-reported usage is available for this entry.</summary>
        public bool HasActualUsage => ActualRegularInputTokens.HasValue;

        /// <summary>
        /// True when the model has an entry in the pricing table (so a 0 cost
        /// reflects a genuinely free provider, e.g. Ollama, rather than a
        /// missing pricing entry). Set by callers that build the entry. When
        /// false, the SummaryLine renders "unknown" instead of "free" so users
        /// don't mistake a non-curated OpenRouter model for a free one.
        /// Default true preserves the legacy "free" display for any entry that
        /// pre-dates this flag.
        /// </summary>
        public bool IsCostKnown { get; set; } = true;

        public TimeSpan Duration { get; set; }
        public bool IsError { get; set; }
        public string ErrorMessage { get; set; }

        public string FeatureLabel
        {
            get
            {
                string baseLabel;
                switch (Feature)
                {
                    case PromptLogFeature.Chat: baseLabel = "Chat"; break;
                    case PromptLogFeature.Translate: baseLabel = "Translate"; break;
                    case PromptLogFeature.BatchTranslate: baseLabel = "Batch Translate"; break;
                    case PromptLogFeature.Proofread: baseLabel = "Proofread"; break;
                    case PromptLogFeature.QuickLauncher: baseLabel = "QuickLauncher"; break;
                    case PromptLogFeature.PromptGeneration: baseLabel = "AutoPrompt"; break;
                    case PromptLogFeature.ConnectionTest: baseLabel = "Connection Test"; break;
                    case PromptLogFeature.AutoTag: baseLabel = "AutoTagger"; break;
                    default: baseLabel = "Unknown"; break;
                }

                if (!string.IsNullOrEmpty(PromptName))
                    return $"{baseLabel} \u00b7 {PromptName}";
                return baseLabel;
            }
        }

        public string SummaryLine
        {
            get
            {
                if (IsError)
                    return $"{DisplayModel ?? Model} \u2022 ERROR \u2022 {Duration.TotalSeconds:F1}s";

                if (HasActualUsage)
                {
                    var regularIn = ActualRegularInputTokens ?? 0;
                    var cacheRead = ActualCacheReadTokens ?? 0;
                    var cacheWrite = ActualCacheWriteTokens ?? 0;
                    var totalIn = regularIn + cacheRead + cacheWrite;
                    var output = ActualOutputTokens ?? 0;
                    var cost = ActualCost ?? 0m;

                    string costStr;
                    if (cost >= 0.01m) costStr = $"${cost:F2}";
                    else if (cost > 0) costStr = $"${cost:F4}";
                    else if (IsCostKnown) costStr = "free";
                    else costStr = "unknown";

                    // Show cache hit count when caching contributed, so users can
                    // see at a glance how much of their input was discounted.
                    var cacheNote = cacheRead > 0 ? $" ({cacheRead:N0} cached)" : "";

                    return $"{DisplayModel ?? Model} \u2022 {totalIn:N0} in{cacheNote} / {output:N0} out \u2022 {costStr} \u2022 {Duration.TotalSeconds:F1}s";
                }

                string estCostStr;
                if (EstimatedCost >= 0.01m) estCostStr = $"~${EstimatedCost:F2}";
                else if (EstimatedCost > 0) estCostStr = $"~${EstimatedCost:F4}";
                else if (IsCostKnown) estCostStr = "free";
                else estCostStr = "unknown";

                return $"{DisplayModel ?? Model} \u2022 {EstimatedInputTokens:N0} in / {EstimatedOutputTokens:N0} out \u2022 {estCostStr} \u2022 {Duration.TotalSeconds:F1}s";
            }
        }

        /// <summary>
        /// Returns the full prompt details as copyable text.
        /// </summary>
        public string ToFullText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Prompt Log: {FeatureLabel} ===");
            sb.AppendLine($"Timestamp: {Timestamp:yyyy-MM-dd HH:mm:ss}");
            if (!string.IsNullOrEmpty(PromptName))
                sb.AppendLine($"Prompt: {PromptName}");
            sb.AppendLine($"Provider: {Provider}");
            sb.AppendLine($"Model: {DisplayModel ?? Model}");
            sb.AppendLine($"Duration: {Duration.TotalSeconds:F1}s");
            if (HasActualUsage)
            {
                var regularIn = ActualRegularInputTokens ?? 0;
                var cacheRead = ActualCacheReadTokens ?? 0;
                var cacheWrite = ActualCacheWriteTokens ?? 0;
                var totalIn = regularIn + cacheRead + cacheWrite;
                var output = ActualOutputTokens ?? 0;
                sb.AppendLine($"Tokens (actual): {totalIn:N0} in ({regularIn:N0} regular, {cacheRead:N0} cache hit, {cacheWrite:N0} cache write) / {output:N0} out");
                var cost = ActualCost ?? 0m;
                string costLabel;
                if (cost > 0) costLabel = $"${cost:F4}";
                else if (IsCostKnown) costLabel = "free";
                else costLabel = "unknown (model not in pricing table)";
                sb.AppendLine($"Cost (actual): {costLabel}");
            }
            else
            {
                sb.AppendLine($"Estimated tokens: {EstimatedInputTokens:N0} in / {EstimatedOutputTokens:N0} out");
                string estLabel;
                if (EstimatedCost > 0) estLabel = $"${EstimatedCost:F4}";
                else if (IsCostKnown) estLabel = "free";
                else estLabel = "unknown (model not in pricing table)";
                sb.AppendLine($"Estimated cost: {estLabel}");
            }
            sb.AppendLine();

            if (!string.IsNullOrEmpty(SystemPrompt))
            {
                sb.AppendLine("--- System Prompt ---");
                sb.AppendLine(SystemPrompt);
                sb.AppendLine();
            }

            if (Messages != null && Messages.Count > 0)
            {
                sb.AppendLine("--- Messages ---");
                foreach (var msg in Messages)
                {
                    sb.AppendLine($"[{msg.Role}]: {msg.Content}");
                }
                sb.AppendLine();
            }
            else if (!string.IsNullOrEmpty(UserPrompt))
            {
                sb.AppendLine("--- User Prompt ---");
                sb.AppendLine(UserPrompt);
                sb.AppendLine();
            }

            if (IsError)
            {
                sb.AppendLine("--- Error ---");
                sb.AppendLine(ErrorMessage);
            }
            else if (!string.IsNullOrEmpty(Response))
            {
                sb.AppendLine("--- Response ---");
                sb.AppendLine(Response);
            }

            return sb.ToString();
        }
    }
}

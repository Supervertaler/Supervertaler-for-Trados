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
        SuperMemory
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

                var costStr = EstimatedCost >= 0.01m
                    ? $"~${EstimatedCost:F2}"
                    : EstimatedCost > 0
                        ? $"~${EstimatedCost:F4}"
                        : "free";

                return $"{DisplayModel ?? Model} \u2022 {EstimatedInputTokens:N0} in / {EstimatedOutputTokens:N0} out \u2022 {costStr} \u2022 {Duration.TotalSeconds:F1}s";
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
            sb.AppendLine($"Estimated tokens: {EstimatedInputTokens:N0} in / {EstimatedOutputTokens:N0} out");
            sb.AppendLine($"Estimated cost: {(EstimatedCost > 0 ? $"${EstimatedCost:F4}" : "free")}");
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

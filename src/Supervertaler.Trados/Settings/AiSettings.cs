using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Supervertaler.Trados.Settings
{
    /// <summary>
    /// AI provider settings, stored as part of the plugin's settings.json.
    /// API key fallback chain: plugin-local → Supervertaler desktop app settings.
    /// </summary>
    [DataContract]
    public class AiSettings
    {
        [DataMember(Name = "selectedProvider")]
        public string SelectedProvider { get; set; } = "openai";

        [DataMember(Name = "openaiModel")]
        public string OpenAiModel { get; set; } = "gpt-4.1";

        [DataMember(Name = "claudeModel")]
        public string ClaudeModel { get; set; } = "claude-sonnet-4-6";

        [DataMember(Name = "geminiModel")]
        public string GeminiModel { get; set; } = "gemini-2.5-flash";

        [DataMember(Name = "grokModel")]
        public string GrokModel { get; set; } = "grok-4.20-0309-non-reasoning";

        [DataMember(Name = "ollamaModel")]
        public string OllamaModel { get; set; } = "translategemma:12b";

        [DataMember(Name = "ollamaEndpoint")]
        public string OllamaEndpoint { get; set; } = "http://localhost:11434";

        [DataMember(Name = "apiKeys")]
        public AiApiKeys ApiKeys { get; set; } = new AiApiKeys();

        [DataMember(Name = "customOpenAiProfiles")]
        public List<CustomOpenAiProfile> CustomOpenAiProfiles { get; set; }
            = new List<CustomOpenAiProfile>();

        [DataMember(Name = "selectedCustomProfileName")]
        public string SelectedCustomProfileName { get; set; } = "";

        [DataMember(Name = "batchSize")]
        public int BatchSize { get; set; } = 20;

        /// <summary>
        /// Relative path of the selected custom prompt from the prompt library.
        /// Empty string means no custom prompt (use default system prompt only).
        /// </summary>
        [DataMember(Name = "selectedPromptPath")]
        public string SelectedPromptPath { get; set; } = "";

        /// <summary>
        /// User's custom system prompt override. When non-null and non-empty,
        /// replaces the entire base system prompt (tag preservation, number formatting, etc.).
        /// Null means use the default base system prompt.
        /// </summary>
        [DataMember(Name = "customSystemPrompt")]
        public string CustomSystemPrompt { get; set; }

        /// <summary>
        /// IDs of termbases disabled for AI context.
        /// Empty means all termbases contribute to AI prompts.
        /// Separate from TermLensSettings.DisabledTermbaseIds (which controls TermLens display).
        /// </summary>
        [DataMember(Name = "disabledAiTermbaseIds")]
        public List<long> DisabledAiTermbaseIds { get; set; } = new List<long>();

        /// <summary>
        /// Whether to include TM (Translation Memory) fuzzy matches in AI context.
        /// Default: true — TM matches provide useful reference for the AI.
        /// </summary>
        [DataMember(Name = "includeTmMatches")]
        public bool IncludeTmMatches { get; set; } = true;

        /// <summary>
        /// Whether to include the full document content (all source segments) in the
        /// AI chat prompt. Enables the AI to assess the document type and provide
        /// context-appropriate assistance.
        /// </summary>
        [DataMember(Name = "includeDocumentContext")]
        public bool IncludeDocumentContext { get; set; } = true;

        /// <summary>
        /// Maximum number of source segments to include in the AI chat prompt.
        /// Documents larger than this are truncated (first 80% + last 20%).
        /// </summary>
        [DataMember(Name = "documentContextMaxSegments")]
        public int DocumentContextMaxSegments { get; set; } = 500;

        /// <summary>
        /// Number of segments before and after the active segment to include as context
        /// in QuickLauncher prompts ({{SURROUNDING_SEGMENTS}} variable) and in the
        /// AI Assistant chat system prompt.
        /// Default: 5 (five segments on each side).
        /// </summary>
        /// <remarks>
        /// Uses a backing field so that <see cref="OnDeserializing"/> can pre-seed the
        /// default before DataContractSerializer fills in the value. Without this,
        /// DataContractSerializer bypasses constructors and property initializers, leaving
        /// the field at 0 when the key is absent from an older settings.json.
        /// </remarks>
        private int _quickLauncherSurroundingSegments = 5;

        [DataMember(Name = "quickLauncherSurroundingSegments")]
        public int QuickLauncherSurroundingSegments
        {
            get => _quickLauncherSurroundingSegments;
            set => _quickLauncherSurroundingSegments = value;
        }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            // Pre-seed defaults that DataContractSerializer would otherwise leave at 0
            // when the key is absent from an older settings.json.
            _quickLauncherSurroundingSegments = 5;
        }

        /// <summary>
        /// QuickLauncher shortcut slot assignments.
        /// Maps slot number (1–10) to prompt file path (relative to prompts folder).
        /// Null/empty means auto-assign by menu order (legacy behaviour).
        /// </summary>
        [DataMember(Name = "quickLauncherSlots")]
        public Dictionary<string, string> QuickLauncherSlots { get; set; }
            = new Dictionary<string, string>();

        /// <summary>
        /// Whether to include term definitions, domains, and notes alongside
        /// matched terminology in the AI chat prompt.
        /// </summary>
        [DataMember(Name = "includeTermMetadata")]
        public bool IncludeTermMetadata { get; set; } = true;

        /// <summary>
        /// When enabled, every AI API call is logged to the Reports tab with
        /// the full prompt, response, estimated token counts, and cost.
        /// </summary>
        [DataMember(Name = "logPromptsToReports")]
        public bool LogPromptsToReports { get; set; }

        /// <summary>
        /// Sets the model for the given provider and makes it the active provider.
        /// </summary>
        public void SetProviderAndModel(string providerKey, string modelId)
        {
            SelectedProvider = providerKey;
            switch (providerKey)
            {
                case "openai": OpenAiModel = modelId; break;
                case "claude": ClaudeModel = modelId; break;
                case "gemini": GeminiModel = modelId; break;
                case "grok": GrokModel = modelId; break;
                case "ollama": OllamaModel = modelId; break;
                case "custom_openai": SelectedCustomProfileName = modelId; break;
            }
        }

        /// <summary>
        /// Returns the selected model ID for the currently active provider.
        /// </summary>
        public string GetSelectedModel()
        {
            switch (SelectedProvider)
            {
                case "openai": return OpenAiModel;
                case "claude": return ClaudeModel;
                case "gemini": return GeminiModel;
                case "grok": return GrokModel;
                case "ollama": return OllamaModel;
                case "custom_openai":
                    var profile = GetActiveCustomProfile();
                    return profile?.Model ?? "custom-model";
                default: return OpenAiModel;
            }
        }

        /// <summary>
        /// Returns the active custom OpenAI profile, or null if none selected.
        /// </summary>
        public CustomOpenAiProfile GetActiveCustomProfile()
        {
            if (CustomOpenAiProfiles == null || CustomOpenAiProfiles.Count == 0)
                return null;

            foreach (var p in CustomOpenAiProfiles)
            {
                if (p.Name == SelectedCustomProfileName)
                    return p;
            }

            return CustomOpenAiProfiles[0];
        }
    }

    [DataContract]
    public class AiApiKeys
    {
        [DataMember(Name = "openai")]
        public string OpenAi { get; set; } = "";

        [DataMember(Name = "claude")]
        public string Claude { get; set; } = "";

        [DataMember(Name = "gemini")]
        public string Gemini { get; set; } = "";

        [DataMember(Name = "grok")]
        public string Grok { get; set; } = "";

        [DataMember(Name = "custom_openai")]
        public string CustomOpenAi { get; set; } = "";
    }

    [DataContract]
    public class CustomOpenAiProfile
    {
        [DataMember(Name = "name")]
        public string Name { get; set; } = "";

        [DataMember(Name = "endpoint")]
        public string Endpoint { get; set; } = "";

        [DataMember(Name = "model")]
        public string Model { get; set; } = "";

        [DataMember(Name = "apiKey")]
        public string ApiKey { get; set; } = "";
    }
}

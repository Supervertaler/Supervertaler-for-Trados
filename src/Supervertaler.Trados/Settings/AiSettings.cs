using System.Collections.Generic;
using System.Runtime.Serialization;
using Supervertaler.Trados.Core;

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
        public string OpenAiModel { get; set; } = LlmModels.OpenAiModels[1].Id;  // GPT-5.4 Mini (recommended)

        [DataMember(Name = "claudeModel")]
        public string ClaudeModel { get; set; } = LlmModels.ClaudeModels[0].Id;

        [DataMember(Name = "geminiModel")]
        public string GeminiModel { get; set; } = LlmModels.GeminiModels[0].Id;

        [DataMember(Name = "grokModel")]
        public string GrokModel { get; set; } = LlmModels.GrokModels[0].Id;

        [DataMember(Name = "mistralModel")]
        public string MistralModel { get; set; } = LlmModels.MistralModels[0].Id;

        [DataMember(Name = "openRouterModel")]
        public string OpenRouterModel { get; set; } = LlmModels.OpenRouterModels[0].Id;

        [DataMember(Name = "ollamaModel")]
        public string OllamaModel { get; set; } = LlmModels.OllamaModels[0].Id;

        [DataMember(Name = "ollamaEndpoint")]
        public string OllamaEndpoint { get; set; } = "http://localhost:11434";

        /// <summary>
        /// User-configurable timeout for Ollama requests, in minutes.
        /// 0 means automatic (based on model size: 3–10 min).
        /// </summary>
        [DataMember(Name = "ollamaTimeoutMinutes")]
        public int OllamaTimeoutMinutes { get; set; }

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
        public int DocumentContextMaxSegments { get; set; } = 20;

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
            // or null when the key is absent from an older settings.json.
            _quickLauncherSurroundingSegments = 5;
            ActiveMemoryBankName = UserDataPath.DefaultMemoryBankName;
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
        /// Folder relative paths whose prompts should appear as a flat section
        /// (with a bold header and separators) instead of an expandable submenu
        /// in the QuickLauncher context menu.
        /// </summary>
        [DataMember(Name = "quickLauncherFlatFolders")]
        public List<string> QuickLauncherFlatFolders { get; set; }
            = new List<string>();

        /// <summary>
        /// Whether to include term definitions, domains, and notes alongside
        /// matched terminology in the AI chat prompt.
        /// </summary>
        [DataMember(Name = "includeTermMetadata")]
        public bool IncludeTermMetadata { get; set; } = true;

        /// <summary>
        /// When enabled, SuperMemory knowledge base articles (client profiles,
        /// domain knowledge, style guides, terminology reasoning) are automatically
        /// loaded and included in the AI context for translations and chat.
        /// </summary>
        [DataMember(Name = "includeSuperMemoryContext")]
        public bool IncludeSuperMemoryContext { get; set; } = true;

        /// <summary>
        /// When enabled, SuperMemory knowledge base articles are included in the
        /// AutoPrompt meta-prompt so that generated translation prompts reflect
        /// established client conventions, terminology reasoning, and style guides.
        /// Only effective when <see cref="IncludeSuperMemoryContext"/> is also true.
        /// </summary>
        [DataMember(Name = "includeSuperMemoryInAutoPrompt")]
        public bool IncludeSuperMemoryInAutoPrompt { get; set; } = true;

        /// <summary>
        /// Name of the memory bank that is currently active for AI context, Quick
        /// Add, Process Inbox, Health Check and Distill. Matches a subfolder name
        /// under <c>&lt;Root&gt;/memory-banks/</c>. When empty or when the named bank
        /// no longer exists, the plugin falls back to
        /// <see cref="UserDataPath.DefaultMemoryBankName"/>. Mirrors the Python
        /// Assistant's <c>last_active_bank</c> field but is kept independent so a
        /// translator can have different banks active in Trados and Workbench.
        /// </summary>
        [DataMember(Name = "activeMemoryBankName")]
        public string ActiveMemoryBankName { get; set; } = UserDataPath.DefaultMemoryBankName;

        /// <summary>
        /// Incognito mode: when enabled, the AI anonymises all personal and project
        /// data in its responses — project names, file paths, TM names, user names,
        /// etc. are replaced with plausible placeholders. Useful for screen sharing,
        /// recording demos, and posting screenshots without exposing client data.
        /// </summary>
        [DataMember(Name = "demoMode")]
        public bool DemoMode { get; set; }

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
                case "mistral": MistralModel = modelId; break;
                case "openrouter": OpenRouterModel = modelId; break;
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
                case "mistral": return MistralModel;
                case "openrouter": return OpenRouterModel;
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

        [DataMember(Name = "mistral")]
        public string Mistral { get; set; } = "";

        [DataMember(Name = "openrouter")]
        public string OpenRouter { get; set; } = "";

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

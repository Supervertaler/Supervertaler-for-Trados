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

        [DataMember(Name = "deepSeekModel")]
        public string DeepSeekModel { get; set; } = LlmModels.DeepSeekModels[0].Id;

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
        /// When true (default), the plugin starts a localhost-only HTTP bridge
        /// (<see cref="Core.SupervertalerBridge"/>) that lets Supervertaler Workbench's
        /// floating Sidekick Chat fetch the active project context and insert
        /// translations back into the active Trados segment. Hidden setting –
        /// no UI checkbox; advanced users can flip it off by editing settings.json
        /// directly. The bridge only listens on 127.0.0.1, requires a per-session
        /// auth token, and only starts when the user has Assistant access (paid
        /// or trial).
        ///
        /// NOTE: the default value (true) is set in <see cref="SetDefaultsBeforeDeserialization"/>
        /// rather than as a property initialiser, because <c>DataContractJsonSerializer</c>
        /// skips constructors during deserialization – without the OnDeserializing
        /// callback, any settings.json written before this property existed would
        /// see the field as <c>false</c> (the bool type default) instead of true.
        /// </summary>
        [DataMember(Name = "sidekickBridgeEnabled")]
        public bool SidekickBridgeEnabled { get; set; } = true;

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
        ///
        /// NB: The privacy-first default is opt-in (nothing included). That is achieved
        /// at first load via the AiTermbaseIdsInitialized flag below: when the flag is
        /// false and this list is empty, the editor migrates by populating this list
        /// with ALL currently-known termbase IDs (effectively disabling everything),
        /// then sets the flag. Existing users with explicit choices (any non-empty list)
        /// or who have already been migrated (flag=true) keep their state untouched.
        /// </summary>
        [DataMember(Name = "disabledAiTermbaseIds")]
        public List<long> DisabledAiTermbaseIds { get; set; } = new List<long>();

        /// <summary>
        /// One-shot migration marker for the opt-in AI termbase default.
        /// false (default for files that pre-date this field) = needs migration.
        /// true = either already migrated, or the user has explicitly chosen their
        /// preferences via the AI Settings dialog. Mirror of ProjectSettings'
        /// per-project flag of the same name, applied at the global level.
        /// </summary>
        [DataMember(Name = "aiTermbaseIdsInitialized")]
        public bool AiTermbaseIdsInitialized { get; set; }

        /// <summary>
        /// IDs of MultiTerm (.sdltb) termbases the user has EXPLICITLY enabled for AI.
        /// MultiTerm termbases are opt-in (default off): unlike Supervertaler .db termbases
        /// — which the one-shot migration adds to <see cref="DisabledAiTermbaseIds"/> so they
        /// start unticked — MultiTerm termbases are discovered per-project after that
        /// migration, so they can't rely on the disabled-list mechanism. They are therefore
        /// tracked here by their (negative) SyntheticId and are sent to the AI ONLY when
        /// present in this list (i.e. the user ticks the AI column for them).
        /// </summary>
        [DataMember(Name = "enabledAiMultiTermIds")]
        public List<long> EnabledAiMultiTermIds { get; set; } = new List<long>();

        /// <summary>
        /// Single source of truth for "may this termbase's terms be sent to the AI?".
        /// MultiTerm termbases (negative SyntheticId) are opt-in: included only when the
        /// user has explicitly enabled them. Supervertaler .db termbases (non-negative Id)
        /// are included unless the user has disabled them. Both default to NOT sending.
        /// </summary>
        public bool IsTermbaseAiEnabled(long termbaseId)
        {
            if (termbaseId < 0)
                return EnabledAiMultiTermIds != null && EnabledAiMultiTermIds.Contains(termbaseId);
            return DisabledAiTermbaseIds == null || !DisabledAiTermbaseIds.Contains(termbaseId);
        }

        /// <summary>
        /// Whether to include TM (Translation Memory) fuzzy matches in AI context.
        /// Default: true – TM matches provide useful reference for the AI.
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

        /// <summary>
        /// Pre-seeds defaults for properties added after the first version of
        /// AiSettings. <c>DataContractJsonSerializer</c> skips constructors and
        /// property initialisers during deserialization, so properties not
        /// present in older settings.json files would otherwise come back with
        /// the bool/int/string TYPE default rather than the initialiser value.
        /// This callback runs BEFORE deserialization, so any value present in
        /// the JSON still overrides what we set here.
        ///
        /// IMPORTANT: a [DataContract] type may have only ONE [OnDeserializing]
        /// method. Adding a second one throws InvalidDataContractException at
        /// the first deserialize attempt, which TermLensSettings.Load() then
        /// silently catches – returning fresh defaults and wiping every saved
        /// setting from the user's perspective. All new defaults must be added
        /// to this single method, never to a second callback.
        /// </summary>
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            _quickLauncherSurroundingSegments = 5;
            ActiveMemoryBankName = UserDataPath.DefaultMemoryBankName;
            SidekickBridgeEnabled = true;
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
        /// Where QuickLauncher prompts run. <c>"TradosAssistant"</c> (default,
        /// preserves existing behaviour) routes the prompt + response through
        /// the in-Trados AI Assistant chat. <c>"WorkbenchSidekick"</c> instead
        /// posts the prompt to Supervertaler Workbench's Chat (AI tab → Chat
        /// sub-tab) via the localhost bridge Workbench exposes (see
        /// WorkbenchBridgeClient and the Workbench-side
        /// modules/supervertaler_bridge_server.py).
        ///
        /// The setting value <c>"WorkbenchSidekick"</c> is a historical
        /// identifier kept stable for back-compat: the Workbench-side
        /// floating Sidekick window was retired in Workbench v1.10.4, but
        /// the wire-protocol name (and this setting value) intentionally
        /// did not change so existing user settings keep resolving. The
        /// user-facing label in the AI Settings UI reads "Workbench Chat"
        /// to reflect the current reality.
        ///
        /// When set to WorkbenchSidekick but Workbench isn't running /
        /// reachable, the action falls back to the in-Trados Assistant
        /// with a status line citing the reason – the user is never
        /// blocked from running their prompt by an unavailable Workbench.
        /// </summary>
        [DataMember(Name = "quickLauncherTarget")]
        public string QuickLauncherTarget { get; set; } = "TradosAssistant";

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
        ///
        /// Defaults to <c>false</c>. SuperMemory is a power-user feature that
        /// most translators should opt into deliberately: the simpler workflow
        /// (TermLens glossaries + AI context awareness) covers the majority of
        /// needs. Users who want the full self-organising knowledge base can
        /// enable this toggle in AI Settings once they have a populated bank.
        /// </summary>
        [DataMember(Name = "includeSuperMemoryContext")]
        public bool IncludeSuperMemoryContext { get; set; } = false;

        /// <summary>
        /// When enabled, SuperMemory knowledge base articles are included in the
        /// AutoPrompt meta-prompt so that generated translation prompts reflect
        /// established client conventions, terminology reasoning, and style guides.
        /// Only effective when <see cref="IncludeSuperMemoryContext"/> is also true.
        ///
        /// Defaults to <c>false</c> (same reasoning as
        /// <see cref="IncludeSuperMemoryContext"/> – opt-in only).
        /// </summary>
        [DataMember(Name = "includeSuperMemoryInAutoPrompt")]
        public bool IncludeSuperMemoryInAutoPrompt { get; set; } = false;

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
        /// data in its responses – project names, file paths, TM names, user names,
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
        /// When enabled (the default), every AI call's token usage and cost is
        /// appended to a persistent JSONL ledger under {Trados}/usage/. This stores
        /// metadata only — model, tokens, cost, project/file — and NEVER the prompt
        /// or response text. Independent of <see cref="LogPromptsToReports"/>.
        /// Stored as nullable so an absent value in older settings files is treated
        /// as enabled (see <see cref="IsUsageLogEnabled"/>).
        /// </summary>
        [DataMember(Name = "persistUsageLog")]
        public bool? PersistUsageLog { get; set; }

        /// <summary>Resolves <see cref="PersistUsageLog"/> with on-by-default semantics.</summary>
        public bool IsUsageLogEnabled => PersistUsageLog ?? true;

        /// <summary>
        /// Optional soft monthly spend budget in USD (0 = no budget). When set,
        /// starting a batch translation after this month's logged spend has reached
        /// the budget shows a warn-and-continue prompt. Advisory only — never blocks.
        /// </summary>
        [DataMember(Name = "monthlyBudgetUsd")]
        public double MonthlyBudgetUsd { get; set; }

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
                case "deepseek": DeepSeekModel = modelId; break;
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
                case "deepseek": return DeepSeekModel;
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

        [DataMember(Name = "deepseek")]
        public string DeepSeek { get; set; } = "";

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

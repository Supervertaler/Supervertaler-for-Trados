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
        public string OpenAiModel { get; set; } = "gpt-4o";

        [DataMember(Name = "claudeModel")]
        public string ClaudeModel { get; set; } = "claude-sonnet-4-6";

        [DataMember(Name = "geminiModel")]
        public string GeminiModel { get; set; } = "gemini-2.5-flash";

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
        /// Returns the selected model ID for the currently active provider.
        /// </summary>
        public string GetSelectedModel()
        {
            switch (SelectedProvider)
            {
                case "openai": return OpenAiModel;
                case "claude": return ClaudeModel;
                case "gemini": return GeminiModel;
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

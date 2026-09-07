using System.Runtime.Serialization;

namespace Supervertaler.Core
{
    /// <summary>
    /// Per-provider API keys, as stored in a plugin's own settings.
    ///
    /// Lifted out of the Trados plugin's AiSettings so that the key-resolution
    /// chain in <see cref="LlmClient"/> — plugin-local key, then the shared
    /// Supervertaler desktop settings — can be shared rather than reimplemented
    /// per plugin.
    ///
    /// The DataMember names are the on-disk contract of settings files already in
    /// the field. Do not rename one without a migration.
    /// </summary>
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
}

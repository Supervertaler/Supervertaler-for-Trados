using System.Runtime.Serialization;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Best-effort snapshot of the active project/file context, read at log time
    /// for token-usage attribution. Any field may be null.
    /// </summary>
    public class UsageContextSnapshot
    {
        public string Project;
        public string ProjectKey;
        public string File;
        public string Client;
        public string SrcLang;
        public string TgtLang;
    }

    /// <summary>
    /// One persisted token-usage record. Metadata only — it deliberately never
    /// contains the prompt or response text, so the ledger stays small and safe
    /// to share. Serialised as a single JSON line in the monthly usage file.
    /// </summary>
    [DataContract]
    public class UsageRecord
    {
        [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
        [DataMember(Name = "ts", Order = 1)] public string Ts { get; set; }           // UTC ISO-8601
        [DataMember(Name = "product", Order = 2)] public string Product { get; set; }
        [DataMember(Name = "app_version", Order = 3)] public string AppVersion { get; set; }
        [DataMember(Name = "task", Order = 4)] public string Task { get; set; }        // PromptLogFeature
        [DataMember(Name = "provider", Order = 5)] public string Provider { get; set; }
        [DataMember(Name = "model", Order = 6)] public string Model { get; set; }
        [DataMember(Name = "project", Order = 7)] public string Project { get; set; }
        [DataMember(Name = "project_key", Order = 8)] public string ProjectKey { get; set; }
        [DataMember(Name = "file", Order = 9)] public string File { get; set; }
        [DataMember(Name = "client", Order = 10)] public string Client { get; set; }
        [DataMember(Name = "src_lang", Order = 11)] public string SrcLang { get; set; }
        [DataMember(Name = "tgt_lang", Order = 12)] public string TgtLang { get; set; }
        [DataMember(Name = "in_regular", Order = 13)] public int InputRegular { get; set; }
        [DataMember(Name = "in_cache_read", Order = 14)] public int InputCacheRead { get; set; }
        [DataMember(Name = "in_cache_write", Order = 15)] public int InputCacheWrite { get; set; }
        [DataMember(Name = "out", Order = 16)] public int Output { get; set; }
        [DataMember(Name = "source", Order = 17)] public string Source { get; set; }   // "actual" | "estimated"
        [DataMember(Name = "cost_usd", Order = 18)] public decimal CostUsd { get; set; }
        [DataMember(Name = "cost_known", Order = 19)] public bool CostKnown { get; set; }
        [DataMember(Name = "duration_s", Order = 20)] public double DurationS { get; set; }
        [DataMember(Name = "ok", Order = 21)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 22)] public string Error { get; set; }
    }
}

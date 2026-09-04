using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Supervertaler.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Writes each completed AI call - the prompt actually sent and the response
    /// actually received - to a daily JSONL file, when "Log prompts and responses"
    /// is on.
    ///
    /// That setting used to feed only the in-memory Reports tab, so the outgoing
    /// payload was never on disk. Establishing which tag notation the model had
    /// really been sent (#97) took an afternoon of cross-referencing MCP calls
    /// against batch backups; with the payload on disk it is one grep. This makes
    /// the whole class of "what did we really send?" question self-answering.
    ///
    /// Same shape as <see cref="UsageLogger"/>: subscribed once at startup to
    /// <see cref="LlmClient.PromptCompleted"/>, so it records every call whether
    /// or not the Assistant pane was ever opened, and never lets a logging
    /// failure reach the caller.
    ///
    /// What is written: the full system and user prompt, every chat message's
    /// text, and the response. What is NOT: image bytes (an attachment is
    /// recorded as its type and size), and connection tests (not real traffic).
    ///
    /// Scale: one batch call can carry a couple of hundred KB of prompt, and a
    /// heavy day of batch work can reach tens of MB. Daily files, and files older
    /// than <see cref="RetainDays"/> are removed once per Studio session, keep the
    /// folder bounded - it lives in the shared data folder, which for many users
    /// is synced to Drive or OneDrive.
    /// </summary>
    public static class PromptFileLogger
    {
        public static string LogDir => Path.Combine(UserDataPath.TradosDir, "logs", "prompts");

        private const int RetainDays = 7;

        private static readonly object _lock = new object();
        private static bool _subscribed;
        private static bool _pruned;

        /// <summary>Subscribe once, at startup (AppInitializer). Idempotent.</summary>
        public static void EnsureSubscribed()
        {
            if (_subscribed) return;
            lock (_lock)
            {
                if (_subscribed) return;
                LlmClient.PromptCompleted += (s, entry) =>
                {
                    try { Record(entry, SettingsService.Current); }
                    catch { /* never let logging disrupt anything */ }
                };
                _subscribed = true;
            }
        }

        public static void Record(PromptLogEntry entry, TermLensSettings settings)
        {
            try
            {
                if (entry == null) return;
                if (entry.Feature == PromptLogFeature.ConnectionTest) return;
                if (settings?.AiSettings == null || !settings.AiSettings.LogPromptsToReports) return;

                var rec = Build(entry);
                var line = Serialize(rec);
                if (line == null) return;

                var path = Path.Combine(LogDir, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl");
                Directory.CreateDirectory(LogDir);
                lock (_lock)
                {
                    PruneOnceNoLock();
                    File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
                }
            }
            catch { /* never let logging disrupt translation */ }
        }

        private static PromptFileRecord Build(PromptLogEntry e)
        {
            var rec = new PromptFileRecord
            {
                Ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Feature = e.Feature.ToString(),
                PromptName = e.PromptName,
                Provider = e.Provider,
                Model = e.Model,
                SystemPrompt = e.SystemPrompt,
                UserPrompt = e.UserPrompt,
                Response = e.Response,
                DurationS = e.Duration.TotalSeconds,
                Ok = !e.IsError,
                Error = e.IsError ? e.ErrorMessage : null,
                UsageSource = e.HasActualUsage ? "actual" : "estimated",
                InputTokens = e.HasActualUsage
                    ? (e.ActualRegularInputTokens ?? 0) + (e.ActualCacheReadTokens ?? 0) + (e.ActualCacheWriteTokens ?? 0)
                    : e.EstimatedInputTokens,
                OutputTokens = e.HasActualUsage ? (e.ActualOutputTokens ?? 0) : e.EstimatedOutputTokens,
                CostUsd = e.HasActualUsage ? (e.ActualCost ?? 0m) : e.EstimatedCost,
            };

            if (e.Messages != null && e.Messages.Count > 0)
            {
                rec.Messages = new List<PromptFileMessage>(e.Messages.Count);
                foreach (var m in e.Messages)
                {
                    if (m == null) continue;
                    var pm = new PromptFileMessage { Role = m.Role.ToString(), Content = m.Content };
                    if (m.HasImages)
                    {
                        // Type and size only - never the bytes.
                        pm.Images = new List<string>(m.Images.Count);
                        foreach (var img in m.Images)
                            pm.Images.Add($"{img?.MimeType ?? "image"} {img?.Width ?? 0}x{img?.Height ?? 0} {(img?.Data?.Length ?? 0) / 1024} KB");
                    }
                    if (m.HasDocuments)
                    {
                        // The extracted text IS what the model was sent, so it stays.
                        pm.Documents = new List<PromptFileDocument>(m.Documents.Count);
                        foreach (var d in m.Documents)
                            pm.Documents.Add(new PromptFileDocument
                            {
                                FileName = d?.FileName,
                                FileSize = d?.FileSize ?? 0,
                                ExtractedText = d?.ExtractedText
                            });
                    }
                    rec.Messages.Add(pm);
                }
            }
            return rec;
        }

        private static string Serialize(PromptFileRecord rec)
        {
            try
            {
                using (var ms = new MemoryStream())
                {
                    // Same serializer as the usage ledger: one line per record,
                    // control characters escaped, so a JSONL file stays a JSONL file.
                    new DataContractJsonSerializer(typeof(PromptFileRecord)).WriteObject(ms, rec);
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// Deletes daily files older than <see cref="RetainDays"/>. Runs once per
        /// Studio session, on the first write, so the cost is one directory listing
        /// rather than one per call. Caller holds <see cref="_lock"/>.
        /// </summary>
        private static void PruneOnceNoLock()
        {
            if (_pruned) return;
            _pruned = true;
            try
            {
                var cutoff = DateTime.Now.AddDays(-RetainDays);
                foreach (var f in Directory.GetFiles(LogDir, "*.jsonl"))
                {
                    var stem = Path.GetFileNameWithoutExtension(f);
                    if (DateTime.TryParseExact(stem, "yyyy-MM-dd", null,
                            System.Globalization.DateTimeStyles.None, out var day)
                        && day < cutoff)
                    {
                        File.Delete(f);
                    }
                }
            }
            catch { /* a failed prune must not cost the caller their record */ }
        }
    }

    [DataContract]
    public class PromptFileRecord
    {
        [DataMember(Name = "ts")] public string Ts { get; set; }
        [DataMember(Name = "feature")] public string Feature { get; set; }
        [DataMember(Name = "promptName")] public string PromptName { get; set; }
        [DataMember(Name = "provider")] public string Provider { get; set; }
        [DataMember(Name = "model")] public string Model { get; set; }
        [DataMember(Name = "systemPrompt")] public string SystemPrompt { get; set; }
        [DataMember(Name = "userPrompt")] public string UserPrompt { get; set; }
        [DataMember(Name = "messages")] public List<PromptFileMessage> Messages { get; set; }
        [DataMember(Name = "response")] public string Response { get; set; }
        [DataMember(Name = "usageSource")] public string UsageSource { get; set; }
        [DataMember(Name = "inputTokens")] public int InputTokens { get; set; }
        [DataMember(Name = "outputTokens")] public int OutputTokens { get; set; }
        [DataMember(Name = "costUsd")] public decimal CostUsd { get; set; }
        [DataMember(Name = "durationS")] public double DurationS { get; set; }
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "error")] public string Error { get; set; }
    }

    [DataContract]
    public class PromptFileMessage
    {
        [DataMember(Name = "role")] public string Role { get; set; }
        [DataMember(Name = "content")] public string Content { get; set; }
        [DataMember(Name = "images")] public List<string> Images { get; set; }
        [DataMember(Name = "documents")] public List<PromptFileDocument> Documents { get; set; }
    }

    [DataContract]
    public class PromptFileDocument
    {
        [DataMember(Name = "fileName")] public string FileName { get; set; }
        [DataMember(Name = "fileSize")] public long FileSize { get; set; }
        [DataMember(Name = "extractedText")] public string ExtractedText { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// A segment to be batch-translated. Carries its index (position in the document)
    /// and a reference to the original segment pair for writing back.
    /// </summary>
    public class BatchSegment
    {
        public int Index { get; set; }
        public string SourceText { get; set; }
        public string ExistingTarget { get; set; }

        /// <summary>
        /// Opaque reference to the Trados ISegmentPair. Stored as object
        /// so the engine doesn't depend on Trados SDK types.
        /// </summary>
        public object SegmentPairRef { get; set; }

        /// <summary>
        /// Whether the source segment contains inline tags (formatting, field codes, etc.).
        /// When true, SourceText contains numbered tag placeholders and TagMap is populated.
        /// </summary>
        public bool HasTags { get; set; }

        /// <summary>
        /// Maps tag placeholder numbers to original Trados markup data.
        /// Only populated when HasTags is true.
        /// </summary>
        public Dictionary<int, TagInfo> TagMap { get; set; }
    }

    public enum BatchScope
    {
        EmptyOnly,
        All,
        Filtered,
        FilteredEmptyOnly
    }

    // ─── Event Args ──────────────────────────────────────────

    public class BatchProgressEventArgs : EventArgs
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public string Message { get; set; }
        public bool IsError { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    public class BatchCompletedEventArgs : EventArgs
    {
        public int Translated { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public TimeSpan TotalTime { get; set; }
        public bool WasCancelled { get; set; }
    }

    public class BatchSegmentResultEventArgs : EventArgs
    {
        public int SegmentIndex { get; set; }
        public string Translation { get; set; }
        public object SegmentPairRef { get; set; }

        /// <summary>Whether the source segment had inline tags.</summary>
        public bool HasTags { get; set; }

        /// <summary>Tag map for reconstructing tags in the target segment.</summary>
        public Dictionary<int, TagInfo> TagMap { get; set; }
    }

    // ─── Engine ──────────────────────────────────────────────

    /// <summary>
    /// Core batch translation engine. UI-agnostic — communicates via events.
    /// Ported from Python Supervertaler's PreTranslationWorker.
    /// </summary>
    public class BatchTranslator
    {
        public event EventHandler<BatchProgressEventArgs> Progress;
        public event EventHandler<BatchCompletedEventArgs> Completed;
        public event EventHandler<BatchSegmentResultEventArgs> SegmentTranslated;

        /// <summary>
        /// Translates segments in batches using the configured LLM provider.
        /// This method is async and should be called on a background thread
        /// or via Task.Run.
        /// </summary>
        public async Task TranslateAsync(
            List<BatchSegment> segments,
            string sourceLang,
            string targetLang,
            AiSettings aiSettings,
            List<TermEntry> termbaseTerms,
            int batchSize,
            CancellationToken cancellationToken,
            string customPromptContent = null,
            string customSystemPrompt = null,
            List<string> documentSegments = null,
            string kbContext = null)
        {
            var sw = Stopwatch.StartNew();
            int translated = 0;
            int failed = 0;
            int skipped = 0;

            // Aggregated prompt log accumulators — combined into one Reports entry at the end
            int aggInputTokens = 0;
            int aggOutputTokens = 0;
            bool aggHasError = false;

            if (segments == null || segments.Count == 0)
            {
                Completed?.Invoke(this, new BatchCompletedEventArgs
                {
                    TotalTime = sw.Elapsed
                });
                return;
            }

            // Build system prompt (composable: base → custom prompt → termbase → document context)
            var includeDoc = aiSettings?.IncludeDocumentContext != false;
            var maxDocSegs = aiSettings?.DocumentContextMaxSegments ?? 500;
            var includeTermMeta = aiSettings?.IncludeTermMetadata != false;
            var systemPrompt = TranslationPrompt.BuildSystemPrompt(
                sourceLang, targetLang,
                customPromptContent, termbaseTerms, customSystemPrompt,
                includeDoc ? documentSegments : null,
                maxDocSegs,
                includeTermMeta,
                kbContext);

            // Resolve provider settings
            var provider = aiSettings.SelectedProvider ?? LlmModels.ProviderOpenAi;
            var model = aiSettings.GetSelectedModel();
            var apiKey = LlmClient.ResolveApiKey(provider, aiSettings.ApiKeys);
            string baseUrl = null;

            if (provider == LlmModels.ProviderOllama)
                baseUrl = aiSettings.OllamaEndpoint ?? "http://localhost:11434";
            else if (provider == LlmModels.ProviderCustomOpenAi)
            {
                var profile = aiSettings.GetActiveCustomProfile();
                if (profile != null)
                {
                    baseUrl = profile.Endpoint;
                    model = profile.Model;
                    apiKey = profile.ApiKey;
                }
            }

            // Determine max tokens based on batch size
            var modelInfo = LlmModels.FindModel(model);
            int maxTokens = modelInfo?.DefaultMaxTokens ?? 16384;

            if (batchSize <= 0) batchSize = 20;

            // Split into batches
            int totalBatches = (segments.Count + batchSize - 1) / batchSize;

            RaiseProgress(0, segments.Count, "Starting translation...", false, TimeSpan.Zero);

            using (var client = new LlmClient(provider, model, apiKey, baseUrl, maxTokens,
                ollamaTimeoutMinutes: aiSettings.OllamaTimeoutMinutes))
            {
                for (int batchNum = 0; batchNum < totalBatches; batchNum++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    int startIdx = batchNum * batchSize;
                    int endIdx = Math.Min(startIdx + batchSize, segments.Count);
                    int batchCount = endIdx - startIdx;

                    RaiseProgress(startIdx, segments.Count,
                        $"Translating batch {batchNum + 1}/{totalBatches} " +
                        $"(segments {startIdx + 1}\u2013{endIdx})...",
                        false, sw.Elapsed);

                    try
                    {
                        var batchSw = Stopwatch.StartNew();

                        // Build numbered segment list for prompt
                        var promptSegments = new List<BatchSegmentInput>();
                        for (int i = startIdx; i < endIdx; i++)
                        {
                            promptSegments.Add(new BatchSegmentInput
                            {
                                Number = i + 1, // 1-based numbering
                                SourceText = segments[i].SourceText
                            });
                        }

                        // Build user prompt
                        var userPrompt = TranslationPrompt.BuildBatchUserPrompt(promptSegments);

                        // Call LLM — suppress per-batch log entries; we fire one aggregated entry at the end
                        var response = await client.SendPromptAsync(
                            userPrompt, systemPrompt, maxTokens, cancellationToken,
                            feature: Models.PromptLogFeature.BatchTranslate,
                            suppressLog: true);

                        // Accumulate token counts for the aggregated log entry
                        aggInputTokens += TokenEstimator.EstimateInputTokens(userPrompt, systemPrompt);
                        aggOutputTokens += TokenEstimator.EstimateTokens(response);

                        // Parse response
                        var parsed = TranslationPrompt.ParseBatchResponse(response, batchCount);

                        // Map parsed translations back to segments by number
                        var translationMap = new Dictionary<int, string>();
                        foreach (var p in parsed)
                            translationMap[p.Number] = p.Translation;

                        // Apply translations
                        int batchTranslated = 0;
                        int batchFailed = 0;

                        for (int i = startIdx; i < endIdx; i++)
                        {
                            int number = i + 1; // match 1-based numbering

                            string translation;
                            if (translationMap.TryGetValue(number, out translation)
                                && !string.IsNullOrWhiteSpace(translation))
                            {
                                SegmentTranslated?.Invoke(this, new BatchSegmentResultEventArgs
                                {
                                    SegmentIndex = segments[i].Index,
                                    Translation = translation,
                                    SegmentPairRef = segments[i].SegmentPairRef,
                                    HasTags = segments[i].HasTags,
                                    TagMap = segments[i].TagMap
                                });

                                batchTranslated++;
                                translated++;
                            }
                            else
                            {
                                batchFailed++;
                                failed++;
                            }

                            RaiseProgress(i + 1, segments.Count, null, false, sw.Elapsed);
                        }

                        batchSw.Stop();
                        RaiseProgress(endIdx, segments.Count,
                            $"\u2713 Batch {batchNum + 1} complete: " +
                            $"{batchTranslated} translated" +
                            (batchFailed > 0 ? $", {batchFailed} failed" : "") +
                            $" ({batchSw.Elapsed.TotalSeconds:F1}s)",
                            false, sw.Elapsed);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Log the batch error and continue to next batch
                        failed += batchCount;
                        aggHasError = true;
                        RaiseProgress(endIdx, segments.Count,
                            $"\u2717 Batch {batchNum + 1} failed: {ex.Message}",
                            true, sw.Elapsed);
                    }
                }
            }

            sw.Stop();

            // Fire a single aggregated log entry for the entire Batch Translate operation
            if (aggInputTokens > 0 || aggOutputTokens > 0)
            {
                var aggModelInfo = LlmModels.FindModel(model);
                var aggEntry = new PromptLogEntry
                {
                    Timestamp = DateTime.Now,
                    Feature = PromptLogFeature.BatchTranslate,
                    PromptName = totalBatches > 1 ? $"{totalBatches} batches · {segments.Count} segments" : null,
                    Provider = provider,
                    Model = model,
                    DisplayModel = aggModelInfo?.DisplayName ?? model,
                    SystemPrompt = systemPrompt,
                    UserPrompt = totalBatches > 1
                        ? $"({totalBatches} batches combined — expand system prompt to see translation instructions)"
                        : null,
                    EstimatedInputTokens = aggInputTokens,
                    EstimatedOutputTokens = aggOutputTokens,
                    EstimatedCost = TokenEstimator.EstimateCost(model, aggInputTokens, aggOutputTokens),
                    Duration = sw.Elapsed,
                    IsError = aggHasError
                };
                LlmClient.FirePromptCompleted(aggEntry);
            }

            Completed?.Invoke(this, new BatchCompletedEventArgs
            {
                Translated = translated,
                Failed = failed,
                Skipped = skipped,
                TotalTime = sw.Elapsed,
                WasCancelled = cancellationToken.IsCancellationRequested
            });
        }

        private void RaiseProgress(int current, int total, string message,
            bool isError, TimeSpan elapsed)
        {
            Progress?.Invoke(this, new BatchProgressEventArgs
            {
                Current = current,
                Total = total,
                Message = message,
                IsError = isError,
                Elapsed = elapsed
            });
        }
    }
}

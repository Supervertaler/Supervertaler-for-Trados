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
        FilteredEmptyOnly,
        /// <summary>
        /// Every segment whose status is NOT finalized — i.e. all statuses
        /// except Translated, Approved (translation), and Approved (sign-off).
        /// Includes Not Translated, Draft, and Rejected. Lets the user batch the
        /// segments that still need work without touching confirmed/signed-off ones.
        /// </summary>
        NotFinalized
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
        public string SourceText { get; set; }
        public string Translation { get; set; }
        public object SegmentPairRef { get; set; }

        /// <summary>Whether the source segment had inline tags.</summary>
        public bool HasTags { get; set; }

        /// <summary>Tag map for reconstructing tags in the target segment.</summary>
        public Dictionary<int, TagInfo> TagMap { get; set; }

        /// <summary>
        /// Set by the event handler to <c>false</c> when writing the translation
        /// to Trados fails. <see cref="BatchTranslator"/> reads this back after
        /// the event returns and treats a failed write as a failed segment in
        /// the final report. Default <c>true</c> so handlers that don't write
        /// (or that don't exist) don't get penalised.
        ///
        /// Requires the event to be raised SYNCHRONOUSLY - any async handler
        /// (BeginInvoke, fire-and-forget Task) will return before setting the
        /// flag and the translator will record success regardless of the
        /// actual write outcome.
        /// </summary>
        public bool WriteSucceeded { get; set; } = true;
    }

    // ─── Engine ──────────────────────────────────────────────

    /// <summary>
    /// Core batch translation engine. UI-agnostic - communicates via events.
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

            // Aggregated prompt log accumulators - combined into one Reports entry at the end
            int aggInputTokens = 0;
            int aggOutputTokens = 0;
            bool aggHasError = false;

            // Real API-reported usage, if every batch returned parseable usage info.
            // If even one batch fails to report (e.g. Ollama, parse failure on a
            // weird response shape), we drop back to the chars/4 estimate for the
            // whole run - mixing the two would produce a misleading "actual" total.
            int aggActualRegularIn = 0;
            int aggActualCacheRead = 0;
            int aggActualCacheWrite = 0;
            int aggActualOutput = 0;
            bool aggActualUsageComplete = true;

            if (segments == null || segments.Count == 0)
            {
                Completed?.Invoke(this, new BatchCompletedEventArgs
                {
                    TotalTime = sw.Elapsed
                });
                return;
            }

            // Build system prompt (composable: base -> custom prompt -> termbase -> document context)
            var includeDoc = aiSettings?.IncludeDocumentContext != false;
            var maxDocSegs = aiSettings?.DocumentContextMaxSegments ?? 500;
            var includeTermMeta = aiSettings?.IncludeTermMetadata != false;

            // 32-bit hosts (Trados Studio 2024 runs as x86) can address only ~2-4 GB.
            // A large document plus a long AI batch can exhaust that and crash Trados
            // in its own renderer, so pre-emptively shrink OUR footprint on 32-bit:
            // cap the document-context embed (a large Large-Object-Heap string) and
            // the batch size. No effect on 64-bit (Studio 2026).
            if (MemoryGuard.Is32Bit)
            {
                if (maxDocSegs > 50) maxDocSegs = 50;
                int requestedBatch = batchSize > 0 ? batchSize : 20;
                int clampedBatch = MemoryGuard.ClampBatchSize(requestedBatch);
                if (clampedBatch < requestedBatch)
                    RaiseProgress(0, segments.Count,
                        "32-bit Trados detected - using a smaller batch size (" + clampedBatch +
                        ") and reduced document context to stay within memory limits.",
                        false, TimeSpan.Zero);
                batchSize = clampedBatch;
            }

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

                    // Memory watchdog (32-bit hosts only). Climbing toward the
                    // address-space ceiling -> compact the heap (the big per-batch
                    // strings live on the LOH, the main fragmentation source). Past
                    // the hard limit -> stop gracefully with a clear message rather
                    // than letting Trados crash/hang in its renderer.
                    if (MemoryGuard.IsOverHardLimit())
                    {
                        aggHasError = true;
                        RaiseProgress(startIdx, segments.Count,
                            "✗ Stopped to protect Trados: this job is too large for 32-bit Trados " +
                            "Studio 2024 (memory limit reached). Translate the remaining segments in smaller " +
                            "batches, split the file, or use Supervertaler Workbench / Trados Studio 2026 (64-bit). " +
                            translated + " segment(s) translated before stopping.",
                            true, sw.Elapsed);
                        break;
                    }
                    if (MemoryGuard.IsOverSoftLimit())
                        MemoryGuard.CollectAndCompact();

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

                        // Call LLM - suppress per-batch log entries; we fire one aggregated entry at the end.
                        // enablePromptCaching: the system prompt (base instructions + custom prompt + KB +
                        // termbase + document context) is byte-stable across every batch in this run, so
                        // caching pays off from batch 2 onwards. Anthropic native and OpenRouter->Anthropic
                        // get explicit cache_control markers; OpenAI/DeepSeek/Gemini 2.5+ get implicit
                        // automatic caching at the provider layer; other providers ignore the flag.
                        var response = await client.SendPromptAsync(
                            userPrompt, systemPrompt, maxTokens, cancellationToken,
                            feature: Models.PromptLogFeature.BatchTranslate,
                            suppressLog: true,
                            enablePromptCaching: true);

                        // Accumulate token counts for the aggregated log entry
                        aggInputTokens += TokenEstimator.EstimateInputTokens(userPrompt, systemPrompt);
                        aggOutputTokens += TokenEstimator.EstimateTokens(response);

                        // Also accumulate real API-reported usage when available.
                        var usage = client.LastUsage;
                        if (usage != null)
                        {
                            aggActualRegularIn += usage.RegularInputTokens;
                            aggActualCacheRead += usage.CacheReadTokens;
                            aggActualCacheWrite += usage.CacheWriteTokens;
                            aggActualOutput += usage.OutputTokens;
                        }
                        else
                        {
                            // Provider didn't report usage - disqualify the run
                            // from showing actuals so we don't display a partial total.
                            aggActualUsageComplete = false;
                        }

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
                                var args = new BatchSegmentResultEventArgs
                                {
                                    SegmentIndex = segments[i].Index,
                                    SourceText = segments[i].SourceText,
                                    Translation = translation,
                                    SegmentPairRef = segments[i].SegmentPairRef,
                                    HasTags = segments[i].HasTags,
                                    TagMap = segments[i].TagMap
                                };
                                SegmentTranslated?.Invoke(this, args);

                                // Only count as translated if the handler actually wrote
                                // the segment to Trados. Pre-v4.19.55 the counter was
                                // bumped here unconditionally, so write failures (e.g.
                                // ProcessSegmentPair throwing) would be logged but the
                                // final completion summary would still over-report
                                // success. Requires SegmentTranslated to be raised
                                // synchronously so the flag is up-to-date here.
                                if (args.WriteSucceeded)
                                {
                                    batchTranslated++;
                                    translated++;
                                }
                                else
                                {
                                    batchFailed++;
                                    failed++;
                                }
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
                        ? $"({totalBatches} batches combined - expand system prompt to see translation instructions)"
                        : null,
                    EstimatedInputTokens = aggInputTokens,
                    EstimatedOutputTokens = aggOutputTokens,
                    EstimatedCost = TokenEstimator.EstimateCost(model, aggInputTokens, aggOutputTokens),
                    IsCostKnown = TokenEstimator.HasPricing(model),
                    Duration = sw.Elapsed,
                    IsError = aggHasError
                };

                // If every batch reported real usage, attach the cache-aware actuals
                // so the Reports tab shows the true billed cost (no `~` prefix).
                if (aggActualUsageComplete && (aggActualRegularIn + aggActualCacheRead + aggActualCacheWrite + aggActualOutput) > 0)
                {
                    aggEntry.ActualRegularInputTokens = aggActualRegularIn;
                    aggEntry.ActualCacheReadTokens = aggActualCacheRead;
                    aggEntry.ActualCacheWriteTokens = aggActualCacheWrite;
                    aggEntry.ActualOutputTokens = aggActualOutput;
                    aggEntry.ActualCost = TokenEstimator.ComputeActualCost(model,
                        aggActualRegularIn, aggActualCacheRead, aggActualCacheWrite, aggActualOutput);
                }

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

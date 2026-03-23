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
    // ─── Event Args ──────────────────────────────────────────

    public class ProofreadSegmentEventArgs : EventArgs
    {
        public int SegmentIndex { get; set; }
        public ProofreadingIssue Issue { get; set; }
    }

    public class ProofreadCompletedEventArgs : EventArgs
    {
        public int TotalChecked { get; set; }
        public int IssueCount { get; set; }
        public int OkCount { get; set; }
        public TimeSpan Elapsed { get; set; }
        public bool Cancelled { get; set; }
    }

    // ─── Engine ──────────────────────────────────────────────

    /// <summary>
    /// Core batch proofreading engine. UI-agnostic — communicates via events.
    /// Parallel to BatchTranslator — uses the same event-driven pattern.
    /// </summary>
    public class BatchProofreader
    {
        public event EventHandler<BatchProgressEventArgs> Progress;
        public event EventHandler<ProofreadSegmentEventArgs> SegmentProofread;
        public event EventHandler<ProofreadCompletedEventArgs> Completed;

        /// <summary>
        /// Proofreads segments in batches using the configured LLM provider.
        /// This method is async and should be called on a background thread
        /// or via Task.Run.
        /// </summary>
        public async Task ProofreadAsync(
            List<BatchSegment> segments,
            string sourceLang,
            string targetLang,
            AiSettings aiSettings,
            List<TermEntry> termbaseTerms,
            int batchSize,
            CancellationToken cancellationToken,
            string customPromptContent = null)
        {
            var sw = Stopwatch.StartNew();
            int totalChecked = 0;
            int issueCount = 0;
            int okCount = 0;

            if (segments == null || segments.Count == 0)
            {
                Completed?.Invoke(this, new ProofreadCompletedEventArgs
                {
                    Elapsed = sw.Elapsed
                });
                return;
            }

            // Build system prompt
            var systemPrompt = ProofreadingPrompt.BuildSystemPrompt(
                sourceLang, targetLang, termbaseTerms, customPromptContent);

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

            // Determine max tokens based on model
            var modelInfo = LlmModels.FindModel(model);
            int maxTokens = modelInfo?.DefaultMaxTokens ?? 16384;

            if (batchSize <= 0) batchSize = 20;

            // Split into batches
            int totalBatches = (segments.Count + batchSize - 1) / batchSize;

            RaiseProgress(0, segments.Count, "Starting proofreading...", false, TimeSpan.Zero);

            using (var client = new LlmClient(provider, model, apiKey, baseUrl, maxTokens))
            {
                for (int batchNum = 0; batchNum < totalBatches; batchNum++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    int startIdx = batchNum * batchSize;
                    int endIdx = Math.Min(startIdx + batchSize, segments.Count);
                    int batchCount = endIdx - startIdx;

                    RaiseProgress(startIdx, segments.Count,
                        $"Proofreading batch {batchNum + 1}/{totalBatches} " +
                        $"(segments {startIdx + 1}\u2013{endIdx})...",
                        false, sw.Elapsed);

                    try
                    {
                        var batchSw = Stopwatch.StartNew();

                        // Build numbered segment list for prompt
                        var promptSegments = new List<(int number, string source, string target)>();
                        for (int i = startIdx; i < endIdx; i++)
                        {
                            promptSegments.Add((
                                number: i + 1, // 1-based numbering
                                source: segments[i].SourceText,
                                target: segments[i].ExistingTarget
                            ));
                        }

                        // Build user prompt
                        var userPrompt = ProofreadingPrompt.BuildBatchUserPrompt(promptSegments);

                        // Call LLM
                        var response = await client.SendPromptAsync(
                            userPrompt, systemPrompt, maxTokens, cancellationToken,
                            feature: Models.PromptLogFeature.Proofread);

                        // Parse response
                        var parsed = ProofreadingPrompt.ParseBatchResponse(
                            response, startIdx + 1, batchCount);

                        // Map parsed results back to segments by number
                        var resultMap = new Dictionary<int, (bool isOk, string issue, string suggestion)>();
                        foreach (var p in parsed)
                            resultMap[p.segmentNumber] = (p.isOk, p.issueDescription, p.suggestion);

                        // Apply results
                        int batchIssues = 0;
                        int batchOk = 0;

                        for (int i = startIdx; i < endIdx; i++)
                        {
                            int number = i + 1; // match 1-based numbering

                            // Extract paragraph unit ID and segment ID from ref array
                            string puId = null, segId = null;
                            if (segments[i].SegmentPairRef is string[] refArr && refArr.Length >= 2)
                            {
                                puId = refArr[0];
                                segId = refArr[1];
                            }

                            var proofResult = new ProofreadingIssue
                            {
                                SegmentIndex = segments[i].Index,
                                SegmentNumber = segments[i].Index + 1, // actual document segment number
                                SourceText = segments[i].SourceText,
                                TargetText = segments[i].ExistingTarget,
                                SegmentPairRef = segments[i].SegmentPairRef,
                                ParagraphUnitId = puId,
                                SegmentId = segId
                            };

                            if (resultMap.TryGetValue(number, out var result))
                            {
                                proofResult.IsOk = result.isOk;
                                proofResult.IssueDescription = result.issue;
                                proofResult.Suggestion = result.suggestion;
                            }
                            else
                            {
                                // No result from AI — mark as OK by default
                                proofResult.IsOk = true;
                            }

                            SegmentProofread?.Invoke(this, new ProofreadSegmentEventArgs
                            {
                                SegmentIndex = segments[i].Index,
                                Issue = proofResult
                            });

                            if (proofResult.IsOk)
                            {
                                batchOk++;
                                okCount++;
                            }
                            else
                            {
                                batchIssues++;
                                issueCount++;
                            }

                            totalChecked++;
                            RaiseProgress(i + 1, segments.Count, null, false, sw.Elapsed);
                        }

                        batchSw.Stop();
                        RaiseProgress(endIdx, segments.Count,
                            $"\u2713 Batch {batchNum + 1} complete: " +
                            $"{batchOk} OK, {batchIssues} issues" +
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
                        totalChecked += batchCount;
                        RaiseProgress(endIdx, segments.Count,
                            $"\u2717 Batch {batchNum + 1} failed: {ex.Message}",
                            true, sw.Elapsed);
                    }
                }
            }

            sw.Stop();

            Completed?.Invoke(this, new ProofreadCompletedEventArgs
            {
                TotalChecked = totalChecked,
                IssueCount = issueCount,
                OkCount = okCount,
                Elapsed = sw.Elapsed,
                Cancelled = cancellationToken.IsCancellationRequested
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Supervertaler.Core;
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

        /// <summary>
        /// The number Trados Studio shows for the segment ("12", "331 a" for a split
        /// one), or null where it is not known. <see cref="Index"/> is no use to a
        /// translator: in a merged document Studio starts again at 1 in each file,
        /// and a one-segment run (Alt+T) has Index 0.
        /// </summary>
        public string Number { get; set; }

        /// <summary>
        /// The file the segment is in, in a document made of several files;
        /// null otherwise. Studio numbers every file from 1, so a number alone
        /// does not name a segment there: the log names it with its file, and a
        /// Proofread request that held "segment 3" of two files gave both of them
        /// one verdict.
        /// </summary>
        public string File { get; set; }

        /// <summary>
        /// The segment as the log names it (<see cref="SegmentLabel"/>): "segment 2 of
        /// Text.docx"; by its position when Studio's number is not known.
        /// <paramref name="withFile"/> false for the diagnostic log, which gets pasted
        /// into issues: a file's name can be a client's.
        /// </summary>
        public string Label(bool capital = false, bool withFile = true) =>
            SegmentLabel.Of(Number ?? (Index + 1).ToString(), withFile ? File : null, capital);

        public string SourceText { get; set; }
        public string ExistingTarget { get; set; }

        /// <summary>
        /// Opaque reference to the Trados ISegmentPair. Stored as object
        /// so the engine doesn't depend on Trados SDK types.
        /// </summary>
        public object SegmentPairRef { get; set; }

        /// <summary>
        /// How close the TM match for this segment is, 0-100, or 0 when there is
        /// none or the translator has turned the feature off.
        ///
        /// <para>Two paths fill this. #116 searches the project's memories, and
        /// sets <see cref="TmSourceText"/> and <see cref="TmTargetText"/> with it -
        /// a fuzzy is then safe to send at any score, because the model can read
        /// the source it was made for and see which words differ. #110 is the
        /// fallback when no memory can be searched: it reads what Studio left on
        /// the segment (its TranslationOrigin, with <see cref="ExistingTarget"/>
        /// as the target) and is acted on ONLY at 100, because that origin records
        /// how close the match was but not the source - and a fuzzy with no source
        /// is a translation of a sentence the model cannot read.</para>
        /// </summary>
        public int TmMatchPercent { get; set; }

        /// <summary>
        /// #116: the SOURCE the TM match was made for, as the memory holds it.
        /// Non-null only on the searched path. Its presence is what makes a
        /// sub-100% match sendable - never set this to the segment's own source,
        /// which would tell the model a 72% match is exact.
        /// </summary>
        public string TmSourceText { get; set; }

        /// <summary>
        /// #116: the approved translation of <see cref="TmSourceText"/>. Set
        /// together with it, and never one without the other.
        /// </summary>
        public string TmTargetText { get; set; }

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

        /// <summary>
        /// #109: the list marker the document renders in front of this segment's
        /// paragraph ("9.", "e)", "•"), set on the paragraph's first segment only,
        /// or null. SourceText stays the document's text; the sentinel is added to
        /// the prompt only, so the TMX backup, the logs and every comparison see the
        /// segment as Studio has it.
        /// </summary>
        public string StructureMarker { get; set; }

        /// <summary>
        /// The segment's repetition key (<see cref="RepetitionTokens.KeyOf"/>) when
        /// the run keeps repetitions consistent, otherwise null.
        /// </summary>
        public string RepetitionKey { get; set; }
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

        /// <summary>The repetitions line for the log, or null when the run did not keep them consistent.</summary>
        public string RepetitionSummary { get; set; }

        /// <summary>
        /// The run's one usage entry - the one also fired through
        /// <see cref="LlmClient.PromptCompleted"/> for the Reports tab and the usage
        /// log - or null when the run sent nothing. SuperBench takes a contender's
        /// cost from here: listening on the global event, it also counted a Batch
        /// Translate run on the same model that happened to finish meanwhile.
        /// </summary>
        public PromptLogEntry Usage { get; set; }
    }

    public class BatchSegmentResultEventArgs : EventArgs
    {
        public int SegmentIndex { get; set; }

        /// <summary>The segment as the log names it, as Studio does: <see cref="BatchSegment.Label"/>.</summary>
        public string SegmentName { get; set; }

        public string SourceText { get; set; }
        public string Translation { get; set; }
        public object SegmentPairRef { get; set; }

        /// <summary>Whether the source segment had inline tags.</summary>
        public bool HasTags { get; set; }

        /// <summary>Tag map for reconstructing tags in the target segment.</summary>
        public Dictionary<int, TagInfo> TagMap { get; set; }

        /// <summary>
        /// The AI's [[TC: ...]] comment on this segment, to be added as a Trados
        /// comment once the target is written, or null. Set only when the
        /// translator has chosen comments (<see cref="AiSettings.TcMarkersAsComments"/>);
        /// otherwise the marker stays at the end of <see cref="Translation"/>. See
        /// <see cref="BatchTranslator.PlaceComment"/>.
        /// </summary>
        public string Comment { get; set; }

        /// <summary>
        /// The AI's own translation of this segment, when it was given its
        /// repetition's instead and the two differ - kept in the backup TMX, so the
        /// backup still records what the model returned. Otherwise null.
        /// </summary>
        public string AiTranslation { get; set; }

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
        /// Keeps the run's repetitions consistent: every later occurrence of a source
        /// is written with its group's translation (docs/design/consistent-repetitions.md).
        /// Set by Batch Translate only - on this instance, never through a field the
        /// other users of <see cref="SegmentTranslated"/> share (Alt+T, SuperBench) -
        /// and null otherwise, which leaves every result as the AI returned it.
        /// </summary>
        public RepetitionRun Repetitions { get; set; }

        /// <summary>
        /// The document the run was started in, for the write handler; null when the
        /// handler should use whatever document is active.
        /// </summary>
        public object DocumentRef { get; set; }

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
            IList<DocumentSegment> documentSegments = null,
            string kbContext = null,
            bool retryUntilComplete = false,
            StructureContextMode structureContext = StructureContextMode.Off)
        {
            var sw = Stopwatch.StartNew();
            int translated = 0;
            int failed = 0;
            int skipped = 0;
            var notTranslated = new List<BatchSegment>(); // collected during the pass; retried if enabled
            // Repetitions (only when Repetitions is set): segments left empty whose
            // group has no translation yet, and the groups that got one in the
            // current batch - see FillFromGroups.
            var awaitingGroup = new List<BatchSegment>();
            var groupsFixed = new HashSet<string>(StringComparer.Ordinal);
            bool promptTooLarge = false; // a local model read too little of it: no retry pass either

            // Aggregated prompt log accumulators - combined into one Reports entry at the end
            int aggInputTokens = 0;
            int aggOutputTokens = 0;
            bool aggHasError = false;

            // Why the last failed request failed, for the aggregated entry. A run
            // whose only request failed used to leave no entry at all - nothing in
            // Reports, the usage log or the prompt log - because the entry was only
            // fired when some tokens had been counted, and a failed request counts
            // none. The reason was on screen in the Batch log and nowhere else.
            string lastError = null;

            // Real API-reported usage, if every batch returned parseable usage info.
            // If even one batch fails to report (e.g. Ollama, parse failure on a
            // weird response shape), we drop back to the chars/4 estimate for the
            // whole run - mixing the two would produce a misleading "actual" total.
            int aggActualRegularIn = 0;
            int aggActualCacheRead = 0;
            int aggActualCacheWrite = 0;
            int aggActualOutput = 0;
            bool aggActualUsageComplete = true;

            // A one-batch run's user prompt, for Reports: the part of what was sent
            // that is not in the system prompt - its segments and, for one segment,
            // that segment's terms. A longer run shows a placeholder instead.
            string singleUserPrompt = null;

            // Segments left empty because their reply broke the output contract
            // twice (see RecheckAsync below), for the closing summary.
            int refusedTotal = 0;
            var tcAsComments = aiSettings?.TcMarkersAsComments == true;

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

            var systemPrompt = SystemPromptFor(segments.Count,
                sourceLang, targetLang,
                customPromptContent, termbaseTerms, customSystemPrompt,
                includeDoc ? documentSegments : null,
                maxDocSegs,
                includeTermMeta,
                kbContext,
                structureContext);

            // Resolve provider settings
            var provider = aiSettings.SelectedProvider ?? LlmModels.ProviderOpenAi;
            var model = aiSettings.GetSelectedModel();
            string apiKey = null, keyProblem = null, baseUrl = null;

            if (provider == LlmModels.ProviderOllama)
                baseUrl = aiSettings.OllamaEndpoint ?? "http://localhost:11434";
            else if (provider == LlmModels.ProviderCustomOpenAi)
            {
                var profile = aiSettings.GetActiveCustomProfile();
                if (profile != null)
                {
                    baseUrl = profile.Endpoint;
                    model = profile.Model;
                }
                apiKey = TradosApiKeys.For(profile, out keyProblem);
            }
            else
                apiKey = TradosApiKeys.For(provider, aiSettings, out keyProblem);

            // No key to use stops the run before any request, with the reason as its
            // one error - a provider is never sent a blank key. Callers check first,
            // but SuperBench's contenders and a key removed since come straight here.
            if (apiKey == null && provider != LlmModels.ProviderOllama)
            {
                RaiseProgress(0, segments.Count, keyProblem, true, sw.Elapsed);
                Completed?.Invoke(this, new BatchCompletedEventArgs
                {
                    Failed = segments.Count,
                    TotalTime = sw.Elapsed
                });
                return;
            }

            // Determine max tokens based on batch size
            var modelInfo = LlmModels.FindModel(model);
            int maxTokens = modelInfo?.DefaultMaxTokens ?? 16384;

            if (batchSize <= 0) batchSize = 20;

            // Split into batches
            int totalBatches = (segments.Count + batchSize - 1) / batchSize;

            // How a segment is named in the log: as Studio names it (BatchSegment.Label).
            // Without its number, a one-segment run (Alt+T) carries Index 0 whatever
            // the segment's place in the document. The diagnostic log leaves the file
            // out (withFile: false).
            string Name(BatchSegment seg, bool withFile = true) =>
                seg.Number == null && segments.Count == 1 ? "The segment" : seg.Label(capital: true, withFile: withFile);

            // Every request of the run is counted here, whichever pass sends it, so
            // the one aggregated Reports entry adds up.
            void Count(LlmClient c, string userPrompt, string response)
            {
                aggInputTokens += TokenEstimator.EstimateInputTokens(userPrompt, systemPrompt);
                aggOutputTokens += TokenEstimator.EstimateTokens(response);
                var usage = c.LastUsage;
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
            }

            // Reported, never refused: a target that drops a formatting tag is often
            // right, and ReconstructTarget falls back to plain text when it cannot
            // place them. This line is the only record that it happened.
            void NoteTagDifference(BatchSegment seg, string reply)
            {
                var tags = ReplyCheck.TagDifference(seg.SourceText, reply);
                if (tags != null)
                    DiagnosticLog.Log("ReplyCheck", $"{Name(seg, withFile: false)}: tags differ from the source ({tags}); written");
            }

            // The reply check (core ReplyCheck, OutputContract). A reply holding
            // anything besides the translation - a note, markdown, an explanation -
            // is taken out of `replies` and asked for once more, in one request with
            // the batch's other offenders and the contract said again in front. A
            // second offence is not written: the segment is left empty, with the
            // reason in the log. An empty segment costs the translator one segment;
            // a note in the target can reach a client.
            //
            // `sent` maps each number in the request to its segment. Returns the
            // numbers refused, which "Retry segments left empty" must not send a
            // third time. A re-ask that fails or is cancelled refuses nothing: those
            // segments were never answered, and are left empty like any other.
            //
            // The log lines carry reasons and sizes, never text: the diagnostic log
            // gets pasted into issues, and the source is client material. The whole
            // exchange is in the prompt log, when that is on.
            async Task<HashSet<int>> RecheckAsync(LlmClient c, Dictionary<int, string> replies,
                Dictionary<int, BatchSegment> sent, string where)
            {
                var refused = new HashSet<int>();
                var offenders = new List<int>();
                foreach (var kv in sent)
                {
                    if (!replies.TryGetValue(kv.Key, out var reply) || string.IsNullOrWhiteSpace(reply)) continue;
                    var problem = ReplyCheck.Problem(kv.Value.SourceText, reply);
                    if (problem == null) { NoteTagDifference(kv.Value, reply); continue; }

                    offenders.Add(kv.Key);
                    replies.Remove(kv.Key);
                    DiagnosticLog.Log("ReplyCheck", $"{Name(kv.Value, withFile: false)}, {where}: {problem} "
                        + $"({reply.Length} chars back for {kv.Value.SourceText?.Length ?? 0}); asked again");
                    RaiseProgress(0, 0, $"{Name(kv.Value)} came back with more than a translation ({problem}); asking again.",
                        false, sw.Elapsed);
                }
                if (offenders.Count == 0 || cancellationToken.IsCancellationRequested) return refused;

                var again = new List<BatchSegmentInput>();
                for (int k = 0; k < offenders.Count; k++)
                    again.Add(ToPromptInput(sent[offenders[k]], k + 1, structureContext));
                var userPrompt = OutputContract.Reminder + Environment.NewLine + Environment.NewLine
                    + UserPromptFor(segments.Count, termbaseTerms, includeTermMeta, again);

                string response;
                try
                {
                    response = await c.SendPromptAsync(userPrompt, systemPrompt, maxTokens, cancellationToken,
                        feature: PromptLogFeature.BatchTranslate, suppressLog: true, enablePromptCaching: true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return refused;
                }
                catch (Exception ex)
                {
                    aggHasError = true;
                    lastError = ex.Message;
                    RaiseProgress(0, 0, $"Asking again failed: {ex.Message}", true, sw.Elapsed);
                    return refused;
                }
                PromptFileLogger.RecordBatchExchange(provider, model, 0, totalBatches, null, userPrompt, response,
                    promptName: where + ", asked again");
                Count(c, userPrompt, response);

                var answers = new Dictionary<int, string>();
                foreach (var p in TranslationPrompt.ParseBatchResponse(response, again))
                    answers[p.Number] = p.Translation;

                for (int k = 0; k < offenders.Count; k++)
                {
                    var seg = sent[offenders[k]];
                    answers.TryGetValue(k + 1, out var t);
                    t = CleanTarget(t, seg, structureContext);
                    if (string.IsNullOrWhiteSpace(t)) continue;   // not answered: an ordinary empty segment

                    var second = ReplyCheck.Problem(seg.SourceText, t);
                    if (second == null)
                    {
                        replies[offenders[k]] = t;
                        NoteTagDifference(seg, t);
                        DiagnosticLog.Log("ReplyCheck", $"{Name(seg, withFile: false)}, {where}: the second answer was a clean translation");
                        continue;
                    }

                    refused.Add(offenders[k]);
                    DiagnosticLog.Log("ReplyCheck", $"{Name(seg, withFile: false)}, {where}: refused - {second}, twice; left for the translator");
                    RaiseProgress(0, 0, $"{Name(seg)} was left empty: it came back with more than a translation twice "
                        + $"({second}). Translate it again, or by hand.", true, sw.Elapsed);
                }
                return refused;
            }

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
                        lastError = "Stopped to protect Trados: 32-bit memory limit reached";
                        RaiseProgress(startIdx, segments.Count,
                            "✗ Stopped to protect Trados: this job is too large for 32-bit Trados " +
                            "Studio 2024 (memory limit reached). Translate the remaining segments in smaller " +
                            "batches, split the file, or use Trados Studio 2026 (64-bit). " +
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
                            promptSegments.Add(ToPromptInput(segments[i], i + 1, structureContext));
                        }

                        // Build user prompt
                        var userPrompt = UserPromptFor(segments.Count, termbaseTerms, includeTermMeta, promptSegments);
                        if (totalBatches == 1) singleUserPrompt = userPrompt;

                        // Call LLM - suppress per-batch log entries; we fire one aggregated entry at the end.
                        // enablePromptCaching: the system prompt (base instructions + custom prompt + KB +
                        // termbase + document context) is byte-stable across every batch in this run, so
                        // caching pays off from batch 2 onwards. A one-segment run (Alt+T, Translate active segment) has one batch,
                        // but the NEXT segment's run sends the same system prompt, because its terms travel
                        // in the user prompt (SystemPromptFor) - so it pays off from the second segment.
                        // Anthropic native and OpenRouter->Anthropic get explicit cache_control markers;
                        // OpenAI/DeepSeek/Gemini 2.5+ get implicit automatic caching at the provider layer;
                        // other providers ignore the flag.
                        var response = await client.SendPromptAsync(
                            userPrompt, systemPrompt, maxTokens, cancellationToken,
                            feature: PromptLogFeature.BatchTranslate,
                            suppressLog: true,
                            enablePromptCaching: true);

                        // #98: suppressLog above keeps the per-batch call out of the Reports
                        // tab and the usage ledger, but the on-disk prompt log needs exactly
                        // this exchange - the segments sent and the reply received - which the
                        // aggregated entry at the end deliberately does not carry.
                        PromptFileLogger.RecordBatchExchange(provider, model, batchNum + 1, totalBatches,
                            systemPrompt, userPrompt, response);

                        // Token counts and real API-reported usage, for the aggregated log entry
                        Count(client, userPrompt, response);

                        // Parse response
                        var parsed = TranslationPrompt.ParseBatchResponse(response, promptSegments);

                        // Map parsed translations back to segments by number
                        var translationMap = new Dictionary<int, string>();
                        foreach (var p in parsed)
                            translationMap[p.Number] = CleanTarget(p.Translation,
                                p.Number >= 1 && p.Number <= segments.Count ? segments[p.Number - 1] : null, structureContext);

                        // Nothing reaches the document before the reply check.
                        var sent = new Dictionary<int, BatchSegment>();
                        for (int i = startIdx; i < endIdx; i++) sent[i + 1] = segments[i];
                        var refused = await RecheckAsync(client, translationMap, sent,
                            $"batch {batchNum + 1} of {totalBatches}");
                        refusedTotal += refused.Count;

                        // Apply translations
                        int batchTranslated = 0;
                        int batchFailed = 0;

                        for (int i = startIdx; i < endIdx; i++)
                        {
                            int number = i + 1; // match 1-based numbering

                            string translation;
                            bool answered = translationMap.TryGetValue(number, out translation)
                                && !string.IsNullOrWhiteSpace(translation);

                            // Only count as translated if the handler actually wrote
                            // the segment to Trados. Pre-v4.19.55 the counter was
                            // bumped here unconditionally, so write failures (e.g.
                            // ProcessSegmentPair throwing) would be logged but the
                            // final completion summary would still over-report
                            // success. Deliver raises SegmentTranslated synchronously
                            // so the flag is up-to-date when it returns. A repetition
                            // is written with its group's translation, and one the AI
                            // did not answer is filled from its group if it has one.
                            var outcome = Deliver(segments[i], answered ? translation : null, tcAsComments, groupsFixed);
                            if (outcome == DeliveryOutcome.Written)
                            {
                                batchTranslated++;
                                translated++;
                            }
                            else
                            {
                                batchFailed++;
                                failed++;
                                if (outcome == DeliveryOutcome.Declined || !refused.Contains(number))
                                    notTranslated.Add(segments[i]);
                                if (outcome == DeliveryOutcome.NothingToWrite) AwaitGroup(segments[i], awaitingGroup);
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
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (PromptTooLargeException ex)
                    {
                        // Every batch carries the same instructions, so on a local
                        // model each one would be read for minutes and refused in turn.
                        failed += segments.Count - startIdx;
                        aggHasError = true;
                        lastError = ex.Message;
                        for (int i = startIdx; i < segments.Count; i++) notTranslated.Add(segments[i]);
                        promptTooLarge = true;
                        RaiseProgress(endIdx, segments.Count,
                            $"✗ Stopped at batch {batchNum + 1}: {ex.Message}",
                            true, sw.Elapsed);
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Log the batch error and continue to next batch. A
                        // repetition whose group already has a translation is
                        // filled from it; the rest wait for a group or a retry.
                        aggHasError = true;
                        lastError = ex.Message;
                        int filledHere = 0;
                        for (int i = startIdx; i < endIdx; i++)
                        {
                            var outcome = Deliver(segments[i], null, tcAsComments, groupsFixed);
                            if (outcome == DeliveryOutcome.Written)
                            {
                                filledHere++;
                                translated++;
                                continue;
                            }
                            failed++;
                            notTranslated.Add(segments[i]);
                            if (outcome == DeliveryOutcome.NothingToWrite) AwaitGroup(segments[i], awaitingGroup);
                        }
                        RaiseProgress(endIdx, segments.Count,
                            $"\u2717 Batch {batchNum + 1} failed: {ex.Message}"
                            + (filledHere > 0 ? $" ({filledHere} filled from their repetitions)" : ""),
                            true, sw.Elapsed);
                    }

                    // Segments left empty earlier whose group got a translation in
                    // this batch: written now, so a run stopped after this batch
                    // leaves no group half filled.
                    int filledLater = FillFromGroups(awaitingGroup, groupsFixed, notTranslated, tcAsComments);
                    if (filledLater > 0)
                    {
                        translated += filledLater;
                        failed -= filledLater;
                        RaiseProgress(endIdx, segments.Count,
                            $"{filledLater} segment(s) left empty earlier filled from their repetitions.",
                            false, sw.Elapsed);
                    }
                }

                // \u2500\u2500 Optional retry passes ("Retry segments left empty") \u2500\u2500
                // Re-translate the segments the main pass left empty, in extra
                // passes, until none remain or we hit the cap. Each retried
                // segment that fills in corrects the translated/failed counters
                // (it was counted as failed in the pass that left it empty).
                // Local numbering (1..N per chunk) so a subset re-runs cleanly.
                if (retryUntilComplete && !promptTooLarge)
                {
                    const int maxRetryPasses = 5;
                    var pending = notTranslated;
                    for (int pass = 1;
                         pass <= maxRetryPasses && pending.Count > 0 && !cancellationToken.IsCancellationRequested;
                         pass++)
                    {
                        RaiseProgress(translated, segments.Count,
                            $"Retry {pass}/{maxRetryPasses}: re-translating {pending.Count} segment(s) left empty...",
                            false, sw.Elapsed);

                        var stillEmpty = new List<BatchSegment>();
                        int rChunks = (pending.Count + batchSize - 1) / batchSize;
                        for (int b = 0; b < rChunks && !cancellationToken.IsCancellationRequested; b++)
                        {
                            int rs = b * batchSize;
                            int re = Math.Min(rs + batchSize, pending.Count);
                            try
                            {
                                var ps = new List<BatchSegmentInput>();
                                for (int i = rs; i < re; i++)
                                    ps.Add(new BatchSegmentInput { Number = (i - rs) + 1, SourceText = PromptSource(pending[i], structureContext) });

                                var rUserPrompt = UserPromptFor(segments.Count, termbaseTerms, includeTermMeta, ps);
                                var rResponse = await client.SendPromptAsync(
                                    rUserPrompt, systemPrompt, maxTokens, cancellationToken,
                                    feature: PromptLogFeature.BatchTranslate,
                                    suppressLog: true, enablePromptCaching: true);

                                Count(client, rUserPrompt, rResponse);

                                var rParsed = TranslationPrompt.ParseBatchResponse(rResponse, ps);
                                var rMap = new Dictionary<int, string>();
                                foreach (var p in rParsed)
                                    rMap[p.Number] = CleanTarget(p.Translation,
                                        rs + p.Number - 1 < pending.Count && p.Number >= 1 ? pending[rs + p.Number - 1] : null, structureContext);

                                var rSent = new Dictionary<int, BatchSegment>();
                                for (int i = rs; i < re; i++) rSent[(i - rs) + 1] = pending[i];
                                var rRefused = await RecheckAsync(client, rMap, rSent, $"retry {pass}");
                                refusedTotal += rRefused.Count;

                                for (int i = rs; i < re; i++)
                                {
                                    string t;
                                    bool answered = rMap.TryGetValue((i - rs) + 1, out t) && !string.IsNullOrWhiteSpace(t);
                                    var outcome = Deliver(pending[i], answered ? t : null, tcAsComments, groupsFixed);
                                    if (outcome == DeliveryOutcome.Written)
                                    {
                                        translated++;
                                        failed--;
                                        awaitingGroup.Remove(pending[i]);
                                    }
                                    else if (outcome == DeliveryOutcome.Declined || !rRefused.Contains((i - rs) + 1))
                                        stillEmpty.Add(pending[i]);
                                    RaiseProgress(translated, segments.Count, null, false, sw.Elapsed);
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                for (int i = rs; i < re; i++) stillEmpty.Add(pending[i]);
                                break;
                            }
                            catch (Exception ex)
                            {
                                aggHasError = true;
                                lastError = ex.Message;
                                for (int i = rs; i < re; i++) stillEmpty.Add(pending[i]);
                                RaiseProgress(translated, segments.Count,
                                    $"\u2717 Retry {pass} batch {b + 1} failed: {ex.Message}",
                                    true, sw.Elapsed);
                            }
                        }
                        pending = stillEmpty;

                        // After the pass, not during it: pending is the list the
                        // pass walks by index.
                        int filledAfterPass = FillFromGroups(awaitingGroup, groupsFixed, pending, tcAsComments);
                        translated += filledAfterPass;
                        failed -= filledAfterPass;
                    }
                }
            }

            sw.Stop();

            // One line to find them by in a long log. A one-segment run has just said it.
            if (refusedTotal > 0 && segments.Count > 1)
                RaiseProgress(0, 0, $"{refusedTotal} segment(s) left empty because the AI's reply held more than "
                    + "a translation twice. Their numbers are in the log above.", true, sw.Elapsed);

            // Fire a single aggregated log entry for the entire Batch Translate operation -
            // also when nothing came back, so a failure is on record (see lastError).
            PromptLogEntry usage = null;
            if (aggInputTokens > 0 || aggOutputTokens > 0 || aggHasError)
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
                        : singleUserPrompt,
                    EstimatedInputTokens = aggInputTokens,
                    EstimatedOutputTokens = aggOutputTokens,
                    EstimatedCost = TokenEstimator.EstimateCost(model, aggInputTokens, aggOutputTokens),
                    IsCostKnown = TokenEstimator.HasPricing(model),
                    Duration = sw.Elapsed,
                    IsError = aggHasError,
                    ErrorMessage = lastError
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
                usage = aggEntry;
            }

            Completed?.Invoke(this, new BatchCompletedEventArgs
            {
                Translated = translated,
                Failed = failed,
                Skipped = skipped,
                TotalTime = sw.Elapsed,
                WasCancelled = cancellationToken.IsCancellationRequested,
                RepetitionSummary = Repetitions?.Summary(),
                Usage = usage
            });
        }

        /// <summary>
        /// Translates the segments with a machine-translation engine - DeepL, Google
        /// or Microsoft (core <see cref="MachineTranslation"/>, docs/design/mt-engines.md)
        /// - instead of an AI model, and reports exactly as <see cref="TranslateAsync"/>
        /// does: <see cref="SegmentTranslated"/> for each segment, through the same
        /// <see cref="Deliver"/> (so consistent repetitions, the write handler and its
        /// backup apply alike), <see cref="Progress"/>, one usage entry, and
        /// <see cref="Completed"/>. SuperBench captures from it now; Batch Translate
        /// can write from it, should Michael decide to offer engines there.
        ///
        /// <para>No prompt, termbase, SuperMemory, TM match or list marker reaches an
        /// engine: it takes the source text alone. DeepL may get
        /// <paramref name="contextSources"/> (decision 2), which it reads without
        /// translating or charging for it. Segments are written as each request
        /// returns, in document order.</para>
        /// </summary>
        /// <param name="sourceLanguage">BCP 47, as Trados has it: "en-GB".</param>
        /// <param name="contextSources">Source texts in document order, for DeepL's context; null for none.</param>
        public async Task TranslateWithEngineAsync(List<BatchSegment> segments, string engine, string apiKey, string region,
            string sourceLanguage, string targetLanguage, IList<string> contextSources, string settingsPlace,
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            segments = segments ?? new List<BatchSegment>();
            int translated = 0;
            var groupsFixed = new HashSet<string>(StringComparer.Ordinal);
            var name = MachineTranslation.DisplayName(engine);

            if (segments.Count == 0)
            {
                Completed?.Invoke(this, new BatchCompletedEventArgs { TotalTime = sw.Elapsed });
                return;
            }

            RaiseProgress(0, segments.Count, $"Translating {segments.Count} segment(s) with {name}...", false, TimeSpan.Zero);
            var job = new MtJob
            {
                Engine = engine,
                ApiKey = apiKey,
                Region = region,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                Texts = segments.Select(s => s.SourceText ?? "").ToList(),
                SettingsPlace = settingsPlace,
                ContextFor = contextSources == null || contextSources.Count == 0 ? null
                    : (Func<int, int, string>)((start, count) => MachineTranslation.ContextAround(contextSources, start, count)),
            };

            // Called as each request comes back, on the thread that awaited it;
            // Deliver raises SegmentTranslated synchronously, as the write handler needs.
            // Repetitions as in TranslateAsync: one left empty waits for its group.
            var handedOver = new bool[segments.Count];
            var awaitingGroup = new List<BatchSegment>();
            void ChunkDone(int start, string[] chunk)
            {
                for (int i = 0; i < chunk.Length; i++)
                {
                    handedOver[start + i] = true;
                    var delivered = Deliver(segments[start + i], chunk[i], false, groupsFixed);
                    if (delivered == DeliveryOutcome.Written) translated++;
                    else if (delivered == DeliveryOutcome.NothingToWrite) AwaitGroup(segments[start + i], awaitingGroup);
                }
                translated += FillFromGroups(awaitingGroup, groupsFixed, null, false);
                RaiseProgress(start + chunk.Length, segments.Count, null, false, sw.Elapsed);
            }

            var outcome = await MachineTranslation.TranslateAsync(job, ChunkDone, cancellationToken).ConfigureAwait(false);

            // A repetition in a request that failed, or was never sent, gets its
            // group's translation when the group has one (consistent repetitions,
            // decision 3) - as TranslateAsync does for a failed batch.
            if (Repetitions != null && !outcome.Cancelled)
            {
                for (int i = 0; i < segments.Count; i++)
                    if (!handedOver[i] && Deliver(segments[i], null, false, groupsFixed) == DeliveryOutcome.Written) translated++;
            }
            sw.Stop();

            if (outcome.Failure != null)
            {
                DiagnosticLog.Log("MT", $"{name}: {outcome.Failure.Kind}, HTTP {outcome.Failure.HttpStatus}; "
                    + (outcome.Failure.EngineMessage ?? "(nothing said)"));
                // The sentence alone, as a missing key is reported: SuperBench shows it
                // in its report as the contender's error, and the log marks it red.
                RaiseProgress(translated, segments.Count, outcome.Failure.Message, true, sw.Elapsed);
            }

            // One entry for the run, as TranslateAsync fires: characters in place of
            // tokens, and their cost at list price. Fired also for a run that failed
            // before anything came back, so the failure is on record.
            var usage = new PromptLogEntry
            {
                Timestamp = DateTime.Now,
                Feature = PromptLogFeature.BatchTranslate,
                PromptName = segments.Count + " segments",
                Provider = engine,
                Model = MachineTranslation.ModelName(engine),
                DisplayModel = name,
                Characters = outcome.BilledCharacters,
                EstimatedCost = outcome.Cost,
                IsCostKnown = true,
                Duration = sw.Elapsed,
                IsError = outcome.Failure != null,
                ErrorMessage = outcome.Failure?.Message,
            };
            LlmClient.FirePromptCompleted(usage);

            Completed?.Invoke(this, new BatchCompletedEventArgs
            {
                Translated = translated,
                Failed = segments.Count - translated,
                TotalTime = sw.Elapsed,
                WasCancelled = outcome.Cancelled || cancellationToken.IsCancellationRequested,
                RepetitionSummary = Repetitions?.Summary(),
                Usage = usage
            });
        }

        // ─── Repetitions ────────────────────────────────────────

        private enum DeliveryOutcome { Written, Declined, NothingToWrite }

        /// <summary>
        /// Raises one segment's result for the write handler and reports how it
        /// went. When <see cref="Repetitions"/> is set, a repetition is given its
        /// group's translation in place of <paramref name="own"/> (null when the AI
        /// gave none), and a written translation that is the first of its group
        /// fixes the group's - only once Studio has taken the write, so a declined
        /// write fixes nothing. The group keeps the text before
        /// <see cref="PlaceComment"/>, so every occurrence gets the same [[TC: ]]
        /// note, inline or as a comment. Raised synchronously, as
        /// <see cref="BatchSegmentResultEventArgs.WriteSucceeded"/> requires.
        /// </summary>
        private DeliveryOutcome Deliver(BatchSegment segment, string own, bool tcAsComments, HashSet<string> groupsFixed)
        {
            var run = Repetitions;
            var key = run != null ? segment.RepetitionKey : null;
            var delivery = key != null ? run.Decide(key, own) : null;
            var text = delivery != null ? delivery.Text : own;
            if (string.IsNullOrWhiteSpace(text)) return DeliveryOutcome.NothingToWrite;

            var args = new BatchSegmentResultEventArgs
            {
                SegmentIndex = segment.Index,
                SegmentName = segment.Label(),
                SourceText = segment.SourceText,
                Translation = PlaceComment(text, tcAsComments, out var comment),
                Comment = comment,
                SegmentPairRef = segment.SegmentPairRef,
                HasTags = segment.HasTags,
                TagMap = segment.TagMap,
                AiTranslation = delivery != null && delivery.Differed ? own : null
            };
            SegmentTranslated?.Invoke(this, args);
            if (!args.WriteSucceeded) return DeliveryOutcome.Declined;

            if (delivery != null)
            {
                run.Commit(key, delivery);
                if (delivery.Source == RepetitionSource.Own) groupsFixed?.Add(key);
            }
            return DeliveryOutcome.Written;
        }

        /// <summary>A segment left empty waits for its group, when the run keeps repetitions.</summary>
        private void AwaitGroup(BatchSegment segment, List<BatchSegment> awaitingGroup)
        {
            if (Repetitions != null && segment.RepetitionKey != null) awaitingGroup.Add(segment);
        }

        /// <summary>
        /// Writes the segments left empty earlier in the run whose group has got a
        /// translation since (decision 3 of the design), and takes them off
        /// <paramref name="retryList"/>. Called after every batch, so a run stopped
        /// after any batch leaves no group half filled. Returns how many it wrote.
        /// </summary>
        private int FillFromGroups(List<BatchSegment> awaitingGroup, HashSet<string> groupsFixed,
            List<BatchSegment> retryList, bool tcAsComments)
        {
            if (awaitingGroup.Count == 0 || groupsFixed.Count == 0)
            {
                groupsFixed.Clear();
                return 0;
            }

            int filled = 0;
            var stillWaiting = new List<BatchSegment>(awaitingGroup.Count);
            foreach (var segment in awaitingGroup)
            {
                if (groupsFixed.Contains(segment.RepetitionKey)
                    && Deliver(segment, null, tcAsComments, null) == DeliveryOutcome.Written)
                {
                    filled++;
                    retryList?.Remove(segment);
                }
                else
                {
                    stillWaiting.Add(segment);
                }
            }

            awaitingGroup.Clear();
            awaitingGroup.AddRange(stillWaiting);
            groupsFixed.Clear();
            return filled;
        }

        /// <summary>
        /// #110: without the match's source, only an exact match is safe to show.
        /// </summary>
        internal const int ExactMatch = 100;

        /// <summary>
        /// Whether a run's termbase hits travel in the user prompt, with its
        /// segments, rather than in the system prompt. Yes for a one-segment run -
        /// Translate active segment (Alt+T), which the translator repeats down the document. Its terms are
        /// that one segment's (#102), so they change with every segment, and in
        /// the system prompt they changed the whole prompt with them: every
        /// request paid 1.25x to write a cache that the next one could never
        /// read. With them in the user prompt, the system prompt (instructions,
        /// SuperMemory, document content) is the same for every segment of the
        /// document, and every request after the first reads it at a tenth of
        /// the price. A longer run keeps one list for all its batches in the
        /// system prompt, where it is cached once for the run.
        /// </summary>
        internal static bool TermsTravelWithTheSegment(int segmentCount) => segmentCount == 1;

        /// <summary>
        /// The system prompt a run of <paramref name="segmentCount"/> segments
        /// sends. Internal, with <see cref="UserPromptFor"/>, so Preview prompt
        /// builds its prompt through the SAME functions the run does.
        /// </summary>
        internal static string SystemPromptFor(int segmentCount,
            string sourceLang, string targetLang,
            string customPromptContent, List<TermEntry> termbaseTerms, string customSystemPrompt,
            IList<DocumentSegment> documentSegments, int maxDocumentSegments, bool includeTermMetadata,
            string kbContext, StructureContextMode structureContext)
        {
            var prompt = TranslationPrompt.BuildSystemPrompt(
                sourceLang, targetLang,
                customPromptContent,
                TermsTravelWithTheSegment(segmentCount) ? null : termbaseTerms,
                customSystemPrompt,
                null, maxDocumentSegments, includeTermMetadata,
                kbContext, structureContext, numberedSegments: documentSegments);

            // Last, after the prompt, the bank, the terms and the document, so no
            // prompt can switch it off and nothing after it can soften it: the
            // translator's own prompt, AutoPrompt's, and a replaced base prompt
            // all get it. Backed by the reply check in TranslateAsync. The same
            // text on every request, so it sits inside the cached prefix.
            return prompt + Environment.NewLine + Environment.NewLine + OutputContract.Text;
        }

        /// <summary>
        /// Where the AI's [[TC: ...]] comment goes. With
        /// <paramref name="asTradosComment"/> it comes off the translation and out
        /// in <paramref name="comment"/>, for the caller to add as a Trados comment
        /// once the target is written. Otherwise it stays at the end, rewritten in
        /// the standard form one space after the text, whatever bracket form or
        /// line break the model used, and <paramref name="comment"/> is null. An
        /// empty marker is dropped either way. Call it after the reply check: a
        /// reply with a misplaced marker is refused there, not placed here.
        /// </summary>
        internal static string PlaceComment(string translation, bool asTradosComment, out string comment)
        {
            var body = ReplyCheck.WithoutComment(translation, out comment);
            if (comment == null || asTradosComment) return body;
            var inline = InlineComment(body, comment);
            comment = null;
            return inline;
        }

        /// <summary>A translation with its [[TC: ...]] comment at the end, the way it is kept inline.</summary>
        internal static string InlineComment(string translation, string comment) =>
            translation + " [[TC: " + comment + "]]";

        /// <summary>
        /// One request's user prompt: its segments, preceded - in a one-segment
        /// run - by that segment's terms, worded exactly as the system prompt
        /// would have worded them.
        /// </summary>
        internal static string UserPromptFor(int segmentCount, List<TermEntry> termbaseTerms,
            bool includeTermMetadata, List<BatchSegmentInput> promptSegments)
        {
            var segmentsPrompt = TranslationPrompt.BuildBatchUserPrompt(promptSegments);
            if (!TermsTravelWithTheSegment(segmentCount)) return segmentsPrompt;

            var terms = TranslationPrompt.BuildTermbaseBlock(termbaseTerms, includeTermMetadata);
            return terms == null
                ? segmentsPrompt
                : terms + Environment.NewLine + Environment.NewLine + segmentsPrompt;
        }

        /// <summary>
        /// #109: the source as the model sees it - the document's list marker in a
        /// sentinel, then the text - when markers are being sent; the text alone
        /// otherwise. BatchSegment.SourceText itself is never changed.
        ///
        /// <para>Internal rather than private so Preview prompt derives the source
        /// text through the SAME function the run does. Two derivations that
        /// drift would make the preview a plausible lie.</para>
        /// </summary>
        internal static string PromptSource(BatchSegment segment, StructureContextMode mode)
        {
            return mode == StructureContextMode.Markers
                ? StructureContext.Prefix(segment.StructureMarker, segment.SourceText)
                : segment.SourceText;
        }

        /// <summary>
        /// One segment as the prompt carries it. Internal for the same reason as
        /// <see cref="PromptSource"/>: Preview prompt must show exactly what the run
        /// sends, and the memory block is the part most worth being able to check.
        ///
        /// <para>Two ways a match reaches the model, and the difference is whether
        /// its source came with it. A searched match (#116) is sent at whatever
        /// score it scored, source and target together, because the model can then
        /// see which words differ. A match read off the segment's TranslationOrigin
        /// (#110) has no source, so it is sent only at 100% - where the match's
        /// source IS this segment's source and nothing is hidden.</para>
        /// </summary>
        internal static BatchSegmentInput ToPromptInput(
            BatchSegment segment, int number, StructureContextMode mode)
        {
            var input = new BatchSegmentInput
            {
                Number = number,
                SourceText = PromptSource(segment, mode)
            };

            if (!string.IsNullOrWhiteSpace(segment.TmSourceText)
                && !string.IsNullOrWhiteSpace(segment.TmTargetText))
            {
                input.FuzzySourceText = segment.TmSourceText;
                input.FuzzyTargetText = segment.TmTargetText;
                input.FuzzyMatchPercent = segment.TmMatchPercent;
            }
            else if (segment.TmMatchPercent >= ExactMatch
                     && !string.IsNullOrWhiteSpace(segment.ExistingTarget))
            {
                // No FuzzySourceText: Studio records the percentage but not the
                // source the match was made for, and writing this segment's own
                // source there would tell the model the match is exact when it
                // may be 72%. Only reached at 100%, where it genuinely is.
                input.FuzzyTargetText = segment.ExistingTarget;
                input.FuzzyMatchPercent = segment.TmMatchPercent;
            }

            return input;
        }

        /// <summary>
        /// #109: a sentinel the model echoed at the start of a target is removed
        /// before anything is written, and logged: that log is the per-model
        /// evidence on whether the rule is obeyed. Only when markers were sent - with
        /// the feature off the prompt is unchanged and so is the reply.
        /// </summary>
        /// <param name="segment">The segment the reply is for; null for a number
        /// that was not sent, which the parser does not return.</param>
        private string CleanTarget(string translation, BatchSegment segment, StructureContextMode mode)
        {
            if (mode == StructureContextMode.Off || string.IsNullOrEmpty(translation)) return translation;
            var cleaned = StructureContext.Strip(translation, out var fired);
            if (fired)
            {
                DiagnosticLog.Log("Structure", $"Sentinel echoed by the model in {segment?.Label(withFile: false) ?? "a reply"}: removed. Reply began: {Truncate(translation, 60)}");
                RaiseProgress(0, 0, $"Structure marker echoed by the model in {segment?.Label() ?? "a reply"} – removed before writing.", false, TimeSpan.Zero);
            }
            return cleaned;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            return s.Substring(0, max) + "…";
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

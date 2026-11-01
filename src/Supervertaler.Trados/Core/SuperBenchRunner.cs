using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Supervertaler.Core;
using Supervertaler.Core.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>Everything a batch run would use, prepared once and shared by every contender (#107).</summary>
    internal sealed class SuperBenchInputs
    {
        public List<BatchSegment> Segments = new List<BatchSegment>();
        public string SourceLang, TargetLang, DocumentName;
        /// <summary>The languages as codes ("en-GB"), for a machine-translation engine; SourceLang and TargetLang are names.</summary>
        public string SourceCode, TargetCode;
        public AiSettings AiSettings;
        public List<TermEntry> TermbaseTerms = new List<TermEntry>();
        public int BatchSize = 20;
        public string CustomPromptContent, CustomSystemPrompt;
        public List<DocumentSegment> DocSegments;
        public string KbContext;
        /// <summary>One line for the report: which prompt, how many terms, context on or off.</summary>
        public string SettingsSummary;
        /// <summary>#109: whether the segments carry list markers, exactly as the batch would send them.</summary>
        public StructureContextMode StructureContext = StructureContextMode.Off;

        public SuperBenchInputs Take(int count)
        {
            var copy = (SuperBenchInputs)MemberwiseClone();
            copy.Segments = Segments.Take(Math.Max(0, count)).Select((s, i) => new BatchSegment
            {
                Index = i, Number = s.Number, File = s.File, SourceText = s.SourceText, ExistingTarget = s.ExistingTarget,
                SegmentPairRef = s.SegmentPairRef, HasTags = s.HasTags, TagMap = s.TagMap,
                StructureMarker = s.StructureMarker,
            }).ToList();
            return copy;
        }
    }

    internal sealed class ModelChoice
    {
        public string Provider, Model, DisplayModel;
        public override string ToString() => (DisplayModel ?? Model) + " (" + Provider + ")";
    }

    /// <summary>
    /// Runs SuperBench (#107): the same segments through the batch translator once
    /// per contender - the pipeline the real batch uses, so the prompt, the filtered
    /// termbase, the context and the batch size are identical and only the model
    /// varies - capturing the translations instead of writing them, then the judge.
    ///
    /// <para>A contender may also be a machine-translation engine - DeepL, Google
    /// Translate, Microsoft Translator (docs/design/mt-engines.md). It goes through
    /// the batch translator's engine path, which takes the source text alone: no
    /// prompt, termbase or SuperMemory, and the report says so. When document
    /// context is on, DeepL reads the neighbouring segments of the sample, free of
    /// charge (decision 2).</para>
    /// </summary>
    internal static class SuperBenchRunner
    {
        public static async Task<SuperBench.Run> RunAsync(SuperBenchInputs inputs, IList<ModelChoice> contenders,
            ModelChoice judge, IProgress<string> progress, CancellationToken ct)
        {
            var run = new SuperBench.Run
            {
                DocumentName = inputs.DocumentName,
                SourceLang = inputs.SourceLang,
                TargetLang = inputs.TargetLang,
                Sources = inputs.Segments.Select(s => s.SourceText ?? "").ToList(),
                SettingsSummary = inputs.SettingsSummary
                    + (DeepLGetsContext(inputs, contenders) ? ", DeepL given the neighbouring segments as context" : ""),
                JudgeProvider = judge?.Provider,
                JudgeModel = judge?.Model,
                JudgeDisplayModel = judge?.DisplayModel,
            };

            int n = 0;
            foreach (var choice in contenders)
            {
                ct.ThrowIfCancellationRequested();
                n++;
                progress?.Report($"Translating with {choice} ({n} of {contenders.Count})…");
                var contender = new SuperBench.Contender
                {
                    Provider = choice.Provider, Model = choice.Model, DisplayModel = choice.DisplayModel,
                    Translations = Enumerable.Repeat<string>(null, inputs.Segments.Count).ToList(),
                };
                run.Contenders.Add(contender);
                await TranslateWith(inputs, contender, ct).ConfigureAwait(false);
            }

            SuperBench.AssignBlindLabels(run);

            if (judge != null)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Judging with {judge}…");
                await Judge(inputs, run, judge, ct).ConfigureAwait(false);
            }
            return run;
        }

        private static bool DeepLGetsContext(SuperBenchInputs inputs, IEnumerable<ModelChoice> contenders) =>
            inputs.AiSettings?.IncludeDocumentContext == true
            && (contenders ?? Enumerable.Empty<ModelChoice>()).Any(c => c.Provider == MachineTranslation.DeepL);

        private static async Task TranslateWith(SuperBenchInputs inputs, SuperBench.Contender contender, CancellationToken ct)
        {
            var translator = new BatchTranslator();
            var translations = contender.Translations;
            var sync = new object();
            translator.SegmentTranslated += (s, e) =>
            {
                // Capture, do not write: the document is untouched by a benchmark.
                lock (sync)
                {
                    if (e.SegmentIndex >= 0 && e.SegmentIndex < translations.Count)
                        translations[e.SegmentIndex] = e.Translation;
                }
                e.WriteSucceeded = true;
            };

            // The translator reports its own failures - a refused key, a model that
            // does not exist, a malformed reply - as progress messages, not exceptions.
            // Keep the last one so the report says why a column is empty.
            string lastError = null;
            translator.Progress += (s, e) =>
            {
                if (!e.IsError || string.IsNullOrWhiteSpace(e.Message)) return;
                // The message can carry the provider's whole JSON reply; the last line
                // is the sentence written for the user.
                var lines = e.Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                lastError = lines.Length > 0 ? lines[lines.Length - 1].Trim() : e.Message.Trim();
            };

            // The run's own usage entry, from its own translator: a Batch Translate run
            // on the same model finishing meanwhile is not this contender's cost.
            PromptLogEntry usage = null;
            translator.Completed += (s, e) => usage = e.Usage;

            var sw = Stopwatch.StartNew();
            try
            {
                if (contender.IsMachineTranslation)
                {
                    // The key first: none, or one this computer cannot read, sends nothing.
                    var key = TradosApiKeys.ForEngine(contender.Provider, out var keyProblem);
                    if (key == null)
                    {
                        contender.Error = keyProblem;
                        return;
                    }
                    var context = contender.Provider == MachineTranslation.DeepL && inputs.AiSettings?.IncludeDocumentContext == true
                        ? inputs.Segments.Select(s => s.SourceText ?? "").ToList()
                        : null;
                    await translator.TranslateWithEngineAsync(inputs.Segments, contender.Provider, key,
                        inputs.AiSettings?.MicrosoftTranslatorRegion, inputs.SourceCode, inputs.TargetCode, context,
                        TradosApiKeys.SettingsPlace, ct).ConfigureAwait(false);
                }
                else
                {
                    var settings = CloneSettings(inputs.AiSettings);
                    settings.SetProviderAndModel(contender.Provider, contender.Model);
                    await translator.TranslateAsync(inputs.Segments, inputs.SourceLang, inputs.TargetLang, settings,
                        inputs.TermbaseTerms, inputs.BatchSize, ct, inputs.CustomPromptContent, inputs.CustomSystemPrompt,
                        inputs.DocSegments, inputs.KbContext, retryUntilComplete: false,
                        structureContext: inputs.StructureContext).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                contender.Error = ex.Message;
            }
            finally
            {
                sw.Stop();
                contender.Elapsed = sw.Elapsed;
                if (usage != null)
                {
                    contender.Cost = CostOf(usage);
                    contender.CostKnown = usage.IsCostKnown;
                    contender.Characters = usage.Characters ?? 0;
                    contender.InputTokens = (usage.ActualRegularInputTokens ?? usage.EstimatedInputTokens)
                                          + (usage.ActualCacheReadTokens ?? 0) + (usage.ActualCacheWriteTokens ?? 0);
                    contender.OutputTokens = usage.ActualOutputTokens ?? usage.EstimatedOutputTokens;
                }
                if (string.IsNullOrEmpty(contender.Error))
                {
                    if (lastError != null) contender.Error = lastError;
                    else if (translations.All(string.IsNullOrEmpty)) contender.Error = "no translation returned";
                }
            }
        }

        private static async Task Judge(SuperBenchInputs inputs, SuperBench.Run run, ModelChoice judge, CancellationToken ct)
        {
            var settings = inputs.AiSettings;
            string apiKey, keyProblem = null, baseUrl = null;
            if (judge.Provider == LlmModels.ProviderOllama)
            {
                apiKey = "ollama";
                baseUrl = settings.OllamaEndpoint ?? "http://localhost:11434";
            }
            else if (judge.Provider == LlmModels.ProviderCustomOpenAi)
            {
                var profile = settings.CustomOpenAiProfiles?.FirstOrDefault(p => p.Name == judge.Model) ?? settings.GetActiveCustomProfile();
                apiKey = TradosApiKeys.For(profile, out keyProblem); baseUrl = profile?.Endpoint;
            }
            else
            {
                apiKey = TradosApiKeys.For(judge.Provider, settings, out keyProblem);
            }
            if (string.IsNullOrEmpty(apiKey))
            {
                run.JudgeError = keyProblem;
                return;
            }

            var system = SuperBench.BuildJudgeSystemPrompt(run);
            var user = SuperBench.BuildJudgeUserPrompt(run, inputs.CustomPromptContent, TerminologyBlock(inputs.TermbaseTerms));
            var usage = new UsageCapture(judge.Provider, judge.Model, PromptLogFeature.SuperBench);
            try
            {
                usage.Start();
                using (var client = new LlmClient(judge.Provider, judge.Model, apiKey, baseUrl, maxTokens: 8192,
                           ollamaTimeoutMinutes: settings.OllamaTimeoutMinutes))
                {
                    run.JudgeReport = await client.SendPromptAsync(user, system, maxTokens: 8192, cancellationToken: ct,
                        feature: PromptLogFeature.SuperBench, promptName: "SuperBench judge").ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                run.JudgeError = ex.Message;
            }
            finally
            {
                usage.Stop();
                run.JudgeCost = usage.Cost;
            }
        }

        /// <summary>The approved terms as the judge should see them: source → target, forbidden marked.</summary>
        private static string TerminologyBlock(List<TermEntry> terms)
        {
            if (terms == null || terms.Count == 0) return null;
            var sb = new StringBuilder();
            foreach (var t in terms)
            {
                if (string.IsNullOrEmpty(t.SourceTerm) || string.IsNullOrEmpty(t.TargetTerm)) continue;
                sb.AppendLine("- " + t.SourceTerm + " → " + (t.Forbidden ? "DO NOT USE: " : "") + t.TargetTerm);
            }
            return sb.ToString();
        }

        /// <summary>A private copy of the settings, so a contender's provider and model never touch the user's.</summary>
        private static AiSettings CloneSettings(AiSettings source)
        {
            var ser = new DataContractJsonSerializer(typeof(AiSettings));
            using (var ms = new MemoryStream())
            {
                ser.WriteObject(ms, source ?? new AiSettings());
                ms.Position = 0;
                return (AiSettings)ser.ReadObject(ms);
            }
        }

        /// <summary>
        /// What one usage entry cost. A model missing from the price list is counted
        /// at the most it can have cost, and shown as "up to" (IsCostKnown false).
        /// </summary>
        private static decimal CostOf(PromptLogEntry e) =>
            e.IsCostKnown
                ? e.ActualCost ?? e.EstimatedCost
                : TokenEstimator.CostCeiling(e.Provider, e.Model,
                    e.ActualRegularInputTokens ?? e.EstimatedInputTokens,
                    e.ActualCacheReadTokens ?? 0, e.ActualCacheWriteTokens ?? 0,
                    e.ActualOutputTokens ?? e.EstimatedOutputTokens);

        /// <summary>
        /// Sums what the client reported for one provider+model while attached - for
        /// the judge, whose call is the only one tagged SuperBench. The
        /// PromptCompleted event is global; the filter keeps a stray chat call from
        /// being counted against it. A contender's cost comes from its own run
        /// (<see cref="BatchCompletedEventArgs.Usage"/>) instead.
        /// </summary>
        private sealed class UsageCapture
        {
            private readonly string _provider, _model;
            private readonly PromptLogFeature _feature;
            private readonly object _sync = new object();
            public decimal Cost; public bool CostKnown = true; public int InputTokens, OutputTokens;

            public UsageCapture(string provider, string model, PromptLogFeature feature)
            {
                _provider = provider; _model = model; _feature = feature;
            }

            public void Start() { LlmClient.PromptCompleted += OnCompleted; }
            public void Stop() { LlmClient.PromptCompleted -= OnCompleted; }

            private void OnCompleted(object sender, PromptLogEntry e)
            {
                if (e == null || e.Feature != _feature) return;
                if (!string.Equals(e.Provider, _provider, StringComparison.OrdinalIgnoreCase)) return;
                if (!string.IsNullOrEmpty(e.Model) && !string.Equals(e.Model, _model, StringComparison.OrdinalIgnoreCase)) return;
                lock (_sync)
                {
                    Cost += CostOf(e);
                    CostKnown &= e.IsCostKnown;
                    InputTokens += (e.ActualRegularInputTokens ?? e.EstimatedInputTokens)
                                 + (e.ActualCacheReadTokens ?? 0) + (e.ActualCacheWriteTokens ?? 0);
                    OutputTokens += e.ActualOutputTokens ?? e.EstimatedOutputTokens;
                }
            }
        }

        /// <summary>
        /// A rough cost before running: every contender's batch plus the judge.
        /// A model missing from the price list is counted at the most it can cost
        /// (<see cref="TokenEstimator.CostCeiling"/>), and <paramref name="upperBound"/>
        /// says so, rather than adding nothing and understating the total. A
        /// machine-translation engine is counted by the characters it would be
        /// sent, at its list price.
        /// </summary>
        public static decimal EstimateCost(SuperBenchInputs inputs, IList<ModelChoice> contenders, ModelChoice judge,
            out bool upperBound)
        {
            upperBound = false;
            if (inputs == null || inputs.Segments.Count == 0) return 0m;
            bool ceilingUsed = false;
            decimal Price(ModelChoice m, int input, int output)
            {
                if (TokenEstimator.HasPricing(m.Model)) return TokenEstimator.EstimateCost(m.Model, input, output);
                var ceiling = TokenEstimator.CostCeiling(m.Provider, m.Model, input, 0, 0, output);
                if (ceiling > 0) ceilingUsed = true;
                return ceiling;
            }
            string systemPrompt;
            try
            {
                var s = inputs.AiSettings;
                systemPrompt = TranslationPrompt.BuildSystemPrompt(inputs.SourceLang, inputs.TargetLang,
                    inputs.CustomPromptContent, inputs.TermbaseTerms, inputs.CustomSystemPrompt,
                    null,
                    s != null && s.DocumentContextMaxSegments > 0 ? s.DocumentContextMaxSegments : 500,
                    s == null || s.IncludeTermMetadata, inputs.KbContext,
                    numberedSegments: s != null && s.IncludeDocumentContext ? inputs.DocSegments : null);
            }
            catch { systemPrompt = ""; }
            var sourceText = string.Join("\n", inputs.Segments.Select(x => x.SourceText));
            int sys = TokenEstimator.EstimateTokens(systemPrompt);
            int src = TokenEstimator.EstimateTokens(sourceText);
            int batches = Math.Max(1, (int)Math.Ceiling(inputs.Segments.Count / (double)Math.Max(1, inputs.BatchSize)));
            decimal total = 0m;
            foreach (var c in contenders ?? new List<ModelChoice>())
            {
                if (MachineTranslation.IsEngine(c.Provider))
                    total += MachineTranslation.Cost(c.Provider,
                        inputs.Segments.Sum(x => MachineTranslation.CharactersAsSent(c.Provider, x.SourceText)));
                else
                    total += Price(c, sys * batches + src, (int)(src * 1.2));
            }
            if (judge != null)
            {
                // The judge reads the instructions and the approved terms as well as
                // every candidate - on a real run that was most of its input.
                int instructions = TokenEstimator.EstimateTokens(inputs.CustomPromptContent ?? "");
                int terms = (inputs.TermbaseTerms?.Count ?? 0) * 12;
                total += Price(judge,
                    instructions + terms + src * (1 + (contenders?.Count ?? 0)) + 1200, 1800);
            }
            upperBound = ceilingUsed;
            return total;
        }
    }
}

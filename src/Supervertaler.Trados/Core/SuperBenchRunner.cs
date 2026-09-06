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
        public AiSettings AiSettings;
        public List<TermEntry> TermbaseTerms = new List<TermEntry>();
        public int BatchSize = 20;
        public string CustomPromptContent, CustomSystemPrompt;
        public List<string> DocSegments;
        public string KbContext;
        /// <summary>One line for the report: which prompt, how many terms, context on or off.</summary>
        public string SettingsSummary;

        public SuperBenchInputs Take(int count)
        {
            var copy = (SuperBenchInputs)MemberwiseClone();
            copy.Segments = Segments.Take(Math.Max(0, count)).Select((s, i) => new BatchSegment
            {
                Index = i, SourceText = s.SourceText, ExistingTarget = s.ExistingTarget,
                SegmentPairRef = s.SegmentPairRef, HasTags = s.HasTags, TagMap = s.TagMap,
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
                SettingsSummary = inputs.SettingsSummary,
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

        private static async Task TranslateWith(SuperBenchInputs inputs, SuperBench.Contender contender, CancellationToken ct)
        {
            var settings = CloneSettings(inputs.AiSettings);
            settings.SetProviderAndModel(contender.Provider, contender.Model);

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

            var usage = new UsageCapture(contender.Provider, contender.Model, PromptLogFeature.BatchTranslate);
            var sw = Stopwatch.StartNew();
            try
            {
                usage.Start();
                await translator.TranslateAsync(inputs.Segments, inputs.SourceLang, inputs.TargetLang, settings,
                    inputs.TermbaseTerms, inputs.BatchSize, ct, inputs.CustomPromptContent, inputs.CustomSystemPrompt,
                    inputs.DocSegments, inputs.KbContext, retryUntilComplete: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                contender.Error = ex.Message;
            }
            finally
            {
                usage.Stop();
                sw.Stop();
                contender.Elapsed = sw.Elapsed;
                contender.Cost = usage.Cost;
                contender.CostKnown = usage.CostKnown;
                contender.InputTokens = usage.InputTokens;
                contender.OutputTokens = usage.OutputTokens;
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
            string apiKey, baseUrl = null;
            if (judge.Provider == LlmModels.ProviderOllama)
            {
                apiKey = "ollama";
                baseUrl = settings.OllamaEndpoint ?? "http://localhost:11434";
            }
            else if (judge.Provider == LlmModels.ProviderCustomOpenAi)
            {
                var profile = settings.CustomOpenAiProfiles?.FirstOrDefault(p => p.Name == judge.Model) ?? settings.GetActiveCustomProfile();
                apiKey = profile?.ApiKey; baseUrl = profile?.Endpoint;
            }
            else
            {
                apiKey = LlmClient.ResolveApiKey(judge.Provider, settings.ApiKeys);
            }
            if (string.IsNullOrEmpty(apiKey))
            {
                run.JudgeError = "no API key for " + judge.Provider;
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
        /// Sums what the client reported for one provider+model while attached. The
        /// PromptCompleted event is global; a benchmark runs one model at a time, and
        /// the filter keeps a stray chat call from being counted against it.
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
                    Cost += e.ActualCost ?? e.EstimatedCost;
                    CostKnown &= e.IsCostKnown;
                    InputTokens += (e.ActualRegularInputTokens ?? e.EstimatedInputTokens)
                                 + (e.ActualCacheReadTokens ?? 0) + (e.ActualCacheWriteTokens ?? 0);
                    OutputTokens += e.ActualOutputTokens ?? e.EstimatedOutputTokens;
                }
            }
        }

        /// <summary>A rough cost before running: every contender's batch plus the judge.</summary>
        public static decimal EstimateCost(SuperBenchInputs inputs, IList<ModelChoice> contenders, ModelChoice judge)
        {
            if (inputs == null || inputs.Segments.Count == 0) return 0m;
            string systemPrompt;
            try
            {
                var s = inputs.AiSettings;
                systemPrompt = TranslationPrompt.BuildSystemPrompt(inputs.SourceLang, inputs.TargetLang,
                    inputs.CustomPromptContent, inputs.TermbaseTerms, inputs.CustomSystemPrompt,
                    s != null && s.IncludeDocumentContext ? inputs.DocSegments : null,
                    s != null && s.DocumentContextMaxSegments > 0 ? s.DocumentContextMaxSegments : 500,
                    s == null || s.IncludeTermMetadata, inputs.KbContext);
            }
            catch { systemPrompt = ""; }
            var sourceText = string.Join("\n", inputs.Segments.Select(x => x.SourceText));
            int sys = TokenEstimator.EstimateTokens(systemPrompt);
            int src = TokenEstimator.EstimateTokens(sourceText);
            int batches = Math.Max(1, (int)Math.Ceiling(inputs.Segments.Count / (double)Math.Max(1, inputs.BatchSize)));
            decimal total = 0m;
            foreach (var c in contenders ?? new List<ModelChoice>())
                total += TokenEstimator.EstimateCost(c.Model, sys * batches + src, (int)(src * 1.2));
            if (judge != null)
            {
                // The judge reads the instructions and the approved terms as well as
                // every candidate - on a real run that was most of its input.
                int instructions = TokenEstimator.EstimateTokens(inputs.CustomPromptContent ?? "");
                int terms = (inputs.TermbaseTerms?.Count ?? 0) * 12;
                total += TokenEstimator.EstimateCost(judge.Model,
                    instructions + terms + src * (1 + (contenders?.Count ?? 0)) + 1200, 1800);
            }
            return total;
        }
    }
}

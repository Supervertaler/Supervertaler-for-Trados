using System;
using System.Reflection;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Turns each completed AI call (a <see cref="PromptLogEntry"/>) into a
    /// persisted <see cref="UsageRecord"/> in the JSONL ledger. Metadata only —
    /// never the prompt or response text. Driven from the existing PromptCompleted
    /// handler; on by default (<see cref="AiSettings.IsUsageLogEnabled"/>). The
    /// batch path fires one aggregated entry, so a batch run is one record.
    /// </summary>
    public static class UsageLogger
    {
        private static readonly string _appVersion =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString();

        public static void Record(PromptLogEntry entry, TermLensSettings settings)
        {
            try
            {
                if (entry == null) return;
                // Connection tests aren't real usage.
                if (entry.Feature == PromptLogFeature.ConnectionTest) return;
                if (settings?.AiSettings != null && !settings.AiSettings.IsUsageLogEnabled) return;

                var ctx = TermLensEditorViewPart.GetCurrentUsageContext()
                          ?? new UsageContextSnapshot();

                var rec = new UsageRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    Product = "trados",
                    AppVersion = _appVersion,
                    Task = entry.Feature.ToString(),
                    Provider = entry.Provider,
                    Model = entry.Model,
                    Project = ctx.Project,
                    ProjectKey = ctx.ProjectKey,
                    File = ctx.File,
                    Client = ctx.Client,
                    SrcLang = ctx.SrcLang,
                    TgtLang = ctx.TgtLang,
                    CostKnown = entry.IsCostKnown,
                    DurationS = entry.Duration.TotalSeconds,
                    Ok = !entry.IsError,
                    Error = entry.IsError ? entry.ErrorMessage : null,
                };

                if (entry.HasActualUsage)
                {
                    rec.Source = "actual";
                    rec.InputRegular = entry.ActualRegularInputTokens ?? 0;
                    rec.InputCacheRead = entry.ActualCacheReadTokens ?? 0;
                    rec.InputCacheWrite = entry.ActualCacheWriteTokens ?? 0;
                    rec.Output = entry.ActualOutputTokens ?? 0;
                    rec.CostUsd = entry.ActualCost ?? 0m;
                }
                else
                {
                    rec.Source = "estimated";
                    rec.InputRegular = entry.EstimatedInputTokens;
                    rec.Output = entry.EstimatedOutputTokens;
                    rec.CostUsd = entry.EstimatedCost;
                }

                UsageStore.Append(rec);
            }
            catch { /* never let logging disrupt translation */ }
        }
    }
}

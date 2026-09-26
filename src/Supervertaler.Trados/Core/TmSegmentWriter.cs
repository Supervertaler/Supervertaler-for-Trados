using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Sdl.Core.Globalization;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.FileTypeSupport.Framework.NativeApi;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Sdl.ProjectAutomation.FileBased;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Writes segments to the project's main translation memories exactly as
    /// Studio does when a translator confirms a segment in the editor
    /// (update_segments with updateTm: true).
    ///
    /// <para><b>Why Studio's route and not our own.</b> The ask is "overwrite the
    /// unit with the same source and context, never add a duplicate". Studio has
    /// no update mode that means that: <c>Overwrite</c> deletes EVERY unit with
    /// the same source, other contexts' variants included, and
    /// <c>AddNew</c>/<c>OverwriteCurrent</c> on their own add a second unit in
    /// the same context. A context is a pair of hashes of the preceding source
    /// and target, computed by the TM's own tokeniser (numbers and other
    /// placeables become placeholders, legacy and current TMs hash differently),
    /// so it cannot be reproduced from outside with any confidence. What the
    /// editor does instead, read from Studio's own code and replayed against a
    /// throwaway TM before this was written:</para>
    /// <list type="bullet">
    /// <item>the unit is built by Studio's own TUConverter, so tags,
    /// structure context and SID context come out as Studio writes them;</item>
    /// <item>the preceding segment travels with it as a masked unit, and the TM
    /// hashes the context itself (<c>IsDocumentImport</c>);</item>
    /// <item>the segment remembers what it last put in the main TM, as the
    /// weak hash of that target in <c>TranslationOrigin.OriginalTranslationHash</c>
    /// (pre-translation sets it too, to the match it applied). Passed back as the
    /// previous translation with <c>OverwriteCurrent</c>, the TM overwrites that
    /// one unit in place and leaves every other variant alone;</item>
    /// <item>after a successful write the new hash is stamped on the segment, so
    /// the next rewrite overwrites this one.</item>
    /// </list>
    /// Measured on a throwaway TM: a first write adds, a rewrite overwrites the
    /// same unit (same id, context kept), writing the same pair again is
    /// discarded, and a variant stored in another context survives. So the
    /// behaviour is "as if the translator had pressed Ctrl+Enter", which a
    /// translator already knows how to predict.
    ///
    /// <para><b>Threads.</b> Building the <see cref="Plan"/> reads the Trados
    /// model and must run on the UI thread (AiAssistantViewPart.PrepareTmUpdate,
    /// and the stamp afterwards). <see cref="Write"/> touches only the plain
    /// translation units built there, and is meant to run OFF the UI thread: a
    /// write to a multi-gigabyte TM or a GroupShare server must not freeze the
    /// editor.</para>
    ///
    /// <para><b>Scale.</b> One <c>AddOrUpdateTranslationUnitsMasked</c> call per
    /// TM per run of adjacent segments, not one per segment: a request is capped
    /// at 40 updates, so at most 80 units (each with its context) in a handful of
    /// calls. Tested at a few segments against a throwaway TM; designed for the
    /// 40-per-call cap against a multi-gigabyte TM, and timed in the log.</para>
    /// </summary>
    public static class TmSegmentWriter
    {
        private const string LogCategory = "TmUpdate";

        /// <summary>The statuses Studio's editor sends to the TM on confirm.
        /// Deliberately not "Translated or higher" by enum order, which would
        /// take in RejectedTranslation.</summary>
        public static readonly ConfirmationLevel[] TmLevels =
        {
            ConfirmationLevel.Translated,
            ConfirmationLevel.ApprovedTranslation,
            ConfirmationLevel.ApprovedSignOff
        };

        public static bool Qualifies(ConfirmationLevel level) => Array.IndexOf(TmLevels, level) >= 0;

        /// <summary>A main TM as named in the project's cascade.</summary>
        public sealed class TmRef
        {
            public string Name;
            /// <summary>Absolute .sdltm path; null for a server TM.</summary>
            public string FilePath;
            /// <summary>sdltm.http(s):// URI; null for a file TM.</summary>
            public string ServerUri;
        }

        /// <summary>One segment to write, built on the UI thread.</summary>
        public sealed class Unit
        {
            /// <summary>The update_segments key ("puId:segId" as KeyOf builds it).</summary>
            public string Key;
            public TranslationUnit Tu;
            /// <summary>The preceding segment's unit, sent masked so the TM can
            /// hash the context. Null at the start of a file, or when the
            /// preceding segment cannot be built: then the unit goes first in
            /// its call, which is how the TM knows it has no predecessor.</summary>
            public TranslationUnit ContextTu;
            public string ContextKey;
            /// <summary>What this segment last put in the main TM (0 = nothing).</summary>
            public int PreviousHash;
            /// <summary>OriginalTranslationHash exactly as read, so the stamp can
            /// tell whether anything else updated the TM from this segment in
            /// the meantime.</summary>
            public string HashSeen;
            /// <summary>The weak hash of the target being written, taken before the
            /// write as the editor does; stamped on the segment afterwards.</summary>
            public int NewHash;
            /// <summary>The live segment, for the stamp. UI thread only.</summary>
            public ISegmentPair Pair;
        }

        /// <summary>What happened to one unit in one TM.</summary>
        public sealed class Outcome
        {
            public string Tm;
            /// <summary>added / updated / merged / unchanged, or null on failure.</summary>
            public string Action;
            public string Error;
            /// <summary>True when the TM now holds a unit this segment should
            /// remember - the same test Studio's Update TM task applies before
            /// it stamps the hash.</summary>
            public bool Stamp;
        }

        /// <summary>Everything <see cref="Write"/> needs, collected on the UI thread.</summary>
        public sealed class Plan
        {
            public List<TmRef> Tms = new List<TmRef>();
            public List<Unit> Units = new List<Unit>();
            public CultureInfo SourceCulture;
            public CultureInfo TargetCulture;
            public FieldValues ProjectFields;
            /// <summary>Set when no write can happen at all; every unit reports it.</summary>
            public string Problem;
            /// <summary>Cascade entries with Update ticked that are not Trados TMs
            /// (an MT provider, say), named so the caller knows they were left out.</summary>
            public List<string> NotTms = new List<string>();
        }

        /// <summary>
        /// Builds the translation unit for <paramref name="pair"/> as the editor
        /// does on confirm, tracked changes accepted. Null when the source is
        /// empty. The target may be empty: a preceding segment with no
        /// translation still counts as context. UI thread.
        /// </summary>
        public static TranslationUnit BuildUnit(LanguagePair lp, ISegmentPair pair, IParagraphUnitProperties paragraph)
        {
            var build = Converter.Value
                ?? throw new InvalidOperationException("Studio's TM unit converter (TUConverter) was not found");
            // (lp, sp, paragraphProperties, stripTags, excludeTagsInLockedContentText,
            //  acceptTrackChanges, out hasSourceTrackChanges, includeTrackChanges)
            var args = new object[] { lp, pair, paragraph, false, false, true, false, false };
            TranslationUnit tu;
            try { tu = build.Invoke(null, args) as TranslationUnit; }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            if (tu?.SourceSegment == null || tu.SourceSegment.IsEmpty) return null;
            return tu;
        }

        /// <summary>
        /// Studio's own document-to-TM converter, the one its editor calls on
        /// confirm. Studio 2024 carries it in Sdl.LanguagePlatform.TranslationMemory;
        /// Studio 2026 moved it to Sdl.LanguagePlatform.TranslationMemoryTools, which
        /// is NOT on Studio's public-assembly list (pluginconfig.xml) - a compile-time
        /// reference to it stops the plugin loading, as #132 found for another
        /// assembly. So it is found by name at run time in both, with the same
        /// signature, the way DocumentItemFactory is. Null if neither is there.
        /// </summary>
        private static readonly Lazy<MethodInfo> Converter = new Lazy<MethodInfo>(() =>
        {
            var signature = new[]
            {
                typeof(LanguagePair), typeof(ISegmentPair), typeof(IParagraphUnitProperties),
                typeof(bool), typeof(bool), typeof(bool), typeof(bool).MakeByRefType(), typeof(bool)
            };
            foreach (var name in new[]
            {
                "Sdl.LanguagePlatform.TranslationMemory.TUConverter, Sdl.LanguagePlatform.TranslationMemory",
                "Sdl.LanguagePlatform.TranslationMemoryTools.TUConverter, Sdl.LanguagePlatform.TranslationMemoryTools"
            })
            {
                try
                {
                    var m = Type.GetType(name, false)?.GetMethod("BuildLinguaTranslationUnit",
                        BindingFlags.Public | BindingFlags.Static, null, signature, null);
                    if (m != null && typeof(TranslationUnit).IsAssignableFrom(m.ReturnType)) return m;
                }
                catch { }
            }
            DiagnosticLog.Log(LogCategory, "TUConverter.BuildLinguaTranslationUnit not found in either assembly.");
            return null;
        });

        /// <summary>The editor's rule for a unit's origin: a confirmed segment
        /// is a translation, whatever produced its first draft.</summary>
        public static void MarkConfirmed(TranslationUnit tu)
        {
            if (tu != null) tu.Origin = TranslationUnitOrigin.TM;
        }

        /// <summary>
        /// Reads the hash the segment carries for the main TM. Studio stores it
        /// as a decimal string; anything unreadable counts as none, which only
        /// costs an add where an overwrite was due.
        /// </summary>
        public static int ReadPreviousHash(ISegmentPair pair, out string raw)
        {
            raw = null;
            try { raw = pair?.Properties?.TranslationOrigin?.OriginalTranslationHash; }
            catch { raw = null; }
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? h : 0;
        }

        /// <summary>
        /// The main TMs Studio would update on confirm: cascade entries with
        /// Update ticked, from the language-specific cascade when it overrides
        /// the project's, otherwise from the project's. UI thread (project API).
        /// </summary>
        public static void FindMainTms(FileBasedProject project, Language target, Plan plan)
        {
            if (project == null)
            {
                plan.Problem = "the open document is not part of a file-based Trados project, so it has no main TM to update";
                return;
            }

            Sdl.ProjectAutomation.Core.TranslationProviderConfiguration cfg = null;
            try { if (target != null) cfg = project.GetTranslationProviderConfiguration(target); }
            catch { cfg = null; }
            if (cfg == null || !cfg.OverrideParent)
            {
                try { cfg = project.GetTranslationProviderConfiguration(); }
                catch (Exception ex)
                {
                    plan.Problem = "could not read the project's translation memory settings: " + ex.Message;
                    return;
                }
            }

            string projectDir = null;
            try { projectDir = Path.GetDirectoryName(project.FilePath); } catch { }

            foreach (var entry in cfg?.Entries ?? new List<Sdl.ProjectAutomation.Core.TranslationProviderCascadeEntry>())
            {
                if (entry == null || !entry.PerformUpdate) continue;
                var r = entry.MainTranslationProvider;
                if (r?.Uri == null || !r.Enabled) continue;

                var uri = r.Uri.OriginalString;
                if (FileBasedTranslationMemory.IsFileBasedTranslationMemory(r.Uri))
                {
                    string path = null;
                    try { path = FileBasedTranslationMemory.GetFileBasedTranslationMemoryFilePath(r.Uri); } catch { }
                    if (string.IsNullOrEmpty(path)) { plan.NotTms.Add(uri); continue; }
                    if (!Path.IsPathRooted(path) && projectDir != null) path = Path.Combine(projectDir, path);
                    plan.Tms.Add(new TmRef { Name = Path.GetFileNameWithoutExtension(path), FilePath = path });
                }
                else if (ServerTmClient.IsServerTmUri(uri))
                {
                    plan.Tms.Add(new TmRef { Name = TmSearcher.DisplayName(uri), ServerUri = uri });
                }
                else
                {
                    plan.NotTms.Add(uri);
                }
            }

            if (plan.Tms.Count == 0 && plan.Problem == null)
                plan.Problem = "the project has no main translation memory with Update ticked "
                             + "(Project Settings > Language Pairs > Translation Memory and Automated Translation)";

            try
            {
                var group = project.GetSettings(target)?.GetSettingsGroup("TranslationMemorySettings");
                if (group != null && group.GetSetting("ProjectSettings", out FieldValues fields))
                    plan.ProjectFields = fields;
            }
            catch { plan.ProjectFields = null; }
        }

        /// <summary>
        /// Writes every unit to every TM in the plan. Returns, per unit in plan
        /// order, one outcome per TM. Never throws: a TM that cannot be opened
        /// or refuses a call fails the units it was given, and the others carry
        /// on. Safe off the UI thread.
        /// </summary>
        public static List<Outcome>[] Write(Plan plan)
        {
            var outcomes = new List<Outcome>[plan.Units.Count];
            for (int i = 0; i < outcomes.Length; i++) outcomes[i] = new List<Outcome>();
            if (plan.Units.Count == 0 || plan.Tms.Count == 0) return outcomes;

            // The bridge serves requests on the thread pool, so an agent making
            // parallel update_segments calls would otherwise open the same .sdltm
            // twice and have SQLite refuse one writer. One write at a time.
            lock (WriteLock)
                return WriteLocked(plan, outcomes);
        }

        private static readonly object WriteLock = new object();

        private static List<Outcome>[] WriteLocked(Plan plan, List<Outcome>[] outcomes)
        {

            var calls = BuildCalls(plan.Units);
            var sw = Stopwatch.StartNew();
            int written = 0, failed = 0;
            try
            {
                foreach (var tm in plan.Tms)
                {
                    ITranslationProviderLanguageDirection ld = null;
                    string openError = null;
                    try { ld = Open(tm, plan.SourceCulture, plan.TargetCulture, out openError); }
                    catch (Exception ex) { openError = ex.Message; }

                    foreach (var call in calls)
                    {
                        if (ld == null)
                        {
                            foreach (var slot in call.Slots.Where(s => s.UnitIndex >= 0))
                                outcomes[slot.UnitIndex].Add(new Outcome { Tm = tm.Name, Error = "could not open this TM: " + openError });
                            failed += call.UnitCount;
                            continue;
                        }

                        ImportResult[] results;
                        try
                        {
                            results = ld.AddOrUpdateTranslationUnitsMasked(
                                call.Slots.Select(s => s.Tu).ToArray(),
                                call.Slots.Select(s => s.Hash).ToArray(),
                                NewSettings(plan.ProjectFields),
                                call.Slots.Select(s => s.UnitIndex >= 0).ToArray());
                        }
                        catch (Exception ex)
                        {
                            foreach (var slot in call.Slots.Where(s => s.UnitIndex >= 0))
                                outcomes[slot.UnitIndex].Add(new Outcome { Tm = tm.Name, Error = "the TM refused the write: " + ex.Message });
                            failed += call.UnitCount;
                            continue;
                        }

                        for (int k = 0; k < call.Slots.Count; k++)
                        {
                            var slot = call.Slots[k];
                            if (slot.UnitIndex < 0) continue;
                            var o = Describe(tm.Name, results != null && k < results.Length ? results[k] : null);
                            outcomes[slot.UnitIndex].Add(o);
                            if (o.Error == null) written++; else failed++;
                        }
                    }
                }
            }
            finally
            {
                // The authenticated-server cache is per run, as in the search paths.
                try { ServerTmClient.ResetCache(); } catch { }
            }

            sw.Stop();
            try
            {
                DiagnosticLog.WriteAlways(LogCategory, string.Format(CultureInfo.InvariantCulture,
                    "{0} segment(s) to {1} TM(s) in {2} call(s) each: {3} written, {4} failed, {5} ms.",
                    plan.Units.Count, plan.Tms.Count, calls.Count, written, failed, sw.ElapsedMilliseconds));
            }
            catch { }
            return outcomes;
        }

        /// <summary>
        /// The editor's import settings for a confirm (Studio's
        /// InteractiveTmUpdateImportSettingsBuilder). Fresh per call: the
        /// importer writes back into the settings object on some paths.
        /// </summary>
        private static ImportSettings NewSettings(FieldValues projectFields) => new ImportSettings
        {
            CheckMatchingSublanguages = false,
            IncrementUsageCount = true,
            IsDocumentImport = true,
            NewFields = ImportSettings.NewFieldsOption.Ignore,
            ProjectSettings = projectFields,
            OverrideTuUserIdWithCurrentContextUser = true,
            UseTmUserIdFromBilingualFile = false,
            ExistingTUsUpdateMode = ImportSettings.TUUpdateMode.OverwriteCurrent,
            ConfirmationLevels = TmLevels
        };

        private static Outcome Describe(string tm, ImportResult r)
        {
            if (r == null) return new Outcome { Tm = tm, Error = "the TM returned no result for this segment" };
            switch (r.Action)
            {
                case Sdl.LanguagePlatform.TranslationMemory.Action.Add:
                    return new Outcome { Tm = tm, Action = "added", Stamp = true };
                case Sdl.LanguagePlatform.TranslationMemory.Action.Overwrite:
                    return new Outcome { Tm = tm, Action = "updated", Stamp = true };
                case Sdl.LanguagePlatform.TranslationMemory.Action.Merge:
                    return new Outcome { Tm = tm, Action = "merged", Stamp = true };
                case Sdl.LanguagePlatform.TranslationMemory.Action.Discard:
                    return new Outcome { Tm = tm, Action = "unchanged" };
                case Sdl.LanguagePlatform.TranslationMemory.Action.Error:
                    return new Outcome { Tm = tm, Error = "the TM refused this segment (" + r.ErrorCode + ")" };
                default:
                    return new Outcome { Tm = tm, Action = r.Action.ToString().ToLowerInvariant(), Stamp = true };
            }
        }

        private sealed class Slot
        {
            public TranslationUnit Tu;
            public int Hash;
            /// <summary>Index into Plan.Units, or -1 for a masked context unit.</summary>
            public int UnitIndex;
            public string Key;
        }

        private sealed class Call
        {
            public List<Slot> Slots = new List<Slot>();
            public int UnitCount => Slots.Count(s => s.UnitIndex >= 0);
        }

        /// <summary>
        /// Lays the units out as the editor and the Update TM task do: each unit
        /// directly after its preceding segment, which is either the previous
        /// unit written (adjacent segments share the call) or a masked copy of
        /// it. A unit with no predecessor must be FIRST in its call - that is
        /// how the TM gives it the start-of-file context - so it opens a new one.
        /// The importer keeps each unit's predecessor across its own internal
        /// batch splits, so one call per run is enough.
        /// </summary>
        private static List<Call> BuildCalls(List<Unit> units)
        {
            var calls = new List<Call>();
            Call current = null;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u.ContextTu == null)
                {
                    current = new Call();
                    calls.Add(current);
                }
                else
                {
                    if (current == null)
                    {
                        current = new Call();
                        calls.Add(current);
                    }
                    var last = current.Slots.Count > 0 ? current.Slots[current.Slots.Count - 1] : null;
                    if (last == null || last.Key != u.ContextKey)
                        current.Slots.Add(new Slot { Tu = u.ContextTu, Hash = 0, UnitIndex = -1, Key = u.ContextKey });
                }
                current.Slots.Add(new Slot { Tu = u.Tu, Hash = u.PreviousHash, UnitIndex = i, Key = u.Key });
            }
            return calls;
        }

        /// <summary>Opens one TM's direction for the document's language pair.</summary>
        private static ITranslationProviderLanguageDirection Open(
            TmRef tm, CultureInfo source, CultureInfo target, out string error)
        {
            error = null;
            if (tm.FilePath != null)
            {
                if (!File.Exists(tm.FilePath)) { error = "the file is missing: " + tm.FilePath; return null; }
                return new FileBasedTranslationMemory(tm.FilePath).LanguageDirection;
            }

            if (!ServerTmClient.TryParseServerTmUri(tm.ServerUri, out var sref))
            {
                error = "unrecognised server TM address";
                return null;
            }
            var directions = ServerTmClient.OpenLanguageDirections(sref).ToList();
            if (directions.Count == 0)
            {
                error = "the server TM could not be opened (not signed in to the server in Studio?)";
                return null;
            }

            // A server TM can hold several directions; writing into the wrong one
            // would put the translation where no lookup will find it.
            var exact = directions.FirstOrDefault(d =>
                SameCulture(d.SourceLanguage, source, exact: true) && SameCulture(d.TargetLanguage, target, exact: true));
            var loose = exact ?? directions.FirstOrDefault(d =>
                SameCulture(d.SourceLanguage, source, exact: false) && SameCulture(d.TargetLanguage, target, exact: false));
            if (loose == null)
                error = "the server TM has no " + source?.Name + " to " + target?.Name + " direction";
            return loose;
        }

        private static bool SameCulture(CultureCode code, CultureInfo culture, bool exact)
        {
            if (code == null || culture == null) return false;
            var name = code.Name ?? "";
            if (exact) return string.Equals(name, culture.Name, StringComparison.OrdinalIgnoreCase);
            var dash = name.IndexOf('-');
            var lang = dash > 0 ? name.Substring(0, dash) : name;
            return string.Equals(lang, culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase);
        }
    }
}

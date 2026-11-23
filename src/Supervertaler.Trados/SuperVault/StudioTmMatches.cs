using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Core.SuperVault;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.SuperVault
{
    /// <summary>
    /// EverTM Matches, Options &gt; "Also show matches from the project's Studio TMs" (Michael, 23 Nov 2026; plan in
    /// the orchestrator's notes, evertm-studio-tms-plan.md): the project's own Studio TMs are searched for the active
    /// segment too, and their matches join EverTM's in the pane, marked with the TM's name.
    ///
    /// <para><b>Which TMs.</b> Those Studio would look up in: the language pair's TM list, Enabled with Lookup ticked,
    /// minus EverTM (<see cref="TmSearcher.FindLookupTms(string,string,string)"/>). File TMs and GroupShare TMs.</para>
    ///
    /// <para><b>The search is Studio's.</b> <c>SearchSegment</c> on each TM, with the segment as Studio's own converter
    /// makes it (<see cref="EverTmEditorSegment"/>), Studio's default formatting and multiple-translation penalties, and
    /// the project's penalty for that TM, so a match scores as it does in Translation Results. The unit is then put in
    /// EverTM's shape (<see cref="VaultLookup.Search"/> with the unit as its only candidate), so its differences, its tags
    /// and inserting work exactly as for EverTM's matches; the score stays Studio's, except that a 100% whose source is
    /// not your segment's text is shown as 99%, as everywhere else in Supervertaler (13 Nov 2026).</para>
    ///
    /// <para><b>Time.</b> The TMs are searched at once, each on a task of its own, and waited for 5 s in all. A TM
    /// that isn't back by then is left out of this segment (the status line says so) and is not searched again until
    /// its search has finished; a TM that fails or runs out of time twice is skipped for the rest of the document.
    /// EverTM's matches never wait for any of this: they are shown first, and these are merged in when they come.</para>
    ///
    /// <para><b>State.</b> One <see cref="Session"/> per document: the TM list, the opened TMs (kept open while the
    /// document is active, released when another document is) and the failure counts. Used only by the pane's Studio TM
    /// worker, one search at a time; the open TMs are guarded by the session's lock because a search that ran out of
    /// time may still be finishing on its task.</para>
    /// </summary>
    public static class StudioTmMatches
    {
        /// <summary>How long a segment waits for the Studio TMs in all. Settable for the checks only.</summary>
        public static int TimeoutMs = 5000;
        public const int FailuresBeforeSkipping = 2;

        /// <summary>One of the project's Studio TMs.</summary>
        public sealed class Tm
        {
            /// <summary>The .sdltm's full path, or the GroupShare TM's URI.</summary>
            public string Entry;
            /// <summary>"Hydraulics_TM", "Legal (GroupShare)".</summary>
            public string Name;
            /// <summary>The project's penalty for this TM (Project Settings, the TM list's Penalty column).</summary>
            public int Penalty;
        }

        public sealed class Hit
        {
            public VaultMatch Match;
            public Tm Tm;
        }

        public sealed class Request
        {
            /// <summary>The document and the segment ("file|paragraph/segment"): which answer is which.</summary>
            public string Key;
            /// <summary>The document's file, to find its project's TM list.</summary>
            public string DocumentFile;
            /// <summary>The segment as Studio's TM search takes it (a copy per search is made from it).</summary>
            public Segment Segment;
            public List<VaultPiece> Document;
            public string SourceLang, TargetLang, SourceCulture, TargetCulture;
            public int MinScore, MaxMatches;
            /// <summary>For tests: TMs to search instead of the project's.</summary>
            public List<Tm> Tms;
        }

        public sealed class Answer
        {
            public string Key;
            public int MinScore, MaxMatches;
            public List<Hit> Hits = new List<Hit>();
            /// <summary>How many Studio TMs the project looks up in for this language pair (EverTM not counted).</summary>
            public int Tms;
            /// <summary>The TMs left out of this segment, and why: "Hydraulics_TM (took over 5 s)".</summary>
            public List<string> LeftOut = new List<string>();
            public long Milliseconds;
        }

        /// <summary>The open TMs for one document. See the class's remarks on threads.</summary>
        public sealed class Session
        {
            internal string Document;
            internal List<Tm> Tms = new List<Tm>();
            internal readonly object Gate = new object();
            internal readonly Dictionary<string, ITranslationProviderLanguageDirection> Open = new Dictionary<string, ITranslationProviderLanguageDirection>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, int> Failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, Task> Running = new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
            internal ServerTmClient.Connections Connections = new ServerTmClient.Connections();

            /// <summary>How many TMs are open (for the checks).</summary>
            public int OpenCount { get { lock (Gate) return Open.Count; } }

            void Reset(string document)
            {
                lock (Gate)
                {
                    Document = document;
                    Tms = new List<Tm>();
                    Open.Clear();          // released: nothing else holds them
                    Failures.Clear();
                    Running.Clear();       // a search still finishing keeps its own TM and is forgotten
                    Connections = new ServerTmClient.Connections();
                }
            }

            /// <summary>The project file the TM list was read from, and when it was last saved then. A later save (Project
            /// Settings changed: a TM's Enabled, Lookup or penalty) has the list read again at the next segment, without
            /// reopening the document (Michael, 23 Nov 2026). One file-date read per segment.</summary>
            internal string ProjectFile;
            internal DateTime ProjectSaved;

            internal void For(Request r)
            {
                string document = (r.DocumentFile ?? "") + "|" + r.SourceCulture + "|" + r.TargetCulture
                                  + (r.Tms == null ? "" : "|" + string.Join("|", r.Tms.Select(t => t.Entry)));
                if (document != Document)
                {
                    Reset(document);
                    if (r.Tms != null) { Tms = r.Tms; return; }
                    ProjectFile = TmSearcher.ProjectFileOf(r.DocumentFile);
                    ReadList(r);
                    return;
                }
                if (r.Tms != null || ProjectFile == null) return;
                DateTime saved;
                try { saved = File.GetLastWriteTimeUtc(ProjectFile); } catch { return; }
                if (saved != ProjectSaved) ReadList(r);
            }

            /// <summary>
            /// Reads the project's TM list for the document's pair. A TM still listed keeps its open handle and its
            /// failure count; one no longer listed (removed, switched off, Lookup unticked) is let go.
            /// </summary>
            void ReadList(Request r)
            {
                try { ProjectSaved = ProjectFile == null ? default : File.GetLastWriteTimeUtc(ProjectFile); } catch { }
                var found = ProjectFile == null ? new TmSearcher.LookupTms()
                    : TmSearcher.FindLookupTms(ProjectFile, r.SourceCulture, r.TargetCulture);   // never throws
                var tms = found.Tms.Select(e => new Tm
                {
                    Entry = e,
                    Name = PaneName(e),
                    Penalty = found.Penalties.TryGetValue(e, out int p) ? p : 0,
                }).ToList();
                var keep = new HashSet<string>(tms.Select(t => t.Entry), StringComparer.OrdinalIgnoreCase);
                lock (Gate)
                {
                    Tms = tms;
                    foreach (var gone in Open.Keys.Where(k => !keep.Contains(k)).ToList()) Open.Remove(gone);
                    foreach (var gone in Failures.Keys.Where(k => !keep.Contains(k)).ToList()) Failures.Remove(gone);
                    foreach (var gone in Running.Keys.Where(k => !keep.Contains(k)).ToList()) Running.Remove(gone);
                }
            }
        }

        /// <summary>
        /// Searches the project's Studio TMs for the segment. On the pane's Studio TM worker; never throws for a TM that
        /// can't be opened or searched (it is left out and said so). At most <see cref="TimeoutMs"/> in all.
        /// </summary>
        public static Answer Search(Session session, Request request)
        {
            var sw = Stopwatch.StartNew();
            var answer = new Answer { Key = request.Key, MinScore = request.MinScore, MaxMatches = request.MaxMatches };
            if (session == null || request?.Segment == null || request.Document == null || request.Document.Count == 0) return answer;
            session.For(request);
            answer.Tms = session.Tms.Count;

            var searches = new List<(Tm tm, Task<List<Hit>> task)>();
            foreach (var tm in session.Tms)
            {
                lock (session.Gate)
                {
                    if (session.Failures.TryGetValue(tm.Entry, out int failed) && failed >= FailuresBeforeSkipping)
                    {
                        answer.LeftOut.Add(tm.Name + " (skipped in this document: it failed or was slow twice)");
                        continue;
                    }
                    if (session.Running.TryGetValue(tm.Entry, out var still) && !still.IsCompleted)
                    {
                        answer.LeftOut.Add(tm.Name + " (still busy with an earlier segment)");
                        continue;
                    }
                }
                Segment copy;
                try { copy = request.Segment.Duplicate(); }
                catch { copy = request.Segment; }
                var one = tm;
                var task = Task.Run(() => SearchOne(session, one, copy, request));
                lock (session.Gate) session.Running[tm.Entry] = task;
                searches.Add((tm, task));
            }

            foreach (var (tm, task) in searches)
            {
                int left = (int)Math.Max(0, TimeoutMs - sw.ElapsedMilliseconds);
                bool done;
                try { done = task.Wait(left); }
                catch { done = true; }   // faulted: read below
                if (done && task.Status == TaskStatus.RanToCompletion)
                {
                    answer.Hits.AddRange(task.Result);
                    continue;
                }
                string why = done ? "failed: " + (task.Exception?.GetBaseException().Message ?? "unknown error") : "took over " + TimeoutMs / 1000 + " s";
                answer.LeftOut.Add(tm.Name + " (" + why + ")");
                lock (session.Gate) session.Failures[tm.Entry] = (session.Failures.TryGetValue(tm.Entry, out int f) ? f : 0) + 1;
                try { DiagnosticLog.WriteAlways("EverTM", "EverTM Matches: Studio TM " + tm.Name + " " + why + (done ? ": " + task.Exception?.GetBaseException() : "")); } catch { }
            }
            answer.Milliseconds = sw.ElapsedMilliseconds;
            return answer;
        }

        /// <summary>One TM: opened if it isn't yet, searched, its results put in EverTM's shape. On a task of its own.</summary>
        static List<Hit> SearchOne(Session session, Tm tm, Segment segment, Request request)
        {
            ITranslationProviderLanguageDirection ld;
            lock (session.Gate)
            {
                if (!session.Open.TryGetValue(tm.Entry, out ld))
                {
                    ld = OpenDirection(tm.Entry, request.SourceCulture, request.TargetCulture, session.Connections);
                    if (ld == null) throw new InvalidOperationException("it can't be opened for this language pair");
                    session.Open[tm.Entry] = ld;
                }
            }

            int min = Math.Max(1, request.MinScore);
            var settings = new SearchSettings
            {
                Mode = SearchMode.NormalSearch,     // what the editor's lookup uses (see TmFuzzyLookup.SearchOne)
                MinScore = min,
                MaxResults = Math.Max(1, request.MaxMatches),
            };
            // Studio's defaults for a project's penalties (Project Settings > Language Pairs > Translation Memory >
            // Penalties), and the project's penalty for this TM.
            settings.AddPenalty(PenaltyType.TagMismatch, 1);
            settings.AddPenalty(PenaltyType.MemoryTagsDeleted, 1);
            settings.AddPenalty(PenaltyType.MultipleTranslations, 1);
            if (tm.Penalty > 0) settings.AddPenalty(PenaltyType.ProviderPenalty, tm.Penalty);

            var results = ld.SearchSegment(settings, segment);
            var hits = new List<Hit>();
            if (results?.Results == null) return hits;
            foreach (var r in results.Results)
            {
                var unit = r?.MemoryTranslationUnit;
                if (unit?.SourceSegment == null || unit.TargetSegment == null || r.ScoringResult == null) continue;
                if (tm.Penalty > 0 && r.ScoringResult.FindPenalty(PenaltyType.ProviderPenalty) == null)
                    r.ScoringResult.ApplyPenalty(new Penalty(PenaltyType.ProviderPenalty, tm.Penalty));
                var m = Shape(unit, r.ScoringResult.Match, tm, request);
                if (m != null && m.Score >= min) hits.Add(new Hit { Match = m, Tm = tm });
            }
            return hits;
        }

        /// <summary>
        /// A Studio TM unit as a match of EverTM's shape: the stored texts as EverTM's codes (as an import stores them),
        /// scored against the segment by EverTM's alignment for the differences and tag placement, with Studio's
        /// <paramref name="score"/>. Null when the unit has no text. Public for the checks.
        /// </summary>
        public static VaultMatch Shape(TranslationUnit unit, int score, Tm tm, Request request)
        {
            var info = SuperVaultImport.Info(unit, tm?.Name ?? "");
            var candidate = new VaultCandidate
            {
                SourceRaw = SuperVaultImport.Codes(unit.SourceSegment),
                TargetRaw = SuperVaultImport.Codes(unit.TargetSegment),
                Variant = (unit.SourceSegment.Culture?.Name ?? request.SourceCulture) + ">" + (unit.TargetSegment.Culture?.Name ?? request.TargetCulture),
                Exact = true,   // Search keeps it only when the source's text is the segment's
                Fields = info.Fields.Where(f => f.Field != VaultFields.Source).ToList(),   // the TM's name is shown apart
                Created = info.Created, Changed = info.Changed, CreatedBy = info.CreatedBy, ChangedBy = info.ChangedBy,
            };
            if (VaultMarkup.Plain(candidate.SourceRaw).Trim().Length == 0 || VaultMarkup.Plain(candidate.TargetRaw).Trim().Length == 0) return null;
            var m = VaultLookup.Search(null, request.Document, request.SourceLang, request.TargetLang, 0, 1, new[] { candidate }, request.TargetCulture).FirstOrDefault();
            if (m == null) return null;
            m.Score = Math.Max(0, Math.Min(100, score));
            if (m.Score >= 100 && !candidate.Exact) m.Score = 99;   // 100 only for your segment's own text
            return m;
        }

        /// <summary>A TM's language direction for the document's pair; null when it has none.</summary>
        static ITranslationProviderLanguageDirection OpenDirection(string entry, string sourceCulture, string targetCulture, ServerTmClient.Connections connections)
        {
            if (ServerTmClient.IsServerTmUri(entry))
            {
                if (!ServerTmClient.TryParseServerTmUri(entry, out var sref)) return null;
                var lds = ServerTmClient.OpenLanguageDirections(sref, connections).ToList();
                return Best(lds, sourceCulture, targetCulture);
            }
            return new FileBasedTranslationMemory(entry).LanguageDirection;
        }

        static ITranslationProviderLanguageDirection Best(List<ITranslationMemoryLanguageDirection> lds, string source, string target)
        {
            bool Same(CultureInfo c, string name, bool baseOnly) =>
                c != null && name != null && (baseOnly
                    ? string.Equals(VaultMarkup.BaseLanguage(c.Name), VaultMarkup.BaseLanguage(name), StringComparison.OrdinalIgnoreCase)
                    : string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            return lds.FirstOrDefault(l => Same(l.SourceLanguage, source, false) && Same(l.TargetLanguage, target, false))
                ?? lds.FirstOrDefault(l => Same(l.SourceLanguage, source, true) && Same(l.TargetLanguage, target, true));
        }

        /// <summary>
        /// A Studio TM's name as the pane shows it (Michael, 23 Nov 2026): a file TM with its ".sdltm", so it reads as a
        /// Trados memory at a glance ("BRANTS (HELLO-003+004-BE-EP).sdltm"); a GroupShare TM, which has no file, as
        /// "Legal (GroupShare)". Pane-only: TmSearcher.DisplayName stays as other features use it.
        /// </summary>
        public static string PaneName(string entry)
        {
            if (string.IsNullOrEmpty(entry)) return "";
            if (ServerTmClient.IsServerTmUri(entry)) return TmSearcher.DisplayName(entry);
            try { return Path.GetFileName(entry); }
            catch { return entry; }
        }

        // ---- merging ----

        public sealed class Merged
        {
            public List<VaultMatch> Matches = new List<VaultMatch>();
            /// <summary>The rows that are a Studio TM's (by reference), and which TM's.</summary>
            public Dictionary<VaultMatch, Tm> FromStudio = new Dictionary<VaultMatch, Tm>();
            /// <summary>A row that some Studio TM has too (the same source and target text): those TMs' names.</summary>
            public Dictionary<VaultMatch, List<string>> AlsoIn = new Dictionary<VaultMatch, List<string>>();
        }

        /// <summary>
        /// EverTM's matches and the Studio TMs', as the pane shows them (Michael, 23 Nov 2026): by score, EverTM's first on
        /// a tie; the same translation (the same source and target as plain text) once, as EverTM's when EverTM has it
        /// (so it can be edited, removed, and its older translations seen), with "also in" the Studio TMs that have it
        /// too; at most <paramref name="max"/> in all.
        /// </summary>
        public static Merged Merge(List<VaultMatch> everTm, IEnumerable<Hit> studio, int max)
        {
            var merged = new Merged();
            var rows = new List<(VaultMatch m, int kind, int order)>();
            var byText = new Dictionary<string, VaultMatch>(StringComparer.Ordinal);
            int order = 0;
            foreach (var m in everTm ?? new List<VaultMatch>())
            {
                rows.Add((m, 0, order++));
                var key = TextKey(m);
                if (!byText.ContainsKey(key)) byText[key] = m;
            }
            foreach (var h in (studio ?? Enumerable.Empty<Hit>()).Where(h => h?.Match != null).OrderByDescending(h => h.Match.Score))
            {
                var key = TextKey(h.Match);
                if (byText.TryGetValue(key, out var had))
                {
                    string ownName = merged.FromStudio.TryGetValue(had, out var own) ? own.Name : null;   // null: EverTM's
                    if (h.Tm.Name != ownName)
                    {
                        if (!merged.AlsoIn.TryGetValue(had, out var names)) merged.AlsoIn[had] = names = new List<string>();
                        if (!names.Contains(h.Tm.Name)) names.Add(h.Tm.Name);
                    }
                    continue;
                }
                byText[key] = h.Match;
                merged.FromStudio[h.Match] = h.Tm;
                rows.Add((h.Match, 1, order++));
            }
            merged.Matches = rows.OrderByDescending(r => r.m.Score).ThenBy(r => r.kind).ThenBy(r => r.order)
                                 .Take(Math.Max(1, max)).Select(r => r.m).ToList();
            var shown = new HashSet<VaultMatch>(merged.Matches);
            foreach (var gone in merged.FromStudio.Keys.Where(k => !shown.Contains(k)).ToList()) merged.FromStudio.Remove(gone);
            foreach (var gone in merged.AlsoIn.Keys.Where(k => !shown.Contains(k)).ToList()) merged.AlsoIn.Remove(gone);
            return merged;
        }

        static string TextKey(VaultMatch m) =>
            VaultLookup.PlainOf(m.Source ?? new List<VaultPiece>()) + "\n" + VaultLookup.PlainOf(m.Target ?? new List<VaultPiece>());
    }
}

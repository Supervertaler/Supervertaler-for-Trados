using System;
using System.Collections.Generic;
using System.Linq;
using Sdl.Core.Globalization;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Core.SuperVault;

namespace Supervertaler.Trados.SuperVault
{
    /// <summary>
    /// SuperVault as a Trados Studio translation memory: one memory for every project in a language pair it holds, in both directions and every language variant. nl-BE>en-US,
    /// nl-NL>en-GB and en-GB>nl-BE all use the same units; there are no reversed copies and nothing to add per variant.
    ///
    /// SuperVault has its own memory (VaultStore), filled by importing TMX files and Studio TMs; it needs no other program.
    /// It also learns as you work: with its Update box ticked in the project's TM settings, every confirmed segment (and
    /// a batch "Update Main Translation Memories") is stored in it, and found by lookups within seconds.
    /// </summary>
    [TranslationProviderFactory(Id = "SupervertalerSuperVaultFactory", Name = "SupervertalerSuperVaultFactory", Description = "SupervertalerSuperVaultFactory_Description")]
    public sealed class SuperVaultProviderFactory : ITranslationProviderFactory
    {
        public bool SupportsTranslationProviderUri(Uri uri) => SuperVaultProvider.IsOurs(uri);
        public ITranslationProvider CreateTranslationProvider(Uri uri, string state, ITranslationProviderCredentialStore store) => new SuperVaultProvider(null, uri);
        public TranslationProviderInfo GetTranslationProviderInfo(Uri uri, string state) =>
            new TranslationProviderInfo { Name = SuperVaultProvider.DisplayName, TranslationMethod = TranslationMethod.TranslationMemory };
    }

    public sealed class SuperVaultProvider : ITranslationProvider
    {
        public const string Scheme = "evertm";
        /// <summary>The scheme before the rename (it was called SuperVault). Projects saved then keep it and still open.</summary>
        public const string OldScheme = "supervault";
        public const string DisplayName = "EverTM";
        public static readonly Uri DefaultUri = new Uri("evertm:///");

        public static bool IsOurs(Uri uri) => uri != null && (uri.Scheme == Scheme || uri.Scheme == OldScheme);

        internal VaultLibrary Library { get; }
        readonly Uri _uri;

        /// <summary>A new EverTM in a project: the user's folder, the new scheme.</summary>
        public SuperVaultProvider() : this(null, DefaultUri) { }

        /// <summary>For tests: a provider on a scratch index.</summary>
        public SuperVaultProvider(string root) : this(root, DefaultUri) { }

        /// <summary>
        /// <paramref name="root"/> null means the user's folder (moved from <c>supervault</c> to <c>evertm</c> the first
        /// time). <see cref="Uri"/> stays the one the project saved, old scheme or new, so Studio finds the same entry.
        /// </summary>
        public SuperVaultProvider(string root, Uri uri)
        {
            if (VaultLibrary.Log == null)
                VaultLibrary.Log = message => Supervertaler.Trados.Core.DiagnosticLog.WriteAlways("EverTM", message);
            if (VaultLibrary.ProjectOf == null) VaultLibrary.ProjectOf = StudioProjects.Describe;   // on the learning thread
            Library = VaultLibrary.For(root ?? VaultLibrary.DefaultRootInUse);   // the log is set first: the move logs what it did
            _uri = IsOurs(uri) ? uri : DefaultUri;
        }

        /// <summary>
        /// Every pair of two different languages, either way round, whatever the variants: SuperVault learns a pair it
        /// doesn't hold yet from the first segment you confirm in it. (Stage 1 offered only the pairs the index held.)
        /// </summary>
        public bool SupportsLanguageDirection(LanguagePair pair)
        {
            string a = VaultMarkup.BaseLanguage(pair?.SourceCultureName), b = VaultMarkup.BaseLanguage(pair?.TargetCultureName);
            return a.Length > 0 && b.Length > 0 && a != b;
        }

        public ITranslationProviderLanguageDirection GetLanguageDirection(LanguagePair pair)
        {
            if (!SupportsLanguageDirection(pair))
                throw new NotSupportedException($"EverTM holds no {pair?.SourceCultureName} / {pair?.TargetCultureName} units.");
            return new SuperVaultLanguageDirection(this, pair);
        }

        /// <summary>
        /// Available unless the index is damaged. An empty SuperVault (no folder, a store with no index yet, or an index
        /// with no units) is a normal state: lookups give no matches and confirmed segments are learned, which creates
        /// the store and index. Studio's Editor disables a TM that reports itself unavailable when a file is opened, and
        /// saves that in the project, so this must never say "unavailable" just because SuperVault holds nothing yet.
        /// Never throws: Studio's Editor reads this without catching.
        /// </summary>
        public ProviderStatusInfo StatusInfo
        {
            get
            {
                try
                {
                    var s = Library.Status;
                    if (s.State == VaultState.Ready) return new ProviderStatusInfo(true, "EverTM: " + s.Message + (s.Building ? " (working: " + s.BuildProgress + ")" : ""));
                    if (s.Building) return new ProviderStatusInfo(true, "EverTM is busy (" + s.BuildProgress + ")");
                    if (s.State == VaultState.Missing) return new ProviderStatusInfo(true, s.Message);
                    return new ProviderStatusInfo(false, s.Message + " To do so, select EverTM and click Settings...");
                }
                catch (Exception ex)
                {
                    try { Supervertaler.Trados.Core.DiagnosticLog.WriteAlways("EverTM", "Status failed: " + ex); } catch { }
                    return new ProviderStatusInfo(true, "EverTM (status unknown: " + ex.Message + ")");
                }
            }
        }

        public void RefreshStatusInfo() { }
        public string SerializeState() => "";
        public void LoadState(string translationProviderState) { }

        public Uri Uri => _uri;
        public string Name => DisplayName;

        public bool SupportsTaggedInput => true;
        public bool SupportsScoring => true;
        public bool SupportsSearchForTranslationUnits => true;
        public bool SupportsMultipleResults => true;
        public bool SupportsFilters => false;
        public bool SupportsPenalties => true;
        public bool SupportsStructureContext => false;
        public bool SupportsDocumentSearches => true;
        public bool SupportsUpdate => true;
        public bool SupportsPlaceables => false;
        public bool SupportsTranslation => true;
        public bool SupportsFuzzySearch => true;
        public bool SupportsConcordanceSearch => true;
        public bool SupportsSourceConcordanceSearch => true;
        public bool SupportsTargetConcordanceSearch => true;
        public bool SupportsWordCounts => false;
        public TranslationMethod TranslationMethod => TranslationMethod.TranslationMemory;
        public bool IsReadOnly => false;
    }

    public sealed class SuperVaultLanguageDirection : ITranslationProviderLanguageDirection
    {
        readonly SuperVaultProvider _provider;
        readonly string _source, _target;   // base languages

        internal SuperVaultLanguageDirection(SuperVaultProvider provider, LanguagePair pair)
        {
            _provider = provider;
            SourceLanguage = pair.SourceCulture;
            TargetLanguage = pair.TargetCulture;
            _source = VaultMarkup.BaseLanguage(pair.SourceCultureName);
            _target = VaultMarkup.BaseLanguage(pair.TargetCultureName);
        }

        public ITranslationProvider TranslationProvider => _provider;
        public CultureCode SourceLanguage { get; }
        public CultureCode TargetLanguage { get; }
        public bool CanReverseLanguageDirection => true;

        public SearchResults SearchSegment(SearchSettings settings, Segment segment)
        {
            var results = new SearchResults { SourceSegment = segment?.Duplicate() };
            if (segment == null || settings == null) return results;
            try
            {
                int max = Math.Max(1, settings.MaxResults);

                if (settings.Mode == SearchMode.ConcordanceSearch || settings.Mode == SearchMode.TargetConcordanceSearch)
                {
                    var idx = _provider.Library.Current;
                    if (idx == null) return results;
                    bool onTarget = settings.Mode == SearchMode.TargetConcordanceSearch;
                    var query = VaultLookup.PlainOf(SuperVaultResults.Pieces(segment));
                    var otherCulture = (onTarget ? SourceLanguage : TargetLanguage)?.Name;
                    foreach (var m in VaultLookup.Concordance(idx, query, onTarget ? _target : _source, onTarget ? _source : _target, settings.MinScore, max, otherCulture))
                    {
                        // Concordance matches come back oriented search language -> other; a TU is always source -> target.
                        var searched = Plain(onTarget ? TargetLanguage : SourceLanguage, m.Source);
                        var other = Plain(onTarget ? SourceLanguage : TargetLanguage, m.Target);
                        results.Add(SuperVaultResults.Concordance(onTarget ? other : searched, onTarget ? searched : other, m));
                    }
                    return results;
                }

                var doc = SuperVaultResults.Pieces(results.SourceSegment);
                // Library.Lookup also sees the units just handed over and not yet stored: Studio looks a segment up
                // again right after confirming it and caches the result, so the new unit must be there at once.
                // The project's exact target variant (en-GB) first, then the newest (docs/design/evertm-metadata.md, 3b).
                var matches = _provider.Library.Lookup(doc, _source, _target, settings.MinScore, max, TargetLanguage?.Name);
                if (matches.Count == 0) return results;
                // Our tokens on the document copy: Studio pairs each provider's results with that provider's copy of the
                // document segment when it draws the differences, so these never meet another TM's tokens.
                results.SourceSegment.Tokens = SuperVaultResults.ToTokens(matches[0].DocumentTokens, results.SourceSegment);
                foreach (var m in matches)
                    results.Add(SuperVaultResults.Lookup(m, results.SourceSegment, results.SourceSegment, SourceLanguage, TargetLanguage, settings));
                return results;
            }
            catch (Exception ex)
            {
                // A lookup must never take Studio's lookup, Analyze or Pre-translate down with it.
                try { Supervertaler.Trados.Core.DiagnosticLog.WriteAlways("EverTM", "Lookup failed: " + ex); } catch { }
                return results;
            }
        }

        static Segment Plain(CultureCode culture, List<VaultPiece> pieces)
        {
            var s = new Segment(culture);
            s.Add(string.Concat(pieces.Where(p => !p.IsTag).Select(p => p.Text)));
            return s;
        }

        public SearchResults[] SearchSegments(SearchSettings settings, Segment[] segments) =>
            segments.Select(s => SearchSegment(settings, s)).ToArray();

        public SearchResults[] SearchSegmentsMasked(SearchSettings settings, Segment[] segments, bool[] mask) =>
            segments.Select((s, i) => mask == null || mask[i] ? SearchSegment(settings, s) : null).ToArray();

        public SearchResults SearchText(SearchSettings settings, string segment)
        {
            var s = new Segment(SourceLanguage);
            s.Add(segment ?? "");
            return SearchSegment(settings, s);
        }

        public SearchResults SearchTranslationUnit(SearchSettings settings, TranslationUnit translationUnit) =>
            SearchSegment(settings, translationUnit?.SourceSegment);

        public SearchResults[] SearchTranslationUnits(SearchSettings settings, TranslationUnit[] translationUnits) =>
            translationUnits.Select(t => SearchTranslationUnit(settings, t)).ToArray();

        public SearchResults[] SearchTranslationUnitsMasked(SearchSettings settings, TranslationUnit[] translationUnits, bool[] mask) =>
            translationUnits.Select((t, i) => mask == null || mask[i] ? SearchTranslationUnit(settings, t) : null).ToArray();

        // ---- updates: learning as you work ----
        //
        // Which of these Studio calls (read from Studio 2022, 2024 and 2026's own assemblies, 8 Nov 2026):
        //   - confirming a segment in the Editor: AddOrUpdateTranslationUnitsMasked (Editor TranslationUpdate.ProcessBuffer);
        //   - batch task "Update Main Translation Memories": AddOrUpdateTranslationUnitsMasked (AbstractUpdateTmContentProcessor);
        //   - editing a TM unit from the Translation Results / Concordance window: UpdateTranslationUnits.
        // All of them go the same way: the units are handed to SuperVault's background thread and the call returns at
        // once. Studio's previous-translation hashes are not used: SuperVault recognises a changed translation of the
        // same source itself (VaultStore.Writer.Learn).

        static ImportResult Added => new ImportResult(Sdl.LanguagePlatform.TranslationMemory.Action.Add);         // a new object each time: Studio may fill in its fields
        static ImportResult Discarded => new ImportResult(Sdl.LanguagePlatform.TranslationMemory.Action.Discard);

        /// <summary>
        /// Hands the units to SuperVault (<see cref="VaultLibrary.Learn"/>) and returns. Never throws and never waits:
        /// Studio's confirm action must not fail or hang because of SuperVault. A unit Studio masked out gets null, as
        /// for searches. The result can only say "added": the unit is stored a moment later, in the background.
        /// </summary>
        ImportResult[] Learn(TranslationUnit[] units, bool[] mask, ImportSettings settings = null)
        {
            var results = new ImportResult[units?.Length ?? 0];
            try
            {
                var batch = new List<VaultLearnUnit>(results.Length);
                var subject = Subject(settings);
                for (int i = 0; i < results.Length; i++)
                {
                    if (mask != null && (i >= mask.Length || !mask[i])) continue;
                    var tu = units[i];
                    if (tu?.SourceSegment == null || tu.TargetSegment == null || tu.TargetSegment.IsEmpty) { results[i] = Discarded; continue; }
                    batch.Add(new VaultLearnUnit
                    {
                        SourceCulture = tu.SourceSegment.Culture?.Name ?? SourceLanguage?.Name,
                        Source = SuperVaultImport.Codes(tu.SourceSegment),
                        TargetCulture = tu.TargetSegment.Culture?.Name ?? TargetLanguage?.Name,
                        Target = SuperVaultImport.Codes(tu.TargetSegment),
                        Origin = "studio",
                        // Who and where (only in-memory reads here, on Studio's UI thread; the project's name and
                        // customer are read from its .sdlproj on EverTM's learning thread, from DocumentPath).
                        User = User(tu),
                        Subject = subject,
                        Document = DocumentName(tu),
                        DocumentPath = DocumentPath(tu),
                    });
                    results[i] = Added;
                }
                _provider.Library.Learn(batch);
            }
            catch (Exception ex)
            {
                try { Supervertaler.Trados.Core.DiagnosticLog.WriteAlways("EverTM", "Could not take confirmed segments: " + ex); } catch { }
                for (int i = 0; i < results.Length; i++) if (results[i] == null && (mask == null || (i < mask.Length && mask[i]))) results[i] = Discarded;
            }
            return results;
        }

        /// <summary>The Studio user Studio put on the unit (it sets the current user on confirm), else Windows' user name.</summary>
        static string User(TranslationUnit tu)
        {
            var u = tu.SystemFields?.ChangeUser;
            if (string.IsNullOrWhiteSpace(u)) u = tu.SystemFields?.CreationUser;
            return string.IsNullOrWhiteSpace(u) ? Environment.UserName : u;
        }

        /// <summary>The original document's file name ("manual.docx"), else the bilingual file's.</summary>
        static string DocumentName(TranslationUnit tu)
        {
            try
            {
                var original = tu.FileProperties?.FileConversionProperties?.OriginalFilePath;
                var path = !string.IsNullOrEmpty(original) ? original : DocumentPath(tu);
                return string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFileName(path);
            }
            catch { return null; }
        }

        static string DocumentPath(TranslationUnit tu)
        {
            try { var p = tu.DocumentProperties?.LastOpenedAsPath; return string.IsNullOrEmpty(p) ? null : p; }
            catch { return null; }
        }

        /// <summary>The project's "field values on update" (Project Settings > Translation Memory > Update) named Subject or Domain, if any.</summary>
        static string Subject(ImportSettings settings)
        {
            try
            {
                if (settings?.ProjectSettings == null) return null;
                var values = new List<string>();
                foreach (var fv in settings.ProjectSettings)
                {
                    if (VaultFields.Canonical(fv?.Name) != VaultFields.Subject) continue;
                    values.AddRange(SuperVaultImport.Values(fv));
                }
                return values.Count == 0 ? null : string.Join(", ", values);
            }
            catch { return null; }
        }

        public ImportResult AddTranslationUnit(TranslationUnit translationUnit, ImportSettings settings) => Learn(new[] { translationUnit }, null, settings)[0];
        public ImportResult[] AddTranslationUnits(TranslationUnit[] translationUnits, ImportSettings settings) => Learn(translationUnits, null, settings);
        public ImportResult[] AddOrUpdateTranslationUnits(TranslationUnit[] translationUnits, int[] previousTranslationHashes, ImportSettings settings) => Learn(translationUnits, null, settings);
        public ImportResult[] AddTranslationUnitsMasked(TranslationUnit[] translationUnits, ImportSettings settings, bool[] mask) => Learn(translationUnits, mask, settings);
        public ImportResult[] AddOrUpdateTranslationUnitsMasked(TranslationUnit[] translationUnits, int[] previousTranslationHashes, ImportSettings settings, bool[] mask) => Learn(translationUnits, mask, settings);
        public ImportResult UpdateTranslationUnit(TranslationUnit translationUnit) => Learn(new[] { translationUnit }, null)[0];
        public ImportResult[] UpdateTranslationUnits(TranslationUnit[] translationUnits) => Learn(translationUnits, null);
    }
}

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
    /// SuperVault as a Trados Studio translation memory: one memory, built from the Supervertaler Workbench database,
    /// for every project in a language pair it holds, in both directions and every language variant. nl-BE>en-US,
    /// nl-NL>en-GB and en-GB>nl-BE all use the same units; there are no reversed copies and nothing to add per variant.
    ///
    /// Stage 1: lookups and concordance, read-only. Confirmed segments are not stored (SupportsUpdate is false).
    /// </summary>
    [TranslationProviderFactory(Id = "SupervertalerSuperVaultFactory", Name = "SupervertalerSuperVaultFactory", Description = "SupervertalerSuperVaultFactory_Description")]
    public sealed class SuperVaultProviderFactory : ITranslationProviderFactory
    {
        public bool SupportsTranslationProviderUri(Uri uri) => uri != null && uri.Scheme == SuperVaultProvider.Scheme;
        public ITranslationProvider CreateTranslationProvider(Uri uri, string state, ITranslationProviderCredentialStore store) => new SuperVaultProvider();
        public TranslationProviderInfo GetTranslationProviderInfo(Uri uri, string state) =>
            new TranslationProviderInfo { Name = SuperVaultProvider.DisplayName, TranslationMethod = TranslationMethod.TranslationMemory };
    }

    public sealed class SuperVaultProvider : ITranslationProvider
    {
        public const string Scheme = "supervault";
        public const string DisplayName = "SuperVault";
        public static readonly Uri DefaultUri = new Uri("supervault:///");

        internal VaultLibrary Library { get; }

        public SuperVaultProvider() : this(VaultLibrary.For(VaultLibrary.DefaultRoot)) { }

        /// <summary>For tests: a provider on a scratch index.</summary>
        public SuperVaultProvider(string root) : this(VaultLibrary.For(root)) { }

        SuperVaultProvider(VaultLibrary library) { Library = library; }

        /// <summary>
        /// Every pair of two different languages the index holds, either way round, whatever the variants. With no index
        /// yet, every pair: SuperVault can be added to a project first and built afterwards.
        /// </summary>
        public bool SupportsLanguageDirection(LanguagePair pair)
        {
            string a = VaultMarkup.BaseLanguage(pair?.SourceCultureName), b = VaultMarkup.BaseLanguage(pair?.TargetCultureName);
            if (a.Length == 0 || b.Length == 0 || a == b) return false;
            var idx = Library.Current;
            return idx == null || idx.Pairs().Contains(VaultIndex.PairKey(a, b));
        }

        public ITranslationProviderLanguageDirection GetLanguageDirection(LanguagePair pair)
        {
            if (!SupportsLanguageDirection(pair))
                throw new NotSupportedException($"SuperVault holds no {pair?.SourceCultureName} / {pair?.TargetCultureName} units.");
            return new SuperVaultLanguageDirection(this, pair);
        }

        public ProviderStatusInfo StatusInfo
        {
            get
            {
                var s = Library.Status;
                if (s.State == VaultState.Ready) return new ProviderStatusInfo(true, "SuperVault: " + s.Message + (s.Building ? " (rebuilding: " + s.BuildProgress + ")" : ""));
                if (s.Building) return new ProviderStatusInfo(true, "SuperVault is being built: " + s.BuildProgress);
                return new ProviderStatusInfo(false, s.Message + " Open SuperVault's settings to build it.");
            }
        }

        public void RefreshStatusInfo() { }
        public string SerializeState() => "";
        public void LoadState(string translationProviderState) { }

        public Uri Uri => DefaultUri;
        public string Name => DisplayName;

        public bool SupportsTaggedInput => true;
        public bool SupportsScoring => true;
        public bool SupportsSearchForTranslationUnits => true;
        public bool SupportsMultipleResults => true;
        public bool SupportsFilters => false;
        public bool SupportsPenalties => true;
        public bool SupportsStructureContext => false;
        public bool SupportsDocumentSearches => true;
        public bool SupportsUpdate => false;
        public bool SupportsPlaceables => false;
        public bool SupportsTranslation => true;
        public bool SupportsFuzzySearch => true;
        public bool SupportsConcordanceSearch => true;
        public bool SupportsSourceConcordanceSearch => true;
        public bool SupportsTargetConcordanceSearch => true;
        public bool SupportsWordCounts => false;
        public TranslationMethod TranslationMethod => TranslationMethod.TranslationMemory;
        public bool IsReadOnly => true;
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
                var idx = _provider.Library.Current;
                if (idx == null) return results;
                int max = Math.Max(1, settings.MaxResults);

                if (settings.Mode == SearchMode.ConcordanceSearch || settings.Mode == SearchMode.TargetConcordanceSearch)
                {
                    bool onTarget = settings.Mode == SearchMode.TargetConcordanceSearch;
                    var query = VaultLookup.PlainOf(SuperVaultResults.Pieces(segment));
                    foreach (var m in VaultLookup.Concordance(idx, query, onTarget ? _target : _source, onTarget ? _source : _target, settings.MinScore, max))
                    {
                        // Concordance matches come back oriented search language -> other; a TU is always source -> target.
                        var searched = Plain(onTarget ? TargetLanguage : SourceLanguage, m.Source);
                        var other = Plain(onTarget ? SourceLanguage : TargetLanguage, m.Target);
                        results.Add(SuperVaultResults.Concordance(onTarget ? other : searched, onTarget ? searched : other, m));
                    }
                    return results;
                }

                var doc = SuperVaultResults.Pieces(results.SourceSegment);
                var matches = VaultLookup.Search(idx, doc, _source, _target, settings.MinScore, max);
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
                try { Supervertaler.Trados.Core.DiagnosticLog.WriteAlways("SuperVault", "Lookup failed: " + ex); } catch { }
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

        // ---- updates: not in stage 1. SupportsUpdate is false, so Studio does not call these; if it does, nothing is stored. ----

        static ImportResult Discard() => new ImportResult(Sdl.LanguagePlatform.TranslationMemory.Action.Discard);
        public ImportResult AddTranslationUnit(TranslationUnit translationUnit, ImportSettings settings) => Discard();
        public ImportResult[] AddTranslationUnits(TranslationUnit[] translationUnits, ImportSettings settings) => translationUnits.Select(_ => Discard()).ToArray();
        public ImportResult[] AddOrUpdateTranslationUnits(TranslationUnit[] translationUnits, int[] previousTranslationHashes, ImportSettings settings) => AddTranslationUnits(translationUnits, settings);
        public ImportResult[] AddTranslationUnitsMasked(TranslationUnit[] translationUnits, ImportSettings settings, bool[] mask) => AddTranslationUnits(translationUnits, settings);
        public ImportResult[] AddOrUpdateTranslationUnitsMasked(TranslationUnit[] translationUnits, int[] previousTranslationHashes, ImportSettings settings, bool[] mask) => AddTranslationUnits(translationUnits, settings);
        public ImportResult UpdateTranslationUnit(TranslationUnit translationUnit) => Discard();
        public ImportResult[] UpdateTranslationUnits(TranslationUnit[] translationUnits) => AddTranslationUnits(translationUnits, null);
    }
}

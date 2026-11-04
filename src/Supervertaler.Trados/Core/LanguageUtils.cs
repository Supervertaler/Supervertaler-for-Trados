using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Utility methods for language name display.
    /// Converts full language display names (from Trados or culture codes)
    /// into shortened forms suitable for UI labels.
    /// </summary>
    public static class LanguageUtils
    {
        private static readonly Regex ParenthesizedRegion = new Regex(
            @"^(.+?)\s*\((.+?)\)$", RegexOptions.Compiled);

        /// <summary>"nl", "nl-NL", "sr-Latn-RS", "es-419": a language subtag of
        /// two or three letters, then any further subtags.</summary>
        private static readonly Regex CultureNameShape = new Regex(
            @"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.Compiled);

        /// <summary>
        /// Shortens a language display name by abbreviating the country/region part
        /// to its ISO 3166-1 alpha-2 code.
        /// <para>Examples:</para>
        /// <list type="bullet">
        /// <item>"Dutch (Belgium)" → "Dutch (BE)"</item>
        /// <item>"English (United States)" → "English (US)"</item>
        /// <item>"nl-BE" → "Dutch (BE)"</item>
        /// <item>"en" → "English" (neutral culture, no region)</item>
        /// <item>"Dutch" → "Dutch" (unchanged)</item>
        /// </list>
        /// </summary>
        public static string ShortenLanguageName(string langName)
        {
            if (string.IsNullOrWhiteSpace(langName))
                return langName;

            langName = langName.Trim();

            // 1) Try to parse as a culture code (e.g., "en-US", "nl-BE").
            // Not for a display name such as Studio's "Dutch (Netherlands)": no
            // culture code has a space or a bracket, so the attempt can only end
            // in a CultureNotFoundException - which, when this ran per term,
            // was some 38,000 exceptions per termbase load.
            if (langName.IndexOf(' ') < 0 && langName.IndexOf('(') < 0)
            {
                try
                {
                    var culture = new CultureInfo(langName);
                    if (!culture.IsNeutralCulture && culture.Name.Contains("-"))
                    {
                        var region = new RegionInfo(culture.Name);
                        var langPart = culture.Parent.EnglishName;
                        return $"{langPart} ({region.TwoLetterISORegionName})";
                    }
                    if (culture.IsNeutralCulture)
                        return culture.EnglishName;
                }
                catch
                {
                    // Not a valid culture code – fall through to display name parsing
                }
            }

            // 2) Try to parse "Language (Country)" format and shorten the country
            var match = ParenthesizedRegion.Match(langName);
            if (match.Success)
            {
                var language = match.Groups[1].Value;
                var country = match.Groups[2].Value;

                // Already short (2–3 chars)? Return as-is.
                if (country.Length <= 3)
                    return langName;

                var isoCode = FindCountryIsoCode(country);
                if (isoCode != null)
                    return $"{language} ({isoCode})";
            }

            // 3) Fall back unchanged
            return langName;
        }

        /// <summary>
        /// Returns just the base language name, stripping any parenthesised
        /// region/variant suffix.
        /// <para>Examples:</para>
        /// <list type="bullet">
        /// <item>"Dutch (Netherlands)" → "Dutch"</item>
        /// <item>"English (United Kingdom)" → "English"</item>
        /// <item>"Chinese (Simplified)" → "Chinese (Simplified)" (kept – not a region)</item>
        /// <item>"Dutch" → "Dutch" (unchanged)</item>
        /// </list>
        /// </summary>
        public static string GetBaseLanguageName(string langName)
        {
            if (string.IsNullOrWhiteSpace(langName))
                return langName;

            langName = langName.Trim();

            var match = ParenthesizedRegion.Match(langName);
            if (!match.Success)
                return langName;

            var language = match.Groups[1].Value;
            var parenthesised = match.Groups[2].Value;

            // Keep the parenthesised part for script variants (Simplified/Traditional)
            // that are essential for disambiguation – strip country/region names only.
            if (parenthesised.Equals("Simplified", StringComparison.OrdinalIgnoreCase)
                || parenthesised.Equals("Traditional", StringComparison.OrdinalIgnoreCase)
                || parenthesised.Equals("Latin", StringComparison.OrdinalIgnoreCase)
                || parenthesised.Equals("Cyrillic", StringComparison.OrdinalIgnoreCase))
            {
                return langName;
            }

            return language;
        }

        /// <summary>
        /// Normalises a locale code to Supervertaler's storage convention:
        /// base language lower-cased, region upper-cased, joined with '-'
        /// (region preserved — "keep region, match on base"). MultiTerm stores
        /// locales upper-case ("EN", "EN-GB", "NL-BE"); Supervertaler stores
        /// "en", "en-GB", "nl-BE". Mirrors the Python Workbench's
        /// <c>language_codes.canonical()</c> so both products store the same codes
        /// against the shared termbase.
        /// <para>Examples: "EN" → "en"; "EN-GB" → "en-GB"; "nl_be" → "nl-BE".</para>
        /// </summary>
        public static string CanonicalLocale(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;

            var parts = value.Trim().Split(
                new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return value.Trim();

            var baseCode = parts[0].ToLowerInvariant();
            if (parts.Length == 1) return baseCode;

            // Region subtag upper-cased; any further subtags preserved as-is.
            var region = parts[1].ToUpperInvariant();
            var rest = parts.Length > 2 ? "-" + string.Join("-", parts, 2, parts.Length - 2) : "";
            return baseCode + "-" + region + rest;
        }

        /// <summary>
        /// Renders a stored locale code as the English language name used in a
        /// column header: "en" to "English", "nl" to "Dutch", "en-GB" to
        /// "English (United Kingdom)".
        ///
        /// The region is KEPT, matching <see cref="CanonicalLocale"/>. It costs
        /// nothing at match time - the TSV importer strips a parenthesised region
        /// before comparing, and comparison is on the base language either way -
        /// and it tells a person opening the file in Excel which variant they have.
        ///
        /// Returns null for a missing code, so a caller can fall back to a neutral
        /// header rather than writing an empty one; and the code itself for a locale
        /// .NET does not recognise, which still matches because MatchesLanguage
        /// compares codes as well as names.
        /// </summary>
        public static string LocaleToEnglishName(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            var canonical = CanonicalLocale(code);
            try
            {
                var culture = System.Globalization.CultureInfo.GetCultureInfo(canonical);
                if (!string.IsNullOrEmpty(culture.EnglishName)
                    && culture.EnglishName.IndexOf("Unknown", StringComparison.OrdinalIgnoreCase) < 0)
                    return culture.EnglishName;
            }
            catch { /* not a locale .NET knows - fall through to the code */ }

            return canonical;
        }

        /// <summary>
        /// Classifies how a termbase's declared language pair relates to the
        /// active project's source language.
        /// </summary>
        public enum TermbaseDirection
        {
            /// <summary>Project or termbase has no declared source language. Caller should default to no-swap.</summary>
            NotApplicable,
            /// <summary>Project source matches termbase source. No inversion needed.</summary>
            Aligned,
            /// <summary>Project source matches termbase target. Termbase is inverted relative to the project.</summary>
            Inverted,
            /// <summary>Project source matches neither side of the termbase – the termbase is for an unrelated language pair.</summary>
            Unrelated
        }

        /// <summary>
        /// Compares a termbase's declared language pair against the project's
        /// source language to decide whether term lookups/writes should swap
        /// source and target.
        ///
        /// Pre-v4.19.55 every call site had its own ad-hoc check that treated
        /// any mismatch between project source and termbase source as
        /// "inverted" – which silently mis-handled termbases whose language
        /// pair didn't match the project on either side (e.g. an EN-NL
        /// termbase loaded into a DE-FR project would get its sides swapped
        /// and indexed under languages it has no terms for). This helper
        /// distinguishes the four cases so each caller can pick the right
        /// behaviour for read vs write vs merge.
        /// </summary>
        public static TermbaseDirection CompareTermbaseDirection(
            string projectSourceLang, string termbaseSourceLang, string termbaseTargetLang)
        {
            if (string.IsNullOrEmpty(projectSourceLang) || string.IsNullOrEmpty(termbaseSourceLang))
                return TermbaseDirection.NotApplicable;

            var projNorm = ShortenLanguageName(projectSourceLang) ?? "";
            var tbSrcNorm = ShortenLanguageName(termbaseSourceLang) ?? "";
            var tbTgtNorm = ShortenLanguageName(termbaseTargetLang ?? "") ?? "";

            if (LanguagePrefixMatches(projNorm, tbSrcNorm)) return TermbaseDirection.Aligned;
            if (LanguagePrefixMatches(projNorm, tbTgtNorm)) return TermbaseDirection.Inverted;
            return TermbaseDirection.Unrelated;
        }

        /// <summary>
        /// True when a term row's OWN stored language tags say the opposite of
        /// the termbase it lives in – its source_lang naming the termbase's
        /// TARGET side and its target_lang the termbase's SOURCE side.
        ///
        /// This reports a CONTRADICTION, not a verdict, and the distinction
        /// matters because the two cases behind it have opposite consequences:
        ///
        ///   • the TEXT is reversed too (a pre-v18.20.x write that dropped a
        ///     project-direction pair into an opposite-direction termbase
        ///     without swapping it). Every read path orients by the termbase's
        ///     DECLARED direction – see <see cref="Core.TermbaseReader.LoadAllTerms"/> –
        ///     so the row is indexed under the wrong language and matches no
        ///     source segment in either project direction. It stays in the
        ///     termbase, answers lookups, and silently checks nothing.
        ///
        ///   • only the TAGS are wrong and the text is correctly oriented. That
        ///     row matches perfectly today, precisely because the read path
        ///     ignores these tags. Nothing is wrong with it beyond the label.
        ///
        /// Telling them apart needs the text's actual language, which this
        /// cannot see and the plugin deliberately never guesses (the same
        /// refusal as in the write path: term pairs are routinely identical
        /// across languages, so a detector would guess, and a wrong silent
        /// answer is worse than an honest "check this"). Both cases are worth
        /// surfacing – one is broken, the other is mislabelled – but the caller
        /// must be told it is a contradiction to inspect, never that the entry
        /// is definitely dead.
        ///
        /// Deliberately never an automatic repair: these tags are exactly the
        /// field the read path stopped trusting in v4.19.21, and flipping the
        /// second case would turn a cosmetic mislabelling into a genuinely
        /// broken entry. Both tags must agree the row is inverted – one alone
        /// is an ordinary tagging slip.
        ///
        /// A row whose two terms are the SAME string is excluded, even with
        /// contradicting tags. Orientation is moot there: the index key is the
        /// same either way, so the entry matches exactly as it should and there
        /// is nothing for the user to repair. On the database this was built
        /// against that is 68 of 108 rows – brand names, units and formulae –
        /// so reporting them would have buried the 40 real ones.
        /// </summary>
        public static bool EntryDirectionContradictsTermbase(
            string sourceTerm, string targetTerm,
            string entrySourceLang, string entryTargetLang,
            string termbaseSourceLang, string termbaseTargetLang)
        {
            // Untagged rows (bulk imports leave both NULL) say nothing about
            // direction, and a termbase with no declared pair has nothing to
            // contradict.
            if (string.IsNullOrWhiteSpace(entrySourceLang) || string.IsNullOrWhiteSpace(entryTargetLang))
                return false;
            if (string.IsNullOrWhiteSpace(termbaseSourceLang) || string.IsNullOrWhiteSpace(termbaseTargetLang))
                return false;

            // Same text both sides – reversing it changes nothing, so the entry
            // is not broken however its tags read.
            if (string.Equals((sourceTerm ?? "").Trim(), (targetTerm ?? "").Trim(),
                    StringComparison.OrdinalIgnoreCase))
                return false;

            return CompareTermbaseDirection(entrySourceLang, termbaseSourceLang, termbaseTargetLang)
                       == TermbaseDirection.Inverted
                && CompareTermbaseDirection(entryTargetLang, termbaseSourceLang, termbaseTargetLang)
                       == TermbaseDirection.Aligned;
        }

        // ─── The two sides of a document, by language ───────────────

        /// <summary>
        /// What to call the two sides of the open document: language names in
        /// place of "Source" and "Target", so a reader can see which column holds
        /// which language. <see cref="Known"/> is false when there is no document
        /// or its languages could not be read, and every label then falls back
        /// to the generic word.
        /// </summary>
        public sealed class PairLabels
        {
            /// <summary>The culture names these labels were made from, as given ("nl-NL").</summary>
            public string SourceLocale, TargetLocale;
            /// <summary>"Dutch", or "Dutch (Belgium)" where the region matters.</summary>
            public string Source, Target;
            /// <summary>For a search box with little room: "NL", or "NL-BE".</summary>
            public string SourceCode, TargetCode;
            /// <summary>For tooltips: "Dutch (Netherlands), nl-NL".</summary>
            public string SourceFull, TargetFull;
            public bool Known;
        }

        /// <summary>
        /// Labels for a document whose sides are <paramref name="sourceLocale"/>
        /// and <paramref name="targetLocale"/> (culture names, as Trados gives
        /// them).
        ///
        /// The region is left off unless it says something: "Dutch", not "Dutch
        /// (Netherlands)". It is shown when the two sides share a language
        /// (en-GB to en-US, where nothing else tells them apart), and for a
        /// variant other than the language's usual one ("Dutch (Belgium)",
        /// "French (Canada)") - see <see cref="IsUsualVariant"/>.
        /// </summary>
        public static PairLabels LabelsForPair(string sourceLocale, string targetLocale)
        {
            var src = TryGetCulture(sourceLocale);
            var tgt = TryGetCulture(targetLocale);
            if (src == null || tgt == null)
                return GenericLabels(sourceLocale, targetLocale);

            bool sameLanguage = string.Equals(
                NeutralName(src), NeutralName(tgt), StringComparison.OrdinalIgnoreCase);

            var labels = new PairLabels
            {
                SourceLocale = sourceLocale,
                TargetLocale = targetLocale,
                Source = ColumnName(src, sameLanguage),
                Target = ColumnName(tgt, sameLanguage),
                SourceCode = ColumnCode(src, sameLanguage),
                TargetCode = ColumnCode(tgt, sameLanguage),
                SourceFull = src.EnglishName + ", " + src.Name,
                TargetFull = tgt.EnglishName + ", " + tgt.Name,
                Known = true,
            };

            // The same culture on both sides: names cannot tell the columns
            // apart, and "Source"/"Target" can.
            if (string.Equals(labels.Source, labels.Target, StringComparison.OrdinalIgnoreCase))
                return GenericLabels(sourceLocale, targetLocale);
            return labels;
        }

        private static PairLabels GenericLabels(string sourceLocale, string targetLocale)
        {
            return new PairLabels
            {
                SourceLocale = sourceLocale,
                TargetLocale = targetLocale,
                Source = "Source", Target = "Target",
                SourceCode = "Src", TargetCode = "Tgt",
                SourceFull = "Source", TargetFull = "Target",
                Known = false,
            };
        }

        private static string ColumnName(CultureInfo c, bool forceRegion)
        {
            if (c.IsNeutralCulture || forceRegion || !IsUsualVariant(c))
                return c.EnglishName;
            return NeutralName(c);
        }

        private static string ColumnCode(CultureInfo c, bool forceRegion)
        {
            if (!c.IsNeutralCulture && (forceRegion || !IsUsualVariant(c)))
                return c.Name.ToUpperInvariant();
            return c.TwoLetterISOLanguageName.ToUpperInvariant();
        }

        /// <summary>
        /// True for the variant a language means when nothing more is said:
        /// whichever one Windows picks for the bare language code (nl-NL, de-DE,
        /// fr-FR, es-ES, sv-SE, ja-JP). So nl-BE, fr-CA and de-AT carry their
        /// region and nl-NL does not.
        ///
        /// English is the exception, and deliberately so: UK and US English are
        /// both reference variants, and labelling one of them as if it were a
        /// regional deviation like en-AU would read wrongly to either audience.
        /// Both are plain "English" - except in an en-GB to en-US project, where
        /// <see cref="LabelsForPair"/> shows the regions on both sides anyway.
        /// </summary>
        internal static bool IsUsualVariant(CultureInfo c)
        {
            if (c == null || c.IsNeutralCulture) return true;
            var lang = c.TwoLetterISOLanguageName;
            if (string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase))
                return c.Name.EndsWith("-GB", StringComparison.OrdinalIgnoreCase)
                    || c.Name.EndsWith("-US", StringComparison.OrdinalIgnoreCase);
            try
            {
                return string.Equals(CultureInfo.CreateSpecificCulture(lang).Name, c.Name,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }   // no default to compare with: claim nothing
        }

        /// <summary>
        /// The language without its region, by English name: "Dutch" for nl-BE,
        /// "Chinese (Simplified)" for zh-CN - the script is part of the language
        /// here, as it is in <see cref="GetBaseLanguageName"/>.
        ///
        /// .NET Framework puts a legacy culture between zh-CN and zh-Hans
        /// ("Chinese (Simplified) Legacy", zh-CHS); it is stepped over.
        /// </summary>
        private static string NeutralName(CultureInfo c)
        {
            var n = c;
            while (!string.IsNullOrEmpty(n.Parent?.Name)
                   && (!n.IsNeutralCulture
                       || n.EnglishName.IndexOf("Legacy", StringComparison.OrdinalIgnoreCase) >= 0))
                n = n.Parent;
            return (string.IsNullOrEmpty(n.Name) ? c : n).EnglishName;
        }

        /// <summary>
        /// The culture for a name such as "nl-NL", or null when there is none.
        /// Windows invents a culture for a well-formed tag it does not know
        /// ("Unknown Language (xx)") rather than refusing it; that counts as none.
        /// </summary>
        private static CultureInfo TryGetCulture(string name)
        {
            // A name such as "German" or "English (United Kingdom)" - termbases
            // declare those - is not even shaped like a culture name. Asked
            // anyway, .NET throws, and this runs for every row the grid draws.
            if (string.IsNullOrWhiteSpace(name) || !CultureNameShape.IsMatch(name.Trim())) return null;
            try
            {
                var c = CultureInfo.GetCultureInfo(name.Trim());
                if (string.IsNullOrEmpty(c.Name)
                    || c.EnglishName.IndexOf("Unknown", StringComparison.OrdinalIgnoreCase) >= 0)
                    return null;
                return c;
            }
            catch { return null; }
        }

        /// <summary>
        /// A language value as a person reads it: "German (Germany)" for de-DE,
        /// "German" for de, and anything that is not a culture name (termbases
        /// may declare "German" outright) as given. Null for null or blank.
        /// </summary>
        public static string LanguageDisplayName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return TryGetCulture(value)?.EnglishName ?? value.Trim();
        }

        /// <summary>
        /// A language value as a short code: "DE" for de-DE or de, and anything
        /// that is not a culture name (a termbase may declare "German") as given.
        /// Null for null or blank.
        /// </summary>
        public static string LanguageCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return TryGetCulture(value)?.TwoLetterISOLanguageName.ToUpperInvariant() ?? value.Trim();
        }

        /// <summary>
        /// True when two language values name the same language, ignoring the
        /// region. Takes culture names ("nl-BE") and English names ("Dutch",
        /// "English (United Kingdom)") alike, since termbases store either.
        /// </summary>
        public static bool IsSameLanguage(string a, string b)
        {
            var x = BaseLanguage(a);
            return x.Length > 0 && string.Equals(x, BaseLanguage(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string BaseLanguage(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var c = TryGetCulture(value);
            if (c != null) return NeutralName(c);
            return GetBaseLanguageName(value) ?? "";
        }

        /// <summary>
        /// True when a translation memory stored as
        /// <paramref name="rowSource"/> → <paramref name="rowTarget"/> lines up
        /// with the open document only once its sides are swapped - an EN→NL
        /// memory in an NL→EN project.
        ///
        /// An exact culture match counts for more than a language-only one, so an
        /// en-US → en-GB memory in an en-GB → en-US project is swapped too; by
        /// language alone the two orientations are indistinguishable. A pair that
        /// matches neither way better (an NL→DE memory in an NL→EN project) stays
        /// as stored, as does everything when either pair is unknown.
        /// </summary>
        public static bool IsReversedPair(string projectSource, string projectTarget,
            string rowSource, string rowTarget)
        {
            if (string.IsNullOrWhiteSpace(projectSource) || string.IsNullOrWhiteSpace(projectTarget)
                || string.IsNullOrWhiteSpace(rowSource) || string.IsNullOrWhiteSpace(rowTarget))
                return false;
            int asStored = Affinity(rowSource, projectSource) + Affinity(rowTarget, projectTarget);
            int swapped = Affinity(rowTarget, projectSource) + Affinity(rowSource, projectTarget);
            return swapped > asStored;
        }

        private static int Affinity(string a, string b)
        {
            if (string.Equals(CanonicalLocale(a), CanonicalLocale(b), StringComparison.OrdinalIgnoreCase))
                return 2;
            return IsSameLanguage(a, b) ? 1 : 0;
        }

        /// <summary>
        /// True unless a row - already turned to face the document's way - is
        /// known to hold some other pair of languages: German in the column
        /// headed English, say, from an NL→DE termbase or memory in an NL→EN
        /// project.
        ///
        /// A side whose language was never recorded (a termbase declared without
        /// languages) counts as matching. TermLens treats such a termbase as
        /// facing the project's way, and SuperSearch has no better information.
        /// </summary>
        public static bool IsInDocumentPair(string projectSource, string projectTarget,
            string rowSource, string rowTarget)
        {
            if (string.IsNullOrWhiteSpace(projectSource) || string.IsNullOrWhiteSpace(projectTarget))
                return true;
            bool srcOk = string.IsNullOrWhiteSpace(rowSource) || IsSameLanguage(rowSource, projectSource);
            bool tgtOk = string.IsNullOrWhiteSpace(rowTarget) || IsSameLanguage(rowTarget, projectTarget);
            return srcOk && tgtOk;
        }

        private static bool LanguagePrefixMatches(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            // Match either direction so that "English (US)" lines up with "English"
            // and "English (United States)" lines up with "English (US)".
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
                || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Exercises <see cref="CompareTermbaseDirection"/> against a fixed
        /// table of canonical language-name shapes (full names, BCP-47 codes,
        /// abbreviated regions, missing/empty inputs, mismatched pairs), and
        /// SuperSearch's row orientation (<see cref="IsReversedPair"/>,
        /// <see cref="IsInDocumentPair"/>) against the same kinds of input.
        /// Returns <c>null</c> on success or a short description of the first
        /// failed case. Wired into plugin startup alongside the
        /// <see cref="Settings.TermLensSettings.RunStartupSelfTest"/> guard so
        /// any future regression in the direction-comparison logic surfaces
        /// in <c>bridge.log</c> instead of after users notice term lookups
        /// going to the wrong column.
        /// </summary>
        public static string RunStartupSelfTest()
        {
            var cases = new[]
            {
                new SelfTestCase("English",                 "English (United States)",  "Dutch",            TermbaseDirection.Aligned),
                new SelfTestCase("English (United States)", "English",                  "Dutch",            TermbaseDirection.Aligned),
                new SelfTestCase("English (US)",            "English (United States)",  "Dutch",            TermbaseDirection.Aligned),
                new SelfTestCase("en-US",                   "English (United States)",  "Dutch",            TermbaseDirection.Aligned),
                new SelfTestCase("en-US",                   "English (UK)",             "Dutch",            TermbaseDirection.Unrelated),
                new SelfTestCase("English",                 "Dutch",                    "English",          TermbaseDirection.Inverted),
                new SelfTestCase("Dutch (Netherlands)",     "English",                  "Dutch",            TermbaseDirection.Inverted),
                new SelfTestCase("nl-NL",                   "English",                  "Dutch",            TermbaseDirection.Inverted),
                new SelfTestCase("German",                  "English",                  "Dutch",            TermbaseDirection.Unrelated),
                new SelfTestCase("",                        "English",                  "Dutch",            TermbaseDirection.NotApplicable),
                new SelfTestCase("English",                 "",                         "Dutch",            TermbaseDirection.NotApplicable),
                new SelfTestCase(null,                      "English",                  "Dutch",            TermbaseDirection.NotApplicable),
                new SelfTestCase("English",                 "English",                  "",                 TermbaseDirection.Aligned),
                new SelfTestCase("Dutch",                   "English",                  "",                 TermbaseDirection.Unrelated),
                new SelfTestCase("English (US)",            "English (UK)",             "French (Canada)",  TermbaseDirection.Unrelated),
                new SelfTestCase("French",                  "English (UK)",             "French (CA)",      TermbaseDirection.Inverted),
            };
            foreach (var c in cases)
            {
                var got = CompareTermbaseDirection(c.Proj, c.TbSrc, c.TbTgt);
                if (got != c.Expected)
                {
                    return $"CompareTermbaseDirection('{c.Proj}', '{c.TbSrc}', '{c.TbTgt}') = {got}, expected {c.Expected}";
                }
            }

            // EntryDirectionContradictsTermbase: srcTerm, tgtTerm, entrySrc, entryTgt, tbSrc, tbTgt, expected.
            var mismatchCases = new[]
            {
                // The shape this exists to catch: an NL→EN pair written into an
                // EN→NL termbase, tags honest, text reversed.
                new MismatchCase("bezinksel", "sediment", "nl", "en", "en", "nl", true),
                new MismatchCase("gras", "grass", "Dutch", "English", "English", "Dutch", true),
                // Correctly oriented rows – the overwhelming majority.
                new MismatchCase("sediment", "bezinksel", "en", "nl", "en", "nl", false),
                // Same text both sides: orientation is moot, the entry matches
                // either way. Must NOT be reported.
                new MismatchCase("DNV", "DNV", "nl", "en", "en", "nl", false),
                new MismatchCase("m³/h", " M³/H ", "nl", "en", "en", "nl", false),
                // Untagged rows (bulk import) and undeclared termbases: unknown,
                // not wrong.
                new MismatchCase("a", "b", null, null, "en", "nl", false),
                new MismatchCase("a", "b", "", "nl", "en", "nl", false),
                new MismatchCase("a", "b", "nl", "en", "",   "",   false),
                // One tag alone is a slip, not a reversed write.
                new MismatchCase("a", "b", "nl", "nl", "en", "nl", false),
                // A row belonging to neither of the termbase's languages is a
                // different fault; don't claim it is reversed.
                new MismatchCase("a", "b", "de", "fr", "en", "nl", false),
                // Same-language pairs must still resolve by region.
                new MismatchCase("colour", "color", "en-GB", "en-US", "en-US", "en-GB", true),
                new MismatchCase("color", "colour", "en-US", "en-GB", "en-US", "en-GB", false),
            };
            foreach (var c in mismatchCases)
            {
                var got = EntryDirectionContradictsTermbase(
                    c.SrcTerm, c.TgtTerm, c.EntrySrc, c.EntryTgt, c.TbSrc, c.TbTgt);
                if (got != c.Expected)
                {
                    return $"EntryDirectionContradictsTermbase('{c.SrcTerm}', '{c.TgtTerm}', " +
                           $"'{c.EntrySrc}', '{c.EntryTgt}', '{c.TbSrc}', '{c.TbTgt}') = {got}, " +
                           $"expected {c.Expected}";
                }
            }

            // SuperSearch: how a TM or termbase row is turned to face the
            // document, and whether it then holds the document's languages.
            // A wrong answer puts Dutch under a column headed English.
            // projSrc, projTgt, rowSrc, rowTgt, reversed, in pair once oriented.
            var pairCases = new[]
            {
                new PairCase("nl-NL", "en-GB", "nl-NL", "en-GB", false, true),
                new PairCase("nl-NL", "en-GB", "en-GB", "nl-NL", true,  true),
                // Regions differ, languages do not: still the same pair.
                new PairCase("nl-NL", "en-GB", "en-US", "nl-BE", true,  true),
                // Same language both sides: only the exact region decides.
                new PairCase("en-GB", "en-US", "en-US", "en-GB", true,  true),
                new PairCase("en-GB", "en-US", "en-GB", "en-US", false, true),
                // A third language: Dutch is turned to the left, German stays
                // German and must be reported as off the pair.
                new PairCase("nl-NL", "en-GB", "nl-NL", "de-DE", false, false),
                new PairCase("nl-NL", "en-GB", "de-DE", "nl-NL", true,  false),
                // Termbases declare names as well as codes.
                new PairCase("nl-NL", "en-GB", "English", "Dutch", true, true),
                new PairCase("nl-NL", "en-GB", "en",      "nl",    true, true),
                // Unknown on either side: leave as stored, claim nothing.
                new PairCase(null,    "en-GB", "en-GB", "nl-NL", false, true),
                new PairCase("nl-NL", "en-GB", "",      "",      false, true),
            };
            foreach (var c in pairCases)
            {
                var reversed = IsReversedPair(c.ProjSrc, c.ProjTgt, c.RowSrc, c.RowTgt);
                var left = reversed ? c.RowTgt : c.RowSrc;
                var right = reversed ? c.RowSrc : c.RowTgt;
                var inPair = IsInDocumentPair(c.ProjSrc, c.ProjTgt, left, right);
                if (reversed != c.Reversed || inPair != c.InPair)
                {
                    return $"SuperSearch orientation ('{c.ProjSrc}'→'{c.ProjTgt}', row " +
                           $"'{c.RowSrc}'→'{c.RowTgt}'): reversed={reversed}, inPair={inPair}; " +
                           $"expected reversed={c.Reversed}, inPair={c.InPair}";
                }
            }
            return null;
        }

        private struct PairCase
        {
            public string ProjSrc, ProjTgt, RowSrc, RowTgt;
            public bool Reversed, InPair;
            public PairCase(string projSrc, string projTgt, string rowSrc, string rowTgt,
                bool reversed, bool inPair)
            {
                ProjSrc = projSrc; ProjTgt = projTgt; RowSrc = rowSrc; RowTgt = rowTgt;
                Reversed = reversed; InPair = inPair;
            }
        }

        private struct SelfTestCase
        {
            public string Proj;
            public string TbSrc;
            public string TbTgt;
            public TermbaseDirection Expected;
            public SelfTestCase(string proj, string tbSrc, string tbTgt, TermbaseDirection expected)
            {
                Proj = proj; TbSrc = tbSrc; TbTgt = tbTgt; Expected = expected;
            }
        }

        private struct MismatchCase
        {
            public string SrcTerm;
            public string TgtTerm;
            public string EntrySrc;
            public string EntryTgt;
            public string TbSrc;
            public string TbTgt;
            public bool Expected;
            public MismatchCase(string srcTerm, string tgtTerm, string entrySrc, string entryTgt,
                string tbSrc, string tbTgt, bool expected)
            {
                SrcTerm = srcTerm; TgtTerm = tgtTerm;
                EntrySrc = entrySrc; EntryTgt = entryTgt;
                TbSrc = tbSrc; TbTgt = tbTgt; Expected = expected;
            }
        }

        /// <summary>
        /// Finds the 2-letter ISO 3166-1 country code for a country name.
        /// </summary>
        private static string FindCountryIsoCode(string countryName)
        {
            if (string.IsNullOrEmpty(countryName)) return null;
            return CountryIsoCodes.Value.TryGetValue(countryName, out var code) ? code : null;
        }

        /// <summary>
        /// Every specific culture's region by its English and display name,
        /// built once per process. It used to be a fresh scan of all ~800
        /// cultures on every call: 4.4 ms, which is nothing once but was paid
        /// three times per term when a termbase loaded for a project whose
        /// source language Studio names "Dutch (Netherlands)" - 52 s on a
        /// 12,800-term project (18.20.205). The table is fixed for the life of
        /// the process, so caching it changes no answer: the first culture to
        /// carry a name wins, as the first match of the scan did.
        /// </summary>
        private static readonly Lazy<System.Collections.Generic.Dictionary<string, string>> CountryIsoCodes =
            new Lazy<System.Collections.Generic.Dictionary<string, string>>(() =>
            {
                var map = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ci in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
                {
                    try
                    {
                        var region = new RegionInfo(ci.Name);
                        foreach (var name in new[] { region.EnglishName, region.DisplayName })
                            if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name))
                                map[name] = region.TwoLetterISORegionName;
                    }
                    catch
                    {
                        // Some cultures may throw – skip them
                    }
                }
                return map;
            });
    }
}

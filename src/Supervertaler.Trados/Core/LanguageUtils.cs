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

            // 1) Try to parse as a culture code (e.g., "en-US", "nl-BE")
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
        /// abbreviated regions, missing/empty inputs, mismatched pairs).
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
            return null;
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

        /// <summary>
        /// Finds the 2-letter ISO 3166-1 country code for a country name.
        /// Searches all specific cultures' RegionInfo for a match.
        /// </summary>
        private static string FindCountryIsoCode(string countryName)
        {
            foreach (var ci in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
            {
                try
                {
                    var region = new RegionInfo(ci.Name);
                    if (string.Equals(region.EnglishName, countryName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(region.DisplayName, countryName, StringComparison.OrdinalIgnoreCase))
                    {
                        return region.TwoLetterISORegionName;
                    }
                }
                catch
                {
                    // Some cultures may throw – skip them
                }
            }
            return null;
        }
    }
}

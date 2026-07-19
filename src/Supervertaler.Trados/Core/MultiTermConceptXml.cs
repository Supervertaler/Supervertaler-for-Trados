using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Parses the MultiTerm concept XML blob stored per entry (the
    /// <c>mtConcepts.text</c> / <c>mtConcepts.Text</c> column — identical compact shape in
    /// both .sdltb and .ttb). Both <see cref="MultiTermReader"/> and <see cref="TtbReader"/>
    /// share this so descriptive-field extraction stays consistent across formats.
    ///
    /// Structure (compact form; verbose MTF names in parentheses):
    ///   &lt;cG&gt; (conceptGrp) root
    ///     &lt;dG&gt;&lt;d type="Note"&gt;…&lt;/d&gt;&lt;/dG&gt;            — concept-level fields
    ///     &lt;lG&gt; (languageGrp)
    ///       &lt;l lang="EN" type="English"/&gt;         — language identity (locale + name)
    ///       &lt;dG&gt;&lt;d type="Definition"&gt;…&lt;/d&gt;&lt;/dG&gt;    — language-level fields
    ///       &lt;tG&gt;&lt;t&gt;shield&lt;/t&gt;&lt;dG&gt;&lt;d type="Status"&gt;preferred&lt;/d&gt;&lt;/dG&gt;&lt;/tG&gt;  — term-level fields
    ///
    /// A descriptive field is a &lt;d&gt;/&lt;descrip&gt; element with a <c>type</c> attribute.
    /// Fields with no language-group ancestor are concept-level; the rest are attributed to
    /// the language identified by their enclosing &lt;lG&gt;'s &lt;l&gt; element (both its
    /// <c>lang</c>/locale and <c>type</c>/name are registered as lookup keys). Transaction
    /// metadata (<c>&lt;trG&gt;&lt;tr type="origination"&gt;</c>) and bare term text (<c>&lt;t&gt;</c>) carry no
    /// <c>type</c> on a &lt;d&gt;/&lt;descrip&gt; element and are naturally ignored.
    /// </summary>
    internal static class MultiTermConceptXml
    {
        private static readonly HashSet<string> DescripNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "d", "descrip" };
        private static readonly HashSet<string> LangGroupNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "lG", "languageGrp" };
        private static readonly HashSet<string> LangElemNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "l", "language" };

        /// <summary>
        /// Parses <paramref name="xml"/> and merges its descriptive fields into
        /// <paramref name="concept"/>: concept-level fields into <see cref="ImportConcept.Fields"/>,
        /// language/term-level fields into <see cref="ImportConcept.LanguageFields"/> keyed by
        /// the matching <see cref="ImportLanguage.Id"/>. Silently ignores missing/malformed XML.
        /// </summary>
        public static void ApplyToConcept(
            ImportConcept concept, string xml, IReadOnlyList<ImportLanguage> languages)
        {
            if (concept == null || string.IsNullOrWhiteSpace(xml)) return;

            XElement root;
            try { root = XElement.Parse(xml); }
            catch { return; }

            // language token (name or locale, lower-invariant) -> ImportLanguage.Id
            var langByToken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (languages != null)
            {
                foreach (var lang in languages)
                {
                    if (!string.IsNullOrWhiteSpace(lang.Name)) langByToken[lang.Name] = lang.Id;
                    if (!string.IsNullOrWhiteSpace(lang.Locale)) langByToken[lang.Locale] = lang.Id;
                }
            }

            foreach (var d in root.DescendantsAndSelf())
            {
                if (!DescripNames.Contains(d.Name.LocalName)) continue;

                var type = (string)d.Attribute("type");
                if (string.IsNullOrWhiteSpace(type)) continue;

                var value = (d.Value ?? string.Empty).Trim();
                if (value.Length == 0) continue;

                var langGroup = FindAncestorLangGroup(d);
                if (langGroup == null)
                {
                    Accumulate(concept.Fields, type, value);
                    continue;
                }

                if (!TryResolveLanguageId(langGroup, langByToken, out var langId)) continue;

                if (!concept.LanguageFields.TryGetValue(langId, out var langDict))
                {
                    langDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    concept.LanguageFields[langId] = langDict;
                }
                Accumulate(langDict, type, value);
            }
        }

        private static XElement FindAncestorLangGroup(XElement el)
        {
            for (var p = el.Parent; p != null; p = p.Parent)
            {
                if (LangGroupNames.Contains(p.Name.LocalName)) return p;
            }
            return null;
        }

        // Resolves the language id for a &lt;lG&gt; via its &lt;l lang="EN" type="English"/&gt; child.
        private static bool TryResolveLanguageId(
            XElement langGroup, Dictionary<string, int> langByToken, out int langId)
        {
            langId = -1;
            var l = langGroup.Elements()
                .FirstOrDefault(e => LangElemNames.Contains(e.Name.LocalName));
            if (l == null) return false;

            var locale = (string)l.Attribute("lang");
            var name = (string)l.Attribute("type");
            if (!string.IsNullOrWhiteSpace(locale) && langByToken.TryGetValue(locale, out langId)) return true;
            if (!string.IsNullOrWhiteSpace(name) && langByToken.TryGetValue(name, out langId)) return true;
            return false;
        }

        private static void Accumulate(Dictionary<string, string> dict, string type, string value)
        {
            if (dict.TryGetValue(type, out var existing))
            {
                if (existing.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0)
                    dict[type] = existing + "; " + value;
            }
            else
            {
                dict[type] = value;
            }
        }

        /// <summary>
        /// Distinct descriptive field names (concept- and language-level) across a set of
        /// concepts, sorted case-insensitively. Drives the rows of the import field-mapping grid.
        /// </summary>
        public static List<string> CollectFieldNames(IEnumerable<ImportConcept> concepts)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in concepts)
            {
                foreach (var name in c.Fields.Keys) set.Add(name);
                foreach (var langDict in c.LanguageFields.Values)
                {
                    foreach (var name in langDict.Keys) set.Add(name);
                }
            }
            return set.ToList();
        }
    }
}

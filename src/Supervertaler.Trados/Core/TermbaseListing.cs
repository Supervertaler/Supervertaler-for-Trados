using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Xml.Linq;

namespace Supervertaler.Trados.Core
{
    // ─── list_terms (GET /v1/termbase-entries) ─────────────────────────────
    //
    // Reads one termbase's entries a page at a time, for an assistant that has
    // to see the whole termbase rather than look terms up one by one: entries
    // whose source form in the text is inflected, split or uses another
    // preposition are only found by reading the list. Read-only for every
    // kind of termbase, and paged in the database, never by loading the whole
    // termbase and slicing it - a 100,000-entry termbase costs one page per call.

    /// <summary>One termbase entry as list_terms reports it.</summary>
    [DataContract]
    public class BridgeTermListEntry
    {
        /// <summary>The entry's own id: the concept id (Trados's entry number) in
        /// a .ttb / .sdltb, the row id in a Supervertaler termbase. The same
        /// number lookup_term reports as entryId.</summary>
        [DataMember(Name = "id", Order = 0)] public long Id { get; set; }
        /// <summary>Every source term of the entry, the first being the main one.</summary>
        [DataMember(Name = "source", Order = 1)] public List<string> Source { get; set; } = new List<string>();
        /// <summary>Every target term of the entry, the first being the main one.</summary>
        [DataMember(Name = "target", Order = 2)] public List<string> Target { get; set; } = new List<string>();
        [DataMember(Name = "sourceAbbreviation", Order = 3, EmitDefaultValue = false)] public string SourceAbbreviation { get; set; }
        [DataMember(Name = "targetAbbreviation", Order = 4, EmitDefaultValue = false)] public string TargetAbbreviation { get; set; }
        [DataMember(Name = "definition", Order = 5, EmitDefaultValue = false)] public string Definition { get; set; }
        [DataMember(Name = "domain", Order = 6, EmitDefaultValue = false)] public string Domain { get; set; }
        [DataMember(Name = "notes", Order = 7, EmitDefaultValue = false)] public string Notes { get; set; }
        /// <summary>Any other descriptive fields, as "Name: value" or
        /// "Name (language): value". A list rather than an object: the bridge's
        /// serializer writes dictionaries in the verbose Key/Value form.</summary>
        [DataMember(Name = "fields", Order = 8, EmitDefaultValue = false)] public List<string> Fields { get; set; }
        [DataMember(Name = "forbidden", Order = 9, EmitDefaultValue = false)] public bool Forbidden { get; set; }
        [DataMember(Name = "nonTranslatable", Order = 10, EmitDefaultValue = false)] public bool NonTranslatable { get; set; }
        [DataMember(Name = "createdBy", Order = 11, EmitDefaultValue = false)] public string CreatedBy { get; set; }
        [DataMember(Name = "created", Order = 12, EmitDefaultValue = false)] public string Created { get; set; }
        /// <summary>The latest change to the entry or any of its terms.</summary>
        [DataMember(Name = "modifiedBy", Order = 13, EmitDefaultValue = false)] public string ModifiedBy { get; set; }
        [DataMember(Name = "modified", Order = 14, EmitDefaultValue = false)] public string Modified { get; set; }
    }

    /// <summary>The termbase a list_terms call read, under the one name and id
    /// every termbase tool reports and accepts.</summary>
    [DataContract]
    public class BridgeTermbaseRef
    {
        [DataMember(Name = "id", Order = 0)] public long Id { get; set; }
        [DataMember(Name = "name", Order = 1)] public string Name { get; set; }
        [DataMember(Name = "kind", Order = 2)] public string Kind { get; set; }
        [DataMember(Name = "sourceLang", Order = 3, EmitDefaultValue = false)] public string SourceLang { get; set; }
        [DataMember(Name = "targetLang", Order = 4, EmitDefaultValue = false)] public string TargetLang { get; set; }
        [DataMember(Name = "file", Order = 5, EmitDefaultValue = false)] public string File { get; set; }
    }

    [DataContract]
    public class BridgeTermListResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "termbase", Order = 2, EmitDefaultValue = false)] public BridgeTermbaseRef Termbase { get; set; }
        /// <summary>Entries matching the call (all of them without 'contains').</summary>
        [DataMember(Name = "total", Order = 3)] public int Total { get; set; }
        [DataMember(Name = "offset", Order = 4)] public int Offset { get; set; }
        [DataMember(Name = "limit", Order = 5)] public int Limit { get; set; }
        [DataMember(Name = "returned", Order = 6)] public int Returned { get; set; }
        /// <summary>True when entries remain after this page.</summary>
        [DataMember(Name = "truncated", Order = 7)] public bool Truncated { get; set; }
        /// <summary>The offset of the next page; absent on the last one.</summary>
        [DataMember(Name = "nextOffset", Order = 8, EmitDefaultValue = false)] public int? NextOffset { get; set; }
        [DataMember(Name = "entries", Order = 9, EmitDefaultValue = false)] public List<BridgeTermListEntry> Entries { get; set; }
        [DataMember(Name = "note", Order = 10, EmitDefaultValue = false)] public string Note { get; set; }
    }

    /// <summary>One page as a reader returns it, before the bridge wraps it.</summary>
    public sealed class TermListPage
    {
        public int Total;
        public List<BridgeTermListEntry> Entries = new List<BridgeTermListEntry>();
        /// <summary>Set when the page could not be read; Total and Entries are then meaningless.</summary>
        public string Error;
    }

    internal static class TermbaseListing
    {
        public const int DefaultLimit = 100;
        /// <summary>A page of 500 entries is about 60-100 KB of JSON: enough to read a
        /// few-hundred-entry client termbase in one or two calls, small enough for a
        /// chat client to take.</summary>
        public const int MaxLimit = 500;

        public static int ClampLimit(int? limit)
            => limit == null || limit.Value <= 0 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);

        public static int ClampOffset(int? offset) => offset == null || offset.Value < 0 ? 0 : offset.Value;

        /// <summary>
        /// The whole list_terms answer: resolve <paramref name="wanted"/> against
        /// <paramref name="all"/> (an unknown or ambiguous name is an error, never an
        /// empty list), read one page, and say plainly when the page is empty
        /// because the termbase is, because nothing matched, or because the offset
        /// is past the end.
        /// </summary>
        public static BridgeTermListResponse BuildResponse(IReadOnlyList<TermbaseRef> all, string dbPath,
            string wanted, int? offset, int? limit, string contains)
        {
            var tb = TermbaseCatalog.Resolve(all, wanted, out var error);
            if (tb == null) return new BridgeTermListResponse { Ok = false, Error = error };

            int off = ClampOffset(offset), lim = ClampLimit(limit);
            var response = new BridgeTermListResponse
            {
                Termbase = new BridgeTermbaseRef
                {
                    Id = tb.Id, Name = tb.Name, Kind = tb.Kind,
                    SourceLang = tb.SourceLang, TargetLang = tb.TargetLang,
                    File = tb.FilePath
                },
                Offset = off,
                Limit = lim
            };

            var page = ReadPage(tb, dbPath, off, lim, contains);
            if (page.Error != null)
            {
                response.Ok = false;
                response.Error = page.Error;
                return response;
            }

            response.Ok = true;
            response.Total = page.Total;
            response.Entries = page.Entries;
            response.Returned = page.Entries.Count;
            response.Truncated = off + page.Entries.Count < page.Total;
            if (response.Truncated) response.NextOffset = off + page.Entries.Count;

            var notes = new List<string>();
            if (limit != null && limit.Value > MaxLimit)
                notes.Add($"limit capped at {MaxLimit}.");
            if (page.Total == 0)
                notes.Add(string.IsNullOrWhiteSpace(contains)
                    ? "The termbase has no entries."
                    : $"No entry has a source or target term containing '{contains.Trim()}'.");
            else if (page.Entries.Count == 0 && off >= page.Total)
                notes.Add($"offset {off} is past the end: there are {page.Total} entries, so the last page starts below {page.Total}.");
            if (response.Truncated)
                notes.Add($"More entries follow: call again with offset={response.NextOffset}.");
            response.Note = notes.Count > 0 ? string.Join(" ", notes) : null;
            return response;
        }

        /// <summary>One page from whichever kind of termbase <paramref name="tb"/> is.
        /// Trados files are read through <see cref="TtbImportSnapshot"/> - a .ttb that
        /// Studio has open keeps recent changes in its -wal file, and the original is
        /// never opened for writing - and with the file-based readers' read-only modes.</summary>
        internal static TermListPage ReadPage(TermbaseRef tb, string dbPath, int offset, int limit, string contains)
        {
            if (!tb.IsTrados)
            {
                if (string.IsNullOrEmpty(dbPath))
                    return new TermListPage { Error = "the Supervertaler termbase database was not found" };
                using (var reader = new TermbaseReader(dbPath))
                {
                    if (!reader.Open())
                        return new TermListPage { Error = "could not open the Supervertaler termbase database: " + reader.LastError };
                    return reader.ListTerms(tb.Id, offset, limit, contains);
                }
            }

            if (string.IsNullOrEmpty(tb.FilePath) || !System.IO.File.Exists(tb.FilePath))
                return new TermListPage { Error = $"the termbase file of {tb} is missing: {tb.FilePath}" };

            try
            {
                using (var snapshot = TtbImportSnapshot.Prepare(tb.FilePath))
                using (var reader = TermbaseReaderFactory.Create(snapshot.ReadPath))
                {
                    if (!reader.Open())
                    {
                        var why = tb.Kind == TermbaseCatalog.KindMultiTerm
                            ? " A .sdltb is a Microsoft Access file: another program (MultiTerm, or Studio itself) " +
                              "may be holding it exclusively, or this Studio cannot read it directly - Studio 2026 " +
                              "has no Access engine for .sdltb, so convert it to .ttb there."
                            : " Another program may be holding it, or it is damaged.";
                        return new TermListPage { Error = $"could not open {tb}: {reader.LastError}.{why}" };
                    }
                    return reader.ListConcepts(tb.SourceLang, tb.TargetLang, offset, limit, contains);
                }
            }
            catch (Exception ex)
            {
                return new TermListPage { Error = $"could not read {tb}: {ex.Message}" };
            }
        }

        /// <summary>Case-insensitive substring test used by every reader's
        /// 'contains' filter. Done in C# (registered as a SQLite function) rather
        /// than with LIKE, which folds case for ASCII only - "Ö" would not match "ö".</summary>
        public static bool Contains(string haystack, string needle)
            => !string.IsNullOrEmpty(haystack) && !string.IsNullOrEmpty(needle)
               && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>A Jet/ACE LIKE pattern matching <paramref name="text"/> anywhere.
        /// The wildcard characters are escaped by bracketing, Jet's only escape.</summary>
        public static string JetContainsPattern(string text)
        {
            var sb = new System.Text.StringBuilder("%");
            foreach (var ch in text ?? "")
            {
                if (ch == '[' || ch == '%' || ch == '_' || ch == '*' || ch == '?' || ch == '#')
                    sb.Append('[').Append(ch).Append(']');
                else
                    sb.Append(ch);
            }
            return sb.Append('%').ToString();
        }

        /// <summary>A SQLite timestamp ("2026-11-26 14:05:00", UTC) in the ISO
        /// form the MultiTerm dates already use ("2026-11-26T14:05:00").</summary>
        public static string IsoTimestamp(object raw)
        {
            if (raw == null || raw is DBNull) return null;
            if (raw is DateTime dt) return dt.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            var s = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrEmpty(s)) return null;
            return s.Length >= 19 && s[10] == ' ' ? s.Substring(0, 10) + "T" + s.Substring(11) : s;
        }

        private static readonly HashSet<string> DefinitionFields =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Definition" };
        private static readonly HashSet<string> DomainFields =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Subject", "Domain", "Subject field", "Subject area" };
        private static readonly HashSet<string> NoteFields =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Note", "Notes", "Comment", "Comments", "Remark" };

        /// <summary>
        /// One entry from MultiTerm's concept XML (mtConcepts.Text in a .ttb,
        /// mtConcepts.text in a .sdltb - the same compact form in both; see
        /// <see cref="MultiTermConceptXml"/>). Terms come from the language groups
        /// whose &lt;l lang/type&gt; is one of <paramref name="sourceTokens"/> /
        /// <paramref name="targetTokens"/> (the index name and its locale).
        /// Created is the entry's origination; modified is the LATEST modification
        /// of the entry or any of its terms, since a translator changing one term
        /// often leaves the entry-level stamp alone. A malformed blob yields an
        /// entry with its id and a note rather than an exception, so one bad
        /// entry never costs the page.
        /// </summary>
        public static BridgeTermListEntry FromConceptXml(
            long conceptId, string xml, ICollection<string> sourceTokens, ICollection<string> targetTokens)
        {
            var entry = new BridgeTermListEntry { Id = conceptId };
            if (string.IsNullOrWhiteSpace(xml)) return entry;

            XElement root;
            try { root = XElement.Parse(xml); }
            catch
            {
                entry.Notes = "(this entry's data could not be read)";
                return entry;
            }

            var definitions = new List<string>();
            var domains = new List<string>();
            var notes = new List<string>();
            var other = new List<string>();
            string modDate = null, modUser = null;

            // Entry-level transactions: direct trG children of the root.
            foreach (var trG in Children(root, "trG", "transacGrp"))
                ApplyTransaction(trG, entry, ref modDate, ref modUser, entryLevel: true);

            foreach (var el in root.Elements())
            {
                var local = el.Name.LocalName;
                if (Is(local, "dG", "descripGrp"))
                {
                    CollectField(el, null, definitions, domains, notes, other);
                }
                else if (Is(local, "lG", "languageGrp"))
                {
                    var l = el.Elements().FirstOrDefault(e => Is(e.Name.LocalName, "l", "language"));
                    var lang = (string)l?.Attribute("lang");
                    var langName = (string)l?.Attribute("type");
                    bool isSource = Matches(sourceTokens, lang, langName);
                    bool isTarget = !isSource && Matches(targetTokens, lang, langName);
                    var label = string.IsNullOrEmpty(langName) ? lang : langName;

                    foreach (var child in el.Elements())
                    {
                        var cl = child.Name.LocalName;
                        if (Is(cl, "dG", "descripGrp"))
                            CollectField(child, label, definitions, domains, notes, other);
                        else if (Is(cl, "tG", "termGrp"))
                        {
                            var t = child.Elements().FirstOrDefault(e => Is(e.Name.LocalName, "t", "term"));
                            var text = t?.Value?.Trim();
                            if (!string.IsNullOrEmpty(text))
                            {
                                if (isSource) entry.Source.Add(text);
                                else if (isTarget) entry.Target.Add(text);
                            }
                            foreach (var d in Children(child, "dG", "descripGrp"))
                                CollectField(d, label, definitions, domains, notes, other);
                            foreach (var trG in Children(child, "trG", "transacGrp"))
                                ApplyTransaction(trG, entry, ref modDate, ref modUser, entryLevel: false);
                        }
                    }
                }
            }

            entry.Definition = JoinOrNull(definitions);
            entry.Domain = JoinOrNull(domains);
            entry.Notes = JoinOrNull(notes);
            entry.Fields = other.Count > 0 ? other : null;
            entry.Modified = modDate;
            entry.ModifiedBy = modUser;
            return entry;
        }

        private static void ApplyTransaction(
            XElement trG, BridgeTermListEntry entry, ref string modDate, ref string modUser, bool entryLevel)
        {
            var tr = trG.Elements().FirstOrDefault(e => Is(e.Name.LocalName, "tr", "transac"));
            if (tr == null) return;
            var type = (string)tr.Attribute("type");
            var user = tr.Value?.Trim();
            var date = trG.Elements().FirstOrDefault(e => Is(e.Name.LocalName, "dt", "date"))?.Value?.Trim();

            if (string.Equals(type, "origination", StringComparison.OrdinalIgnoreCase))
            {
                // The entry's own origination wins (entry-level transactions are
                // read before any term's); a term's only fills a gap.
                if (entryLevel || entry.Created == null)
                {
                    entry.Created = date;
                    entry.CreatedBy = user;
                }
            }
            else if (string.Equals(type, "modification", StringComparison.OrdinalIgnoreCase))
            {
                // ISO dates compare correctly as strings.
                if (modDate == null || (date != null && string.CompareOrdinal(date, modDate) > 0))
                {
                    modDate = date;
                    modUser = user;
                }
            }
        }

        private static void CollectField(XElement dG, string language,
            List<string> definitions, List<string> domains, List<string> notes, List<string> other)
        {
            foreach (var d in dG.DescendantsAndSelf().Where(e => Is(e.Name.LocalName, "d", "descrip")))
            {
                var type = (string)d.Attribute("type");
                var value = d.Value?.Trim();
                if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(value)) continue;
                if (DefinitionFields.Contains(type)) AddDistinct(definitions, value);
                else if (DomainFields.Contains(type)) AddDistinct(domains, value);
                else if (NoteFields.Contains(type)) AddDistinct(notes, value);
                else AddDistinct(other, language == null ? $"{type}: {value}" : $"{type} ({language}): {value}");
            }
        }

        private static IEnumerable<XElement> Children(XElement parent, string compact, string verbose)
            => parent.Elements().Where(e => Is(e.Name.LocalName, compact, verbose));

        private static bool Is(string local, string compact, string verbose)
            => string.Equals(local, compact, StringComparison.OrdinalIgnoreCase)
               || string.Equals(local, verbose, StringComparison.OrdinalIgnoreCase);

        private static bool Matches(ICollection<string> tokens, string lang, string name)
        {
            if (tokens == null) return false;
            foreach (var t in tokens)
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (string.Equals(t, lang, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static void AddDistinct(List<string> list, string value)
        {
            if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
        }

        private static string JoinOrNull(List<string> parts) => parts.Count == 0 ? null : string.Join("; ", parts);
    }
}

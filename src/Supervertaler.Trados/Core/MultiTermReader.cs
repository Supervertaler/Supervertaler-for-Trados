using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Reads terms from a MultiTerm .sdltb termbase file (JET4/MDB format).
    /// Read-only access using System.Data.OleDb with Microsoft.ACE.OLEDB.12.0.
    ///
    /// Schema (discovered from real .sdltb files):
    ///   mtIndexes       — language index definitions (name, locale)
    ///   I_{IndexName}   — indexed terms per language (origterm, termid, conceptid, topterm)
    ///   mtConcepts      — concept XML with all languages, terms, and metadata
    ///   mtFields        — field definitions (name, type)
    ///   mtFieldsValues  — field values per entry
    /// </summary>
    public class MultiTermReader : IDisposable
    {
        private OleDbConnection _connection;
        private readonly string _filePath;
        private bool _disposed;

        /// <summary>
        /// OleDb provider versions to try, in order of preference.
        /// ACE drivers (Office/Access Database Engine): 16.0, 15.0, 14.0, 12.0.
        /// JET 4.0: Built into Windows for 32-bit processes — since Trados Studio 2024
        /// runs as x86 (Program Files (x86)), this should always be available without
        /// any additional driver installation.
        /// </summary>
        private static readonly string[] OleDbProviders =
        {
            "Microsoft.ACE.OLEDB.16.0",
            "Microsoft.ACE.OLEDB.15.0",
            "Microsoft.ACE.OLEDB.14.0",
            "Microsoft.ACE.OLEDB.12.0",
            "Microsoft.Jet.OLEDB.4.0"
        };

        public string LastError { get; private set; }
        public string TermbaseName { get; private set; }

        /// <summary>
        /// Name of the ACE OLEDB provider that was successfully used to open the file.
        /// Null if the file hasn't been opened yet.
        /// </summary>
        public string UsedProvider { get; private set; }

        public MultiTermReader(string sdltbPath)
        {
            _filePath = sdltbPath ?? throw new ArgumentNullException(nameof(sdltbPath));
            TermbaseName = Path.GetFileNameWithoutExtension(sdltbPath);
        }

        /// <summary>
        /// Opens the .sdltb file in read-only mode.
        /// Tries multiple ACE OLEDB driver versions (16.0, 15.0, 14.0, 12.0).
        /// Returns false if no compatible driver is installed or the file cannot be opened.
        /// </summary>
        public bool Open()
        {
            if (!File.Exists(_filePath))
            {
                LastError = $"File not found: {_filePath}";
                return false;
            }

            var triedProviders = new List<string>();

            foreach (var provider in OleDbProviders)
            {
                try
                {
                    var connStr = $"Provider={provider};Data Source={_filePath};Mode=Read;";
                    _connection = new OleDbConnection(connStr);
                    _connection.Open();
                    UsedProvider = provider;
                    return true;
                }
                catch (InvalidOperationException)
                {
                    // This provider version isn't registered — try the next one
                    triedProviders.Add(provider + " (not registered)");
                    _connection?.Dispose();
                    _connection = null;
                }
                catch (OleDbException ex)
                {
                    // Driver is registered but can't open the file — report and try next
                    triedProviders.Add($"{provider} ({ex.Message})");
                    _connection?.Dispose();
                    _connection = null;
                }
                catch (Exception ex)
                {
                    triedProviders.Add($"{provider} ({ex.GetType().Name}: {ex.Message})");
                    _connection?.Dispose();
                    _connection = null;
                }
            }

            LastError = "No compatible OleDb provider found for .sdltb files. " +
                        $"Tried: {string.Join(", ", triedProviders)}. " +
                        "Install the Access Database Engine from https://www.microsoft.com/en-us/download/details.aspx?id=54920";
            return false;
        }

        /// <summary>
        /// Returns the language indexes defined in this termbase.
        /// Each entry is (IndexName, LocaleCode), e.g. ("English", "EN"), ("Dutch", "NL").
        /// </summary>
        public List<(string Name, string Locale)> GetLanguageIndexes()
        {
            var result = new List<(string, string)>();
            if (_connection == null) return result;

            try
            {
                using (var cmd = new OleDbCommand("SELECT [name], [locale] FROM mtIndexes", _connection))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var name = reader.GetString(0);
                        var locale = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        result.Add((name, locale));
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = $"Error reading mtIndexes: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// Bulk-loads all term pairs for the specified source/target language indexes.
        /// Uses the I_{indexName} tables for fast enumeration, then parses concept XML
        /// from mtConcepts for cross-language term extraction.
        ///
        /// Returns a TermMatcher-compatible index dictionary.
        /// </summary>
        public Dictionary<string, List<TermEntry>> LoadAllTerms(
            string sourceIndexName, string targetIndexName,
            long termbaseId, string termbaseName)
        {
            var index = new Dictionary<string, List<TermEntry>>(StringComparer.OrdinalIgnoreCase);
            if (_connection == null) return index;

            try
            {
                // Verify that the source and target index tables exist
                if (!TableExists($"I_{sourceIndexName}") || !TableExists($"I_{targetIndexName}"))
                {
                    LastError = $"Index table not found for '{sourceIndexName}' or '{targetIndexName}'";
                    return index;
                }

                // Step 1: Load all source terms grouped by conceptId
                // Key: conceptId → List of (origterm, termid)
                var sourceTermsByConcept = new Dictionary<int, List<(string term, int termid)>>();
                using (var cmd = new OleDbCommand(
                    $"SELECT conceptid, origterm, termid FROM [I_{sourceIndexName}]", _connection))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var conceptId = reader.GetInt32(0);
                        var term = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        var termid = reader.GetInt32(2);

                        if (string.IsNullOrWhiteSpace(term)) continue;

                        if (!sourceTermsByConcept.TryGetValue(conceptId, out var list))
                        {
                            list = new List<(string, int)>();
                            sourceTermsByConcept[conceptId] = list;
                        }
                        list.Add((term, termid));
                    }
                }

                if (sourceTermsByConcept.Count == 0) return index;

                // Step 2: Load all target terms grouped by conceptId
                var targetTermsByConcept = new Dictionary<int, List<(string term, int termid)>>();
                using (var cmd = new OleDbCommand(
                    $"SELECT conceptid, origterm, termid FROM [I_{targetIndexName}]", _connection))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var conceptId = reader.GetInt32(0);
                        var term = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        var termid = reader.GetInt32(2);

                        if (string.IsNullOrWhiteSpace(term)) continue;

                        if (!targetTermsByConcept.TryGetValue(conceptId, out var list))
                        {
                            list = new List<(string, int)>();
                            targetTermsByConcept[conceptId] = list;
                        }
                        list.Add((term, termid));
                    }
                }

                // Step 3: Optionally load concept XML for definitions/notes
                var definitions = LoadDefinitions(sourceTermsByConcept.Keys);

                // Step 4: Build TermEntry objects and index them
                long entryIdCounter = 0;
                foreach (var kvp in sourceTermsByConcept)
                {
                    var conceptId = kvp.Key;
                    var sourceTerms = kvp.Value;

                    if (!targetTermsByConcept.TryGetValue(conceptId, out var targetTerms))
                        continue; // No target terms for this concept

                    var primaryTarget = targetTerms[0].term;
                    var targetSynonyms = targetTerms.Skip(1).Select(t => t.term).ToList();

                    string definition = null;
                    definitions?.TryGetValue(conceptId, out definition);

                    // Create an entry for each source term (primary + synonyms)
                    // The first source term is the primary; others are source synonyms
                    var primarySource = sourceTerms[0].term;

                    var entry = new TermEntry
                    {
                        Id = termbaseId * -100000 - (++entryIdCounter),
                        SourceTerm = primarySource,
                        TargetTerm = primaryTarget,
                        TargetSynonyms = targetSynonyms,
                        TermbaseId = termbaseId,
                        TermbaseName = termbaseName,
                        Definition = definition,
                        IsMultiTerm = true,
                        Ranking = 50 // Default ranking for MultiTerm entries
                    };

                    // Index the primary source term
                    AddToIndex(index, primarySource, entry);

                    // Index source synonyms (additional source terms in the same concept)
                    for (int i = 1; i < sourceTerms.Count; i++)
                    {
                        AddToIndex(index, sourceTerms[i].term, entry);
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = $"Error loading terms: {ex.Message}";
            }

            return index;
        }

        /// <summary>
        /// Returns metadata about this termbase for display in the settings grid.
        /// </summary>
        public MultiTermTermbaseInfo GetTermbaseInfo(
            string sourceIndexName, string targetIndexName, long syntheticId)
        {
            int termCount = 0;
            try
            {
                if (TableExists($"I_{sourceIndexName}"))
                {
                    using (var cmd = new OleDbCommand(
                        $"SELECT COUNT(*) FROM [I_{sourceIndexName}]", _connection))
                    {
                        termCount = (int)cmd.ExecuteScalar();
                    }
                }
            }
            catch { /* ignore count errors */ }

            return new MultiTermTermbaseInfo
            {
                SyntheticId = syntheticId,
                FilePath = _filePath,
                Name = TermbaseName,
                SourceIndexName = sourceIndexName,
                TargetIndexName = targetIndexName,
                TermCount = termCount,
                LoadMode = MultiTermLoadMode.DirectAccess
            };
        }

        /// <summary>
        /// Loads definitions from concept XML for the given concept IDs.
        /// Only parses XML for concepts that have descriptive fields.
        /// Returns conceptId → definition text, or null if mtConcepts is inaccessible.
        /// </summary>
        private Dictionary<int, string> LoadDefinitions(IEnumerable<int> conceptIds)
        {
            var result = new Dictionary<int, string>();

            try
            {
                if (!TableExists("mtConcepts")) return null;

                // Load all concept XML in one query
                using (var cmd = new OleDbCommand("SELECT conceptid, [text] FROM mtConcepts", _connection))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var conceptId = reader.GetInt32(0);
                        if (reader.IsDBNull(1)) continue;

                        var xml = reader.GetString(1);
                        if (string.IsNullOrEmpty(xml) || !xml.Contains("<dG>"))
                            continue;

                        try
                        {
                            var doc = XElement.Parse(xml);
                            // Look for definition at concept level: <dG><d type="Definition">text</d></dG>
                            var defElement = doc.Descendants("d")
                                .FirstOrDefault(d => (string)d.Attribute("type") == "Definition");
                            if (defElement != null)
                                result[conceptId] = defElement.Value;
                        }
                        catch
                        {
                            // Skip malformed XML
                        }
                    }
                }
            }
            catch
            {
                return null;
            }

            return result;
        }

        private bool TableExists(string tableName)
        {
            try
            {
                var schema = _connection.GetOleDbSchemaTable(
                    OleDbSchemaGuid.Tables,
                    new object[] { null, null, tableName, "TABLE" });
                return schema != null && schema.Rows.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Adds a term entry to the index under the normalized key and stripped variant.
        /// Matches the key normalization used by TermbaseReader.LoadAllTerms().
        /// </summary>
        private static void AddToIndex(Dictionary<string, List<TermEntry>> index,
            string sourceTerm, TermEntry entry)
        {
            var key = sourceTerm.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key)) return;

            if (!index.TryGetValue(key, out var list))
            {
                list = new List<TermEntry>();
                index[key] = list;
            }
            list.Add(entry);

            // Also index by punctuation-stripped variant
            var stripped = key.TrimEnd('.', '!', '?', ',', ';', ':');
            if (stripped != key && stripped.Length > 0)
            {
                if (!index.TryGetValue(stripped, out var strippedList))
                {
                    strippedList = new List<TermEntry>();
                    index[stripped] = strippedList;
                }
                strippedList.Add(entry);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _connection?.Close();
                _connection?.Dispose();
            }
            catch { /* ignore close errors */ }
        }
    }
}

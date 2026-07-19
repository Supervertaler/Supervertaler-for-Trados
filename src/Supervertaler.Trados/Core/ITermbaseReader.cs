using System;
using System.Collections.Generic;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Common contract for a read-only file-based termbase reader. Implemented
    /// by <see cref="MultiTermReader"/> (.sdltb, JET/ACE OleDb) and
    /// <see cref="TtbReader"/> (.ttb, SQLite + FTS5).
    ///
    /// Use <see cref="TermbaseReaderFactory.Open"/> to dispatch on file extension
    /// rather than constructing a specific reader directly.
    /// </summary>
    internal interface ITermbaseReader : IDisposable
    {
        string LastError { get; }
        bool Open();
        Dictionary<string, List<TermEntry>> LoadAllTerms(
            string sourceIndexName, string targetIndexName,
            long termbaseId, string termbaseName);
        MultiTermTermbaseInfo GetTermbaseInfo(
            string sourceIndexName, string targetIndexName, long syntheticId);

        /// <summary>
        /// Loads the full concept-oriented content of the termbase (all languages,
        /// all concepts with their terms grouped by language, and concept-level
        /// descriptive fields) as a reader-agnostic <see cref="ImportedTermbase"/>.
        /// Used by the "import external termbase" feature; kept separate from the
        /// lightweight <see cref="LoadAllTerms"/> path used by live TermLens matching.
        /// </summary>
        ImportedTermbase LoadForImport();
    }

    /// <summary>
    /// Dispatches a file path to the correct concrete termbase reader by extension.
    /// </summary>
    internal static class TermbaseReaderFactory
    {
        public static ITermbaseReader Create(string filePath)
        {
            if (filePath != null &&
                filePath.EndsWith(".ttb", StringComparison.OrdinalIgnoreCase))
            {
                return new TtbReader(filePath);
            }
            // Default: treat as legacy MultiTerm .sdltb
            return new MultiTermReader(filePath);
        }
    }
}

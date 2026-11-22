using System;
using System.Collections.Generic;
using Supervertaler.Core.SuperVault;

namespace Supervertaler.Trados.SuperVault
{
    /// <summary>
    /// One EverTM Matches lookup: what the pane copied from the editor on the UI thread, and what it gets back. Runs on
    /// the pane's worker (<see cref="EverTmLookupWorker{TRequest,TResult}"/>), never on Studio's UI thread, and touches
    /// no Studio object. Public for the checks in tools/SuperVaultCheck.
    /// </summary>
    public static class EverTmMatchesLookup
    {
        /// <summary>Shows a little more than Translation Results' usual 70% (design, section 2).</summary>
        public const int MinScore = 50;
        /// <summary>Newest-wins already removes repeats, so 20 is plenty.</summary>
        public const int MaxMatches = 20;

        public sealed class Request
        {
            public List<VaultPiece> Document;
            public string SourceLang, TargetLang;       // base languages ("nl", "en")
            public string SourceCulture, TargetCulture;  // the project's, with their variants ("nl-BE", "en-GB")
        }

        public sealed class Answer
        {
            public List<VaultMatch> Matches = new List<VaultMatch>();
            public long Units;
            /// <summary>Set when there is no usable index: what the status line says instead of a count.</summary>
            public string Problem;
            /// <summary>"Indexing: 412,000 of 871,290 translation units" while a rebuild runs; lookups still use the old index.</summary>
            public string Building;
        }

        /// <summary>
        /// Looks the segment up. A missing, empty, broken or rebuilding EverTM comes back as <see cref="Answer.Problem"/>
        /// or <see cref="Answer.Building"/>, never as an empty list that looks like "no matches". Units confirmed in the
        /// last minute and not yet stored are found too (<see cref="VaultLibrary.Lookup"/>).
        /// </summary>
        public static Answer Run(VaultLibrary library, Request request)
        {
            if (library == null) throw new ArgumentNullException(nameof(library));
            var answer = new Answer();
            var status = library.Status;
            answer.Units = status.Units;
            if (status.Building) answer.Building = status.BuildProgress;
            if (status.State == VaultState.Broken) { answer.Problem = status.Message; return answer; }
            if (request?.Document == null || request.Document.Count == 0) return answer;
            // Missing: there may still be units just confirmed, which Lookup finds without an index.
            answer.Matches = library.Lookup(request.Document, request.SourceLang, request.TargetLang, MinScore, MaxMatches, request.TargetCulture)
                             ?? new List<VaultMatch>();
            if (status.State == VaultState.Missing && answer.Matches.Count == 0) answer.Problem = status.Message;
            return answer;
        }
    }
}

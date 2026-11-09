using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sdl.Core.Globalization;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.Core.EditDistance;
using Sdl.LanguagePlatform.Core.Tokenization;
using Sdl.LanguagePlatform.TranslationMemory;
using Supervertaler.Core.SuperVault;

namespace Supervertaler.Trados.SuperVault
{
    /// <summary>
    /// Turns SuperVault matches into Studio search results, using only Studio's public types: Segment, Tag, the token
    /// classes and EditDistance. Studio's own tokenizer and scorer are in assemblies a plugin may not reference.
    /// </summary>
    internal static class SuperVaultResults
    {
        /// <summary>
        /// Set on every SuperVault result so that Studio ranks it below any other TM's exact match of the same score.
        ///
        /// Why: when two 100% matches differ, Studio lowers EVERY exact match by its "multiple translations" penalty,
        /// the client TM's included, unless the top one is "visibly better" than the next (Studio's
        /// <c>SearchResults.CheckForMultipleTranslations</c>). Fewer text replacements or fewer placeable format changes
        /// count as visibly better, and these two fields are read by nothing else in Studio (no display, no Analyze).
        /// So a SuperVault match still shows 100% on its own, and a client TM's 100% is never lowered by one: proven
        /// with Studio's own Cascade and a real file TM, in Studio 2024 and 2026 (tools/SuperVaultCheck).
        /// </summary>
        internal const int YieldRank = 1000;

        /// <summary>The document segment as pieces, one per element, so piece indexes are element indexes.</summary>
        public static List<VaultPiece> Pieces(Segment segment)
        {
            var pieces = new List<VaultPiece>();
            if (segment?.Elements == null) return pieces;
            for (int i = 0; i < segment.Elements.Count; i++)
            {
                var e = segment.Elements[i];
                if (e is Tag t)
                {
                    var kind = t.Type == TagType.Start ? VaultTagKind.Open : t.Type == TagType.End ? VaultTagKind.Close : VaultTagKind.Standalone;
                    pieces.Add(new VaultPiece { Kind = kind, Name = "doc", Key = "doc#" + i, Raw = t.ToString() });
                }
                else if (e is Text x) pieces.Add(new VaultPiece { Text = x.Value ?? "" });
                else pieces.Add(new VaultPiece { Text = e?.ToString() ?? "" });
            }
            return pieces;
        }

        /// <summary>A Studio segment from match pieces: text as Text, each mapped tag as a copy of the document's own tag.</summary>
        public static Segment ToSegment(CultureCode culture, List<VaultPiece> pieces, List<int> docTag, Segment document)
        {
            var s = new Segment(culture);
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                if (!p.IsTag) { s.Elements.Add(new Text(p.Text)); continue; }
                var tag = docTag != null && i < docTag.Count && docTag[i] >= 0 ? document.Elements[docTag[i]] as Tag : null;
                // A tag piece always has a document tag here (unmapped ones were dropped); keep the element count equal regardless.
                s.Elements.Add(tag != null ? (SegmentElement)tag.Duplicate() : new Text(""));
            }
            return s;
        }

        /// <summary>Studio tokens for our tokens, with spans into <paramref name="segment"/> (whose elements are the pieces).</summary>
        public static List<Token> ToTokens(List<VaultToken> tokens, Segment segment)
        {
            var list = new List<Token>(tokens.Count);
            foreach (var t in tokens)
            {
                Token token;
                if (t.Kind == VaultTokenKind.Tag && segment.Elements[t.Piece] is Tag tag) token = new TagToken(tag);
                else
                {
                    // Numbers stay plain words: Studio's NumberToken carries a parsed value we don't compute.
                    var type = t.Kind == VaultTokenKind.Word ? TokenType.Word : t.Kind == VaultTokenKind.Space ? TokenType.Whitespace : TokenType.GeneralPunctuation;
                    token = new SimpleToken(t.Text, type);
                }
                int into = t.Length > 0 ? t.Offset + t.Length - 1 : t.Offset;
                token.Span = new SegmentRange(t.Piece, t.Offset, into);
                token.Culture = segment.Culture;
                list.Add(token);
            }
            return list;
        }

        /// <summary>The alignment as Studio's EditDistance: Source = document token, Target = memory token, as Studio's own TMs do.</summary>
        public static EditDistance ToEditDistance(VaultDiffResult diff, int documentTokens, int memoryTokens)
        {
            if (diff?.Edits == null) return null;
            var ed = new EditDistance(documentTokens, memoryTokens, diff.Distance);
            foreach (var e in diff.Edits)
            {
                var op = e.Op == VaultEditOp.Identity ? EditOperation.Identity : e.Op == VaultEditOp.Change ? EditOperation.Change
                       : e.Op == VaultEditOp.Insert ? EditOperation.Insert : EditOperation.Delete;
                ed.Add(new EditDistanceItem { Operation = op, Source = e.A, Target = e.B, Costs = e.Cost, Resolution = op == EditOperation.Change ? EditDistanceResolution.Substitution : EditDistanceResolution.None });
            }
            return ed;
        }

        /// <summary>
        /// A Studio search result for a TM lookup match. Penalties are the project's own, from the search settings, as
        /// a Studio TM would apply them: the provider penalty set for SuperVault in the project, and the formatting
        /// penalty when the document has tags the match can't carry.
        /// </summary>
        public static SearchResult Lookup(VaultMatch m, Segment document, Segment tokenizedDocument, CultureCode source, CultureCode target, SearchSettings settings)
        {
            var src = ToSegment(source, m.Source, m.SourceDocTag, document);
            var trg = ToSegment(target, m.Target, m.TargetDocTag, document);
            src.Tokens = ToTokens(m.SourceTokens, src);
            trg.Tokens = ToTokens(VaultDiff.Tokenize(m.Target), trg);

            var scoring = new ScoringResult
            {
                BaseScore = m.Score,
                EditDistance = ToEditDistance(m.Diff, tokenizedDocument?.Tokens?.Count ?? 0, src.Tokens.Count),
                TextReplacements = YieldRank,
                PlaceableFormatChanges = YieldRank,
            };
            var result = Make(src, trg, m, scoring);
            ApplyPenalty(result, settings, PenaltyType.ProviderPenalty, 0);
            bool documentHasTags = false;
            foreach (var e in document.Elements) if (e is Tag) { documentHasTags = true; break; }
            if (m.Tags == VaultTagOutcome.Unmapped && documentHasTags)
                ApplyPenalty(result, settings, PenaltyType.TagMismatch, 1);   // the project's "different formatting" penalty, else 1%
            return result;
        }

        /// <summary>A Studio search result for a concordance match. Always plain text.</summary>
        public static SearchResult Concordance(Segment src, Segment trg, VaultMatch m)
        {
            var scoring = new ScoringResult { BaseScore = m.Score, TextReplacements = YieldRank, PlaceableFormatChanges = YieldRank };
            // The searched words, for Studio to highlight. Our concordance segments are one Text element each (plain
            // text, see SuperVaultLanguageDirection.Plain), so element 0, with Into inclusive as in a token's Span.
            if (m.Hits != null && m.Hits.Count > 0)
                scoring.MatchingConcordanceRanges = m.Hits.Select(h => new SegmentRange(0, h.Offset, h.Offset + Math.Max(1, h.Length) - 1)).ToList();
            return Make(src, trg, m, scoring);
        }

        static SearchResult Make(Segment src, Segment trg, VaultMatch m, ScoringResult scoring)
        {
            var now = DateTime.UtcNow;
            int.TryParse(m.Candidate.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id);
            var tu = new TranslationUnit(src, trg)
            {
                Origin = TranslationUnitOrigin.TM,
                OriginSystem = "EverTM",
                ConfirmationLevel = ConfirmationLevel.Translated,
                ResourceId = new PersistentObjectToken(id, Guid.Empty),
                SystemFields = new SystemFields { CreationDate = now, ChangeDate = now, UseDate = now, CreationUser = "EverTM", ChangeUser = "EverTM", UseUser = "EverTM" },
                FieldValues = new FieldValues(),
            };
            return new SearchResult(tu) { ScoringResult = scoring, TranslationProposal = new TranslationUnit(tu) };
        }

        static void ApplyPenalty(SearchResult r, SearchSettings settings, PenaltyType type, int fallback)
        {
            var p = settings?.FindPenalty(type);
            int malus = p != null ? p.Malus : fallback;
            if (malus > 0) r.ScoringResult.ApplyPenalty(new Penalty(type, malus));
        }
    }
}

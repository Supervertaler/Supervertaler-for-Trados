using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Supervertaler.Core
{
    /// <summary>
    /// Keyword-based domain detection and document statistics.
    /// Analyzes source segments to determine the document's domain
    /// (patent, legal, medical, technical, financial, marketing, or general).
    /// No AI calls – pure heuristic analysis.
    /// </summary>
    public static class DocumentAnalyzer
    {
        // ─── Domain keyword dictionaries ──────────────────────────────

        private static readonly Dictionary<string, DomainSignature> DomainSignatures =
            new Dictionary<string, DomainSignature>(StringComparer.OrdinalIgnoreCase)
            {
                ["patent"] = new DomainSignature
                {
                    Keywords = new[] {
                        "invention", "claim", "embodiment", "apparatus", "comprising",
                        "wherein", "patent", "prior art", "novelty", "figure", "drawing",
                        "applicant", "inventor", "compound", "formula", "characterized",
                        "uitvinding", "conclusie", "uitvoeringsvorm", "omvattende", "waarbij",
                        "stand der techniek", "octrooiaanvraag", "figuur", "tekening"
                    },
                    Patterns = new[] {
                        @"Figure\s+\d+", @"claim\s+\d+", @"Fig\.\s*\d+[A-Z]?",
                        @"\([IVX]+\)", @"EP\s*\d{7}", @"WO\s*\d{4}/\d{6}",
                        @"conclusie\s+\d+", @"Figuur\s+\d+"
                    }
                },
                ["legal"] = new DomainSignature
                {
                    Keywords = new[] {
                        "contract", "agreement", "party", "clause", "hereby", "whereas",
                        "pursuant", "liability", "jurisdiction", "arbitration", "plaintiff",
                        "defendant", "court", "regulation", "statute", "breach", "damages",
                        "overeenkomst", "partij", "clausule", "aansprakelijkheid", "rechtbank"
                    },
                    Patterns = new[] {
                        @"§\s*\d+", @"Article\s+\d+", @"Section\s+\d+\.\d+",
                        @"Artikel\s+\d+", @"\bArtt?\.\s*\d+"
                    }
                },
                ["medical"] = new DomainSignature
                {
                    Keywords = new[] {
                        "patient", "diagnosis", "treatment", "medication", "clinical",
                        "hospital", "surgery", "therapeutic", "pharmaceutical", "disease",
                        "symptom", "therapy", "prescription", "dosage", "pathology",
                        "patiënt", "diagnose", "behandeling", "medicatie", "klinisch"
                    },
                    Patterns = new[] {
                        @"\d+\s*mg", @"\d+\s*ml", @"ICD-\d+", @"ATC\s+[A-Z]\d{2}"
                    }
                },
                ["technical"] = new DomainSignature
                {
                    Keywords = new[] {
                        "system", "configuration", "parameter", "interface", "protocol",
                        "module", "component", "specification", "algorithm", "implementation",
                        "hardware", "software", "network", "database", "server",
                        "systeem", "configuratie", "parameter", "specificatie", "implementatie"
                    },
                    Patterns = new[] {
                        @"\d+\.\d+\.\d+", @"\w+\(\)", @"[A-Z_]{4,}", @"https?://"
                    }
                },
                ["financial"] = new DomainSignature
                {
                    Keywords = new[] {
                        "investment", "revenue", "profit", "asset", "liability", "equity",
                        "fiscal", "budget", "expense", "income", "balance", "accounting",
                        "audit", "dividend", "portfolio", "securities", "capital",
                        "investering", "omzet", "winst", "begroting", "balans"
                    },
                    Patterns = new[] {
                        @"[\$€£]\s*[\d,]+", @"\d+\.\d+\s*%", @"Q[1-4]\s+\d{4}",
                        @"IFRS", @"GAAP"
                    }
                },
                ["marketing"] = new DomainSignature
                {
                    Keywords = new[] {
                        "brand", "customer", "campaign", "audience", "engagement",
                        "strategy", "creative", "promotion", "sales", "consumer",
                        "advertising", "content", "experience", "conversion",
                        "merk", "klant", "campagne", "doelgroep", "strategie"
                    },
                    Patterns = new[] {
                        @"[®™©]", @"\d+%\s+(?:more|less|increase|decrease|meer|minder)"
                    }
                }
            };

        // ─── Public API ──────────────────────────────────────────────

        /// <summary>
        /// Analyzes a list of source segments and returns domain detection results.
        /// </summary>
        public static DocumentAnalysis Analyze(List<string> sourceSegments)
        {
            if (sourceSegments == null || sourceSegments.Count == 0)
                return new DocumentAnalysis { PrimaryDomain = "general" };

            var fullText = string.Join(" ", sourceSegments);
            var wordCount = fullText.Split(new[] { ' ', '\t', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries).Length;

            // Score each domain
            var scores = new Dictionary<string, double>();
            foreach (var kvp in DomainSignatures)
            {
                double score = 0;

                // Keyword hits
                foreach (var kw in kvp.Value.Keywords)
                {
                    var count = CountOccurrences(fullText, kw);
                    score += count;
                }

                // Pattern hits (weighted 2x)
                foreach (var pattern in kvp.Value.Patterns)
                {
                    try
                    {
                        var matches = Regex.Matches(fullText, pattern, RegexOptions.IgnoreCase);
                        score += matches.Count * 2;
                    }
                    catch { /* skip invalid regex */ }
                }

                // Normalize by word count to avoid bias toward longer documents
                if (wordCount > 0)
                    score = score / Math.Sqrt(wordCount) * 100;

                scores[kvp.Key] = score;
            }

            // Pick top domain
            var sorted = scores.OrderByDescending(s => s.Value).ToList();
            var primary = sorted.Count > 0 && sorted[0].Value > 1.0
                ? sorted[0].Key : "general";
            var secondary = sorted.Count > 1 && sorted[1].Value > 1.0 &&
                            sorted[1].Value >= sorted[0].Value * 0.5
                ? sorted[1].Key : null;

            // Tone detection
            var formalMarkers = new[] { "whereas", "hereby", "pursuant", "thereof",
                "aforementioned", "notwithstanding", "comprising", "wherein" };
            var formalCount = formalMarkers.Sum(m => CountOccurrences(fullText, m));
            var tone = formalCount > 3 ? "formal" : "neutral";

            return new DocumentAnalysis
            {
                PrimaryDomain = primary,
                SecondaryDomain = secondary,
                SegmentCount = sourceSegments.Count,
                WordCount = wordCount,
                Tone = tone,
                DomainScores = scores
            };
        }

        private static int CountOccurrences(string text, string keyword)
        {
            var count = 0;
            var idx = 0;
            while ((idx = text.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                idx += keyword.Length;
            }
            return count;
        }

        // ─── Supporting types ─────────────────────────────────────────

        private class DomainSignature
        {
            public string[] Keywords;
            public string[] Patterns;
        }
    }

    /// <summary>
    /// Results of document analysis – domain, statistics, tone.
    /// </summary>
    public class DocumentAnalysis
    {
        public string PrimaryDomain { get; set; }
        public string SecondaryDomain { get; set; }
        public int SegmentCount { get; set; }
        public int WordCount { get; set; }
        public string Tone { get; set; }
        public Dictionary<string, double> DomainScores { get; set; }

        /// <summary>
        /// Returns a human-readable summary for display in the chat.
        /// </summary>
        public string ToSummary()
        {
            var domain = char.ToUpper(PrimaryDomain[0]) + PrimaryDomain.Substring(1);
            if (!string.IsNullOrEmpty(SecondaryDomain))
                domain += " / " + char.ToUpper(SecondaryDomain[0]) + SecondaryDomain.Substring(1);
            return $"Domain: {domain} | {SegmentCount:N0} segments | {WordCount:N0} words | Tone: {Tone}";
        }
    }
}

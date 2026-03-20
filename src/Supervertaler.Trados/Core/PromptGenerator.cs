using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Builds a meta-prompt that instructs the AI to generate a comprehensive,
    /// domain-specific translation prompt. Ported from Supervertaler Workbench's
    /// unified_prompt_manager_qt.py.
    /// </summary>
    internal static class PromptGenerator
    {
        // ─── Domain templates ────────────────────────────────────────

        private static readonly Dictionary<string, DomainTemplate> DomainTemplates =
            new Dictionary<string, DomainTemplate>(StringComparer.OrdinalIgnoreCase)
            {
                ["patent"] = new DomainTemplate
                {
                    Role = "Senior patent translator specializing in intellectual property, " +
                           "patent prosecution, and technical patent documentation. " +
                           "Deep expertise in EPO/PCT filings, claim drafting conventions, " +
                           "and mechanical/electromechanical/chemical patent terminology.",
                    Rules = new[]
                    {
                        "Translate claims exactly, preserving dependency chains (independent/dependent claim relationships)",
                        "Maintain patent-specific open-ended language: \"comprising\" (open-ended), never \"consisting of\" unless source explicitly uses limiting language",
                        "Preserve all reference numerals, figure references (Fig. 1, Figure 2A), and part numbers exactly as written",
                        "Never paraphrase, simplify, or improve source text — patents require exact semantic equivalence",
                        "Preserve formal patent register: \"wherein\", \"thereof\", \"hereinafter\", \"person skilled in the art\"",
                        "Maintain claim numbering, cross-references, and dependency structure without alteration",
                        "Use gerund constructions naturally: \"An example is replacing...\" NOT \"An example is the replacing of...\"",
                        "Preserve all prior art document references verbatim (e.g., US 20130183090, EP 2923344)",
                        "Maintain the hierarchical structure: TECHNICAL FIELD > PRIOR ART > SUMMARY > DRAWINGS > DETAILED DESCRIPTION > CLAIMS > ABSTRACT",
                        "When source is long, repetitive, or awkward, reproduce it faithfully — every word in a patent is legally operative"
                    },
                    Sections = new[]
                    {
                        "ROLE (senior patent translator with specific expertise areas)",
                        "SCOPE OF APPLICATION (project context: invention type, technology field, patent number if known)",
                        "TRANSLATION MANDATE (NON-NEGOTIABLE) — pure translation only, explicitly forbid improvement, simplification, harmonization, correction, streamlining",
                        "HARD CONSTRAINT: NO HALLUCINATED TRUNCATION — never omit repetitive phrases, collapse clauses, shorten lists, simplify enumerations, or \"fix\" grammar",
                        "CORE EXECUTION PRINCIPLES — with ABSOLUTE REQUIREMENTS (checkmarks) and ABSOLUTE PROHIBITIONS (crosses)",
                        "SUPERVERTALER INPUT HANDLING — translate only provided segment, preserve exact order, do not rely on unseen context",
                        "TRANSLATION STYLE (LOCKED) — mandatory term mappings",
                        "CLAIM TRANSLATION STYLE — preserve dependency structure, maintain phrasing, avoid stylistic smoothing",
                        "GERUND STYLE RULE — prefer natural English gerund over \"the [verb]ing of\" construction",
                        "TERMINOLOGY CONSISTENCY HIERARCHY — (1) Previous correct translations, (2) Project-specific glossary, (3) General mandatory mappings",
                        "TECHNICAL AND MECHANICAL FORMATTING RULES — dimensions, figure refs, prior art numbers, standard abbreviations",
                        "PREFLIGHT SELF-CHECK (MANDATORY) — verify every word translated, no compression, all values intact",
                        "POST-TRANSLATION INTEGRITY ASSERTION (MANDATORY)",
                        "PROJECT CONTEXT (for model understanding only — do not output)",
                        "PROJECT-SPECIFIC GLOSSARY (MANDATORY, LOCKED)",
                        "PREVIOUS CORRECT TRANSLATIONS",
                        "OUTPUT FORMAT"
                    },
                    Special = "Patent translation demands ABSOLUTE fidelity. Every word, repetition, structure, " +
                              "dimension, and cross-reference is legally operative. Deviation from literal structure " +
                              "constitutes a critical error. If the source text is long, repetitive, or awkward, " +
                              "reproduce it faithfully in the target language."
                },

                ["legal"] = new DomainTemplate
                {
                    Role = "Senior legal translator specializing in comparative law, contract law, " +
                           "corporate law, and cross-jurisdictional legal translation. " +
                           "Deep expertise in civil law and common law systems, notarial acts, and regulatory texts.",
                    Rules = new[]
                    {
                        "Maintain exact legal terminology — never substitute informal equivalents",
                        "Preserve legal entity types and abbreviations (BV, NV, GmbH, Ltd, Inc., SA, SARL) without translation",
                        "Preserve statutory references, article numbers, and legal citations exactly as written",
                        "Maintain formal legal register: \"hereby\", \"pursuant to\", \"notwithstanding\", \"whereas\"",
                        "Preserve all dates, deadlines, and procedural time limits without alteration",
                        "Distinguish between common law and civil law terminology as appropriate for the target jurisdiction",
                        "Preserve Latin legal terms (bona fide, inter alia, prima facie) unless target convention replaces them",
                        "Never translate proper names of laws, statutes, or regulations — retain original with optional translation in parentheses",
                        "Maintain contractual numbering, clause references, and article structure exactly"
                    },
                    Sections = new[]
                    {
                        "ROLE (senior legal translator with jurisdiction expertise)",
                        "LEGAL FRAMEWORK (jurisdiction, legal system type, document type)",
                        "TRANSLATION MANDATE (NON-NEGOTIABLE) — faithful legal translation, no interpretation or simplification",
                        "HARD CONSTRAINT: NO HALLUCINATED TRUNCATION — every clause, proviso, and exception is legally operative",
                        "CORE EXECUTION PRINCIPLES — absolute requirements and prohibitions",
                        "LEGAL REGISTER REQUIREMENTS — formality, precision, no colloquial language",
                        "LEGAL ENTITY AND TITLE HANDLING — preservation rules for entities, titles, proper names",
                        "STATUTORY REFERENCE PRESERVATION — article numbers, law names, citations",
                        "TERMINOLOGY CONSISTENCY HIERARCHY",
                        "NUMBER, DATE & LOCALISATION RULES",
                        "PREFLIGHT SELF-CHECK (MANDATORY)",
                        "PROJECT CONTEXT — document type, parties, jurisdiction, subject matter",
                        "PROJECT-SPECIFIC GLOSSARY (MANDATORY, LOCKED)",
                        "PREVIOUS CORRECT TRANSLATIONS",
                        "OUTPUT FORMAT"
                    },
                    Special = "Legal translation demands EXACT fidelity. Every clause, proviso, condition, " +
                              "and exception carries legal weight. Never simplify, merge, or \"improve\" legal drafting. " +
                              "Ambiguity in the source must be preserved as ambiguity in the target."
                },

                ["medical"] = new DomainTemplate
                {
                    Role = "Senior medical translator specializing in clinical documentation, " +
                           "pharmaceutical texts, regulatory submissions, and medical device documentation. " +
                           "Deep expertise in pharmacology, clinical trials, and medical terminology standards.",
                    Rules = new[]
                    {
                        "Use INN (International Nonproprietary Names) for drug names unless source uses brand names",
                        "Preserve all dosages, measurements, and units exactly (mg, ml, IU, mmol/L)",
                        "Maintain ICD codes, ATC codes, and clinical classification numbers verbatim",
                        "Never alter, omit, or simplify safety warnings, contraindications, or adverse effects",
                        "Use target-language anatomical nomenclature (Terminologia Anatomica standard)",
                        "Preserve all clinical trial identifiers, study numbers, and regulatory references",
                        "Maintain distinction between generic and brand drug names as used in source",
                        "Preserve all statistical values, confidence intervals, and p-values exactly"
                    },
                    Sections = new[]
                    {
                        "ROLE (senior medical translator with clinical and regulatory expertise)",
                        "CLINICAL CONTEXT (document type, therapeutic area, regulatory framework)",
                        "TRANSLATION MANDATE (NON-NEGOTIABLE) — patient safety paramount, faithful translation",
                        "HARD CONSTRAINT: NO HALLUCINATED TRUNCATION — every dosage, warning, and specification is safety-critical",
                        "CORE EXECUTION PRINCIPLES — absolute requirements and prohibitions",
                        "PHARMACOLOGICAL TERM HANDLING — drug names, dosages, routes of administration",
                        "ANATOMICAL NOMENCLATURE RULES — standardized anatomical terminology",
                        "DOSAGE AND MEASUREMENT PRESERVATION — exact reproduction of all numerical medical data",
                        "SAFETY-CRITICAL CONTENT RULES — warnings, contraindications, adverse effects must be complete",
                        "TERMINOLOGY CONSISTENCY HIERARCHY",
                        "PREFLIGHT SELF-CHECK (SAFETY-FOCUSED) — verify all dosages, warnings, and measurements intact",
                        "PROJECT CONTEXT — document type, therapeutic area, patient population",
                        "PROJECT-SPECIFIC GLOSSARY (MANDATORY, LOCKED)",
                        "PREVIOUS CORRECT TRANSLATIONS",
                        "OUTPUT FORMAT"
                    },
                    Special = "Medical translation is SAFETY-CRITICAL. Any error in dosages, warnings, " +
                              "contraindications, or drug names could directly harm patients. Double-check all " +
                              "numerical values and safety-related content."
                },

                ["technical"] = new DomainTemplate
                {
                    Role = "Senior technical translator specializing in engineering documentation, " +
                           "IT/software localization, and industrial/manufacturing texts. " +
                           "Deep expertise in technical specifications, user documentation, and standards.",
                    Rules = new[]
                    {
                        "Preserve all technical specifications, model numbers, and part references exactly",
                        "Maintain consistent terminology for UI elements, menu items, and software terms",
                        "Preserve code snippets, file paths, command syntax, and API names without translation",
                        "Maintain measurement units as specified — do not convert unless explicitly required",
                        "Preserve camelCase, snake_case, and PascalCase identifiers verbatim",
                        "Maintain the distinction between similar technical terms (do not conflate related but distinct concepts)"
                    },
                    Sections = new[]
                    {
                        "ROLE (senior technical translator with domain expertise)",
                        "TECHNICAL DOMAIN (field, technology, product/system)",
                        "TRANSLATION MANDATE (NON-NEGOTIABLE) — precise technical translation, no interpretation",
                        "HARD CONSTRAINT: NO HALLUCINATED TRUNCATION",
                        "CORE EXECUTION PRINCIPLES — absolute requirements and prohibitions",
                        "TECHNICAL IDENTIFIER HANDLING — product names, API names, code, file paths",
                        "MEASUREMENT AND SPECIFICATION RULES — units, tolerances, dimensions",
                        "UI/SOFTWARE STRING RULES — menu items, button labels, error messages",
                        "TERMINOLOGY CONSISTENCY HIERARCHY",
                        "NUMBER, DATE & LOCALISATION RULES",
                        "PREFLIGHT SELF-CHECK (MANDATORY)",
                        "PROJECT CONTEXT — product/system, technical domain, target audience",
                        "PROJECT-SPECIFIC GLOSSARY (MANDATORY, LOCKED)",
                        "PREVIOUS CORRECT TRANSLATIONS",
                        "OUTPUT FORMAT"
                    },
                    Special = "Technical translation requires absolute precision. Never translate product names, " +
                              "API names, or technical identifiers. Preserve all formatting in code blocks and " +
                              "technical specifications."
                },

                ["financial"] = new DomainTemplate
                {
                    Role = "Senior financial translator specializing in banking, investment, audit, " +
                           "and regulatory financial documentation. Deep expertise in IFRS/GAAP conventions, " +
                           "financial instruments, and regulatory compliance language.",
                    Rules = new[]
                    {
                        "Preserve all financial figures, percentages, exchange rates, and calculations exactly",
                        "Use target-market financial terminology (IFRS vs GAAP conventions as appropriate)",
                        "Maintain all regulatory references, compliance language, and risk disclosures verbatim",
                        "Preserve currency codes (EUR, USD, GBP) and financial instrument names",
                        "Never alter or omit risk warnings, disclaimers, or regulatory obligations",
                        "Maintain all table structures, balance sheet formatting, and numerical alignment"
                    },
                    Sections = new[]
                    {
                        "ROLE (senior financial translator with regulatory expertise)",
                        "FINANCIAL CONTEXT (document type, regulatory framework, jurisdiction)",
                        "TRANSLATION MANDATE (NON-NEGOTIABLE) — faithful financial translation, no interpretation",
                        "HARD CONSTRAINT: NO HALLUCINATED TRUNCATION — every figure and disclaimer is regulatory",
                        "CORE EXECUTION PRINCIPLES — absolute requirements and prohibitions",
                        "FINANCIAL DATA PRESERVATION RULES — figures, percentages, calculations",
                        "REGULATORY AND COMPLIANCE LANGUAGE — risk warnings, disclaimers, obligations",
                        "CURRENCY AND NUMBER FORMAT RULES — currency codes, decimal/thousands separators",
                        "TERMINOLOGY CONSISTENCY HIERARCHY",
                        "PREFLIGHT SELF-CHECK (MANDATORY) — verify all figures, calculations, and disclosures",
                        "PROJECT CONTEXT — document type, financial instrument, jurisdiction",
                        "PROJECT-SPECIFIC GLOSSARY (MANDATORY, LOCKED)",
                        "PREVIOUS CORRECT TRANSLATIONS",
                        "OUTPUT FORMAT"
                    },
                    Special = "Financial data integrity is paramount. Any altered figure could constitute a " +
                              "regulatory violation. Preserve all numerical data, risk warnings, and compliance " +
                              "language with absolute fidelity."
                },

                ["marketing"] = new DomainTemplate
                {
                    Role = "Senior marketing and creative translator specializing in brand communication, " +
                           "transcreation, and cultural adaptation. Deep expertise in advertising copy, " +
                           "digital content, and brand voice preservation.",
                    Rules = new[]
                    {
                        "Prioritize cultural resonance and emotional impact over literal accuracy where appropriate",
                        "Adapt slogans, taglines, and CTAs for target market effectiveness",
                        "Maintain brand voice consistency (tone, personality, register) throughout",
                        "Adapt cultural references, humor, and idioms for target audience",
                        "Preserve brand names, product names, and trademarked terms unchanged",
                        "Maintain SEO keyword effectiveness in target language where applicable"
                    },
                    Sections = new[]
                    {
                        "ROLE (senior marketing translator/transcreator)",
                        "BRAND CONTEXT (brand, audience, campaign, tone of voice)",
                        "CREATIVE MANDATE — cultural adaptation and persuasive effectiveness prioritized",
                        "HARD CONSTRAINT: NO HALLUCINATED TRUNCATION",
                        "BRAND VOICE RULES (LOCKED) — tone, personality, register specifications",
                        "CULTURAL ADAPTATION GUIDELINES — when to adapt vs. preserve",
                        "CALL-TO-ACTION AND TAGLINE RULES — effectiveness over literalness",
                        "TERMINOLOGY CONSISTENCY HIERARCHY",
                        "PREFLIGHT SELF-CHECK (MANDATORY)",
                        "PROJECT CONTEXT — brand, campaign, target audience, key messages",
                        "PROJECT-SPECIFIC GLOSSARY (MANDATORY, LOCKED)",
                        "PREVIOUS CORRECT TRANSLATIONS",
                        "OUTPUT FORMAT"
                    },
                    Special = "Marketing translation permits creative freedom — prioritize persuasive effectiveness " +
                              "and cultural fit over word-for-word fidelity. However, brand names, product names, " +
                              "and trademarked terms must never be altered."
                },

                ["general"] = new DomainTemplate
                {
                    Role = "Professional translator with broad expertise across multiple domains, " +
                           "strong command of both source and target languages, and deep understanding " +
                           "of cultural and register differences.",
                    Rules = new[]
                    {
                        "Maintain the tone and register of the source text faithfully",
                        "Preserve all formatting, tags, placeholders, and structural elements exactly",
                        "Ensure terminology consistency throughout the entire document",
                        "Adapt cultural references appropriately for the target audience",
                        "Preserve all numbers, dates, measurements, and special formatting"
                    },
                    Sections = new[]
                    {
                        "ROLE (professional translator)",
                        "DOCUMENT CONTEXT (type, domain, subject matter)",
                        "TRANSLATION MANDATE (NON-NEGOTIABLE) — faithful translation, no improvement or simplification",
                        "HARD CONSTRAINT: NO HALLUCINATED TRUNCATION",
                        "CORE EXECUTION PRINCIPLES — absolute requirements and prohibitions",
                        "TRANSLATION STYLE RULES — register, tone, formality",
                        "TERMINOLOGY CONSISTENCY HIERARCHY",
                        "NUMBER, DATE & LOCALISATION RULES",
                        "PREFLIGHT SELF-CHECK (MANDATORY)",
                        "PROJECT CONTEXT — document description and subject matter",
                        "PROJECT-SPECIFIC GLOSSARY (MANDATORY, LOCKED)",
                        "PREVIOUS CORRECT TRANSLATIONS",
                        "OUTPUT FORMAT"
                    },
                    Special = "Analyze the document to identify the most appropriate domain and apply " +
                              "domain-appropriate conventions. When in doubt, prioritize faithfulness to " +
                              "the source text over stylistic preferences."
                }
            };

        // ─── Public API ──────────────────────────────────────────────

        /// <summary>
        /// Builds the complete meta-prompt that instructs the AI to generate
        /// a comprehensive translation prompt for the given project context.
        /// </summary>
        internal static string BuildMetaPrompt(PromptGenerationContext ctx)
        {
            // Get domain template
            var domain = ctx.DetectedDomain ?? "general";
            if (!DomainTemplates.TryGetValue(domain, out var template))
                template = DomainTemplates["general"];

            // Build sections instruction
            var sectionsBuilder = new StringBuilder();
            for (int i = 0; i < template.Sections.Length; i++)
                sectionsBuilder.AppendLine($"{i + 1}. {template.Sections[i]}");

            // Build domain rules
            var rulesBuilder = new StringBuilder();
            for (int i = 0; i < template.Rules.Length; i++)
                rulesBuilder.AppendLine($"- {template.Rules[i]}");

            // Build terminology table
            var termInstruction = BuildTerminologySection(ctx.TermbaseTerms);

            // Build TM reference pairs
            var tmInstruction = BuildTmSection(ctx.TmPairs);

            // Build document content excerpt
            var documentContent = BuildDocumentContent(ctx.SourceSegments);

            var sb = new StringBuilder();
            sb.AppendLine("You are a prompt engineering specialist for professional translation. Your task is to generate");
            sb.AppendLine("a comprehensive, expert-level translation prompt.");
            sb.AppendLine();
            sb.AppendLine("This prompt will be used in Supervertaler, a CAT (Computer-Assisted Translation) tool that sends text");
            sb.AppendLine("segment by segment. The prompt must account for this segment-by-segment delivery.");
            sb.AppendLine();
            sb.AppendLine("=== ANALYSIS RESULTS ===");
            sb.AppendLine($"DETECTED DOMAIN: {domain.ToUpperInvariant()}");
            sb.AppendLine($"LANGUAGE PAIR: {ctx.SourceLang} -> {ctx.TargetLang}");
            sb.AppendLine($"SEGMENT COUNT: {ctx.SegmentCount}");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(ctx.AnalysisSummary))
            {
                sb.AppendLine(ctx.AnalysisSummary);
                sb.AppendLine();
            }

            sb.AppendLine("=== DOMAIN-SPECIFIC ROLE ===");
            sb.AppendLine(template.Role);
            sb.AppendLine();
            sb.AppendLine("=== PROJECT CONTEXT (document content) ===");
            sb.AppendLine(documentContent);
            sb.AppendLine();
            sb.AppendLine("=== PROMPT GENERATION INSTRUCTIONS ===");
            sb.AppendLine();
            sb.AppendLine($"Generate a COMPREHENSIVE translation prompt (2000-5000 words) that a senior {domain} translator");
            sb.AppendLine("would consider authoritative and complete. The prompt must be SPECIFIC to THIS document and domain,");
            sb.AppendLine("not generic. Use NON-NEGOTIABLE, LOCKED, and ABSOLUTE language for critical rules.");
            sb.AppendLine();
            sb.AppendLine("THE PROMPT MUST CONTAIN THESE SECTIONS (in this order):");
            sb.Append(sectionsBuilder);
            sb.AppendLine();
            sb.AppendLine("DOMAIN-SPECIFIC RULES TO EMBED IN THE PROMPT:");
            sb.Append(rulesBuilder);
            sb.AppendLine();
            sb.AppendLine("SPECIAL DOMAIN INSTRUCTIONS:");
            sb.AppendLine(template.Special);
            sb.AppendLine();

            // Universal rules
            sb.AppendLine("=== UNIVERSAL RULES (embed in EVERY prompt) ===");
            sb.AppendLine();
            sb.AppendLine("1. TRANSLATION MANDATE (NON-NEGOTIABLE):");
            sb.AppendLine("   \"This is a professional translation task. Every word, repetition, structure, and cross-reference");
            sb.AppendLine("   in the source is intentional. You must perform PURE TRANSLATION ONLY. You are explicitly forbidden");
            sb.AppendLine("   from: improving clarity, simplifying descriptions, harmonizing terminology, correcting perceived");
            sb.AppendLine("   drafting issues, streamlining enumerations, removing redundancies. If the source is long, repetitive,");
            sb.AppendLine("   or awkward, reproduce it faithfully.\"");
            sb.AppendLine();
            sb.AppendLine("2. HARD CONSTRAINT - NO HALLUCINATED TRUNCATION:");
            sb.AppendLine("   \"You must assume that every element of the source text is deliberate. You are strictly forbidden from:");
            sb.AppendLine("   omitting repetitive phrases, collapsing coordinated or parallel clauses, shortening component lists,");
            sb.AppendLine("   simplifying enumerations or method steps, 'fixing' grammar or perceived defects. If uncertain,");
            sb.AppendLine("   default to literal surface structure — never interpretation.\"");
            sb.AppendLine();
            sb.AppendLine("3. SUPERVERTALER INPUT HANDLING:");
            sb.AppendLine("   \"Text is supplied in controlled segments by Supervertaler. You must: translate only the provided");
            sb.AppendLine("   segment, preserve exact order, not rely on unseen context, not reconstruct missing structure.");
            sb.AppendLine("   If a segment appears incomplete, translate exactly what is provided without comment.\"");
            sb.AppendLine();
            sb.AppendLine("4. TERMINOLOGY CONSISTENCY HIERARCHY:");
            sb.AppendLine("   \"(1) Previous correct translations from TM (highest priority), (2) Project-specific glossary terms");
            sb.AppendLine("   (LOCKED), (3) Domain-specific conventions, (4) General language knowledge. Never mix competing");
            sb.AppendLine("   variants once established.\"");
            sb.AppendLine();
            sb.AppendLine("5. PREFLIGHT SELF-CHECK (MANDATORY INTERNAL STEP):");
            sb.AppendLine("   \"Before producing output, internally verify: every word and clause translated, no compression or");
            sb.AppendLine("   optimization occurred, all values/references intact, no restructuring occurred, segment boundaries");
            sb.AppendLine("   preserved. If any check fails, revise internally before output.\"");
            sb.AppendLine();
            sb.AppendLine("6. POST-TRANSLATION INTEGRITY ASSERTION (MANDATORY INTERNAL STEP):");
            sb.AppendLine("   \"Before finalizing output, internally assert: 'This translation is complete, literal, and");
            sb.AppendLine("   structurally faithful. No content has been omitted, merged, compressed, inferred, harmonized,");
            sb.AppendLine("   corrected, or stylistically optimized.' If this cannot be truthfully asserted, revise internally.\"");
            sb.AppendLine();
            sb.AppendLine($"7. Number/date/currency localization rules appropriate for {ctx.SourceLang} -> {ctx.TargetLang}:");
            sb.AppendLine("   - If translating FROM a European language (Dutch/French/German/etc.) TO English: convert decimal");
            sb.AppendLine("     comma to decimal point, convert period thousands separator to comma");
            sb.AppendLine("   - If translating FROM English TO a European language: reverse the above");
            sb.AppendLine("   - Currency symbols directly against the number with no space");
            sb.AppendLine("   - Date format adaptation as appropriate");
            sb.AppendLine();
            sb.AppendLine("8. OUTPUT FORMAT:");
            sb.AppendLine("   - Translation only, no commentary, no explanations, no markdown formatting");
            sb.AppendLine("   - Preserve original line breaks and paragraph structure");
            sb.AppendLine("   - UTF-8 text, straight quotation marks only");
            sb.AppendLine();

            // Terminology
            sb.AppendLine("=== TERMINOLOGY DATA ===");
            sb.AppendLine(termInstruction);
            sb.AppendLine();

            // TM pairs
            sb.AppendLine("=== REFERENCE TRANSLATIONS FROM TM ===");
            sb.AppendLine(tmInstruction);
            sb.AppendLine();

            // Constraint language
            sb.AppendLine("=== CONSTRAINT LANGUAGE REQUIREMENTS ===");
            sb.AppendLine("Use strong, unambiguous language throughout the generated prompt:");
            sb.AppendLine("- \"NON-NEGOTIABLE\" for translation mandate and core rules");
            sb.AppendLine("- \"LOCKED\" and \"MANDATORY\" for glossary and style rules");
            sb.AppendLine("- \"ABSOLUTE\" for formatting preservation requirements");
            sb.AppendLine("- \"MUST\" and \"MUST NOT\" throughout (never \"should\", \"try to\", or \"consider\")");
            sb.AppendLine("- Describe violations as critical errors");
            sb.AppendLine();

            // Project context instruction
            sb.AppendLine("=== PROJECT CONTEXT SECTION ===");
            sb.AppendLine("Analyze the document content above and write a 3-8 sentence PROJECT CONTEXT section that describes:");
            sb.AppendLine("- What the document is about (invention, contract, product, procedure, etc.)");
            sb.AppendLine("- The specific technology/domain/subject matter");
            sb.AppendLine("- Key components, parties, or concepts involved");
            sb.AppendLine("This section is marked \"FOR MODEL UNDERSTANDING ONLY — DO NOT OUTPUT\" in the final prompt.");
            sb.AppendLine();

            // Output instructions
            sb.AppendLine("=== OUTPUT INSTRUCTIONS ===");
            sb.AppendLine("1. The prompt content must be ready to use — NO placeholders like [Translation] or [Source Language]");
            sb.AppendLine($"2. Use actual values: {ctx.SourceLang} and {ctx.TargetLang}");
            sb.AppendLine("3. Include ALL termbase terms in the glossary (do not summarize or sample)");
            sb.AppendLine("4. The prompt should be comprehensive (2000-5000 words)");
            sb.AppendLine("5. Output the prompt content between the delimiters shown below — NOTHING else");
            sb.AppendLine();
            sb.AppendLine("===PROMPT_START===");
            sb.AppendLine("(Your full prompt content here — plain text, no JSON escaping needed)");
            sb.AppendLine("===PROMPT_END===");
            sb.AppendLine();
            sb.AppendLine("Output ONLY the delimiters and prompt content. No text before ===PROMPT_START=== or after ===PROMPT_END===.");

            return sb.ToString();
        }

        /// <summary>
        /// Parses the AI response to extract the generated prompt content
        /// between ===PROMPT_START=== and ===PROMPT_END=== delimiters.
        /// Returns null if delimiters are not found.
        /// </summary>
        internal static string ParseGeneratedPrompt(string aiResponse)
        {
            if (string.IsNullOrEmpty(aiResponse))
                return null;

            const string startDelimiter = "===PROMPT_START===";
            const string endDelimiter = "===PROMPT_END===";

            var startIdx = aiResponse.IndexOf(startDelimiter, StringComparison.Ordinal);
            if (startIdx < 0) return null;

            startIdx += startDelimiter.Length;

            var endIdx = aiResponse.IndexOf(endDelimiter, startIdx, StringComparison.Ordinal);
            if (endIdx < 0) return null;

            var content = aiResponse.Substring(startIdx, endIdx - startIdx).Trim();
            return string.IsNullOrEmpty(content) ? null : content;
        }

        /// <summary>
        /// Builds a short display message for the chat bubble while the
        /// full meta-prompt (which may be very large) is sent to the AI.
        /// </summary>
        internal static string BuildDisplayMessage(PromptGenerationContext ctx)
        {
            var domain = ctx.DetectedDomain ?? "general";
            var sb = new StringBuilder();
            sb.AppendLine("Analysing project and generating prompt...");
            sb.AppendLine();
            sb.AppendLine($"Domain: {char.ToUpper(domain[0])}{domain.Substring(1)}");
            sb.AppendLine($"Language pair: {ctx.SourceLang} \u2192 {ctx.TargetLang}");
            sb.AppendLine($"Segments: {ctx.SegmentCount:N0}");

            if (ctx.TermbaseTerms != null && ctx.TermbaseTerms.Count > 0)
            {
                if (ctx.TotalTermCount > 0 && ctx.TotalTermCount != ctx.TermbaseTerms.Count)
                    sb.AppendLine($"Termbase terms: filtered {ctx.TermbaseTerms.Count:N0} relevant from {ctx.TotalTermCount:N0} total");
                else
                    sb.AppendLine($"Termbase terms: {ctx.TermbaseTerms.Count:N0}");
            }

            if (ctx.TmPairs != null && ctx.TmPairs.Count > 0)
                sb.AppendLine($"TM reference pairs: {ctx.TmPairs.Count:N0}");

            return sb.ToString().TrimEnd();
        }

        // ─── Term filtering ─────────────────────────────────────────

        /// <summary>
        /// Filters term entries to only those whose source term (or source synonyms /
        /// source abbreviations) appear in at least one of the provided source segments.
        /// Uses simple case-insensitive substring matching for speed.
        /// </summary>
        internal static List<TermEntry> FilterRelevantTerms(
            List<TermEntry> terms, List<string> sourceSegments)
        {
            if (terms == null || terms.Count == 0)
                return terms ?? new List<TermEntry>();
            if (sourceSegments == null || sourceSegments.Count == 0)
                return new List<TermEntry>();

            // Concatenate all source segments into one string for fast substring search
            var combined = string.Join("\n", sourceSegments);
            var combinedUpper = combined.ToUpperInvariant();

            var relevant = new List<TermEntry>();
            foreach (var term in terms)
            {
                if (IsTermRelevant(term, combinedUpper))
                    relevant.Add(term);
            }

            return relevant;
        }

        private static bool IsTermRelevant(TermEntry term, string combinedUpper)
        {
            // Check primary source term
            if (!string.IsNullOrEmpty(term.SourceTerm) &&
                combinedUpper.Contains(term.SourceTerm.ToUpperInvariant()))
                return true;

            // Check source abbreviation variants
            if (!string.IsNullOrWhiteSpace(term.SourceAbbreviation))
            {
                foreach (var variant in term.GetSourceAbbreviationVariants())
                {
                    if (!string.IsNullOrEmpty(variant) &&
                        combinedUpper.Contains(variant.Trim().ToUpperInvariant()))
                        return true;
                }
            }

            // Check source synonyms (rich entries populated by editor)
            if (term.SourceSynonyms != null)
            {
                foreach (var syn in term.SourceSynonyms)
                {
                    if (!string.IsNullOrEmpty(syn.Text) &&
                        combinedUpper.Contains(syn.Text.ToUpperInvariant()))
                        return true;
                }
            }

            return false;
        }

        // ─── Private helpers ─────────────────────────────────────────

        private static string BuildTerminologySection(List<TermEntry> terms)
        {
            if (terms == null || terms.Count == 0)
                return "No termbase terms available. The generated prompt should include an empty " +
                       "PROJECT-SPECIFIC GLOSSARY section with instructions to add terms later.";

            var sb = new StringBuilder();
            sb.AppendLine($"The following {terms.Count} terms are from the project's termbase(s).");
            sb.AppendLine("Include ALL of them in the PROJECT-SPECIFIC GLOSSARY section of the generated prompt.");
            sb.AppendLine("Mark the glossary as MANDATORY and LOCKED — no substitutions or variants permitted.");
            sb.AppendLine();

            // Group by termbase for clarity
            var grouped = terms.GroupBy(t => t.TermbaseName ?? "Default");
            foreach (var group in grouped)
            {
                sb.AppendLine($"## {group.Key}");
                foreach (var term in group)
                {
                    var arrow = term.IsNonTranslatable ? " = " : " \u2192 ";
                    sb.Append($"  {term.SourceTerm}{arrow}{term.TargetTerm}");
                    if (term.Forbidden)
                        sb.Append(" [FORBIDDEN]");
                    if (term.IsNonTranslatable)
                        sb.Append(" [NON-TRANSLATABLE]");
                    sb.AppendLine();
                }
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        private static string BuildTmSection(List<TmMatch> tmPairs)
        {
            if (tmPairs == null || tmPairs.Count == 0)
                return "No TM reference translations available. The generated prompt should include an empty " +
                       "PREVIOUS CORRECT TRANSLATIONS section noting that none are available yet.";

            var sb = new StringBuilder();
            sb.AppendLine($"The following {tmPairs.Count} validated translation pairs come from the project's");
            sb.AppendLine("Translation Memory. Include them in the PREVIOUS CORRECT TRANSLATIONS section.");
            sb.AppendLine("These serve as style anchors — the AI must match their register and terminology choices.");
            sb.AppendLine();

            foreach (var pair in tmPairs)
            {
                sb.AppendLine($"  Source: {pair.SourceText}");
                sb.AppendLine($"  Target: {pair.TargetText}");
                if (pair.MatchPercentage > 0)
                    sb.AppendLine($"  Match: {pair.MatchPercentage}%");
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        private static string BuildDocumentContent(List<string> segments)
        {
            if (segments == null || segments.Count == 0)
                return "(No document content available)";

            // Send the full document — user confirmed cost is acceptable
            var sb = new StringBuilder();
            for (int i = 0; i < segments.Count; i++)
            {
                var text = segments[i];
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine(text);
            }

            return sb.ToString().TrimEnd();
        }

        // ─── Supporting types ────────────────────────────────────────

        private class DomainTemplate
        {
            public string Role;
            public string[] Rules;
            public string[] Sections;
            public string Special;
        }
    }

    /// <summary>
    /// All data needed by PromptGenerator to build the meta-prompt.
    /// Gathered by AiAssistantViewPart before calling BuildMetaPrompt.
    /// </summary>
    internal class PromptGenerationContext
    {
        public string SourceLang { get; set; }
        public string TargetLang { get; set; }
        public string DetectedDomain { get; set; }
        public string AnalysisSummary { get; set; }
        public int SegmentCount { get; set; }
        public List<string> SourceSegments { get; set; }
        public List<TermEntry> TermbaseTerms { get; set; }
        public List<TmMatch> TmPairs { get; set; }

        /// <summary>
        /// Total number of termbase terms before relevance filtering.
        /// Used by BuildDisplayMessage to show "Filtered X relevant terms from Y total".
        /// Zero means no filtering was applied.
        /// </summary>
        public int TotalTermCount { get; set; }
    }
}

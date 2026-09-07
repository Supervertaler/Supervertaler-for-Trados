using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Supervertaler.Core.Models;

namespace Supervertaler.Core
{
    /// <summary>
    /// Builds a meta-prompt that instructs the AI to generate a comprehensive,
    /// domain-specific translation prompt. Ported from Supervertaler Workbench's
    /// unified_prompt_manager_qt.py.
    /// </summary>
    public static class PromptGenerator
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
                        "Never paraphrase, simplify, or improve source text – patents require exact semantic equivalence",
                        "Preserve formal patent register: \"wherein\", \"thereof\", \"hereinafter\", \"person skilled in the art\"",
                        "Maintain claim numbering, cross-references, and dependency structure without alteration",
                        "Use gerund constructions naturally: \"An example is replacing...\" NOT \"An example is the replacing of...\"",
                        "Preserve all prior art document references verbatim (e.g., US 20130183090, EP 2923344)",
                        "Maintain the hierarchical structure: TECHNICAL FIELD > PRIOR ART > SUMMARY > DRAWINGS > DETAILED DESCRIPTION > CLAIMS > ABSTRACT",
                        "When source is long, repetitive, or awkward, reproduce it faithfully – every word in a patent is legally operative"
                    },
                    Sections = new[]
                    {
                        "ROLE (senior patent translator with specific expertise areas)",
                        "SCOPE OF APPLICATION (project context: invention type, technology field, patent number if known)",
                        "TRANSLATION MANDATE (NON-NEGOTIABLE) – pure translation only, explicitly forbid improvement, simplification, harmonization, correction, streamlining",
                        "HARD CONSTRAINT: NO HALLUCINATED TRUNCATION – never omit repetitive phrases, collapse clauses, shorten lists, simplify enumerations, or \"fix\" grammar",
                        "CORE EXECUTION PRINCIPLES – with ABSOLUTE REQUIREMENTS (checkmarks) and ABSOLUTE PROHIBITIONS (crosses)",
                        "SUPERVERTALER INPUT HANDLING – batched segment delivery: translate every delivered segment, keep count/order/boundaries aligned, use only in-batch context",
                        "TRANSLATION STYLE (LOCKED) – mandatory term mappings",
                        "CLAIM TRANSLATION STYLE – preserve dependency structure, maintain phrasing, avoid stylistic smoothing",
                        "GERUND STYLE RULE – prefer natural English gerund over \"the [verb]ing of\" construction",
                        "TERMINOLOGY CONSISTENCY HIERARCHY – (1) Previous correct translations, (2) Project-specific glossary, (3) General mandatory mappings",
                        "TECHNICAL AND MECHANICAL FORMATTING RULES – dimensions, figure refs, prior art numbers, standard abbreviations",
                        "PREFLIGHT SELF-CHECK (MANDATORY) – verify every word translated, no compression, all values intact",
                        "POST-TRANSLATION INTEGRITY ASSERTION (MANDATORY)",
                        "PROJECT CONTEXT (for model understanding only – do not output)",
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
                        "Maintain exact legal terminology – never substitute informal equivalents",
                        "Preserve legal entity types and abbreviations (BV, NV, GmbH, Ltd, Inc., SA, SARL) without translation",
                        "Preserve statutory references, article numbers, and legal citations exactly as written",
                        "Maintain formal legal register: \"hereby\", \"pursuant to\", \"notwithstanding\", \"whereas\"",
                        "Preserve all dates, deadlines, and procedural time limits without alteration",
                        "Distinguish between common law and civil law terminology as appropriate for the target jurisdiction",
                        "Preserve Latin legal terms (bona fide, inter alia, prima facie) unless target convention replaces them",
                        "Never translate proper names of laws, statutes, or regulations – retain original with optional translation in parentheses",
                        "Maintain contractual numbering, clause references, and article structure exactly"
                    },
                    Sections = new[]
                    {
                        "ROLE (senior legal translator with jurisdiction expertise)",
                        "LEGAL FRAMEWORK (jurisdiction, legal system type, document type)",
                        "TRANSLATION MANDATE (NON-NEGOTIABLE) – faithful legal translation, no interpretation or simplification",
                        "HARD CONSTRAINT: NO HALLUCINATED TRUNCATION – every clause, proviso, and exception is legally operative",
                        "CORE EXECUTION PRINCIPLES – absolute requirements and prohibitions",
                        "LEGAL REGISTER REQUIREMENTS – formality, precision, no colloquial language",
                        "LEGAL ENTITY AND TITLE HANDLING – preservation rules for entities, titles, proper names",
                        "STATUTORY REFERENCE PRESERVATION – article numbers, law names, citations",
                        "TERMINOLOGY CONSISTENCY HIERARCHY",
                        "NUMBER, DATE & LOCALISATION RULES",
                        "PREFLIGHT SELF-CHECK (MANDATORY)",
                        "PROJECT CONTEXT – document type, parties, jurisdiction, subject matter",
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
                        "TRANSLATION MANDATE (NON-NEGOTIABLE) – patient safety paramount, faithful translation",
                        "HARD CONSTRAINT: NO HALLUCINATED TRUNCATION – every dosage, warning, and specification is safety-critical",
                        "CORE EXECUTION PRINCIPLES – absolute requirements and prohibitions",
                        "PHARMACOLOGICAL TERM HANDLING – drug names, dosages, routes of administration",
                        "ANATOMICAL NOMENCLATURE RULES – standardized anatomical terminology",
                        "DOSAGE AND MEASUREMENT PRESERVATION – exact reproduction of all numerical medical data",
                        "SAFETY-CRITICAL CONTENT RULES – warnings, contraindications, adverse effects must be complete",
                        "TERMINOLOGY CONSISTENCY HIERARCHY",
                        "PREFLIGHT SELF-CHECK (SAFETY-FOCUSED) – verify all dosages, warnings, and measurements intact",
                        "PROJECT CONTEXT – document type, therapeutic area, patient population",
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
                        "Maintain measurement units as specified – do not convert unless explicitly required",
                        "Preserve camelCase, snake_case, and PascalCase identifiers verbatim",
                        "Maintain the distinction between similar technical terms (do not conflate related but distinct concepts)"
                    },
                    Sections = new[]
                    {
                        "ROLE (senior technical translator with domain expertise)",
                        "TECHNICAL DOMAIN (field, technology, product/system)",
                        "TRANSLATION MANDATE (NON-NEGOTIABLE) – precise technical translation, no interpretation",
                        "HARD CONSTRAINT: NO HALLUCINATED TRUNCATION",
                        "CORE EXECUTION PRINCIPLES – absolute requirements and prohibitions",
                        "TECHNICAL IDENTIFIER HANDLING – product names, API names, code, file paths",
                        "MEASUREMENT AND SPECIFICATION RULES – units, tolerances, dimensions",
                        "UI/SOFTWARE STRING RULES – menu items, button labels, error messages",
                        "TERMINOLOGY CONSISTENCY HIERARCHY",
                        "NUMBER, DATE & LOCALISATION RULES",
                        "PREFLIGHT SELF-CHECK (MANDATORY)",
                        "PROJECT CONTEXT – product/system, technical domain, target audience",
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
                        "TRANSLATION MANDATE (NON-NEGOTIABLE) – faithful financial translation, no interpretation",
                        "HARD CONSTRAINT: NO HALLUCINATED TRUNCATION – every figure and disclaimer is regulatory",
                        "CORE EXECUTION PRINCIPLES – absolute requirements and prohibitions",
                        "FINANCIAL DATA PRESERVATION RULES – figures, percentages, calculations",
                        "REGULATORY AND COMPLIANCE LANGUAGE – risk warnings, disclaimers, obligations",
                        "CURRENCY AND NUMBER FORMAT RULES – currency codes, decimal/thousands separators",
                        "TERMINOLOGY CONSISTENCY HIERARCHY",
                        "PREFLIGHT SELF-CHECK (MANDATORY) – verify all figures, calculations, and disclosures",
                        "PROJECT CONTEXT – document type, financial instrument, jurisdiction",
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
                        "CREATIVE MANDATE – cultural adaptation and persuasive effectiveness prioritized",
                        "HARD CONSTRAINT: NO HALLUCINATED TRUNCATION",
                        "BRAND VOICE RULES (LOCKED) – tone, personality, register specifications",
                        "CULTURAL ADAPTATION GUIDELINES – when to adapt vs. preserve",
                        "CALL-TO-ACTION AND TAGLINE RULES – effectiveness over literalness",
                        "TERMINOLOGY CONSISTENCY HIERARCHY",
                        "PREFLIGHT SELF-CHECK (MANDATORY)",
                        "PROJECT CONTEXT – brand, campaign, target audience, key messages",
                        "PROJECT-SPECIFIC GLOSSARY (MANDATORY, LOCKED)",
                        "PREVIOUS CORRECT TRANSLATIONS",
                        "OUTPUT FORMAT"
                    },
                    Special = "Marketing translation permits creative freedom – prioritize persuasive effectiveness " +
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
                        "TRANSLATION MANDATE (NON-NEGOTIABLE) – faithful translation, no improvement or simplification",
                        "HARD CONSTRAINT: NO HALLUCINATED TRUNCATION",
                        "CORE EXECUTION PRINCIPLES – absolute requirements and prohibitions",
                        "TRANSLATION STYLE RULES – register, tone, formality",
                        "TERMINOLOGY CONSISTENCY HIERARCHY",
                        "NUMBER, DATE & LOCALISATION RULES",
                        "PREFLIGHT SELF-CHECK (MANDATORY)",
                        "PROJECT CONTEXT – document description and subject matter",
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
        /// <summary>"segments" or whatever the host said it was counting, singular-safe.</summary>
        private static string UnitOf(PromptGenerationContext ctx)
        {
            var unit = (ctx?.SegmentUnit ?? "").Trim();
            if (unit.Length == 0) unit = "segments";
            if (ctx != null && ctx.SegmentCount == 1 && unit.EndsWith("s")) unit = unit.Substring(0, unit.Length - 1);
            return unit;
        }

        private static string Singular(string unit) =>
            !string.IsNullOrEmpty(unit) && unit.EndsWith("s") ? unit.Substring(0, unit.Length - 1) : unit;

        private static string Capitalise(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        public static string BuildMetaPrompt(PromptGenerationContext ctx)
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
            sb.AppendLine("This prompt will be used in Supervertaler, a CAT (Computer-Assisted Translation) tool. Supervertaler");
            sb.AppendLine("delivers the source text as NUMBERED BATCHES of segments (typically dozens of segments per request;");
            sb.AppendLine("in some contexts a single segment). The prompt must account for this batched, segment-numbered");
            sb.AppendLine("delivery - do NOT describe delivery as \"one segment at a time\" or \"in isolation\".");
            sb.AppendLine();
            // #97: the excerpt below is rendered exactly as the segments will be sent,
            // inline formatting included. Without this paragraph the generating model
            // invented a tag inventory - for months it named a notation the batch never
            // sends. Phrased as "the notation the excerpt shows" rather than naming one,
            // because this generator is shared across products.
            sb.AppendLine("INLINE TAGS: the DOCUMENT CONTENT excerpt below is rendered EXACTLY as segments will be");
            sb.AppendLine("delivered to the translating model, including any inline-formatting tags. If it contains");
            sb.AppendLine("tags, the prompt you write MUST describe them in THAT notation and no other - never a");
            sb.AppendLine("notation you assume the CAT tool uses internally. The prompt MUST include these rules:");
            sb.AppendLine("reproduce every tag verbatim - same tags, same count, same order and nesting - around the");
            sb.AppendLine("corresponding translated text; never invent, drop, merge or rename a tag; and never replace");
            sb.AppendLine("a tagged character with a Unicode equivalent or vice versa (a tagged subscript digit stays");
            sb.AppendLine("tagged even when the same formula appears elsewhere as a Unicode subscript - \"tidying\" it");
            sb.AppendLine("destroys a tag pair). If the excerpt contains no tags, say only that any tags that do arrive");
            sb.AppendLine("must be reproduced verbatim, and do not enumerate a notation.");
            sb.AppendLine();
            sb.AppendLine("=== ANALYSIS RESULTS ===");
            sb.AppendLine($"DETECTED DOMAIN: {domain.ToUpperInvariant()}");
            sb.AppendLine($"LANGUAGE PAIR: {ctx.SourceLang} -> {ctx.TargetLang}");
            // The label takes the singular noun whatever the count - "SEGMENT COUNT:
            // 370", as it always read - where the summary line below says "Segments:".
            sb.AppendLine($"{Singular(UnitOf(ctx)).ToUpperInvariant()} COUNT: {ctx.SegmentCount}");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(ctx.AnalysisSummary))
            {
                sb.AppendLine(ctx.AnalysisSummary);
                sb.AppendLine();
            }

            // Translator-supplied briefing (from the AutoPrompt context dialog).
            // Surfaced as authoritative because the human knows the document's real
            // purpose better than any inference – this is what lets a user rescue a
            // misclassified text ("this is creative copy, not a patent").
            if (!string.IsNullOrWhiteSpace(ctx.UserContextHint))
            {
                sb.AppendLine("=== TRANSLATOR-PROVIDED CONTEXT (AUTHORITATIVE) ===");
                sb.AppendLine("The translator supplied the following briefing about this document. Treat it");
                sb.AppendLine("as authoritative: where it conflicts with the detected domain or any inference");
                sb.AppendLine("below, follow the briefing.");
                sb.AppendLine();
                sb.AppendLine(ctx.UserContextHint);
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
            sb.AppendLine($"Generate a comprehensive translation prompt (2000\u20135000 words) that a senior {domain} translator");
            sb.AppendLine("would consider authoritative and complete. The prompt must be specific to this document and domain,");
            sb.AppendLine("not generic. Use clear, firm language for critical rules (e.g. \"Required\", \"Must\", \"Always\").");
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
            sb.AppendLine("=== UNIVERSAL RULES (embed in every prompt) ===");
            sb.AppendLine();
            sb.AppendLine("1. TRANSLATION MANDATE:");
            sb.AppendLine("   \"This is a professional translation task. Every word, repetition, structure, and cross-reference");
            sb.AppendLine("   in the source is intentional. You must perform pure translation only. Do not: improve clarity,");
            sb.AppendLine("   simplify descriptions, harmonise terminology, correct perceived drafting issues, streamline");
            sb.AppendLine("   enumerations, or remove redundancies. If the source is long, repetitive, or awkward, reproduce");
            sb.AppendLine("   it faithfully.\"");
            sb.AppendLine();
            sb.AppendLine("2. NO TRUNCATION OR OMISSION:");
            sb.AppendLine("   \"Assume that every element of the source text is deliberate. Do not: omit repetitive phrases,");
            sb.AppendLine("   collapse coordinated or parallel clauses, shorten component lists, simplify enumerations or");
            sb.AppendLine("   method steps, or 'fix' grammar or perceived defects. If uncertain, default to literal surface");
            sb.AppendLine("   structure rather than interpretation.\"");
            sb.AppendLine();
            sb.AppendLine("3. SUPERVERTALER INPUT HANDLING:");
            sb.AppendLine("   \"Supervertaler delivers the source text as a numbered batch of segments (in some contexts a");
            sb.AppendLine("   single segment). You must: translate EVERY delivered segment, keep segment count and order");
            sb.AppendLine("   exactly aligned with the input, and preserve segment boundaries. You MAY use context visible");
            sb.AppendLine("   within the delivered batch (e.g. resolving an antecedent that appears a few segments earlier),");
            sb.AppendLine("   but batch boundaries are arbitrary: never assume the batch is the whole document, and leave");
            sb.AppendLine("   document-wide checks (e.g. reference-numeral consistency across the full text) to a separate");
            sb.AppendLine("   QA pass. There is no memory between requests, so terminology must come from the glossary and");
            sb.AppendLine("   reference translations below - never from choices made in an earlier batch. If a segment");
            sb.AppendLine("   appears incomplete, translate exactly what is provided without comment.\"");
            sb.AppendLine();
            sb.AppendLine("4. TERMINOLOGY CONSISTENCY HIERARCHY:");
            sb.AppendLine("   \"(1) Previous correct translations from TM (highest priority), (2) Project-specific glossary");
            sb.AppendLine("   terms (required), (3) Domain-specific conventions, (4) General language knowledge. Never mix");
            sb.AppendLine("   competing variants once established. Because there is no memory between batches, the prompt");
            sb.AppendLine("   itself must LOCK every recurring term to a single translation - never leave an open choice");
            sb.AppendLine("   (‘X or Y’) for the translator AI to resolve, as consistency cannot carry across batches.\"");
            sb.AppendLine("   This applies to EVERY term mapping the prompt states, not only the glossary table:");
            sb.AppendLine("   mappings given in prose style sections are binding in exactly the same way. Never");
            sb.AppendLine("   write a mapping of the form ‘X → \"a\" / \"b\" per context’ or ‘X → a, or b where");
            sb.AppendLine("   appropriate’. If a source term truly renders differently in different collocations,");
            sb.AppendLine("   state each collocation as its own mapping with its own single locked target, and");
            sb.AppendLine("   name the collocation explicitly so the choice needs no judgement at translation time.");
            sb.AppendLine();
            sb.AppendLine("5. PREFLIGHT SELF-CHECK:");
            sb.AppendLine("   \"Before producing output, internally verify: every word and clause translated, no compression or");
            sb.AppendLine("   optimisation occurred, all values/references intact, no restructuring occurred, segment boundaries");
            sb.AppendLine("   preserved. If any check fails, revise before output.\"");
            sb.AppendLine();
            sb.AppendLine("6. POST-TRANSLATION INTEGRITY CHECK:");
            sb.AppendLine("   \"Before finalising output, confirm that the translation is complete, literal, and structurally");
            sb.AppendLine("   faithful. No content has been omitted, merged, compressed, inferred, harmonised, corrected, or");
            sb.AppendLine("   stylistically optimised. If this is not the case, revise before output.\"");
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

            // SuperMemory KB context
            if (!string.IsNullOrWhiteSpace(ctx.KbContext))
            {
                sb.AppendLine("=== KNOWLEDGE BASE (SuperMemory) ===");
                sb.AppendLine("The translator maintains a structured knowledge base with established conventions,");
                sb.AppendLine("terminology reasoning, client preferences, and style guides. Incorporate these into");
                sb.AppendLine("the generated prompt where relevant – they represent hard-won translation decisions");
                sb.AppendLine("and client-specific rules that should be baked into the prompt's glossary, style");
                sb.AppendLine("rules, and domain instructions rather than being rediscovered from scratch.");
                sb.AppendLine();
                sb.AppendLine(ctx.KbContext);
                sb.AppendLine();
            }

            // Provenance discipline. Observed after the TM section was locked down: the model
            // stopped inventing entries in PREVIOUS CORRECT TRANSLATIONS and started writing
            // "Anchored by validated TM segment" in glossary Notes cells for terms the TM
            // never contained. The fabrication did not stop, it moved – from inventing an
            // entry to mislabelling one. A note is a claim, so the rule has to govern claims
            // wherever they appear, not just the section they were last seen in.
            var hasTermData = ctx.TermbaseTerms != null && ctx.TermbaseTerms.Count > 0;
            var hasTmData = ctx.TmPairs != null && ctx.TmPairs.Count > 0;
            var hasKbData = !string.IsNullOrWhiteSpace(ctx.KbContext);

            sb.AppendLine("=== ATTRIBUTION OF TERMINOLOGY DECISIONS ===");
            sb.AppendLine();
            sb.AppendLine("The prompt you generate may record where a rendering came from – in a glossary");
            sb.AppendLine("Notes cell, in a style rule, anywhere. Every such note is a claim about provenance");
            sb.AppendLine("and must be true. Cite a source ONLY when that source, exactly as supplied above,");
            sb.AppendLine("actually contains the term or rule.");
            sb.AppendLine();
            sb.Append("- \"TM\", \"validated\", \"validated segment\", \"per the TM\", \"anchored by the validated ");
            sb.AppendLine("title\"");
            sb.AppendLine("  and equivalents: permitted ONLY for a term that literally appears in the pairs under");
            sb.AppendLine(hasTmData
                ? $"  REFERENCE TRANSLATIONS FROM TM above. Those {ctx.TmPairs.Count} pairs are the entire TM you"
                : "  REFERENCE TRANSLATIONS FROM TM above. NO TM pairs were supplied for this project, so no");
            sb.AppendLine(hasTmData
                ? "  have been shown; a term absent from them has no TM provenance, however plausible."
                : "  note of this kind is permitted anywhere in the generated prompt.");
            sb.AppendLine();
            sb.Append("- \"house default\", \"approved\", \"established convention\", \"the translator's own\" ");
            sb.AppendLine("and");
            sb.AppendLine(hasKbData
                ? "  equivalents: permitted ONLY for a rule that appears in the KNOWLEDGE BASE section above."
                : "  equivalents: NO knowledge base was supplied, so no note of this kind is permitted.");
            sb.AppendLine();
            sb.AppendLine(hasTermData
                ? "- \"termbase\", \"approved terminology\": permitted ONLY for a term listed under TERMINOLOGY DATA."
                : "- \"termbase\", \"approved terminology\": NO termbase terms were supplied, so these are barred.");
            sb.AppendLine();
            sb.AppendLine("For every other rendering – anything you decided yourself, from the document or from");
            sb.AppendLine("domain knowledge – state the rule with NO provenance note at all. A note that gives a");
            sb.AppendLine("reason or forbids an alternative is always fine (\"Never 'apparatus'\", \"UK spelling\");");
            sb.AppendLine("what is barred is claiming an authority that did not supply it.");
            sb.AppendLine();
            sb.AppendLine("Attributing your own inference to the translator is worse than leaving it unattributed.");
            sb.AppendLine("The translator reads it back as their own earlier decision and will not re-examine it,");
            sb.AppendLine("so a wrong lock that looks approved is the hardest kind to catch. When in doubt about");
            sb.AppendLine("where something came from, omit the note and keep the rule.");
            sb.AppendLine();

            // Translator-comment methodology — required in every generated prompt
            sb.AppendLine("=== TRANSLATOR-COMMENT METHODOLOGY (REQUIRED IN EVERY GENERATED PROMPT) ===");
            sb.AppendLine();
            sb.AppendLine("Every prompt you generate MUST embed the following silent-correction-with-flagged-");
            sb.AppendLine("comment methodology. This is a project-wide Supervertaler standard for all");
            sb.AppendLine("AutoPrompt-generated prompts, required regardless of source language or domain.");
            sb.AppendLine("Do not omit it because the source looks clean - defects appear in nearly every");
            sb.AppendLine("real document, and the methodology must be in the prompt so the translator AI");
            sb.AppendLine("knows how to handle them when (not if) they do.");
            sb.AppendLine();
            sb.AppendLine("**The methodology, in brief:**");
            sb.AppendLine();
            sb.AppendLine("The translator AI silently corrects obvious mechanical defects in the source");
            sb.AppendLine("(typos, broken words across whitespace, hanging mid-sentence breaks, doubled");
            sb.AppendLine("spaces, stray punctuation, missing inflections, reference-numeral mismatches");
            sb.AppendLine("that are unambiguous in context, missing diacritics, etc. - the translator AI");
            sb.AppendLine("identifies the categories appropriate to the actual source language).");
            sb.AppendLine();
            sb.AppendLine("For every silent correction, the translator AI appends ONE concise comment at");
            sb.AppendLine("the very end of the segment, in this exact format:");
            sb.AppendLine();
            sb.AppendLine("    [[TC: short factual description of the fix(es)]]");
            sb.AppendLine();
            sb.AppendLine("- Multiple fixes in one segment are joined with semicolons inside ONE marker.");
            sb.AppendLine("  Never more than one [[TC: ...]] per segment.");
            sb.AppendLine("- Segments with no defects emit NO marker. Do not emit empty [[TC: ]].");
            sb.AppendLine("- The delimiters MUST be double ASCII square brackets: [[ and ]]. Nothing");
            sb.AppendLine("  parses these: the translator reads them in the grid while reviewing, and");
            sb.AppendLine("  decides per segment whether the comment is worth keeping. So the marker");
            sb.AppendLine("  has to be instantly recognisable at a glance, and above all it has to");
            sb.AppendLine("  RENDER - the white square brackets used before are missing from the fonts");
            sb.AppendLine("  CAT grids use and showed up as empty boxes.");
            sb.AppendLine("- Where the silent correction inserts a word or short phrase the translator");
            sb.AppendLine("  supplied to fill a clear gap, that supplied text is wrapped in standard");
            sb.AppendLine("  ASCII square brackets [like this] INSIDE the running translation. The");
            sb.AppendLine("  trailing [[TC: ...]] marker then references this, e.g.");
            sb.AppendLine("  [[TC: [bracketed text] supplied to close hanging sentence]].");
            sb.AppendLine("- The comment body is concise - typically 5 to 20 words. Noun-phrase /");
            sb.AppendLine("  sentence-fragment style; avoid full sentences, first-person (\"I\",");
            sb.AppendLine("  \"the translator\", \"the LLM\"), or apologetic hedging.");
            sb.AppendLine("- The marker is the FINAL content of the segment, separated from the running");
            sb.AppendLine("  text by exactly one regular space, with no line break, no full stop, and no");
            sb.AppendLine("  other punctuation between.");
            sb.AppendLine("- Markers attach to THEIR OWN segment's end. Never pool markers at the end of");
            sb.AppendLine("  the batch or response - each fix stays with the segment it describes.");
            sb.AppendLine();
            sb.AppendLine("**What the methodology MUST NOT silently correct** (the generated prompt MUST");
            sb.AppendLine("state these as hard exclusions, regardless of domain):");
            sb.AppendLine();
            sb.AppendLine("- Numerical values, dates, currency figures, dosages (legal / regulatory weight).");
            sb.AppendLine("- Anything that changes legal scope (claim language, contract terms, statutory");
            sb.AppendLine("  references, etc.) - preserve faithfully even if awkward.");
            sb.AppendLine("- Long, repetitive, or awkward source prose - length and repetition are not");
            sb.AppendLine("  defects.");
            sb.AppendLine("- Synonym variation that may be deliberate (the drafter may have varied for");
            sb.AppendLine("  effect; preserve unless clearly an error).");
            sb.AppendLine("- Headings, identifiers, proper names, citations - preserve verbatim.");
            sb.AppendLine("- Anything the AI cannot resolve unambiguously from immediate context. In case");
            sb.AppendLine("  of doubt, translate faithfully and use:");
            sb.AppendLine("  [[TC: source ambiguous - possible defect at \"...\" but preserved as written]]");
            sb.AppendLine();
            sb.AppendLine("**How to embed this in the generated prompt:**");
            sb.AppendLine();
            sb.AppendLine("1. The generated prompt's TRANSLATION MANDATE section MUST describe the silent-");
            sb.AppendLine("   correction methodology in terms appropriate to the source language and");
            sb.AppendLine("   domain (the translator AI needs to know which defect categories are");
            sb.AppendLine("   relevant - e.g. -d/-t verb-ending typos for Dutch, missing umlauts for");
            sb.AppendLine("   German, accent slips for French, conjugation typos for Spanish/Italian).");
            sb.AppendLine("2. The generated prompt MUST include a dedicated section titled");
            sb.AppendLine("   \"TRANSLATOR COMMENT FORMAT\" (or equivalent) near the end with the exact");
            sb.AppendLine("   [[TC: ...]] spec verbatim, plus 4-6 example comment bodies adapted to the");
            sb.AppendLine("   source language and domain. Example bodies for reference (the LLM should");
            sb.AppendLine("   produce equivalents for the actual source language):");
            sb.AppendLine();
            sb.AppendLine("       [[TC: \"verzekerd\" corrected to \"verzekert\"]]");
            sb.AppendLine("       [[TC: stray space before full stop closed]]");
            sb.AppendLine("       [[TC: doubled space inside sentence collapsed]]");
            sb.AppendLine("       [[TC: hanging mid-sentence break reconstructed; [bracketed text] supplied]]");
            sb.AppendLine("       [[TC: \"achterzijde (6)\" corrected to (5) per antecedent in same paragraph]]");
            sb.AppendLine("       [[TC: source ambiguous - possible defect at \"...\" but preserved as written]]");
            sb.AppendLine();
            sb.AppendLine("3. The generated prompt's PREFLIGHT SELF-CHECK and POST-TRANSLATION INTEGRITY");
            sb.AppendLine("   sections MUST include a check that any silent correction has its");
            sb.AppendLine("   corresponding [[TC: ...]] marker at the segment end, and that segments");
            sb.AppendLine("   without corrections have no marker.");
            sb.AppendLine("4. The generated prompt's OUTPUT FORMAT section MUST name [[TC: ...]] as the");
            sb.AppendLine("   only permitted non-translation content, so that a rule against commentary");
            sb.AppendLine("   is not read as a rule against the marker.");
            sb.AppendLine();
            sb.AppendLine("The translator's comments appear inline in the target text as [[TC: ...]].");
            sb.AppendLine("They are for the translator, who is going through the document segment by");
            sb.AppendLine("segment anyway: they read the comment, judge whether it still matters, and");
            sb.AppendLine("if it does, turn it into a real CAT tool comment and delete it from the");
            sb.AppendLine("target text. Nothing automated consumes them. What the prompt owes that");
            sb.AppendLine("workflow is markers that are consistent, brief, and never emitted without");
            sb.AppendLine("cause - a marker on every segment is a marker nobody reads.");
            sb.AppendLine();

            // Constraint language
            sb.AppendLine("=== LANGUAGE STYLE ===");
            sb.AppendLine("Use clear, firm language throughout the generated prompt:");
            sb.AppendLine("- \"Required\" and \"Must\" for core translation rules");
            sb.AppendLine("- \"Always\" and \"Never\" for glossary and style rules");
            sb.AppendLine("- Use direct instructions (prefer \"Must\" over \"should\" or \"try to\")");
            sb.AppendLine("- Be specific and unambiguous about expectations");
            sb.AppendLine();

            // Project context instruction
            sb.AppendLine("=== PROJECT CONTEXT SECTION ===");
            sb.AppendLine("Analyze the document content above and write a 3-8 sentence PROJECT CONTEXT section that describes:");
            sb.AppendLine("- What the document is about (invention, contract, product, procedure, etc.)");
            sb.AppendLine("- The specific technology/domain/subject matter");
            sb.AppendLine("- Key components, parties, or concepts involved");
            sb.AppendLine("This section is marked \"FOR MODEL UNDERSTANDING ONLY – DO NOT OUTPUT\" in the final prompt.");
            sb.AppendLine();

            // Host constraints: how the prompt will actually be delivered and
            // consumed. Placed after every default so that, where the two
            // disagree, the host wins.
            if (!string.IsNullOrWhiteSpace(ctx.HostConstraints))
            {
                sb.AppendLine("=== HOST CONSTRAINTS (THESE OVERRIDE ANYTHING ABOVE THAT CONFLICTS) ===");
                sb.AppendLine(ctx.HostConstraints.Trim());
                sb.AppendLine();
            }

            // Output instructions
            sb.AppendLine("=== OUTPUT INSTRUCTIONS ===");
            sb.AppendLine("1. The prompt content must be ready to use – NO placeholders like [Translation] or [Source Language]");
            sb.AppendLine($"2. Use actual values: {ctx.SourceLang} and {ctx.TargetLang}");
            if (ctx.TermbaseTerms != null && ctx.TermbaseTerms.Count > 0)
                sb.AppendLine("3. Include ALL termbase terms in the glossary (do not summarize or sample)");
            else
                sb.AppendLine("3. No approved termbase terms were supplied – derive the glossary from the document, " +
                              "locked and without caveats, exactly as specified under TERMINOLOGY DATA above");
            sb.AppendLine("4. The prompt should be comprehensive (2000-5000 words)");
            sb.AppendLine("5. Use exactly ONE blank line between sections, paragraphs, and list blocks.");
            sb.AppendLine("   Never insert two or more consecutive blank lines.");
            sb.AppendLine("6. Output the prompt content between the delimiters shown below – NOTHING else");
            sb.AppendLine();
            sb.AppendLine("=== FORMATTING: USE PROPER MARKDOWN ===");
            sb.AppendLine("The generated prompt is written to a `.md` file in the user's shared prompt library");
            sb.AppendLine("and is read both by humans (in Markdown-aware editors) and by the LLM at translation time.");
            sb.AppendLine("Format it as PROPER MARKDOWN, not plain text dressed up as a list:");
            sb.AppendLine();
            sb.AppendLine("- Open with a `# H1` heading for the prompt title and one or two `## H2` subtitles.");
            sb.AppendLine("- Each major numbered section MUST be a `## H2` heading, e.g.");
            sb.AppendLine("    ## 1. ROLE");
            sb.AppendLine("    ## 2. TECHNICAL DOMAIN");
            sb.AppendLine("    ## 3. TRANSLATION MANDATE (NON-NEGOTIABLE)");
            sb.AppendLine("- Use `### H3` for subsections inside a major section (e.g. `### Absolute requirements`,");
            sb.AppendLine("  `### Absolute prohibitions`).");
            sb.AppendLine("- Use `-` bullet lists for absolute-requirements, absolute-prohibitions, rule lists,");
            sb.AppendLine("  and any other enumerable content. One item per line. No prose paragraphs masquerading");
            sb.AppendLine("  as lists.");
            sb.AppendLine("- Use `**bold**` for emphasised terms, locked glossary keywords, and section labels.");
            sb.AppendLine("- Use a proper Markdown table for the PROJECT-SPECIFIC GLOSSARY:");
            sb.AppendLine();
            sb.AppendLine("    | Dutch (source) | English (locked target) | Notes |");
            sb.AppendLine("    |---|---|---|");
            sb.AppendLine("    | inrichting | device | EPO standard; never \"apparatus\" |");
            sb.AppendLine();
            sb.AppendLine("  The locked-target cell is the SINGLE binding rendering for that source term:");
            sb.AppendLine("  exactly one target per row. Never put alternatives in it (\"housing (enclosure)\",");
            sb.AppendLine("  \"casing / housing\"), and never defer the choice to context.");
            sb.AppendLine();
            sb.AppendLine("  The Notes cell explains or forbids – it must NEVER introduce a second target.");
            sb.AppendLine("  Correct: `EPO standard; never \"apparatus\"`. NOT allowed: `= \"casing\" where paired");
            sb.AppendLine("  with X`, which silently overrides the locked cell and reopens the very choice the");
            sb.AppendLine("  lock exists to close. The translator AI has no memory between batches and cannot");
            sb.AppendLine("  resolve such a choice consistently.");
            sb.AppendLine();
            sb.AppendLine("  Where a source term genuinely needs different renderings in different collocations,");
            sb.AppendLine("  give each collocation its OWN row with its own locked target – e.g. a row for the");
            sb.AppendLine("  bare term and a separate row for the fixed phrase it appears in – rather than one");
            sb.AppendLine("  row plus a caveat. Check each row you write against the document: if the locked");
            sb.AppendLine("  target does not fit every occurrence, split the row.");
            sb.AppendLine();
            sb.AppendLine("- Use `---` horizontal rules to separate major sections where it aids scanability.");
            sb.AppendLine("- Use fenced code blocks (```) only for actual code / file-path / API-name examples; do");
            sb.AppendLine("  not wrap whole sections in code blocks.");
            sb.AppendLine();
            sb.AppendLine("IMPORTANT: the Markdown formatting requirement above applies to the GENERATED PROMPT");
            sb.AppendLine("(the system prompt you write between the delimiters). It does NOT change the inner");
            sb.AppendLine("\"OUTPUT FORMAT\" rule that the generated prompt itself imposes on the translator AI");
            sb.AppendLine("(\"translation only, no markdown formatting in the translation output\") – that rule");
            sb.AppendLine("governs what the translator's per-segment output looks like, and must remain in the");
            sb.AppendLine("generated prompt unchanged.");
            sb.AppendLine();
            sb.AppendLine("===PROMPT_START===");
            sb.AppendLine("(Your full prompt content here as proper Markdown – no JSON escaping needed)");
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
        public static string ParseGeneratedPrompt(string aiResponse)
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
            if (string.IsNullOrEmpty(content)) return null;

            // Collapse 3+ consecutive newlines into 2 (one blank line max between blocks).
            // Models often emit 2-3 blank lines around section headings even when not asked.
            content = Regex.Replace(content, @"(\r?\n[ \t]*){3,}", "\n\n");
            return content;
        }

        /// <summary>
        /// Builds a short display message for the chat bubble while the
        /// full meta-prompt (which may be very large) is sent to the AI.
        /// </summary>
        public static string BuildDisplayMessage(PromptGenerationContext ctx)
        {
            var domain = ctx.DetectedDomain ?? "general";
            var sb = new StringBuilder();
            sb.AppendLine("Analysing project and generating prompt...");
            sb.AppendLine();
            sb.AppendLine($"Domain: {char.ToUpper(domain[0])}{domain.Substring(1)}");
            sb.AppendLine($"Language pair: {ctx.SourceLang} \u2192 {ctx.TargetLang}");
            sb.AppendLine($"{Capitalise(UnitOf(ctx))}: {ctx.SegmentCount:N0}");

            if (ctx.TermbaseTerms != null && ctx.TermbaseTerms.Count > 0)
            {
                if (ctx.TotalTermCount > 0 && ctx.TotalTermCount != ctx.TermbaseTerms.Count)
                    sb.AppendLine($"Termbase terms: filtered {ctx.TermbaseTerms.Count:N0} relevant from {ctx.TotalTermCount:N0} total");
                else
                    sb.AppendLine($"Termbase terms: {ctx.TermbaseTerms.Count:N0}");
            }

            if (ctx.TmPairs != null && ctx.TmPairs.Count > 0)
                sb.AppendLine($"TM reference pairs: {ctx.TmPairs.Count:N0}");

            if (!string.IsNullOrWhiteSpace(ctx.KbContext))
                sb.AppendLine("Memory bank: included");

            return sb.ToString().TrimEnd();
        }

        // ─── Term filtering ─────────────────────────────────────────

        /// <summary>
        /// Filters term entries to only those whose source term (or source synonyms /
        /// source abbreviations) appear in at least one of the provided source segments.
        /// Uses simple case-insensitive substring matching for speed.
        /// </summary>
        public static List<TermEntry> FilterRelevantTerms(
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
                MatchesWholeWord(term.SourceTerm.ToUpperInvariant(), combinedUpper))
                return true;

            // Check source abbreviation variants
            if (!string.IsNullOrWhiteSpace(term.SourceAbbreviation))
            {
                foreach (var variant in term.GetSourceAbbreviationVariants())
                {
                    if (!string.IsNullOrEmpty(variant) &&
                        MatchesWholeWord(variant.Trim().ToUpperInvariant(), combinedUpper))
                        return true;
                }
            }

            // Check source synonyms (rich entries populated by editor)
            if (term.SourceSynonyms != null)
            {
                foreach (var syn in term.SourceSynonyms)
                {
                    if (!string.IsNullOrEmpty(syn.Text) &&
                        MatchesWholeWord(syn.Text.ToUpperInvariant(), combinedUpper))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns true if <paramref name="termUpper"/> appears in <paramref name="textUpper"/>
        /// as a whole word (bounded by non-alphanumeric characters or string edges).
        /// Multi-word terms (e.g. "PRIOR ART") are matched as a whole phrase.
        /// Falls back to substring match if the term contains regex-special characters
        /// that cannot be safely escaped (extremely rare in practice).
        /// </summary>
        private static bool MatchesWholeWord(string termUpper, string textUpper)
        {
            if (string.IsNullOrEmpty(termUpper)) return false;
            // Ignore single-character candidates (e.g. chemical symbols like "S", "C", "N",
            // or stray one-letter codes). They whole-word-match incidental lone letters in
            // the document and inject irrelevant terms – a "sulfur" entry carrying the
            // abbreviation "S" would otherwise match any standalone "S". Dropping
            // one-character candidates costs nothing (they are never useful glossary
            // entries); two-character abbreviations (UI, AI, ID, API…) are still matched.
            if (termUpper.Length < 2) return false;
            // A term containing CJK characters has no word boundary to anchor on: \w
            // includes them, and Chinese and Japanese text has no spaces, so \b never
            // occurs inside a run of them and a Chinese term in Chinese source never
            // matched (#102). Substring is the right test there - the language has no
            // word boundaries to respect.
            if (ContainsCjk(termUpper))
                return textUpper.IndexOf(termUpper, StringComparison.Ordinal) >= 0;
            try
            {
                // \b matches between \w and \W. For multi-word terms the spaces inside
                // are already non-\w, so \b…\b around the whole phrase is sufficient.
                var pattern = @"\b" + Regex.Escape(termUpper) + @"\b";
                return Regex.IsMatch(textUpper, pattern, RegexOptions.None);
            }
            catch
            {
                // Fallback for pathological term text
                return textUpper.Contains(termUpper);
            }
        }

        // ─── Private helpers ─────────────────────────────────────────

        private static bool ContainsCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var ch in s)
            {
                if ((ch >= '\u4E00' && ch <= '\u9FFF')      // CJK Unified Ideographs
                    || (ch >= '\u3400' && ch <= '\u4DBF')   // Extension A
                    || (ch >= '\u3040' && ch <= '\u30FF')   // Hiragana, Katakana
                    || (ch >= '\uAC00' && ch <= '\uD7AF'))  // Hangul syllables
                    return true;
            }
            return false;
        }

        private static string BuildTerminologySection(List<TermEntry> terms)
        {
            // No approved terms. The old instruction here asked for an EMPTY glossary
            // section – a lone "should" against three "MUST"s elsewhere in the meta-prompt
            // (the mandatory section list, universal rule 4's lock-every-recurring-term,
            // and the worked glossary table in the Markdown block), so the model built one
            // from the document anyway and the instruction was simply dead text. Deriving
            // a glossary from the source is in fact the point of AutoPrompt – with no
            // memory between batches, a locked glossary the model extracted beats no
            // terminology guidance at all. So say so plainly.
            //
            // What must NOT go in the glossary is a provenance caveat. The generated .md
            // is not a document a human reads and edits before use – it is shipped verbatim
            // as the system prompt to the translating AI. A "derived from source, verify
            // before use" line under the heading would therefore be read by the translator
            // AI, sitting directly beside "MANDATORY, LOCKED" and universal rule 4's ban on
            // leaving an open choice. It would license exactly the substitution the lock
            // exists to prevent. Provenance belongs in the prompt's YAML `description`,
            // which PromptLibrary parses out into a field shown only in the library panel
            // and QuickLauncher tooltip – see OnSaveAsPromptRequested.
            if (terms == null || terms.Count == 0)
            {
                var noTerms = new StringBuilder();
                noTerms.AppendLine("No approved termbase terms were supplied for this project – none of the");
                noTerms.AppendLine("translator's termbases is enabled for AI.");
                noTerms.AppendLine();
                noTerms.AppendLine("Do NOT emit an empty glossary. Build the PROJECT-SPECIFIC GLOSSARY section by");
                noTerms.AppendLine("identifying the recurring, project-defining terminology in the document content");
                noTerms.AppendLine("above: the terms whose translation must stay identical across every batch");
                noTerms.AppendLine("(components, processes, materials, terms of art, and any term whose obvious");
                noTerms.AppendLine("dictionary equivalent would be wrong in this domain). Because there is no memory");
                noTerms.AppendLine("between batches, a locked glossary derived from the source is far more valuable");
                noTerms.AppendLine("than none.");
                noTerms.AppendLine();
                noTerms.AppendLine("Treat that glossary as fully binding: it is MANDATORY and LOCKED on exactly the");
                noTerms.AppendLine("same terms as one supplied from a termbase. Do NOT hedge it, do NOT mark it");
                noTerms.AppendLine("provisional or unverified, do NOT add a note about where the terms came from,");
                noTerms.AppendLine("and do NOT offer alternatives for any entry. The prompt you generate is sent");
                noTerms.AppendLine("verbatim to the translating AI, and any such caveat would invite exactly the");
                noTerms.AppendLine("term substitution the lock exists to prevent. Choose each target term with that");
                noTerms.AppendLine("in mind: commit only to entries you are confident enough to lock.");
                return noTerms.ToString();
            }

            var sb = new StringBuilder();
            sb.AppendLine($"The following {terms.Count} terms are from the project's termbase(s).");
            sb.AppendLine("Include ALL of them in the PROJECT-SPECIFIC GLOSSARY section of the generated prompt.");
            sb.AppendLine("Mark the glossary as MANDATORY and LOCKED – no substitutions or variants permitted.");
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
            // Observed on a real run: given 7 pairs, the model emitted 11 and filed its own
            // renderings under "Additional validated project segments" – one of them carrying
            // a [[TC:]] marker, which no human TM contains. This section outranks the glossary
            // in the terminology hierarchy, so an invented entry is the most authoritative
            // thing in the prompt while being the least grounded. Saying "include them" was
            // never the same as saying "and no others"; both branches now say the latter.
            if (tmPairs == null || tmPairs.Count == 0)
                return "No TM reference translations are available for this project. The generated prompt " +
                       "must include a PREVIOUS CORRECT TRANSLATIONS section that says so plainly and lists " +
                       "no pairs.\n\n" +
                       "Do NOT invent, reconstruct or supply example pairs to fill it. This section records " +
                       "human-validated translations only and outranks the glossary, so a fabricated entry " +
                       "is presented to the translator AI as approved by the translator when nobody has " +
                       "approved it.";

            var sb = new StringBuilder();
            sb.AppendLine($"The following {tmPairs.Count} validated translation pairs come from the project's");
            sb.AppendLine("Translation Memory. Include them in the PREVIOUS CORRECT TRANSLATIONS section.");
            sb.AppendLine("These serve as style anchors – the AI must match their register and terminology choices.");
            sb.AppendLine();
            sb.AppendLine($"Include EXACTLY these {tmPairs.Count} pairs and no others. Never invent, extrapolate");
            sb.AppendLine("or add a pair that is not listed below, and never promote a rendering you chose");
            sb.AppendLine("yourself into this section – not as an \"additional validated segment\", not as an");
            sb.AppendLine("example, not in any other guise. These are human-validated translations and they");
            sb.AppendLine("outrank the glossary, so anything you add here is presented to the translator AI as");
            sb.AppendLine("approved by the translator when nobody has approved it. A rendering you derived");
            sb.AppendLine("yourself belongs in the glossary or a style section, never in this one.");
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

            // Send the full document – user confirmed cost is acceptable
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
    public class PromptGenerationContext
    {
        public string SourceLang { get; set; }
        public string TargetLang { get; set; }
        public string DetectedDomain { get; set; }
        public string AnalysisSummary { get; set; }
        public int SegmentCount { get; set; }

        /// <summary>
        /// What <see cref="SegmentCount"/> counts: "segments" unless the host says
        /// otherwise. memoQ's live-document link hands over paragraphs, each of
        /// which holds one or more segments, and a count of 170 presented to the
        /// model as segments told it the document was half its size when deciding
        /// how much material to lock. Trados counts real segments and leaves this
        /// at the default.
        /// </summary>
        public string SegmentUnit { get; set; } = "segments";

        public List<string> SourceSegments { get; set; }
        public List<TermEntry> TermbaseTerms { get; set; }
        public List<TmMatch> TmPairs { get; set; }

        /// <summary>
        /// Total number of termbase terms before relevance filtering.
        /// Used by BuildDisplayMessage to show "Filtered X relevant terms from Y total".
        /// Zero means no filtering was applied.
        /// </summary>
        public int TotalTermCount { get; set; }

        /// <summary>
        /// Optional SuperMemory knowledge base context (formatted text).
        /// When present, included in the meta-prompt so the generated prompt
        /// reflects established client conventions, terminology reasoning,
        /// and style guides.
        /// </summary>
        public string KbContext { get; set; }

        /// <summary>
        /// Optional free-text briefing the translator supplied in the AutoPrompt
        /// context dialog. Injected as an authoritative context block so it
        /// overrides the inferred domain where they conflict.
        /// </summary>
        public string UserContextHint { get; set; }

        /// <summary>
        /// Optional block describing how the HOST delivers text to the model and
        /// what it does with the reply — injected into the meta-prompt as an
        /// overriding section, so the generated prompt is written for the
        /// runtime it will actually run in.
        ///
        /// Added for memoQ. The meta-prompt's defaults describe the Trados
        /// plugin: numbered batches, an inline [[TC:]] comment channel that
        /// downstream tooling extracts, TM matches supplied per request. None
        /// of that is true in memoQ, where every character the model returns
        /// lands verbatim in the target cell and the plugin re-sends the whole
        /// prompt with every ten-segment request. Null keeps Trados behaviour
        /// exactly as it was.
        /// </summary>
        public string HostConstraints { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Manages the prompt template library: loading, saving, and built-in prompt seeding.
    /// Prompts are stored as .svprompt files in the shared UserDataPath.PromptLibraryDir,
    /// which is the same folder Supervertaler Workbench uses — so prompts are automatically
    /// shared between both products.
    /// </summary>
    public class PromptLibrary
    {
        private static string PromptsDir => UserDataPath.PromptLibraryDir;

        /// <summary>
        /// Full path to the prompts folder on disk.
        /// </summary>
        public static string PromptsFolderPath => UserDataPath.PromptLibraryDir;

        private List<PromptTemplate> _cache;

        /// <summary>
        /// Returns all prompts from the library (cached; call Refresh() to reload).
        /// </summary>
        public List<PromptTemplate> GetAllPrompts()
        {
            if (_cache == null)
                Refresh();
            return _cache;
        }

        /// <summary>
        /// Reloads all prompts from the shared prompt_library folder.
        /// Both Workbench and this plugin read from the same location, so there is
        /// no longer a separate "desktop prompts" scan.
        /// </summary>
        public void Refresh()
        {
            _cache = new List<PromptTemplate>();

            if (Directory.Exists(PromptsDir))
                ScanDirectory(PromptsDir, PromptsDir, isReadOnly: false);
        }

        /// <summary>
        /// Finds a prompt by its relative path. Returns null if not found.
        /// </summary>
        public PromptTemplate GetPromptByRelativePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return null;

            foreach (var p in GetAllPrompts())
            {
                if (string.Equals(p.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        /// <summary>
        /// Applies variable substitution to prompt content.
        /// Supports both {{UPPERCASE}} and {lowercase} placeholder formats.
        /// </summary>
        public static string ApplyVariables(string content, string sourceLang, string targetLang)
        {
            return ApplyVariables(content, sourceLang, targetLang, null, null, null);
        }

        /// <summary>
        /// Applies variable substitution to prompt content, including segment-level variables.
        /// Supports both {{UPPERCASE}} and {lowercase} placeholder formats.
        /// </summary>
        public static string ApplyVariables(string content, string sourceLang, string targetLang,
            string sourceText, string targetText, string selection)
        {
            if (string.IsNullOrEmpty(content))
                return content;

            // {{UPPERCASE}} format (Python Supervertaler / Workbench standard)
            content = content.Replace("{{SOURCE_LANGUAGE}}", sourceLang ?? "");
            content = content.Replace("{{TARGET_LANGUAGE}}", targetLang ?? "");
            content = content.Replace("{{SOURCE_TEXT}}", sourceText ?? "");
            content = content.Replace("{{TARGET_TEXT}}", targetText ?? "");
            content = content.Replace("{{SELECTION}}", selection ?? "");

            // {lowercase} format (legacy compatibility with Python domain prompts)
            content = content.Replace("{source_lang}", sourceLang ?? "");
            content = content.Replace("{target_lang}", targetLang ?? "");

            return content;
        }

        /// <summary>
        /// Returns all prompts that should appear in the QuickLauncher right-click menu.
        /// </summary>
        public List<PromptTemplate> GetQuickLauncherPrompts()
        {
            var all = GetAllPrompts();
            var result = new List<PromptTemplate>();
            foreach (var p in all)
            {
                if (p.IsQuickLauncher)
                    result.Add(p);
            }
            return result;
        }

        /// <summary>
        /// Saves a prompt template to disk as a Markdown file with YAML frontmatter.
        /// Creates directories as needed.
        /// </summary>
        public void SavePrompt(PromptTemplate prompt)
        {
            if (prompt == null || string.IsNullOrEmpty(prompt.Name))
                return;

            // Determine file path
            string filePath;
            if (!string.IsNullOrEmpty(prompt.FilePath) && !prompt.IsReadOnly)
            {
                filePath = prompt.FilePath;
            }
            else
            {
                // New prompt: build path from domain + name
                var folder = string.IsNullOrEmpty(prompt.Domain)
                    ? PromptsDir
                    : Path.Combine(PromptsDir, SanitizeFileName(prompt.Domain));
                Directory.CreateDirectory(folder);
                filePath = Path.Combine(folder, SanitizeFileName(prompt.Name) + ".svprompt");
            }

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("name: \"" + EscapeYamlString(prompt.Name) + "\"");
            if (!string.IsNullOrEmpty(prompt.Description))
                sb.AppendLine("description: \"" + EscapeYamlString(prompt.Description) + "\"");
            if (!string.IsNullOrEmpty(prompt.Domain))
                sb.AppendLine("category: \"" + EscapeYamlString(prompt.Domain) + "\"");
            if (prompt.IsBuiltIn)
                sb.AppendLine("built_in: true");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append(prompt.Content ?? "");

            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);

            prompt.FilePath = filePath;
            prompt.RelativePath = GetRelativePath(filePath, PromptsDir);

            Refresh();
        }

        /// <summary>
        /// Deletes a prompt file from disk.
        /// </summary>
        public void DeletePrompt(PromptTemplate prompt)
        {
            if (prompt == null || prompt.IsReadOnly || string.IsNullOrEmpty(prompt.FilePath))
                return;

            if (File.Exists(prompt.FilePath))
                File.Delete(prompt.FilePath);

            Refresh();
        }

        /// <summary>
        /// Ensures built-in prompts exist in the prompts directory.
        /// Creates any that are missing (idempotent — safe to call on every startup).
        /// </summary>
        public void EnsureBuiltInPrompts()
        {
            Directory.CreateDirectory(PromptsDir);

            foreach (var builtin in GetBuiltInPromptDefinitions())
            {
                var folder = string.IsNullOrEmpty(builtin.Domain)
                    ? PromptsDir
                    : Path.Combine(PromptsDir, builtin.Domain);
                Directory.CreateDirectory(folder);

                var filePath = Path.Combine(folder, SanitizeFileName(builtin.Name) + ".svprompt");
                if (!File.Exists(filePath))
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("---");
                    sb.AppendLine("name: \"" + EscapeYamlString(builtin.Name) + "\"");
                    if (!string.IsNullOrEmpty(builtin.Description))
                        sb.AppendLine("description: \"" + EscapeYamlString(builtin.Description) + "\"");
                    if (!string.IsNullOrEmpty(builtin.Domain))
                        sb.AppendLine("domain: \"" + EscapeYamlString(builtin.Domain) + "\"");
                    sb.AppendLine("built_in: true");
                    sb.AppendLine("---");
                    sb.AppendLine();
                    sb.Append(builtin.Content);

                    File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
                }
            }

            // Invalidate cache so next GetAllPrompts() reloads
            _cache = null;
        }

        /// <summary>
        /// Restores all built-in prompts (overwrites any user edits).
        /// </summary>
        public void RestoreBuiltInPrompts()
        {
            foreach (var builtin in GetBuiltInPromptDefinitions())
            {
                var folder = string.IsNullOrEmpty(builtin.Domain)
                    ? PromptsDir
                    : Path.Combine(PromptsDir, builtin.Domain);
                Directory.CreateDirectory(folder);

                var filePath = Path.Combine(folder, SanitizeFileName(builtin.Name) + ".svprompt");

                var sb = new StringBuilder();
                sb.AppendLine("---");
                sb.AppendLine("name: \"" + EscapeYamlString(builtin.Name) + "\"");
                if (!string.IsNullOrEmpty(builtin.Description))
                    sb.AppendLine("description: \"" + EscapeYamlString(builtin.Description) + "\"");
                if (!string.IsNullOrEmpty(builtin.Domain))
                    sb.AppendLine("domain: \"" + EscapeYamlString(builtin.Domain) + "\"");
                sb.AppendLine("built_in: true");
                sb.AppendLine("---");
                sb.AppendLine();
                sb.Append(builtin.Content);

                File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            }

            _cache = null;
        }

        // ─── Private Methods ─────────────────────────────────────────

        private void ScanDirectory(string dir, string rootDir, bool isReadOnly)
        {
            try
            {
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Scan .svprompt files first (preferred format)
                foreach (var file in Directory.GetFiles(dir, "*.svprompt", SearchOption.AllDirectories))
                {
                    try
                    {
                        var prompt = ParsePromptFile(file, rootDir);
                        if (prompt != null)
                        {
                            prompt.IsReadOnly = isReadOnly;
                            _cache.Add(prompt);
                            seenNames.Add(prompt.Name);
                        }
                    }
                    catch
                    {
                        // Skip files that can't be parsed
                    }
                }

                // Also scan .md files (legacy format) — skip if .svprompt version exists
                foreach (var file in Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories))
                {
                    try
                    {
                        var prompt = ParsePromptFile(file, rootDir);
                        if (prompt != null && !seenNames.Contains(prompt.Name))
                        {
                            prompt.IsReadOnly = isReadOnly;
                            _cache.Add(prompt);
                        }
                    }
                    catch
                    {
                        // Skip files that can't be parsed
                    }
                }
            }
            catch
            {
                // Skip directories that can't be accessed
            }
        }

        /// <summary>
        /// Parses a Markdown file with optional YAML frontmatter.
        /// YAML is parsed as simple key: "value" pairs (no external library needed).
        /// </summary>
        private PromptTemplate ParsePromptFile(string filePath, string rootDir)
        {
            var text = File.ReadAllText(filePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var prompt = new PromptTemplate
            {
                FilePath = filePath,
                RelativePath = GetRelativePath(filePath, rootDir)
            };

            // Parse YAML frontmatter (between --- delimiters)
            if (text.TrimStart().StartsWith("---"))
            {
                var idx1 = text.IndexOf("---", StringComparison.Ordinal);
                var idx2 = text.IndexOf("---", idx1 + 3, StringComparison.Ordinal);

                if (idx2 > idx1)
                {
                    var yaml = text.Substring(idx1 + 3, idx2 - idx1 - 3);
                    ParseYamlFrontmatter(prompt, yaml);

                    // Content is everything after the second ---
                    var contentStart = idx2 + 3;
                    prompt.Content = text.Substring(contentStart).TrimStart('\r', '\n');
                }
                else
                {
                    // Malformed frontmatter — treat entire file as content
                    prompt.Content = text;
                }
            }
            else
            {
                // No frontmatter — entire file is content
                prompt.Content = text;
            }

            // Fallback: use filename if no name in frontmatter
            if (string.IsNullOrEmpty(prompt.Name))
                prompt.Name = Path.GetFileNameWithoutExtension(filePath);

            // Fallback: use folder name as domain if not specified in YAML
            if (string.IsNullOrEmpty(prompt.Domain))
            {
                var relDir = Path.GetDirectoryName(prompt.RelativePath);
                if (!string.IsNullOrEmpty(relDir))
                    prompt.Domain = relDir;
            }

            // Normalise domain regardless of whether it came from YAML or folder name
            NormaliseDomain(prompt);

            return prompt;
        }

        /// <summary>
        /// Applies canonical normalisation to prompt.Domain and sets IsQuickLauncher.
        /// Called after both YAML parsing and the folder-name fallback so the logic
        /// is consistent regardless of how the domain was determined.
        /// </summary>
        private static void NormaliseDomain(PromptTemplate prompt)
        {
            if (string.IsNullOrEmpty(prompt.Domain)) return;

            // Normalise legacy names → canonical "QuickLauncher"
            if (prompt.Domain.Equals("quickmenu_prompts", StringComparison.OrdinalIgnoreCase) ||
                prompt.Domain.Equals("quicklauncher_prompts", StringComparison.OrdinalIgnoreCase))
            {
                prompt.Domain = "QuickLauncher";
            }

            if (prompt.Domain.Equals("QuickLauncher", StringComparison.OrdinalIgnoreCase))
                prompt.IsQuickLauncher = true;
        }

        private void ParseYamlFrontmatter(PromptTemplate prompt, string yaml)
        {
            var lines = yaml.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;

                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx <= 0)
                    continue;

                var key = trimmed.Substring(0, colonIdx).Trim().ToLowerInvariant();
                var value = trimmed.Substring(colonIdx + 1).Trim();

                // Strip quotes
                if (value.Length >= 2 &&
                    ((value.StartsWith("\"") && value.EndsWith("\"")) ||
                     (value.StartsWith("'") && value.EndsWith("'"))))
                {
                    value = value.Substring(1, value.Length - 2);
                }

                switch (key)
                {
                    case "name":
                        prompt.Name = value;
                        break;
                    case "description":
                        prompt.Description = value;
                        break;
                    case "category":
                    case "domain": // backward compatibility
                        prompt.Domain = value;
                        // Full normalisation (legacy names → QuickLauncher, IsQuickLauncher flag)
                        // runs after all YAML is parsed, in NormaliseDomain().
                        break;
                    case "built_in":
                        prompt.IsBuiltIn = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "sv_quicklauncher":
                    case "sv_quickmenu": // legacy alias — kept for backward compatibility
                        if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
                            prompt.IsQuickLauncher = true;
                        break;
                    case "quickmenu_label":
                        prompt.QuickLauncherLabel = value;
                        break;
                }
            }
        }

        private static string GetRelativePath(string fullPath, string rootDir)
        {
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(rootDir))
                return fullPath;

            var root = rootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(root.Length);

            return fullPath;
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (Array.IndexOf(invalid, c) >= 0)
                    sb.Append('_');
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static string EscapeYamlString(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // ─── Built-in Prompt Definitions ──────────────────────────────

        private List<PromptTemplate> GetBuiltInPromptDefinitions()
        {
            return new List<PromptTemplate>
            {
                // ─── Domain Expertise ─────────────────────────────────
                new PromptTemplate
                {
                    Name = "Medical Translation Specialist",
                    Description = "Specialized for medical and healthcare translation",
                    Domain = "Translate",
                    IsBuiltIn = true,
                    Content = @"You are a medical translation specialist with extensive knowledge of medical terminology, anatomy, pharmacology, and healthcare procedures. Your task is to translate medical content from {source_lang} to {target_lang} with the highest level of accuracy and precision.

Key requirements:
- Maintain exact medical terminology and drug names (verify generic/brand name accuracy)
- Preserve dosages, measurements, and medical units precisely
- Follow target language medical convention standards
- Ensure patient safety by never altering critical medical information
- Use appropriate medical register and formality level
- Maintain consistency in anatomical terms and medical procedures
- Consider regulatory and legal implications of medical translations

Special attention to:
- Drug interactions and contraindications
- Diagnostic criteria and clinical guidelines
- Patient consent forms and medical legal documents
- Medical device instructions and safety warnings
- Clinical trial protocols and research documentation"
                },
                new PromptTemplate
                {
                    Name = "Legal Translation Specialist",
                    Description = "Specialized for legal and juridical translation",
                    Domain = "Translate",
                    IsBuiltIn = true,
                    Content = @"You are a legal translation specialist with deep expertise in comparative law, legal systems, and juridical terminology. Your task is to translate legal content from {source_lang} to {target_lang} with absolute precision and legal accuracy.

Key requirements:
- Preserve exact legal terminology and maintain legal precision
- Consider differences between legal systems (common law vs. civil law)
- Maintain formal legal register and appropriate juridical tone
- Preserve legal concepts even when direct equivalents don't exist
- Flag legal terms that may require legal system adaptation
- Ensure compliance with target jurisdiction's legal conventions
- Maintain chronological and procedural accuracy
- Preserve legal citations and references appropriately

Special attention to:
- Contractual terms and conditions
- Legal procedures and court processes
- Rights, obligations, and legal consequences
- Statutory references and legal citations
- Corporate and commercial law terminology
- International law and cross-border implications"
                },
                new PromptTemplate
                {
                    Name = "Patent Translation Specialist",
                    Description = "Specialized for patent and intellectual property translation",
                    Domain = "Translate",
                    IsBuiltIn = true,
                    Content = @"You are an expert {source_lang} to {target_lang} patent translator with deep expertise in intellectual property, technical terminology, and patent law requirements.

Key patent translation principles:
- Maintain technical precision and legal accuracy
- Preserve claim structure and dependency relationships
- Use consistent terminology throughout (especially for technical terms)
- Ensure numerical references, measurements, and chemical formulas remain accurate
- Maintain the formal, precise tone required for patent documentation
- If a sentence refers to figures or drawings (e.g., 'Figure 1A', 'FIG. 2B'), use context to accurately translate references to components and structural relationships

Special attention to:
- Patent claims (independent and dependent) — preserve exact scope
- Technical descriptions and specifications
- Abstract and summary sections
- Prior art references
- Inventor and applicant information"
                },
                new PromptTemplate
                {
                    Name = "Financial Translation Specialist",
                    Description = "Specialized for financial and banking translation",
                    Domain = "Translate",
                    IsBuiltIn = true,
                    Content = @"You are a financial translation specialist with expertise in banking, investment, financial markets, and regulatory compliance. Your task is to translate financial content from {source_lang} to {target_lang} with precision and market-appropriate terminology.

Key requirements:
- Maintain exact financial figures, percentages, and calculations
- Use appropriate financial terminology and market conventions
- Preserve regulatory compliance language and requirements
- Follow target market's financial reporting standards
- Maintain consistency in financial instrument names and terminology
- Ensure accuracy in currency codes, exchange rates, and financial data
- Consider cross-border financial regulations and compliance
- Preserve risk disclosures and legal financial obligations

Special attention to:
- Financial statements and accounting principles
- Investment products and risk disclosures
- Banking procedures and regulatory requirements
- Market analysis and financial forecasting
- Tax implications and regulatory compliance
- Insurance and actuarial terminology
- Corporate finance and M&A documentation"
                },
                new PromptTemplate
                {
                    Name = "Technical Translation Specialist",
                    Description = "Specialized for engineering and technical documentation",
                    Domain = "Translate",
                    IsBuiltIn = true,
                    Content = @"You are a technical translation specialist with extensive knowledge of engineering, manufacturing, and industrial processes. Your task is to translate technical content from {source_lang} to {target_lang} with precision and clarity.

Key requirements:
- Maintain exact technical terminology for the relevant engineering discipline
- Preserve measurements, specifications, and tolerances precisely
- Follow target language technical writing conventions and standards
- Ensure safety-critical information is translated with absolute accuracy
- Use consistent terminology throughout the document
- Preserve references to technical standards (ISO, DIN, ASTM, etc.)

Special attention to:
- Operating and maintenance manuals
- Safety instructions and warnings
- Technical specifications and datasheets
- Assembly and installation guides
- Quality control and testing procedures
- Engineering drawings and diagram references"
                },
                new PromptTemplate
                {
                    Name = "Marketing & Creative Specialist",
                    Description = "Specialized for marketing copy and creative content",
                    Domain = "Translate",
                    IsBuiltIn = true,
                    Content = @"You are a marketing and creative translation specialist (transcreator) with expertise in adapting persuasive content across cultures. Your task is to translate marketing content from {source_lang} to {target_lang} while preserving its persuasive impact and cultural relevance.

Key requirements:
- Adapt messaging for cultural relevance in the target market
- Preserve brand voice and tone while making it natural in the target language
- Maintain the persuasive impact and call-to-action effectiveness
- Adapt idioms, wordplay, and cultural references appropriately
- Preserve SEO keywords where applicable (adapt for target market search behavior)
- Maintain visual and typographic considerations (text length for layouts)

Special attention to:
- Headlines, taglines, and slogans
- Product descriptions and USPs
- Social media content and campaigns
- Email marketing copy
- Website and landing page content
- Press releases and corporate communications"
                },
                new PromptTemplate
                {
                    Name = "IT & Software Localization Specialist",
                    Description = "Specialized for software UI and IT documentation",
                    Domain = "Translate",
                    IsBuiltIn = true,
                    Content = @"You are an IT and software localization specialist with expertise in translating user interfaces, technical documentation, and software-related content. Your task is to translate IT content from {source_lang} to {target_lang} with technical accuracy and user-friendly language.

Key requirements:
- Use established target-language software terminology (menus, buttons, dialogs)
- Maintain consistency with platform conventions (Windows, macOS, Linux, mobile)
- Preserve code snippets, API references, and technical identifiers untranslated
- Adapt UI strings for appropriate length (button labels, menu items)
- Maintain placeholder syntax and variables (e.g., {0}, %s, {{variable}})
- Follow target language software localization standards

Special attention to:
- User interface strings (buttons, menus, tooltips, error messages)
- Help documentation and knowledge base articles
- API documentation and developer guides
- Release notes and changelogs
- System administration guides
- Cloud and SaaS terminology"
                },

                // ─── Style Guides ──────────────────────────────────────
                new PromptTemplate
                {
                    Name = "Dutch Style Guide",
                    Description = "Number formatting and style conventions for Dutch",
                    Domain = "Translate",
                    IsBuiltIn = true,
                    Content = @"# Dutch Style Guide

## Number Formatting
- **Thousand separator:** 10.000 (period)
- **Decimal separator:** 1,5 (comma)
- **Negative numbers:** -1 (hyphen)

## Units and Measurements
- **Space between number and unit:** 25 °C (space before symbol)
- **Angles:** 90° (no space)
- **Dimensions:** 25 cm (space)
- **Percentages:** 25,5% (no space)

## Ranges and Mathematical Expressions
- **Ranges:** 7–8 m or 7 m – 8 m (en dash)
- **Range with percentages:** 7%–8% (en dash, no spaces)
- **Plus:** 3 cm + 1 cm (spaces around operator)
- **Multiply:** 2 × 2 (multiplication sign)

## Terminology and Style
- Use consistent terminology throughout the document
- Maintain technical accuracy while ensuring clarity
- Follow Dutch grammar rules for compound words
- Use formal register for technical documentation
- Use 'u' (formal) or 'je/jij' (informal) consistently based on context"
                },
                new PromptTemplate
                {
                    Name = "English Style Guide",
                    Description = "Number formatting and style conventions for English",
                    Domain = "Translate",
                    IsBuiltIn = true,
                    Content = @"# English Style Guide

## Number Formatting
- **Thousand separator:** 10,000 (comma)
- **Decimal separator:** 1.5 (period/full stop)
- **Negative numbers:** -1 (hyphen)

## Units and Measurements
- **Space between number and unit:** 25 °C (space before symbol)
- **Angles:** 90° (no space)
- **Dimensions:** 25 cm (space)
- **Percentages:** 25.5% (no space)

## Ranges
- **Ranges:** 7–8 m or 7 m – 8 m (en dash)
- **Range with percentages:** 7%–8% (en dash, no spaces)

## Terminology and Style
- Use Oxford comma in lists (A, B, and C)
- Maintain consistent US or UK English throughout
- Use active voice where possible
- Follow title case for headings"
                },
                new PromptTemplate
                {
                    Name = "French Style Guide",
                    Description = "Number formatting and style conventions for French",
                    Domain = "Translate",
                    IsBuiltIn = true,
                    Content = @"# French Style Guide

## Number Formatting
- **Thousand separator:** 10 000 (non-breaking space)
- **Decimal separator:** 1,5 (comma)
- **Negative numbers:** −1 (minus sign)

## Units and Measurements
- **Space between number and unit:** 25 °C (non-breaking space)
- **Percentages:** 25,5 % (space before %)

## Punctuation
- **Thin non-breaking space before:** ; : ! ? » and after «
- **Guillemets for quotes:** « texte » (with non-breaking spaces)

## Terminology and Style
- Use 'vous' for formal/professional contexts
- Follow Académie française recommendations
- Use recommended French terminology over anglicisms where official equivalents exist"
                },
                new PromptTemplate
                {
                    Name = "German Style Guide",
                    Description = "Number formatting and style conventions for German",
                    Domain = "Translate",
                    IsBuiltIn = true,
                    Content = @"# German Style Guide

## Number Formatting
- **Thousand separator:** 10.000 (period)
- **Decimal separator:** 1,5 (comma)
- **Negative numbers:** −1 (minus sign)

## Units and Measurements
- **Space between number and unit:** 25 °C (space before symbol)
- **Percentages:** 25,5 % (space before %)

## Punctuation
- **Quotation marks:** use low-high style or guillemets as appropriate

## Terminology and Style
- Follow compound word rules (Zusammenschreibung)
- Use formal address (Sie) for professional contexts
- Follow Duden recommendations for spelling
- Maintain gender-appropriate language (Gendern) when required by client style guide"
                },
                new PromptTemplate
                {
                    Name = "Spanish Style Guide",
                    Description = "Number formatting and style conventions for Spanish",
                    Domain = "Translate",
                    IsBuiltIn = true,
                    Content = @"# Spanish Style Guide

## Number Formatting
- **Thousand separator:** 10.000 (period) or 10 000 (space)
- **Decimal separator:** 1,5 (comma)
- **Negative numbers:** −1 (minus sign)

## Units and Measurements
- **Space between number and unit:** 25 °C (space before symbol)
- **Percentages:** 25,5 % (space before %)

## Punctuation
- **Opening marks:** ¿ for questions, ¡ for exclamations (always include)
- **Quotation marks:** «text» (guillemets) or ""text"" (angular quotes)

## Terminology and Style
- Use 'usted' for formal or 'tú' for informal, consistently
- Follow RAE recommendations
- Distinguish between European Spanish and Latin American variants when specified"
                },

                // ─── Project Prompts ────────────────────────────────────
                new PromptTemplate
                {
                    Name = "Professional Tone & Style",
                    Description = "Maintain formal, business-appropriate language",
                    Domain = "Translate",
                    IsBuiltIn = true,
                    Content = @"IMPORTANT: Maintain a professional, formal tone throughout the translation. Use business-appropriate language and avoid colloquialisms or casual expressions.

Guidelines:
- Use formal register and respectful forms of address
- Prefer established business terminology over informal alternatives
- Maintain consistency in style and formality level
- Avoid contractions and informal abbreviations
- Use complete sentences with proper grammar"
                },
                new PromptTemplate
                {
                    Name = "Preserve Formatting & Layout",
                    Description = "Strict formatting preservation for layout-sensitive content",
                    Domain = "Translate",
                    IsBuiltIn = true,
                    Content = @"CRITICAL FORMATTING REQUIREMENT:
Preserve ALL formatting elements exactly as they appear in the source:
- Line breaks and paragraph boundaries
- Bullet points and numbering
- Indentation and spacing
- Special characters and punctuation marks
- Whitespace patterns and alignment

Translate only the text content while keeping ALL formatting identical to the source."
                },

                // ─── Proofreading ─────────────────────────────────────────
                new PromptTemplate
                {
                    Name = "Default Proofreading Prompt",
                    Description = "Reviews translations for accuracy, completeness, terminology, grammar, and style issues",
                    Domain = "Proofread",
                    IsBuiltIn = true,
                    Content = @"You are a professional translation proofreader. Your task is to review {{SOURCE_LANGUAGE}} to {{TARGET_LANGUAGE}} translation pairs and identify issues. You must check EVERY segment provided — do not skip any.

For each segment, check the following:

## 1. Accuracy
- Does the translation faithfully convey the meaning of the source?
- Are there any mistranslations, shifts in meaning, or misinterpretations?
- Are ambiguous source phrases resolved appropriately for the target language?

## 2. Completeness
- Is any source content omitted in the translation?
- Is any content added that is not present in the source?
- Are all numbers, dates, and references carried over correctly?

## 3. Terminology Consistency
- Are key terms translated consistently across segments?
- Are domain-specific terms translated correctly?
- Are proper nouns, brand names, and product names handled appropriately?

## 4. Grammar & Style
- Is the translation grammatically correct in {{TARGET_LANGUAGE}}?
- Is the style appropriate for the text type and register?
- Is the sentence structure natural and fluent in the target language?

## 5. Number & Unit Formatting
- Are numbers formatted according to {{TARGET_LANGUAGE}} conventions (decimal separators, thousand separators)?
- Are units of measurement correct and properly formatted?
- Are currency symbols and codes appropriate for the target locale?

## Language-Specific Checks

### Dutch
- Compound words: verify correct spelling (e.g., 'ziekenhuisopname' not 'ziekenhuis opname')
- dt-errors: check verb conjugation (word/wordt, vind/vindt, etc.)
- de/het articles: verify correct article usage with nouns
- Spelling: follow current Woordenlijst Nederlandse Taal (het Groene Boekje)

### German
- Compound nouns: verify correct formation (Zusammenschreibung)
- Capitalization: all nouns must be capitalized
- Case system: check correct use of Nominativ, Akkusativ, Dativ, Genitiv
- Verb position: verify correct verb placement in main and subordinate clauses

### French
- Accents: verify all accents are correct (é, è, ê, ë, à, ç, etc.)
- Gender/number agreement: check adjective-noun and subject-verb agreement
- Punctuation spacing: non-breaking space before ; : ! ? and inside « »
- Elision and liaison rules

## Output Format

You MUST use this exact format for every segment. Check ALL segments — do not skip any.

For segments with no issues:
[SEGMENT XXXX] OK

For segments with issues:
[SEGMENT XXXX] ISSUE
Issue: <brief description of the problem>
Suggestion: <describe what should be changed — do NOT provide a full corrected translation>

IMPORTANT RULES:
- NEVER provide corrected full translations. Only describe the issue and suggest what specifically should be fixed.
- Use the segment number as it appears in the input (e.g., [SEGMENT 0042]).
- Report each distinct issue on its own ISSUE block if a segment has multiple problems.
- You MUST review ALL segments. Do not stop early or summarize remaining segments as 'OK'."
                }
            };
        }
    }
}

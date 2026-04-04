using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Reads and queries the SuperMemory Obsidian vault.
    /// Scans frontmatter for lightweight indexing, then loads full articles on demand.
    /// </summary>
    public class SuperMemoryReader
    {
        private readonly string _vaultDir;
        private List<KbArticleIndex> _index;
        private DateTime _indexBuiltAt;

        // Folders to scan for KB articles (skip 00_INBOX, 05_INDICES, 06_TEMPLATES)
        private static readonly string[] ContentFolders =
            { "01_CLIENTS", "02_TERMINOLOGY", "03_DOMAINS", "04_STYLE" };

        public SuperMemoryReader(string vaultDir)
        {
            _vaultDir = vaultDir;
        }

        /// <summary>
        /// Returns true if the SuperMemory vault exists and has content folders.
        /// </summary>
        public bool VaultExists =>
            Directory.Exists(_vaultDir) &&
            ContentFolders.Any(f => Directory.Exists(Path.Combine(_vaultDir, f)));

        /// <summary>
        /// Builds or refreshes the lightweight frontmatter index.
        /// Only re-scans if the index is older than 30 seconds.
        /// </summary>
        public void RefreshIndex(bool force = false)
        {
            if (!force && _index != null && (DateTime.UtcNow - _indexBuiltAt).TotalSeconds < 30)
                return;

            var entries = new List<KbArticleIndex>();

            foreach (var folder in ContentFolders)
            {
                var dir = Path.Combine(_vaultDir, folder);
                if (!Directory.Exists(dir)) continue;

                foreach (var file in Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName.StartsWith("_EXAMPLE_", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (fileName.StartsWith("_archive", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Skip files in _archive subdirectories
                    var relPath = file.Substring(_vaultDir.Length).TrimStart('\\', '/');
                    if (relPath.Contains("_archive")) continue;

                    try
                    {
                        var entry = new KbArticleIndex
                        {
                            FilePath = file,
                            RelativePath = relPath,
                            Folder = folder,
                            FileName = fileName
                        };

                        // Read just the frontmatter (first ~1KB is enough)
                        var head = ReadHead(file, 2048);
                        entry.Frontmatter = ParseFrontmatter(head);
                        entry.FileSizeBytes = new FileInfo(file).Length;

                        entries.Add(entry);
                    }
                    catch
                    {
                        // Skip unreadable files
                    }
                }
            }

            _index = entries;
            _indexBuiltAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Loads relevant KB context for a translation based on project name, domain, and language pair.
        /// Returns null if the vault doesn't exist or has no relevant content.
        /// </summary>
        public KbContext LoadContext(
            string projectName,
            string domain,
            string sourceLang,
            string targetLang,
            int tokenBudget = 4000,
            string manualClientProfile = null)
        {
            if (!VaultExists) return null;

            RefreshIndex();
            if (_index == null || _index.Count == 0) return null;

            var ctx = new KbContext();

            // ── Step 1: Resolve client profile ──────────────────────
            KbArticleIndex clientEntry = null;

            if (!string.IsNullOrEmpty(manualClientProfile))
            {
                // Manual override — exact filename match
                clientEntry = _index.FirstOrDefault(e =>
                    e.Folder == "01_CLIENTS" &&
                    e.FileName.Equals(manualClientProfile, StringComparison.OrdinalIgnoreCase));
                ctx.DetectionMethod = "manual";
            }

            if (clientEntry == null && !string.IsNullOrEmpty(projectName))
            {
                // Auto-detect: match project name against client names
                clientEntry = DetectClient(projectName);
                ctx.DetectionMethod = clientEntry != null ? "project-name" : "none";
            }

            if (clientEntry != null)
            {
                ctx.ClientName = clientEntry.GetFrontmatter("client")
                    ?? Path.GetFileNameWithoutExtension(clientEntry.FileName);
                ctx.ClientProfileText = ReadFullArticle(clientEntry.FilePath);
                ctx.ClientProfilePath = clientEntry.RelativePath;
            }

            // ── Step 2: Resolve domain article ──────────────────────
            if (!string.IsNullOrEmpty(domain))
            {
                var domainEntry = _index.FirstOrDefault(e =>
                    e.Folder == "03_DOMAINS" &&
                    MatchesDomain(e, domain));

                if (domainEntry != null)
                {
                    ctx.DomainArticleText = ReadFullArticle(domainEntry.FilePath);
                    ctx.DomainArticlePath = domainEntry.RelativePath;
                    ctx.DomainName = domainEntry.GetFrontmatter("domain")
                        ?? Path.GetFileNameWithoutExtension(domainEntry.FileName);
                }
            }

            // ── Step 3: Resolve style guide ─────────────────────────
            var styleEntry = FindStyleGuide(sourceLang, targetLang, ctx.ClientName);
            if (styleEntry != null)
            {
                ctx.StyleGuideText = ReadFullArticle(styleEntry.FilePath);
                ctx.StyleGuidePath = styleEntry.RelativePath;
            }

            // ── Step 4: Resolve terminology articles ────────────────
            var termEntries = FindTerminologyArticles(ctx.ClientName, domain, sourceLang, targetLang);
            foreach (var te in termEntries)
            {
                var text = ReadFullArticle(te.FilePath);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    ctx.TerminologyArticles.Add(text);
                    ctx.TerminologyPaths.Add(te.RelativePath);
                }
            }

            // ── Step 5: Apply token budget ──────────────────────────
            ctx.TrimToTokenBudget(tokenBudget);

            return ctx.HasContent ? ctx : null;
        }

        /// <summary>
        /// Formats the KB context as a prompt-ready Markdown section.
        /// </summary>
        public static string FormatForPrompt(KbContext ctx)
        {
            if (ctx == null || !ctx.HasContent) return null;

            var sb = new StringBuilder(4096);
            sb.AppendLine("# KNOWLEDGE BASE");
            sb.AppendLine();
            sb.AppendLine("The following context comes from your SuperMemory knowledge base.");
            sb.AppendLine("Use this information to inform your translations and terminology choices.");
            sb.AppendLine("Knowledge base decisions take priority over general assumptions.");

            if (!string.IsNullOrWhiteSpace(ctx.ClientProfileText))
            {
                sb.AppendLine();
                sb.AppendLine("## Client Profile" +
                    (string.IsNullOrEmpty(ctx.ClientName) ? "" : ": " + ctx.ClientName));
                sb.AppendLine();
                sb.AppendLine(ctx.ClientProfileText.Trim());
            }

            if (!string.IsNullOrWhiteSpace(ctx.DomainArticleText))
            {
                sb.AppendLine();
                sb.AppendLine("## Domain Knowledge" +
                    (string.IsNullOrEmpty(ctx.DomainName) ? "" : ": " + ctx.DomainName));
                sb.AppendLine();
                sb.AppendLine(ctx.DomainArticleText.Trim());
            }

            if (!string.IsNullOrWhiteSpace(ctx.StyleGuideText))
            {
                sb.AppendLine();
                sb.AppendLine("## Style Guide");
                sb.AppendLine();
                sb.AppendLine(ctx.StyleGuideText.Trim());
            }

            if (ctx.TerminologyArticles.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Terminology Decisions");
                sb.AppendLine();
                sb.AppendLine("These terms have been specifically chosen with reasoning. " +
                    "Follow them exactly — rejected alternatives are listed so you know what to avoid.");
                sb.AppendLine();

                foreach (var article in ctx.TerminologyArticles)
                {
                    sb.AppendLine(article.Trim());
                    sb.AppendLine();
                }
            }

            return sb.ToString().TrimEnd();
        }

        // ─── Private helpers ─────────────────────────────────────────

        private KbArticleIndex DetectClient(string projectName)
        {
            if (string.IsNullOrEmpty(projectName)) return null;

            var clients = _index.Where(e => e.Folder == "01_CLIENTS").ToList();
            if (clients.Count == 0) return null;

            // Try exact match on client name from frontmatter
            foreach (var c in clients)
            {
                var clientName = c.GetFrontmatter("client");
                if (!string.IsNullOrEmpty(clientName) &&
                    projectName.IndexOf(clientName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return c;
            }

            // Try match on filename (without extension)
            foreach (var c in clients)
            {
                var name = Path.GetFileNameWithoutExtension(c.FileName);
                if (name.Length >= 3 && // avoid short false positives
                    projectName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return c;
            }

            return null;
        }

        private bool MatchesDomain(KbArticleIndex entry, string domain)
        {
            var entryDomain = entry.GetFrontmatter("domain");
            if (!string.IsNullOrEmpty(entryDomain))
                return entryDomain.IndexOf(domain, StringComparison.OrdinalIgnoreCase) >= 0
                    || domain.IndexOf(entryDomain, StringComparison.OrdinalIgnoreCase) >= 0;

            // Fallback: match filename
            var name = Path.GetFileNameWithoutExtension(entry.FileName);
            return name.IndexOf(domain, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private KbArticleIndex FindStyleGuide(string sourceLang, string targetLang, string clientName)
        {
            var styles = _index.Where(e => e.Folder == "04_STYLE").ToList();
            if (styles.Count == 0) return null;

            // Prefer client-specific style guide
            if (!string.IsNullOrEmpty(clientName))
            {
                var clientStyle = styles.FirstOrDefault(s =>
                    s.FileName.IndexOf(clientName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (clientStyle != null) return clientStyle;
            }

            // Match by language pair in filename or frontmatter
            if (!string.IsNullOrEmpty(sourceLang) && !string.IsNullOrEmpty(targetLang))
            {
                // Try matching common language code patterns
                var srcShort = ExtractLangCode(sourceLang);
                var tgtShort = ExtractLangCode(targetLang);

                foreach (var s in styles)
                {
                    var name = s.FileName.ToUpperInvariant();
                    var fm = s.GetFrontmatter("languages") ?? "";

                    if ((name.Contains(srcShort) && name.Contains(tgtShort)) ||
                        (fm.Contains(srcShort) && fm.Contains(tgtShort)))
                        return s;
                }
            }

            // Fallback: return first "General" style guide
            return styles.FirstOrDefault(s =>
                s.FileName.IndexOf("General", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private List<KbArticleIndex> FindTerminologyArticles(
            string clientName, string domain, string sourceLang, string targetLang)
        {
            var terms = _index.Where(e => e.Folder == "02_TERMINOLOGY").ToList();
            if (terms.Count == 0) return terms;

            // Score each term article by relevance
            var scored = new List<(KbArticleIndex entry, int score)>();

            foreach (var t in terms)
            {
                int score = 0;

                // Client match: +3 points
                if (!string.IsNullOrEmpty(clientName))
                {
                    var clients = t.GetFrontmatter("clients") ?? t.GetFrontmatter("client") ?? "";
                    if (clients.IndexOf(clientName, StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 3;
                }

                // Domain match: +2 points
                if (!string.IsNullOrEmpty(domain))
                {
                    var entryDomain = t.GetFrontmatter("domain") ?? "";
                    if (entryDomain.IndexOf(domain, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        domain.IndexOf(entryDomain, StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 2;
                }

                // Language match: +1 point
                var langs = t.GetFrontmatter("languages") ??
                    t.GetFrontmatter("source_language") ?? "";
                if (!string.IsNullOrEmpty(sourceLang))
                {
                    var srcShort = ExtractLangCode(sourceLang);
                    if (langs.ToUpperInvariant().Contains(srcShort))
                        score += 1;
                }

                // Only include articles with at least some relevance
                if (score > 0)
                    scored.Add((t, score));
            }

            // If nothing matched by client/domain/language, include all term articles
            // (they're still useful general terminology)
            if (scored.Count == 0 && terms.Count <= 20)
            {
                return terms;
            }

            // Return sorted by relevance (highest first)
            return scored
                .OrderByDescending(x => x.score)
                .Select(x => x.entry)
                .ToList();
        }

        private static string ExtractLangCode(string langDisplayName)
        {
            if (string.IsNullOrEmpty(langDisplayName)) return "";

            // Common patterns: "English (United States)", "Dutch", "en-US", etc.
            // Extract a short code like "EN", "NL", "FR"
            var upper = langDisplayName.ToUpperInvariant();

            // Try to match known language names
            if (upper.Contains("DUTCH") || upper.Contains("NEDERLAND") || upper.Contains("NL"))
                return "NL";
            if (upper.Contains("FRENCH") || upper.Contains("FRAN") || upper.Contains("FR"))
                return "FR";
            if (upper.Contains("GERMAN") || upper.Contains("DEUTSCH") || upper.Contains("DE"))
                return "DE";
            if (upper.Contains("ENGLISH") || upper.Contains("EN"))
                return "EN";
            if (upper.Contains("SPANISH") || upper.Contains("ESPA") || upper.Contains("ES"))
                return "ES";
            if (upper.Contains("ITALIAN") || upper.Contains("IT"))
                return "IT";
            if (upper.Contains("PORTUGUESE") || upper.Contains("PT"))
                return "PT";

            // Fallback: take first two characters
            return upper.Length >= 2 ? upper.Substring(0, 2) : upper;
        }

        private static string ReadHead(string path, int maxChars)
        {
            using (var sr = new StreamReader(path, Encoding.UTF8))
            {
                var buf = new char[maxChars];
                int read = sr.Read(buf, 0, maxChars);
                return new string(buf, 0, read);
            }
        }

        private static string ReadFullArticle(string path)
        {
            try
            {
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, string> ParseFrontmatter(string text)
        {
            var fm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return fm;

            var trimmed = text.TrimStart();
            if (!trimmed.StartsWith("---")) return fm;

            var idx1 = trimmed.IndexOf("---", StringComparison.Ordinal);
            var idx2 = trimmed.IndexOf("---", idx1 + 3, StringComparison.Ordinal);
            if (idx2 <= idx1) return fm;

            var yaml = trimmed.Substring(idx1 + 3, idx2 - idx1 - 3);
            var lines = yaml.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var colonIdx = line.IndexOf(':');
                if (colonIdx <= 0) continue;

                var key = line.Substring(0, colonIdx).Trim();
                var value = line.Substring(colonIdx + 1).Trim()
                    .Trim('"', '\'');

                // Handle YAML arrays: clients: ["[[Acme]]", "[[Beta]]"]
                if (value.StartsWith("[") && value.EndsWith("]"))
                    value = value.Trim('[', ']').Replace("\"", "").Replace("'", "");

                // Handle backlink syntax: [[Name]] -> Name
                value = value.Replace("[[", "").Replace("]]", "");

                fm[key] = value;
            }

            return fm;
        }
    }

    /// <summary>
    /// Lightweight index entry for a KB article (frontmatter only, no content).
    /// </summary>
    public class KbArticleIndex
    {
        public string FilePath { get; set; }
        public string RelativePath { get; set; }
        public string Folder { get; set; }
        public string FileName { get; set; }
        public Dictionary<string, string> Frontmatter { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public long FileSizeBytes { get; set; }

        public string GetFrontmatter(string key)
        {
            return Frontmatter != null && Frontmatter.TryGetValue(key, out var val) ? val : null;
        }
    }

    /// <summary>
    /// Resolved KB context for a translation — the relevant articles loaded and ready.
    /// </summary>
    public class KbContext
    {
        // Client
        public string ClientName { get; set; }
        public string ClientProfileText { get; set; }
        public string ClientProfilePath { get; set; }

        // Domain
        public string DomainName { get; set; }
        public string DomainArticleText { get; set; }
        public string DomainArticlePath { get; set; }

        // Style guide
        public string StyleGuideText { get; set; }
        public string StyleGuidePath { get; set; }

        // Terminology
        public List<string> TerminologyArticles { get; set; } = new List<string>();
        public List<string> TerminologyPaths { get; set; } = new List<string>();

        // Detection info
        public string DetectionMethod { get; set; } = "none";

        /// <summary>True if any KB content was loaded.</summary>
        public bool HasContent =>
            !string.IsNullOrWhiteSpace(ClientProfileText) ||
            !string.IsNullOrWhiteSpace(DomainArticleText) ||
            !string.IsNullOrWhiteSpace(StyleGuideText) ||
            TerminologyArticles.Count > 0;

        /// <summary>
        /// Estimated token count (chars / 4 heuristic).
        /// </summary>
        public int EstimatedTokens
        {
            get
            {
                int chars = 0;
                if (ClientProfileText != null) chars += ClientProfileText.Length;
                if (DomainArticleText != null) chars += DomainArticleText.Length;
                if (StyleGuideText != null) chars += StyleGuideText.Length;
                foreach (var t in TerminologyArticles) chars += t.Length;
                return chars / 4;
            }
        }

        /// <summary>
        /// Trims content to fit within a token budget, removing lowest-priority content first.
        /// Priority: Client > Domain > Style > Terminology (from end).
        /// </summary>
        public void TrimToTokenBudget(int maxTokens)
        {
            if (maxTokens <= 0 || EstimatedTokens <= maxTokens) return;

            // Remove terminology articles from the end until within budget
            while (TerminologyArticles.Count > 0 && EstimatedTokens > maxTokens)
            {
                TerminologyArticles.RemoveAt(TerminologyArticles.Count - 1);
                TerminologyPaths.RemoveAt(TerminologyPaths.Count - 1);
            }

            // If still over budget, remove style guide
            if (EstimatedTokens > maxTokens)
            {
                StyleGuideText = null;
                StyleGuidePath = null;
            }

            // If still over budget, remove domain article
            if (EstimatedTokens > maxTokens)
            {
                DomainArticleText = null;
                DomainArticlePath = null;
            }

            // Last resort: truncate client profile
            if (EstimatedTokens > maxTokens && ClientProfileText != null)
            {
                var maxChars = maxTokens * 4;
                if (ClientProfileText.Length > maxChars)
                    ClientProfileText = ClientProfileText.Substring(0, maxChars) + "\n[... truncated ...]";
            }
        }

        /// <summary>
        /// Returns a short summary of what was loaded (for UI display).
        /// </summary>
        public string GetSummary()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ClientProfileText))
                parts.Add("client: " + (ClientName ?? "detected"));
            if (!string.IsNullOrWhiteSpace(DomainArticleText))
                parts.Add("domain: " + (DomainName ?? "detected"));
            if (!string.IsNullOrWhiteSpace(StyleGuideText))
                parts.Add("style guide");
            if (TerminologyArticles.Count > 0)
                parts.Add(TerminologyArticles.Count + " term article" +
                    (TerminologyArticles.Count != 1 ? "s" : ""));

            if (parts.Count == 0) return null;
            return "SuperMemory: " + string.Join(", ", parts) +
                " (~" + EstimatedTokens + " tokens)";
        }
    }
}

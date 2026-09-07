// Shared with Supervertaler for Trados, which is where this was written.
//
// Moved into core rather than copied because the on-disk format is a
// cross-product contract: Workbench, the Python assistant and both CAT plugins
// all read the same memory-banks/ root, and a second implementation of a
// contract is how the two halves drift.
//
// Nothing here touches a CAT tool API - it reads a folder of Markdown.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Supervertaler.Core
{
    /// <summary>
    /// Reads and queries a Supervertaler memory bank (Obsidian-compatible Markdown folder).
    /// Scans frontmatter for lightweight indexing, then loads full articles on demand.
    /// Wire format: see SPEC.md in the supervertaler-assistant repo.
    /// </summary>
    public class MemoryBankReader
    {
        // NOTE: internal field and property names (_vaultDir, VaultExists) are kept as-is
        // in this commit to minimise churn. A follow-up commit will rename them to
        // _memoryBankDir / MemoryBankExists alongside a fuller sweep of the SuperMemory
        // vocabulary in the Trados plugin (docs, help pages, action IDs, etc.).
        private readonly string _vaultDir;
        private List<KbArticleIndex> _index;
        private DateTime _indexBuiltAt;

        // Cache of lowercased note bodies for content matching, keyed by file
        // path and invalidated per-file by last-write time (so external Obsidian
        // edits are picked up). Only populated when a query is supplied.
        private readonly Dictionary<string, KeyValuePair<long, string>> _bodyCache
            = new Dictionary<string, KeyValuePair<long, string>>(StringComparer.OrdinalIgnoreCase);

        // ── Bank layout (from 2026-08-08) ────────────────────────────────────
        //
        // A bank is THREE markdown files plus a reference/ folder, all at the
        // bank root. That replaced a seven-folder wiki with one file per fact,
        // which produced 136 files for what is a 136-row table, put 15% of the
        // corpus behind malformed frontmatter nobody noticed, and became
        // impossible for a human to audit - the whole point of keeping notes.
        //
        // reference/ holds the raw source material the three files were derived
        // from. It is deliberately NOT read into prompts: it is the audit trail,
        // so a derived claim can be checked against what it came from.
        internal const string BriefFile = "brief.md";
        internal const string TerminologyFile = "terminology.md";
        internal const string StyleFile = "style.md";
        internal const string ReferenceFolder = "reference";

        internal static readonly string[] BankFiles =
            { BriefFile, TerminologyFile, StyleFile };

        /// <summary>
        /// The bank loaded ALONGSIDE the active one, always. Holds defaults that
        /// are true of the translator's work rather than of any one client; the
        /// active bank overrides it wherever they disagree. The leading
        /// underscore is a reserved namespace - <see cref="Settings.UserDataPath.SanitizeBankName"/>
        /// trims leading separators, so a user cannot create a colliding bank.
        /// </summary>
        public const string SharedBankName = "_shared";

        /// <summary>
        /// File extensions that appear inside memory banks but are NOT knowledge
        /// content – Obsidian plugin sidecars, editor metadata, etc. Callers that
        /// enumerate inbox files for Process Inbox or Distill must filter these
        /// out, otherwise Distill tries to hand them to DocumentTextExtractor and
        /// fails with "Unsupported file format".
        /// </summary>
        public static readonly HashSet<string> IgnoredSidecarExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".edtz"  // Obsidian plugin edit-metadata sidecar (one per .md)
            };

        /// <summary>
        /// True if the file is a bank-internal sidecar that should be ignored by
        /// any feature that enumerates bank content (Process Inbox, Distill,
        /// article counts, merge planners, …).
        /// </summary>
        public static bool IsIgnoredSidecar(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return !string.IsNullOrEmpty(ext) && IgnoredSidecarExtensions.Contains(ext);
        }

        public MemoryBankReader(string memoryBankDir)
        {
            _vaultDir = memoryBankDir;
        }

        /// <summary>Folder name of the bank this reader is pointed at.</summary>
        public string BankName => SafeBankName(_vaultDir);

        /// <summary>True when this reader is pointed at the shared overlay bank.</summary>
        public bool IsSharedBank =>
            string.Equals(BankName, SharedBankName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns true if the memory bank exists on disk and has content folders.
        /// </summary>
        public bool VaultExists =>
            Directory.Exists(_vaultDir) &&
            BankFiles.Any(f => File.Exists(Path.Combine(_vaultDir, f)));

        /// <summary>
        /// Builds or refreshes the lightweight frontmatter index.
        /// Only re-scans if the index is older than 30 seconds.
        /// </summary>
        public void RefreshIndex(bool force = false)
        {
            if (!force && _index != null && (DateTime.UtcNow - _indexBuiltAt).TotalSeconds < 30)
                return;

            var entries = new List<KbArticleIndex>();

            // Index the bank's own markdown files (brief/terminology/style, plus
            // anything else the user has dropped at the root). reference/ is
            // excluded: it is source material, not knowledge, and indexing it
            // would let a superseded draft answer a search as if it were current.
            if (Directory.Exists(_vaultDir))
            {
                foreach (var file in Directory.GetFiles(_vaultDir, "*.md", SearchOption.TopDirectoryOnly))
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName.StartsWith("_EXAMPLE_", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (IsIgnoredSidecar(file)) continue;

                    try
                    {
                        entries.Add(new KbArticleIndex
                        {
                            FilePath = file,
                            RelativePath = fileName,
                            Folder = "",
                            FileName = fileName,
                            Frontmatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                            FileSizeBytes = new FileInfo(file).Length
                        });
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
        /// Returns a snapshot of the current frontmatter index (refreshing if
        /// stale). Used by the overview/reporting features that need lightweight
        /// metadata for every note without reading full article bodies.
        /// </summary>
        public IReadOnlyList<KbArticleIndex> GetIndexSnapshot()
        {
            if (!VaultExists) return new List<KbArticleIndex>();
            RefreshIndex();
            return _index ?? new List<KbArticleIndex>();
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
            int tokenBudget = 24000,
            string manualClientProfile = null,
            string queryText = null)
        {
            var ctx = new KbContext();

            // The bank IS the selection. There is no client detection any more:
            // the user picked a bank from the toolbar, so filtering its contents
            // by a frontmatter "client" field would only be a chance to get it
            // wrong. projectName/domain/langs are kept in the signature because
            // callers pass them, and they still label the result.
            ctx.ClientName = string.IsNullOrWhiteSpace(manualClientProfile)
                ? SafeBankName(_vaultDir)
                : manualClientProfile;
            ctx.DomainName = domain;
            ctx.DetectionMethod = "bank";

            if (VaultExists)
            {
                ctx.ClientProfileText = ReadBankFile(_vaultDir, BriefFile, out var briefPath);
                ctx.ClientProfilePath = briefPath;
                ctx.StyleGuideText = ReadBankFile(_vaultDir, StyleFile, out var stylePath);
                ctx.StyleGuidePath = stylePath;

                var terms = ReadBankFile(_vaultDir, TerminologyFile, out var termPath);
                if (!string.IsNullOrWhiteSpace(terms))
                {
                    ctx.TerminologyArticles.Add(terms);
                    ctx.TerminologyPaths.Add(termPath);
                }

                // Anything else the translator put at the bank root. Previously
                // these were simply inert: the loader read three fixed names
                // while the search index enumerated *.md, so a file like
                // figures.md was COUNTED as an article by list_supermemory_banks
                // and then contributed nothing to the prompt — no error, no
                // warning, no content. Someone who adds a file to a bank means
                // it to be used.
                //
                // Top directory only: reference/ holds bulk material that is
                // deliberately not in every prompt, and this must not sweep it in.
                foreach (var extra in ReadOtherBankFiles(_vaultDir))
                {
                    ctx.ExtraArticles.Add(extra.Key);
                    ctx.ExtraPaths.Add(extra.Value);
                }
            }

            // ── The shared bank, always loaded alongside ─────────────
            // Skipped when the active bank IS _shared, so editing your own
            // defaults doesn't show them to you twice.
            var sharedDir = ResolveSharedBankDir(_vaultDir);
            if (sharedDir != null)
            {
                ctx.SharedBriefText = ReadBankFile(sharedDir, BriefFile, out _);
                ctx.SharedTerminologyText = ReadBankFile(sharedDir, TerminologyFile, out _);
                ctx.SharedStyleText = ReadBankFile(sharedDir, StyleFile, out _);

                // Anything else at the shared root, on the same footing as the
                // extras of a selected bank. Only the three named files were read
                // here, so a translator who added _shared/method.md - working
                // rules, what to verify, what has bitten before - had written a
                // file that reached no prompt in either product and gave no sign
                // of it. The selected bank has loaded its extras since the same
                // reasoning was applied there: someone who adds a file to a bank
                // means it to be used.
                foreach (var extra in ReadOtherBankFiles(sharedDir))
                {
                    ctx.SharedExtraArticles.Add(extra.Key);
                    ctx.SharedExtraPaths.Add(SharedBankName + "/" + extra.Value);
                }
            }

            ctx.TrimToTokenBudget(tokenBudget);

            return ctx.HasContent ? ctx : null;
        }

        /// <summary>
        /// Every other <c>*.md</c> at the bank root, as (text, relative path).
        /// The three named files are excluded — they have their own slots — as
        /// are dotfiles and the Obsidian sidecars the index already skips.
        /// Ordered by name so the prompt is stable between runs.
        /// </summary>
        private static List<KeyValuePair<string, string>> ReadOtherBankFiles(string bankDir)
        {
            var result = new List<KeyValuePair<string, string>>();
            try
            {
                var known = new HashSet<string>(
                    new[] { BriefFile, TerminologyFile, StyleFile },
                    StringComparer.OrdinalIgnoreCase);

                var files = Directory.GetFiles(bankDir, "*.md", SearchOption.TopDirectoryOnly);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                foreach (var path in files)
                {
                    var name = Path.GetFileName(path);
                    if (known.Contains(name)) continue;
                    if (name.StartsWith(".", StringComparison.Ordinal)) continue;

                    var text = ReadBankFile(bankDir, name, out var rel);
                    if (!string.IsNullOrWhiteSpace(text))
                        result.Add(new KeyValuePair<string, string>(text, rel));
                }
            }
            catch { /* a bank we cannot enumerate is not an error, just empty */ }
            return result;
        }

        /// <summary>Bank folder name, used as the context's display label.</summary>
        private static string SafeBankName(string dir)
        {
            try { return new DirectoryInfo(dir).Name; } catch { return null; }
        }

        /// <summary>
        /// Reads one of the bank's three files. Returns null when absent - a bank
        /// with no style.md is normal, not an error.
        /// </summary>
        private static string ReadBankFile(string bankDir, string fileName, out string relativePath)
        {
            relativePath = null;
            try
            {
                var path = Path.Combine(bankDir, fileName);
                if (!File.Exists(path)) return null;
                var text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text)) return null;

                // A bank the user created but never filled in is not content. Its files are
                // ~1.8 KB of headings and instructions, so the old IsNullOrWhiteSpace check
                // passed and the prompt builder announced "hard-won translation decisions"
                // over a set of blank slots – an invitation to invent house conventions and
                // attribute them to the translator. Treat an untouched file as absent.
                if (IsSkeletonOnly(text, fileName, BankNameOf(bankDir))) return null;

                relativePath = fileName;
                return text.Trim();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The bank name is the folder name – the skeleton embeds it in each file.</summary>
        private static string BankNameOf(string bankDir)
        {
            try { return Path.GetFileName(bankDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
            catch { return null; }
        }

        /// <summary>
        /// True when every substantive line of <paramref name="text"/> also appears in the
        /// skeleton this file was created from – i.e. the user has added nothing.
        /// </summary>
        /// <remarks>
        /// Compares against <see cref="Settings.UserDataPath.SkeletonBody"/> rather than
        /// guessing structurally, because a structural rule ("is there a filled table row?")
        /// has to tell instructional prose from real prose, and its failure mode is dropping
        /// content the user did write. This comparison fails the other way: anything it does
        /// not recognise is treated as content and included. That also covers skeleton text
        /// changing in a later release – banks seeded by an older skeleton stop matching and
        /// are simply included again, which is the pre-guard behaviour, not a new break.
        ///
        /// Deleting the instructions without adding anything still counts as untouched: the
        /// remaining lines are a subset of the skeleton's.
        /// </remarks>
        private static bool IsSkeletonOnly(string text, string fileName, string bankName)
        {
            if (string.IsNullOrEmpty(bankName)) return false;

            var skeleton = MemoryBanks.SkeletonBody(fileName, bankName);
            if (string.IsNullOrEmpty(skeleton)) return false;   // a file we never seeded

            var skeletonLines = new HashSet<string>(SubstantiveLines(skeleton), StringComparer.Ordinal);
            foreach (var line in SubstantiveLines(text))
            {
                if (!skeletonLines.Contains(line))
                    return false;                                // something the skeleton never had
            }
            return true;
        }

        /// <summary>
        /// Lines that could carry meaning: drops blanks, bare bullets, table separators and
        /// all-blank table rows. Headings are deliberately KEPT – the skeleton's "## 1." slot
        /// is filled by writing into the heading itself ("## 1. Never abbreviate"), so
        /// discarding headings would hide exactly the edit this check exists to notice.
        /// </summary>
        private static IEnumerable<string> SubstantiveLines(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;

            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line == "-" || line == "*" || line == "+") continue;

                if (line.StartsWith("|", StringComparison.Ordinal))
                {
                    // Separator ("|---|---|") or an empty row ("|  |  |  |") carries nothing.
                    bool empty = true;
                    foreach (var ch in line)
                    {
                        if (ch != '|' && ch != '-' && ch != ':' && !char.IsWhiteSpace(ch)) { empty = false; break; }
                    }
                    if (empty) continue;
                }

                yield return line;
            }
        }

        /// <summary>
        /// Locates the <c>_shared</c> bank next to the active one, or null when it
        /// does not exist or the active bank already is it.
        /// </summary>
        private static string ResolveSharedBankDir(string activeBankDir)
        {
            try
            {
                var name = SafeBankName(activeBankDir);
                if (string.Equals(name, SharedBankName, StringComparison.OrdinalIgnoreCase))
                    return null;

                var root = Path.GetDirectoryName(activeBankDir);
                if (string.IsNullOrEmpty(root)) return null;

                var shared = Path.Combine(root, SharedBankName);
                return Directory.Exists(shared) ? shared : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Formats the KB context as a prompt-ready Markdown section.
        /// </summary>
        public static string FormatForPrompt(KbContext ctx)
        {
            if (ctx == null || !ctx.HasContent) return null;

            var sb = new StringBuilder(4096);
            sb.AppendLine("# SUPERMEMORY");
            sb.AppendLine();
            sb.AppendLine("The translator's own decisions and the reasoning behind them. These are");
            sb.AppendLine("choices you cannot derive from the source text, so getting one wrong is a");
            sb.AppendLine("real error, not a stylistic difference.");
            sb.AppendLine();
            sb.AppendLine("Two layers, and the order matters: house defaults first, then the client.");
            sb.AppendLine("**Where they disagree, the client section wins** - that is what it is for.");

            bool hasShared =
                !string.IsNullOrWhiteSpace(ctx.SharedBriefText) ||
                !string.IsNullOrWhiteSpace(ctx.SharedTerminologyText) ||
                !string.IsNullOrWhiteSpace(ctx.SharedStyleText) ||
                ctx.SharedExtraArticles.Count > 0;

            if (hasShared)
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine("# House defaults");
                sb.AppendLine();
                sb.AppendLine("How this translator works generally. Apply unless the client section below");
                sb.AppendLine("says otherwise.");
                AppendSection(sb, "General notes", null, ctx.SharedBriefText);
                AppendSection(sb, "Terminology", null, ctx.SharedTerminologyText);
                AppendSection(sb, "Style", null, ctx.SharedStyleText);

                // Named after the file, since that is the only name they have -
                // and a heading of "method.md" is what the translator will
                // recognise when they go looking for what the model was told.
                for (var i = 0; i < ctx.SharedExtraArticles.Count; i++)
                {
                    var path = i < ctx.SharedExtraPaths.Count ? ctx.SharedExtraPaths[i] : null;
                    var name = string.IsNullOrWhiteSpace(path)
                        ? "Notes"
                        : path.Substring(path.LastIndexOf('/') + 1);

                    AppendSection(sb, name, null, ctx.SharedExtraArticles[i]);
                }
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("# Client: " + (ctx.ClientName ?? "current"));
            if (hasShared)
            {
                sb.AppendLine();
                sb.AppendLine("**Overrides the house defaults above.**");
            }

            AppendSection(sb, "Brief", null, ctx.ClientProfileText);

            foreach (var article in ctx.TerminologyArticles)
            {
                if (string.IsNullOrWhiteSpace(article)) continue;
                AppendSection(sb, "Terminology", null, article);
            }

            AppendSection(sb, "Style", null, ctx.StyleGuideText);

            for (int i = 0; i < ctx.ExtraArticles.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(ctx.ExtraArticles[i])) continue;
                // Titled by filename: these are whatever the translator chose to
                // put in the bank, so the name is the only thing that says what
                // it is. "figures.md" tells the model more than "Reference".
                var title = i < ctx.ExtraPaths.Count && !string.IsNullOrEmpty(ctx.ExtraPaths[i])
                    ? System.IO.Path.GetFileName(ctx.ExtraPaths[i])
                    : "Reference";
                AppendSection(sb, title, null, ctx.ExtraArticles[i]);
            }

            // Name what the budget cut, inside the block itself. This reaches
            // the in-Trados chat as well as the MCP `context` field, and both
            // need it for the same reason: a model handed a bank's material has
            // no way to tell a complete answer from a trimmed one, so an absent
            // rule reads as a rule that was never written. Saying so lets it
            // flag the gap instead of translating confidently past it.
            if (ctx.TrimmedPaths != null && ctx.TrimmedPaths.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Not included");
                sb.AppendLine();
                sb.AppendLine("This bank holds more than fitted the token budget. **Left out:** "
                    + string.Join(", ", ctx.TrimmedPaths) + ".");
                sb.AppendLine();
                sb.AppendLine("If a question turns on something those files would cover, say so");
                sb.AppendLine("rather than guessing - the answer may be written down but absent here.");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Confidence values that mean "this has not been checked". An article
        /// that says <c>confidence: low</c>, or flags itself as a stub, must not
        /// outrank the model's own judgement – it is usually a Quick Add note
        /// whose body was never completed. A MISSING confidence field is left
        /// authoritative: most older articles predate the field, and silently
        /// demoting all of them would gut the bank.
        /// </summary>
        private static bool IsUnverified(string articleText)
        {
            if (string.IsNullOrWhiteSpace(articleText)) return false;

            var fm = ParseFrontmatter(articleText);
            string stub;
            if (fm.TryGetValue("stub", out stub) &&
                stub.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                return true;

            string confidence;
            if (!fm.TryGetValue("confidence", out confidence)) return false;

            confidence = (confidence ?? "").Trim();
            return confidence.Equals("low", StringComparison.OrdinalIgnoreCase)
                || confidence.Equals("draft", StringComparison.OrdinalIgnoreCase)
                || confidence.Equals("unverified", StringComparison.OrdinalIgnoreCase);
        }

        private static void AppendUnverifiedMarker(StringBuilder sb, string articleText)
        {
            if (IsUnverified(articleText))
                sb.AppendLine("> UNVERIFIED – recorded but never checked. Weigh it; do not obey it.");
        }

        private static void AppendSection(StringBuilder sb, string heading, string name, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            sb.AppendLine();
            sb.AppendLine("## " + heading + (string.IsNullOrEmpty(name) ? "" : ": " + name));
            sb.AppendLine();
            AppendUnverifiedMarker(sb, text);
            sb.AppendLine(text.Trim());
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

        // Boost applied when the user's query (chat message / segment) explicitly
        // mentions a term note's source/target term or filename. Large enough to
        // dominate the client/domain/language signals so a directly-asked-about
        // note ranks first and survives token-budget trimming.
        private const int QueryMatchBoost = 100;

        private List<KbArticleIndex> FindTerminologyArticles(
            string clientName, string domain, string sourceLang, string targetLang,
            string queryText = null)
        {
            var terms = _index.Where(e => e.Folder == "02_TERMINOLOGY").ToList();
            if (terms.Count == 0) return terms;

            var queryLower = string.IsNullOrWhiteSpace(queryText) ? null : queryText.ToLowerInvariant();
            var queryTokens = QueryTokens(queryLower);

            // Score each term article by relevance
            var scored = new List<(KbArticleIndex entry, int score)>();

            foreach (var t in terms)
            {
                int score = 0;

                // Query match: the user explicitly mentioned this term. Dominant signal.
                if (queryLower != null && QueryMentionsTerm(t, queryLower))
                    score += QueryMatchBoost;

                // Content match: the user's query words appear in the note BODY
                // (not just its frontmatter term). Additive – surfaces a note that
                // discusses the topic even when its title/term doesn't match. Read
                // bodies only when there is a query (chat), and cache by mtime.
                if (queryTokens.Count > 0)
                {
                    var body = GetBodyLower(t);
                    if (body.Length > 0)
                    {
                        int hits = 0;
                        foreach (var tok in queryTokens)
                            if (body.IndexOf(tok, StringComparison.Ordinal) >= 0) hits++;
                        if (hits > 0) score += Math.Min(hits * 5, 40);
                    }
                }

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

                // Language match: +1 point. Accept the keys real notes actually use
                // (language_pair) alongside the older languages / source_language keys.
                var langs = t.GetFrontmatter("languages")
                    ?? t.GetFrontmatter("language_pair")
                    ?? t.GetFrontmatter("source_language") ?? "";
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

            // If nothing matched by query/client/domain/language, fall back to all
            // term articles (still useful general terminology). The token budget –
            // not an arbitrary count cap – limits how many actually reach the prompt.
            if (scored.Count == 0)
                return terms;

            // Return sorted by relevance (highest first)
            return scored
                .OrderByDescending(x => x.score)
                .Select(x => x.entry)
                .ToList();
        }

        /// <summary>Lower-cased note body, cached and invalidated by file mtime.</summary>
        private string GetBodyLower(KbArticleIndex entry)
        {
            try
            {
                var mtime = File.GetLastWriteTimeUtc(entry.FilePath).Ticks;
                if (_bodyCache.TryGetValue(entry.FilePath, out var cached) && cached.Key == mtime)
                    return cached.Value;
                var text = (ReadFullArticle(entry.FilePath) ?? "").ToLowerInvariant();
                _bodyCache[entry.FilePath] = new KeyValuePair<long, string>(mtime, text);
                return text;
            }
            catch { return ""; }
        }

        /// <summary>
        /// Splits a lower-cased query into distinct word tokens of 4+ characters
        /// (short words are too noisy for body matching).
        /// </summary>
        private static List<string> QueryTokens(string queryLower)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(queryLower)) return tokens;
            var sb = new StringBuilder();
            foreach (var ch in queryLower)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                else { if (sb.Length >= 4) tokens.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length >= 4) tokens.Add(sb.ToString());
            return tokens.Distinct().ToList();
        }

        /// <summary>
        /// True if the user's (lower-cased) query text mentions this term note –
        /// by its source term, target term, or filename. Candidates shorter than
        /// 3 characters are ignored to avoid spurious matches on stop-words.
        /// </summary>
        private static bool QueryMentionsTerm(KbArticleIndex entry, string queryLower)
        {
            foreach (var candidate in new[]
            {
                entry.GetFrontmatter("term_source"),
                entry.GetFrontmatter("term_target"),
                Path.GetFileNameWithoutExtension(entry.FileName)
            })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var c = candidate.Trim().ToLowerInvariant();
                if (c.Length >= 3 && queryLower.IndexOf(c, StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
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

        internal static string ReadHead(string path, int maxChars)
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

        internal static Dictionary<string, string> ParseFrontmatter(string text)
        {
            var fm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return fm;

            var trimmed = text.TrimStart();

            // Tolerate files that were pasted from an LLM reply wrapped in a
            // ```markdown code fence. Strip a leading fence line so the real
            // frontmatter below it can still be parsed.
            if (trimmed.StartsWith("```"))
            {
                var nl = trimmed.IndexOf('\n');
                if (nl < 0) return fm;
                trimmed = trimmed.Substring(nl + 1).TrimStart();
            }

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

        /// <summary>
        /// Free-text search across the whole bank, independent of the
        /// translation-prompt path. <see cref="LoadContext"/> answers "what is
        /// relevant to the segment in front of me?"; this answers "where did I
        /// write about X?", which is the question a translator actually asks
        /// out loud. Shared by the MCP memory-bank tools and the in-plugin KB
        /// query mode.
        ///
        /// Only <see cref="ContentFolders"/> are searched, because only those
        /// are indexed – raw 00_INBOX material and 06_TEMPLATES prompt files
        /// are deliberately out of scope.
        /// </summary>
        /// <param name="query">Free text, split into terms on whitespace.</param>
        /// <param name="limit">Maximum hits to return, best first.</param>
        public IReadOnlyList<KbSearchHit> Search(string query, int limit = 10)
        {
            var hits = new List<KbSearchHit>();
            if (!VaultExists || string.IsNullOrWhiteSpace(query)) return hits;

            RefreshIndex();
            if (_index == null || _index.Count == 0) return hits;

            var terms = query.ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\r', '\n', ',', ';', ':', '"', '\'', '(', ')' },
                       StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2)
                .Distinct()
                .ToList();
            if (terms.Count == 0) return hits;

            var bankName = BankName;

            foreach (var entry in _index)
            {
                // Cached and mtime-invalidated, so repeated queries stay cheap
                // and external Obsidian edits are still picked up.
                var haystack = GetBodyLower(entry);
                if (haystack.Length == 0) continue;

                var title = entry.GetFrontmatter("title")
                            ?? Path.GetFileNameWithoutExtension(entry.FileName) ?? "";
                var titleLower = title.ToLowerInvariant();
                var frontmatterLower = entry.Frontmatter == null
                    ? ""
                    : string.Join(" ", entry.Frontmatter.Values).ToLowerInvariant();

                // A term in the title is a far stronger signal than one buried
                // in the body, so weight them rather than counting flat.
                int score = 0;
                string firstMatch = null;
                foreach (var term in terms)
                {
                    if (titleLower.IndexOf(term, StringComparison.Ordinal) >= 0) score += 10;
                    if (frontmatterLower.IndexOf(term, StringComparison.Ordinal) >= 0) score += 5;

                    var occurrences = CountOccurrences(haystack, term);
                    if (occurrences > 0)
                    {
                        score += Math.Min(occurrences, 5);
                        if (firstMatch == null) firstMatch = term;
                    }
                }

                if (score == 0) continue;

                hits.Add(new KbSearchHit
                {
                    Bank = bankName,
                    RelativePath = entry.RelativePath,
                    Folder = entry.Folder,
                    FileName = entry.FileName,
                    Title = title,
                    Score = score,
                    Snippet = BuildSnippet(ReadFullArticle(entry.FilePath), haystack, firstMatch)
                });
            }

            return hits
                .OrderByDescending(h => h.Score)
                .ThenBy(h => h.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(limit > 0 ? limit : 10)
                .ToList();
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, i = 0;
            while (i <= haystack.Length - needle.Length)
            {
                var at = haystack.IndexOf(needle, i, StringComparison.Ordinal);
                if (at < 0) break;
                count++;
                i = at + needle.Length;
            }
            return count;
        }

        /// <summary>
        /// Index of the first character of an article's prose: past a leading
        /// ``` fence and past the YAML frontmatter block. A snippet cut from
        /// the frontmatter shows "title:" and "type:" lines rather than the
        /// reasoning the translator actually wrote, which is the part worth
        /// showing. Returns 0 when there is no frontmatter to skip.
        /// </summary>
        private static int ProseStart(string body)
        {
            if (string.IsNullOrEmpty(body)) return 0;

            var i = 0;
            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;

            // Tolerate an article pasted inside a ```markdown fence, the same
            // way ParseFrontmatter does.
            if (i + 3 <= body.Length && string.CompareOrdinal(body, i, "```", 0, 3) == 0)
            {
                var fenceEnd = body.IndexOf('\n', i);
                if (fenceEnd < 0) return 0;
                i = fenceEnd + 1;
                while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
            }

            if (i + 3 > body.Length || string.CompareOrdinal(body, i, "---", 0, 3) != 0) return 0;

            var pos = body.IndexOf('\n', i);
            if (pos < 0) return 0;
            pos++;

            // Walk to the closing --- on a line of its own.
            while (pos < body.Length)
            {
                var lineEnd = body.IndexOf('\n', pos);
                var line = (lineEnd < 0 ? body.Substring(pos) : body.Substring(pos, lineEnd - pos)).Trim();
                if (line == "---")
                    return lineEnd < 0 ? body.Length : lineEnd + 1;
                if (lineEnd < 0) break;
                pos = lineEnd + 1;
            }

            return 0;
        }

        /// <summary>
        /// Single-line excerpt around the first match, so the caller can show
        /// why an article matched without shipping the whole file. Frontmatter
        /// is skipped: matches there still count towards the score, but the
        /// excerpt comes from the prose.
        /// </summary>
        private static string BuildSnippet(string body, string haystack, string term, int width = 240)
        {
            if (string.IsNullOrEmpty(body)) return null;

            var proseStart = ProseStart(body);
            if (proseStart >= body.Length) proseStart = 0;

            // Prefer a match in the prose; fall back to the opening of the
            // prose when the term appears only in the frontmatter.
            var at = -1;
            if (!string.IsNullOrEmpty(term) && proseStart < haystack.Length)
                at = haystack.IndexOf(term, proseStart, StringComparison.Ordinal);
            if (at < 0 || at >= body.Length) at = proseStart;

            var start = Math.Max(proseStart, at - width / 2);
            var length = Math.Min(width, body.Length - start);
            if (length <= 0) return null;

            var sb = new StringBuilder(length + 4);
            if (start > proseStart) sb.Append("… ");

            // Collapse whitespace so a multi-line Markdown excerpt still reads
            // as one line in a chat reply or an MCP tool result.
            var lastWasSpace = false;
            foreach (var ch in body.Substring(start, length))
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!lastWasSpace) sb.Append(' ');
                    lastWasSpace = true;
                }
                else
                {
                    sb.Append(ch);
                    lastWasSpace = false;
                }
            }

            if (start + length < body.Length) sb.Append(" …");
            return sb.ToString().Trim();
        }
    }

    /// <summary>
    /// One search result from <see cref="MemoryBankReader.Search"/>: enough to
    /// cite the article and show why it matched, without its full text.
    /// </summary>
    public class KbSearchHit
    {
        /// <summary>
        /// Folder name of the bank this hit came from. Set because a search now
        /// spans the active bank AND <see cref="MemoryBankReader.SharedBankName"/>,
        /// and "which of my banks did I write this in?" is the first thing the
        /// reader asks - not least because the two layers can disagree, and the
        /// active one wins.
        /// </summary>
        public string Bank { get; set; }
        public string RelativePath { get; set; }
        public string Folder { get; set; }
        public string FileName { get; set; }
        public string Title { get; set; }
        public int Score { get; set; }
        public string Snippet { get; set; }
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
    /// Resolved KB context for a translation – the relevant articles loaded and ready.
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

        // Any other *.md the user put at the bank root. Kept separate from
        // terminology because they are not term lists — a figure brief filed
        // under a "Terminology" heading tells the model the wrong thing about
        // what it is reading.
        public List<string> ExtraArticles { get; set; } = new List<string>();
        public List<string> ExtraPaths { get; set; } = new List<string>();

        // Detection info
        public string DetectionMethod { get; set; } = "none";

        /// <summary>
        /// What <see cref="TrimToTokenBudget"/> dropped to fit the budget, as
        /// bank-relative paths.
        ///
        /// <para>Trimming is necessary — a bank can be far larger than any
        /// sensible prompt — but it must not be silent. A caller that asks for
        /// a bank's context and receives two of its three articles has an
        /// incomplete answer that looks complete, which is the failure mode
        /// where a rule the translator wrote down is simply absent from the
        /// translation and nothing says so. Reporting the omission lets the
        /// caller re-ask with a bigger budget.</para>
        /// </summary>
        public List<string> TrimmedPaths { get; set; } = new List<string>();

        /// <summary>True if the brief was cut mid-way rather than dropped
        /// whole. Tracked separately because a truncated article is still
        /// present in the output, so it does not belong in
        /// <see cref="TrimmedPaths"/>, but it is still content the caller did
        /// not get.</summary>
        public bool ClientProfileTruncated { get; set; }

        // ── The _shared bank, loaded alongside the active one ────────────
        // Separate fields rather than merged text, so FormatForPrompt can label
        // which layer a rule came from. An AI told "the client overrides the
        // house defaults" can only act on that if it can see which is which.
        /// <summary>
        /// Every other <c>*.md</c> at the shared bank's root, alongside the three
        /// named files. Kept apart from <see cref="ExtraArticles"/> so the
        /// formatter can put them under the house-defaults heading, where they
        /// belong, rather than among the client's own material.
        /// </summary>
        public List<string> SharedExtraArticles { get; set; } = new List<string>();

        public List<string> SharedExtraPaths { get; set; } = new List<string>();

        public string SharedBriefText { get; set; }
        public string SharedTerminologyText { get; set; }
        public string SharedStyleText { get; set; }

        /// <summary>True if any KB content was loaded.</summary>
        public bool HasContent =>
            !string.IsNullOrWhiteSpace(ClientProfileText) ||
            !string.IsNullOrWhiteSpace(StyleGuideText) ||
            TerminologyArticles.Count > 0 ||
            ExtraArticles.Count > 0 ||
            !string.IsNullOrWhiteSpace(SharedBriefText) ||
            !string.IsNullOrWhiteSpace(SharedTerminologyText) ||
            !string.IsNullOrWhiteSpace(SharedStyleText);

        /// <summary>
        /// Estimated token count (chars / 4 heuristic).
        /// </summary>
        public int EstimatedTokens
        {
            get
            {
                int chars = 0;
                if (ClientProfileText != null) chars += ClientProfileText.Length;
                if (StyleGuideText != null) chars += StyleGuideText.Length;
                foreach (var t in TerminologyArticles) chars += t.Length;
                foreach (var t in ExtraArticles) chars += t.Length;
                if (SharedBriefText != null) chars += SharedBriefText.Length;
                if (SharedTerminologyText != null) chars += SharedTerminologyText.Length;
                if (SharedStyleText != null) chars += SharedStyleText.Length;
                foreach (var t in SharedExtraArticles) chars += t.Length;
                return chars / 4;
            }
        }

        /// <summary>
        /// Trims to fit a token budget. Drops the shared layer before the client
        /// layer - the client bank is the one that was chosen deliberately, and
        /// it overrides the defaults anyway, so shedding defaults loses least.
        /// Terminology is dropped last on each layer: it is the densest content
        /// and the hardest for a model to guess.
        /// </summary>
        public void TrimToTokenBudget(int maxTokens)
        {
            if (maxTokens <= 0 || EstimatedTokens <= maxTokens) return;

            // Each drop is recorded. The order below is the priority order —
            // shared house defaults go before the client's own material — and
            // TrimmedPaths preserves it, so a caller reading the list sees what
            // was considered least important first.
            var sharedPrefix = MemoryBankReader.SharedBankName + "/";

            // First of all, because they are the least canonical thing here: the
            // shared layer loses before the client layer, and within the shared
            // layer the three named files are the ones the format is built
            // around.
            while (SharedExtraArticles.Count > 0 && EstimatedTokens > maxTokens)
            {
                SharedExtraArticles.RemoveAt(SharedExtraArticles.Count - 1);
                if (SharedExtraPaths.Count > 0)
                {
                    TrimmedPaths.Add(SharedExtraPaths[SharedExtraPaths.Count - 1]);
                    SharedExtraPaths.RemoveAt(SharedExtraPaths.Count - 1);
                }
            }

            if (EstimatedTokens > maxTokens && SharedBriefText != null)
            {
                SharedBriefText = null;
                TrimmedPaths.Add(sharedPrefix + MemoryBankReader.BriefFile);
            }
            if (EstimatedTokens > maxTokens && SharedStyleText != null)
            {
                SharedStyleText = null;
                TrimmedPaths.Add(sharedPrefix + MemoryBankReader.StyleFile);
            }
            if (EstimatedTokens > maxTokens && SharedTerminologyText != null)
            {
                SharedTerminologyText = null;
                TrimmedPaths.Add(sharedPrefix + MemoryBankReader.TerminologyFile);
            }

            if (EstimatedTokens > maxTokens && StyleGuideText != null)
            {
                TrimmedPaths.Add(StyleGuidePath ?? MemoryBankReader.StyleFile);
                StyleGuideText = null;
                StyleGuidePath = null;
            }

            while (ExtraArticles.Count > 0 && EstimatedTokens > maxTokens)
            {
                ExtraArticles.RemoveAt(ExtraArticles.Count - 1);
                if (ExtraPaths.Count > 0)
                {
                    TrimmedPaths.Add(ExtraPaths[ExtraPaths.Count - 1]);
                    ExtraPaths.RemoveAt(ExtraPaths.Count - 1);
                }
            }

            while (TerminologyArticles.Count > 0 && EstimatedTokens > maxTokens)
            {
                TerminologyArticles.RemoveAt(TerminologyArticles.Count - 1);
                if (TerminologyPaths.Count > 0)
                {
                    TrimmedPaths.Add(TerminologyPaths[TerminologyPaths.Count - 1]);
                    TerminologyPaths.RemoveAt(TerminologyPaths.Count - 1);
                }
            }

            // Last resort: truncate the brief.
            if (EstimatedTokens > maxTokens && ClientProfileText != null)
            {
                var maxChars = maxTokens * 4;
                if (ClientProfileText.Length > maxChars)
                {
                    ClientProfileText = ClientProfileText.Substring(0, maxChars) + "\n[... truncated ...]";
                    ClientProfileTruncated = true;
                }
            }
        }

        /// <summary>
        /// Returns a short summary of what was loaded (for UI display).
        /// </summary>
        public string GetSummary()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ClientProfileText))
                parts.Add("bank: " + (ClientName ?? "active"));
            if (TerminologyArticles.Count > 0)
                parts.Add("terminology");
            if (!string.IsNullOrWhiteSpace(StyleGuideText))
                parts.Add("style");
            if (!string.IsNullOrWhiteSpace(SharedBriefText) ||
                !string.IsNullOrWhiteSpace(SharedTerminologyText) ||
                !string.IsNullOrWhiteSpace(SharedStyleText))
                parts.Add("+ " + MemoryBankReader.SharedBankName);

            if (parts.Count == 0) return null;
            return "SuperMemory: " + string.Join(", ", parts) +
                " (~" + EstimatedTokens + " tokens)";
        }
    }
}

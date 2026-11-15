using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Supervertaler.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// The memory-bank block a TRANSLATION sends - Batch Translate, Alt+T, the
    /// prompt preview and SuperBench - for one document.
    ///
    /// <para>It differs from the chat's block in two ways. Notes marked
    /// <c>audience: assistant</c> stay out: they are written for the AI Assistant
    /// and the MCP clients, and the chat and MCP still load them. And a bank over
    /// <see cref="BankExtract.Threshold"/> is narrowed to the part this document
    /// needs (see <see cref="BankExtract"/>): the terminology rows that occur in
    /// it, and the articles one small AI call picks from the start of the
    /// document.</para>
    ///
    /// <para>That call is made the first time a document is translated, never
    /// when it is opened, so no AI call is made and no document text leaves the
    /// machine unless the translator translates (decided 27 Sep 2026). The result
    /// is then reused for the document until the bank changes, so every request
    /// of a job carries the same bytes and reads the provider's prompt
    /// cache.</para>
    ///
    /// <para>Everything here may run off the UI thread. It reads no Trados object:
    /// the view part gathers the document and the bank on the UI thread and hands
    /// them in.</para>
    /// </summary>
    internal static class TranslationBankContext
    {
        /// <summary>The budget every translation prompt has carried the bank under.</summary>
        internal const int TokenBudget = 24000;

        /// <summary>How long the article choice may take before every article is kept.</summary>
        internal static readonly TimeSpan ChooserTimeout = TimeSpan.FromSeconds(90);

        /// <summary>
        /// The time the article choice gets: <see cref="ChooserTimeout"/> for a
        /// cloud model, the translator's local-server timeout for a model on their
        /// own machine. 90 seconds is never enough without a graphics card: the
        /// choice is a ~4,300-token prompt, and gemma4:26b-a4b reads ~16 tokens a
        /// second on Michael's PC, so it gave up every time and kept every
        /// article (2026-10-17) - including house rules written for other
        /// language directions and kinds of text. Michael chose to let it finish:
        /// it is asked once per document, and with a graphics card it takes
        /// seconds anyway.
        /// </summary>
        internal static TimeSpan ChooserTimeoutFor(string provider, int localTimeoutMinutes)
        {
            if (!IsLocalServer(provider)) return ChooserTimeout;
            return TimeSpan.FromMinutes(localTimeoutMinutes > 0
                ? localTimeoutMinutes
                : LlmClient.LocalServerDefaultTimeoutMinutes);
        }

        /// <summary>Ollama, or an OpenAI-compatible server such as LM Studio.</summary>
        internal static bool IsLocalServer(string provider) =>
            provider == LlmModels.ProviderOllama || provider == LlmModels.ProviderCustomOpenAi;

        /// <summary>How many extract files are kept - one per document ever translated would grow forever.</summary>
        internal const int KeepFiles = 200;

        /// <summary>An extract file not rewritten for this long belongs to a finished job.</summary>
        internal static readonly TimeSpan KeepFilesFor = TimeSpan.FromDays(90);

        /// <summary>
        /// Where each document's extract is written for the translator to read.
        /// Outside the bank folders, which may be synced or kept in git; memoQ
        /// keeps its own under memoq/.
        /// </summary>
        internal static string ExtractsDir => Path.Combine(UserDataPath.TradosDir, "bank-extracts");

        internal sealed class Result
        {
            /// <summary>What goes into the prompt; null when there is nothing to send.</summary>
            public string Block;

            /// <summary>Lines for the Batch log, each complete in itself.</summary>
            public List<string> Log = new List<string>();
        }

        /// <summary>
        /// The files <see cref="MemoryBankReader.LoadContext"/> reads - the
        /// top-level <c>*.md</c> of the bank and of <c>_shared</c> - by name, size
        /// and time, so an edit, an addition, a removal or a rename all change it.
        /// <c>reference/</c> never reaches a prompt, so it is not looked at. A few
        /// files, cheap enough to take on every translation.
        /// </summary>
        internal static string BankFingerprint(string bankDir)
        {
            var sb = new StringBuilder();
            foreach (var dir in new[] { bankDir, SharedDir(bankDir) })
            {
                sb.Append('>').Append(dir);
                if (dir == null || !Directory.Exists(dir)) continue;
                try
                {
                    var files = new DirectoryInfo(dir).GetFiles("*.md", SearchOption.TopDirectoryOnly);
                    Array.Sort(files, (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
                    foreach (var f in files)
                        sb.Append('|').Append(f.Name).Append(':').Append(f.Length)
                          .Append(':').Append(f.LastWriteTimeUtc.Ticks);
                }
                catch
                {
                    // Never matches a cached block: a bank that could not be listed
                    // is read again next time rather than remembered as it looked.
                    sb.Append("|unlisted:").Append(Guid.NewGuid().ToString("N"));
                }
            }
            return sb.ToString();
        }

        // The block made last, and what it was made from. One document at a time:
        // a translator works in one, and a second key would only keep a finished
        // job's selection alive.
        private static readonly object _lastLock = new object();
        private static string _lastKey;
        private static Task<Result> _last;

        /// <summary>
        /// The bank block a translation of one document sends, for every caller
        /// that translates: Batch Translate, Proofread, Preview prompt, SuperBench
        /// and Translate active segment (Alt+T) - with the Supervertaler Assistant
        /// pane or without it. There is no second way to get one, so two callers
        /// cannot send different banks for the same document and settings.
        ///
        /// <para>Null (as a completed task) when the setting is off or the bank
        /// has no files. Made once per bank, state of its files, language pair and
        /// document, and the same task is handed to every caller after that, so
        /// all the requests of a job carry the same bytes. A bank at or under
        /// <see cref="BankExtract.Threshold"/> is read here and is ready at once.
        /// A larger one is selected on a pool thread - it may ask the model which
        /// articles the document needs - and the caller waits where it can.</para>
        ///
        /// <para><paramref name="documentText"/> and <paramref name="createClient"/>
        /// are called here, on the caller's thread, and only for a bank over the
        /// threshold: reading the document is the host's business, and its
        /// thread's. The task returned never faults.</para>
        /// </summary>
        /// <param name="bankName">The project's bank; blank for the default one.</param>
        /// <param name="createClient">A client for the model the translation uses, or
        /// null when there is none: then every article is kept.</param>
        internal static Task<Result> ForDocument(
            AiSettings ai,
            string bankName,
            string projectName,
            string sourceLang,
            string targetLang,
            string documentKey,
            string fileName,
            Func<string> documentText,
            Func<LlmClient> createClient)
        {
            var none = Task.FromResult<Result>(null);
            try
            {
                if (ai?.IncludeSuperMemoryContext == false) return none;

                if (string.IsNullOrWhiteSpace(bankName)) bankName = UserDataPath.DefaultMemoryBankName;
                var bankDir = UserDataPath.GetMemoryBankDir(bankName);
                // The bank or _shared beside it: a project with no bank of its own
                // still sends the house defaults.
                if (!UserDataPath.HasBankToRead(bankDir)) return none;
                var reader = new MemoryBankReader(bankDir);

                var key = string.Join("\n", bankDir, BankFingerprint(bankDir), sourceLang, targetLang, documentKey);
                lock (_lastLock)
                {
                    if (_last != null && string.Equals(key, _lastKey, StringComparison.Ordinal)) return _last;
                }

                // Untrimmed: the selection decides what goes before the budget does.
                // No domain: it only labels the result, and detecting it analyses the
                // whole document on every call.
                var full = reader.LoadContext(projectName, null, sourceLang, targetLang,
                    tokenBudget: 0, forTranslation: true);

                Task<Result> task;
                if (full == null)
                {
                    task = none;
                }
                else if (full.EstimatedTokens <= BankExtract.Threshold)
                {
                    // Sent whole, as before: needs neither the document nor a model.
                    task = Task.FromResult(Build(full, null, sourceLang, targetLang,
                        null, null, null, null, null));
                }
                else
                {
                    var text = documentText?.Invoke() ?? "";
                    var client = createClient?.Invoke();   // none: no way to choose, so every article is kept
                    var chooserName = ai == null ? null
                        : (ai.SelectedProvider == LlmModels.ProviderCustomOpenAi
                            ? ai.GetActiveCustomProfile()?.Model
                            : ai.GetSelectedModel());
                    var label = (projectName ?? "this project") + ", " + (fileName ?? "this document")
                        + (reader.VaultExists
                            ? ", bank '" + bankName + "'"
                            : ", no project bank (" + MemoryBankReader.SharedBankName + " only)");
                    var stem = FileStem(fileName, documentKey);
                    var chooserTimeout = ChooserTimeoutFor(ai?.SelectedProvider, ai?.OllamaTimeoutMinutes ?? 0);

                    task = Task.Run(() =>
                    {
                        try
                        {
                            return Build(full, text, sourceLang, targetLang,
                                client == null
                                    ? null
                                    : (Func<ArticleSelectionRequest, IList<string>>)(request =>
                                        ChooseArticles(client, request, chooserTimeout)),
                                chooserName,
                                // A reader of its own: this runs on a pool thread.
                                () => new MemoryBankReader(bankDir).LoadContext(projectName, null, sourceLang, targetLang,
                                    tokenBudget: TokenBudget, forTranslation: true),
                                label, stem);
                        }
                        finally { client?.Dispose(); }
                    });
                }

                lock (_lastLock)
                {
                    _lastKey = key;
                    _last = task;
                }
                return task;
            }
            catch (Exception ex)
            {
                // Always written: with the bank switched on, a translation that goes
                // out without it must leave a trace whatever the logging setting.
                DiagnosticLog.WriteAlways("SuperMemory", "Could not prepare the bank for translation, so nothing from it is sent: " + ex.Message);
                return none;   // the bank is optional - never block translation
            }
        }

        /// <summary>
        /// The bank a project's prompts read: the one recorded for it, else the
        /// one named after it (<see cref="UserDataPath.SanitizeBankName"/> of the
        /// project's name; exact match or nothing, and never <c>_shared</c>), else
        /// "" - none of its own, which every reader resolves to the default bank,
        /// with <c>_shared</c> on top. Never the bank of the project that was open
        /// before. The one rule, for the pane when the project changes and for
        /// Alt+T when there is no pane to have applied it.
        /// </summary>
        internal static string BankForProject(string recorded, string projectName, out bool matchedByName)
        {
            matchedByName = false;
            if (!string.IsNullOrEmpty(recorded)) return recorded;

            var slug = UserDataPath.SanitizeBankName(projectName);
            if (slug.Length > 0 && !UserDataPath.IsSharedBankName(slug)
                && UserDataPath.ListMemoryBanks().Any(b => string.Equals(b, slug, StringComparison.Ordinal)))
            {
                matchedByName = true;
                return slug;
            }
            return "";
        }

        /// <summary>
        /// Selects from <paramref name="full"/> for this document and formats the
        /// result. Never throws: the bank is optional, and a translation must not
        /// fail because of it.
        ///
        /// <para><paramref name="full"/> is loaded untrimmed (budget 0) and for
        /// translation, and is modified in place. <paramref name="reload"/> loads
        /// the bank again the old way - whole, within <see cref="TokenBudget"/> -
        /// for when selecting fails part-way and <paramref name="full"/> can no
        /// longer be trusted. <paramref name="documentText"/> may be null for a
        /// bank at or under the threshold, which is sent whole without it.</para>
        /// </summary>
        internal static Result Build(
            KbContext full,
            string documentText,
            string sourceLang,
            string targetLang,
            Func<ArticleSelectionRequest, IList<string>> chooseArticles,
            string chooserName,
            Func<KbContext> reload,
            string label,
            string fileStem)
        {
            var result = new Result();
            try
            {
                var extract = BankExtract.Build(full, documentText, sourceLang, targetLang, TokenBudget,
                    chooseArticles, earlierChoice: null, chooserName: chooserName);

                result.Block = extract.Context.HasContent ? MemoryBankReader.FormatForPrompt(extract.Context) : null;

                if (!extract.Selected)
                {
                    result.Log.Add(extract.Context.GetSummary() ?? ("SuperMemory: ~" + extract.Context.EstimatedTokens + " tokens"));
                    foreach (var line in extract.Report) result.Log.Add("  " + line);
                    return result;
                }

                var path = WriteFile(extract, label, fileStem, out var writeError);
                result.Log.Add("SuperMemory: sending " + Tokens(extract.TokensAfter) + " of the bank's "
                    + Tokens(extract.TokensBefore) + ", selected for this document.");
                foreach (var line in extract.Report) result.Log.Add("  " + line);
                result.Log.Add(path != null
                    ? "  What was sent, and why: " + path
                    : "  Could not write the extract file (" + writeError + ").");
                return result;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("SuperMemory", "Selecting from the bank failed: " + ex);
                result.Log.Clear();
                try
                {
                    var whole = reload?.Invoke();
                    result.Block = whole != null && whole.HasContent ? MemoryBankReader.FormatForPrompt(whole) : null;
                    result.Log.Add("SuperMemory: could not select from the bank (" + ex.Message + "); sending it "
                        + "as before, within the " + Tokens(TokenBudget) + " budget.");
                }
                catch (Exception ex2)
                {
                    DiagnosticLog.Log("SuperMemory", "Loading the bank whole failed too: " + ex2);
                    result.Block = null;
                    result.Log.Add("SuperMemory: could not read the bank (" + ex2.Message + "); nothing from it is sent.");
                }
                return result;
            }
        }

        /// <summary>
        /// The article choice: one request to the model the translation uses.
        /// A null answer or a throw makes <see cref="BankExtract.Build"/> keep every
        /// article and record why. It appears in Reports and the usage ledger under
        /// SuperMemory, like every other call that costs money.
        /// </summary>
        internal static IList<string> ChooseArticles(LlmClient client, ArticleSelectionRequest request, TimeSpan timeout)
        {
            using (var cancel = new CancellationTokenSource(timeout))
            {
                string reply;
                try
                {
                    reply = client.SendPromptAsync(
                            BankExtract.SelectionUserPrompt(request),
                            BankExtract.SelectionSystemPrompt,
                            cancellationToken: cancel.Token,
                            feature: PromptLogFeature.SuperMemory,
                            promptName: "Memory bank selection")
                        .ConfigureAwait(false).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    throw new TimeoutException("no answer within " + (int)timeout.TotalSeconds + " seconds");
                }
                return BankExtract.ParseSelection(reply, request.Candidates);
            }
        }

        /// <summary>
        /// A file name for one document's extract: its name, readable, and a short
        /// hash of its identity, so two documents called the same do not share one.
        /// </summary>
        internal static string FileStem(string documentName, string documentKey)
        {
            var name = documentName ?? "";
            if (name.EndsWith(".sdlxliff", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - ".sdlxliff".Length);
            var invalid = Path.GetInvalidFileNameChars();
            name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
            if (name.Length > 80) name = name.Substring(0, 80).TrimEnd(' ', '.');
            if (name.Length == 0) name = "document";

            string hash;
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(documentKey ?? ""));
                hash = string.Concat(bytes.Take(4).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
            return name + " - " + hash;
        }

        /// <summary>
        /// Writes the extract where the translator can read it. Returns the path,
        /// or null with <paramref name="error"/> set; never throws, since the file
        /// is for reading and the translation does not depend on it.
        /// </summary>
        internal static string WriteFile(BankExtractResult extract, string label, string fileStem, out string error)
        {
            error = null;
            try
            {
                var dir = ExtractsDir;
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, fileStem + ".md");
                File.WriteAllText(path, BankExtract.FormatFile(extract, label, DateTime.Now), new UTF8Encoding(false));

                var pruned = Prune(dir, KeepFiles, KeepFilesFor, path);
                if (pruned > 0) DiagnosticLog.Log("SuperMemory", "Removed " + pruned + " old bank extract file(s)");
                return path;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                DiagnosticLog.Log("SuperMemory", "Could not write the bank extract: " + ex);
                return null;
            }
        }

        /// <summary>
        /// Keeps the newest <paramref name="keep"/> extract files and removes any
        /// older than <paramref name="maxAge"/>, never <paramref name="justWritten"/>.
        /// Returns how many went. A file that cannot be removed is left for next
        /// time: this is housekeeping, and must never fail the write it follows.
        /// </summary>
        internal static int Prune(string folder, int keep, TimeSpan maxAge, string justWritten)
        {
            var removed = 0;
            try
            {
                var files = new DirectoryInfo(folder).GetFiles("*.md")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
                var cutoff = DateTime.UtcNow - maxAge;

                for (var i = 0; i < files.Count; i++)
                {
                    var f = files[i];
                    if (string.Equals(f.FullName, justWritten, StringComparison.OrdinalIgnoreCase)) continue;
                    if (i < keep && f.LastWriteTimeUtc >= cutoff) continue;

                    try { f.Delete(); removed++; }
                    catch { /* locked or in use: next time */ }
                }
            }
            catch { /* a folder we cannot list is not worth failing over */ }
            return removed;
        }

        /// <summary>The <c>_shared</c> bank beside <paramref name="bankDir"/>, or null when the bank is <c>_shared</c> itself.</summary>
        private static string SharedDir(string bankDir)
        {
            try
            {
                var name = Path.GetFileName((bankDir ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.Equals(name, MemoryBankReader.SharedBankName, StringComparison.OrdinalIgnoreCase)) return null;
                var root = Path.GetDirectoryName(bankDir);
                return string.IsNullOrEmpty(root) ? null : Path.Combine(root, MemoryBankReader.SharedBankName);
            }
            catch { return null; }
        }

        private static string Tokens(int n) =>
            n.ToString("N0", CultureInfo.InvariantCulture) + " tokens";
    }
}

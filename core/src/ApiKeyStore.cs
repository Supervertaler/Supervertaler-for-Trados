using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Supervertaler.Core
{
    /// <summary>
    /// One API-key file for every Supervertaler product (Trados #108):
    /// <c>&lt;data root&gt;\settings\api-keys.json</c>, one key per provider, plain
    /// text, hand-editable. Trados and memoQ read it through this class; Sidekick
    /// reads the same file with its own parser.
    ///
    /// It is the source of truth: a product's own settings hold a key only as a
    /// legacy fallback, and <see cref="MigrateFrom"/> fills the file from those the
    /// first time, so nothing is typed twice. Plain text on purpose - a key that can
    /// be rotated by pasting a line into a text file is a key that actually gets
    /// rotated, and anyone who can read the file can read the whole profile anyway.
    ///
    /// Keys are stored under the plugin's provider ids (claude, openai, gemini, grok,
    /// mistral, deepseek, openrouter). Common aliases are accepted on read
    /// (anthropic, google, xai), so a file written by hand in either vocabulary works.
    /// </summary>
    public static class ApiKeyStore
    {
        public static string FilePath
        {
            get
            {
                try { return Path.Combine(SupervertalerPaths.Root, "settings", "api-keys.json"); }
                catch { return null; }
            }
        }

        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "anthropic", LlmModels.ProviderClaude },
            { "xai", LlmModels.ProviderGrok },
            { "x.ai", LlmModels.ProviderGrok },
            { "custom", LlmModels.ProviderCustomOpenAi },
        };

        private static readonly object Gate = new object();

        public static string Canonical(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey)) return providerKey;
            var k = providerKey.Trim().ToLowerInvariant();
            return Aliases.TryGetValue(k, out var c) ? c : k;
        }

        /// <summary>The key for a provider, or null when the file has none. Never throws.</summary>
        public static string Get(string providerKey)
        {
            try
            {
                var map = Load();
                return map.TryGetValue(Canonical(providerKey), out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Writes one key (empty removes it). Returns false only when the file could
        /// not be written.
        ///
        /// <para>A key already stored with this exact value needs no write, and that
        /// is a success: it is by far the commonest case, since a settings dialog
        /// saves every field whether or not it was touched. Reporting it as a
        /// failure made memoQ believe every ordinary OK had failed to save.</para>
        /// </summary>
        public static bool Set(string providerKey, string key)
        {
            var p = Canonical(providerKey);
            var wanted = string.IsNullOrWhiteSpace(key) ? null : key.Trim();

            return Update(map =>
            {
                if (wanted == null) return map.Remove(p);
                if (map.TryGetValue(p, out var have) && have == wanted) return false;
                map[p] = wanted;
                return true;
            }) != null;
        }

        /// <summary>
        /// Adds every non-empty key from a product's own settings that the file does
        /// not have yet. Never overwrites: the file is the source of truth once it
        /// holds a key. Returns how many were added.
        /// </summary>
        public static int MigrateFrom(AiApiKeys local)
        {
            if (local == null) return 0;
            int added = 0;
            Update(map =>
            {
                void Take(string provider, string key)
                {
                    if (string.IsNullOrWhiteSpace(key)) return;
                    if (map.TryGetValue(provider, out var have) && !string.IsNullOrWhiteSpace(have)) return;
                    map[provider] = key.Trim(); added++;
                }
                Take(LlmModels.ProviderOpenAi, local.OpenAi);
                Take(LlmModels.ProviderClaude, local.Claude);
                Take(LlmModels.ProviderGemini, local.Gemini);
                Take(LlmModels.ProviderGrok, local.Grok);
                Take(LlmModels.ProviderMistral, local.Mistral);
                Take(LlmModels.ProviderDeepSeek, local.DeepSeek);
                Take(LlmModels.ProviderOpenRouter, local.OpenRouter);
                return added > 0;
            });
            return added;
        }

        /// <summary>The whole file as provider → key, canonical ids. Empty when absent.</summary>
        public static Dictionary<string, string> Load()
        {
            var path = FilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // A writer holds the file exclusively for a few milliseconds; wait it out.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var reader = new StreamReader(fs, Encoding.UTF8, true))
                        return Parse(reader.ReadToEnd());
                }
                catch (IOException) when (attempt < 20)
                {
                    System.Threading.Thread.Sleep(50);
                }
                catch
                {
                    // "Empty when absent" is what the summary promises, and a file
                    // that cannot be read is as good as absent to every caller here:
                    // the key simply is not available. Without this, a locked file
                    // after the retries - or one the user has no rights to - throws
                    // out of a method documented never to.
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>
        /// One read-modify-write under an exclusive open (#108 handoff): three
        /// products share this file and two of them saving at the same moment must
        /// not lose a write. A lock on the file, not the process; a second writer
        /// waits up to a second. <paramref name="mutate"/> returns false to leave the
        /// file untouched.
        ///
        /// <para>Three outcomes, not two: <c>true</c> written, <c>false</c> nothing
        /// needed writing, <c>null</c> could not be written. Collapsing the last two
        /// is what let a no-op save look like a failed one.</para>
        /// </summary>
        private static bool? Update(Func<Dictionary<string, string>, bool> mutate)
        {
            var path = FilePath;
            if (string.IsNullOrEmpty(path)) return null;
            lock (Gate)
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(path)); } catch { return null; }
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        // FileShare.Delete alone: still exclusive against every reader and
                        // writer, and it lets the empty-file case below delete while the
                        // handle is held, so no other product can open the file in between.
                        using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Delete))
                        {
                            string existing;
                            using (var reader = new StreamReader(fs, Encoding.UTF8, true, 4096, leaveOpen: true))
                                existing = reader.ReadToEnd();
                            var map = Parse(existing);
                            if (!mutate(map))
                            {
                                // Nothing to write. An empty key file - the one OpenOrCreate just
                                // made on a machine that had none, or any other - is removed so it
                                // cannot be mistaken for a failed write. Deleted BEFORE the handle
                                // closes: Windows marks it delete-pending and nobody can open it
                                // in the meantime, so a key another product writes in that window
                                // cannot be lost (memoQ session, 2026-09-06).
                                bool wasEmpty = existing.Length == 0;
                                if (wasEmpty) { try { File.Delete(path); } catch { } }
                                return false;
                            }
                            var bytes = new UTF8Encoding(false).GetBytes(Render(map));
                            fs.SetLength(0);
                            fs.Position = 0;
                            fs.Write(bytes, 0, bytes.Length);
                            fs.Flush(true);
                            return true;
                        }
                    }
                    catch (IOException) when (attempt < 20)
                    {
                        System.Threading.Thread.Sleep(50);
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
        }

        private static Dictionary<string, string> Parse(string json)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // A flat object of strings; nothing else is expected, and anything else is ignored.
            foreach (Match m in Regex.Matches(json ?? "", "\"(?<k>[^\"\\\\]+)\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\""))
            {
                var k = m.Groups["k"].Value;
                if (k.StartsWith("_")) continue;   // "_comment" and the like
                map[Canonical(k)] = Unescape(m.Groups["v"].Value);
            }
            return map;
        }

        private static string Render(Dictionary<string, string> map)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"_comment\": \"API keys shared by Supervertaler for Trados, Supervertaler for memoQ and Supervertaler Sidekick. One key per provider; edit by hand or in any product's settings.\",");
            var keys = new List<string>(map.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < keys.Count; i++)
            {
                sb.Append("  \"").Append(Escape(keys[i])).Append("\": \"").Append(Escape(map[keys[i]] ?? "")).Append("\"");
                sb.AppendLine(i < keys.Count - 1 ? "," : "");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static string Unescape(string s) => (s ?? "").Replace("\\\"", "\"").Replace("\\\\", "\\");

        // ─── Does this look like a key for that provider? ─────────────────────────────

        /// <summary>
        /// A sentence when the key plainly belongs to another service, else null. Only
        /// the prefixes that are stable and public are checked; an unknown shape passes.
        /// </summary>
        public static string CheckShape(string providerKey, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;

            var k = key.Trim();
            var wanted = Canonical(providerKey);
            var prefix = PrefixFor(wanted);
            if (prefix == null) return null;                  // no known shape: anything goes

            var name = NameOf(wanted);

            // The service the key belongs to is decided first, not the prefix the
            // box wants. Every OpenRouter key also starts with OpenAI's "sk-", so
            // testing the wanted prefix first would wave one straight through.
            //
            // Naming the service it DOES belong to is the useful half: "this does
            // not look like an Anthropic key" leaves someone staring at a key that
            // looks perfectly fine to them; "this is your OpenAI key" ends it.
            var actual = ServiceOf(k);
            if (actual != null)
                return string.Equals(actual, name, StringComparison.Ordinal)
                    ? null
                    : "This is " + Article(actual) + " " + actual + " key, not " + Article(name) + " " + name + " one.";

            return "This does not look like " + Article(name) + " " + name
                 + " key - they start with " + prefix + ".";
        }

        /// <summary>The prefix a provider's keys are known to start with, or null.</summary>
        private static string PrefixFor(string providerKey)
        {
            switch (providerKey)
            {
                case LlmModels.ProviderClaude: return "sk-ant-";
                case LlmModels.ProviderOpenAi: return "sk-";
                case LlmModels.ProviderGemini: return "AIza";
                case LlmModels.ProviderGrok: return "xai-";
                case LlmModels.ProviderOpenRouter: return "sk-or-";
                default: return null;
            }
        }

        private static string NameOf(string providerKey)
        {
            switch (providerKey)
            {
                case LlmModels.ProviderClaude: return "Anthropic";
                case LlmModels.ProviderOpenAi: return "OpenAI";
                case LlmModels.ProviderGemini: return "Gemini";
                case LlmModels.ProviderGrok: return "xAI";
                case LlmModels.ProviderOpenRouter: return "OpenRouter";
                default: return providerKey;
            }
        }

        /// <summary>
        /// Which service a key plainly belongs to, or null. Longest prefix first:
        /// every OpenRouter key also starts with "sk-".
        /// </summary>
        private static string ServiceOf(string key)
        {
            if (key.StartsWith("sk-ant-", StringComparison.Ordinal)) return "Anthropic";
            if (key.StartsWith("sk-or-", StringComparison.Ordinal)) return "OpenRouter";
            if (key.StartsWith("sk-", StringComparison.Ordinal)) return "OpenAI";
            if (key.StartsWith("AIza", StringComparison.Ordinal)) return "Gemini";
            if (key.StartsWith("xai-", StringComparison.Ordinal)) return "xAI";
            return null;
        }

        private static string Article(string name)
        {
            if (string.IsNullOrEmpty(name)) return "a";
            return "AEIOUaeiou".IndexOf(name[0]) >= 0 ? "an" : "a";
        }
    }
}

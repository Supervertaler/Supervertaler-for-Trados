using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Core.SuperVault;

namespace Supervertaler.Trados.SuperVault
{
    /// <summary>
    /// Imports a Studio file-based TM (.sdltm) into SuperVault's store, through Studio's public TM API: every unit, read
    /// in batches with a <see cref="RegularIterator"/>, tags kept. Units are added in one transaction, so a TM that
    /// can't be read to the end adds nothing.
    /// </summary>
    public static class SuperVaultImport
    {
        const int Batch = 500;

        public static VaultImportResult Sdltm(string store, string path, Action<string> progress, CancellationToken cancel)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The translation memory was not found: " + path, path);
            ITranslationMemoryLanguageDirection dir;
            string sourceLang, targetLang;
            try
            {
                // Studio checks the file only when it is first used, so the language direction is read here too.
                dir = new FileBasedTranslationMemory(path).LanguageDirection;
                sourceLang = dir.SourceLanguage?.Name ?? "";
                targetLang = dir.TargetLanguage?.Name ?? "";
            }
            catch (Exception ex)
            {
                // A password-protected TM, a TM from a newer Studio, or not a TM at all.
                throw new InvalidOperationException("Studio could not open " + Path.GetFileName(path) + " (" + ex.Message + "). A TM with a password can't be imported; export it to TMX in Studio and import that.", ex);
            }
            var origin = "sdltm:" + Path.GetFileName(path);
            using (var w = new VaultStore.Writer(store))
            {
                var iterator = new RegularIterator { MaxCount = Batch };
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    var units = dir.GetTranslationUnits(ref iterator);
                    if (units == null || units.Length == 0) break;
                    foreach (var tu in units)
                        w.Add(tu.SourceSegment?.Culture?.Name ?? sourceLang, Codes(tu.SourceSegment), tu.TargetSegment?.Culture?.Name ?? targetLang, Codes(tu.TargetSegment), origin);
                    progress?.Invoke(Path.GetFileName(path) + ": " + w.Result.Read.ToString("N0", CultureInfo.InvariantCulture) + " units read");
                }
                cancel.ThrowIfCancellationRequested();
                w.Commit();
                return w.Result;
            }
        }

        /// <summary>
        /// A Studio segment as SuperVault's neutral codes. A tag's Anchor is what pairs a source tag with its target tag
        /// in a Studio TM unit, so it becomes the code's id: <c>&lt;g id="1"&gt;…&lt;/g id="1"&gt;</c>, <c>&lt;x id="2"/&gt;</c>.
        /// </summary>
        public static string Codes(Segment segment)
        {
            if (segment?.Elements == null) return "";
            var sb = new StringBuilder();
            foreach (var e in segment.Elements)
            {
                if (e is Text t) sb.Append(t.Value);
                else if (e is Tag tag)
                {
                    var id = tag.Anchor.ToString(CultureInfo.InvariantCulture);
                    if (tag.Type == TagType.Start) sb.Append("<g id=\"").Append(id).Append("\">");
                    else if (tag.Type == TagType.End) sb.Append("</g id=\"").Append(id).Append("\">");
                    else sb.Append("<x id=\"").Append(id).Append("\"/>");
                }
                else sb.Append(e?.ToString());
            }
            return sb.ToString();
        }
    }
}

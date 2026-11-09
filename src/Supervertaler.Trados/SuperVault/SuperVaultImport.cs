using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    ///
    /// With each unit, its metadata (schema 3): its creation and change dates and users (Studio's system fields), the
    /// fields EverTM keeps (<see cref="VaultFields.Canonical"/>: a TM's "Project", "Projekt", "Client", "Subject",
    /// "Fachgebiet", ...; others are dropped) and the TM's file name as Source. Re-importing a TM fills them into units
    /// imported before.
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
                        w.Add(tu.SourceSegment?.Culture?.Name ?? sourceLang, Codes(tu.SourceSegment), tu.TargetSegment?.Culture?.Name ?? targetLang, Codes(tu.TargetSegment), origin, null, Info(tu, Path.GetFileName(path)));
                    progress?.Invoke(Path.GetFileName(path) + ": " + w.Result.Read.ToString("N0", CultureInfo.InvariantCulture) + " units read");
                }
                cancel.ThrowIfCancellationRequested();
                w.Commit();
                return w.Result;
            }
        }

        /// <summary>A Studio TM unit's metadata: its system fields' dates and users, its kept fields, and Source.</summary>
        internal static VaultUnitInfo Info(TranslationUnit tu, string source)
        {
            var info = new VaultUnitInfo();
            var sf = tu.SystemFields;
            if (sf != null)
            {
                if (Plausible(sf.CreationDate)) { info.Created = sf.CreationDate.ToUniversalTime(); info.CreatedBy = User(sf.CreationUser); }
                if (Plausible(sf.ChangeDate)) { info.Changed = sf.ChangeDate.ToUniversalTime(); info.ChangedBy = User(sf.ChangeUser); }
            }
            if (tu.FieldValues != null)
                foreach (var fv in tu.FieldValues)
                {
                    var field = VaultFields.Canonical(fv?.Name);
                    if (field == null) continue;
                    foreach (var v in Values(fv)) info.Add(field, v);
                }
            info.Add(VaultFields.Source, source);
            return info;
        }

        static bool Plausible(DateTime t) => t.Year >= 1980 && t.Year <= 2100;
        static string User(string u) { var c = VaultFields.Clean(u); return c.Length == 0 ? null : c; }

        /// <summary>A text or picklist field's values; other types (dates, numbers) give none.</summary>
        public static IEnumerable<string> Values(FieldValue fv)
        {
            switch (fv)
            {
                case MultipleStringFieldValue m: return m.Values?.ToList() ?? new List<string>();
                case SingleStringFieldValue s: return new[] { s.Value };
                case MultiplePicklistFieldValue mp: return mp.Values?.Select(i => i?.Name).ToList() ?? new List<string>();
                case SinglePicklistFieldValue sp: return new[] { sp.Value?.Name };
                default: return new string[0];
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

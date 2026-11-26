using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Sdl.Core.Globalization;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.LanguagePlatform.Core;
using Supervertaler.Core.SuperVault;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.SuperVault
{
    /// <summary>
    /// The editor's active source segment as EverTM Matches looks it up, and an EverTM match back into the editor's
    /// target with the document's own tags.
    ///
    /// <para><b>The same segment Translation Results looks up.</b> Studio's editor turns the active segment into a
    /// translation-memory segment with its <c>TUConverter</c> (read from Studio 2022, 2024 and 2026's own editor,
    /// <c>TranslationMemorySegmentSearch.BuildTranslationUnit</c>: tags kept, tags in locked content kept, tracked
    /// changes accepted). EverTM's provider then turns that segment into pieces, one per element
    /// (<see cref="SuperVaultResults.Pieces"/>). The pane does both steps the same way, with the same Studio code, so its
    /// matches, scores and tag placement are the provider's. <c>TUConverter</c> is public, but lives in
    /// <c>Sdl.LanguagePlatform.TranslationMemory</c> in 2024 and 2026 and in <c>…TranslationMemoryTools</c> (another
    /// assembly) in 2022, so it is reached by name, like <see cref="SegmentTagHandler.CreateDocumentItemFactory"/>.</para>
    ///
    /// <para><b>Back into the target.</b> The converter also says which editor tag (an <see cref="ITagPair"/> or
    /// <see cref="IPlaceholderTag"/>) each segment tag came from. A match's target pieces name the document tag they
    /// are (<see cref="VaultMatch.TargetDocTag"/>), so the target is written as text with the numbered placeholders
    /// <see cref="SegmentTagHandler.Serialize"/> gives those editor tags, and rebuilt by
    /// <see cref="SegmentTagHandler.ReconstructTarget"/>, the code every AI translation is written with: tags cloned
    /// from the source, comments kept, line breaks handled.</para>
    /// </summary>
    public sealed class EverTmEditorSegment
    {
        /// <summary>The segment as the TM lookup sees it.</summary>
        public Segment Segment;
        /// <summary>Its pieces, one per element of <see cref="Segment"/>: what <see cref="VaultLibrary.Lookup"/> takes.</summary>
        public List<VaultPiece> Pieces;
        /// <summary>For each element index that is a tag: the editor tag it came from. Missing when the converter didn't say.</summary>
        public Dictionary<int, IAbstractMarkupData> EditorTags = new Dictionary<int, IAbstractMarkupData>();

        static readonly object Gate = new object();
        static MethodInfo _build;
        static Type _settingsType;
        static bool _looked;

        /// <summary>
        /// The active source segment, converted. UI thread (Studio's objects). Throws when the converter can't be found
        /// or fails; the caller shows that rather than an empty list.
        /// </summary>
        public static EverTmEditorSegment From(ISegment source, CultureCode culture)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var build = Converter(out var settingsType);
            if (build == null) throw new InvalidOperationException("Studio's TM segment converter was not found in this Studio version.");

            var settings = Activator.CreateInstance(settingsType);
            Set(settings, "StripTags", false);
            Set(settings, "ExcludeTagsInLockedContentText", false);
            Set(settings, "AcceptTrackChanges", true);

            var args = new object[] { culture, source, settings, null, null };
            Segment segment;
            try { segment = (Segment)build.Invoke(null, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }

            var result = new EverTmEditorSegment { Segment = segment, Pieces = SuperVaultResults.Pieces(segment) };
            if (args[4] is List<KeyValuePair<Tag, IAbstractMarkupData>> tags)
            {
                for (int i = 0; i < segment.Elements.Count; i++)
                {
                    if (!(segment.Elements[i] is Tag t)) continue;
                    // The very tag object; failing that (should the converter ever copy its tags), the same tag id and type.
                    var hit = tags.FirstOrDefault(kv => ReferenceEquals(kv.Key, t));
                    if (hit.Key == null)
                        hit = tags.FirstOrDefault(kv => kv.Key != null && kv.Key.Type == t.Type && kv.Key.TagID == t.TagID);
                    if (hit.Value != null) result.EditorTags[i] = hit.Value;
                }
            }
            return result;
        }

        static void Set(object o, string property, bool value) => o.GetType().GetProperty(property)?.SetValue(o, value);

        /// <summary>TUConverter.BuildLinguaSegment(CultureCode, ISegment, LinguaTuBuilderSettings, out texts, out tags), found once.</summary>
        static MethodInfo Converter(out Type settingsType)
        {
            lock (Gate)
            {
                if (!_looked)
                {
                    _looked = true;
                    foreach (var ns in new[] { "Sdl.LanguagePlatform.TranslationMemory", "Sdl.LanguagePlatform.TranslationMemoryTools" })
                    {
                        // The assembly of the same name as the namespace, loaded by name if nothing has loaded it yet
                        // (Studio 2022's TranslationMemoryTools may not be, when the pane first looks a segment up).
                        var candidates = AppDomain.CurrentDomain.GetAssemblies().ToList();
                        if (!candidates.Any(a => string.Equals(a.GetName().Name, ns, StringComparison.OrdinalIgnoreCase)))
                            try { candidates.Add(Assembly.Load(ns)); } catch { }
                        foreach (var asm in candidates)
                        {
                            Type conv, set;
                            try { conv = asm.GetType(ns + ".TUConverter", false); set = asm.GetType(ns + ".LinguaTuBuilderSettings", false); }
                            catch { continue; }
                            if (conv == null || set == null) continue;
                            var m = conv.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(x =>
                            {
                                if (x.Name != "BuildLinguaSegment") return false;
                                var p = x.GetParameters();
                                return p.Length == 5 && p[0].ParameterType == typeof(CultureCode) && p[1].ParameterType == typeof(ISegment)
                                    && p[2].ParameterType == set && p[3].IsOut && p[4].IsOut;
                            });
                            if (m != null) { _build = m; _settingsType = set; break; }
                        }
                        if (_build != null) break;
                    }
                    if (_build == null)
                        try { DiagnosticLog.WriteAlways("EverTM", "EverTM Matches: Studio's TUConverter.BuildLinguaSegment was not found"); } catch { }
                }
                settingsType = _settingsType;
                return _build;
            }
        }

        // ---- back into the target ----

        static readonly Regex PlaceholderLike = new Regex(@"<\s*/?\s*t\s*\d+\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// The match's target as text with the numbered placeholders <paramref name="tagMap"/> (from
        /// <see cref="SegmentTagHandler.Serialize"/> of the same source) gives the editor's tags. Null when it can't be
        /// written with tags with confidence: a tag the converter didn't trace back, or stored text that itself looks
        /// like a placeholder. The caller then writes plain text and says so.
        /// </summary>
        public string TargetMarkup(VaultMatch m, Dictionary<int, TagInfo> tagMap)
        {
            if (m?.Target == null || tagMap == null) return null;
            var numberOf = new Dictionary<IAbstractMarkupData, int>();
            foreach (var kv in tagMap) if (kv.Value?.OriginalMarkup != null) numberOf[kv.Value.OriginalMarkup] = kv.Key;

            var sb = new StringBuilder();
            for (int i = 0; i < m.Target.Count; i++)
            {
                var p = m.Target[i];
                if (!p.IsTag)
                {
                    if (PlaceholderLike.IsMatch(p.Text ?? "")) return null;
                    sb.Append(p.Text);
                    continue;
                }
                int doc = m.TargetDocTag != null && i < m.TargetDocTag.Count ? m.TargetDocTag[i] : -1;
                if (doc < 0 || !EditorTags.TryGetValue(doc, out var editorTag) || !numberOf.TryGetValue(editorTag, out int n)) return null;
                var tag = Segment.Elements[doc] as Tag;
                var info = tagMap[n];
                if (tag != null && tag.Type == Sdl.LanguagePlatform.Core.TagType.Start && info.TagType == Core.TagType.Paired) sb.Append("<t").Append(n).Append('>');
                else if (tag != null && tag.Type == Sdl.LanguagePlatform.Core.TagType.End && info.TagType == Core.TagType.Paired) sb.Append("</t").Append(n).Append('>');
                else sb.Append("<t").Append(n).Append("/>");   // a placeholder, or locked content (cloned whole)
            }
            return sb.ToString();
        }

        /// <summary>The match's target as plain text: for a match whose tags can't be placed.</summary>
        public static string PlainTarget(VaultMatch m) =>
            m?.Target == null ? "" : string.Concat(m.Target.Where(p => !p.IsTag).Select(p => p.Text));

        /// <summary>Whether the document segment has any tags (a plain-text insert then loses formatting).</summary>
        public bool HasTags => Pieces.Any(p => p.IsTag);
    }
}

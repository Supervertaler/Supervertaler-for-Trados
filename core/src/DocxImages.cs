using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Supervertaler.Core
{
    /// <summary>
    /// An image pulled out of a .docx, together with the thing that ties it to
    /// the text.
    ///
    /// <para><see cref="Anchor"/> is the point of this class. A folder of image
    /// files has already lost it: you cannot recover "page 3, between 'Mount the
    /// bracket' and 'Tighten the screws'" from <c>IMG_2094.png</c>. A label
    /// exists only when the document names its figures; the anchor exists
    /// always, which is what lets this feature work on documents whose images
    /// have no names at all.</para>
    /// </summary>
    public class ExtractedImage
    {
        /// <summary>1-based position in document order.</summary>
        public int Ordinal { get; set; }

        /// <summary>
        /// Index of the paragraph carrying the image: the same index
        /// <see cref="DocxStructure.ReadParagraphs(Stream)"/> assigns, so a host
        /// that has matched paragraphs to its segments can place the image
        /// without a second alignment.
        /// </summary>
        public int ParagraphIndex { get; set; }

        /// <summary>"FIG. 3", "Table 2", "Plate IV" - or null when the document
        /// does not name it. Null is the normal case outside patents and papers.</summary>
        public string Label { get; set; }

        /// <summary>Text of the paragraph that reads as this image's caption,
        /// or null. Distinct from <see cref="Label"/>: a caption may be a whole
        /// sentence, and is itself usually a segment being translated.</summary>
        public string Caption { get; set; }

        /// <summary>The source text the image sits among. Always populated when
        /// the document has any text near the image.</summary>
        public string Anchor { get; set; }

        /// <summary>The image part's name inside the package, e.g.
        /// "/word/media/image3.png". Stable within one file.</summary>
        public string PartName { get; set; }

        public string ContentType { get; set; }

        /// <summary>Extension implied by the content type, including the dot.</summary>
        public string Extension { get; set; }

        public long SizeBytes { get; set; }

        /// <summary>The bytes, only when extraction was asked for them.
        /// Off by default: a 32-bit host and a document whose pictures run to
        /// tens of megabytes do not mix.</summary>
        public byte[] Data { get; set; }

        /// <summary>How <see cref="Label"/> was arrived at - useful when a
        /// label looks wrong and someone has to work out why.</summary>
        public string LabelSource { get; set; }

        /// <summary>What the document SAYS this figure shows, found by figure
        /// number rather than by proximity.
        ///
        /// <para>On a patent this is the only route that works: the plates are
        /// at the back and their descriptions are in the body, hundreds of
        /// paragraphs away. Expect more than one - a short entry in the figure
        /// list and a longer one in the detailed description - and keep both,
        /// because the longer one usually names the parts.</para></summary>
        public List<string> Descriptions { get; set; } = new List<string>();

        public override string ToString()
        {
            return "#" + Ordinal + " " + (Label ?? "(unlabelled)") + " [" + PartName + "]";
        }
    }

    /// <summary>How the labels in a <see cref="DocxImageSet"/> were arrived at.</summary>
    public enum LabelingMethod
    {
        /// <summary>No labels found at all.</summary>
        None,
        /// <summary>Nth image paired with Nth plate label, assertion passed.
        /// The reliable case: exact, and checked.</summary>
        Ordinal,
        /// <summary>Labels taken from neighbouring paragraphs. Right for inline
        /// images with captions; a guess on anything plate-shaped.</summary>
        Proximity,
        /// <summary>A plate-label sequence exists but does not line up with the
        /// images. Nothing is labelled, deliberately.</summary>
        Refused,
    }

    /// <summary>The images in one .docx, and how confident their labels are.</summary>
    public class DocxImageSet
    {
        public List<ExtractedImage> Images { get; set; } = new List<ExtractedImage>();

        public LabelingMethod Method { get; set; } = LabelingMethod.None;

        /// <summary>Non-null when the caller must not trust the labels, with the
        /// reason in plain words. Surface it; do not swallow it.</summary>
        public string Warning { get; set; }

        /// <summary>File names written when extraction was asked to save, in
        /// document order. Empty otherwise.</summary>
        public List<string> SavedFiles { get; set; } = new List<string>();
    }

    /// <summary>
    /// Reads a .docx and returns each image with its label, caption and anchor.
    /// No AI, no network.
    ///
    /// <para>Built on <see cref="DocxStructure"/>'s paragraph scanner and the
    /// package's own relationship and content-type parts, with nothing beyond
    /// <c>System.IO.Compression</c>: the plugins load into someone else's
    /// process, where every extra DLL is a packaging and resolution risk. Ported
    /// from the OpenXml-based extractor in Supervertaler for Trados, whose
    /// detection rules carry a bug history worth inheriting (see
    /// <see cref="DetectLabel"/>); the port was checked against it document by
    /// document, image by image, byte for byte.</para>
    ///
    /// <para>Scale: one pass over <c>document.xml</c> as a string plus one regex
    /// scan per paragraph's own spans; image bytes are streamed, never held,
    /// unless asked for. A 300-paragraph document with 14 drawings takes
    /// milliseconds.</para>
    /// </summary>
    public static class DocxImageExtractor
    {
        // Word's built-in Caption style, plus the synonyms real documents use.
        private static readonly HashSet<string> CaptionStyleIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "caption", "figurecaption", "figurelegend",
                "tablecaption", "tablelegend",
            };

        // Ordered by specificity: patent FIG. first, then academic Figure, then
        // the rest. Each captures the whole label verbatim so the document's own
        // spelling and capitalisation survive.
        private static readonly Regex[] LabelPatterns =
        {
            // Patent style: FIG. 7, FIGS. 6, FIG.7, FIG 7
            new Regex(@"\b(FIGS?\.?\s*\d+[A-Za-z]?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // Academic / generic
            new Regex(@"\b(Figures?\s+\d+[A-Za-z]?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\b(Fig\.?\s+\d+[A-Za-z]?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\b(Tables?\s+\d+[A-Za-z]?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\b(Diagrams?\s+\d+[A-Za-z]?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\b(Charts?\s+\d+[A-Za-z]?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\b(Photo(?:graph)?s?\s+\d+[A-Za-z]?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\b(Schemes?\s+\d+[A-Za-z]?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // Plates take Roman numerals, per older scientific practice.
            new Regex(@"\b(Plates?\s+(?:\d+|[IVXLCM]+))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\b(Exhibits?\s+[A-Za-z]?\d*[A-Za-z]?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        /// <summary>How many paragraphs either side of the image make up the anchor.</summary>
        private const int AnchorWindow = 2;

        /// <summary>A sentence opening by naming a figure: "Figuur 8 toont ...",
        /// "Figure 3 shows ...", "FIG. 12 is a section through ...". Dutch,
        /// English and the bare patent form, because the source language is the
        /// one being described.</summary>
        private static readonly Regex DescriptionOpener = new Regex(
            @"^\s*(?:Figuur|Figure|Fig|FIG)\.?\s*(\d+)\s*[A-Za-z]?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Sentence splitter, good enough for this: the openers we care
        /// about always start a sentence.</summary>
        private static readonly Regex SentenceSplit = new Regex(@"(?<=[.!?])\s+", RegexOptions.Compiled);

        // The image references a paragraph can carry: DrawingML a:blip (r:embed or
        // r:link) and legacy VML v:imagedata (r:id).
        private static readonly Regex ImageRef = new Regex("<(?:a:blip|v:imagedata)\\b[^>]*>", RegexOptions.Compiled);
        private static readonly Regex AttrEmbed = new Regex("\\br:embed=\"([^\"]*)\"", RegexOptions.Compiled);
        private static readonly Regex AttrLink = new Regex("\\br:link=\"([^\"]*)\"", RegexOptions.Compiled);
        private static readonly Regex AttrId = new Regex("\\br:id=\"([^\"]*)\"", RegexOptions.Compiled);
        private static readonly Regex RelationshipTag = new Regex("<Relationship\\b[^>]*>", RegexOptions.Compiled);
        private static readonly Regex AttrAny = new Regex("\\b(Id|Type|Target|TargetMode|Extension|ContentType|PartName)=\"([^\"]*)\"", RegexOptions.Compiled);

        /// <summary>An image part of the package: its name, content type and zip entry.</summary>
        private sealed class ImagePart
        {
            public string PartName;      // "/word/media/image1.png"
            public string ContentType;
            public ZipArchiveEntry Entry;
        }

        /// <summary>
        /// Every image in the file at <paramref name="docxPath"/>, in document
        /// order. Returns an empty set rather than throwing when the file cannot
        /// be read as a .docx - callers are UI paths and a malformed file is not
        /// exceptional.
        /// </summary>
        /// <param name="includeImageData">Load the bytes as well. Off by default.</param>
        /// <param name="saveToFolder">When set, each image is streamed to a file
        /// in this folder, named for the figure it is.</param>
        public static DocxImageSet Extract(string docxPath, bool includeImageData = false, string saveToFolder = null)
        {
            if (string.IsNullOrWhiteSpace(docxPath) || !File.Exists(docxPath)) return new DocxImageSet();
            try
            {
                // ReadWrite share: the document is often open in Word at the same
                // time, and Word holds it with a write lock. A reader that refuses
                // to share reports "no images" for a file that has several.
                using (var fs = new FileStream(docxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return Extract(fs, includeImageData, saveToFolder);
            }
            catch { return new DocxImageSet(); }
        }

        /// <summary>The same for a stream holding the .docx (or a zip wrapping it, as Studio embeds it).</summary>
        public static DocxImageSet Extract(Stream stream, bool includeImageData = false, string saveToFolder = null)
        {
            try
            {
                using (var pkg = DocxStructure.OpenPackage(stream))
                    return pkg == null ? new DocxImageSet() : Extract(pkg, includeImageData, saveToFolder);
            }
            catch { return new DocxImageSet(); }
        }

        /// <summary>The same for an already opened package, so a host that has it open for the structure reader does not open it twice.</summary>
        public static DocxImageSet Extract(ZipArchive package, bool includeImageData = false, string saveToFolder = null)
        {
            var set = new DocxImageSet();
            var results = set.Images;
            try
            {
                var xml = DocxStructure.ReadEntry(package, "word/document.xml");
                if (xml == null) return set;
                var paragraphs = DocxStructure.ParseParagraphs(xml);
                var parts = ReadImageParts(package);

                // Per paragraph: its own text, its own image relationship ids, and
                // whether it is Caption-styled.
                int n = paragraphs.Count;
                var texts = new string[n];
                var rids = new List<string>[n];
                var isCaption = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    var p = paragraphs[i];
                    // The OpenXml original turned a line break into a space; the
                    // structure reader keeps it as a newline for its own callers.
                    texts[i] = (p.Text ?? "").Replace('\n', ' ').Trim();
                    rids[i] = ImageRelationshipIds(xml, p);
                    isCaption[i] = p.StyleId != null && CaptionStyleIds.Contains(p.StyleId.Trim());
                }

                // Plate labels, in document order: paragraphs that ARE a label.
                var plateLabels = new List<string>();
                var plateNumbers = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    string lbl; int num;
                    // Any paragraph that IS a label counts, whether or not it also
                    // carries an image.
                    if (TryPlateLabel(texts[i], out lbl, out num)) { plateLabels.Add(lbl); plateNumbers.Add(num); }
                }

                // Images in document order, before labelling.
                var flat = new List<KeyValuePair<int, string>>();   // paragraph -> rid
                for (int i = 0; i < n; i++)
                    foreach (var rid in rids[i]) flat.Add(new KeyValuePair<int, string>(i, rid));

                // Can we pair by ordinal? Only if the counts match AND the sequence
                // really is 1..N. Anything else and we do not guess.
                var sequenceIsClean = plateNumbers.Count > 0 && plateNumbers.Count == flat.Count;
                if (sequenceIsClean)
                    for (int k = 0; k < plateNumbers.Count; k++)
                        if (plateNumbers[k] != k + 1) { sequenceIsClean = false; break; }

                if (sequenceIsClean)
                {
                    set.Method = LabelingMethod.Ordinal;
                }
                else if (plateNumbers.Count > 0)
                {
                    set.Method = LabelingMethod.Refused;
                    set.Warning = "Found " + plateNumbers.Count + " figure label(s) and "
                        + flat.Count + " image(s), and they do not pair up"
                        + (plateNumbers.Count == flat.Count
                            ? " in order (the labels are not 1.." + flat.Count + ")."
                            : ".")
                        + " Labels have been left off rather than guessed: mislabelled"
                        + " figures are invisible downstream and corrupt anything built"
                        + " on them.";
                }
                else
                {
                    set.Method = LabelingMethod.Proximity;
                }

                var descriptions = CollectDescriptions(texts);

                int ordinal = 0;
                for (int i = 0; i < n; i++)
                {
                    if (rids[i].Count == 0) continue;

                    string labelSource = null;
                    string label = null;
                    if (set.Method == LabelingMethod.Proximity)
                        label = DetectLabel(texts, rids, isCaption, i, out labelSource);

                    var caption = DetectCaption(texts, isCaption, i);
                    var anchor = CollectAnchor(texts, i);

                    foreach (var rid in rids[i])
                    {
                        ImagePart part;
                        if (!parts.TryGetValue(rid, out part)) continue;   // dangling or external

                        ordinal++;

                        // Ordinal pairing happens here, per image, not per paragraph:
                        // one label per paragraph applied to every image in it once
                        // made four images in one paragraph all "FIG. 3".
                        if (set.Method == LabelingMethod.Ordinal && ordinal - 1 < plateLabels.Count)
                        {
                            label = plateLabels[ordinal - 1];
                            labelSource = "ordinal#" + ordinal;
                        }

                        var img = new ExtractedImage
                        {
                            Ordinal = ordinal,
                            ParagraphIndex = i,
                            Label = label,
                            LabelSource = labelSource,
                            Caption = caption,
                            Anchor = anchor,
                            PartName = part.PartName,
                            ContentType = part.ContentType,
                            Extension = ExtensionFor(part.ContentType),
                        };

                        try
                        {
                            img.SizeBytes = part.Entry.Length;
                            if (includeImageData)
                            {
                                using (var s = part.Entry.Open())
                                using (var ms = new MemoryStream())
                                {
                                    s.CopyTo(ms);
                                    img.Data = ms.ToArray();
                                }
                            }
                        }
                        catch { /* a part we cannot read still has its anchor */ }

                        // What the document says this figure shows, matched on its
                        // number. Proximity cannot find it on a patent.
                        var num = LabelNumber(img.Label);
                        List<string> found;
                        if (num.HasValue && descriptions.TryGetValue(num.Value, out found))
                            img.Descriptions = new List<string>(found);

                        if (!string.IsNullOrEmpty(saveToFolder))
                        {
                            var saved = SaveImagePart(part, img, saveToFolder, flat.Count);
                            if (saved != null) set.SavedFiles.Add(saved);
                        }

                        results.Add(img);
                    }
                }
            }
            catch
            {
                // Not a readable .docx. An empty set says "no images found", which
                // is what the caller can act on.
                return set;
            }
            return set;
        }

        // ---- package reading ---------------------------------------------------------------

        /// <summary>Relationship id → image part, from word/_rels/document.xml.rels and [Content_Types].xml.</summary>
        private static Dictionary<string, ImagePart> ReadImageParts(ZipArchive package)
        {
            var map = new Dictionary<string, ImagePart>(StringComparer.Ordinal);
            var rels = DocxStructure.ReadEntry(package, "word/_rels/document.xml.rels");
            if (rels == null) return map;

            // Content types: Override by part name, else Default by extension.
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ct = DocxStructure.ReadEntry(package, "[Content_Types].xml") ?? "";
            foreach (Match m in Regex.Matches(ct, "<(Default|Override)\\b[^>]*>"))
            {
                var a = Attrs(m.Value);
                string v;
                if (m.Groups[1].Value == "Override" && a.TryGetValue("PartName", out v) && a.ContainsKey("ContentType"))
                    overrides[v] = a["ContentType"];
                else if (m.Groups[1].Value == "Default" && a.TryGetValue("Extension", out v) && a.ContainsKey("ContentType"))
                    defaults[v.TrimStart('.')] = a["ContentType"];
            }

            foreach (Match m in RelationshipTag.Matches(rels))
            {
                var a = Attrs(m.Value);
                string id, type, target, mode;
                if (!a.TryGetValue("Id", out id) || !a.TryGetValue("Type", out type) || !a.TryGetValue("Target", out target)) continue;
                if (!type.EndsWith("/image", StringComparison.OrdinalIgnoreCase)) continue;
                if (a.TryGetValue("TargetMode", out mode) && string.Equals(mode, "External", StringComparison.OrdinalIgnoreCase)) continue;

                var partName = ResolvePartName(DocxStructure.XmlUnescape(target));
                var entry = package.GetEntry(partName.TrimStart('/'));
                if (entry == null) continue;

                string contentType;
                if (!overrides.TryGetValue(partName, out contentType))
                {
                    var ext = Path.GetExtension(partName).TrimStart('.');
                    defaults.TryGetValue(ext, out contentType);
                }
                map[id] = new ImagePart { PartName = partName, ContentType = contentType ?? "", Entry = entry };
            }
            return map;
        }

        private static Dictionary<string, string> Attrs(string tag)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in AttrAny.Matches(tag)) d[m.Groups[1].Value] = m.Groups[2].Value;
            return d;
        }

        /// <summary>A relationship target relative to /word/, resolved to a part name with a leading slash.</summary>
        private static string ResolvePartName(string target)
        {
            var t = target.Replace('\\', '/');
            var segments = new List<string>();
            if (!t.StartsWith("/")) segments.Add("word");
            foreach (var s in t.Split('/'))
            {
                if (s.Length == 0 || s == ".") continue;
                if (s == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); continue; }
                segments.Add(s);
            }
            return "/" + string.Join("/", segments);
        }

        /// <summary>
        /// Image relationship ids referenced by this paragraph itself, in document
        /// order. Nested paragraphs (a text box's own) are not this paragraph's,
        /// which is what the own-spans are for.
        /// </summary>
        private static List<string> ImageRelationshipIds(string xml, DocxParagraph p)
        {
            var ids = new List<string>();
            var spans = p.OwnSpans;
            for (int i = 0; i + 1 < spans.Count; i += 2)
            {
                int start = spans[i], end = spans[i + 1];
                if (end <= start) continue;
                foreach (Match m in ImageRef.Matches(xml.Substring(start, end - start)))
                {
                    var tag = m.Value;
                    string rid = null;
                    if (tag.StartsWith("<a:blip", StringComparison.Ordinal))
                    {
                        var e = AttrEmbed.Match(tag);
                        if (e.Success && e.Groups[1].Value.Length > 0) rid = e.Groups[1].Value;
                        else { var l = AttrLink.Match(tag); if (l.Success && l.Groups[1].Value.Length > 0) rid = l.Groups[1].Value; }
                    }
                    else
                    {
                        var r = AttrId.Match(tag);
                        if (r.Success && r.Groups[1].Value.Length > 0) rid = r.Groups[1].Value;
                    }
                    if (rid != null) ids.Add(rid);
                }
            }
            return ids;
        }

        /// <summary>
        /// Stream one image part to <paramref name="folder"/>, named for the
        /// figure it is. Returns the file name, or null if it could not be written.
        ///
        /// <para>Zero-padded to the width of the set, so a file manager sorts them
        /// in figure order - the reason word/media/image1, image10, image2 is
        /// unusable as-is. Re-running overwrites, so the action is idempotent.</para>
        /// </summary>
        private static string SaveImagePart(ImagePart part, ExtractedImage img, string folder, int totalCount)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var width = Math.Max(2, totalCount.ToString().Length);

                // Number from the label when we have a checked one, else the
                // ordinal - and say which, so a file called Image 03 is never
                // mistaken for a figure number.
                var num = LabelNumber(img.Label);
                var stem = num.HasValue
                    ? "Figure " + num.Value.ToString().PadLeft(width, '0')
                    : "Image " + img.Ordinal.ToString().PadLeft(width, '0');

                var name = stem + (img.Extension ?? ".img");
                var path = Path.Combine(folder, name);
                using (var src = part.Entry.Open())
                using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write))
                    src.CopyTo(dst);
                return name;
            }
            catch { return null; }
        }

        // ---- descriptions ------------------------------------------------------------------

        /// <summary>
        /// Figure number to every sentence in the document that describes it.
        /// Plate labels are excluded - "FIG.8" on its own opens with a figure
        /// reference but describes nothing.
        /// </summary>
        private static Dictionary<int, List<string>> CollectDescriptions(string[] texts)
        {
            var map = new Dictionary<int, List<string>>();
            foreach (var raw in texts)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string dummyLabel; int dummyNum;
                if (TryPlateLabel(raw, out dummyLabel, out dummyNum)) continue;

                foreach (var sentence in SentenceSplit.Split(raw.Trim()))
                {
                    var m = DescriptionOpener.Match(sentence);
                    if (!m.Success) continue;
                    int n;
                    if (!int.TryParse(m.Groups[1].Value, out n)) continue;
                    var text = sentence.Trim();
                    if (text.Length == 0) continue;
                    List<string> list;
                    if (!map.TryGetValue(n, out list)) { list = new List<string>(); map[n] = list; }
                    AddDescription(list, text);
                }
            }
            return map;
        }

        /// <summary>
        /// Add <paramref name="text"/> unless an existing description already
        /// contains it, in which case keep the longer one. A patent states each
        /// figure twice - briefly in the figure list, at length in the detailed
        /// description - often differing only by a trailing full stop, so
        /// containment of the normalised form is the test rather than equality.
        /// </summary>
        private static void AddDescription(List<string> list, string text)
        {
            var key = NormaliseForCompare(text);
            if (key.Length == 0) return;
            for (int i = 0; i < list.Count; i++)
            {
                var existing = NormaliseForCompare(list[i]);
                if (existing.Contains(key))
                {
                    if (key.Length == existing.Length && text.Length > list[i].Length) list[i] = text;
                    return;
                }
                if (key.Contains(existing)) { list[i] = text; return; }
            }
            list.Add(text);
        }

        /// <summary>Whitespace collapsed, trailing punctuation dropped, casefolded.</summary>
        private static string NormaliseForCompare(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var collapsed = Regex.Replace(s.Trim(), @"\s+", " ");
            return collapsed.TrimEnd('.', ',', ';', ':', ' ').ToLowerInvariant();
        }

        /// <summary>The digits in a label, or null when it has none.</summary>
        private static int? LabelNumber(string label)
        {
            if (string.IsNullOrEmpty(label)) return null;
            var m = Regex.Match(label, @"(\d+)");
            if (!m.Success) return null;
            int n;
            return int.TryParse(m.Groups[1].Value, out n) ? (int?)n : null;
        }

        /// <summary>
        /// True when the paragraph's ENTIRE text is a figure label - "FIG. 8",
        /// "Figure 12". That is a plate caption. The same label inside a longer
        /// sentence is a citation, and counting it is what makes a naive scan find
        /// 16 labels for 14 images.
        /// </summary>
        private static bool TryPlateLabel(string text, out string label, out int number)
        {
            label = null; number = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var trimmed = text.Trim();
            var matched = MatchLabel(trimmed);
            if (matched == null) return false;
            if (!string.Equals(matched.Trim(), trimmed, StringComparison.Ordinal)) return false;
            var digits = Regex.Match(trimmed, @"(\d+)");
            if (!digits.Success) return false;
            label = trimmed;
            return int.TryParse(digits.Groups[1].Value, out number);
        }

        // ---- label detection ------------------------------------------------------------------

        /// <summary>
        /// Find a label for the image in paragraph <paramref name="idx"/>.
        ///
        /// <para><b>Position dominates style</b>, and the previous paragraph is
        /// only a candidate when the paragraph before THAT has no image of its
        /// own. Patent layouts run [image][caption][image][caption]..., so the
        /// paragraph immediately before an image is usually the PREVIOUS image's
        /// caption. An earlier version promoted any Caption-styled paragraph above
        /// any pattern match, and so labelled
        /// <code>
        ///   p[N-1]  "FIG. 16"   [Caption-styled, belongs to image N-2]
        ///   p[N]    &lt;image of FIG. 17&gt;
        ///   p[N+1]  "FIG. 17"   [not Caption-styled]
        /// </code>
        /// as FIG. 16. Do not "simplify" this back.</para>
        /// </summary>
        private static string DetectLabel(string[] texts, List<string>[] rids, bool[] isCaption, int idx, out string labelSource)
        {
            labelSource = null;
            var order = new List<int> { idx };
            if (idx + 1 < texts.Length) order.Add(idx + 1);
            var prevIsOurs = idx > 0 && (idx < 2 || rids[idx - 2].Count == 0);
            if (prevIsOurs) order.Add(idx - 1);

            foreach (var i in order)
            {
                var text = texts[i];
                if (string.IsNullOrEmpty(text)) continue;
                var matched = MatchLabel(text);
                if (matched != null) { labelSource = "pattern@" + Offset(i, idx); return matched; }
                if (isCaption[i])
                {
                    var leading = Regex.Split(text, @"[.,:;\n]")[0].Trim();
                    if (leading.Length > 0)
                    {
                        labelSource = "caption-style@" + Offset(i, idx);
                        return leading.Length > 80 ? leading.Substring(0, 80).Trim() : leading;
                    }
                }
            }
            return null;
        }

        private static string Offset(int i, int idx)
        {
            if (i == idx) return "same";
            return i > idx ? "next" : "prev";
        }

        /// <summary>First label pattern that matches, captured verbatim.</summary>
        private static string MatchLabel(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            foreach (var pat in LabelPatterns)
            {
                var m = pat.Match(text);
                if (m.Success) return m.Groups[1].Value.Trim();
            }
            return null;
        }

        /// <summary>The caption paragraph's full text; only Caption-styled paragraphs count.</summary>
        private static string DetectCaption(string[] texts, bool[] isCaption, int idx)
        {
            if (isCaption[idx] && !string.IsNullOrWhiteSpace(texts[idx])) return texts[idx];
            if (idx + 1 < texts.Length && isCaption[idx + 1] && !string.IsNullOrWhiteSpace(texts[idx + 1])) return texts[idx + 1];
            return null;
        }

        /// <summary>The text the image sits among: up to <see cref="AnchorWindow"/> paragraphs either side.</summary>
        private static string CollectAnchor(string[] texts, int idx)
        {
            var start = Math.Max(0, idx - AnchorWindow);
            var end = Math.Min(texts.Length - 1, idx + AnchorWindow);
            var parts = new List<string>();
            for (int i = start; i <= end; i++)
            {
                var t = (texts[i] ?? "").Trim();
                if (t.Length > 0) parts.Add(t);
            }
            return string.Join("\n", parts);
        }

        private static string ExtensionFor(string contentType)
        {
            switch ((contentType ?? "").ToLowerInvariant())
            {
                case "image/png": return ".png";
                case "image/jpeg": return ".jpg";
                case "image/gif": return ".gif";
                case "image/bmp": return ".bmp";
                case "image/tiff": return ".tif";
                case "image/x-emf":
                case "image/emf": return ".emf";
                case "image/x-wmf":
                case "image/wmf": return ".wmf";
                default: return ".img";
            }
        }
    }
}

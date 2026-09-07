using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Supervertaler.Core
{
    /// <summary>
    /// Renders <c>figures.md</c>: the file in a memory bank that tells the model
    /// what each image in a document shows. Two shapes - from the text alone,
    /// and with what a vision model saw - and the diff that is the point of the
    /// second: signs printed in a figure that the text never cites. The host
    /// decides where the file goes and asks before replacing one; this only
    /// writes the words.
    /// </summary>
    public static class FiguresFile
    {
        /// <summary>The heading only the text-only shape carries; a host uses it to tell the two apart.</summary>
        public const string TextOnlyMarker = "## What is not here";

        /// <summary>True when a figures.md was written from the text alone, without the AI pass.</summary>
        public static bool IsTextOnly(string markdown) => markdown != null && markdown.Contains(TextOnlyMarker);

        /// <summary>
        /// The signs a vision model read in the figures that the text never
        /// cites - neither in the reference-number inventory nor anywhere in the
        /// raw text as a whole word. The second gate errs towards suppressing: a
        /// false accusation costs more than a missed hint in a list the file
        /// already tells the reader to verify.
        /// </summary>
        public static List<string> SignsNotInText(IList<FigureVision> visions, ISet<string> textSigns, string rawSourceText)
        {
            var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (visions == null) return new List<string>();
            foreach (var v in visions)
                foreach (var s in v.SignsInDrawing ?? new List<string>())
                {
                    var sign = (s ?? "").Trim();
                    if (sign.Length == 0) continue;
                    if (textSigns != null && textSigns.Contains(sign)) continue;
                    if (AppearsInText(rawSourceText, sign)) continue;
                    seen.Add(sign);
                }
            return seen.ToList();
        }

        private static bool AppearsInText(string rawSourceText, string sign)
        {
            if (string.IsNullOrEmpty(rawSourceText) || string.IsNullOrEmpty(sign)) return false;
            try
            {
                return Regex.IsMatch(rawSourceText, @"(?<![\w])" + Regex.Escape(sign) + @"(?![\w])", RegexOptions.IgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// figures.md with what the AI saw: the diff first, then one row per
        /// figure with what the document says and what the model read.
        /// <paramref name="regenerateHint"/> names the host's own button.
        /// </summary>
        public static string RenderWithVision(string docName, DocxImageSet set, IList<FigureVision> visions,
            IList<string> signsNotInText, string regenerateHint)
        {
            var anyLabelled = set != null && set.Images.Any(i => !string.IsNullOrEmpty(i.Label));
            var sb = new StringBuilder();
            sb.AppendLine("# " + VisualNoun(anyLabelled, true, true));
            sb.AppendLine();
            sb.AppendLine("*Written by Supervertaler on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                + " from " + (string.IsNullOrEmpty(docName) ? "the project" : docName)
                + ", with the images examined by AI."
                + (string.IsNullOrEmpty(regenerateHint) ? "" : " Regenerate from " + regenerateHint + ".") + "*");
            sb.AppendLine();

            // The finding first.
            sb.AppendLine("## Reference signs in the " + VisualNoun(anyLabelled, true, false) + " but not in the text");
            sb.AppendLine();
            if (signsNotInText == null || signsNotInText.Count == 0)
            {
                sb.AppendLine("None. Every sign read in the images also appears in the description.");
            }
            else
            {
                sb.AppendLine("**" + string.Join(", ", signsNotInText) + "**");
                sb.AppendLine();
                sb.AppendLine("A reference sign printed in an image with no basis in the text is a defect in the "
                            + "document. Raise it with the client rather than inventing a description for it.");
                sb.AppendLine();
                sb.AppendLine("*Read off the images by an AI. Check each one against the image before relying on it.*");
            }
            sb.AppendLine();

            sb.AppendLine("## The " + VisualNoun(anyLabelled, true, false));
            sb.AppendLine();
            if (set != null && set.Method == LabelingMethod.Ordinal)
            {
                sb.AppendLine("Image *N* carries figure *N*, checked for all " + set.Images.Count + ".");
                sb.AppendLine();
            }

            var noun = VisualNoun(anyLabelled, false, false);
            sb.AppendLine("| " + VisualNoun(anyLabelled, false, true) + " | File | What the document says | What the " + noun + " shows | Signs on the " + noun + " |");
            sb.AppendLine("|---|---|---|---|---|");
            for (int i = 0; i < (visions?.Count ?? 0); i++)
            {
                var v = visions[i];
                var img = (set != null && i < set.Images.Count) ? set.Images[i] : null;
                var said = "";
                if (img != null && img.Descriptions != null && img.Descriptions.Count > 0) said = string.Join(" ", img.Descriptions);
                if (said.Length == 0) said = "—";
                var saw = !string.IsNullOrEmpty(v.Error) ? "*not analysed: " + v.Error + "*"
                        : (string.IsNullOrWhiteSpace(v.Caption) ? "—" : v.Caption);
                var signs = v.SignsInDrawing != null && v.SignsInDrawing.Count > 0 ? string.Join(", ", v.SignsInDrawing) : "—";
                sb.AppendLine("| " + Cell(v.Label) + " | " + Cell(v.FileName) + " | " + Cell(said) + " | " + Cell(saw) + " | " + Cell(signs) + " |");
            }
            sb.AppendLine();

            var failed = visions?.Count(x => !string.IsNullOrEmpty(x.Error)) ?? 0;
            if (failed > 0)
            {
                sb.AppendLine("*" + failed + " figure(s) could not be analysed; their rows say why. They are listed rather than dropped, so the gap is visible.*");
                sb.AppendLine();
            }

            sb.AppendLine("## How to read this");
            sb.AppendLine();
            sb.AppendLine("\"What the document says\" is quoted from the source text and is exact. "
                        + "\"What the " + noun + " shows\" and \"Signs on the " + noun + "\" were produced by "
                        + "an AI looking at the image, and can be wrong. This file is read into every "
                        + "prompt, so correct anything that is wrong here rather than leaving it: a "
                        + "mistaken caption would otherwise be repeated into every request silently.");
            return sb.ToString();
        }

        /// <summary>
        /// figures.md from the documents' own text, without looking at the
        /// images: one section per document, one row per image. Null when no
        /// document had an image; <paramref name="wrote"/> and <paramref name="refused"/>
        /// count rows and documents whose labels were withheld.
        /// </summary>
        public static string RenderFromText(IList<KeyValuePair<string, DocxImageSet>> documents, string regenerateHint,
            out int wrote, out int refused)
        {
            wrote = 0; refused = 0;
            var anyLabelled = documents != null && documents.Any(d => d.Value != null && d.Value.Images.Any(i => !string.IsNullOrEmpty(i.Label)));
            var sb = new StringBuilder();
            sb.AppendLine("# " + VisualNoun(anyLabelled, true, true));
            sb.AppendLine();
            sb.AppendLine("*Written by Supervertaler on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " from the documents' own text, without looking at the images."
                + (string.IsNullOrEmpty(regenerateHint) ? "" : " Regenerate from " + regenerateHint + ".") + "*");
            sb.AppendLine();

            foreach (var d in documents ?? new List<KeyValuePair<string, DocxImageSet>>())
            {
                var set = d.Value;
                if (set == null || set.Images.Count == 0) continue;
                sb.AppendLine("## " + d.Key);
                sb.AppendLine();
                if (set.Method == LabelingMethod.Refused)
                {
                    refused++;
                    sb.AppendLine("**Figure labels could not be established.** " + set.Warning);
                    sb.AppendLine();
                    sb.AppendLine("The images are listed in document order, unlabelled. Do not assume image *N* is figure *N* here.");
                    sb.AppendLine();
                }
                else if (set.Method == LabelingMethod.Ordinal)
                {
                    sb.AppendLine("Image *N* carries figure *N*, checked for all " + set.Images.Count + ".");
                    sb.AppendLine();
                }
                sb.AppendLine("| " + VisualNoun(anyLabelled, false, true) + " | Source part | What the document says it shows |");
                sb.AppendLine("|---|---|---|");
                foreach (var img in set.Images)
                {
                    // Not truncated: this file is read by the model and by a Markdown
                    // previewer, and both want the whole sentence.
                    var desc = "";
                    if (img.Descriptions != null && img.Descriptions.Count > 0) desc = string.Join(" ", img.Descriptions);
                    if (string.IsNullOrWhiteSpace(desc)) desc = "—";
                    desc = desc.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
                    var part = (img.PartName ?? "").Replace("/word/", "").Replace("|", "\\|");
                    sb.AppendLine("| " + (img.Label ?? ("image " + img.Ordinal)) + " | " + part + " | " + desc + " |");
                    wrote++;
                }
                sb.AppendLine();
            }
            if (wrote == 0) return null;

            sb.AppendLine(TextOnlyMarker);
            sb.AppendLine();
            sb.AppendLine("What each image **actually shows** — the parts visible in it, and any reference sign "
                        + "printed in it but absent from the text — is not in this file. That needs the AI to look "
                        + "at the images. Everything above comes from the documents' own text.");
            return sb.ToString();
        }

        /// <summary>
        /// Writes the file with CRLF throughout and no BOM: the text it wraps
        /// arrives with bare LF, and a mixed file makes Markdown editors complain.
        /// </summary>
        public static void Save(string path, string markdown)
        {
            var text = (markdown ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// <summary>Table-cell safe: pipes escaped, newlines flattened.</summary>
        internal static string Cell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "—";
            return s.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
        }

        /// <summary>
        /// "Figures" or "Images", by what was actually identified. A document
        /// whose pictures carry no figure labels should not be handed a file
        /// headed "Figures" listing "Image 03".
        /// </summary>
        internal static string VisualNoun(bool anyLabelled, bool plural, bool capital)
        {
            var word = anyLabelled ? (plural ? "figures" : "figure") : (plural ? "images" : "image");
            return capital ? char.ToUpperInvariant(word[0]) + word.Substring(1) : word;
        }
    }
}

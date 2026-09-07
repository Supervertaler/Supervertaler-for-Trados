using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Supervertaler.Core.Models;

namespace Supervertaler.Core
{
    /// <summary>What a model saw in one drawing.</summary>
    public class FigureVision
    {
        /// <summary>The figure this describes, e.g. "FIG. 8".</summary>
        public string Label { get; set; }

        /// <summary>File the model was shown.</summary>
        public string FileName { get; set; }

        /// <summary>One or two sentences: what the drawing shows.</summary>
        public string Caption { get; set; }

        /// <summary>Every reference sign legible IN the drawing - numerals,
        /// lettered points, label-series signs. This is the half no amount of
        /// text parsing can reach, and the reason the pass exists.</summary>
        public List<string> SignsInDrawing { get; set; } = new List<string>();

        /// <summary>Set when the call failed; the figure is reported, not
        /// silently dropped.</summary>
        public string Error { get; set; }
    }

    /// <summary>
    /// Sends each drawing to a vision model with the description the document
    /// gives it, and asks two questions: what does this show, and which
    /// reference signs are printed on it.
    ///
    /// <para>The second question is the point. On one real job, <c>ST 05</c>
    /// appears in Figures 13 and 14 and in no segment of the description - an
    /// Art. 84 / Rule 42 objection waiting to happen, and one a human found only
    /// by opening the drawings. It is baked into the bitmap: no parser will ever
    /// find it.</para>
    ///
    /// <para>One call per figure rather than one call carrying all of them. Each
    /// figure gets its own description as context, a failure is isolated to the
    /// figure that caused it, and progress is reportable. The cost is the same
    /// either way - the images dominate.</para>
    ///
    /// <para>Nothing here writes to a prompt. The output is a manifest for a
    /// human to read and correct first, because an occasionally-wrong
    /// description injected into every request is the worst failure available in
    /// this feature.</para>
    /// </summary>
    public static class FigureAnalyzer
    {
        /// <summary>
        /// Deliberately narrow. The model is asked to transcribe what is legible,
        /// not to interpret the mechanism, because a plausible invention summary
        /// is exactly the kind of wrong that survives review.
        /// </summary>
        private const string SystemPrompt =
            "You are examining a single image from a document that is being translated - a figure, drawing, diagram or photo.\n" +
            "\n" +
            "Answer with JSON only, in this exact shape:\n" +
            "{\"caption\": \"...\", \"signs\": [\"...\", \"...\"]}\n" +
            "\n" +
            "caption: one or two plain sentences describing what the image shows. " +
            "Describe only what is visible. Do not explain how anything works, " +
            "do not speculate about purpose, and do not repeat the supplied description " +
            "back verbatim.\n" +
            "\n" +
            "signs: every reference sign you can actually READ in the image - part " +
            "numerals such as 8 or 15, lettered points such as A or H, and label-series " +
            "signs such as ST 01. Transcribe them exactly as printed. Include a sign even " +
            "if it does not appear in the supplied description; that mismatch is the " +
            "reason for this task. If you cannot read a sign clearly, leave it out rather " +
            "than guessing. An empty list is a valid answer.";

        /// <summary>
        /// Analyse one figure. Returns a result carrying <see cref="FigureVision.Error"/>
        /// rather than throwing, so one unreadable drawing does not lose the rest.
        /// </summary>
        public static async Task<FigureVision> AnalyseAsync(
            LlmClient client,
            string imagePath,
            string label,
            IEnumerable<string> descriptions,
            CancellationToken cancellationToken = default)
        {
            var result = new FigureVision
            {
                Label = label,
                FileName = Path.GetFileName(imagePath ?? ""),
            };

            try
            {
                byte[] bytes;
                try { bytes = File.ReadAllBytes(imagePath); }
                catch (Exception ex) { result.Error = "could not read the file: " + ex.Message; return result; }

                var mime = MimeFor(imagePath);
                if (mime == null)
                {
                    // EMF and WMF are common in patents and no vision API takes
                    // them. Say so rather than sending bytes that will be refused.
                    result.Error = "not a format a vision model accepts ("
                                 + Path.GetExtension(imagePath) + "). "
                                 + "Convert it to PNG or JPEG first.";
                    return result;
                }

                var said = (descriptions ?? Enumerable.Empty<string>())
                    .Where(d => !string.IsNullOrWhiteSpace(d)).ToList();

                var sb = new StringBuilder();
                sb.AppendLine("Figure: " + (label ?? result.FileName));
                sb.AppendLine();
                if (said.Count > 0)
                {
                    sb.AppendLine("The document describes it as:");
                    foreach (var d in said) sb.AppendLine("  " + d);
                }
                else
                {
                    sb.AppendLine("The document gives no description of this figure.");
                }

                var message = new ChatMessage
                {
                    Role = ChatRole.User,
                    Content = sb.ToString().TrimEnd(),
                    Images = new List<ImageAttachment>
                    {
                        new ImageAttachment
                        {
                            Data = bytes,
                            MimeType = mime,
                            FileName = result.FileName,
                        }
                    }
                };

                var reply = await client.SendChatAsync(
                    new List<ChatMessage> { message },
                    SystemPrompt,
                    cancellationToken: cancellationToken,
                    feature: PromptLogFeature.FigureAnalysis,
                    promptName: "Figure analysis").ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(reply))
                {
                    result.Error = "the model returned nothing";
                    return result;
                }

                ParseInto(result, reply);
                if (string.IsNullOrWhiteSpace(result.Caption) && result.SignsInDrawing.Count == 0)
                    result.Error = "could not read the model's answer: " + Trim(reply, 200);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Pull caption and signs out of the reply.
        ///
        /// <para>Hand-parsed rather than deserialised: models wrap JSON in prose
        /// or fences often enough that a strict parse fails on answers that are
        /// perfectly usable, and failing there would throw away a whole figure.</para>
        /// </summary>
        internal static void ParseInto(FigureVision result, string reply)
        {
            var text = reply.Trim();

            // Strip a ```json fence if there is one.
            var fence = Regex.Match(text, @"```(?:json)?\s*(\{.*?\})\s*```", RegexOptions.Singleline);
            if (fence.Success) text = fence.Groups[1].Value;

            var caption = Regex.Match(text,
                @"""caption""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Singleline);
            if (caption.Success)
                result.Caption = Unescape(caption.Groups[1].Value).Trim();

            var signs = Regex.Match(text, @"""signs""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
            if (signs.Success)
            {
                foreach (Match m in Regex.Matches(signs.Groups[1].Value,
                             @"""((?:[^""\\]|\\.)*)"""))
                {
                    var sign = Unescape(m.Groups[1].Value).Trim();
                    if (sign.Length > 0 && !result.SignsInDrawing.Contains(sign))
                        result.SignsInDrawing.Add(sign);
                }
            }

            // No JSON at all: keep the prose rather than losing the figure.
            if (string.IsNullOrWhiteSpace(result.Caption) && !signs.Success && text.Length > 0
                && !text.TrimStart().StartsWith("{"))
                result.Caption = Trim(text, 400);
        }

        private static string Unescape(string s)
        {
            return (s ?? "")
                .Replace("\\\"", "\"")
                .Replace("\\n", " ")
                .Replace("\\r", " ")
                .Replace("\\t", " ")
                .Replace("\\\\", "\\");
        }

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s.Trim(), @"\s+", " ");
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        /// <summary>
        /// The MIME type, or null when no vision API will take it. EMF and WMF
        /// are deliberately null: patent drawings are often vector, and a clear
        /// "convert this first" beats an opaque provider error.
        /// </summary>
        private static string MimeFor(string path)
        {
            switch ((Path.GetExtension(path) ?? "").ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                default: return null;
            }
        }
    }
}

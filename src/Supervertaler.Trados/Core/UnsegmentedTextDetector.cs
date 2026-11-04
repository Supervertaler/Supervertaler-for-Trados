using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// One stretch of source text that sits in an SDLXLIFF outside every segment:
    /// never shown in the editor grid, skipped by QA and the word count, and
    /// exported as it is - i.e. untranslated.
    /// </summary>
    public sealed class UnsegmentedHit
    {
        /// <summary>Full path of the .sdlxliff the hit was found in.</summary>
        public string FilePath;

        /// <summary>The paragraph (trans-unit) the text sits in.</summary>
        public string ParagraphUnitId;

        /// <summary>The text, whitespace collapsed, at most <see cref="UnsegmentedTextDetector.MaxTextChars"/> characters.</summary>
        public string Text;

        /// <summary>
        /// True when the paragraph has no segments at all (no &lt;seg-source&gt;, or one
        /// with no segment in it); false for text left between or beside segments.
        /// Such paragraphs are kept apart, in <see cref="UnsegmentedFileResult.Paragraphs"/>:
        /// see there for why.
        /// </summary>
        public bool ParagraphNotSegmented;

        /// <summary>The nearest segment before the text in file order (its mid), or null at the start of the file.</summary>
        public string SegmentBefore;
        public string SegmentBeforeParagraphUnitId;

        /// <summary>The nearest segment after the text, or null at the end of the file.</summary>
        public string SegmentAfter;
        public string SegmentAfterParagraphUnitId;

        /// <summary>
        /// Where to send the editor: the segment before, else the one after. Both
        /// null only for a file with no segments at all.
        /// </summary>
        public string JumpSegmentId => SegmentBefore ?? SegmentAfter;
        public string JumpParagraphUnitId => SegmentBefore != null ? SegmentBeforeParagraphUnitId : SegmentAfterParagraphUnitId;

        /// <summary>"between segments 155 and 156", "after segment 842", ...</summary>
        public string Location
        {
            get
            {
                if (SegmentBefore != null && SegmentAfter != null)
                    return "between segments " + SegmentBefore + " and " + SegmentAfter;
                if (SegmentBefore != null) return "after segment " + SegmentBefore;
                if (SegmentAfter != null) return "before segment " + SegmentAfter;
                return "in a file with no segments";
            }
        }
    }

    /// <summary>The outcome of checking one .sdlxliff.</summary>
    public sealed class UnsegmentedFileResult
    {
        public string FilePath;

        /// <summary>
        /// The file's size and write time when the check began. A result is only
        /// reused while both still match; any save makes the file look new.
        /// </summary>
        public long Length;
        public DateTime LastWriteUtc;

        /// <summary>False when the file could not be read to the end (locked, half
        /// written, malformed). Such a file reports no hits, only <see cref="Error"/>.</summary>
        public bool Ok;
        public string Error;

        /// <summary>Segments in the file. A file with none has not been prepared and
        /// reports nothing.</summary>
        public int Segments;

        /// <summary>
        /// Stretches of text left between or beside segments, inside a paragraph
        /// that has segments - the case that ships untranslated by accident, and
        /// the only one the warning counts. Includes those beyond <see cref="Hits"/>.
        /// </summary>
        public int HitCount;

        /// <summary>The first <see cref="UnsegmentedTextDetector.MaxHitsPerFile"/> of them, in file order.</summary>
        public List<UnsegmentedHit> Hits = new List<UnsegmentedHit>();

        /// <summary>
        /// Paragraphs with text but no segment, in a file that has segments. Kept
        /// apart from <see cref="HitCount"/> because they are usually deliberate: a
        /// survey of 2,700 real files found them only in files made from another
        /// tool's bilingual format (memoQ, XLIFF, TMX, DITA, Illustrator XML), where
        /// they are the rows that tool locked, hid or left out - often hundreds per
        /// file. Text between segments was found in four native Word and InDesign
        /// files, every one a real miss. Only check_unsegmented_text lists these.
        /// </summary>
        public int ParagraphCount;

        /// <summary>The first <see cref="UnsegmentedTextDetector.MaxHitsPerFile"/> of them, in file order.</summary>
        public List<UnsegmentedHit> Paragraphs = new List<UnsegmentedHit>();

        public bool IsCurrent(long length, DateTime lastWriteUtc) =>
            length == Length && lastWriteUtc == LastWriteUtc;

        internal void ClearFindings()
        {
            HitCount = 0;
            Hits.Clear();
            ParagraphCount = 0;
            Paragraphs.Clear();
        }
    }

    /// <summary>
    /// Finds source text in an SDLXLIFF that is outside every segment
    /// (<c>&lt;mrk mtype="seg"&gt;</c>). Trados can leave text like that behind -
    /// seen when several Word comments ended exactly at a sentence boundary: the
    /// sentence sat between two segments, invisible in the editor, and shipped
    /// untranslated in the exported file.
    ///
    /// <para>Reads the file on disk, i.e. the last saved state, with a streaming
    /// <see cref="XmlReader"/>: an SDLXLIFF can be 50 MB and more, and memory use
    /// here does not grow with it. Segments are not assumed to be direct children
    /// of &lt;seg-source&gt;: comment markers (<c>x-sdl-comment</c>), formatting and
    /// other markers can wrap them, so the walk descends into everything and only
    /// tracks whether it is inside a segment. Text counts when it contains a run of
    /// three or more letters (any script); whitespace, punctuation, digits, tags
    /// and markers between segments never do.</para>
    ///
    /// <para>Pure: no logging, no Studio. <see cref="UnsegmentedTextMonitor"/> runs it
    /// in the background and reports.</para>
    /// </summary>
    public static class UnsegmentedTextDetector
    {
        /// <summary>Hits kept per file, of each kind. More are counted, not kept: a
        /// file whose segmentation failed wholesale would otherwise hold every paragraph.</summary>
        public const int MaxHitsPerFile = 200;

        /// <summary>Characters of text kept per hit.</summary>
        public const int MaxTextChars = 300;

        private const int MinLetterRun = 3;

        /// <summary>
        /// Checks one .sdlxliff on disk. Never throws except on cancellation: a file
        /// that cannot be opened or parsed to the end comes back with
        /// <see cref="UnsegmentedFileResult.Ok"/> false and the reason in
        /// <see cref="UnsegmentedFileResult.Error"/>, and no hits. The file is opened
        /// with full sharing, so Studio can save it while it is being read.
        /// </summary>
        public static UnsegmentedFileResult CheckFile(string path, CancellationToken cancel = default(CancellationToken))
        {
            var result = new UnsegmentedFileResult { FilePath = path };
            try
            {
                var info = new FileInfo(path);
                result.Length = info.Length;
                result.LastWriteUtc = info.LastWriteTimeUtc;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
                {
                    Check(stream, path, result, cancel);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Locked, half written, malformed, gone. Whatever was found before
                // the failure is not trustworthy as a complete list, and a warning
                // built on a partial read would be noise: report nothing.
                result.Ok = false;
                result.Error = ex.GetType().Name + ": " + ex.Message;
                result.ClearFindings();
            }
            return result;
        }

        /// <summary>Checks SDLXLIFF content from a stream. Throws on malformed XML.</summary>
        public static UnsegmentedFileResult Check(Stream stream, string path,
            CancellationToken cancel = default(CancellationToken))
        {
            var result = new UnsegmentedFileResult { FilePath = path };
            Check(stream, path, result, cancel);
            return result;
        }

        private static void Check(Stream stream, string path, UnsegmentedFileResult result, CancellationToken cancel)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                // Studio writes characters XML 1.0 does not allow (control
                // characters from Word); a strict reader would reject whole files.
                CheckCharacters = false,
                CloseInput = false
            };
            using (var reader = XmlReader.Create(stream, settings))
            {
                new Walker(reader, path, result, cancel).Run();
            }
            result.Ok = true;
        }

        /// <summary>
        /// True when <paramref name="text"/> has a run of at least three letters.
        /// Combining marks continue a run without counting, so a word in Hindi or
        /// Thai is one run, as it reads.
        /// </summary>
        public static bool HasLetterRun(string text)
        {
            var run = new LetterRun();
            for (int i = 0; i < (text?.Length ?? 0); i++)
                if (run.Feed(text, ref i)) return true;
            return false;
        }

        /// <summary>Letter-run state that survives across text nodes, so "Hee&lt;g&gt;ft&lt;/g&gt;" is one word.</summary>
        private struct LetterRun
        {
            private int _run;

            /// <summary>Feeds the character at <paramref name="i"/> (advancing past a
            /// surrogate pair); true once the run reaches the minimum.</summary>
            public bool Feed(string s, ref int i)
            {
                var c = s[i];
                bool letter;
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    letter = char.IsLetter(s, i);
                    i++;
                }
                else
                {
                    letter = char.IsLetter(c);
                    if (!letter && _run > 0)
                    {
                        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
                        if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark
                            || cat == UnicodeCategory.EnclosingMark)
                            return false;   // continues the run, does not lengthen it
                    }
                }
                _run = letter ? _run + 1 : 0;
                return _run >= MinLetterRun;
            }
        }

        /// <summary>
        /// The walk. One instance per file. State is only what the next decision
        /// needs: where in the unit the reader is, the stretch being collected,
        /// the last segment seen, and the hits still waiting for the next one.
        /// </summary>
        private sealed class Walker
        {
            private readonly XmlReader _r;
            private readonly string _path;
            private readonly UnsegmentedFileResult _result;
            private readonly CancellationToken _cancel;

            private enum Zone { None, Source, SegSource }

            private bool _inUnit;
            private string _unitId;
            private Zone _zone;
            private int _zoneDepth;
            private int _segDepth = -1;          // depth of the open segment mrk, -1 outside one
            private int _segmentsInUnit;
            private int _segmentsInFile;
            private bool _unitHasSegSource;
            private UnsegmentedHit _sourceCandidate; // text of a <source> with no <seg-source> (yet)

            private readonly StringBuilder _text = new StringBuilder();
            private bool _textTruncated;
            private bool _lastWasSpace = true;
            private LetterRun _run;
            private bool _stretchHasRun;

            private string _lastSeg, _lastSegUnit;
            private readonly List<UnsegmentedHit> _awaitingNext = new List<UnsegmentedHit>();

            public Walker(XmlReader reader, string path, UnsegmentedFileResult result, CancellationToken cancel)
            {
                _r = reader; _path = path; _result = result; _cancel = cancel;
            }

            public void Run()
            {
                _r.MoveToContent();
                while (!_r.EOF)
                {
                    switch (_r.NodeType)
                    {
                        case XmlNodeType.Element:
                            if (OnElement()) continue;   // already advanced (Skip)
                            break;
                        case XmlNodeType.Text:
                        case XmlNodeType.CDATA:
                        case XmlNodeType.Whitespace:
                        case XmlNodeType.SignificantWhitespace:
                            if (_zone != Zone.None && _segDepth < 0) Collect(_r.Value);
                            break;
                        case XmlNodeType.EndElement:
                            OnEndElement();
                            break;
                    }
                    _r.Read();
                }
                // A truncated file ends the reader early only by throwing, so getting
                // here means the document was complete.
                _result.Segments = _segmentsInFile;

                // No segment anywhere: a file that was never prepared (the source-
                // language copy, say), not a file with text missed by segmentation.
                if (_segmentsInFile == 0) _result.ClearFindings();
            }

            /// <summary>Returns true when it moved the reader itself.</summary>
            private bool OnElement()
            {
                var name = _r.LocalName;
                bool empty = _r.IsEmptyElement;

                if (!_inUnit)
                {
                    // The header holds the embedded original (base64, often most of
                    // the file) and tag definitions: nothing to check, so pass it by
                    // without materialising it.
                    if (name == "header") { _r.Skip(); return true; }
                    if (name == "trans-unit")
                    {
                        BeginUnit(_r.GetAttribute("id"));
                        if (empty) EndUnit();
                    }
                    return false;
                }

                if (_zone == Zone.None)
                {
                    if (name == "source" || name == "seg-source")
                    {
                        _zone = name == "source" ? Zone.Source : Zone.SegSource;
                        if (_zone == Zone.SegSource)
                        {
                            _unitHasSegSource = true;
                            _sourceCandidate = null;   // the segmented copy decides
                        }
                        _zoneDepth = _r.Depth;
                        StartStretch();
                        if (empty) EndZone();
                        return false;
                    }
                    // <target>, <sdl:seg-defs>, <alt-trans>, notes: not source text.
                    _r.Skip();
                    return true;
                }

                // Inside <source> or <seg-source>.
                if (name == "mrk")
                {
                    var mtype = _r.GetAttribute("mtype");
                    if (_zone == Zone.SegSource && _segDepth < 0 && mtype == "seg")
                    {
                        EndStretch(atZoneEnd: false);
                        OnSegment(_r.GetAttribute("mid"));
                        if (empty) StartStretch();
                        else _segDepth = _r.Depth;
                        return false;
                    }
                    // Deleted text (source-side tracked changes) does not export.
                    if (mtype == "x-sdl-deleted") { _r.Skip(); return true; }
                    return false;   // comments, added text, locations: descend
                }
                // XLIFF native-code containers hold markup, not text.
                if (name == "ph" || name == "bpt" || name == "ept" || name == "it")
                {
                    _r.Skip();
                    return true;
                }
                return false;   // <g>, <x/>, <bx/>, <ex/>, ...: descend / nothing
            }

            private void OnEndElement()
            {
                if (_zone != Zone.None)
                {
                    if (_segDepth >= 0)
                    {
                        if (_r.Depth == _segDepth)
                        {
                            _segDepth = -1;
                            StartStretch();
                        }
                        return;
                    }
                    if (_r.Depth == _zoneDepth)
                    {
                        EndStretch(atZoneEnd: true);
                        EndZone();
                    }
                    return;
                }
                if (_inUnit && _r.LocalName == "trans-unit") EndUnit();
            }

            private void BeginUnit(string id)
            {
                _inUnit = true;
                _unitId = id ?? "";
                _zone = Zone.None;
                _segDepth = -1;
                _segmentsInUnit = 0;
                _unitHasSegSource = false;
                _sourceCandidate = null;
            }

            private void EndUnit()
            {
                // A paragraph Trados never segmented: its text only exists in <source>.
                if (!_unitHasSegSource && _sourceCandidate != null)
                    AddHit(_sourceCandidate);
                _inUnit = false;
                _sourceCandidate = null;
                _cancel.ThrowIfCancellationRequested();
            }

            private void EndZone()
            {
                _zone = Zone.None;
                _segDepth = -1;
            }

            private void OnSegment(string mid)
            {
                mid = mid ?? "";
                _segmentsInUnit++;
                _segmentsInFile++;
                foreach (var hit in _awaitingNext)
                {
                    hit.SegmentAfter = mid;
                    hit.SegmentAfterParagraphUnitId = _unitId;
                }
                _awaitingNext.Clear();
                _lastSeg = mid;
                _lastSegUnit = _unitId;
            }

            private void StartStretch()
            {
                _text.Clear();
                _textTruncated = false;
                _lastWasSpace = true;
                _run = default(LetterRun);
                _stretchHasRun = false;
            }

            private void Collect(string s)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    int start = i;
                    if (_run.Feed(s, ref i)) _stretchHasRun = true;

                    if (_text.Length >= MaxTextChars) { _textTruncated = true; continue; }
                    var c = s[start];
                    if (char.IsWhiteSpace(c))
                    {
                        if (!_lastWasSpace) _text.Append(' ');
                        _lastWasSpace = true;
                    }
                    else
                    {
                        _text.Append(s, start, i - start + 1);
                        _lastWasSpace = false;
                    }
                }
            }

            private void EndStretch(bool atZoneEnd)
            {
                if (!_stretchHasRun) return;
                var text = _text.ToString().Trim();
                if (_textTruncated) text += "…";
                var hit = new UnsegmentedHit
                {
                    FilePath = _path,
                    ParagraphUnitId = _unitId,
                    Text = text,
                    ParagraphNotSegmented = atZoneEnd && _segmentsInUnit == 0,
                };
                if (_zone == Zone.Source)
                {
                    // Only a hit if no <seg-source> follows in this unit.
                    hit.ParagraphNotSegmented = true;
                    _sourceCandidate = hit;
                    return;
                }
                AddHit(hit);
            }

            private void AddHit(UnsegmentedHit hit)
            {
                List<UnsegmentedHit> list;
                if (hit.ParagraphNotSegmented) { _result.ParagraphCount++; list = _result.Paragraphs; }
                else { _result.HitCount++; list = _result.Hits; }
                if (list.Count >= MaxHitsPerFile) return;
                hit.SegmentBefore = _lastSeg;
                hit.SegmentBeforeParagraphUnitId = _lastSegUnit;
                list.Add(hit);
                _awaitingNext.Add(hit);
            }
        }
    }
}

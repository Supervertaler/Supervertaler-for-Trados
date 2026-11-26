using System;
using System.IO;
using System.Xml;

namespace Supervertaler.Trados.Core.MtMarks
{
    /// <summary>
    /// Whether an .sdlxliff holds anything Remove MT marks would change, read
    /// straight from the file without Studio's file-type pipeline.
    ///
    /// <para><b>Why.</b> Studio rewrites every file a batch task processes in
    /// its own serialisation, even when nothing in it changed: self-closing
    /// tags lose their space, namespace declarations move, the embedded
    /// original file is renamed (measured 24 Nov 2026 with Studio 2026's own
    /// reader and writer). The task skips a file this says needs nothing, so a
    /// file without MT marks stays byte for byte as it was.</para>
    ///
    /// <para>It applies the same test as <see cref="MtOriginRule.Clean"/>: an
    /// unlocked segment whose <c>sdl:seg</c> origin, or any <c>sdl:prev-origin</c>
    /// inside it, is MT. It streams, holding one element at a time, so a
    /// 100,000-segment file costs one pass of reading and no more memory than a
    /// small one. A file that cannot be read this way is reported as needing
    /// the task, so that Studio opens it and reports its own error rather than
    /// the file being skipped without a word.</para>
    /// </summary>
    internal static class MtMarksScan
    {
        private const string SdlNamespace = "http://sdl.com/FileTypes/SdlXliff/1.0";

        internal static bool NeedsCleaning(string sdlxliffPath)
        {
            try
            {
                using (var stream = new FileStream(sdlxliffPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return NeedsCleaning(stream);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is XmlException
                                        || ex is ArgumentException || ex is NotSupportedException)
            {
                DiagnosticLog.Log("RemoveMtMarks", "could not read " + Path.GetFileName(sdlxliffPath ?? "")
                    + " before cleaning (" + ex.Message + "); Studio processes it anyway");
                return true;
            }
        }

        /// <summary>The same, on a stream. Throws <see cref="XmlException"/> for a file that is not XML.</summary>
        internal static bool NeedsCleaning(Stream sdlxliff)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            };
            using (var reader = XmlReader.Create(sdlxliff, settings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "seg"
                        || reader.NamespaceURI != SdlNamespace)
                        continue;

                    bool locked = string.Equals(reader.GetAttribute("locked"), "true", StringComparison.OrdinalIgnoreCase);
                    bool mt = MtOriginRule.IsMt(reader.GetAttribute("origin"));
                    if (!mt && !reader.IsEmptyElement)
                    {
                        using (var seg = reader.ReadSubtree())
                        {
                            while (seg.Read())
                            {
                                if (seg.NodeType == XmlNodeType.Element && seg.LocalName == "prev-origin"
                                    && seg.NamespaceURI == SdlNamespace && MtOriginRule.IsMt(seg.GetAttribute("origin")))
                                {
                                    mt = true;
                                    break;
                                }
                            }
                        }
                    }
                    if (mt && !locked)
                        return true;
                }
            }
            return false;
        }
    }
}

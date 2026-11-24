using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.FileTypeSupport.Framework.Core.Utilities.BilingualApi;
using Sdl.FileTypeSupport.Framework.IntegrationApi;
using Sdl.ProjectAutomation.AutomaticTasks;
using Sdl.ProjectAutomation.Core;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Core.MtMarks;

namespace Supervertaler.Trados.BatchTasks
{
    /// <summary>
    /// "Supervertaler: Remove MT marks", a Trados batch task: takes the
    /// machine-translation origin off the segments of the chosen files, so
    /// Trados no longer shows AT (or a faded AT) on them. The rule is
    /// <see cref="MtOriginRule"/>; this only hands Studio a processor for each
    /// file and reports what happened.
    ///
    /// <para><b>A batch task, not a setting</b> (design note, 23 Nov 2026):
    /// removing the marks is a one-off decision about one delivery, and some
    /// clients require MT use to be declared, so it is never automatic and has
    /// no options. The description, which Studio shows in the Batch Tasks list,
    /// says so.</para>
    ///
    /// <para>Studio streams each file through the processor paragraph by
    /// paragraph and writes the file itself, so a large file costs no more
    /// memory than a small one. A file with nothing to remove is skipped
    /// (<see cref="ShouldProcessFile"/>), so it stays byte for byte as it was:
    /// Studio would otherwise rewrite it in its own formatting.</para>
    ///
    /// <para><b>The manifest.</b> Studio registers the task from
    /// Supervertaler.Trados.plugin.xml, not from these attributes; the two must
    /// agree (tools\remove_mt_marks_test.ps1 checks it).</para>
    /// </summary>
    [AutomaticTask(TaskId, TaskName, TaskDescription, GeneratedFileType = AutomaticTaskFileType.BilingualTarget)]
    [AutomaticTaskSupportedFileType(AutomaticTaskFileType.BilingualTarget)]
    public sealed class RemoveMtMarksTask : AbstractFileContentProcessingAutomaticTask
    {
        internal const string TaskId = "Supervertaler_RemoveMtMarks";
        internal const string TaskName = "Supervertaler: Remove MT marks";
        /// <summary>The report's name: Studio turns it into a file name, so no colon (or other character Windows refuses).</summary>
        internal const string ReportName = "Supervertaler - Remove MT marks";
        internal const string TaskDescription =
            "Removes the machine translation mark (AT, or AI for an AI engine, and the faded mark on segments edited afterwards) from the "
            + "selected files, so the segments read as your own work. It cannot be undone. Before you run it, "
            + "check that your client does not require you to declare machine translation.";

        // Studio may call the per-file methods from its own worker threads.
        private readonly object _gate = new object();
        private readonly List<FileResult> _files = new List<FileResult>();

        public override bool ShouldProcessFile(ProjectFile projectFile)
        {
            bool needs = MtMarksScan.NeedsCleaning(projectFile.LocalFilePath);
            if (!needs)
                Add(new FileResult(projectFile) { NothingToRemove = true });
            return needs;
        }

        protected override void ConfigureConverter(ProjectFile projectFile, IMultiFileConverter multiFileConverter)
        {
            var result = new FileResult(projectFile);
            Add(result);
            multiFileConverter.AddBilingualProcessor(
                new BilingualContentHandlerAdapter(new RemoveMtMarksProcessor(result.Count)));
        }

        /// <summary>Studio calls this once a file has been processed and written.
        /// A file that failed part-way never gets here, and the report says it
        /// was not finished rather than counting it as cleaned.</summary>
        public override bool OnFileComplete(ProjectFile projectFile, IMultiFileConverter multiFileConverter)
        {
            var path = projectFile?.LocalFilePath;
            lock (_gate)
                foreach (var f in _files)
                    if (!f.NothingToRemove && string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))
                        f.Finished = true;
            return base.OnFileComplete(projectFile, multiFileConverter);
        }

        public override void TaskComplete()
        {
            base.TaskComplete();
            List<FileResult> files;
            lock (_gate) files = _files.ToList();

            var summary = Summary(files);
            DiagnosticLog.Log("RemoveMtMarks", summary + " (" + files.Count + " file(s) looked at)");
            var report = ReportXml(files);
            // Studio makes a file name of the report's name, so it must be a valid one: TaskName's colon made
            // CreateReport throw "The given path's format is not supported", which failed the whole task and
            // Studio then discarded the cleaned files (Michael's live test, 24 Nov 2026). And a report that
            // still can't be written must never cost the user the cleaning itself: log it and carry on.
            try
            {
#if STUDIO19
                CreateReport(ReportName, summary, report, TaskId);
#else
                CreateReport(ReportName, summary, report);
#endif
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("RemoveMtMarks", "Report not created (the files are cleaned): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Add(FileResult result)
        {
            lock (_gate) _files.Add(result);
        }

        /// <summary>One line for the report's description, which Studio shows in its Reports list.</summary>
        internal static string Summary(IReadOnlyCollection<FileResult> files)
        {
            // Only files Studio finished writing: a failed file's counts were
            // never saved.
            var done = files.Where(f => f.Finished).ToList();
            int cleaned = done.Sum(f => f.Count.Cleaned);
            int changedFiles = done.Count(f => f.Count.Cleaned > 0);
            int locked = done.Sum(f => f.Count.LockedLeft);
            var text = cleaned == 0
                ? "No MT marks removed"
                : "MT marks removed from " + cleaned + " segment" + (cleaned == 1 ? "" : "s")
                  + " in " + changedFiles + " file" + (changedFiles == 1 ? "" : "s");
            int unfinished = files.Count(f => !f.NothingToRemove && !f.Finished);
            if (unfinished > 0)
                text += "; " + unfinished + " file" + (unfinished == 1 ? " was" : "s were") + " not finished";
            if (locked > 0)
                text += "; " + locked + (locked == 1 ? " locked segment left as it is" : " locked segments left as they are");
            return text + ".";
        }

        /// <summary>
        /// The report: per file, the segments cleaned (unedited MT, edited after
        /// MT), the locked ones left, and the engines whose records were removed.
        /// </summary>
        internal static string ReportXml(IEnumerable<FileResult> files)
        {
            var sb = new StringBuilder();
            var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true };
            using (var w = XmlWriter.Create(sb, settings))
            {
                w.WriteStartElement("RemoveMtMarksReport");
                foreach (var f in files.OrderBy(x => x.Language, StringComparer.Ordinal)
                                       .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    w.WriteStartElement("File");
                    w.WriteAttributeString("Name", f.Name);
                    w.WriteAttributeString("Language", f.Language);
                    w.WriteAttributeString("Result", f.NothingToRemove ? "Nothing to remove" : f.Finished ? "Cleaned" : "Not finished");
                    w.WriteAttributeString("UneditedMT", f.Count.FullAt.ToString());
                    w.WriteAttributeString("EditedAfterMT", f.Count.FadedAt.ToString());
                    w.WriteAttributeString("LockedLeft", f.Count.LockedLeft.ToString());
                    foreach (var e in f.Count.Engines.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        w.WriteStartElement("Engine");
                        w.WriteAttributeString("Name", e.Key);
                        w.WriteAttributeString("Records", e.Value.ToString());
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            return sb.ToString();
        }

        internal sealed class FileResult
        {
            public readonly string Path;
            public readonly string Name;
            public readonly string Language;
            public readonly MtMarkCount Count = new MtMarkCount();
            public bool NothingToRemove;
            public bool Finished;

            public FileResult(ProjectFile file) : this(file?.LocalFilePath, file?.Language?.IsoAbbreviation) { }

            internal FileResult(string path, string language)
            {
                Path = path ?? "";
                Name = string.IsNullOrEmpty(path) ? "" : System.IO.Path.GetFileName(path);
                Language = language ?? "";
            }
        }
    }

    /// <summary>
    /// Applies <see cref="MtOriginRule"/> to every segment pair Studio streams
    /// through it, then passes the paragraph on unchanged otherwise.
    /// </summary>
    internal sealed class RemoveMtMarksProcessor : AbstractBilingualContentProcessor
    {
        private readonly MtMarkCount _count;

        public RemoveMtMarksProcessor(MtMarkCount count)
        {
            _count = count ?? throw new ArgumentNullException(nameof(count));
        }

        public override void ProcessParagraphUnit(IParagraphUnit paragraphUnit)
        {
            if (paragraphUnit != null && !paragraphUnit.IsStructure)
                foreach (var pair in paragraphUnit.SegmentPairs)
                    MtOriginRule.Clean(pair.Properties, _count);
            base.ProcessParagraphUnit(paragraphUnit);
        }
    }
}

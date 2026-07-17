using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Supervertaler.Trados.Core.Export
{
    /// <summary>
    /// Renders bilingual data as a Word (.docx) document using
    /// DocumentFormat.OpenXml, as a 5-column table (columns: #, Source,
    /// Target, Status, Notes) matching the Supervertaler Workbench's
    /// "Bilingual Table" format. This is the canonical round-trippable
    /// shape — files exported from the Trados plugin can be re-imported by
    /// Supervertaler Workbench and vice versa, as long as the structure is
    /// preserved. (The old stacked layouts were retired in favour of the
    /// Text (.txt) format; see <see cref="BilingualTextRenderer"/>.)
    ///
    /// Note: OpenXml WordprocessingML is verbose; keep formatting
    /// minimal here. The DOCX is a deliverable, not a place to
    /// experiment with rich typography.
    /// </summary>
    public class DocxRenderer : IExportRenderer
    {
        // Distinguisher URL written into the subtitle below the title.
        // The Workbench's exporter writes "Supervertaler.com/workbench"
        // for the same place; that's the only visible thing differentiating
        // a Bilingual Table produced by the Trados plugin from one produced
        // by the Workbench. The title itself is identical so the format
        // remains one recognisable family.
        private const string SUBTITLE_URL = "https://supervertaler.com/trados/";
        private const string SUBTITLE_DISPLAY = "Supervertaler.com/trados";

        public void Render(List<ExportSegment> segments, ExportOptions options, string outputPath)
        {
            using (var doc = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();
                mainPart.Document = new Document();
                var body = mainPart.Document.AppendChild(new Body());

                AppendHeader(mainPart, body, options, segments.Count);

                // Only the 5-column Bilingual Table remains; the stacked
                // layouts were retired in favour of the Text (.txt) format.
                AppendBilingualTable(body, segments, options);

                // Landscape page setup (better for long segments in table form).
                AppendSectionProperties(body, true);

                mainPart.Document.Save();
            }
        }

        // ─── Header block ─────────────────────────────────────────────
        //
        // Mirrors the Workbench's _export_review_table header so that
        // proofreaders / clients receive a visually consistent document
        // regardless of which Supervertaler product produced it:
        //   - Decorative horizontal line (━ × 50, blue 10pt)
        //   - "🌐 Supervertaler Bilingual Table" 18pt bold blue, centered
        //   - Clickable URL subtitle (10pt blue underlined)
        //   - Decorative horizontal line
        //   - Project / Source file / Languages / Segments / Exported
        //     key-value lines (bold labels)
        //   - "⚠️ Important: ..." amber notice + italic instructions

        private static void AppendHeader(MainDocumentPart mainPart, Body body, ExportOptions opts, int total)
        {
            // Decorative line above title.
            body.AppendChild(MakeDecorativeLine());

            // Title: globe emoji + "Supervertaler Bilingual Table"
            // (the emoji uses a non-bold run so the emoji glyph itself
            // isn't bolded; the title text after it is bold blue 18pt).
            var titlePara = new Paragraph(
                new ParagraphProperties(
                    new Justification() { Val = JustificationValues.Center },
                    new SpacingBetweenLines() { Before = "0", After = "120" }),
                MakeRun("🌐 ", fontSize: "36"),
                MakeRun("Supervertaler Bilingual Table", bold: true, fontSize: "36", color: "0066CC"));
            body.AppendChild(titlePara);

            // Subtitle: clickable URL distinguishing Trados-produced files
            // from Workbench-produced files. 10pt blue underlined.
            var subtitlePara = new Paragraph(
                new ParagraphProperties(
                    new Justification() { Val = JustificationValues.Center },
                    new SpacingBetweenLines() { Before = "0", After = "120" }));
            AppendHyperlinkRun(mainPart, subtitlePara,
                SUBTITLE_URL, SUBTITLE_DISPLAY,
                fontSize: "20", color: "0066CC", underline: true);
            body.AppendChild(subtitlePara);

            // Decorative line below subtitle.
            body.AppendChild(MakeDecorativeLine(spaceAfter: "240"));

            // Project info lines.
            body.AppendChild(MakeKeyValueLine("Project: ", opts.ProjectName));
            body.AppendChild(MakeKeyValueLine("Source file: ", opts.SourceFileName));
            body.AppendChild(MakeKeyValueLine("Languages: ",
                opts.SourceLanguageDisplay + " → " + opts.TargetLanguageDisplay));
            body.AppendChild(MakeKeyValueLine("Segments: ", total.ToString(CultureInfo.InvariantCulture)));
            body.AppendChild(MakeKeyValueLine("Exported: ",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));

            // Notice / re-import warning. Same wording as the Workbench.
            var notice = new Paragraph(
                new ParagraphProperties(new SpacingBetweenLines() { Before = "240", After = "240" }),
                MakeRun("⚠️ Important: ", bold: true, color: "B46400"),
                MakeRun("Do not change segment numbers (#) or source text. ", italic: true),
                MakeRun("This file can be re-imported into Supervertaler after proofreading.", italic: true));
            body.AppendChild(notice);
        }

        /// <summary>Decorative horizontal line — 50 heavy-horizontal-line
        /// characters, blue 10pt, centered. Visually matches the Workbench's
        /// `━ × 50` ruling above and below the title.</summary>
        private static Paragraph MakeDecorativeLine(string spaceAfter = "120")
        {
            var pp = new ParagraphProperties(
                new Justification() { Val = JustificationValues.Center },
                new SpacingBetweenLines() { After = spaceAfter });
            return new Paragraph(pp,
                MakeRun(new string('━', 50), fontSize: "20", color: "0066CC"));
        }

        /// <summary>Append a Hyperlink element to <paramref name="parent"/>
        /// that opens <paramref name="url"/> in the user's browser when
        /// clicked in Word. Requires a hyperlink relationship registered on
        /// the document's <c>MainDocumentPart</c>; we create one per call.</summary>
        private static void AppendHyperlinkRun(MainDocumentPart mainPart, Paragraph parent,
            string url, string display, string fontSize, string color, bool underline)
        {
            var rel = mainPart.AddHyperlinkRelationship(new Uri(url), true);

            var runProps = new RunProperties();
            runProps.AppendChild(new RunFonts() { Ascii = "Segoe UI", HighAnsi = "Segoe UI" });
            if (!string.IsNullOrEmpty(fontSize))
                runProps.AppendChild(new FontSize() { Val = fontSize });
            if (!string.IsNullOrEmpty(color))
                runProps.AppendChild(new Color() { Val = color });
            if (underline)
                runProps.AppendChild(new Underline() { Val = UnderlineValues.Single });

            var run = new Run();
            run.AppendChild(runProps);
            var text = new Text(display) { Space = SpaceProcessingModeValues.Preserve };
            run.AppendChild(text);

            var hyperlink = new Hyperlink(run) { Id = rel.Id };
            parent.AppendChild(hyperlink);
        }

        // ─── Table layout (Supervertaler Bilingual Table) ─────────────

        private static void AppendBilingualTable(Body body, List<ExportSegment> segments, ExportOptions opts)
        {
            // Auto-detect whether the segments span more than one source
            // file. If so, the table grows a 6th "File" column and we
            // emit a yellow-highlighted full-width section-break row
            // between each file boundary so the proofreader can see at
            // a glance where one source file ends and the next starts.
            bool multiFile = HasMultipleSourceFiles(segments);

            // Trados segment comments get their own read-only column, but
            // only when at least one segment actually has any — otherwise
            // the table keeps its classic layout and the extra column
            // doesn't eat Source/Target width for nothing.
            bool hasComments = HasAnyComments(segments);

            // Column widths as percentages of the table width × 50 (the
            // OpenXML Pct unit is fiftieths-of-a-percent, so 5000 = 100%).
            // The table itself is set to Pct 5000 = 100% of section width,
            // so columns scale proportionally to fill whatever the page
            // gives them (A4 portrait, A4 landscape, US Letter — all OK).
            //
            // With TableLayout = Fixed on top of percentages, Word respects
            // the column proportions even when body content (long file
            // names, long source paragraphs) would otherwise force a
            // column to grow. No more overflow past the page edge AND
            // no more shrunken-half-the-page tables on wider pages.
            int[] colPct;
            if (multiFile)
                colPct = hasComments
                    // #, Src, Tgt, File, Status, Comments, Notes — sums to 5000.
                    ? new[] { 250, 1250, 1250, 650, 400, 800, 400 }
                    // #, Src, Tgt, File, Status, Notes — sums to 5000.
                    : new[] { 250, 1500, 1500, 800, 500, 450 };
            else
                colPct = hasComments
                    // #, Src, Tgt, Status, Comments, Notes — sums to 5000.
                    ? new[] { 250, 1550, 1550, 450, 750, 450 }
                    // #, Src, Tgt, Status, Notes — sums to 5000.
                    : new[] { 250, 1900, 1900, 500, 450 };

            var table = new Table();

            var tblProps = new TableProperties(
                new TableBorders(
                    new TopBorder() { Val = BorderValues.Single, Size = 4, Color = "888888" },
                    new BottomBorder() { Val = BorderValues.Single, Size = 4, Color = "888888" },
                    new LeftBorder() { Val = BorderValues.Single, Size = 4, Color = "888888" },
                    new RightBorder() { Val = BorderValues.Single, Size = 4, Color = "888888" },
                    new InsideHorizontalBorder() { Val = BorderValues.Single, Size = 4, Color = "BBBBBB" },
                    new InsideVerticalBorder() { Val = BorderValues.Single, Size = 4, Color = "BBBBBB" }),
                new TableWidth() { Width = "5000", Type = TableWidthUnitValues.Pct },
                new TableLayout() { Type = TableLayoutValues.Fixed });
            table.AppendChild(tblProps);

            // TableGrid: required by OpenXML even for percentage tables,
            // and only accepts DXA. Word uses these as PROPORTIONS when
            // the table width is a percentage — so the absolute twip
            // values here only matter relative to each other. We pass in
            // the percentage values directly (they're already in the right
            // proportions) cast to twips for the grid.
            var grid = new TableGrid();
            foreach (var p in colPct)
                grid.AppendChild(new GridColumn() { Width = p.ToString(CultureInfo.InvariantCulture) });
            table.AppendChild(grid);

            // Header row. Cell widths in Pct matching the grid.
            var header = new TableRow(new TableRowProperties(new TableHeader()));
            int hi = 0;
            header.AppendChild(MakeHeaderCellPct("#", colPct[hi++]));
            header.AppendChild(MakeHeaderCellPct(opts.SourceLanguageDisplay, colPct[hi++]));
            header.AppendChild(MakeHeaderCellPct(opts.TargetLanguageDisplay, colPct[hi++]));
            if (multiFile)
                header.AppendChild(MakeHeaderCellPct("File", colPct[hi++]));
            header.AppendChild(MakeHeaderCellPct("Status", colPct[hi++]));
            if (hasComments)
                header.AppendChild(MakeHeaderCellPct("Comments", colPct[hi++]));
            header.AppendChild(MakeHeaderCellPct("Notes", colPct[hi++]));
            table.AppendChild(header);

            // Body rows. Source + Target cells are rendered tag-aware so
            // any <tN>...</tN> placeholders coming through from
            // SegmentTagHandler.Serialize() show up coloured red, matching
            // the Workbench's "With Tags" Bilingual Table appearance.
            // # / Status / Notes columns stay plain — they never contain tags.
            //
            // In multi-file mode an extra yellow-highlighted row precedes
            // the first segment of each new file — proofreader-visible
            // section divider that spans the whole table width.
            string previousFile = null;
            int columnCount = (multiFile ? 6 : 5) + (hasComments ? 1 : 0);
            foreach (var seg in segments)
            {
                if (multiFile)
                {
                    var thisFile = seg.SourceFileName ?? "";
                    if (!string.Equals(thisFile, previousFile, StringComparison.Ordinal))
                    {
                        table.AppendChild(MakeFileSectionRow(thisFile, columnCount));
                        previousFile = thisFile;
                    }
                }

                var row = new TableRow();
                // Segment-number cell: centre-aligned. Numbers read more
                // naturally as a vertical column when centred rather than
                // right-aligned, especially when the column is narrow
                // and the numbers vary in digit count (1 vs 21 vs 121).
                row.AppendChild(MakeBodyCell(seg.Number.ToString(CultureInfo.InvariantCulture), alignment: "center"));
                // Source + Target cells inherit any paragraph-level
                // formatting flags (Heading 1 bold, whole-paragraph italic,
                // etc.) from the segment so the bilingual file visually
                // matches what Trados shows in its editor. # / Status /
                // Notes columns deliberately stay plain.
                row.AppendChild(MakeBodyCell(seg.SourceText ?? "", tagAware: true,
                    bold: seg.IsBold, italic: seg.IsItalic, underline: seg.IsUnderline));
                row.AppendChild(MakeBodyCell(seg.TargetText ?? "", tagAware: true,
                    bold: seg.IsBold, italic: seg.IsItalic, underline: seg.IsUnderline));
                if (multiFile)
                    row.AppendChild(MakeBodyCell(seg.SourceFileName ?? "", alignment: "left"));
                row.AppendChild(MakeBodyCell(seg.DisplayStatus));
                if (hasComments)
                    row.AppendChild(MakeBodyCell(seg.Comments ?? ""));
                row.AppendChild(MakeBodyCell(seg.Notes ?? ""));
                table.AppendChild(row);
            }

            body.AppendChild(table);
        }

        // ─── Section / page setup ─────────────────────────────────────

        private static void AppendSectionProperties(Body body, bool landscape)
        {
            var sectPr = new SectionProperties();
            // Page size: landscape A4 if landscape, otherwise portrait Letter-ish default.
            // Numbers are in twentieths of a point (twips).
            // A4 landscape: 16838 × 11906; A4 portrait: 11906 × 16838.
            if (landscape)
            {
                sectPr.AppendChild(new PageSize() { Width = 16838, Height = 11906, Orient = PageOrientationValues.Landscape });
                sectPr.AppendChild(new PageMargin() { Top = 720, Bottom = 720, Left = 720, Right = 720, Header = 720, Footer = 720, Gutter = 0 });
            }
            else
            {
                sectPr.AppendChild(new PageSize() { Width = 11906, Height = 16838, Orient = PageOrientationValues.Portrait });
                sectPr.AppendChild(new PageMargin() { Top = 1440, Bottom = 1440, Left = 1440, Right = 1440, Header = 720, Footer = 720, Gutter = 0 });
            }
            body.AppendChild(sectPr);
        }

        // ─── Run / cell helpers ───────────────────────────────────────

        private static Run MakeRun(string text, bool bold = false, bool italic = false,
            bool underline = false, string fontSize = null, string color = null)
        {
            var props = new RunProperties();
            if (bold) props.AppendChild(new Bold());
            if (italic) props.AppendChild(new Italic());
            if (underline) props.AppendChild(new Underline() { Val = UnderlineValues.Single });
            if (fontSize != null) props.AppendChild(new FontSize() { Val = fontSize });
            if (color != null) props.AppendChild(new Color() { Val = color });
            props.AppendChild(new RunFonts() { Ascii = "Segoe UI", HighAnsi = "Segoe UI" });

            var run = new Run();
            run.AppendChild(props);

            // Split on newlines so Word renders soft returns rather than literal "\n".
            if (text == null) text = "";
            text = text.Replace("\r\n", "\n");
            var parts = text.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) run.AppendChild(new Break());
                if (parts[i].Length > 0)
                {
                    var t = new Text(parts[i]);
                    t.Space = SpaceProcessingModeValues.Preserve;
                    run.AppendChild(t);
                }
            }
            return run;
        }

        private static Paragraph MakeParagraph(string text, bool bold = false, bool italic = false,
            string fontSize = null, string color = null, string alignment = null)
        {
            var p = new Paragraph();
            if (alignment != null)
            {
                var pp = new ParagraphProperties();
                pp.AppendChild(new Justification() { Val = ParseAlignment(alignment) });
                p.AppendChild(pp);
            }
            p.AppendChild(MakeRun(text, bold: bold, italic: italic, fontSize: fontSize, color: color));
            return p;
        }

        private static JustificationValues ParseAlignment(string alignment)
        {
            switch (alignment)
            {
                case "center": return JustificationValues.Center;
                case "right":  return JustificationValues.Right;
                default:       return JustificationValues.Left;
            }
        }

        private static Paragraph MakeKeyValueLine(string key, string value)
        {
            return new Paragraph(
                new ParagraphProperties(new SpacingBetweenLines() { After = "60" }),
                MakeRun(key, bold: true),
                MakeRun(value ?? ""));
        }

        private static TableCell MakeHeaderCell(string text, int widthPct)
        {
            var tcp = new TableCellProperties(
                new TableCellWidth() { Width = (widthPct * 50).ToString(CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Pct },
                new Shading() { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F0F4F8" });
            var p = new Paragraph(MakeRun(text, bold: true, color: "333333"));
            var cell = new TableCell();
            cell.AppendChild(tcp);
            cell.AppendChild(p);
            return cell;
        }

        /// <summary>Header cell with an absolute (DXA) cell width matching
        /// the table grid. Used by the bilingual-table renderer so column
        /// widths stay stable across page setups — see the comment block
        /// in <see cref="AppendBilingualTable"/> for the rationale.</summary>
        private static TableCell MakeHeaderCellDxa(string text, int widthDxa)
        {
            var tcp = new TableCellProperties(
                new TableCellWidth() { Width = widthDxa.ToString(CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa },
                new Shading() { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F0F4F8" });
            var p = new Paragraph(MakeRun(text, bold: true, color: "333333"));
            var cell = new TableCell();
            cell.AppendChild(tcp);
            cell.AppendChild(p);
            return cell;
        }

        /// <summary>Header cell with a percentage (Pct, fiftieths-of-a-percent)
        /// cell width. Used together with TableWidth = Pct 5000 so the
        /// table fills 100% of the section width regardless of page
        /// orientation or margins.</summary>
        private static TableCell MakeHeaderCellPct(string text, int widthPct)
        {
            var tcp = new TableCellProperties(
                new TableCellWidth() { Width = widthPct.ToString(CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Pct },
                new Shading() { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F0F4F8" });
            var p = new Paragraph(MakeRun(text, bold: true, color: "333333"));
            var cell = new TableCell();
            cell.AppendChild(tcp);
            cell.AppendChild(p);
            return cell;
        }

        /// <summary>Does the segment list contain more than one distinct
        /// SourceFileName? Empty file names are treated as a single
        /// implicit "active file" group — same as single-file mode.</summary>
        private static bool HasMultipleSourceFiles(List<ExportSegment> segments)
        {
            if (segments == null || segments.Count == 0) return false;
            string seen = null;
            foreach (var seg in segments)
            {
                var name = seg.SourceFileName ?? "";
                if (seen == null) { seen = name; continue; }
                if (!string.Equals(seen, name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Does any segment carry Trados comments? Decides whether
        /// the table gets the extra read-only Comments column.</summary>
        private static bool HasAnyComments(List<ExportSegment> segments)
        {
            if (segments == null) return false;
            foreach (var seg in segments)
                if (!string.IsNullOrEmpty(seg.Comments)) return true;
            return false;
        }

        /// <summary>Yellow-highlighted full-width section-break row that
        /// announces a new source file in the multi-file combined-output
        /// mode. Word-side proofreaders can spot file boundaries at a
        /// glance, and the highlight survives Track Changes / save-back.</summary>
        private static TableRow MakeFileSectionRow(string fileName, int columnCount)
        {
            var row = new TableRow();
            var cell = new TableCell();

            // Span the cell across every column so the divider visually
            // covers the full table width.
            var tcp = new TableCellProperties(
                new GridSpan() { Val = columnCount },
                new Shading() { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "FFF2A8" });
            cell.AppendChild(tcp);

            var p = new Paragraph();
            p.AppendChild(MakeRun("📄  ", fontSize: "24"));
            p.AppendChild(MakeRun(
                "File: " + (string.IsNullOrEmpty(fileName) ? "(unknown)" : fileName),
                bold: true, fontSize: "24", color: "5C4A00"));
            cell.AppendChild(p);

            row.AppendChild(cell);
            return row;
        }

        private static TableCell MakeBodyCell(string text, string alignment = "left",
            bool tagAware = false, bool bold = false, bool italic = false, bool underline = false)
        {
            var p = new Paragraph();
            if (alignment != "left")
            {
                var pp = new ParagraphProperties();
                pp.AppendChild(new Justification() { Val = ParseAlignment(alignment) });
                p.AppendChild(pp);
            }
            if (tagAware)
                AppendTagAwareRuns(p, text ?? "", bold: bold, italic: italic, underline: underline);
            else
                p.AppendChild(MakeRun(text ?? "", bold: bold, italic: italic, underline: underline));
            var cell = new TableCell();
            cell.AppendChild(p);
            return cell;
        }

        // ─── Tag-aware cell rendering ─────────────────────────────────
        //
        // Mirrors the Workbench's "With Tags" Bilingual Table export by
        // colouring <tN>, </tN>, <tN/> tag placeholders dark red while
        // leaving the surrounding text in its normal body colour. The
        // proofreader can therefore see at a glance where the inline
        // formatting / field-code / page-number anchors are, and the
        // markers survive plain-text round-trip cleanly because they're
        // literal text. The Trados re-import path
        // (AiAssistantViewPart.OnBilingualImportRequested) re-serialises
        // the live source to regenerate the matching TagMap and calls
        // SegmentTagHandler.ReconstructTarget so the proofreader's
        // re-positioned tags land back on the correct words in Trados.

        // Matches both numbered placeholders (<t1>, </t1>, <t2/>) AND
        // semantic markers (<b>, </b>, <i>, </i>, <u>, </u>, <bi>, </bi>)
        // so the proofreader sees every tag-related marker coloured red
        // regardless of whether BilingualTagNamer.ApplySemanticNames
        // renamed it or kept it numbered. Delegates to the shared regex
        // on BilingualTagNamer so the matching rule stays in one place.
        private static readonly Regex TagPlaceholderRegex = BilingualTagNamer.AnyTagMarkerRegex;

        /// <summary>The "memoQ dark red" used by the Workbench's "With Tags"
        /// export for tag placeholders. Hex #7F0001.</summary>
        private const string TAG_PLACEHOLDER_COLOR = "7F0001";

        /// <summary>Appends runs to <paramref name="p"/> that render
        /// <paramref name="text"/> with tag placeholders coloured red AND
        /// the text between semantic markers (<c>&lt;b&gt;...&lt;/b&gt;</c>,
        /// <c>&lt;i&gt;...&lt;/i&gt;</c>, <c>&lt;u&gt;...&lt;/u&gt;</c>,
        /// <c>&lt;bi&gt;...&lt;/bi&gt;</c>) rendered with the matching
        /// bold/italic/underline character formatting. The proofreader
        /// sees both the markers (so they know which text is anchored to
        /// a tag) and a visual preview of how that text will look when
        /// the formatting is applied in Trados.
        ///
        /// Numbered structural markers (<c>&lt;tN&gt;</c>) don't change
        /// the active formatting state — they wrap text that has no
        /// semantic styling (field codes, page numbers, etc.).
        ///
        /// Cell-baseline bold/italic/underline (from
        /// <see cref="ExportSegment.IsBold"/> etc., usually a paragraph-
        /// level "Heading 1" / "Title" style) is unioned with the active
        /// in-text state, so a bolded heading containing an italic span
        /// renders bold+italic in the italic span, bold everywhere else.</summary>
        private static void AppendTagAwareRuns(Paragraph p, string text,
            bool bold = false, bool italic = false, bool underline = false)
        {
            if (string.IsNullOrEmpty(text))
            {
                p.AppendChild(MakeRun("", bold: bold, italic: italic, underline: underline));
                return;
            }

            // Active formatting state as we walk left-to-right. Counts
            // (rather than booleans) so nested same-name tags like
            // "<b>x <b>y</b> z</b>" do the right thing — bold stays on
            // through inner pairs.
            int activeBold = 0, activeItalic = 0, activeUnderline = 0;

            int lastEnd = 0;
            foreach (Match m in TagPlaceholderRegex.Matches(text))
            {
                if (m.Index > lastEnd)
                {
                    var fragment = text.Substring(lastEnd, m.Index - lastEnd);
                    p.AppendChild(MakeRun(fragment,
                        bold: bold || activeBold > 0,
                        italic: italic || activeItalic > 0,
                        underline: underline || activeUnderline > 0));
                }

                // Emit the marker itself in red. Carries any baseline
                // cell formatting (so a Heading-1 cell's red markers are
                // also bold). We deliberately do NOT carry the active
                // in-text formatting onto the markers themselves —
                // markers represent tag anchors, not the formatted span,
                // so they read more cleanly when only the body text
                // shows the styling.
                p.AppendChild(MakeRun(m.Value,
                    bold: bold, italic: italic, underline: underline,
                    color: TAG_PLACEHOLDER_COLOR));

                // Update active state AFTER emitting the marker.
                UpdateActiveFormatting(m.Value,
                    ref activeBold, ref activeItalic, ref activeUnderline);

                lastEnd = m.Index + m.Length;
            }
            if (lastEnd < text.Length)
            {
                p.AppendChild(MakeRun(text.Substring(lastEnd),
                    bold: bold || activeBold > 0,
                    italic: italic || activeItalic > 0,
                    underline: underline || activeUnderline > 0));
            }
        }

        /// <summary>Apply the effect of a single tag marker on the
        /// active-formatting counters. Opening tag → increment matching
        /// counter(s); closing tag → decrement; numbered structural
        /// markers (<c>&lt;tN&gt;</c>) and self-closing tags don't
        /// affect formatting state.</summary>
        private static void UpdateActiveFormatting(string marker,
            ref int activeBold, ref int activeItalic, ref int activeUnderline)
        {
            if (string.IsNullOrEmpty(marker)) return;
            bool isClosing = marker.Length > 1 && marker[1] == '/';
            int delta = isClosing ? -1 : 1;

            // Extract the tag name (strip the < / > and optional /).
            // Examples: "<b>" → "b", "</bi>" → "bi", "<t1>" → "t1",
            // "<t2/>" → "t2/" (we strip the trailing slash next).
            string name = marker.TrimStart('<').TrimEnd('>').TrimStart('/').TrimEnd('/').Trim();
            if (string.IsNullOrEmpty(name)) return;

            switch (name.ToLowerInvariant())
            {
                case "b":
                    activeBold = Math.Max(0, activeBold + delta);
                    break;
                case "i":
                    activeItalic = Math.Max(0, activeItalic + delta);
                    break;
                case "u":
                    activeUnderline = Math.Max(0, activeUnderline + delta);
                    break;
                case "bi":
                    activeBold = Math.Max(0, activeBold + delta);
                    activeItalic = Math.Max(0, activeItalic + delta);
                    break;
                default:
                    // Numbered structural markers (t1, t2, …) and any
                    // unrecognised marker name don't carry semantic
                    // styling — leave the active counters alone.
                    break;
            }
        }
    }
}

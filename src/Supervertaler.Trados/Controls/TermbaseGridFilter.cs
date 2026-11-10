using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Filtering of the termbase grid in Settings, kept apart from the form so it
    /// can be tested on a plain grid.
    ///
    /// <para>The one rule: filtering HIDES rows and never changes them. A ticked
    /// termbase that the filter hides stays ticked, is still saved, and is still
    /// used. The only things filtering does besides hiding are the ones that keep
    /// a hidden row from being acted on by accident: it is deselected (so
    /// Remove / Export / Open never act on a row the user cannot see), and a
    /// header click that ticks or unticks a whole column only reaches the rows
    /// on screen (<see cref="BulkTargets"/>).</para>
    /// </summary>
    public static class TermbaseGridFilter
    {
        public struct Result
        {
            public int Total;
            public int Shown;
            /// <summary>Hidden rows with at least one of the watched boxes ticked.</summary>
            public int HiddenTicked;
        }

        public static Result Apply(DataGridView grid, string needle, string nameColumn, string languageColumn,
                                   IList<string> tickColumns)
        {
            var result = new Result();
            if (grid == null) return result;

            // A half-finished checkbox edit is committed first, so hiding its row
            // can never drop the click.
            if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            grid.EndEdit();

            var words = FilterText.Words(needle);
            var current = grid.CurrentRow;

            // Decide first, then hide: the current row has to be released before
            // it can be made invisible.
            var show = new bool[grid.Rows.Count];
            for (int i = 0; i < grid.Rows.Count; i++)
            {
                var row = grid.Rows[i];
                show[i] = words.Length == 0 ||
                          FilterText.Matches(CellText(row, nameColumn) + "  " + CellText(row, languageColumn), words);
            }
            if (current != null && !show[current.Index]) grid.CurrentCell = null;

            int changes = 0;
            for (int i = 0; i < grid.Rows.Count; i++)
                if (grid.Rows[i].Visible != show[i]) changes++;

            // Each row shown or hidden makes the grid re-measure itself and its
            // scrollbars: ~0.7 ms a row, so a first letter hiding 1,000 termbases
            // took 0.7 s. With the scrollbars and painting off while rows change,
            // it is about half that; they come back once, at the end.
            var bars = grid.ScrollBars;
            var quiet = changes > 0 && grid.IsHandleCreated;
            if (quiet)
            {
                grid.ScrollBars = ScrollBars.None;
                SendMessage(grid.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            }
            try
            {
                for (int i = 0; i < grid.Rows.Count; i++)
                {
                    var row = grid.Rows[i];
                    if (!show[i] && row.Selected) row.Selected = false;
                    if (row.Visible != show[i]) row.Visible = show[i];
                }
            }
            finally
            {
                if (quiet)
                {
                    SendMessage(grid.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                    grid.ScrollBars = bars;
                    grid.Invalidate();
                }
            }

            for (int i = 0; i < grid.Rows.Count; i++)
            {
                var row = grid.Rows[i];

                result.Total++;
                if (show[i]) { result.Shown++; continue; }
                foreach (var col in tickColumns)
                {
                    if (grid.Columns.Contains(col) && (row.Cells[col].Value as bool? ?? false))
                    {
                        result.HiddenTicked++;
                        break;
                    }
                }
            }
            return result;
        }

        /// <summary>The rows a "tick / untick the whole column" header click acts on: the visible ones.</summary>
        public static IEnumerable<DataGridViewRow> BulkTargets(DataGridView grid)
        {
            foreach (DataGridViewRow row in grid.Rows)
                if (row.Visible) yield return row;
        }

        private const int WM_SETREDRAW = 0x000B;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static string CellText(DataGridViewRow row, string column)
        {
            if (string.IsNullOrEmpty(column) || !row.DataGridView.Columns.Contains(column)) return "";
            return row.Cells[column].Value?.ToString() ?? "";
        }
    }
}

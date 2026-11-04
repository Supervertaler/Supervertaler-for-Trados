using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// The list of stretches of source text outside any segment, as a menu under
    /// a control: the yellow line in the TermLens panel and the pop-up both open
    /// it. Clicking a stretch puts the editor on the nearest segment.
    /// </summary>
    internal static class UnsegmentedTextMenu
    {
        /// <summary>Stretches listed in the menu; the rest are counted.</summary>
        public const int MaxListed = 25;

        /// <summary>UI thread.</summary>
        public static void Show(Control anchor, IList<UnsegmentedHit> hits, int total, bool severalFiles)
        {
            if (anchor == null || anchor.IsDisposed || !anchor.IsHandleCreated || hits == null || hits.Count == 0)
                return;

            var menu = new ContextMenuStrip { ShowImageMargin = false, ShowItemToolTips = true };
            // Disposed after the clicked item's handler has run, not before it.
            menu.Closed += (s, e) =>
            {
                if (!anchor.IsDisposed && anchor.IsHandleCreated) anchor.BeginInvoke(new Action(menu.Dispose));
                else menu.Dispose();
            };

            foreach (var hit in hits.Take(MaxListed))
            {
                var h = hit;
                var where = (severalFiles ? UnsegmentedTextMonitor.DisplayName(h.FilePath) + ", " : "") + h.Location;
                var label = where + ": " + UnsegmentedTextMonitor.Shorten(h.Text, 80);
                var item = new ToolStripMenuItem(label.Replace("&", "&&"))
                {
                    ToolTipText = UnsegmentedTextMonitor.Shorten(h.Text, 300),
                    Enabled = h.JumpSegmentId != null
                };
                item.Click += (s, e) =>
                {
                    var problem = UnsegmentedTextMonitor.GoTo(h);
                    if (problem != null)
                        MessageBox.Show(anchor.IsDisposed ? null : anchor.FindForm(), problem, "Supervertaler for Trados",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                };
                menu.Items.Add(item);
            }
            if (total > MaxListed)
                menu.Items.Add(new ToolStripMenuItem($"…and {total - MaxListed} more") { Enabled = false });

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Why this matters and what to do", null,
                (s, e) => HelpSystem.OpenHelp(HelpSystem.Topics.UnsegmentedText));

            menu.Show(anchor, new Point(0, anchor.Height));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Finds a copy of this Trados Studio that is still running without a window,
    /// so Supervertaler can tell the user at startup (25 Nov 2026).
    ///
    /// <para><b>Why.</b> A Studio that "closed" but kept running in the background
    /// is common (Michael has seen it for years; on 12 Nov 2026 a second, invisible
    /// Studio 2026 sat idle beside the real one; on 25 Nov the beta lingered after
    /// its window closed). While it runs it holds the plugin's Unpacked folder, so
    /// an update from the App Store can't replace it: two users lost Supervertaler
    /// that way after 209 (23-24 Nov), and Studio never repairs such a folder. The
    /// 25 Nov simulation showed the in-app update itself works when nothing holds
    /// the folder (notes: studio-in-app-update-rea.md).</para>
    ///
    /// <para><b>What counts.</b> Another SDLTradosStudio process started from the
    /// same SDLTradosStudio.exe as ours (so Studio 2024 next to Studio 2026, or the
    /// beta next to the release, is never reported) and showing no main window. A
    /// process whose path can't be read is left out: when in doubt, say nothing.</para>
    ///
    /// <para><b>When.</b> Once, <see cref="DelaySeconds"/> after startup, on a timer
    /// thread: a second launch of Studio (a double click) is windowless for a moment
    /// and then exits, and that must not raise a warning. Getting the process list
    /// takes milliseconds and happens once per Studio session, whatever the size of
    /// the user's projects.</para>
    /// </summary>
    internal static class HiddenStudioCheck
    {
        internal const int DelaySeconds = 30;
        internal const string ProcessName = "SDLTradosStudio";

        internal sealed class StudioProcess
        {
            public int Id;
            /// <summary>Full path of the process's exe; null when it could not be read.</summary>
            public string Path;
            public bool HasWindow;
            public DateTime? Started;
        }

        /// <summary>
        /// The processes in <paramref name="processes"/> that are another, windowless
        /// copy of the Studio at <paramref name="ownPath"/>. Pure: the test drives it
        /// with made-up processes.
        /// </summary>
        internal static List<StudioProcess> FindHidden(int ownId, string ownPath, IEnumerable<StudioProcess> processes)
        {
            var own = Normalise(ownPath);
            if (own == null || processes == null) return new List<StudioProcess>();
            return processes
                .Where(p => p != null && p.Id != ownId && !p.HasWindow)
                .Where(p => string.Equals(Normalise(p.Path), own, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Id)
                .ToList();
        }

        private static string Normalise(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return System.IO.Path.GetFullPath(path.Trim()); }
            catch { return null; }
        }

        /// <summary>The warning shown to the user; null when there is nothing to say.</summary>
        internal static string Message(IList<StudioProcess> hidden)
        {
            if (hidden == null || hidden.Count == 0) return null;
            var which = string.Join(", ", hidden.Select(p =>
                p.Id + (p.Started.HasValue ? " (started " + p.Started.Value.ToString("HH:mm") + ")" : "")));
            var one = hidden.Count == 1;
            return (one
                    ? "Another copy of Trados Studio is still running in the background, without a window"
                    : "Other copies of Trados Studio are still running in the background, without a window")
                + ": process ID " + which + ".\n\n"
                + "While " + (one ? "it runs" : "they run") + ", installing or updating plugins can't finish, "
                + "and Supervertaler or another plugin may be missing after the next update.\n\n"
                + "To close " + (one ? "it" : "them") + ": press Ctrl+Shift+Esc, open the Details tab, select "
                + "SDLTradosStudio.exe with that process ID and click End task. "
                + "The Studio you are working in is not affected.";
        }

        /// <summary>The Studio processes on this computer, as far as they can be read.</summary>
        internal static List<StudioProcess> Snapshot()
        {
            var list = new List<StudioProcess>();
            Process[] found;
            try { found = Process.GetProcessesByName(ProcessName); }
            catch { return list; }
            foreach (var p in found)
            {
                try
                {
                    var sp = new StudioProcess { Id = p.Id };
                    try { sp.Path = p.MainModule?.FileName; } catch { sp.Path = null; }
                    try { sp.HasWindow = p.MainWindowHandle != IntPtr.Zero; } catch { sp.HasWindow = true; }
                    try { sp.Started = p.StartTime; } catch { sp.Started = null; }
                    list.Add(sp);
                }
                catch { }
                finally { p.Dispose(); }
            }
            return list;
        }

        /// <summary>Path of our own SDLTradosStudio.exe, or null.</summary>
        internal static string OwnPath()
        {
            try
            {
                using (var self = Process.GetCurrentProcess())
                    return self.MainModule?.FileName;
            }
            catch { return null; }
        }
    }
}

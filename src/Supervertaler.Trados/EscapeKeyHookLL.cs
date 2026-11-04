using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Supervertaler.Trados
{
    /// <summary>
    /// WH_KEYBOARD_LL hook that dismisses Supervertaler pop-up surfaces on
    /// Escape. Lowest interception level Windows offers: keys arrive here
    /// before they are posted to any thread queue, so whatever Studio's input
    /// pipeline does afterwards cannot starve us (both the IMessageFilter and
    /// a WH_GETMESSAGE hook measurably never saw Escape at all). Installed
    /// from inside the Trados process, so it works when Studio runs elevated -
    /// UIPI only blocks OTHER, lower-integrity processes.
    ///
    /// Scope guard: only acts when the foreground window belongs to this
    /// process AND the dismiss callback reports it actually closed something.
    /// In every other case the key passes through untouched, including to
    /// other applications.
    ///
    /// THE HOOK LIVES ON ITS OWN THREAD, NEVER ON STUDIO'S UI THREAD (20.205).
    /// A low-level hook is system-wide: Windows calls it, on the thread that
    /// installed it, for every key pressed anywhere on the PC, and holds the
    /// key until it returns. On Studio's UI thread, every busy spell (a long
    /// TermLens refresh, opening a project) held every keystroke on the PC for
    /// the hook timeout (~300 ms) and then sent it on WITHOUT calling the hooks
    /// installed before ours. Measured: an application that hooked the
    /// keyboard before Studio started - Wispr Flow, whose Ctrl+Win dictation
    /// shortcut can only be seen that way - received none of the keys pressed
    /// while Studio was busy, so its shortcut did nothing. Long spells also
    /// made Windows remove our hook for good, silently ending this feature.
    ///
    /// Now the hook thread does nothing but wait for keys, so it always
    /// answers at once. Only an Escape key-down while Studio is the foreground
    /// process involves the UI thread: the hook asks it (a sent message to a
    /// message-only window it owns) to run the dismiss callback, and waits at
    /// most <see cref="AskTimeoutMs"/> for the answer. A busy UI thread
    /// therefore costs that one Escape at most 100 ms and lets it through to
    /// Studio; it never touches any other key or any other application.
    /// </summary>
    internal sealed class EscapeKeyHookLL : IDisposable
    {
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam;
            public IntPtr lParam;
            public int time;
            public int pt_x;
            public int pt_y;
        }

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_QUIT = 0x0012;
        private const int WM_ESCAPE_QUESTION = 0x8000 + 0x5E1;   // WM_APP range, private to our own window
        private const int VK_ESCAPE = 0x1B;
        private const uint PM_NOREMOVE = 0x0000;
        private const uint SMTO_BLOCK = 0x0001;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        /// <summary>Longest the hook waits for the UI thread's answer. Well
        /// under Windows' hook timeout (LowLevelHooksTimeout; measured 306-315
        /// ms on Windows 11 with the value not set), which must never be
        /// reached: that is the failure this class is built to avoid.</summary>
        private const int AskTimeoutMs = 100;

        /// <summary>The UI thread acts only on a question at most this old.
        /// An older one may be one the hook has already given up on and let
        /// through to Studio; acting on it as well would peel a second layer
        /// with one press. Half the wait leaves the dismissal itself time to
        /// finish before the hook's deadline.</summary>
        private const int FreshMs = AskTimeoutMs / 2;

        private readonly uint _ownPid;
        private readonly UiWindow _window;         // created on, and answering on, the UI thread
        private readonly IntPtr _windowHandle;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private readonly LowLevelKeyboardProc _proc;   // held so the GC can't collect it while hooked
        private IntPtr _hookId;                    // set and unhooked on the hook thread only
        private volatile uint _hookThreadId;
        private bool _disposed;

        /// <summary>Construct on the UI thread: <paramref name="onEscape"/>
        /// runs there, for each Escape key-down while this process is in the
        /// foreground; return true to eat the keypress (something was
        /// dismissed).</summary>
        public EscapeKeyHookLL(Func<bool> onEscape)
        {
            if (onEscape == null) throw new ArgumentNullException(nameof(onEscape));
            using (var me = Process.GetCurrentProcess())
                _ownPid = (uint)me.Id;
            _window = new UiWindow(onEscape);
            _windowHandle = _window.Handle;
            _proc = HookProc;
            _thread = new Thread(HookThreadMain)
            {
                IsBackground = true,                  // never keeps Studio alive
                Name = "Supervertaler Escape hook",
                Priority = ThreadPriority.AboveNormal // not kept waiting by Studio's own busy threads
            };
            _thread.Start();
            // A few milliseconds: until the thread has its message queue and
            // the hook, so Dispose can always reach it. Bounded either way.
            _ready.Wait(2000);
        }

        private void HookThreadMain()
        {
            try
            {
                // Creates this thread's message queue, so a WM_QUIT from Dispose can't be lost.
                PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
                _hookThreadId = GetCurrentThreadId();
                using (var me = Process.GetCurrentProcess())
                using (var mod = me.MainModule)
                    _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(mod.ModuleName), 0);
                if (_hookId == IntPtr.Zero)
                    Core.DiagnosticLog.WriteAlways("EscapeHook",
                        "low-level keyboard hook not installed, error " + Marshal.GetLastWin32Error());
            }
            catch (Exception ex)
            {
                // An exception escaping a thread would take Studio down with it.
                try { Core.DiagnosticLog.WriteAlways("EscapeHook", "hook thread failed: " + ex.Message); } catch { }
            }
            finally
            {
                _ready.Set();
            }
            if (_hookId == IntPtr.Zero) return;

            try
            {
                // Windows calls HookProc from inside GetMessage. Nothing else
                // ever runs on this thread, which is the whole point.
                while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            }
            catch { }
            finally
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        // Runs on the hook thread for every key event on the PC: keep it trivial.
        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN
                    && Marshal.ReadInt32(lParam) == VK_ESCAPE)   // KBDLLHOOKSTRUCT.vkCode is its first field
                {
                    // Only when Trados itself is the foreground process - never
                    // touch Escape typed into any other application.
                    GetWindowThreadProcessId(GetForegroundWindow(), out uint fgPid);
                    if (fgPid == _ownPid && AskUiThread())
                        return (IntPtr)1;   // swallow: the Escape did its job
                }
            }
            catch { }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        /// <summary>Asks the UI thread to dismiss whatever is open; true when
        /// something was. False at once when the UI thread is hung or the
        /// window is gone, after <see cref="AskTimeoutMs"/> when it is busy.</summary>
        private bool AskUiThread()
        {
            var sent = SendMessageTimeout(_windowHandle, WM_ESCAPE_QUESTION,
                (IntPtr)Environment.TickCount, IntPtr.Zero,
                SMTO_BLOCK | SMTO_ABORTIFHUNG, AskTimeoutMs, out UIntPtr result);
            return sent != IntPtr.Zero && result == (UIntPtr)1;
        }

        /// <summary>Call on the UI thread (it destroys the UI-thread window).
        /// In Studio the hook lives for the whole session; Windows removes it
        /// when the process ends.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            uint tid = _hookThreadId;
            if (tid != 0) PostThreadMessage(tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            try { _window.DestroyHandle(); } catch { }
        }

        /// <summary>Message-only window on the UI thread that answers the
        /// hook's Escape question there, where the pop-ups live.</summary>
        private sealed class UiWindow : NativeWindow
        {
            private readonly Func<bool> _onEscape;

            public UiWindow(Func<bool> onEscape)
            {
                _onEscape = onEscape;
                CreateHandle(new CreateParams { Parent = (IntPtr)(-3) });   // HWND_MESSAGE
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg != WM_ESCAPE_QUESTION)
                {
                    base.WndProc(ref m);
                    return;
                }
                bool handled = false;
                int age = unchecked(Environment.TickCount - (int)m.WParam.ToInt64());
                if (age >= 0 && age <= FreshMs)
                {
                    try { handled = _onEscape(); }
                    catch { handled = false; }   // never let an error reach the window procedure
                }
                m.Result = (IntPtr)(handled ? 1 : 0);
            }
        }
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Supervertaler.Trados.VoiceControl
{
    /// <summary>
    /// Minimal P/Invoke binding for libvosk (the offline Kaldi-based speech
    /// recogniser Workbench uses). We only bind what grammar-mode command
    /// recognition needs: model load, grammar recogniser, waveform feed,
    /// result strings.
    ///
    /// The native DLL is NOT shipped in the plugin package – it is downloaded
    /// on first activation by VoiceRuntimeInstaller (x64: libvosk 0.3.45,
    /// x86: 0.3.42 – the last upstream release with a 32-bit Windows build,
    /// needed for Studio 2024's 32-bit process; the C API we use is identical).
    /// <see cref="Preload"/> pins it in the module table by absolute path
    /// before the first DllImport call, same trick as AppInitializer's
    /// e_sqlite3 preload.
    /// </summary>
    internal static class VoskNative
    {
        private const string Dll = "libvosk";

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hReservedNull, uint dwFlags);
        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        private static bool _loaded;

        /// <summary>
        /// Loads libvosk.dll from an absolute path so subsequent DllImport
        /// resolution finds it. LOAD_WITH_ALTERED_SEARCH_PATH lets any
        /// dependent DLLs sitting next to it resolve too.
        /// </summary>
        public static bool Preload(string dllPath)
        {
            if (_loaded) return true;
            if (!File.Exists(dllPath)) return false;
            var handle = LoadLibraryEx(dllPath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
            _loaded = handle != IntPtr.Zero;
            return _loaded;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vosk_set_log_level(int level);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr vosk_model_new(byte[] modelPathUtf8);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vosk_model_free(IntPtr model);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr vosk_recognizer_new_grm(IntPtr model, float sampleRate, byte[] grammarJsonUtf8);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vosk_recognizer_free(IntPtr recognizer);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vosk_recognizer_accept_waveform(IntPtr recognizer, byte[] data, int length);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr vosk_recognizer_result(IntPtr recognizer);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr vosk_recognizer_final_result(IntPtr recognizer);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vosk_recognizer_reset(IntPtr recognizer);

        /// <summary>1 = include per-word start/end seconds (a "result" array) in results.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vosk_recognizer_set_words(IntPtr recognizer, int words);

        /// <summary>UTF-8 path/JSON marshalling (net48 has no UTF8 string marshaller).</summary>
        public static byte[] Utf8(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? "");
            var buf = new byte[bytes.Length + 1]; // NUL-terminated
            Array.Copy(bytes, buf, bytes.Length);
            return buf;
        }

        /// <summary>Reads a NUL-terminated UTF-8 string returned by libvosk.</summary>
        public static string ReadUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return "";
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            if (len == 0) return "";
            var buf = new byte[len];
            Marshal.Copy(ptr, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }
    }
}

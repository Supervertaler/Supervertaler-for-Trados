using System;
using System.Diagnostics;
using System.Runtime;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Lightweight guard against memory / address-space exhaustion, aimed at the
    /// 32-bit Trados Studio 2024 host. A 32-bit process can address only ~2-4 GB in
    /// total, so a very large document plus a long AI batch can run it out of address
    /// space. That surfaces as GDI/Direct2D paint failures inside Trados (bitmap/HDC
    /// allocations start failing) and ultimately a fatal CLR ExecutionEngine error.
    ///
    /// We cannot raise that ceiling, but we can stay under it: cap batch size, trim
    /// the heavy document-context embed, compact the heap when memory climbs, and
    /// stop gracefully (with a clear message) before the host corrupts its heap.
    /// All of this is a no-op on 64-bit (Trados Studio 2026), where there is plenty
    /// of address space.
    /// </summary>
    public static class MemoryGuard
    {
        /// <summary>True on a 32-bit process (the constrained case).</summary>
        public static bool Is32Bit => IntPtr.Size == 4;

        // Heuristic thresholds (private committed bytes). Only consulted on 32-bit.
        // A 32-bit process tops out near 2 GB (4 GB if LARGEADDRESSAWARE on 64-bit
        // Windows); we stay well under to leave room for Trados's own paint/GDI
        // allocations and for heap fragmentation. Tunable.
        public const long SoftLimitBytes = 1_200L * 1024 * 1024; // 1.2 GB -> compact the heap
        public const long HardLimitBytes = 1_600L * 1024 * 1024; // 1.6 GB -> stop gracefully

        /// <summary>Current process private (committed) memory, best-effort; 0 on failure.</summary>
        public static long PrivateBytes()
        {
            try { using (var p = Process.GetCurrentProcess()) return p.PrivateMemorySize64; }
            catch { return 0; }
        }

        public static bool IsOverSoftLimit() => Is32Bit && PrivateBytes() >= SoftLimitBytes;
        public static bool IsOverHardLimit() => Is32Bit && PrivateBytes() >= HardLimitBytes;

        /// <summary>
        /// Force a full GC with one-shot Large Object Heap compaction. The big
        /// per-batch prompt/response strings land on the LOH, which is not compacted
        /// by default and is the main source of 32-bit address-space fragmentation.
        /// </summary>
        public static void CollectAndCompact()
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Suggested safe batch size for the current host. On 64-bit, returns the
        /// requested size unchanged. On 32-bit, caps it so each request's transient
        /// memory stays modest.
        /// </summary>
        public static int ClampBatchSize(int requested)
        {
            if (!Is32Bit) return requested;
            if (requested <= 0) return 5;
            return Math.Min(requested, 10);
        }
    }
}

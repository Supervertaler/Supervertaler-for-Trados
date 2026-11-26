using System;
using System.Collections.Generic;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.FileTypeSupport.Framework.NativeApi;

namespace Supervertaler.Trados.Core.MtMarks
{
    /// <summary>
    /// What one run of Remove MT marks did to one file. Filled from Studio's
    /// batch-task thread, one instance per file, read once the file is done.
    /// </summary>
    internal sealed class MtMarkCount
    {
        /// <summary>Segments that were unedited machine translation (Trados shows AT).</summary>
        public int FullAt;

        /// <summary>Segments edited after machine translation (Trados shows a faded AT).</summary>
        public int FadedAt;

        /// <summary>Locked segments with an MT mark, left as they are.</summary>
        public int LockedLeft;

        /// <summary>MT records removed, by engine (the origin's system name).</summary>
        public readonly Dictionary<string, int> Engines = new Dictionary<string, int>(StringComparer.Ordinal);

        public int Cleaned => FullAt + FadedAt;
    }

    /// <summary>
    /// The rule behind the "Supervertaler - Remove MT marks" batch task, on one
    /// segment's <see cref="ITranslationOrigin"/>. The same edits as
    /// Supervertaler for memoQ's TradosMtMarks makes to .sdlxliff text, which
    /// Trados accepted with no AT on any row (23 Nov 2026):
    /// <list type="bullet">
    /// <item>an MT origin (<c>mt</c>, <c>nmt</c>, <c>amt</c>) becomes
    /// <c>interactive</c>, without the engine name and match percent that
    /// belonged to the MT;</item>
    /// <item>every MT link in the earlier-origin chain
    /// (<see cref="ITranslationOrigin.OriginBeforeAdaptation"/>, the .sdlxliff's
    /// <c>sdl:prev-origin</c>) goes; a non-MT link behind it - a TM match the
    /// MT replaced - moves up and stays.</item>
    /// </list>
    /// Everything else on the origin stays: who created and changed it and when,
    /// the TM hash Supervertaler and Studio use to update the TM, context-match
    /// flags. So do the status, the text, comments and tracked changes, which
    /// this never reads.
    ///
    /// <para><b>Why not <c>ChangeTranslationOriginOnSegment</c>.</b> That runs
    /// Studio's edit rule, which copies the MT origin into the earlier origin -
    /// the faded AT this exists to remove. RWS's Trados Batch Anonymizer leaves
    /// exactly that trace ("Before Interactive Editing", RWS Community, 2024).</para>
    ///
    /// <para><b>Locked segments are left alone</b>, and counted: a lock says the
    /// segment is not the translator's to change, typically the client's own
    /// pre-translation.</para>
    ///
    /// <para>Only those three origin names: they are the ones Studio's editor
    /// tests for MT (Studio 2026's editor holds the literals "mt" and "nmt";
    /// "amt" is adaptive MT). Studio's other automated origin,
    /// "automatic-translation", appears in no editor code, so it is left.</para>
    /// </summary>
    internal static class MtOriginRule
    {
        internal const string Interactive = "interactive";

        /// <summary>The report's name for an MT record that does not name its engine.</summary>
        internal const string UnnamedEngine = "machine translation";

        private static readonly HashSet<string> MtOrigins =
            new HashSet<string>(StringComparer.Ordinal) { "mt", "nmt", "amt" };

        internal static bool IsMt(string originType) => originType != null && MtOrigins.Contains(originType);

        /// <summary>Whether the origin or any earlier origin in its chain is MT.</summary>
        internal static bool HasMt(ITranslationOrigin origin)
        {
            // A chain is a handful of links in practice; the cap only guards a
            // malformed, looping one.
            int guard = 0;
            for (var o = origin; o != null && guard++ < 1000; o = o.OriginBeforeAdaptation)
                if (IsMt(o.OriginType)) return true;
            return false;
        }

        /// <summary>
        /// Removes the MT marks from one segment's origin, in place, counting
        /// into <paramref name="count"/>. True when the origin changed. A
        /// segment without MT, or a locked one, is not touched.
        /// </summary>
        internal static bool Clean(ISegmentPairProperties props, MtMarkCount count)
        {
            var origin = props?.TranslationOrigin;
            if (origin == null || !HasMt(origin))
                return false;
            if (props.IsLocked)
            {
                count.LockedLeft++;
                return false;
            }

            if (IsMt(origin.OriginType))
            {
                count.FullAt++;
                CountEngine(count, origin.OriginSystem);
                origin.OriginType = Interactive;
                origin.OriginSystem = null;
                origin.MatchPercent = 0;
            }
            else
            {
                count.FadedAt++;
            }

            // The earlier origins that are not MT, in order. Read the whole chain
            // before relinking anything.
            var kept = new List<ITranslationOrigin>();
            int guard = 0;
            for (var link = origin.OriginBeforeAdaptation; link != null && guard++ < 1000; link = link.OriginBeforeAdaptation)
            {
                if (IsMt(link.OriginType)) CountEngine(count, link.OriginSystem);
                else kept.Add(link);
            }

            // Relink from the innermost outwards, so it comes out right whether
            // the setter keeps the object or stores a copy of it.
            for (int i = kept.Count - 1; i >= 0; i--)
                kept[i].OriginBeforeAdaptation = i + 1 < kept.Count ? kept[i + 1] : null;
            origin.OriginBeforeAdaptation = kept.Count > 0 ? kept[0] : null;

            props.TranslationOrigin = origin;
            return true;
        }

        private static void CountEngine(MtMarkCount count, string engine)
        {
            var key = string.IsNullOrWhiteSpace(engine) ? UnnamedEngine : engine;
            count.Engines.TryGetValue(key, out var n);
            count.Engines[key] = n + 1;
        }
    }
}

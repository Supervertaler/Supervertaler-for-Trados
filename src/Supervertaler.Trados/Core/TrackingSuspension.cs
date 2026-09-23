using System;
using System.Reflection;
using Sdl.TranslationStudioAutomation.IntegrationApi;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Reads, and briefly suspends, the editor's Track Changes setting (issue
    /// #138).
    ///
    /// <para><b>Why suspend it.</b> <c>ProcessSegmentPair</c> replaces the whole
    /// segment, so with tracking on every write is recorded as one deletion of
    /// everything and one insertion of everything. To show a reviewer only what
    /// changed, the revisions are built by <see cref="TrackedTargetMerge"/> and
    /// tracking is switched off while Studio applies them - otherwise Studio
    /// would wrap our own revisions in its whole-segment one.</para>
    ///
    /// <para><b>This is Studio's own mechanism, not ours.</b> Studio does exactly
    /// this around its own writes - adding a comment, for one - through an
    /// internal class, <c>TrackChangesState</c>, which switches off tracking, TQA
    /// mode and revision protection together and restores all three. That class
    /// is used here by reflection rather than re-implemented, because it already
    /// knows which three flags matter and in what order to restore them. It is
    /// internal, so it cannot be referenced at compile time: Studio refuses to
    /// load a plugin that references a non-public assembly (#132).</para>
    ///
    /// <para><b>Every failure means "unknown", and unknown means today's
    /// behaviour.</b> If Studio renames the class, or the document has no
    /// side-by-side editor, nothing is switched and the write goes through as it
    /// always has. The one thing this must never do is leave tracking off, so
    /// restoring happens in <see cref="Dispose"/>, which the caller runs from a
    /// finally.</para>
    /// </summary>
    internal sealed class TrackingSuspension : IDisposable
    {
        private const string StateTypeName =
            "Sdl.TranslationStudioAutomation.IntegrationApi.Internal.TrackChangesState";

        private readonly object _state;
        private readonly MethodInfo _revert;
        private bool _restored;

        private TrackingSuspension(object state, MethodInfo revert)
        {
            _state = state;
            _revert = revert;
        }

        /// <summary>
        /// Whether Track Changes is on in the editor for this document: true,
        /// false, or null when it cannot be determined. Reads only; switches
        /// nothing.
        /// </summary>
        public static bool? IsTracking(IStudioDocument doc)
        {
            try
            {
                var state = CreateState(doc);
                if (state == null) return null;
                var field = state.GetType().GetField("_trackChanges",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                return field?.GetValue(state) as bool?;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Switches tracking off and returns the object whose Dispose switches it
        /// back, or null if it could not be switched - in which case the caller
        /// must not write as though it had been.
        /// </summary>
        public static TrackingSuspension TurnOff(IStudioDocument doc)
        {
            try
            {
                var state = CreateState(doc);
                if (state == null) return null;
                var type = state.GetType();
                var off = type.GetMethod("TurnOffTracking", BindingFlags.Instance | BindingFlags.NonPublic);
                var revert = type.GetMethod("RevertTrackingToOriginalState", BindingFlags.Instance | BindingFlags.NonPublic);
                // Both or neither: a way to switch off with no way back is the
                // one outcome this class exists to prevent.
                if (off == null || revert == null) return null;
                off.Invoke(state, null);
                return new TrackingSuspension(state, revert);
            }
            catch (Exception ex)
            {
                Log("could not suspend tracking: " + Inner(ex).Message);
                return null;
            }
        }

        public void Dispose()
        {
            if (_restored) return;
            _restored = true;
            try
            {
                _revert.Invoke(_state, null);
            }
            catch (Exception ex)
            {
                // Loud, because this leaves the translator's editor with Track
                // Changes off and nothing on screen saying so.
                Log("FAILED TO RESTORE TRACK CHANGES: " + Inner(ex).Message);
            }
        }

        /// <summary>
        /// A TrackChangesState for the document's editor. Its constructor records
        /// the current settings, which is what both reading and restoring rely
        /// on, so a fresh one is made per write rather than cached across writes
        /// the translator may have toggled in between.
        /// </summary>
        private static object CreateState(IStudioDocument doc)
        {
            if (doc == null) return null;
            var docType = doc.GetType();
            var internalProp = docType.GetProperty("InternalDocument",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var translatable = internalProp?.GetValue(doc);
            if (translatable == null) return null;

            var stateType = docType.Assembly.GetType(StateTypeName, false);
            if (stateType == null) return null;

            // The constructor throws when the active view is not the side-by-side
            // editor; that is "unknown", caught by the callers.
            return Activator.CreateInstance(stateType,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { translatable }, null);
        }

        /// <summary>
        /// The name Studio itself puts on revisions and comments made in this
        /// editor, so ours carry the same author as the translator's own. Falls
        /// back to the Windows account name, which is what Studio defaults to.
        /// </summary>
        public static string EditingUser(IStudioDocument doc)
        {
            try
            {
                var field = doc?.GetType().GetField("_sideBySideView",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var view = field?.GetValue(doc);
                var prop = view?.GetType().GetProperty("DocumentEditingUser",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var user = prop?.GetValue(view) as string;
                if (!string.IsNullOrWhiteSpace(user)) return user;
            }
            catch { }
            return Environment.UserName;
        }

        private static Exception Inner(Exception ex) =>
            ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;

        private static void Log(string message)
        {
            try { DiagnosticLog.Log("TrackedWrite", message); } catch { }
        }
    }
}

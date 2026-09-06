using System;
using System.Threading;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Settings
{
    /// <summary>
    /// The single owner of <see cref="TermLensSettings"/>.
    ///
    /// <para><b>Why this exists.</b> <c>TermLensSettings.Save()</c> serialises the
    /// whole object, and five components each held their own long-lived copy —
    /// both ViewParts, the settings form and two dialogs. Whichever copy saved
    /// last silently reverted every field another had changed since it loaded.
    /// That is how a memory bank switched in the Assistant pane came back to its
    /// old value when Settings was opened from the TermLens pane: same file, two
    /// stale copies, last writer wins. See
    /// <c>docs/design/settings-single-source-of-truth.md</c>.</para>
    ///
    /// <para><b>Stage 1 of that plan: this type exists but nothing uses it yet.</b>
    /// It is deliberately additive so it cannot regress anything. Components move
    /// over in stage 2, starting with the two ViewParts that hold 19 of the 29
    /// save sites between them.</para>
    ///
    /// <para><b>Aliasing.</b> Once a component uses <see cref="Current"/> it no
    /// longer has a private copy: a mutation is live everywhere immediately,
    /// rather than at <see cref="Save"/>. Any code that mutates settings
    /// speculatively — to preview something, or before the user may still press
    /// Cancel — has to be found and changed before it is converted. That audit is
    /// the risky part of stage 2, not this class.</para>
    /// </summary>
    public static class SettingsService
    {
        private const string LogCategory = "Settings";

        private static readonly object _gate = new object();
        private static TermLensSettings _current;

        /// <summary>
        /// Raised after the settings have been saved or reloaded, so panes can
        /// refresh instead of being told by hand.
        ///
        /// <para><b>Not what the settings dialog uses.</b> This fires on every
        /// save, including the A+/A− font buttons; the post-dialog refresh forces
        /// a termbase reload that can take ~2 minutes on a cold Studio 2026
        /// cache, so it is called explicitly by <see cref="SettingsDialog"/>
        /// rather than triggered by any write that happens past. Use this event
        /// for refreshes cheap enough to run on an arbitrary save.</para>
        ///
        /// <para>Marshalled to the UI thread when one is known, because every
        /// subscriber is a WinForms surface and a bridge-thread save would
        /// otherwise touch controls from the wrong thread.</para>
        ///
        /// <para>Subscribers must not throw; a handler that does is logged and
        /// skipped so one bad listener cannot block the others or fail a save.</para>
        /// </summary>
        public static event EventHandler Changed;

        /// <summary>
        /// The one settings instance. Never null: a failed load yields defaults,
        /// because every caller of this today assumes a usable object and a null
        /// here would turn a corrupt settings file into a crash on start-up.
        /// </summary>
        public static TermLensSettings Current
        {
            get
            {
                var existing = Volatile.Read(ref _current);
                if (existing != null) return existing;

                lock (_gate)
                {
                    if (_current == null)
                    {
                        _current = LoadOrDefault();
                        DiagnosticLog.Log(LogCategory, "Settings loaded into the shared instance");
                        // #108: the shared key file takes over from the keys in this file.
                        // Fill it from them where it has none, once, so nothing is retyped.
                        try
                        {
                            var added = Supervertaler.Core.ApiKeyStore.MigrateFrom(_current.AiSettings?.ApiKeys);
                            if (added > 0)
                                DiagnosticLog.Log(LogCategory, $"Copied {added} API key(s) into {Supervertaler.Core.ApiKeyStore.FilePath}");
                        }
                        catch { /* a key file that cannot be written is a nuisance, not a reason to fail start-up */ }
                    }
                    return _current;
                }
            }
        }

        /// <summary>
        /// Persists <see cref="Current"/> and raises <see cref="Changed"/>.
        ///
        /// <para>Serialised against other saves and against <see cref="Reload"/>:
        /// the MCP bridge reads settings on HttpListener threads while the UI
        /// writes on the UI thread, and one shared instance makes that overlap
        /// real where private copies hid it.</para>
        /// </summary>
        public static void Save()
        {
            lock (_gate)
            {
                var settings = _current ?? (_current = LoadOrDefault());
                settings.Save();
            }
            RaiseChanged();
        }

        /// <summary>
        /// Re-reads the file into the shared instance and raises
        /// <see cref="Changed"/>. For the case where something outside the plugin
        /// edited <c>settings.json</c> — the user with a text editor, or the
        /// Workbench, which shares this file.
        /// </summary>
        public static void Reload()
        {
            lock (_gate)
            {
                _current = LoadOrDefault();
                DiagnosticLog.Log(LogCategory, "Settings reloaded from disk");
            }
            RaiseChanged();
        }

        /// <summary>
        /// Runs <paramref name="mutate"/> against the shared instance and saves,
        /// under one lock. Preferred over <c>Current.X = y; Save();</c> because
        /// that pair can interleave with another writer between the two
        /// statements — the exact shape of the defect this class exists to close.
        /// </summary>
        public static void Update(Action<TermLensSettings> mutate)
        {
            if (mutate == null) return;
            lock (_gate)
            {
                var settings = _current ?? (_current = LoadOrDefault());
                mutate(settings);
                settings.Save();
            }
            RaiseChanged();
        }

        /// <summary>
        /// Like <see cref="Update"/>, but <paramref name="mutate"/> returns
        /// whether it actually changed anything, and the save is skipped when it
        /// did not.
        ///
        /// <para>For the read-then-maybe-write sites — "generate an id if there
        /// isn't one", "apply defaults if this termbase has none". Doing that as
        /// a load, a test and a conditional save leaves a window in which another
        /// writer can act between the test and the write; here the whole
        /// decision happens under the one lock, so two callers cannot both
        /// conclude that the id is missing and generate a different one each.</para>
        /// </summary>
        /// <returns>Whether anything was saved.</returns>
        public static bool UpdateIf(Func<TermLensSettings, bool> mutate)
        {
            if (mutate == null) return false;
            bool changed;
            lock (_gate)
            {
                var settings = _current ?? (_current = LoadOrDefault());
                changed = mutate(settings);
                if (changed) settings.Save();
            }
            if (changed) RaiseChanged();
            return changed;
        }

        private static TermLensSettings LoadOrDefault()
        {
            // The one sanctioned call. Load() is [Obsolete] precisely so that any
            // OTHER call becomes a build warning naming this class instead.
#pragma warning disable 618
            try { return TermLensSettings.Load() ?? new TermLensSettings(); }
#pragma warning restore 618
            catch (Exception ex)
            {
                DiagnosticLog.Log(LogCategory, "Could not load settings, using defaults: " + ex.Message);
                return new TermLensSettings();
            }
        }

        private static void RaiseChanged()
        {
            var handler = Changed;
            if (handler == null) return;

            Action fire = () =>
            {
                foreach (var d in handler.GetInvocationList())
                {
                    try { ((EventHandler)d)(null, EventArgs.Empty); }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Log(LogCategory,
                            "A settings-changed handler threw and was skipped: " + ex.Message);
                    }
                }
            };

            try
            {
                // UiThread.Invoke already runs inline when marshalling is not
                // required or no marshaller exists, so no guard is needed here.
                //
                // It is BLOCKING. Harmless today: every save originates on the UI
                // thread, so this is a direct call. If a save is ever triggered
                // from a bridge (HttpListener) thread, this becomes a cross-thread
                // wait on the UI, and should be made fire-and-forget first —
                // subscribers only refresh themselves, so nothing needs the
                // handlers to have finished.
                UiThread.Invoke(fire);
            }
            catch (Exception ex)
            {
                // Never let notification failure fail the save that triggered it.
                DiagnosticLog.Log(LogCategory, "Could not raise Changed: " + ex.Message);
            }
        }
    }
}

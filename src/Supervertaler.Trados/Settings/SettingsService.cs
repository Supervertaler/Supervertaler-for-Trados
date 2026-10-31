using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
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
        public static TermLensSettings Current => EnsureLoaded();

        /// <summary>
        /// The shared instance, loaded by the session's first call of any entry
        /// point - <see cref="Current"/>, <see cref="Save"/>, <see cref="Update"/>,
        /// <see cref="UpdateIf"/> or <see cref="Reload"/>. Whichever comes first
        /// also runs the once-per-session key sync: in Studio that is
        /// AppInitializer's UpdateIf, so a first load inside Current alone never
        /// ran there (found in the step-1 live check, 31 Oct 2026; the #108
        /// migration had been skipped the same way since it shipped).
        /// </summary>
        private static TermLensSettings EnsureLoaded()
        {
            var existing = Volatile.Read(ref _current);
            if (existing != null) return existing;

            AiApiKeys ownKeys = null;
            List<(string Name, string Key)> profileKeys = null;
            lock (_gate)
            {
                if (_current == null)
                {
                    _current = LoadOrDefault();
                    DiagnosticLog.Log(LogCategory, "Settings loaded into the shared instance");
                    ownKeys = _current.AiSettings?.ApiKeys ?? new AiApiKeys();
                    profileKeys = TradosApiKeys.ProfileKeys(_current.AiSettings);
                }
                existing = _current;
            }

            // Once per session, with the settings just loaded: the shared key file
            // is filled from this file's keys where it has none (#108), and the keys
            // are locked to this computer (step 1 of the API-key lock). Outside the
            // lock: it reads and writes two files, waiting up to a second for
            // another product holding one, and the bridge threads reading settings
            // need not wait for that. Never throws.
            if (ownKeys != null)
                TradosApiKeys.SyncAtStartup(ownKeys, profileKeys);
            return existing;
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
            EnsureLoaded();
            lock (_gate)
            {
                _current.Save();
            }
            RaiseChanged();
        }

        /// <summary>
        /// Re-reads the file into the shared instance and raises
        /// <see cref="Changed"/>. For the case where something outside the plugin
        /// edited <c>settings.json</c> — the user with a text editor, or the
        /// Workbench, which shares this file.
        ///
        /// <para>If nothing has loaded settings yet this session, the first load
        /// and its key sync happen first. A reload does not sync the keys again:
        /// in step 1 the plain copies decide what is read, and the next start
        /// locks whatever changed.</para>
        /// </summary>
        public static void Reload()
        {
            EnsureLoaded();
            lock (_gate)
            {
#pragma warning disable 618
                var loaded = TermLensSettings.TryLoad(out var problem);
#pragma warning restore 618
                if (loaded == null)
                {
                    // Keep what is in memory: it was read fine, the file on disk
                    // is what cannot be read now.
                    DiagnosticLog.WriteAlways(LogCategory, "Settings not reloaded, the file cannot be read: " + problem);
                    return;
                }
                _current = loaded;
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
            EnsureLoaded();
            lock (_gate)
            {
                var settings = _current;
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
            EnsureLoaded();
            lock (_gate)
            {
                var settings = _current;
                changed = mutate(settings);
                if (changed) settings.Save();
            }
            if (changed) RaiseChanged();
            return changed;
        }

        private static TermLensSettings LoadOrDefault()
        {
            // The one sanctioned call. TryLoad() is [Obsolete] precisely so that any
            // OTHER call becomes a build warning naming this class instead.
#pragma warning disable 618
            var loaded = TermLensSettings.TryLoad(out var problem);
#pragma warning restore 618
            if (loaded != null) return loaded;

            // The file is there but cannot be read. Defaults are used - every
            // caller needs a usable object - but they are never saved over it
            // unseen: a copy is kept first, and if no copy can be kept, this
            // session does not save settings at all.
            var defaults = new TermLensSettings();
            var kept = TermLensSettings.KeepUnreadableCopy();
            if (kept == null)
                defaults.SaveRefused = "the settings file could not be read when Trados Studio started, " +
                                       "and no copy of it could be kept, so it is not overwritten";
            DiagnosticLog.WriteAlways(LogCategory, "Settings file unreadable (" + problem + "); " +
                (kept != null ? "copy kept at " + kept : "no copy could be kept, so settings are not saved this session"));

            Warn("settings-unreadable",
                "Supervertaler could not read its settings file, so it has started with default settings.\n\n" +
                (kept != null
                    ? "A copy of the file as it was has been kept:\n" + kept
                    : "To protect the file, settings will not be saved until Trados Studio is restarted:\n" + TermLensSettings.SettingsFilePath) +
                "\n\nReason: " + problem);
            return defaults;
        }

        private static int _saveFailureShown;

        /// <summary>
        /// A save that did not happen: always logged, and shown to the user the
        /// first time in a session. Once is enough - a failing save tends to fail
        /// again on every change, and a dialog per font-size click helps nobody.
        /// </summary>
        internal static void ReportSaveFailure(string why)
        {
            DiagnosticLog.WriteAlways(LogCategory, "Settings not saved: " + why);
            if (Interlocked.Exchange(ref _saveFailureShown, 1) == 1) return;
            Warn("settings-not-saved",
                "Supervertaler could not save its settings:\n" + why + "\n\n" +
                "Your changes apply until Trados Studio closes, but will be lost then. " +
                "This is shown once; any further failures go to the diagnostic log.");
        }

        private static void Warn(string name, string text)
        {
            StartupNotices.Enqueue(null, name, owner =>
                MessageBox.Show(owner, text, "Supervertaler – settings", MessageBoxButtons.OK, MessageBoxIcon.Warning));
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

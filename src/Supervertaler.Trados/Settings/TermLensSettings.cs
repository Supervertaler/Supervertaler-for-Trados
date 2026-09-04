using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Settings
{
    /// <summary>
    /// Persisted settings for the Supervertaler for Trados plugin.
    /// Stored at %LocalAppData%\Supervertaler.Trados\settings.json.
    /// </summary>
    [DataContract]
    public class TermLensSettings
    {
        /// <summary>
        /// Full path to the settings JSON file on disk.
        /// Resolved through UserDataPath so it moves with the shared data folder.
        /// </summary>
        public static string SettingsFilePath => UserDataPath.SettingsFilePath;

        // Old settings path for auto-migration from TermLens
        private static readonly string OldSettingsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TermLens", "settings.json");

        [DataMember(Name = "termbasePath")]
        public string TermbasePath { get; set; } = "";

        [DataMember(Name = "autoLoadOnStartup")]
        public bool AutoLoadOnStartup { get; set; } = true;

        /// <summary>
        /// Opt-in diagnostic logging. When true, the plugin writes a detailed debug
        /// trace to <see cref="Core.DiagnosticLog.LogFilePath"/> for troubleshooting.
        /// Off by default; mirrored into <see cref="Core.DiagnosticLog.Enabled"/> on load.
        /// </summary>
        [DataMember(Name = "diagnosticLogging")]
        public bool DiagnosticLogging { get; set; } = false;

        /// <summary>
        /// IDs of termbases the user has disabled. Empty means all termbases are active.
        /// Stored as disabled-list so newly added termbases are active by default.
        /// </summary>
        [DataMember(Name = "disabledTermbaseIds")]
        public List<long> DisabledTermbaseIds { get; set; } = new List<long>();

        /// <summary>
        /// DEPRECATED – kept for backward-compatible migration from settings that
        /// stored a single write target.  New code should use <see cref="WriteTermbaseIds"/>.
        /// </summary>
        [DataMember(Name = "writeTermbaseId")]
        public long WriteTermbaseId { get; set; } = -1;

        /// <summary>
        /// IDs of termbases that receive new terms via the Add Term / Quick-Add Term actions.
        /// Multiple termbases can be marked as Write targets – a new term is inserted into all of them.
        /// Empty list means no write termbases are configured.
        /// </summary>
        [DataMember(Name = "writeTermbaseIds")]
        public List<long> WriteTermbaseIds { get; set; } = new List<long>();

        /// <summary>
        /// Names of termbases for which the user has explicitly confirmed
        /// Write/Project assignment despite the termbase's declared language
        /// pair not matching the active project (i.e.
        /// <see cref="Core.LanguageUtils.TermbaseDirection.Unrelated"/>).
        ///
        /// Keyed by termbase name rather than ID because the underlying
        /// SQLite schema declares <c>name TEXT NOT NULL UNIQUE</c>, so names
        /// are stable across database rebuilds in a way IDs aren't. With
        /// <c>INTEGER PRIMARY KEY AUTOINCREMENT</c> ID reuse within one
        /// database is impossible, but a user who wipes and recreates
        /// <c>supervertaler.db</c> could end up with stale ID-based
        /// confirmations applying to different termbases. Names sidestep
        /// that – the only re-ask trigger is a deliberate rename.
        ///
        /// The confirm dialog in Settings → Termbases adds the name here on
        /// "Yes, add anyway"; unticking the box removes it so a re-tick
        /// re-asks. Empty / missing means no overrides have been confirmed.
        /// </summary>
        [DataMember(Name = "confirmedNonMatchingWriteTermbaseNames")]
        public List<string> ConfirmedNonMatchingWriteTermbaseNames { get; set; } = new List<string>();

        /// <summary>
        /// ID of the termbase the user has marked as the "Project" termbase.
        /// The project termbase is shown in pink; all others in blue.
        /// -1 means no project termbase is configured.
        /// </summary>
        [DataMember(Name = "projectTermbaseId")]
        public long ProjectTermbaseId { get; set; } = -1;

        /// <summary>
        /// Synthetic IDs of MultiTerm termbases the user has disabled (negative numbers).
        /// Empty means all detected MultiTerm termbases are active.
        /// </summary>
        [DataMember(Name = "disabledMultiTermIds")]
        public List<long> DisabledMultiTermIds { get; set; } = new List<long>();

        // ─── TermPicker layout persistence ────────────────────────────
        [DataMember(Name = "termPickerWidth")]
        public int TermPickerWidth { get; set; }

        [DataMember(Name = "termPickerHeight")]
        public int TermPickerHeight { get; set; }

        [DataMember(Name = "termPickerColumnWidths")]
        public List<int> TermPickerColumnWidths { get; set; } = new List<int>();

        // ─── Settings form layout persistence ─────────────────────────
        [DataMember(Name = "settingsFormWidth")]
        public int SettingsFormWidth { get; set; }

        [DataMember(Name = "settingsFormHeight")]
        public int SettingsFormHeight { get; set; }

        // ─── Termbase Editor dialog layout persistence ──────────────
        [DataMember(Name = "termbaseEditorWidth")]
        public int TermbaseEditorWidth { get; set; }

        [DataMember(Name = "termbaseEditorHeight")]
        public int TermbaseEditorHeight { get; set; }

        // ─── Panel font size ─────────────────────────────────────────
        /// <summary>
        /// Font size (in points) for the TermLens panel. Default: 9pt.
        /// Adjustable via the A+/A- buttons in the panel header or the Settings dialog.
        /// </summary>
        [DataMember(Name = "panelFontSize")]
        public float PanelFontSize { get; set; } = 9f;

        /// <summary>
        /// Font size (in points) for the AI Assistant chat bubbles. Default: 9pt.
        /// Adjustable via the A+/A- buttons in the chat header.
        /// </summary>
        [DataMember(Name = "chatFontSize")]
        public float ChatFontSize { get; set; } = 9f;

        // ─── UI scale factor ──────────────────────────────────────────
        /// <summary>
        /// Global UI scale factor for all Supervertaler controls. Default: 1.0 (100%).
        /// Applied on top of Windows DPI scaling. Requires Trados restart to take full effect.
        /// </summary>
        [DataMember(Name = "uiScaleFactor")]
        public float UiScaleFactor { get; set; } = 1.0f;

        // ─── SuperSearch docking ──────────────────────────────────────
        /// <summary>
        /// When true, SuperSearch is hosted as a tab inside the Supervertaler
        /// Assistant panel instead of its own dockable ViewPart. Requires a
        /// Trados restart to take effect (the control can only have one host).
        /// Default: false (standalone ViewPart).
        /// </summary>
        [DataMember(Name = "superSearchInAssistantTab")]
        public bool SuperSearchInAssistantTab { get; set; } = false;

        /// <summary>
        /// SuperSearch search source: "ProjectFiles" (SDLXLIFF files only),
        /// "FilesAndTms" (files + project translation memories), or "TmsOnly"
        /// (concordance — project TMs only). Persisted until the user changes
        /// it. Default: "FilesAndTms" so a fresh install searches BOTH the
        /// project files and the TMs (incl. GroupShare) out of the box —
        /// otherwise TM/GroupShare hits silently never appear and users assume
        /// the TM search is broken. Stored as a string for forward
        /// compatibility with future modes.
        /// </summary>
        [DataMember(Name = "superSearchMode")]
        public string SuperSearchMode { get; set; } = "FilesAndTms";

        // ─── SuperSearch: web resources ─────────────────────────────────
        /// <summary>
        /// The user's web resource list — built-ins plus any they added, with
        /// their enabled state and ordering. Never read directly: go through
        /// <see cref="GetWebResources"/>, which reconciles this against the
        /// built-ins shipped by the current build.
        /// <para>The element shape is deliberately identical to the standalone
        /// SuperLookup app's <c>superlookup-searches.json</c>, so a list can be
        /// exported from one product and imported into the other unchanged.</para>
        /// </summary>
        [DataMember(Name = "webResources")]
        public List<WebResource> WebResources { get; set; } = new List<WebResource>();

        /// <summary>
        /// The <see cref="WebResourceCatalog.DefaultsRevision"/> in force when
        /// <see cref="WebResources"/> was last written. When it lags behind the
        /// current build, <see cref="GetWebResources"/> refreshes each built-in's
        /// URL/name/icon from the shipped defaults while keeping the user's
        /// on/off choices — so a site that changes its URL scheme is fixed by an
        /// update instead of staying broken forever.
        /// </summary>
        [DataMember(Name = "webResourcesRevision")]
        public int WebResourcesRevision { get; set; } = 0;

        /// <summary>
        /// Where web results are rendered: "Embedded" (WebView2 tabs inside the
        /// SuperSearch pane) or "Browser" (one new window in the user's default
        /// browser). Default: "Embedded". Falls back to "Browser" at runtime when
        /// the WebView2 Runtime is missing — some users also prefer Browser
        /// permanently, for their own ad blocker and their logged-in sessions.
        /// Stored as a string for forward compatibility with future modes.
        /// </summary>
        [DataMember(Name = "webResultsMode")]
        public string WebResultsMode { get; set; } = "Embedded";

        /// <summary>True when web results should open in the user's browser
        /// rather than in the SuperSearch pane.</summary>
        public bool WebResultsInBrowser
        {
            get { return string.Equals(WebResultsMode, "Browser", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// The web resource list to actually use: the stored list reconciled
        /// against the built-ins of the running build. Cheap enough to call per
        /// search, but callers that hold it should re-fetch after a settings save.
        /// </summary>
        public List<WebResource> GetWebResources()
        {
            return WebResourceCatalog.Merge(WebResources, WebResourcesRevision);
        }

        /// <summary>
        /// Stores a reconciled list and stamps it with the current defaults
        /// revision. Call this rather than assigning <see cref="WebResources"/>
        /// directly, or the next load will re-run the refresh pass.
        /// </summary>
        public void SetWebResources(List<WebResource> resources)
        {
            WebResources = resources ?? new List<WebResource>();
            WebResourcesRevision = WebResourceCatalog.DefaultsRevision;
        }

        // ─── Term shortcut style ────────────────────────────────────────
        /// <summary>
        /// How Alt+digit shortcuts work for terms beyond 9.
        /// "sequential" = type Alt+4,5 for term 45 (timer-based, clean badges).
        /// "repeated"   = type Alt+5,5 for term 14 (no timer ambiguity, repeated-digit badges).
        /// Default: sequential.
        /// </summary>
        [DataMember(Name = "termShortcutStyle")]
        public string TermShortcutStyle { get; set; } = "sequential";

        /// <summary>
        /// Delay in milliseconds for the sequential chord timer.
        /// After the first digit, the system waits this long for a second digit
        /// before inserting the single-digit term. Default: 1100ms.
        /// Only applies when TermShortcutStyle is "sequential".
        /// </summary>
        [DataMember(Name = "chordDelayMs")]
        public int ChordDelayMs { get; set; } = 1100;

        // ─── Case sensitivity ────────────────────────────────────────
        /// <summary>
        /// Global default for case-sensitive term matching.
        /// When true, terms only match if the source text has the same case as the indexed term.
        /// Individual termbases can override this via their own case_sensitive setting.
        /// Default: false (case-insensitive, matching current behaviour).
        /// </summary>
        [DataMember(Name = "caseSensitiveMatching")]
        public bool CaseSensitiveMatching { get; set; } = false;

        /// <summary>
        /// Backing field for <see cref="AdaptTermCasing"/>. Nullable because
        /// DataContractJsonSerializer skips field initialisers: a settings file
        /// from before this feature has no key, deserializes as null, and the
        /// property getter turns that into the default (true). A plain bool
        /// would silently come back false for every existing user.
        /// </summary>
        [DataMember(Name = "adaptTermCasing")]
        private bool? _adaptTermCasing = true;

        /// <summary>
        /// Adapt the capitalisation of displayed/inserted target terms to the
        /// source occurrence in the segment: a term stored as "More preferably"
        /// shows and inserts as "more preferably" when the segment contains it
        /// lower-case mid-sentence, and vice versa for sentence-initial
        /// occurrences. Conservative rules in TermCaseAdapter (acronyms and
        /// mixed-case targets are never touched). Default: true.
        /// </summary>
        public bool AdaptTermCasing
        {
            get => _adaptTermCasing ?? true;
            set => _adaptTermCasing = value;
        }

        /// <summary>
        /// Column widths of the dockable TermPicker pane. Kept separate from
        /// TermPickerColumnWidths (the Alt+P popup): the pane is usually much
        /// narrower, so sharing one set of widths would fight the user.
        /// </summary>
        [DataMember(Name = "termPickerPaneColumnWidths")]
        public List<int> TermPickerPaneColumnWidths { get; set; }

        // ─── Voice commands ──────────────────────────────────────────
        /// <summary>
        /// Remembered position of the floating voice status strip (the
        /// fallback shown when the TermLens panel isn't open to host the
        /// indicator). 0/0 = never moved → auto bottom-right placement.
        /// </summary>
        [DataMember(Name = "voiceStripLeft")]
        public int VoiceStripLeft { get; set; }

        [DataMember(Name = "voiceStripTop")]
        public int VoiceStripTop { get; set; }

        // ─── Agglutinative / no-space language matching ──────────────
        /// <summary>
        /// Suffix-tolerant term matching for languages where grammatical
        /// particles attach directly to a noun with no intervening space
        /// (Korean, Japanese). When active, a term still matches when the
        /// segment token carries a trailing particle (값 ↦ 값으로,
        /// 제2 전압 값 ↦ 제2 전압 값으로), and the Add Term actions stop
        /// auto-expanding the selection to whitespace so the bare noun can be
        /// saved (장치, not 장치의).
        ///
        /// Three-state, stored as a string for forward compatibility:
        ///   "auto" (default) — on when the project source language is Korean
        ///                      or Japanese;
        ///   "on"             — always on (e.g. Chinese, or an agglutinative
        ///                      language not auto-detected);
        ///   "off"            — always off (strict whole-token matching).
        /// </summary>
        [DataMember(Name = "suffixTolerantMatching")]
        public string SuffixTolerantMatching { get; set; } = "auto";

        /// <summary>
        /// Resolves whether suffix-tolerant matching is active for a given
        /// source language, honouring the three-state <see cref="SuffixTolerantMatching"/>.
        /// </summary>
        public bool ResolveSuffixTolerant(string sourceLanguage)
        {
            switch ((SuffixTolerantMatching ?? "auto").Trim().ToLowerInvariant())
            {
                case "on": return true;
                case "off": return false;
                default: return IsAgglutinativeNoSpaceLanguage(sourceLanguage);
            }
        }

        /// <summary>
        /// True for languages whose script does not delimit words with spaces, so
        /// expanding a term selection to the whitespace token is wrong:
        ///   • Korean / Japanese — grammatical particles attach to the noun with
        ///     no space (값 ↦ 값으로); we keep the bare selection rather than
        ///     swallow the particle.
        ///   • Chinese — no inter-word spaces at all, so auto-expanding to
        ///     whitespace swallows the whole segment (the user selects
        ///     挂车控制模块 but 挂车控制模块的更换 gets saved). Chinese has no
        ///     word boundaries to expand to, so the exact selection is kept by
        ///     design — this is the correct behaviour, not a stopgap. A general
        ///     word segmenter is deliberately NOT used here: it would over-split
        ///     multi-character technical terms (挂车控制模块 → 挂车 / 控制 /
        ///     模块) and snap to the wrong span, whereas the user's own
        ///     character-precise selection is exactly the term they want.
        /// Accepts ISO codes (ko, ja, zh, ko-KR, ja-JP, zh-CN, zh-TW, zh-Hans …)
        /// and English display names ("Korean", "Japanese (Japan)",
        /// "Chinese (Simplified)").
        /// </summary>
        public static bool IsAgglutinativeNoSpaceLanguage(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) return false;
            var l = lang.Trim().ToLowerInvariant();
            return l.StartsWith("ko") || l.StartsWith("ja") || l.StartsWith("zh")
                || l.Contains("korean") || l.Contains("japanese") || l.Contains("chinese");
        }

        // ─── Update checker ──────────────────────────────────────────
        /// <summary>
        /// Version string the user chose to skip (e.g. "4.2.0-beta").
        /// The update dialog will not show again for this version.
        ///
        /// Retained so settings written before the snooze below keep working,
        /// and still honoured – but "Not now" sets
        /// <see cref="UpdateSnoozedUntilUtc"/> instead.
        /// </summary>
        [DataMember(Name = "skippedUpdateVersion")]
        public string SkippedUpdateVersion { get; set; } = "";

        /// <summary>
        /// Suppress ALL update prompts until this UTC time. Empty = not snoozed.
        ///
        /// Why a time window rather than a version: "skip this version" only
        /// silenced the exact build named in the dialog, so the next release
        /// prompted again. That was fine at one release a week, but submitting
        /// to the App Store after every meaningful fix would turn it into a
        /// near-daily dialog the user cannot quiet, because each day's build is
        /// a different version to skip. A window decouples the prompt from the
        /// release rate: the more often we ship, the LESS often they are
        /// interrupted.
        ///
        /// Stored as an ISO-8601 UTC string rather than a DateTime because
        /// DataContractJsonSerializer round-trips DateTime with a local-time
        /// bias, which has caused date bugs here before.
        /// </summary>
        [DataMember(Name = "updateSnoozedUntilUtc")]
        public string UpdateSnoozedUntilUtc { get; set; } = "";

        /// <summary>How long "Not now" silences update prompts.</summary>
        public const int UpdateSnoozeDays = 7;

        /// <summary>True while update prompts are snoozed.</summary>
        public bool IsUpdateSnoozed()
        {
            if (string.IsNullOrWhiteSpace(UpdateSnoozedUntilUtc)) return false;
            DateTime until;
            if (!DateTime.TryParse(UpdateSnoozedUntilUtc,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out until))
                return false;   // unparseable = treat as not snoozed
            return until.ToUniversalTime() > DateTime.UtcNow;
        }

        /// <summary>Silences update prompts for <see cref="UpdateSnoozeDays"/> days.</summary>
        public void SnoozeUpdatePrompts()
        {
            UpdateSnoozedUntilUtc = DateTime.UtcNow.AddDays(UpdateSnoozeDays)
                .ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ─── Usage statistics ──────────────────────────────────────
        /// <summary>
        /// Whether the user has opted in to anonymous usage statistics.
        /// Default: false (strictly opt-in). Can be changed at any time in Settings.
        /// </summary>
        [DataMember(Name = "usageStatisticsEnabled")]
        public bool UsageStatisticsEnabled { get; set; } = false;

        /// <summary>
        /// Random anonymous UUID generated on first opt-in.
        /// Not tied to any account, machine, or identity – purely random.
        /// </summary>
        [DataMember(Name = "usageStatisticsId")]
        public string UsageStatisticsId { get; set; } = "";

        /// <summary>
        /// Whether the user has already been asked about usage statistics.
        /// Once true, the opt-in dialog is not shown again (the user can
        /// still change the setting in Settings at any time).
        ///
        /// Legacy v1 flag - kept for backwards compatibility with old settings
        /// files but no longer checked. The opt-in dialog now uses
        /// UsageStatisticsAskedV2 so that users who saw the old "Yes, share?"
        /// framing get a second chance under the new "default-on, switch off
        /// here if you'd rather not" framing.
        /// </summary>
        [DataMember(Name = "usageStatisticsAsked")]
        public bool UsageStatisticsAsked { get; set; } = false;

        /// <summary>
        /// Whether the user has been asked about usage statistics under the
        /// rewritten dialog (v2: informational, default-on, opt-out). Defaults
        /// to false so every existing user sees the new dialog once after
        /// updating, regardless of what they answered to the old one.
        /// </summary>
        [DataMember(Name = "usageStatisticsAskedV2")]
        public bool UsageStatisticsAskedV2 { get; set; } = false;

        // ─── In-app survey (issue #43) ──────────────────────────────
        /// <summary>
        /// Survey ids the user has answered or dismissed ("Don't ask again", or
        /// shown the maximum number of times). A question in this list is never
        /// shown again.
        /// </summary>
        [DataMember(Name = "answeredSurveyIds")]
        public List<int> AnsweredSurveyIds { get; set; } = new List<int>();

        /// <summary>
        /// How many times each survey id has been shown without an answer, so an
        /// ignored question is re-asked at most a few startups rather than nagging
        /// forever. Keyed by survey id as a string (DataContractJsonSerializer
        /// dictionary keys must be strings).
        /// </summary>
        [DataMember(Name = "surveyShownCounts")]
        public Dictionary<string, int> SurveyShownCounts { get; set; } = new Dictionary<string, int>();

        // ─── In-app announcements ───────────────────────────────────
        /// <summary>
        /// String ids of one-way announcements (<see cref="Controls.AnnouncementDialog"/>)
        /// already shown to this user. Unlike surveys, an announcement has no
        /// server round-trip, no re-ask logic, and is shown exactly once: the id
        /// is recorded the moment the dialog is displayed, not on a particular
        /// button click, so closing via Esc/X still counts as shown.
        /// </summary>
        [DataMember(Name = "shownAnnouncementIds")]
        public List<string> ShownAnnouncementIds { get; set; } = new List<string>();

        // ─── AI settings ────────────────────────────────────────────
        /// <summary>
        /// AI provider configuration (API keys, provider selection, model selection).
        /// </summary>
        [DataMember(Name = "aiSettings")]
        public AiSettings AiSettings { get; set; } = new AiSettings();

        // ─── GroupShare server credentials ──────────────────────────
        /// <summary>
        /// Stored GroupShare server credentials for SuperSearch's server-based
        /// TM support (issue #35). Passwords are DPAPI-encrypted. Studio does not
        /// expose its own credential store to plugin code, so the user enters the
        /// login once here.
        /// </summary>
        [DataMember(Name = "groupShareServers")]
        public List<GroupShareServerCredential> GroupShareServers { get; set; }
            = new List<GroupShareServerCredential>();

        /// <summary>
        /// Loads settings from disk. Returns default settings if the file doesn't exist or can't be read.
        ///
        /// <para><b>Use <see cref="SettingsService.Current"/> instead.</b> This is
        /// the service's own way of filling the shared instance and has exactly
        /// one legitimate caller. Every other call returns a PRIVATE COPY of the
        /// same file, and <see cref="Save"/> serialises the whole object — so
        /// whoever saves such a copy last silently reverts every field anyone
        /// else changed since it was read. That was one defect with several
        /// faces: a memory bank that would not stay switched, a new prompt
        /// missing from the dropdown, settings reverting depending on which gear
        /// icon you opened them from. See
        /// <c>docs/design/settings-single-source-of-truth.md</c>.</para>
        ///
        /// <para>Marked obsolete rather than merely documented because a comment
        /// cannot stop the next person reaching for the obvious-looking method;
        /// a build warning naming the alternative can. Marked internal so the
        /// temptation at least stops at the assembly boundary.</para>
        /// </summary>
        [Obsolete("Use SettingsService.Current (or Update/UpdateIf to write). " +
                  "Load() returns a private copy, and saving one reverts every change " +
                  "another component made since it was read.")]
        internal static TermLensSettings Load()
        {
            try
            {
                var settingsFile = SettingsFilePath;
                var settingsDir  = Path.GetDirectoryName(settingsFile);

                // Auto-migrate from old TermLens settings location
                if (!File.Exists(settingsFile) && File.Exists(OldSettingsFile))
                {
                    Directory.CreateDirectory(settingsDir);
                    File.Copy(OldSettingsFile, settingsFile);
                }

                if (!File.Exists(settingsFile))
                    return new TermLensSettings();

                var json = File.ReadAllText(settingsFile, Encoding.UTF8);
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var loadSettings = new DataContractJsonSerializerSettings
                    {
                        UseSimpleDictionaryFormat = true
                    };
                    var serializer = new DataContractJsonSerializer(typeof(TermLensSettings), loadSettings);
                    var s = (TermLensSettings)serializer.ReadObject(stream);

                    // Migrate: old single WriteTermbaseId → new WriteTermbaseIds list
                    if ((s.WriteTermbaseIds == null || s.WriteTermbaseIds.Count == 0)
                        && s.WriteTermbaseId >= 0)
                    {
                        s.WriteTermbaseIds = new List<long> { s.WriteTermbaseId };
                        s.WriteTermbaseId = -1;
                    }

                    // Ensure list is never null
                    if (s.WriteTermbaseIds == null)
                        s.WriteTermbaseIds = new List<long>();
                    if (s.ConfirmedNonMatchingWriteTermbaseNames == null)
                        s.ConfirmedNonMatchingWriteTermbaseNames = new List<string>();
                    // Absent from any settings file written before web resources
                    // shipped. Left empty rather than seeded: GetWebResources()
                    // treats empty as "give me the defaults", so a pre-existing
                    // user gets the current built-ins on first use.
                    if (s.WebResources == null)
                        s.WebResources = new List<WebResource>();
                    if (string.IsNullOrWhiteSpace(s.WebResultsMode))
                        s.WebResultsMode = "Embedded";

                    // Migrate: chord delay missing from older settings (deserializes as 0)
                    if (s.ChordDelayMs <= 0)
                        s.ChordDelayMs = 1100;

                    // Ensure AI settings are never null (backward compat with older settings files)
                    if (s.AiSettings == null)
                        s.AiSettings = new AiSettings();
                    if (s.AiSettings.ApiKeys == null)
                        s.AiSettings.ApiKeys = new AiApiKeys();
                    if (s.AiSettings.CustomOpenAiProfiles == null)
                        s.AiSettings.CustomOpenAiProfiles = new List<CustomOpenAiProfile>();
                    if (s.AiSettings.DisabledAiTermbaseIds == null)
                        s.AiSettings.DisabledAiTermbaseIds = new List<long>();
                    if (s.AiSettings.EnabledAiMultiTermIds == null)
                        s.AiSettings.EnabledAiMultiTermIds = new List<long>();
                    if (s.GroupShareServers == null)
                        s.GroupShareServers = new List<GroupShareServerCredential>();

                    // Ensure prompt settings have safe defaults
                    if (s.AiSettings.SelectedPromptPath == null)
                        s.AiSettings.SelectedPromptPath = "";
                    // CustomSystemPrompt is intentionally nullable (null = use default)

                    // Ensure the active memory bank is populated. The OnDeserializing
                    // hook pre-seeds this, but belt-and-braces for callers who build
                    // AiSettings instances outside of the serializer.
                    if (string.IsNullOrWhiteSpace(s.AiSettings.ActiveMemoryBankName))
                        s.AiSettings.ActiveMemoryBankName = UserDataPath.DefaultMemoryBankName;

                    // Migrate: retired OpenAI models → GPT-5.4 Mini
                    var openAiModel = s.AiSettings.OpenAiModel;
                    if (openAiModel == "gpt-4.1" || openAiModel == "gpt-4.1-mini" ||
                        openAiModel == "o4-mini" || openAiModel == "gpt-4o" ||
                        openAiModel == "gpt-4o-mini")
                    {
                        s.AiSettings.OpenAiModel = "gpt-5.4-mini";
                    }

                    // Migrate: UI scale factor missing or invalid from older settings
                    if (s.UiScaleFactor <= 0f || s.UiScaleFactor > 3f)
                        s.UiScaleFactor = 1.0f;

                    // Migrate: chat font size missing from older settings (deserializes as 0)
                    if (s.ChatFontSize <= 0f)
                        s.ChatFontSize = 9f;

                    // Ensure update checker field is never null
                    if (s.SkippedUpdateVersion == null)
                        s.SkippedUpdateVersion = "";
                    if (s.UpdateSnoozedUntilUtc == null)
                        s.UpdateSnoozedUntilUtc = "";

                    // Suffix-tolerant matching missing from older settings → "auto"
                    if (string.IsNullOrWhiteSpace(s.SuffixTolerantMatching))
                        s.SuffixTolerantMatching = "auto";

                    // Ensure usage statistics ID is never null
                    if (s.UsageStatisticsId == null)
                        s.UsageStatisticsId = "";

                    // Ensure survey collections are never null
                    if (s.AnsweredSurveyIds == null)
                        s.AnsweredSurveyIds = new List<int>();
                    if (s.SurveyShownCounts == null)
                        s.SurveyShownCounts = new Dictionary<string, int>();
                    if (s.ShownAnnouncementIds == null)
                        s.ShownAnnouncementIds = new List<string>();

                    // Keep the global diagnostic-logging switch in sync with the
                    // persisted preference on every load.
                    DiagnosticLog.Enabled = s.DiagnosticLogging;

                    return s;
                }
            }
            catch (Exception ex)
            {
                // Log the failure to a sidecar file so a future regression
                // surfaces immediately instead of silently wiping the user's
                // saved settings (the exact failure mode of the v4.19.52 bug).
                try
                {
                    var dir = Path.GetDirectoryName(SettingsFilePath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                        var logPath = Path.Combine(dir, "settings-load-errors.log");
                        File.AppendAllText(logPath,
                            $"[{DateTime.Now:O}] {ex.GetType().FullName}: {ex.Message}\r\n{ex.StackTrace}\r\n\r\n");
                    }
                }
                catch { /* logging must never throw out of Load */ }
                return new TermLensSettings();
            }
        }

        // ─── Per-project overlay ─────────────────────────────────────

        /// <summary>
        /// Applies a project-specific settings overlay onto this global settings instance.
        /// Only copies the per-project fields (termbase path, enabled/disabled IDs, etc.).
        /// </summary>
        /// <param name="allTermbaseIds">
        /// Every Supervertaler termbase id currently in the database, when the
        /// caller has it. With it, a termbase the project file has never heard of
        /// is switched OFF in this project outright - deterministic, whatever the
        /// global state. Without it, unknown ids fall back to inheriting the global
        /// list, which is only as good as whatever the previous project left there.
        /// </param>
        public void ApplyProjectOverlay(ProjectSettings ps, IEnumerable<long> allTermbaseIds = null)
        {
            if (ps == null) return;

            TermbasePath = ps.TermbasePath ?? "";
            WriteTermbaseIds = ps.WriteTermbaseIds ?? new List<long>();
            ProjectTermbaseId = ps.ProjectTermbaseId;
            DisabledMultiTermIds = ps.DisabledMultiTermIds ?? new List<long>();

            // #103: the two opt-out lists are MERGED, not replaced. They list what is
            // switched OFF, so a termbase the project file has never heard of - one
            // created after the file was saved - was absent from it and therefore
            // ON, in every project opened afterwards, other clients' termbases
            // included. The file speaks for the ids it knows about; for anything
            // newer it defers to the global state, which since #62 is off for a
            // newly created termbase. Ids are monotonic, so "newer than anything
            // the file mentions" is the whole test - no new field, no migration.
            var knownMax = HighestKnownTermbaseId(ps);
            DisabledTermbaseIds = MergeOptOut(ps.DisabledTermbaseIds, DisabledTermbaseIds, knownMax);

            if (AiSettings != null && ps.DisabledAiTermbaseIds != null)
                AiSettings.DisabledAiTermbaseIds =
                    MergeOptOut(ps.DisabledAiTermbaseIds, AiSettings.DisabledAiTermbaseIds, knownMax);

            // #103, second step: with the full id list in hand, "unknown" is not left
            // to the global state at all. Every termbase above the ceiling is off in
            // this project, full stop - a project only reads, and only sends to the
            // model, what it was told about.
            if (allTermbaseIds != null)
            {
                AddUnknownAsOff(DisabledTermbaseIds, allTermbaseIds, knownMax);
                if (AiSettings?.DisabledAiTermbaseIds != null)
                    AddUnknownAsOff(AiSettings.DisabledAiTermbaseIds, allTermbaseIds, knownMax);
            }

            if (AiSettings != null && ps.EnabledAiMultiTermIds != null)
                AiSettings.EnabledAiMultiTermIds = ps.EnabledAiMultiTermIds;

            // Per-project active prompt (overrides global SelectedPromptPath)
            if (AiSettings != null && !string.IsNullOrEmpty(ps.ActivePromptPath))
                AiSettings.SelectedPromptPath = ps.ActivePromptPath;
        }

        /// <summary>
        /// The highest Supervertaler termbase id a project file mentions anywhere:
        /// either opt-out list, the write list, the project termbase. Everything the
        /// file knows about has an id at or below this; anything above it was created
        /// after the file was last saved. MultiTerm synthetic ids are negative and
        /// excluded, as is the -1 "none" sentinel. -1 when the file mentions nothing.
        /// </summary>
        private static long HighestKnownTermbaseId(ProjectSettings ps)
        {
            long max = -1;
            void Consider(IEnumerable<long> ids)
            {
                if (ids == null) return;
                foreach (var id in ids) if (id > max) max = id;
            }
            Consider(ps.DisabledTermbaseIds);
            Consider(ps.DisabledAiTermbaseIds);
            Consider(ps.WriteTermbaseIds);
            if (ps.ProjectTermbaseId > max) max = ps.ProjectTermbaseId;
            return max;
        }

        /// <summary>
        /// A project's opt-out list, plus every id from the global list that the
        /// project file cannot have known about (above <paramref name="knownMax"/>).
        /// Known ids keep the project's own setting, listed or not; unknown ones
        /// inherit the global state. Never null; never mutates its inputs.
        /// </summary>
        private static List<long> MergeOptOut(List<long> project, List<long> global, long knownMax)
        {
            var merged = new List<long>(project ?? new List<long>());
            if (global != null)
            {
                foreach (var id in global)
                    if (id > knownMax && !merged.Contains(id)) merged.Add(id);
            }
            return merged;
        }

        /// <summary>Adds every id above <paramref name="knownMax"/> to an opt-out list.</summary>
        private static void AddUnknownAsOff(List<long> optOut, IEnumerable<long> allIds, long knownMax)
        {
            foreach (var id in allIds)
                if (id > knownMax && !optOut.Contains(id)) optOut.Add(id);
        }

        /// <summary>
        /// Extracts the per-project fields from this settings instance into a
        /// ProjectSettings object suitable for saving.
        /// </summary>
        /// <summary>
        /// Reads back what is already stored for this project, so that the
        /// fields belonging to the JOB rather than to the installation can be
        /// carried forward instead of rebuilt. Null when there is nothing yet.
        /// </summary>
        private static ProjectSettings LoadExistingProjectSettings(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return null;
            try { return ProjectSettings.Load(projectPath); }
            catch { return null; }
        }

        public ProjectSettings ExtractProjectSettings(string projectPath = null, string projectName = null)
        {
            // Read once, carry the per-job fields across. See the note on them
            // below: everything NOT listed there is blanked by every save.
            var existing = LoadExistingProjectSettings(projectPath);

            return new ProjectSettings
            {
                ProjectPath = projectPath ?? "",
                ProjectName = projectName ?? "",
                TermbasePath = TermbasePath ?? "",
                WriteTermbaseIds = WriteTermbaseIds != null
                    ? new List<long>(WriteTermbaseIds) : new List<long>(),
                ProjectTermbaseId = ProjectTermbaseId,
                DisabledTermbaseIds = DisabledTermbaseIds != null
                    ? new List<long>(DisabledTermbaseIds) : new List<long>(),
                DisabledMultiTermIds = DisabledMultiTermIds != null
                    ? new List<long>(DisabledMultiTermIds) : new List<long>(),
                DisabledAiTermbaseIds = AiSettings?.DisabledAiTermbaseIds != null
                    ? new List<long>(AiSettings.DisabledAiTermbaseIds) : new List<long>(),
                EnabledAiMultiTermIds = AiSettings?.EnabledAiMultiTermIds != null
                    ? new List<long>(AiSettings.EnabledAiMultiTermIds) : new List<long>(),
                // Always true when extracted – the settings are considered initialised once
                // the plugin has loaded and applied them at least once.
                AiTermbaseIdsInitialized = true,
                ActivePromptPath = AiSettings?.SelectedPromptPath ?? "",

                // Carried forward, not derived — and every per-job field added
                // here in future must be too.
                //
                // Every other field above mirrors a global setting, so rebuilding
                // it from memory is right. These two have no global counterpart:
                // they belong to the JOB. Save() writes the whole object, so a
                // per-job field omitted here is not left alone, it is BLANKED —
                // by any unrelated save, silently, with the file's timestamp
                // updating as if all were well.
                //
                // Both have now been caught by this. ReferenceImagesFolder was
                // blanked whenever the user opened Settings and pressed OK.
                // MemoryBankName was blanked on every project switch, because
                // SaveCurrentProjectSettings() runs there — so choosing a bank
                // for a project and then leaving that project erased the choice
                // between the two actions.
                ReferenceImagesFolder = existing?.ReferenceImagesFolder ?? "",
                MemoryBankName = existing?.MemoryBankName ?? "",
            };
        }

        /// <summary>
        /// Saves settings to disk.
        /// </summary>
        public void Save()
        {
            try
            {
                var settingsFile = SettingsFilePath;
                var settingsDir  = Path.GetDirectoryName(settingsFile);
                Directory.CreateDirectory(settingsDir);

                MergeBackgroundOwnedFields();

                using (var stream = new MemoryStream())
                {
                    var settings = new DataContractJsonSerializerSettings
                    {
                        UseSimpleDictionaryFormat = true
                    };
                    var serializer = new DataContractJsonSerializer(typeof(TermLensSettings), settings);
                    serializer.WriteObject(stream, this);

                    // Pretty-print by re-parsing (DataContractJsonSerializer writes compact JSON)
                    var json = Encoding.UTF8.GetString(stream.ToArray());
                    File.WriteAllText(settingsFile, json, Encoding.UTF8);
                }
            }
            catch
            {
                // Silently ignore save failures
            }
        }

        /// <summary>
        /// Re-reads the fields that BACKGROUND tasks own and folds anything new
        /// on disk back into this instance before it is written.
        ///
        /// The whole settings file is serialised on every Save. This was written
        /// when several long-lived objects each held a copy loaded at startup, so
        /// a startup task recording "the user has now seen this" had its write
        /// silently undone minutes later by an unrelated save of a stale copy —
        /// observed with the SuperMemory announcement, which reappeared on some
        /// restarts and not others depending on what the user did in between.
        /// That was the five-copies defect, and it is another mitigation someone
        /// built for it before the cause was found.
        ///
        /// <b>It is still needed, for a different reason.</b> In-process staleness
        /// is gone — there is one shared instance now — but settings.json is also
        /// written by the Supervertaler Workbench, and a merge with what is
        /// actually on disk is the only thing that keeps an append-only record
        /// written by the other process from being dropped by ours.
        ///
        /// These fields are append-only records of things that HAPPENED, so a
        /// union with whatever is on disk is always the correct merge: an id
        /// present in either copy means the event occurred. Preferences are
        /// deliberately not merged - for those, last writer wins is right.
        /// </summary>
        private void MergeBackgroundOwnedFields()
        {
            try
            {
                if (!File.Exists(SettingsFilePath)) return;
                // A sanctioned Load(): this genuinely wants the bytes on disk,
                // not the shared instance, because the point is to find what
                // ANOTHER process wrote. SettingsService.Current would return
                // the very object we are about to serialise.
#pragma warning disable 618
                var onDisk = Load();
#pragma warning restore 618
                if (onDisk == null) return;

                if (onDisk.ShownAnnouncementIds != null)
                {
                    if (ShownAnnouncementIds == null)
                        ShownAnnouncementIds = new List<string>();
                    foreach (var id in onDisk.ShownAnnouncementIds)
                        if (!ShownAnnouncementIds.Contains(id))
                            ShownAnnouncementIds.Add(id);
                }

                if (onDisk.AnsweredSurveyIds != null)
                {
                    if (AnsweredSurveyIds == null)
                        AnsweredSurveyIds = new List<int>();
                    foreach (var id in onDisk.AnsweredSurveyIds)
                        if (!AnsweredSurveyIds.Contains(id))
                            AnsweredSurveyIds.Add(id);
                }

                if (onDisk.SurveyShownCounts != null)
                {
                    if (SurveyShownCounts == null)
                        SurveyShownCounts = new Dictionary<string, int>();
                    foreach (var kv in onDisk.SurveyShownCounts)
                    {
                        // The higher count is the truthful one: it means the
                        // dialog really was put in front of the user that often,
                        // and under-counting would show it past its cap.
                        int mine;
                        if (!SurveyShownCounts.TryGetValue(kv.Key, out mine) || kv.Value > mine)
                            SurveyShownCounts[kv.Key] = kv.Value;
                    }
                }
            }
            catch
            {
                // A failed merge must never block the save itself.
            }
        }

        /// <summary>
        /// Round-trips a default <see cref="TermLensSettings"/> through the same
        /// DataContractJsonSerializer pipeline that <see cref="Load"/> and
        /// <see cref="Save"/> use. Returns <c>null</c> on success or an error
        /// description on failure.
        ///
        /// Guards against the silent-data-loss class of bug introduced in
        /// v4.19.52 (commit 71af680), where adding a second
        /// <c>[OnDeserializing]</c> method to <see cref="AiSettings"/> caused
        /// <see cref="System.Runtime.Serialization.InvalidDataContractException"/>
        /// on every deserialize attempt – which <c>Load()</c>'s catch-all then
        /// swallowed, returning fresh defaults and making every saved setting
        /// vanish from the user's perspective. Plugin startup calls this and
        /// logs the result, so any future contract violation surfaces
        /// immediately in the plugin log instead of after users notice their
        /// settings have disappeared.
        /// </summary>
        public static string RunStartupSelfTest()
        {
            try
            {
                var sample = new TermLensSettings();
                var s = new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };
                var serializer = new DataContractJsonSerializer(typeof(TermLensSettings), s);
                using (var ms = new MemoryStream())
                {
                    serializer.WriteObject(ms, sample);
                    ms.Position = 0;
                    var roundTripped = serializer.ReadObject(ms);
                    if (roundTripped == null)
                        return "ReadObject returned null";
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().FullName + ": " + ex.Message;
            }
        }
    }
}

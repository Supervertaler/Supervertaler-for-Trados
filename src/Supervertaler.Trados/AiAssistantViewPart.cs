using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.ProjectAutomation.FileBased;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Licensing;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Dockable ViewPart for the Supervertaler Assistant.
    /// Hosts the AI Chat interface and Batch Translate tabs.
    /// Provides a conversational interface where translators can ask questions
    /// about translations, get suggestions, and apply them to the target segment.
    /// </summary>
    [ViewPart(
        Id = "AiAssistantViewPart",
        Name = "Supervertaler Assistant",
        Description = "AI-powered translation assistant with chat and batch translate",
        Icon = "TermLensIcon"
    )]
    [ViewPartLayout(typeof(EditorController), Dock = DockType.Right, Pinned = false)]
    public class AiAssistantViewPart : AbstractViewPartController
    {
        private static readonly Lazy<AiAssistantControl> _control =
            new Lazy<AiAssistantControl>(() => new AiAssistantControl());

        private static AiAssistantViewPart _currentInstance;

        private EditorController _editorController;
        private IStudioDocument _activeDocument;
        /// <summary>
        /// The shared settings instance. A property, not a field: this pane used
        /// to hold its own copy, and a copy is how a memory bank switched here
        /// came back to its old value when Settings was opened from TermLens —
        /// whichever stale copy saved last won. See
        /// docs/design/settings-single-source-of-truth.md.
        /// </summary>
        private TermLensSettings _settings => SettingsService.Current;

        // Cached language pair – ActiveFile can be null when the AI panel has focus
        private string _cachedSourceLang;
        private string _cachedTargetLang;

        // Chat state
        private readonly List<ChatMessage> _chatHistory = new List<ChatMessage>();
        private CancellationTokenSource _chatCts;
        private bool _userCancelled;

        // Batch translate state
        private BatchTranslator _batchTranslator;
        private CancellationTokenSource _batchCts;
        private BatchTranslationBackup _batchBackup;

        // Proofreading state
        private BatchProofreader _batchProofreader;
        private CancellationTokenSource _proofreadCts;
        private ProofreadingReport _currentReport;

        // Clipboard Mode state
        private List<BatchSegment> _clipboardSegments;

        // Prompt library
        private PromptLibrary _promptLibrary;

        // Memory-bank inbox watcher
        private FileSystemWatcher _inboxWatcher;
        private bool _fullyInitialized;

        // Localhost HTTP bridge for the Workbench Sidekick Chat. See
        // Core/SupervertalerBridge.cs for protocol details. Started at the end of
        // InitializeFullIfNeeded when the user has Assistant access AND the
        // hidden setting AiSettings.SidekickBridgeEnabled is true.
        private SupervertalerBridge _supervertalerBridge;

        // Memory-bank reader (lazy: created once, cached for the session).
        // Cached against _kbReaderBankName so that switching the active memory
        // bank at runtime (Step 5 toolbar dropdown) forces a fresh reader on the
        // next LoadKbContextForPrompt() call.
        private MemoryBankReader _kbReader;
        private string _kbReaderBankName;

        /// <summary>
        /// Resolves the on-disk path of the active memory bank for the current
        /// session. Reads <c>AiSettings.ActiveMemoryBankName</c> and falls back
        /// to <see cref="UserDataPath.DefaultMemoryBankName"/> when settings are
        /// not yet loaded or the field is blank.
        /// </summary>
        private string ActiveMemoryBankDir
        {
            get
            {
                var name = _settings?.AiSettings?.ActiveMemoryBankName;
                if (string.IsNullOrWhiteSpace(name))
                    name = UserDataPath.DefaultMemoryBankName;
                return UserDataPath.GetMemoryBankDir(name);
            }
        }

        /// <summary>
        /// Human-friendly label for the active memory bank, used in log/toast
        /// messages so translators can tell which bank they just acted on.
        /// </summary>
        /// <summary>Project the active bank was last aligned to, so the
        /// realignment runs once per project rather than on every document
        /// change within it.</summary>
        private string _bankProjectPath;

        private string ActiveMemoryBankName =>
            string.IsNullOrWhiteSpace(_settings?.AiSettings?.ActiveMemoryBankName)
                ? UserDataPath.DefaultMemoryBankName
                : _settings.AiSettings.ActiveMemoryBankName;

        protected override IUIControl GetContentControl()
        {
            return _control.Value;
        }

        private bool _initializeRan;

        /// <summary>Public entry point so the plugin can force initialization
        /// (and thus the bridge) without the user activating the pane – see
        /// AppInitializer.EnsureBridgeViewPartLoads. Trados only calls the
        /// protected Initialize() when the pane is shown; GetController returns
        /// an uninitialised controller. Idempotent via <see cref="_initializeRan"/>,
        /// so a later framework call is a no-op. Must be called on the UI thread.</summary>
        public void EnsureInitialized() => Initialize();

        protected override void Initialize()
        {
            if (_initializeRan) return;   // idempotent: framework OR forced call, whichever first
            _initializeRan = true;

            BridgeLog.Write("AiAssistantViewPart.Initialize() ENTERED");
            _currentInstance = this;

            // Regression guard for the v4.19.52 silent-data-loss bug. If the
            // DataContractJsonSerializer can't round-trip a default
            // TermLensSettings, Load() will swallow the exception and return
            // fresh defaults – making every saved setting vanish. Logging the
            // failure here surfaces it in bridge.log immediately instead of
            // after users notice their settings have disappeared.
            var selfTestError = TermLensSettings.RunStartupSelfTest();
            if (selfTestError != null)
            {
                BridgeLog.Write("CRITICAL: TermLensSettings.RunStartupSelfTest FAILED: " + selfTestError);
                BridgeLog.Write("CRITICAL: Saved settings will appear empty until this is fixed. Likely cause: duplicate [OnDeserializing]/[OnSerializing]/[OnDeserialized]/[OnSerialized] callback in a [DataContract] type.");
            }
            else
            {
                BridgeLog.Write("TermLensSettings.RunStartupSelfTest passed.");
            }

            var langSelfTestError = LanguageUtils.RunStartupSelfTest();
            if (langSelfTestError != null)
            {
                BridgeLog.Write("CRITICAL: LanguageUtils.RunStartupSelfTest FAILED: " + langSelfTestError);
                BridgeLog.Write("CRITICAL: Termbase language-direction logic is misclassifying inputs. Term lookups, writes, or merges may go to the wrong columns until this is fixed.");
            }
            else
            {
                BridgeLog.Write("LanguageUtils.RunStartupSelfTest passed.");
            }

            // License check – show/hide upgrade overlay based on tier.
            // When the user activates a licence mid-session (after Initialize
            // returned early due to no access), run the deferred full init so
            // event handlers, memory-bank dropdown, inbox watcher, etc. are
            // all wired up without requiring a Trados restart.
            LicenseManager.Instance.LicenseStateChanged += (s, e) =>
            {
                _control.Value.BeginInvoke(new Action(() =>
                {
                    if (LicenseManager.Instance.HasAssistantAccess)
                    {
                        _control.Value.HideUpgradeRequired();
                        InitializeFullIfNeeded();
                    }
                    else
                    {
                        _control.Value.ShowUpgradeRequired();
                    }
                }));
            };

            // Settings load themselves on first touch via SettingsService.
            // Kept as a statement so the gear button is still wired up below
            // even when unlicensed, letting users reach Settings → License.
            var _ = _settings;
            _promptLibrary = TermLensEditorViewPart.GetPromptLibrary() ?? new PromptLibrary();
            _promptLibrary.EnsureDefaultPrompts();
            _control.Value.SettingsRequested += OnSettingsRequested;

            // Live-sync the Batch Translate dropdown whenever the user toggles the
            // active prompt in the Prompt Manager, no matter which entry point
            // opened the Settings dialog (AI Assistant gear, termbase gear, etc.).
            // A static event avoids the per-instance wiring that previously missed
            // forms opened from TermLensEditorViewPart.
            Controls.PromptManagerPanel.ActivePromptChangedGlobal -= OnActivePromptChangedGlobal;
            Controls.PromptManagerPanel.ActivePromptChangedGlobal += OnActivePromptChangedGlobal;

            if (!LicenseManager.Instance.HasAssistantAccess)
            {
                BridgeLog.Write($"Initialize: HasAssistantAccess=false (tier={LicenseManager.Instance.CurrentTier}). Bridge will NOT start until license activates.");
                _control.Value.ShowUpgradeRequired();
                return;
            }

            BridgeLog.Write($"Initialize: HasAssistantAccess=true (tier={LicenseManager.Instance.CurrentTier}). Calling InitializeFullIfNeeded.");
            InitializeFullIfNeeded();
        }

        /// <summary>
        /// Performs the full initialisation that requires a valid licence:
        /// wires event handlers, populates the memory-bank dropdown, starts
        /// the inbox watcher, and restores chat history.  Guarded by
        /// <see cref="_fullyInitialized"/> so it runs at most once per
        /// ViewPart lifetime – either from <see cref="Initialize"/> (when
        /// licensed at startup) or from the <c>LicenseStateChanged</c>
        /// handler (when the user activates mid-session).
        /// </summary>
        private void InitializeFullIfNeeded()
        {
            if (_fullyInitialized) return;
            _fullyInitialized = true;

            _editorController = SdlTradosStudio.Application.GetController<EditorController>();
            if (_editorController != null)
            {
                _editorController.ActiveDocumentChanged += OnActiveDocumentChanged;

                if (_editorController.ActiveDocument != null)
                {
                    _activeDocument = _editorController.ActiveDocument;
                    _activeDocument.ActiveSegmentChanged += OnActiveSegmentChanged;
                    _activeDocument.DocumentFilterChanged += OnDocumentFilterChanged;
                    GetDocumentSourceLanguage();
                    GetDocumentTargetLanguage();
                }
            }

            // Wire chat control events
            _control.Value.SendRequested += OnSendRequested;
            _control.Value.ClearRequested += OnClearRequested;
            _control.Value.ApplyToTargetRequested += OnApplyToTargetRequested;
            _control.Value.SaveAsPromptRequested += OnSaveAsPromptRequested;
            _control.Value.SaveToMemoryBankRequested += OnSaveToMemoryBank;
            _control.Value.StopRequested += OnStopRequested;

            // Wire remaining buttons (SettingsRequested already wired above)
            _control.Value.ModelChangeRequested += OnModelChangeRequested;
            _control.Value.CustomProfilesSource = GetCustomProfileMenuItems;

            // Chat font size: restore persisted size and wire change handler
            _control.Value.SetChatFontSize(_settings.ChatFontSize);
            _control.Value.ChatFontSizeChanged += OnChatFontSizeChanged;

            // Wire batch translate control events
            var batchControl = _control.Value.BatchTranslateControl;
            batchControl.TranslateRequested += OnBatchTranslateRequested;
            batchControl.ProofreadRequested += OnProofreadRequested;
            batchControl.StopRequested += OnBatchStopRequested;
            batchControl.ScopeChanged += OnBatchScopeChanged;
            batchControl.OpenAiSettingsRequested += OnSettingsRequested;
            batchControl.BatchModeChanged += (s, e) => PopulateBatchPromptDropdown();
            batchControl.GeneratePromptRequested += OnGeneratePromptRequested;
            batchControl.OpenBackupFolderRequested += OnOpenBackupFolderRequested;
            batchControl.CopyToClipboardRequested += OnCopyToClipboardRequested;
            batchControl.PasteFromClipboardRequested += OnPasteFromClipboardRequested;
            batchControl.PreviewPromptRequested += OnPreviewPromptRequested;
            batchControl.ReferenceNumeralsRequested += OnReferenceNumeralsRequested;
            batchControl.DocumentImagesRequested += OnDocumentImagesRequested;
            batchControl.ReferenceImagesFolderRequested += OnReferenceImagesFolderRequested;
            batchControl.WriteFiguresFileRequested += OnWriteFiguresFileRequested;
            batchControl.ExtractImagesRequested += OnExtractImagesRequested;
            batchControl.AnalyseFiguresRequested += OnAnalyseFiguresRequested;
            batchControl.TranslateViaWorkbenchRequested += OnTranslateViaWorkbenchRequested;
            batchControl.ModelChangeRequested += OnModelChangeRequested;
            batchControl.CustomProfilesSource = GetCustomProfileMenuItems;

            // Wire reports control events
            var reportsControl = _control.Value.ReportsControl;
            reportsControl.NavigateToSegmentRequested += OnNavigateToSegment;
            reportsControl.ClearResultsRequested += OnClearReports;

            // Wire Import / Export control events (v4.20.7). Export collects
            // segments from the active document and writes them via the
            // Core.Export.* pipeline; import reads the sidecar manifest +
            // round-tripped file and applies diffs back via ProcessSegmentPair.
            var importExportControl = _control.Value.ImportExportControl;
            importExportControl.ExportRequested += OnBilingualExportRequested;
            importExportControl.ImportRequested += OnBilingualImportRequested;
            importExportControl.OpenFileRequested += OnImportExportOpenFile;
            importExportControl.OpenFolderRequested += OnImportExportOpenFolder;
            importExportControl.FileSelectionChanged += (s, e) => UpdateImportExportSegmentCount();

            // Optionally host SuperSearch as a 4th tab in this panel. The
            // SuperSearchController owns the control and all its logic; we just
            // re-parent the shared control into a tab here. The standalone
            // SuperSearchViewPart shows a placeholder when this mode is on.
            if (_settings.SuperSearchInAssistantTab)
            {
                _control.Value.EnsureSuperSearchTab(SuperSearchController.Shared.Control);
            }

            // Wire prompt logging
            LlmClient.PromptCompleted += OnPromptCompleted;

            // Wire tag-handler diagnostics to batch translate log
            SegmentTagHandler.DiagnosticMessage = msg =>
                SafeInvoke(() => _control.Value.BatchTranslateControl.AppendLog(msg, true));

            // Wire SuperMemory toolbar events
            _control.Value.ConvertLegacyBankRequested += OnConvertLegacyBank;
            _control.Value.OpenBankFolderRequested += OnOpenBankFolder;
            _control.Value.HarvestTrackedChangesRequested += OnHarvestTrackedChanges;
            _control.Value.OverviewRequested += OnOverview;
            _control.Value.SuperMemoryRefreshRequested += (s, e) => RefreshSuperMemoryInboxCount();
            _control.Value.MemoryBankChanged += OnMemoryBankChanged;
            _control.Value.NewMemoryBankRequested += OnNewMemoryBankRequested;

            // Initial context update
            UpdateContextDisplay();
            UpdateProviderDisplay();
            UpdateBatchProviderDisplay();
            UpdateBatchSegmentCounts();
            PopulateBatchPromptDropdown();
            RefreshMemoryBankDropdown();
            RefreshSuperMemoryInboxCount();
            StartInboxWatcher();
            StartSupervertalerBridge();

            // Check the already-active bank at start-up too. If the user has
            // a bank (e.g. one created before template bundling shipped, or
            // pre-existing from Step 5i) that is missing canonical template
            // files, offer to restore them now rather than waiting for the
            // user to click Process Inbox and see a confusing error.
            //
            // Deferred via BeginInvoke so the MessageBox does not block
            // Trados Studio's plugin-init message pump – without this, the
            // whole Studio UI would freeze until the user dismisses the
            // prompt at start-up.
            try
            {
                var activeName = ActiveMemoryBankName;
                if (_control.Value.IsHandleCreated)
                {
                    _control.Value.BeginInvoke(new Action(() =>
                    {
                        try { CheckAndOfferTemplateHealing(activeName); } catch { }
                    }));
                }
                else
                {
                    _control.Value.HandleCreated += (s, e) =>
                    {
                        _control.Value.BeginInvoke(new Action(() =>
                        {
                            try { CheckAndOfferTemplateHealing(activeName); } catch { }
                        }));
                    };
                }
            }
            catch { }

            // Restore persisted chat history
            LoadChatHistory();
        }

        // ─── Document / Segment Events ────────────────────────────

        private void OnActiveDocumentChanged(object sender, DocumentEventArgs e)
        {
            if (_activeDocument != null)
            {
                try { _activeDocument.ActiveSegmentChanged -= OnActiveSegmentChanged; }
                catch { }
                try { _activeDocument.DocumentFilterChanged -= OnDocumentFilterChanged; }
                catch { }
            }

            _activeDocument = _editorController?.ActiveDocument;
            _cachedSourceLang = null;
            _cachedTargetLang = null;

            if (_activeDocument != null)
            {
                _activeDocument.ActiveSegmentChanged += OnActiveSegmentChanged;
                _activeDocument.DocumentFilterChanged += OnDocumentFilterChanged;
                // Pre-cache language pair while ActiveFile is likely available
                GetDocumentSourceLanguage();
                GetDocumentTargetLanguage();
                SafeInvoke(UpdateContextDisplay);
                UpdateBatchSegmentCounts();
                PopulateBatchPromptDropdown();
                ApplyProjectMemoryBank();
            }
            else
            {
                SafeInvoke(() =>
                {
                    UpdateContextDisplay();
                    _control.Value.BatchTranslateControl.Reset();
                });
            }

            // Keep the bridge handshake's project/document name current, so a
            // client naming this instance to the user names the right project.
            try { _supervertalerBridge?.RefreshInstanceFile(); }
            catch { /* never let handshake bookkeeping break document switching */ }
        }

        private void OnActiveSegmentChanged(object sender, EventArgs e)
        {
            // Refresh language cache while ActiveFile is available
            GetDocumentSourceLanguage();
            GetDocumentTargetLanguage();
            SafeInvoke(UpdateContextDisplay);
        }

        private void OnDocumentFilterChanged(object sender, DocumentFilterEventArgs e)
        {
            UpdateBatchSegmentCounts();
        }

        private void UpdateContextDisplay()
        {
            // Strip inline-formatting tags from the source preview – ToString()
            // would emit e.g. `<cf bold=True>SEVT</cf>` which leaks Trados'
            // internal tag syntax into the chat header. SegmentTagHandler
            // .GetFinalText returns just the readable text. Same treatment
            // already applied to the target.
            var sourceText = _activeDocument?.ActiveSegmentPair?.Source != null
                ? SegmentTagHandler.GetFinalText(_activeDocument.ActiveSegmentPair.Source)
                : null;
            var targetText = _activeDocument?.ActiveSegmentPair?.Target != null
                ? SegmentTagHandler.GetFinalText(_activeDocument.ActiveSegmentPair.Target)
                : null;
            var matches = TermLensEditorViewPart.GetCurrentSegmentMatches();
            var langPair = BuildLangPairString();

            _control.Value.UpdateContextInfo(
                sourceText, targetText, matches.Count, langPair);
        }

        private void UpdateProviderDisplay()
        {
            var aiSettings = _settings?.AiSettings;
            if (aiSettings != null)
            {
                var provider = aiSettings.SelectedProvider ?? "openai";
                var model = aiSettings.GetSelectedModel() ?? "";
                _control.Value.UpdateProviderInfo(provider, model);
            }
        }

        /// <summary>
        /// Builds the "Custom (OpenAI-compatible)" submenu entries for the two
        /// provider menus (Batch Translate + chat status bar) from the current
        /// AI settings. Called lazily each time a menu opens, so profiles
        /// added or renamed in Settings appear without any refresh wiring.
        /// </summary>
        private List<Controls.CustomProfileMenuItem> GetCustomProfileMenuItems()
        {
            var items = new List<Controls.CustomProfileMenuItem>();
            try
            {
                var ai = _settings?.AiSettings;
                if (ai?.CustomOpenAiProfiles == null) return items;
                var activeName = ai.SelectedProvider == LlmModels.ProviderCustomOpenAi
                    ? (ai.GetActiveCustomProfile()?.Name ?? "")
                    : "";
                foreach (var p in ai.CustomOpenAiProfiles)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.Name)) continue;
                    items.Add(new Controls.CustomProfileMenuItem
                    {
                        Name = p.Name,
                        Model = p.Model,
                        IsActive = string.Equals(p.Name, activeName, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch { /* menu decoration only – never break the click */ }
            return items;
        }

        private void OnModelChangeRequested(string providerKey, string modelId)
        {
            SafeInvoke(() =>
            {
                var aiSettings = _settings?.AiSettings;
                if (aiSettings == null) return;

                aiSettings.SetProviderAndModel(providerKey, modelId);
                SettingsService.Save();

                UpdateProviderDisplay();
                UpdateBatchProviderDisplay();

                // Nothing to tell TermLens. This used to push AiSettings into
                // that panel's private copy, so its gear icon would not show a
                // provider the chat status bar had already moved off. There is
                // one instance now, so both are reading the same values.
            });
        }

        // ─── Chat font size ────────────────────────────────────────

        private void OnChatFontSizeChanged(object sender, EventArgs e)
        {
            _settings.ChatFontSize = _control.Value.ChatFontSize;
            SettingsService.Save();
        }

        // ─── Settings ───────────────────────────────────────────────

        private void OnSettingsRequested(object sender, EventArgs e)
        {
            // One entry point for every gear icon; the tab index is the only
            // thing this panel gets to decide. Refreshing both panels afterwards
            // is SettingsDialog's job, so it happens however Settings was opened.
            SafeInvoke(() =>
            {
                SettingsDialog.Show(_control.Value.FindForm(), _promptLibrary, defaultTab: 2);

                // Prompt deletions hit disk even on Cancel, so the dropdown is
                // rebuilt either way. (SettingsDialog refreshes the library
                // itself; this rebuilds the control that reads it.)
                PopulateBatchPromptDropdown();
            });
        }

        // ─── Chat Logic ───────────────────────────────────────────

        private void OnSendRequested(object sender, ChatSendEventArgs args)
        {
            var messageText = args.Text;
            var images = args.Images;
            var documents = args.Documents;

            if (string.IsNullOrWhiteSpace(messageText)
                && (images == null || images.Count == 0)
                && (documents == null || documents.Count == 0))
                return;

            // Prepend document content to the message text for the AI
            string displayText = args.DisplayText;
            if (documents != null && documents.Count > 0)
            {
                var docParts = new System.Text.StringBuilder();
                foreach (var doc in documents)
                {
                    docParts.AppendLine($"[Attached file: {doc.FileName}]");
                    docParts.AppendLine(doc.ExtractedText);
                    docParts.AppendLine();
                }

                // Build display summary (short) for the chat bubble
                var docNames = new List<string>();
                foreach (var doc in documents)
                    docNames.Add($"{doc.FileName} ({DocumentTextExtractor.FormatFileSize(doc.FileSize)})");

                var displaySummary = string.Join(", ", docNames);
                var userText = messageText ?? "";

                // Full text sent to AI: document content + user's message
                messageText = docParts.ToString() + userText;

                // Display text: show short summary instead of full extracted content
                if (string.IsNullOrEmpty(displayText))
                {
                    displayText = string.IsNullOrWhiteSpace(userText)
                        ? $"\U0001F4CE {displaySummary}"
                        : $"\U0001F4CE {displaySummary}\n\n{userText}";
                }
            }

            // 1. Add user message to history and display
            // ShowAsStatus = true means the message was system-initiated (e.g. Generate Prompt)
            // and should display as an assistant-styled bubble, even though it's sent as a user message
            var userMsg = new ChatMessage
            {
                Role = ChatRole.User,
                Content = messageText ?? "",
                DisplayContent = displayText,  // null = show full Content; set for {{PROJECT}} prompts
                Images = images,
                Documents = documents
            };
            _chatHistory.Add(userMsg);

            // For display, use assistant role if this is a system-initiated message
            var displayMsg = args.ShowAsStatus
                ? new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = messageText ?? "",
                    DisplayContent = displayText,
                    Images = images,
                    Documents = documents
                }
                : userMsg;
            _control.Value.AddMessage(displayMsg);
            SaveChatHistory();

            // 2. Gather current context
            // #97: as the model reads it - placeholders, never Studio markup.
            var sourceText = SegmentTagHandler.ToModelText(_activeDocument?.ActiveSegmentPair?.Source);
            // Strip Unicode line/paragraph separators (U+2028, U+2029).
            // These are used by InDesign (IDML) as forced line breaks and by some
            // PDF converters as layout artifacts. They're invisible in Trados but
            // cause the AI to introduce spurious line breaks in the translation.
            // The break position is a layout concern, not a linguistic one – it
            // almost never belongs in the same place in the target language.
            if (sourceText != null)
                sourceText = sourceText.Replace("\u2028", " ").Replace("\u2029", " ");
            var targetText = _activeDocument?.ActiveSegmentPair?.Target != null
                ? SegmentTagHandler.GetFinalText(_activeDocument.ActiveSegmentPair.Target)
                : null;
            var sourceLang = GetDocumentSourceLanguage();
            var targetLang = GetDocumentTargetLanguage();

            // Filter matched terms by AI-disabled termbase IDs
            var aiCfgChat = _settings?.AiSettings ?? new AiSettings();
            var allMatches = TermLensEditorViewPart.GetCurrentSegmentMatches();
            var matchedTerms = allMatches.Where(m => aiCfgChat.IsTermbaseAiEnabled(m.PrimaryEntry?.TermbaseId ?? 0)).ToList();

            // Gather TM matches if enabled
            List<TmMatch> tmMatches = null;
            if (_settings?.AiSettings?.IncludeTmMatches != false)
                tmMatches = DocumentContextHelper.GetTmMatches(_activeDocument);

            // Document context (all segments for document type analysis)
            List<string> documentSegments = null;
            int activeSegmentIndex = -1;
            int totalSegmentCount = 0;
            if (_settings?.AiSettings?.IncludeDocumentContext != false)
            {
                var docCtx = CollectDocumentContext();
                documentSegments = docCtx.Item1;
                activeSegmentIndex = docCtx.Item2;
                totalSegmentCount = documentSegments?.Count ?? 0;
            }

            // Surrounding segments – count from settings (default 5)
            var surroundingSegments = GetSurroundingSegments(
                _settings?.AiSettings?.QuickLauncherSurroundingSegments ?? 5);

            // Project metadata
            var projectName = GetProjectName();
            var fileName = GetFileName();

            // 3. Build system prompt with full context
            // Load SuperMemory KB context (if vault exists). Pass the user's
            // message so a term they ask about is force-included even when the
            // document's domain/language wouldn't otherwise rank that note.
            var kbPromptSection = LoadKbContextForPrompt(projectName, sourceLang, targetLang, messageText);

            var chatCtx = new ChatContext
            {
                SourceLang = sourceLang,
                TargetLang = targetLang,
                SourceText = sourceText,
                TargetText = targetText,
                MatchedTerms = matchedTerms,
                TmMatches = tmMatches,
                ProjectName = projectName,
                FileName = fileName,
                DocumentSegments = documentSegments,
                ActiveSegmentIndex = activeSegmentIndex,
                TotalSegmentCount = totalSegmentCount,
                MaxDocumentSegments = _settings?.AiSettings?.DocumentContextMaxSegments ?? 500,
                SurroundingSegments = surroundingSegments,
                IncludeTermMetadata = _settings?.AiSettings?.IncludeTermMetadata != false,
                KbContext = kbPromptSection,
                DemoMode = _settings?.AiSettings?.DemoMode ?? false
            };
            var systemPrompt = ChatPrompt.BuildSystemPrompt(chatCtx);

            // 4. Build message window
            // QuickLauncher prompts are standalone – send only the current message,
            // not the chat history. This prevents accumulated history from inflating
            // token costs (e.g. previous {{PROJECT}} expansions).
            // AutoPrompt (showAsStatus) is also standalone.
            List<ChatMessage> messagesToSend;
            var isStandalone = !string.IsNullOrEmpty(args.PromptName) || args.ShowAsStatus;
            if (isStandalone)
            {
                // Send only the current message – no history
                messagesToSend = new List<ChatMessage> { _chatHistory[_chatHistory.Count - 1] };
            }
            else
            {
                // Regular chat: send last 10 messages for conversational context
                messagesToSend = BuildMessageWindow(_chatHistory, 10);
            }

            // 5. Resolve provider / API key
            var aiSettings = _settings?.AiSettings;
            if (aiSettings == null)
            {
                AddErrorMessage("AI settings not configured. Open Settings \u2192 AI Settings to configure a provider.");
                return;
            }

            var provider = aiSettings.SelectedProvider ?? LlmModels.ProviderOpenAi;
            string apiKey;
            string baseUrl = null;
            string model = aiSettings.GetSelectedModel();

            if (provider == LlmModels.ProviderOllama)
            {
                apiKey = "ollama";
                baseUrl = aiSettings.OllamaEndpoint ?? "http://localhost:11434";
            }
            else if (provider == LlmModels.ProviderCustomOpenAi)
            {
                var profile = aiSettings.GetActiveCustomProfile();
                if (profile == null)
                {
                    AddErrorMessage("No custom OpenAI profile configured.");
                    return;
                }
                apiKey = profile.ApiKey;
                baseUrl = profile.Endpoint;
                model = profile.Model;
            }
            else
            {
                apiKey = LlmClient.ResolveApiKey(provider, aiSettings.ApiKeys);
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                AddErrorMessage($"No API key configured for {provider}. Open Settings \u2192 AI Settings to add one.");
                return;
            }

            // 6. Show thinking state
            _control.Value.SetThinking(true);
            _chatCts?.Cancel();
            _chatCts = new CancellationTokenSource();
            var ct = _chatCts.Token;

            // Capture for async
            var capturedProvider = provider;
            var capturedModel = model;
            var capturedKey = apiKey;
            var capturedBaseUrl = baseUrl;
            var capturedSystemPrompt = systemPrompt;
            var capturedMessages = messagesToSend;
            var capturedMaxTokens = args.MaxTokens ?? 4096;
            var capturedPromptName = args.PromptName;
            var capturedFeature = !string.IsNullOrEmpty(args.PromptName)
                ? PromptLogFeature.QuickLauncher
                : PromptLogFeature.Chat;

            // 7. Call LLM async – calculate prompt size for diagnostics
            var promptCharCount = 0;
            foreach (var m in capturedMessages)
                promptCharCount += m.Content?.Length ?? 0;
            promptCharCount += capturedSystemPrompt?.Length ?? 0;

            // Cost guard: warn if estimated cost exceeds $0.50
            var estimatedTokens = promptCharCount / 4; // rough: 1 token ≈ 4 chars
            var estimatedCost = TokenEstimator.EstimateInputCost(capturedModel, estimatedTokens);
            if (estimatedCost > 0.50m)
            {
                var costStr = estimatedCost.ToString("F2");
                var tokenStr = estimatedTokens.ToString("N0");
                var result = System.Windows.Forms.MessageBox.Show(
                    $"This request will send approximately {tokenStr} tokens to {capturedModel}.\n" +
                    $"Estimated input cost: ~${costStr}\n\n" +
                    // Deliberately names no model: this dialog fires for whichever provider
                    // is selected, and the tip used to recommend OpenAI models to users on
                    // Claude, Gemini or Ollama.
                    "Tip: a smaller, cheaper model from your provider handles everyday queries well.\n" +
                    "Reserve premium models for AutoPrompt and other complex tasks.\n\n" +
                    "Continue?",
                    "Cost Warning",
                    System.Windows.Forms.MessageBoxButtons.YesNo,
                    System.Windows.Forms.MessageBoxIcon.Warning,
                    System.Windows.Forms.MessageBoxDefaultButton.Button2);

                if (result != System.Windows.Forms.DialogResult.Yes)
                {
                    _control.Value.SetThinking(false);
                    return;
                }
            }

            // Capture tool settings – Claude, OpenAI, Gemini, Grok, Mistral all support tool use
            var useTools = LlmClient.SupportsToolUse(capturedProvider);
            var toolDefsJson = useTools ? TradosTools.GetToolDefinitionsJson(capturedProvider) : null;

            Task.Run(async () =>
            {
                try
                {
                    var client = new LlmClient(capturedProvider, capturedModel, capturedKey, capturedBaseUrl,
                        ollamaTimeoutMinutes: aiSettings.OllamaTimeoutMinutes);

                    string response;
                    if (useTools)
                    {
                        response = await client.SendChatWithToolsAsync(
                            capturedMessages, capturedSystemPrompt,
                            toolDefsJson, TradosTools.ExecuteTool,
                            maxTokens: capturedMaxTokens, cancellationToken: ct,
                            feature: capturedFeature, promptName: capturedPromptName,
                            toolStatusCallback: toolName =>
                                SafeInvoke(() => _control.Value.SetThinking(true, FormatToolStatus(toolName))));
                    }
                    else
                    {
                        response = await client.SendChatAsync(
                            capturedMessages, capturedSystemPrompt,
                            maxTokens: capturedMaxTokens, cancellationToken: ct,
                            feature: capturedFeature, promptName: capturedPromptName);
                    }

                    var assistantMsg = new ChatMessage
                    {
                        Role = ChatRole.Assistant,
                        Content = response?.Trim() ?? "(No response)"
                    };

                    SafeInvoke(() =>
                    {
                        _chatHistory.Add(assistantMsg);
                        _control.Value.AddMessage(assistantMsg);
                        _control.Value.SetThinking(false);
                        SaveChatHistory();
                    });
                }
                catch (OperationCanceledException oce)
                {
                    SafeInvoke(() =>
                    {
                        _control.Value.SetThinking(false);
                        if (_userCancelled)
                        {
                            _userCancelled = false;
                        }
                        else
                        {
                            var tokensEst = promptCharCount / 4;
                            var inner = oce.InnerException?.Message;
                            var detail = inner != null ? $"\n\nInner: {inner}" : "";
                            AddErrorMessage(
                                $"The request timed out.\n\n" +
                                $"Model: {capturedModel}\n" +
                                $"Prompt size: ~{tokensEst:N0} tokens ({promptCharCount:N0} chars)\n" +
                                $"Max output tokens: {capturedMaxTokens}\n\n" +
                                $"If the model is slow or reasoning-heavy, try a faster one " +
                                $"(e.g. GPT-5.4 Mini or Claude Sonnet 5), or send less context." +
                                detail);

                            // Always log it: an AI failure that leaves nothing in the
                            // diagnostic log is undiagnosable from a bug report. A user
                            // hit exactly this – a 120 s AutoPrompt timeout whose 14 MB
                            // log contained not one line about the request.
                            try
                            {
                                Core.DiagnosticLog.WriteAlways("AI",
                                    $"request TIMED OUT: model={capturedModel} " +
                                    $"promptChars={promptCharCount} promptTokensEst={tokensEst} " +
                                    $"maxOutputTokens={capturedMaxTokens}");
                            }
                            catch { }
                        }
                    });
                }
                catch (Exception ex)
                {
                    SafeInvoke(() =>
                    {
                        _control.Value.SetThinking(false);
                        var inner = ex.InnerException?.Message;
                        var detail = inner != null ? $"\n\nInner: {inner}" : "";
                        AddErrorMessage($"Error: {ex.Message}{detail}");
                    });
                }
            });
        }

        private void OnClearRequested(object sender, EventArgs e)
        {
            // Archive the current session before wiping it, so it can be recovered.
            if (_chatHistory.Count > 0)
                ArchiveChatHistory();

            _chatHistory.Clear();
            _control.Value.ClearMessages();
            SaveChatHistory();
        }

        private void ArchiveChatHistory()
        {
            try
            {
                var archivePath = UserDataPath.ChatArchiveFilePath(DateTime.Now);
                Directory.CreateDirectory(Path.GetDirectoryName(archivePath));
                var serializer = new DataContractJsonSerializer(typeof(List<ChatMessage>));
                using (var fs = new FileStream(archivePath, FileMode.Create, FileAccess.Write))
                    serializer.WriteObject(fs, _chatHistory);
            }
            catch { /* archive is best-effort – never block the clear */ }
        }

        private void OnStopRequested(object sender, EventArgs e)
        {
            _userCancelled = true;
            _chatCts?.Cancel();
        }

        private void OnApplyToTargetRequested(object sender, string text)
        {
            if (_activeDocument == null || string.IsNullOrEmpty(text))
                return;

            try
            {
                _activeDocument.Selection.Target.Replace(text, "Supervertaler AI");
            }
            catch (Exception)
            {
                // Editor may not allow insertion at this moment
            }
        }

        // ─── Supervertaler Bridge ─────────────────────────────────────────────
        //
        // The Supervertaler Bridge (Core/SupervertalerBridge.cs) is a localhost-only
        // HTTP listener that lets external Supervertaler clients – primarily
        // the floating Sidekick Chat in Supervertaler Workbench – read the
        // active Trados project context and insert translations back into
        // the editor. The bridge runs in this ViewPart's lifecycle because
        // we already hold the references to _activeDocument, _settings, and
        // the helpers (GetSurroundingSegments, GetProjectName, etc.) the
        // bridge needs to build its context snapshot.

        private void StartSupervertalerBridge()
        {
            try
            {
                BridgeLog.Write("StartSupervertalerBridge() called");

                if (_supervertalerBridge != null)
                {
                    BridgeLog.Write("guard: bridge already non-null – no-op");
                    return;
                }
                if (!LicenseManager.Instance.HasAssistantAccess)
                {
                    BridgeLog.Write("guard: HasAssistantAccess=false – bridge skipped");
                    return;
                }
                if (_settings?.AiSettings?.SidekickBridgeEnabled == false)
                {
                    BridgeLog.Write("guard: AiSettings.SidekickBridgeEnabled=false – bridge skipped");
                    return;
                }
                BridgeLog.Write($"guards passed: tier={LicenseManager.Instance.CurrentTier}, enabled={_settings?.AiSettings?.SidekickBridgeEnabled}");

                _supervertalerBridge = new SupervertalerBridge(
                    getContext: BuildBridgeContextSnapshot,
                    insertText: BridgeInsertTranslation,
                    getProject: BuildBridgeProjectSnapshot,
                    getSegments: BuildBridgeSegments,
                    getDbPath: ResolveSupervertalerDbPath,
                    updateSegments: BridgeUpdateSegments,
                    addTerm: BridgeAddTerm,
                    getFiles: BuildBridgeFiles,
                    findInconsistencies: BuildBridgeInconsistencies,
                    searchStudioTm: BridgeSearchStudioTm,
                    runQaCheck: BridgeRunQaCheck,
                    compareTm: BridgeCompareTm,
                    listResources: BridgeListResources,
                    goToSegment: BridgeGoToSegment,
                    getComments: BridgeGetComments,
                    addComment: BridgeAddComment,
                    updateComment: BridgeUpdateComment,
                    deleteComment: BridgeDeleteComment,
                    runVerification: BridgeRunVerification,
                    findReplace: BridgeFindReplace,
                    runTask: BridgeRunTask,
                    getTaskStatus: BridgeGetTaskStatus,
                    getPromptContext: BuildPromptContext,
                    updateTerm: BridgeUpdateTerm,
                    deleteTerm: BridgeDeleteTerm,
                    saveDocument: BridgeSaveDocument,
                    getSuperMemoryContext: BridgeGetSuperMemoryContext,
                    searchSuperMemory: BridgeSearchSuperMemory,
                    listSuperMemoryBanks: BridgeListSuperMemoryBanks,
                    markReviewed: BridgeMarkReviewed,
                    getCoverage: BridgeGetCoverage,
                    getTrackedChanges: BridgeGetTrackedChanges,
                    importTermbase: BridgeImportTermbase,
                    getInstanceInfo: BuildBridgeInstanceInfo);
                _supervertalerBridge.Start();
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"StartSupervertalerBridge() THREW: {ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}");
                _supervertalerBridge = null;
            }
        }

        /// <summary>
        /// True when THIS Studio process has a bridge listening. The Connect dialog
        /// used to answer this by testing whether bridge.json existed, which stopped
        /// being about this session the moment a second Studio could overwrite that
        /// file — the first Studio would cheerfully report the second one's bridge as
        /// its own. Asking the live listener is exact and needs no file at all.
        /// </summary>
        public static bool IsBridgeRunning
        {
            get
            {
                try { return _currentInstance?._supervertalerBridge?.IsRunning == true; }
                catch { return false; }
            }
        }

        /// <summary>
        /// Identity for this Studio process's bridge handshake: which project and
        /// document it has open. When two Studio versions run side by side, this is
        /// what lets an MCP client tell the user which one it is talking to rather
        /// than guessing between two indistinguishable ports (issue #72).
        /// Called on the UI thread, from bridge start and ActiveDocumentChanged.
        /// </summary>
        private BridgeInstanceInfo BuildBridgeInstanceInfo()
        {
            string activeFile = null;
            try { activeFile = _activeDocument?.ActiveFile?.Name; } catch { /* between documents */ }

            return new BridgeInstanceInfo
            {
                ProjectName = TermLensEditorViewPart.GetCurrentProjectName(),
                ActiveFile = activeFile
            };
        }

        /// <summary>
        /// Called by the bridge listener thread; marshals to the UI thread to
        /// build a snapshot of the current Trados project state. Mirrors the
        /// fields the in-Trados Chat already gathers in OnSendRequested so
        /// both consumers see the same shape of context.
        /// </summary>
        private BridgeContextSnapshot BuildBridgeContextSnapshot()
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeContextSnapshot { Available = false };

            if (ctrl.InvokeRequired)
            {
                return (BridgeContextSnapshot)ctrl.Invoke(new Func<BridgeContextSnapshot>(() => BuildBridgeContextSnapshot()));
            }
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies – marshal via the UI thread we
            // captured at startup instead. See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BuildBridgeContextSnapshot());

            var snapshot = new BridgeContextSnapshot { Available = false };
            if (_activeDocument == null) return snapshot;

            try
            {
                var pair = _activeDocument.ActiveSegmentPair;
                if (pair == null) return snapshot;

                // Serialize BOTH sides the way get_segments does, so the markers an
                // agent sees here are the <t1/>/<t2>\u2026</t2> ones update_segments
                // accepts. Previously source used the raw ToString() (emitting
                // internal markup like <group name="Group 258"><cf size=8>, which
                // update_segments rejects) and target used GetFinalText, which
                // strips tags entirely \u2013 so every neighbouring target looked like it
                // had lost its formatting. An agent acting on that would "repair"
                // segments that were never broken.
                var sourceText = SerializeForBridge(pair.Source);
                var targetText = pair.Target != null ? SerializeForBridge(pair.Target) : null;

                snapshot.Available = true;
                snapshot.Project = new BridgeProjectInfo
                {
                    Name = GetProjectName(),
                    FileName = GetFileName(),
                    SourceLang = GetDocumentSourceLanguage(),
                    TargetLang = GetDocumentTargetLanguage()
                };
                snapshot.ActiveSegment = new BridgeSegmentInfo
                {
                    Source = sourceText ?? "",
                    Target = targetText
                };

                // Surrounding segments
                var surroundingCount = _settings?.AiSettings?.QuickLauncherSurroundingSegments ?? 5;
                var surrounding = GetSurroundingSegments(surroundingCount, serializeTags: true);
                snapshot.SurroundingSegments = new List<BridgeSegmentInfo>();
                foreach (var s in surrounding)
                {
                    snapshot.SurroundingSegments.Add(new BridgeSegmentInfo
                    {
                        Source = s[0] ?? "",
                        Target = s[1]
                    });
                }

                // TM matches (only if user has IncludeTmMatches enabled, mirroring Chat)
                if (_settings?.AiSettings?.IncludeTmMatches != false)
                {
                    try
                    {
                        var tmMatches = DocumentContextHelper.GetTmMatches(_activeDocument);
                        snapshot.TmMatches = new List<BridgeTmMatch>();
                        if (tmMatches != null)
                        {
                            foreach (var m in tmMatches)
                            {
                                snapshot.TmMatches.Add(new BridgeTmMatch
                                {
                                    Score = m.MatchPercentage,
                                    Source = m.SourceText ?? "",
                                    Target = m.TargetText ?? "",
                                    TmName = m.TmName
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SupervertalerBridge] TM gather threw: {ex.Message}");
                    }
                }

                // Termbase hits – filter by AI-disabled IDs the same way Chat does
                try
                {
                    var aiCfgSnap = _settings?.AiSettings ?? new AiSettings();
                    var allMatches = TermLensEditorViewPart.GetCurrentSegmentMatches();
                    var matchedTerms = allMatches.Where(m => aiCfgSnap.IsTermbaseAiEnabled(m.PrimaryEntry?.TermbaseId ?? 0)).ToList();

                    snapshot.TermbaseHits = new List<BridgeTermbaseHit>();
                    foreach (var m in matchedTerms)
                    {
                        var entry = m.PrimaryEntry;
                        if (entry == null) continue;
                        snapshot.TermbaseHits.Add(new BridgeTermbaseHit
                        {
                            Source = entry.SourceTerm ?? "",
                            Target = entry.TargetTerm ?? "",
                            TermbaseName = entry.TermbaseName,
                            Definition = entry.Definition,
                            Domain = entry.Domain,
                            Notes = entry.Notes
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SupervertalerBridge] termbase gather threw: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SupervertalerBridge] BuildBridgeContextSnapshot threw: {ex.Message}");
                return new BridgeContextSnapshot { Available = false };
            }

            return snapshot;
        }

        /// <summary>
        /// Inserts text into the active Trados target segment via the same
        /// Selection.Target.Replace path that powers the in-Chat Apply-To-Target
        /// button. Returns null on success, an error string otherwise.
        /// Marshals to the UI thread.
        /// </summary>
        private string BridgeInsertTranslation(string text)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed) return "ai assistant disposed";

            if (ctrl.InvokeRequired)
            {
                return (string)ctrl.Invoke(new Func<string>(() => BridgeInsertTranslation(text)));
            }
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies – marshal via the UI thread we
            // captured at startup instead. See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeInsertTranslation(text));

            if (_activeDocument == null) return "no active document";
            if (string.IsNullOrEmpty(text)) return "empty text";

            try
            {
                _activeDocument.Selection.Target.Replace(text, "Supervertaler Workbench");
                return null;
            }
            catch (Exception ex)
            {
                return "insert failed: " + ex.Message;
            }
        }

        /// <summary>
        /// Bridge delegate for GET /v1/project (MCP get_active_project).
        /// Project metadata plus segment counts per confirmation status,
        /// gathered by walking the active document. Marshals to the UI thread.
        /// </summary>
        private BridgeProjectSnapshot BuildBridgeProjectSnapshot()
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeProjectSnapshot { Available = false };

            if (ctrl.InvokeRequired)
            {
                return (BridgeProjectSnapshot)ctrl.Invoke(new Func<BridgeProjectSnapshot>(() => BuildBridgeProjectSnapshot()));
            }
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies – marshal via the UI thread we
            // captured at startup instead. See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BuildBridgeProjectSnapshot());

            var snapshot = new BridgeProjectSnapshot { Available = false };
            if (_activeDocument == null)
            {
                snapshot.Note = "No document is open in the Trados editor.";
                return snapshot;
            }

            try
            {
                snapshot.Available = true;
                snapshot.Name = GetProjectName();
                snapshot.FileName = GetFileName();
                snapshot.SourceLang = GetDocumentSourceLanguage();
                snapshot.TargetLang = GetDocumentTargetLanguage();

                // Full path to the live project's .sdlproj – lets /v1/statistics
                // read the analysis report from the open project directly, instead
                // of a name->projects.xml lookup (which misses recently-created
                // projects and projects registered under another Studio version).
                try
                {
                    var fbp = _activeDocument.Project as Sdl.ProjectAutomation.FileBased.FileBasedProject;
                    snapshot.SdlprojPath = fbp?.FilePath;
                }
                catch { /* non-fatal – statistics falls back to the name lookup */ }

                var statusCounts = new Dictionary<string, int>();
                int total = 0, locked = 0;
                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    total++;
                    if (pair.Properties?.IsLocked == true) locked++;
                    var status = (pair.Properties?.ConfirmationLevel
                        ?? Sdl.Core.Globalization.ConfirmationLevel.Unspecified).ToString();
                    int count;
                    statusCounts.TryGetValue(status, out count);
                    statusCounts[status] = count + 1;
                }

                snapshot.TotalSegments = total;
                snapshot.LockedSegments = locked;
                snapshot.StatusCounts = statusCounts
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => new BridgeStatusCount { Status = kv.Key, Segments = kv.Value })
                    .ToList();

                // Orientation call – the right place to surface a setup problem
                // the caller would otherwise never see.
                var termbaseWarning = BridgeTermbaseWarning();
                if (termbaseWarning != null)
                    snapshot.Warnings = new List<string> { termbaseWarning };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SupervertalerBridge] BuildBridgeProjectSnapshot threw: {ex.Message}");
                return new BridgeProjectSnapshot
                {
                    Available = false,
                    Note = "error reading project: " + ex.Message
                };
            }

            return snapshot;
        }

        /// <summary>
        /// Bridge delegate for GET /v1/segments (MCP get_segments). Walks the
        /// active document with the same tag-safe serialization the bilingual
        /// export uses, applying status/contains filters and paging. Marshals
        /// to the UI thread.
        /// </summary>
        private BridgeSegmentsResponse BuildBridgeSegments(BridgeSegmentsQuery query)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeSegmentsResponse { Available = false };

            if (ctrl.InvokeRequired)
            {
                return (BridgeSegmentsResponse)ctrl.Invoke(new Func<BridgeSegmentsResponse>(() => BuildBridgeSegments(query)));
            }
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies – marshal via the UI thread we
            // captured at startup instead. See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BuildBridgeSegments(query));

            var response = new BridgeSegmentsResponse
            {
                Available = false,
                Segments = new List<BridgeSegmentRecord>()
            };
            if (_activeDocument == null)
            {
                response.Note = "No document is open in the Trados editor.";
                return response;
            }

            // File filter / attribution (merged multi-file documents). The
            // pu→file map comes from RefreshFileToSegmentMap (SDLXLIFF GUID
            // scan); refresh it lazily when the document changed.
            string filterFileId = null;
            bool attributeFiles = false;
            if (!string.IsNullOrEmpty(query.File))
            {
                EnsureBridgeFileMapFresh();
                if (!_perFileMappingWorked)
                {
                    response.Available = true;
                    response.Note = "This document's segments could not be attributed to files, so the " +
                                    "'file' filter is unavailable – query without it.";
                    return response;
                }
                filterFileId = ResolveBridgeFileId(query.File);
                if (filterFileId == null)
                {
                    response.Available = true;
                    response.Note = $"No file matching '{query.File}' in this document – call get_files " +
                                    "for the list of files.";
                    return response;
                }
                attributeFiles = true;
            }
            else if (_fileIdToName.Count > 1 || TryGetEnumerable(_activeDocument, "Files") != null)
            {
                // Attribute fileName on multi-file documents even without a
                // filter, so the AI can tell files apart in the results.
                EnsureBridgeFileMapFresh();
                attributeFiles = _perFileMappingWorked && _fileIdToName.Count > 1;
            }

            try
            {
                response.Available = true;
                int matching = 0;

                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    string source, target, status, id;
                    string segFileName = null;
                    bool isLocked;
                    int? matchPercent = null;
                    string originType = null;
                    try
                    {
                        status = (pair.Properties?.ConfirmationLevel
                            ?? Sdl.Core.Globalization.ConfirmationLevel.Unspecified).ToString();
                        if (!string.IsNullOrEmpty(query.Status)
                            && !status.Equals(query.Status, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // TM match-rate filter (issue #44 field feedback: "find
                        // segments with a certain match rate, not just a status").
                        // Segments without a TM/MT origin count as 0%, so
                        // matchMax=0 finds the no-match segments.
                        var origin = pair.Properties?.TranslationOrigin;
                        if (origin != null)
                        {
                            originType = string.IsNullOrEmpty(origin.OriginType) ? null : origin.OriginType;
                            if (originType != null && originType != "not-translated")
                                matchPercent = origin.MatchPercent;
                        }
                        int effectivePercent = matchPercent ?? 0;
                        if (query.MatchMin >= 0 && effectivePercent < query.MatchMin) continue;
                        if (query.MatchMax >= 0 && effectivePercent > query.MatchMax) continue;

                        var sourceSer = SegmentTagHandler.Serialize(pair.Source);
                        source = Core.Export.BilingualTagNamer.ApplySemanticNames(
                            sourceSer.SerializedText ?? "", sourceSer.TagMap);
                        if (string.IsNullOrWhiteSpace(SegmentTagHandler.StripTagPlaceholders(source)))
                            continue; // structural/empty segment – same rule as the bilingual export

                        if (pair.Target != null)
                        {
                            var targetSer = SegmentTagHandler.Serialize(pair.Target);
                            target = Core.Export.BilingualTagNamer.ApplySemanticNames(
                                targetSer.SerializedText ?? "", targetSer.TagMap);
                        }
                        else
                        {
                            target = "";
                        }

                        if (!string.IsNullOrEmpty(query.Contains)
                            && source.IndexOf(query.Contains, StringComparison.OrdinalIgnoreCase) < 0
                            && target.IndexOf(query.Contains, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        var puId = _activeDocument.GetParentParagraphUnit(pair)
                            ?.Properties?.ParagraphUnitId.Id ?? "";
                        var segId = pair.Properties?.Id.Id ?? "";
                        id = puId + ":" + segId;
                        isLocked = pair.Properties?.IsLocked == true;

                        if (filterFileId != null || attributeFiles)
                        {
                            string fid;
                            _puIdToFileId.TryGetValue(puId, out fid);
                            if (filterFileId != null && fid != filterFileId)
                                continue;
                            if (attributeFiles && fid != null)
                                _fileIdToName.TryGetValue(fid, out segFileName);
                        }

                        // Grid-number range filter ("look at segment 331"). The
                        // grid number is the segment id's numeric part; split
                        // segments ("331 a") count by their leading digits. In a
                        // merged document numbers restart per file, so without a
                        // file filter the range matches in every file (the
                        // fileName field tells them apart).
                        if (query.FromNumber > 0 || query.ToNumber > 0)
                        {
                            int digits = 0;
                            while (digits < segId.Length && char.IsDigit(segId[digits])) digits++;
                            int segNum;
                            if (digits == 0 || !int.TryParse(segId.Substring(0, digits), out segNum))
                                continue;
                            if (query.FromNumber > 0 && segNum < query.FromNumber) continue;
                            if (query.ToNumber > 0 && segNum > query.ToNumber) continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[SupervertalerBridge] segment read threw, skipping: {ex.Message}");
                        continue;
                    }

                    matching++;
                    if (matching <= query.Offset) continue;
                    if (response.Segments.Count >= query.Limit)
                    {
                        response.Truncated = true;
                        continue; // keep counting totalMatching, stop collecting
                    }

                    var segNumber = id.Substring(id.LastIndexOf(':') + 1);
                    response.Segments.Add(new BridgeSegmentRecord
                    {
                        Id = id,
                        Source = source,
                        Target = string.IsNullOrEmpty(target) ? null : target,
                        Status = status,
                        IsLocked = isLocked,
                        FileName = string.IsNullOrEmpty(segFileName) ? null : segFileName,
                        Number = segNumber,
                        Match = matchPercent,
                        Origin = originType
                    });
                }

                response.TotalMatching = matching;
                response.Returned = response.Segments.Count;
                if (response.Truncated)
                    response.Note = $"Only {response.Returned} of {matching} matching segments returned – " +
                                    "use offset/limit to page through the rest, or narrow the filters.";
                if ((query.FromNumber > 0 || query.ToNumber > 0) && filterFileId == null && attributeFiles)
                    response.Note = ((response.Note ?? "") + " Note: this is a merged multi-file document and " +
                        "segment numbers restart per file, so the number range matched in every file – check " +
                        "each hit's fileName, or pass 'file' to target one file.").Trim();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SupervertalerBridge] BuildBridgeSegments threw: {ex.Message}");
                return new BridgeSegmentsResponse
                {
                    Available = false,
                    Note = "error reading segments: " + ex.Message
                };
            }

            return response;
        }

        /// <summary>
        /// Bridge delegate for POST /v1/update-segments (MCP update_segments).
        /// Writes target text and/or confirmation status for segments addressed
        /// by "puId:segId" keys, using the same tag-aware ProcessSegmentPair
        /// write path as bilingual re-import. A target write without an explicit
        /// status defaults to Draft so AI writes are always visible as such in
        /// Studio. Locked segments are refused. Marshals to the UI thread.
        /// </summary>
        private BridgeUpdateSegmentsResponse BridgeUpdateSegments(BridgeUpdateSegmentsRequest req)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeUpdateSegmentsResponse { Ok = false, Error = "ai assistant disposed" };

            if (ctrl.InvokeRequired)
            {
                return (BridgeUpdateSegmentsResponse)ctrl.Invoke(new Func<BridgeUpdateSegmentsResponse>(() => BridgeUpdateSegments(req)));
            }
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies – marshal via the UI thread we
            // captured at startup instead. See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeUpdateSegments(req));

            if (_activeDocument == null)
                return new BridgeUpdateSegmentsResponse { Ok = false, Error = "no document is open in the Trados editor" };

            var response = new BridgeUpdateSegmentsResponse
            {
                Ok = true,
                Results = new List<BridgeUpdateResultItem>()
            };

            var pairIndex = BuildSegmentPairIndex(null);
            int processed = 0;
            int tagMismatches = 0;

            foreach (var u in req.Updates)
            {
                var item = new BridgeUpdateResultItem { Id = u?.Id ?? "" };
                response.Results.Add(item);
                processed++;

                // Keep Studio responsive during large batches (same cadence as re-import).
                if (processed % 20 == 0)
                    System.Windows.Forms.Application.DoEvents();

                if (u == null || string.IsNullOrEmpty(u.Id))
                {
                    item.Error = "missing id";
                    response.Failed++;
                    continue;
                }

                // Id format "puId:segId" – split on the LAST colon (GUIDs contain none,
                // but be safe about future id shapes).
                var sep = u.Id.LastIndexOf(':');
                if (sep <= 0 || sep == u.Id.Length - 1)
                {
                    item.Error = "malformed id – expected \"<paragraphUnitId>:<segmentId>\" as returned by get_segments";
                    response.Failed++;
                    continue;
                }

                ISegmentPair pair;
                if (!pairIndex.TryGetValue(KeyOf(u.Id.Substring(0, sep), u.Id.Substring(sep + 1)), out pair) || pair == null)
                {
                    item.Error = "segment not found in the open document";
                    response.Failed++;
                    continue;
                }

                if (pair.Properties?.IsLocked == true)
                {
                    item.Error = "segment is locked";
                    response.Failed++;
                    continue;
                }

                // Resolve the requested status up front so an unknown name fails
                // the whole item before any content is written.
                var statusName = u.Status;
                if (string.IsNullOrEmpty(statusName) && u.Target != null)
                    statusName = "Draft";

                Sdl.Core.Globalization.ConfirmationLevel level = default;
                bool setStatus = false;
                if (!string.IsNullOrEmpty(statusName))
                {
                    if (!Enum.TryParse(statusName, true, out level))
                    {
                        item.Error = $"unknown status '{statusName}' – valid: Unspecified, Draft, Translated, " +
                                     "RejectedTranslation, ApprovedTranslation, RejectedSignOff, ApprovedSignOff";
                        response.Failed++;
                        continue;
                    }
                    setStatus = true;
                }

                if (u.Target == null && !setStatus)
                {
                    item.Error = "nothing to do – provide 'target' and/or 'status'";
                    response.Failed++;
                    continue;
                }

                // Decode once, here, so the tag-aware path and the plain-text
                // fallback below both see the real characters.
                var targetText = u.Target;
                if (req.DecodeEntities && targetText != null)
                    targetText = Core.EntityEscapes.Decode(targetText);

                string tagWarning = null;

                try
                {
                    if (targetText != null)
                    {
                        _activeDocument.ProcessSegmentPair(pair, "Supervertaler MCP",
                            (sp, cancel) =>
                            {
                                // Tag-aware write. NOTE the difference from bilingual
                                // re-import: that path uses BuildCombinedTagMap, where
                                // the TARGET wins numbering collisions, because the
                                // proofreader's <b>/<tN> markers came from the target
                                // rendering of the exported table.
                                //
                                // Here the markers came from get_segments' *source*
                                // field, and — decisively — Studio's Tag Verifier
                                // compares the target's underlying tag ids against the
                                // SOURCE. Letting a stale fuzzy-match target tag win
                                // therefore cloned a tag carrying the wrong id into the
                                // target: the verifier reported "Duplicated tag with id
                                // 'N'" plus "Missing tag with id 'N-1'". Worse, it was
                                // self-perpetuating — the next write re-serialised that
                                // same corrupt target and let it win again, so
                                // rewriting the segment could never heal it.
                                // Field report: job PO414646, segments 498/500/552/559,
                                // all of the shape "<b>I/O</b> switch … <b>O</b> position".
                                //
                                // So: source-authoritative, always.
                                bool reconstructed = false;
                                var sourceSer = Core.SegmentTagHandler.Serialize(sp.Source);
                                var tagMap = sourceSer.TagMap
                                    ?? new Dictionary<int, Core.TagInfo>();

                                // How many comments this segment carried going in.
                                // Both write branches clear the target, and comments
                                // live only there, so without deliberate preservation
                                // the routine "fix the segment this comment is about"
                                // edit deletes the comment. Counted before the write
                                // so the post-write check can prove they survived.
                                int commentsBefore =
                                    Core.SegmentTagHandler.CaptureCommentMarkers(sp.Target).Count;

                                var resolved = Core.Export.BilingualTagNamer.ResolveSemanticNames(
                                    targetText, tagMap);

                                bool hasAnyMarker = tagMap.Count > 0
                                    || resolved.IndexOf("<t", StringComparison.Ordinal) >= 0;
                                if (hasAnyMarker)
                                {
                                    reconstructed = Core.SegmentTagHandler.ReconstructTarget(
                                        sp.Target, sp.Source, resolved, tagMap);
                                }

                                if (!reconstructed)
                                {
                                    var plain = Core.SegmentTagHandler.StripTagPlaceholders(targetText);
                                    plain = System.Text.RegularExpressions.Regex.Replace(
                                        plain, @"</?(?:bi|b|i|u)>", "",
                                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                                    var textTpl = Core.SegmentTagHandler.FindFirstText(sp.Source);
                                    if (textTpl != null)
                                    {
                                        var keptComments =
                                            Core.SegmentTagHandler.CaptureCommentMarkers(sp.Target);
                                        sp.Target.Clear();
                                        var dest = Core.SegmentTagHandler.OpenCommentMarkers(
                                            sp.Target, keptComments);
                                        var clone = (IText)textTpl.Clone();
                                        clone.Properties.Text = plain;
                                        dest.Add(clone);
                                    }
                                }

                                // Post-write audit. Whatever we just built, check that
                                // the tag ids actually in the target match the source's,
                                // and report a mismatch in the tool result rather than
                                // leaving the caller to find it via run_verification.
                                //
                                // Audited after BOTH branches, not just after a successful
                                // reconstruction. The fallback above strips every tag and
                                // writes plain text, so a source carrying tags ends up with
                                // a target carrying none - which Studio reports as N missing
                                // tags. That is exactly the silent lossy write this audit
                                // exists to stop being silent.
                                tagWarning = DescribeTagIdMismatch(sp.Source, sp.Target);

                                // Comment audit, same principle as the tag audit: a
                                // write that silently destroys a comment is the worst
                                // available behaviour, so if preservation did not hold
                                // the caller is told rather than left to discover it
                                // by re-parsing the saved file.
                                if (!Core.SegmentTagHandler.CommentsPreserved(sp.Target, commentsBefore))
                                {
                                    var lost = commentsBefore == 1
                                        ? "1 comment on this segment was"
                                        : commentsBefore + " comments on this segment were";
                                    tagWarning = (tagWarning == null ? "" : tagWarning + " ")
                                        + lost + " not preserved by this write – re-add with "
                                        + "add_comment (get_comments to confirm).";
                                }
                            });
                    }

                    if (setStatus)
                    {
                        pair.Properties.ConfirmationLevel = level;
                        _activeDocument.UpdateSegmentPairProperties(pair, pair.Properties);
                    }

                    item.Ok = true;
                    BridgeRecordWrite(u.Id); // coverage: this segment was written this session
                    if (!string.IsNullOrEmpty(tagWarning))
                    {
                        item.Warning = tagWarning;
                        tagMismatches++;
                    }
                    response.Applied++;
                }
                catch (Exception ex)
                {
                    item.Error = "write failed: " + ex.Message;
                    response.Failed++;
                }
            }

            // Escape-sequence safety net. Callers are told to send an invisible
            // character as a JSON escape, because a literal U+00A0 in a tool
            // argument gets normalised to a plain space by some MCP clients
            // before it ever reaches us. If the client did NOT decode the
            // escape, the six characters land in the segment verbatim - say so
            // rather than decoding it here: silently reinterpreting the text a
            // caller asked us to write is how the original bug stayed invisible.
            // Two different mistakes, so two different warnings. A backslash
            // escape is never decoded (there is no opt-in for it), so it is
            // always wrong. An entity is only wrong when the caller forgot to
            // ask for decoding — with decodeEntities it is the happy path, and
            // crying wolf there just teaches the caller to ignore the warning.
            bool wroteBackslashEscape = false, wroteUndecodedEntity = false;
            foreach (var u in req.Updates)
            {
                var t = u?.Target;
                if (t == null) continue;
                if (t.IndexOf("\\u00", StringComparison.OrdinalIgnoreCase) >= 0)
                    wroteBackslashEscape = true;
                if (!req.DecodeEntities
                    && t.IndexOf("&nbsp;", StringComparison.OrdinalIgnoreCase) >= 0)
                    wroteUndecodedEntity = true;
            }

            if (response.Applied > 0)
            {
                response.Note = "Changes are applied to the open document in Trados Studio; the user still " +
                                "needs to save the document to persist them. Tell the user exactly what was changed.";
                if (wroteBackslashEscape)
                    response.Note += " WARNING: at least one target contained a literal \\uXXXX escape, which was " +
                        "written to the segment exactly as sent — nothing here decodes backslash escapes. To write " +
                        "a non-breaking space, set decodeEntities=true and use &nbsp; instead.";
                if (wroteUndecodedEntity)
                    response.Note += " WARNING: at least one target contained &nbsp; but decodeEntities was not set, " +
                        "so it was written as those six literal characters. Re-send with decodeEntities=true if you " +
                        "meant a non-breaking space.";
                if (tagMismatches > 0)
                    response.Note += $" WARNING: {tagMismatches} segment(s) were written with inline tags whose " +
                        "underlying tag ids do not match the source — see the per-item 'warning' field. Studio's Tag " +
                        "Verifier will flag these. Usually the target text used a <tN> number the source does not " +
                        "have, or repeated the same <tN> twice; re-send that segment using exactly the tag markers " +
                        "shown in the segment's SOURCE field.";
                _bridgeUnsavedWritesDoc = _activeDocument;
            }
            return response;
        }

        /// <summary>
        /// Compare the multiset of underlying Trados tag ids in a written target
        /// against the source's, and describe the difference — or return null when
        /// they agree. This is the same comparison Studio's Tag Verifier makes, run
        /// immediately after the write so update_segments can report it.
        ///
        /// Deliberately a multiset (list) comparison, not a set comparison: the
        /// failure mode this exists to catch is the SAME id appearing twice while
        /// another goes missing, which a set comparison would hide.
        ///
        /// Extra ids in the target that the source lacks are reported; the reverse
        /// (source tags the translation legitimately dropped) is reported too, since
        /// Studio treats a missing tag as an error as well.
        /// </summary>
        private static string DescribeTagIdMismatch(ISegment source, ISegment target)
        {
            try
            {
                var mismatch = DescribeTagIdListMismatch(CollectTagIds(source), CollectTagIds(target));
                return mismatch == null
                    ? null
                    : mismatch + ". The text was written, but Studio's Tag Verifier will flag this segment.";
            }
            catch
            {
                // An audit that throws must never fail the write it is auditing.
                return null;
            }
        }

        /// <summary>
        /// The multiset comparison behind the write audit and the document-wide
        /// check_tags pass: compares how many times each underlying tag id occurs,
        /// not merely which ids occur, because the canonical failure is one id
        /// appearing twice while another goes missing – which a set comparison
        /// calls clean. Returns null when the multisets agree (or either list is
        /// unavailable), otherwise a description without trailing punctuation so
        /// callers can append their own context.
        /// </summary>
        private static string DescribeTagIdListMismatch(List<string> src, List<string> tgt)
        {
            if (src == null || tgt == null) return null;

            var srcCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var id in src)
                srcCounts[id] = (srcCounts.TryGetValue(id, out var c) ? c : 0) + 1;

            var tgtCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var id in tgt)
                tgtCounts[id] = (tgtCounts.TryGetValue(id, out var c) ? c : 0) + 1;

            var duplicated = new List<string>();
            var missing = new List<string>();

            foreach (var kv in tgtCounts)
            {
                srcCounts.TryGetValue(kv.Key, out var inSource);
                if (kv.Value > inSource)
                    duplicated.Add(kv.Key + (kv.Value > 1 ? $" (×{kv.Value})" : ""));
            }
            foreach (var kv in srcCounts)
            {
                tgtCounts.TryGetValue(kv.Key, out var inTarget);
                if (inTarget < kv.Value)
                    missing.Add(kv.Key);
            }

            if (duplicated.Count == 0 && missing.Count == 0) return null;

            var parts = new List<string>();
            if (duplicated.Count > 0)
                parts.Add("tag id(s) in the target that the source does not have (or has fewer of): "
                          + string.Join(", ", duplicated));
            if (missing.Count > 0)
                parts.Add("source tag id(s) missing from the target: " + string.Join(", ", missing));

            return "tag-id mismatch — " + string.Join("; ", parts);
        }

        /// <summary>Depth-first list of the underlying Trados tag ids in a segment,
        /// in document order, including duplicates. Tags whose id cannot be read are
        /// skipped rather than guessed at.</summary>
        private static List<string> CollectTagIds(IAbstractMarkupDataContainer container)
        {
            if (container == null) return null;
            var ids = new List<string>();
            CollectTagIdsInto(container, ids);
            return ids;
        }

        private static void CollectTagIdsInto(IAbstractMarkupDataContainer container, List<string> ids)
        {
            foreach (var item in container)
            {
                if (item is ITagPair pair)
                {
                    var id = SafeTagId(pair.StartTagProperties);
                    if (id != null) ids.Add(id);
                    CollectTagIdsInto(pair, ids);
                }
                else if (item is IPlaceholderTag ph)
                {
                    var id = SafeTagId(ph.Properties);
                    if (id != null) ids.Add(id);
                }
                else if (item is IAbstractMarkupDataContainer nested)
                {
                    CollectTagIdsInto(nested, ids);
                }
            }
        }

        /// <summary>The underlying Trados tag id, or null when it is blank.</summary>
        /// <remarks>
        /// Typed rather than reflective on purpose. Reflection here would degrade to
        /// "no audit" if the SDK ever moved TagId — that is, the check would go quiet
        /// and keep reporting success, which is the one failure mode a checker must
        /// not have. A compile error is the loud, cheap alternative.
        /// </remarks>
        private static string SafeTagId(
            Sdl.FileTypeSupport.Framework.NativeApi.IAbstractTagProperties tagProperties)
        {
            var id = tagProperties?.TagId.Id;
            return string.IsNullOrEmpty(id) ? null : id;
        }

        /// <summary>Document the pu→file map was last built for. The map itself
        /// lives in the export-panel fields (_puIdToFileId etc.); this tracker
        /// lets the bridge refresh it lazily on document switches without
        /// touching the export code paths.</summary>
        private IStudioDocument _bridgeFileMapDoc;

        /// <summary>The document that has bridge-applied edits not yet saved,
        /// or null. Set by update_segments / find_and_replace, cleared by
        /// save_document. run_verification reads the LAST SAVED files, so
        /// without this its findings silently describe a superseded state –
        /// field report: a caller reported 17 segments as "still untranslated"
        /// from a stale read. Scoped to a document so switching files can't
        /// leave a stale flag behind. Only covers our own writes: edits the
        /// user makes directly in Studio are invisible to us, which is why the
        /// advisory note stays in place regardless.</summary>
        private IStudioDocument _bridgeUnsavedWritesDoc;

        /// <summary>True when the open document holds bridge writes that have
        /// not been saved.</summary>
        private bool BridgeHasUnsavedWrites =>
            _bridgeUnsavedWritesDoc != null && ReferenceEquals(_bridgeUnsavedWritesDoc, _activeDocument);

        private void EnsureBridgeFileMapFresh()
        {
            if (!ReferenceEquals(_bridgeFileMapDoc, _activeDocument) || _fileIdToName.Count == 0)
            {
                RefreshFileToSegmentMap();
                _bridgeFileMapDoc = _activeDocument;
            }
        }

        /// <summary>Resolves a get_segments 'file' filter value (file id, exact
        /// name, or unique name substring, case-insensitive) to a file id.
        /// Returns null when nothing matches or a substring is ambiguous.</summary>
        private string ResolveBridgeFileId(string fileRef)
        {
            if (string.IsNullOrEmpty(fileRef)) return null;

            if (_fileIdToName.ContainsKey(fileRef)) return fileRef;

            foreach (var kv in _fileIdToName)
                if (string.Equals(kv.Value, fileRef, StringComparison.OrdinalIgnoreCase))
                    return kv.Key;

            string match = null;
            foreach (var kv in _fileIdToName)
            {
                if ((kv.Value ?? "").IndexOf(fileRef, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (match != null) return null; // ambiguous substring
                    match = kv.Key;
                }
            }
            return match;
        }

        /// <summary>Plain-text snapshot of one segment for off-thread QA analysis.</summary>
        private sealed class BridgeRawSegment
        {
            public string Id;
            public string Source;        // tag-stripped, whitespace-collapsed
            public string Target;        // tag-stripped, whitespace-collapsed ("" if empty)
            // Tag-stripped but NOT whitespace-collapsed. The collapsing above uses
            // \s+, and .NET's \s matches U+00A0 – so the fields above cannot be
            // used to reason about non-breaking spaces at all. The nbsp check
            // needs the characters exactly as the document holds them.
            public string SourceRaw;
            public string TargetRaw;
            public string Status;
            public int SourceTagCount;
            public int TargetTagCount;
            // TM match percentage and locked flag, captured for get_coverage's
            // match-band bookkeeping (null = no TM/MT origin).
            public int? MatchPercent;
            public bool IsLocked;
            // Underlying Trados tag ids, in document order, duplicates included.
            // Captured at snapshot time (UI thread) so the tags check can compare
            // IDS off-thread, not just counts – a target whose two tags carry the
            // same id has the right count and still fails Studio's verifier.
            public List<string> SourceTagIds;
            public List<string> TargetTagIds;
            public string FileName;      // null unless multi-file attribution worked
        }

        /// <summary>Collects a plain snapshot of every segment on the UI thread,
        /// so QA analysis can run off-thread without touching the Trados SDK.</summary>
        private List<BridgeRawSegment> BridgeCollectRawSegments()
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed) return null;
            if (ctrl.InvokeRequired)
                return (List<BridgeRawSegment>)ctrl.Invoke(
                    new Func<List<BridgeRawSegment>>(BridgeCollectRawSegments));
            // Panel handle may not exist yet – see Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeCollectRawSegments());

            if (_activeDocument == null) return null;

            EnsureBridgeFileMapFresh();
            bool attributeFiles = _perFileMappingWorked && _fileIdToName.Count > 1;

            var list = new List<BridgeRawSegment>();
            int processed = 0;
            foreach (var pair in _activeDocument.SegmentPairs)
            {
                processed++;
                if (processed % 200 == 0)
                    System.Windows.Forms.Application.DoEvents();
                try
                {
                    var sourceSer = Core.SegmentTagHandler.Serialize(pair.Source);
                    var sourceRaw = Core.SegmentTagHandler.StripTagPlaceholders(sourceSer.SerializedText ?? "");
                    var source = System.Text.RegularExpressions.Regex.Replace(
                        sourceRaw, @"\s+", " ").Trim();
                    if (source.Length == 0) continue;

                    var targetSer = pair.Target != null
                        ? Core.SegmentTagHandler.Serialize(pair.Target) : null;
                    var targetRaw = targetSer != null
                        ? Core.SegmentTagHandler.StripTagPlaceholders(targetSer.SerializedText ?? "")
                        : "";
                    var target = targetSer != null
                        ? System.Text.RegularExpressions.Regex.Replace(targetRaw, @"\s+", " ").Trim()
                        : "";

                    var puId = _activeDocument.GetParentParagraphUnit(pair)
                        ?.Properties?.ParagraphUnitId.Id ?? "";
                    var segId = pair.Properties?.Id.Id ?? "";

                    string fileName = null;
                    if (attributeFiles)
                    {
                        string fid;
                        if (_puIdToFileId.TryGetValue(puId, out fid) && fid != null)
                            _fileIdToName.TryGetValue(fid, out fileName);
                    }

                    list.Add(new BridgeRawSegment
                    {
                        Id = puId + ":" + segId,
                        Source = source,
                        Target = target,
                        SourceRaw = sourceRaw,
                        TargetRaw = targetRaw,
                        Status = (pair.Properties?.ConfirmationLevel
                            ?? Sdl.Core.Globalization.ConfirmationLevel.Unspecified).ToString(),
                        SourceTagCount = sourceSer.TagMap?.Count ?? 0,
                        TargetTagCount = targetSer?.TagMap?.Count ?? 0,
                        SourceTagIds = CollectTagIds(pair.Source),
                        TargetTagIds = pair.Target != null ? CollectTagIds(pair.Target) : null,
                        MatchPercent = BridgeReadMatchPercent(pair),
                        IsLocked = pair.Properties?.IsLocked == true,
                        FileName = fileName
                    });
                }
                catch { /* skip unreadable segment */ }
            }
            return list;
        }

        /// <summary>
        /// A path inside the open project, for TmSearcher.FindProjectTms to walk
        /// up from. MUST be called on the UI thread.
        ///
        /// Several routes, because no single one proved dependable: the
        /// FileBasedProject cast and ActiveFile.LocalFilePath both came back
        /// empty on a perfectly ordinary single-file project, even though
        /// ActiveFile itself was live enough to report its Name. Reflection is
        /// used for the SDK members that are not on the interfaces we compile
        /// against, so a route that does not exist simply loses rather than
        /// failing the build.
        /// </summary>
        private string ResolveProjectAnchorPathCore()
        {
            return ResolveProjectAnchorPathCore(null);
        }

        /// <param name="trace">Optional: receives what each route produced, so a
        /// failure can be diagnosed from the tool response instead of guessing.</param>
        private string ResolveProjectAnchorPathCore(List<string> trace)
        {
            Action<string, string> note = (route, value) =>
            {
                if (trace != null)
                    trace.Add(route + "=" + (string.IsNullOrEmpty(value) ? "(empty)" : value));
            };

            // 1. The project's own .sdlproj.
            try
            {
                var fbp = _activeDocument?.Project as Sdl.ProjectAutomation.FileBased.FileBasedProject;
                var v = fbp?.FilePath;
                note("Project.FilePath", v);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch (Exception ex) { note("Project.FilePath", "threw: " + ex.Message); }

            // 2. ProjectInfo.LocalProjectFolder, via reflection so we do not
            //    depend on the concrete project type.
            try
            {
                var proj = (object)_activeDocument?.Project;
                var mi = proj?.GetType().GetMethod("GetProjectInfo", Type.EmptyTypes);
                var info = mi?.Invoke(proj, null);
                var folder = info?.GetType().GetProperty("LocalProjectFolder")?.GetValue(info, null) as string;
                note("ProjectInfo.LocalProjectFolder", folder);
                if (!string.IsNullOrEmpty(folder))
                {
                    // FindProjectTms takes the DIRECTORY of what it is given, so
                    // hand it a path inside the folder rather than the folder.
                    return System.IO.Path.Combine(folder, "anchor.sdlxliff");
                }
            }
            catch (Exception ex) { note("ProjectInfo.LocalProjectFolder", "threw: " + ex.Message); }

            // 3. The active file's own path.
            try
            {
                var v = _activeDocument?.ActiveFile?.LocalFilePath;
                note("ActiveFile.LocalFilePath", v);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch (Exception ex) { note("ActiveFile.LocalFilePath", "threw: " + ex.Message); }

            // 4. Any string property on ActiveFile that looks like a rooted path.
            try
            {
                var af = (object)_activeDocument?.ActiveFile;
                if (af != null)
                {
                    foreach (var pi in af.GetType().GetProperties())
                    {
                        if (pi.PropertyType != typeof(string)) continue;
                        string v = null;
                        try { v = pi.GetValue(af, null) as string; } catch { }
                        if (string.IsNullOrEmpty(v)) continue;
                        if (v.Length > 3 && v[1] == ':' && v.IndexOf('\\') > 0)
                        {
                            note("ActiveFile." + pi.Name, v);
                            return v;
                        }
                    }
                }
                note("ActiveFile.<rooted-string-property>", null);
            }
            catch (Exception ex) { note("ActiveFile.<scan>", "threw: " + ex.Message); }

            // 5. Any file of the document.
            try
            {
                var files = TryGetEnumerable(_activeDocument, "Files");
                if (files != null)
                    foreach (var f in files)
                    {
                        var v = f?.GetType().GetProperty("LocalFilePath")?.GetValue(f, null) as string;
                        if (!string.IsNullOrEmpty(v)) { note("Files[].LocalFilePath", v); return v; }
                    }
                note("Files[].LocalFilePath", null);
            }
            catch (Exception ex) { note("Files[]", "threw: " + ex.Message); }

            return null;
        }

        private BridgeTmCompareResponse BridgeCompareTm(BridgeTmCompareQuery q)
        {
            var raw = BridgeCollectRawSegments();
            if (raw == null)
                return new BridgeTmCompareResponse
                {
                    Available = false,
                    Error = "No document is open in the Trados editor."
                };

            // Resolving the project's TMs needs the active file, so read that on
            // the UI thread before going wide.
            string activeFilePath = null;
            var anchorTrace = new List<string>();
            var ctrl = _control?.Value;
            try
            {
                if (ctrl != null && !ctrl.IsDisposed && ctrl.InvokeRequired)
                    activeFilePath = (string)ctrl.Invoke(new Func<string>(
                        () => ResolveProjectAnchorPathCore(anchorTrace)));
                else if (UiThread.InvokeRequired && UiThread.IsAvailable)
                    activeFilePath = UiThread.Invoke(() => ResolveProjectAnchorPathCore(anchorTrace));
                else
                    activeFilePath = ResolveProjectAnchorPathCore(anchorTrace);
            }
            catch { }

            if (string.IsNullOrEmpty(activeFilePath))
                return new BridgeTmCompareResponse
                {
                    Available = false,
                    Error = "Could not locate the open project on disk, so its TMs cannot be read. "
                        + "Routes tried: " + string.Join("; ", anchorTrace)
                };

            var tmEntries = Core.TmSearcher.FindProjectTms(activeFilePath) ?? new List<string>();
            if (!string.IsNullOrWhiteSpace(q.Tm))
                tmEntries = tmEntries
                    .Where(t => (Core.TmSearcher.DisplayName(t) ?? "")
                        .IndexOf(q.Tm.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

            if (tmEntries.Count == 0)
                return new BridgeTmCompareResponse
                {
                    Available = false,
                    Error = string.IsNullOrWhiteSpace(q.Tm)
                        ? "No translation memory is attached to this project."
                        : "No TM matching that name is attached to this project - call list_resources for the list."
                };

            var response = new BridgeTmCompareResponse
            {
                Available = true,
                TmsCompared = new List<string>(),
                Items = new List<BridgeTmDeviation>()
            };

            // One budget shared across every TM, so the call still answers inside
            // the caller's HTTP timeout instead of dying without a reply.
            var budget = TimeSpan.FromSeconds(20);
            var started = System.Diagnostics.Stopwatch.StartNew();

            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in tmEntries)
            {
                var remaining = budget - started.Elapsed;
                if (remaining <= TimeSpan.Zero) { response.TmPartiallyRead = true; break; }

                int read;
                bool complete;
                string err;
                var part = Core.TmComparer.BuildIndex(entry, 250000, remaining,
                    out read, out complete, out err);
                response.TmUnitsRead += read;
                if (!complete) response.TmPartiallyRead = true;
                if (err != null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[SupervertalerBridge] compare-tm: " + entry + ": " + err);
                    continue;
                }
                response.TmsCompared.Add(Core.TmSearcher.DisplayName(entry));

                foreach (var kv in part)
                {
                    List<string> existing;
                    if (!index.TryGetValue(kv.Key, out existing)) { index[kv.Key] = kv.Value; continue; }
                    foreach (var t in kv.Value)
                        if (!existing.Contains(t, StringComparer.Ordinal)) existing.Add(t);
                }
            }

            var wantStatus = string.IsNullOrWhiteSpace(q.Status) ? null : q.Status.Trim();
            foreach (var seg in raw)
            {
                if (string.IsNullOrEmpty(seg.TargetRaw)) continue;
                if (wantStatus == null)
                {
                    // Default to finished work: an unconfirmed segment differing
                    // from the TM is not news.
                    if (!string.Equals(seg.Status, "Translated", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(seg.Status, "ApprovedTranslation", StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                else if (!string.Equals(seg.Status, wantStatus, StringComparison.OrdinalIgnoreCase))
                    continue;

                response.SegmentsChecked++;

                var key = Core.TmComparer.NormaliseWhitespace(seg.SourceRaw);
                List<string> tmTargets;
                if (key.Length == 0 || !index.TryGetValue(key, out tmTargets) || tmTargets.Count == 0)
                    continue;

                response.ExactSourceHits++;

                var mine = Core.TmComparer.NormaliseWhitespace(seg.TargetRaw);
                if (tmTargets.Contains(mine, StringComparer.Ordinal)) continue;

                response.Deviations++;
                if (response.Items.Count >= q.Limit) { response.Truncated = true; continue; }

                response.Items.Add(new BridgeTmDeviation
                {
                    Id = seg.Id,
                    Number = seg.Id != null && seg.Id.LastIndexOf(':') >= 0
                        ? seg.Id.Substring(seg.Id.LastIndexOf(':') + 1) : null,
                    FileName = seg.FileName,
                    Source = TruncateForBridge(seg.SourceRaw, 300),
                    DocumentTarget = TruncateForBridge(seg.TargetRaw, 300),
                    TmTargets = tmTargets.Select(t => TruncateForBridge(t, 300)).Take(3).ToList()
                });
            }

            response.Returned = response.Items.Count;

            if (response.TmsCompared.Count == 0)
                response.Note = "None of the attached TMs could be read.";
            else if (response.ExactSourceHits == 0)
                response.Note = "No segment's source was found in the TM verbatim, so there was nothing to "
                    + "compare. That is normal for a document translated from scratch; it does NOT mean the "
                    + "translation agrees with the TM.";
            else if (response.Deviations == 0)
                response.Note = "All " + response.ExactSourceHits + " segment(s) whose source appears in the "
                    + "TM match what the TM holds. IMPORTANT: this proves agreement with the memory, not "
                    + "correctness. If the memory itself is contaminated (pooled boilerplate from unrelated "
                    + "products, stale fuzzy matches), the document agreeing with it is the SYMPTOM, and this "
                    + "result is circular. On one real job this check reported 0 deviations across 600 hits on "
                    + "a memory that was itself the source of ~40 defects. Do not cite this result as evidence "
                    + "the translation is right - only that it does not diverge from the TM.";
            else
                response.Note = response.Deviations + " of " + response.ExactSourceHits + " segment(s) with an "
                    + "exact source match are translated differently from the TM. A difference is NOT "
                    + "automatically an error - a deliberate improvement looks exactly like a mistake here - so "
                    + "present these for the user to review rather than aligning them automatically.";

            if (response.Truncated)
                response.Note = "Showing " + response.Returned + " of " + response.Deviations + ". " + response.Note;
            if (response.TmPartiallyRead)
                response.Note += " WARNING: a TM was too large to read completely in the time available, so some "
                    + "segments may not have been compared at all. Treat this as a partial answer.";

            return response;
        }

        /// <summary>
        /// Whether an expected termbase target rendering is present in the
        /// target text, tolerating inflection. Exact (case-insensitive)
        /// substring first; failing that, for single-word terms of 4+
        /// characters, a target word counts as a hit when it shares a
        /// word-initial stem with the term - "gereedschap" satisfies an
        /// expected "gereedschappen", which the plain substring check reported
        /// as missing and thereby flooded the terminology check with false
        /// positives. The stem rule: the shared prefix must cover all but the
        /// last two characters of the shorter of the two words, and always at
        /// least four characters, so short unrelated words do not collide.
        /// Multi-word terms stay exact-substring: their false-positive rate was
        /// never the problem, and stemming each word would over-match.
        /// </summary>
        private static bool TermFoundInTarget(string target, string term)
        {
            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(term)) return false;
            if (target.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (term.IndexOf(' ') >= 0 || term.Length < 4) return false;

            foreach (var word in System.Text.RegularExpressions.Regex
                     .Split(target, @"[^\p{L}\p{Nd}\-']+"))
            {
                if (word.Length < 4) continue;
                int max = Math.Min(word.Length, term.Length);
                int shared = 0;
                while (shared < max && char.ToLowerInvariant(word[shared]) == char.ToLowerInvariant(term[shared]))
                    shared++;
                int needed = Math.Max(4, max - 2);
                if (shared >= needed) return true;
            }
            return false;
        }

        /// <summary>Shortens a value for a bridge response, marking that it was shortened.</summary>
        private static string TruncateForBridge(string s, int max)
        {
            return string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "\u2026";
        }

        /// <summary>TM match percentage of a segment pair, or null when it has
        /// no TM/MT origin. Same reading as the get_segments matchMin/matchMax
        /// filter uses. Must be called on the UI thread (SDK access).</summary>
        private static int? BridgeReadMatchPercent(ISegmentPair pair)
        {
            try
            {
                var origin = pair.Properties?.TranslationOrigin;
                if (origin == null) return null;
                var originType = string.IsNullOrEmpty(origin.OriginType) ? null : origin.OriginType;
                if (originType == null || originType == "not-translated") return null;
                return origin.MatchPercent;
            }
            catch { return null; }
        }

        // ── Coverage tracking (session-scoped) ─────────────────────────────
        //
        // The root cause of the worst QA miss on record (84 defects behind two
        // "clean" QA passes, PO414646) was not any single broken check - it was
        // that nothing tracked which segments had actually been LOOKED AT, so
        // an agent could fix three defect categories, re-run the suite, see
        // green, and stop without ever reading ~40 of the 220 fuzzy segments.
        // These sets make "have I been through the fuzzy band?" a queryable
        // fact instead of a memory. Held in the plugin, never written to the
        // document: no file pollution, cleared when Studio restarts, and the
        // response notes say so.
        private readonly object _bridgeCoverageLock = new object();
        private readonly HashSet<string> _bridgeWrittenIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _bridgeReviewedIds = new HashSet<string>(StringComparer.Ordinal);

        private void BridgeRecordWrite(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_bridgeCoverageLock) _bridgeWrittenIds.Add(id);
        }

        /// <summary>Bridge delegate for POST /v1/mark-reviewed (MCP mark_reviewed).</summary>
        private BridgeMarkReviewedResponse BridgeMarkReviewed(BridgeMarkReviewedRequest req)
        {
            var raw = BridgeCollectRawSegments();
            if (raw == null)
                return new BridgeMarkReviewedResponse { Ok = false, Note = "No document is open in the Trados editor." };

            // Validate against the real document so a typo'd id cannot create
            // phantom coverage - a review claim that silently marks nothing is
            // exactly the false assurance this feature exists to end.
            var known = new HashSet<string>(raw.Select(s => s.Id), StringComparer.Ordinal);
            var unknown = new List<string>();
            int marked = 0, reviewedTotal;
            lock (_bridgeCoverageLock)
            {
                foreach (var id in req.Ids)
                {
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var trimmed = id.Trim();
                    if (!known.Contains(trimmed)) { unknown.Add(trimmed); continue; }
                    if (_bridgeReviewedIds.Add(trimmed)) marked++;
                    else marked++; // idempotent re-mark still counts as handled
                }
                reviewedTotal = _bridgeReviewedIds.Count;
            }

            return new BridgeMarkReviewedResponse
            {
                Ok = unknown.Count == 0,
                Marked = marked,
                UnknownIds = unknown.Count > 0 ? unknown : null,
                ReviewedThisSession = reviewedTotal,
                Note = unknown.Count > 0
                    ? unknown.Count + " id(s) do not exist in the open document and were NOT marked - check them against get_segments."
                    : "Marked as reviewed for this session. Only mark segments you have actually read source-against-target; " +
                      "marking what you merely scrolled past recreates the false assurance this tracking exists to end."
            };
        }

        /// <summary>Bridge delegate for GET /v1/coverage (MCP get_coverage).</summary>
        private BridgeCoverageResponse BridgeGetCoverage()
        {
            var raw = BridgeCollectRawSegments();
            if (raw == null)
                return new BridgeCoverageResponse { Available = false, Note = "No document is open in the Trados editor." };

            HashSet<string> written, reviewed;
            lock (_bridgeCoverageLock)
            {
                written = new HashSet<string>(_bridgeWrittenIds, StringComparer.Ordinal);
                reviewed = new HashSet<string>(_bridgeReviewedIds, StringComparer.Ordinal);
            }

            // Risk-first band order: the 85-99 band is where a stale fuzzy
            // match reads fluent and plausible, so it leads.
            var bandOrder = new[] { "95-99", "85-94", "100", "70-84", "1-69", "no-match" };
            string BandOf(int? m)
            {
                int v = m ?? 0;
                if (v >= 100) return "100";
                if (v >= 95) return "95-99";
                if (v >= 85) return "85-94";
                if (v >= 70) return "70-84";
                if (v >= 1) return "1-69";
                return "no-match";
            }

            var bands = bandOrder.ToDictionary(b => b, b => new BridgeCoverageBand
            {
                Band = b,
                UncoveredIds = new List<string>()
            });

            const int maxIdsPerBand = 40;
            int locked = 0, uncoveredTotal = 0;
            foreach (var s in raw)
            {
                if (s.IsLocked) { locked++; continue; }
                var band = bands[BandOf(s.MatchPercent)];
                band.Total++;
                if (written.Contains(s.Id)) band.Written++;
                else if (reviewed.Contains(s.Id)) band.Reviewed++;
                else
                {
                    band.Uncovered++;
                    uncoveredTotal++;
                    if (band.UncoveredIds.Count < maxIdsPerBand) band.UncoveredIds.Add(s.Id);
                    else band.UncoveredIdsTruncated = true;
                }
            }
            foreach (var b in bands.Values)
                if (b.UncoveredIds.Count == 0) b.UncoveredIds = null;

            var response = new BridgeCoverageResponse
            {
                Available = true,
                TotalSegments = raw.Count,
                LockedExcluded = locked,
                WrittenThisSession = written.Count,
                ReviewedThisSession = reviewed.Count,
                UncoveredTotal = uncoveredTotal,
                Bands = bandOrder.Select(b => bands[b]).ToList()
            };

            response.Note = (uncoveredTotal > 0
                    ? uncoveredTotal + " non-locked segment(s) have been neither written nor explicitly marked " +
                      "reviewed in this session - no delivery note may claim they were looked at. The fuzzy bands " +
                      "(85-99 especially) are where stale matches read fluent and plausible; read those " +
                      "source-against-target and mark_reviewed what you deliberately leave unchanged. "
                    : "Every non-locked segment has been written or explicitly marked reviewed this session. ")
                + "Session-scoped: this tracking lives in the plugin's memory, clears when Studio restarts, and " +
                  "cannot see edits or reviews the user made by hand in Studio - it tracks THIS assistant's work only.";

            return response;
        }

        /// <summary>
        /// Bridge delegate for GET /v1/tracked-changes (MCP get_tracked_changes).
        /// Walks the open document for segments whose target carries tracked
        /// changes (IRevisionMarker) and returns (before, after) pairs – the
        /// target as it stood before the edits vs. the reviewed final. With
        /// save=true the FULL harvest (not just the returned page) is also
        /// written as a Markdown file into the active SuperMemory bank's
        /// reference/, as source material to draw style rules and terminology from.
        /// </summary>
        private BridgeTrackedChangesResponse BridgeGetTrackedChanges(BridgeTrackedChangesQuery query)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeTrackedChangesResponse { Available = false };
            if (ctrl.InvokeRequired)
                return (BridgeTrackedChangesResponse)ctrl.Invoke(
                    new Func<BridgeTrackedChangesResponse>(() => BridgeGetTrackedChanges(query)));
            // Panel handle may not exist yet – see Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeGetTrackedChanges(query));

            if (_activeDocument == null)
                return new BridgeTrackedChangesResponse
                {
                    Available = false,
                    Note = "No document is open in the Trados editor."
                };

            EnsureBridgeFileMapFresh();
            bool attributeFiles = _perFileMappingWorked && _fileIdToName.Count > 1;

            string Collapse(string s) =>
                System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();

            var all = new List<BridgeTrackedChangeRecord>();
            int scanned = 0, noNetChange = 0, processed = 0;

            foreach (var pair in _activeDocument.SegmentPairs)
            {
                processed++;
                if (processed % 200 == 0)
                    System.Windows.Forms.Application.DoEvents();
                try
                {
                    if (pair.Target == null) continue;
                    scanned++;

                    List<string> authors;
                    DateTime? lastDate;
                    if (!Core.SegmentTagHandler.TryCollectRevisionInfo(pair.Target, out authors, out lastDate))
                        continue;

                    var before = Collapse(Core.SegmentTagHandler.GetOriginalText(pair.Target));
                    var after = Collapse(Core.SegmentTagHandler.GetFinalText(pair.Target));

                    // Revision markers with no net text effect (formatting-only
                    // edits, or an edit typed and reverted) teach nothing; a
                    // wholly inserted or wholly deleted target is not a
                    // correction pair either.
                    if (before == after || before.Length == 0 || after.Length == 0)
                    {
                        noNetChange++;
                        continue;
                    }

                    var sourceSer = Core.SegmentTagHandler.Serialize(pair.Source);
                    var source = Collapse(Core.SegmentTagHandler.StripTagPlaceholders(sourceSer.SerializedText ?? ""));

                    var puId = _activeDocument.GetParentParagraphUnit(pair)
                        ?.Properties?.ParagraphUnitId.Id ?? "";
                    var segId = pair.Properties?.Id.Id ?? "";

                    string fileName = null;
                    if (attributeFiles)
                    {
                        string fid;
                        if (_puIdToFileId.TryGetValue(puId, out fid) && fid != null)
                            _fileIdToName.TryGetValue(fid, out fileName);
                    }

                    all.Add(new BridgeTrackedChangeRecord
                    {
                        Id = puId + ":" + segId,
                        FileName = fileName,
                        Source = source,
                        Before = before,
                        After = after,
                        Authors = authors,
                        LastDate = lastDate?.ToString("yyyy-MM-dd HH:mm"),
                        Status = (pair.Properties?.ConfirmationLevel
                            ?? Sdl.Core.Globalization.ConfirmationLevel.Unspecified).ToString()
                    });
                }
                catch { /* skip unreadable segment */ }
            }

            var response = new BridgeTrackedChangesResponse
            {
                Available = true,
                SegmentsScanned = scanned,
                SegmentsWithChanges = all.Count,
                Changes = all.Take(query.Limit).ToList(),
                Truncated = all.Count > query.Limit
            };

            if (query.Save && all.Count > 0)
            {
                try
                {
                    response.SavedTo = SaveTrackedChangesHarvest(all);
                    response.SavedToBank = ActiveMemoryBankName;
                    RefreshSuperMemoryInboxCount();
                }
                catch (Exception ex)
                {
                    response.Note = "Changes were extracted, but saving to the memory bank failed: "
                        + ex.Message + " ";
                }
            }

            if (all.Count == 0)
            {
                response.Note = (response.Note ?? "")
                    + "No segments with net tracked changes were found. Studio only records tracked "
                    + "changes when Track Changes was switched ON during editing"
                    + (noNetChange > 0
                        ? $" ({noNetChange} segment(s) carried revision markers with no net text effect and were skipped)."
                        : ".");
            }
            else
            {
                response.Note = (response.Note ?? "")
                    + "'before' is the target as it stood before the tracked edits (e.g. the AI draft "
                    + "or fuzzy match as offered); 'after' is the reviewed final. Target-side revisions "
                    + "only, formatting-only edits excluded."
                    + (response.Truncated
                        ? $" Response shows the first {query.Limit} of {all.Count} changes (raise 'limit' to page)."
                        : "")
                    + (response.SavedTo != null
                        ? $" The FULL harvest was saved to the reference folder of memory bank '{response.SavedToBank}'. Nothing reads it automatically - fold what matters into brief.md, terminology.md or style.md."
                        : (query.Save
                            ? ""
                            : " Pass save=true to write the full harvest into the active SuperMemory bank's inbox for future projects."));
            }

            return response;
        }

        /// <summary>
        /// Writes a tracked-changes harvest as a Markdown note into the active
        /// SuperMemory bank's reference/ folder (same flow as chat's "save to memory
        /// bank"). Returns the path written. MUST be called on the UI thread.
        /// </summary>
        private string SaveTrackedChangesHarvest(List<BridgeTrackedChangeRecord> changes)
        {
            var vaultDir = ActiveMemoryBankDir;
            var bankName = ActiveMemoryBankName;
            if (!Directory.Exists(vaultDir))
                throw new InvalidOperationException(
                    $"Memory bank '{bankName}' does not exist yet (expected at {vaultDir}).");

            var inboxDir = Path.Combine(vaultDir, MemoryBankReader.ReferenceFolder);
            Directory.CreateDirectory(inboxDir);

            string projectName = null, sourceLang = null, targetLang = null;
            try { projectName = GetProjectName(); } catch { }
            try
            {
                sourceLang = GetDocumentSourceLanguage();
                targetLang = GetDocumentTargetLanguage();
            }
            catch { }

            var safeProject = string.IsNullOrWhiteSpace(projectName)
                ? "project"
                : System.Text.RegularExpressions.Regex.Replace(
                    projectName.ToLowerInvariant(), @"[^a-z0-9-_]+", "-").Trim('-');
            if (safeProject.Length > 40) safeProject = safeProject.Substring(0, 40).Trim('-');
            if (safeProject.Length == 0) safeProject = "project";

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var filePath = Path.Combine(inboxDir, $"tracked-changes-{safeProject}-{stamp}.md");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Tracked changes harvest – "
                + (string.IsNullOrWhiteSpace(projectName) ? "untitled project" : projectName));
            sb.AppendLine($"*Harvested {DateTime.Now:yyyy-MM-dd HH:mm} from Supervertaler for Trados*");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(sourceLang) || !string.IsNullOrWhiteSpace(targetLang))
                sb.AppendLine($"- **Language pair:** {sourceLang} → {targetLang}");
            sb.AppendLine($"- **Segments with tracked changes:** {changes.Count}");
            sb.AppendLine();
            sb.AppendLine("Each entry shows the target BEFORE the tracked edits (the draft as it was "
                + "offered, e.g. by AI translation or a fuzzy match) and AFTER them (the reviewed "
                + "final) - a record of how this translator actually corrects machine output.");
            sb.AppendLine();
            sb.AppendLine("This file is source material and nothing reads it automatically. Read it "
                + "yourself, or paste it into the assistant and ask what keeps recurring, then write "
                + "the decisions worth keeping into `terminology.md` (as table rows) and `style.md`. "
                + "A change that shows up once is an edit; one that shows up nine times is a rule.");
            sb.AppendLine();

            int n = 0;
            foreach (var c in changes)
            {
                n++;
                sb.AppendLine($"## {n}. Segment {c.Id}"
                    + (string.IsNullOrEmpty(c.FileName) ? "" : $" ({c.FileName})"));
                sb.AppendLine();
                sb.AppendLine($"- **Source:** {c.Source}");
                sb.AppendLine($"- **Before:** {c.Before}");
                sb.AppendLine($"- **After:** {c.After}");
                if (c.Authors != null && c.Authors.Count > 0)
                    sb.AppendLine($"- **Edited by:** {string.Join(", ", c.Authors)}"
                        + (c.LastDate != null ? $" ({c.LastDate})" : ""));
                sb.AppendLine();
            }

            File.WriteAllText(filePath, sb.ToString(), new System.Text.UTF8Encoding(false));
            return filePath;
        }

        /// <summary>
        /// Bridge delegate for GET /v1/qa-check (MCP check_numbers / check_tags /
        /// check_terminology). Snapshot on the UI thread, analysis off-thread.
        /// Only segments with a non-empty target are checked – untranslated
        /// segments can't have QA issues yet.
        /// </summary>
        private BridgeQaResponse BridgeRunQaCheck(BridgeQaQuery q)
        {
            var raw = BridgeCollectRawSegments();
            if (raw == null)
                return new BridgeQaResponse
                {
                    Available = false,
                    Note = "No document is open in the Trados editor."
                };

            var response = new BridgeQaResponse
            {
                Available = true,
                Check = q.Type,
                Issues = new List<BridgeQaIssue>()
            };

            var translated = raw.Where(s => !string.IsNullOrEmpty(s.Target)).ToList();
            response.SegmentsChecked = translated.Count;

            void AddIssue(BridgeRawSegment s, string detail)
            {
                response.IssuesFound++;
                if (response.Issues.Count >= q.Limit) { response.Truncated = true; return; }
                response.Issues.Add(new BridgeQaIssue
                {
                    Id = s.Id,
                    Status = s.Status,
                    Detail = detail,
                    Source = s.Source.Length > 160 ? s.Source.Substring(0, 160) + "…" : s.Source,
                    Target = s.Target.Length > 160 ? s.Target.Substring(0, 160) + "…" : s.Target,
                    FileName = s.FileName
                });
            }

            if (q.Type == "numbers")
            {
                foreach (var s in translated)
                {
                    var src = ExtractNumbers(s.Source);
                    var tgt = ExtractNumbers(s.Target);
                    if (!src.OrderBy(x => x).SequenceEqual(tgt.OrderBy(x => x)))
                        AddIssue(s, $"source numbers [{string.Join(", ", src)}] vs target [{string.Join(", ", tgt)}]");
                }
                if (response.IssuesFound == 0)
                    response.Note = "All numbers in translated segments match between source and target.";
            }
            else if (q.Type == "tags")
            {
                foreach (var s in translated)
                {
                    if (s.SourceTagCount != s.TargetTagCount)
                    {
                        AddIssue(s, $"source has {s.SourceTagCount} inline tag(s), target has {s.TargetTagCount}");
                        continue;
                    }

                    // Counts agree – now compare the underlying tag IDS as a
                    // multiset. Equal counts with unequal ids is precisely the
                    // corruption shape a stale fuzzy match leaves behind (two
                    // tags sharing one id), it fails Studio's Tag Verifier, and
                    // no count check can ever see it. Until 20.157 this check
                    // stopped at counts, which is how a whole band of corrupt
                    // segments on a real job passed check_tags while failing
                    // verification.
                    var idMismatch = DescribeTagIdListMismatch(s.SourceTagIds, s.TargetTagIds);
                    if (idMismatch != null)
                        AddIssue(s, idMismatch +
                            ". Re-send this segment with update_segments, copying the tag markers from its SOURCE field, to repair it");
                }
                if (response.IssuesFound == 0)
                    response.Note = "Inline tag counts and underlying tag ids match in every translated segment.";
                else
                    response.Note = (response.Note ?? "") +
                        "A count difference is not always an error (formatting may legitimately differ) – review each case. " +
                        "A tag-ID mismatch with matching counts, however, is always a defect: Studio's Tag Verifier will " +
                        "reject it, and re-writing the segment with the source's markers repairs it.";
            }
            else if (q.Type == "nbsp")
            {
                // Non-breaking spaces are invisible in every view the user and
                // the AI have, so a lost one is never noticed until the client
                // rejects the file. Compare counts on the RAW text: the
                // whitespace-collapsed fields would have folded U+00A0 into a
                // plain space already.
                foreach (var s in translated)
                {
                    int src = CountNbsp(s.SourceRaw);
                    int tgt = CountNbsp(s.TargetRaw);
                    if (src > 0 && tgt < src)
                        AddIssue(s, $"source has {src} non-breaking space(s), target has {tgt}");
                }
                if (response.IssuesFound == 0)
                    response.Note = "Every translated segment keeps at least as many non-breaking spaces as its source.";
                else
                    response.Note = "A missing non-breaking space is invisible on screen – verify against the " +
                        "source before fixing, as the target legitimately needs fewer in some segments. To write " +
                        "one back, use update_segments or find_and_replace with decodeEntities=true and write the " +
                        "character as &nbsp;. Sending the character itself – or the JSON escape \\u00a0 – is " +
                        "unreliable: it sometimes arrives intact and sometimes as an ordinary space, with success " +
                        "reported either way, so re-run this check afterwards to confirm the fix landed.";
            }
            else // terminology
            {
                Dictionary<string, List<TermEntry>> index;
                // Entries whose stored direction contradicts their termbase's.
                // Any of them that is genuinely reversed is indexed under the
                // wrong language and so was never matched by any source segment
                // – silence about it here means "not looked at", not "not
                // violated". Reported alongside the findings rather than left
                // to pass for a clean result.
                List<TermbaseDirectionMismatch> mismatched = null;
                try
                {
                    var settings = SettingsService.Current;
                    var dbPath = ResolveSupervertalerDbPath();
                    if (dbPath == null)
                        return new BridgeQaResponse
                        {
                            Available = false,
                            Note = "Supervertaler database not found – terminology check unavailable."
                        };
                    using (var reader = new TermbaseReader(dbPath))
                    {
                        if (!reader.Open())
                            return new BridgeQaResponse { Available = false, Note = "could not open Supervertaler database" };
                        var disabled = settings.DisabledTermbaseIds != null && settings.DisabledTermbaseIds.Count > 0
                            ? new HashSet<long>(settings.DisabledTermbaseIds) : null;
                        string projSrcLang = null;
                        try
                        {
                            var ctrl = _control?.Value;
                            projSrcLang = ctrl != null && !ctrl.IsDisposed && (ctrl.InvokeRequired || (UiThread.InvokeRequired && UiThread.IsAvailable))
                                ? UiThread.Invoke(() => GetDocumentSourceLanguage())
                                : GetDocumentSourceLanguage();
                        }
                        catch { }
                        index = reader.LoadAllTerms(disabled, settings.CaseSensitiveMatching, projSrcLang);
                        try { mismatched = reader.GetDirectionMismatchedTerms(disabled); }
                        catch { /* reporting aid only – never fail the check for it */ }
                    }

                    // Also include the Trados project's own termbases (.ttb /
                    // MultiTerm .sdltb) from TermLens's merged in-memory index,
                    // so the check verifies against ALL the terminology the
                    // translator actually sees while working.
                    foreach (var e in TermLensEditorViewPart.GetCurrentTermbaseTerms()
                             ?? new List<TermEntry>())
                    {
                        if (e == null || !e.IsMultiTerm || string.IsNullOrWhiteSpace(e.SourceTerm))
                            continue;
                        var key = e.SourceTerm.Trim();
                        List<TermEntry> list;
                        if (!index.TryGetValue(key, out list))
                        {
                            list = new List<TermEntry>();
                            index[key] = list;
                        }
                        list.Add(e);
                    }
                }
                catch (Exception ex)
                {
                    return new BridgeQaResponse { Available = false, Note = "terminology check failed: " + ex.Message };
                }

                // Aggregate violations per (term, termbase) instead of per
                // segment: one project-wide terminology divergence otherwise
                // floods the result with hundreds of identical findings.
                var groups = new Dictionary<string, BridgeQaTermGroup>(StringComparer.OrdinalIgnoreCase);
                var groupOrder = new List<string>();
                // Ranking metadata kept beside the groups rather than on the
                // DataContract - it steers the sort below and is not part of the
                // wire format.
                var groupIsProjectTb = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

                // Optional restriction to named termbases (name or numeric id).
                // On a job pairing a small curated client termbase with a large
                // general-domain one, this is the difference between signal and
                // hundreds of findings of noise.
                HashSet<string> tbFilter = null;
                if (q.Termbases != null && q.Termbases.Count > 0)
                    tbFilter = new HashSet<string>(
                        q.Termbases.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()),
                        StringComparer.OrdinalIgnoreCase);

                foreach (var s in translated)
                {
                    // Word n-grams (1..5) of the source, looked up in the term index.
                    var words = System.Text.RegularExpressions.Regex
                        .Split(s.Source, @"[^\p{L}\p{Nd}\-']+")
                        .Where(w => w.Length > 0).ToArray();
                    var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // Pass 1 - collect every span that matches a termbase entry.
                    // Junk grams are dropped here: entries of 1-2 characters ("m",
                    // "to", "No", "16") matched ordinary prose far more often than
                    // terminology (198 segments of "to" on one real job), and a
                    // gram with no letter at all is a number, not a term.
                    var candidates = new List<Tuple<int, int, List<TermEntry>>>();
                    for (int i = 0; i < words.Length; i++)
                    {
                        for (int n = 1; n <= 5 && i + n <= words.Length; n++)
                        {
                            var gram = string.Join(" ", words, i, n);
                            if (gram.Length < 3 || !gram.Any(char.IsLetter)) continue;
                            List<TermEntry> entries;
                            if (index.TryGetValue(gram, out entries))
                                candidates.Add(Tuple.Create(i, n, entries));
                        }
                    }

                    // Pass 2 - longest match wins. When "safety valve" and
                    // "valve" both match overlapping words, only "safety valve"
                    // is checked: the longest entry the termbase knows OWNS
                    // those words. Without this the generic single-word entry
                    // fired on every occurrence while the specific multi-word
                    // entry - the one that actually mattered, 15 segments of a
                    // regulated pressure-vessel component name on a real job -
                    // was never surfaced at all.
                    candidates.Sort((a, b) => b.Item2 != a.Item2 ? b.Item2 - a.Item2 : a.Item1 - b.Item1);
                    var consumed = new bool[words.Length];
                    foreach (var cand in candidates)
                    {
                        bool overlaps = false;
                        for (int w = cand.Item1; w < cand.Item1 + cand.Item2; w++)
                            if (consumed[w]) { overlaps = true; break; }
                        if (overlaps) continue;

                        // Apply the termbases=[...] restriction BEFORE claiming the
                        // span. Marking it consumed first let a longer entry from an
                        // EXCLUDED termbase eat the words and silently suppress a
                        // shorter entry from an included one - so narrowing a check
                        // to one termbase could hide that termbase's own findings,
                        // and report a clean result while doing it.
                        var applicable = cand.Item3
                            .Where(en => tbFilter == null
                                         || tbFilter.Contains(en.TermbaseName ?? "")
                                         || tbFilter.Contains(en.TermbaseId.ToString()))
                            .ToList();
                        if (applicable.Count == 0) continue;   // span stays available

                        for (int w = cand.Item1; w < cand.Item1 + cand.Item2; w++)
                            consumed[w] = true;

                        foreach (var entry in applicable)
                        {
                            if (entry.Forbidden) continue;
                            if (string.IsNullOrWhiteSpace(entry.TargetTerm)) continue;
                            if (reported.Contains(entry.SourceTerm)) continue;

                            var expected = new List<string> { entry.TargetTerm };
                            if (entry.TargetSynonyms != null) expected.AddRange(entry.TargetSynonyms);
                            expected = expected.Where(x => !string.IsNullOrWhiteSpace(x))
                                               .Select(x => x.Trim()).ToList();

                            bool found = expected.Any(x => TermFoundInTarget(s.Target, x));
                            if (!found)
                            {
                                reported.Add(entry.SourceTerm);
                                response.IssuesFound++;

                                var key = entry.SourceTerm + "" + (entry.TermbaseName ?? "");
                                BridgeQaTermGroup g;
                                if (!groups.TryGetValue(key, out g))
                                {
                                    g = new BridgeQaTermGroup
                                    {
                                        Term = entry.SourceTerm,
                                        Termbase = entry.TermbaseName,
                                        Expected = expected,
                                        SegmentsAffected = 0,
                                        SampleSegmentIds = new List<string>(),
                                        ExampleTarget = s.Target.Length > 120
                                            ? s.Target.Substring(0, 120) + "…" : s.Target
                                    };
                                    groups[key] = g;
                                    groupOrder.Add(key);
                                    groupIsProjectTb[key] = entry.IsProjectTermbase;
                                }
                                g.SegmentsAffected++;
                                if (g.SampleSegmentIds.Count < 5)
                                    g.SampleSegmentIds.Add(s.Id);
                            }
                        }
                    }
                }

                // Rank by SIGNAL, not by raw segment count. The old
                // most-affected-first sort put the noisiest general-domain
                // entries on top - yet the response note itself said that a
                // term affecting very many segments usually means the project
                // consistently uses a different translation, i.e. a decision,
                // not a defect. That is an argument for sorting such groups
                // DOWN, not up. What predicts a real finding: a multi-word term
                // (someone curated a phrase), a project/client termbase over a
                // general-domain one, and a moderate segment count over a huge
                // one.
                Func<string, int> signalScore = key =>
                {
                    var g = groups[key];
                    int score = 0;
                    if (g.Term != null && g.Term.IndexOf(' ') >= 0) score += 2000;
                    bool isProj;
                    if (groupIsProjectTb.TryGetValue(key, out isProj) && isProj) score += 1000;
                    score += Math.Min(g.SegmentsAffected, 30) * 10;
                    if (g.SegmentsAffected > 60) score -= 500;
                    return score;
                };

                response.TermsAffected = groups.Count;
                response.TermGroups = groupOrder
                    .OrderByDescending(signalScore)
                    .ThenByDescending(k => groups[k].SegmentsAffected)
                    .Select(k => groups[k])
                    .Take(q.Limit)
                    .ToList();
                if (groups.Count > q.Limit) response.Truncated = true;

                // Honour the same termbases=[...] restriction the findings use,
                // so a check narrowed to one client termbase isn't told about
                // unrelated damage elsewhere in the database.
                if (mismatched != null && mismatched.Count > 0)
                {
                    var reportable = mismatched
                        .Where(m => tbFilter == null
                                    || tbFilter.Contains(m.TermbaseName ?? "")
                                    || tbFilter.Contains(m.TermbaseId.ToString()))
                        .Select(m => new BridgeQaDirectionMismatch
                        {
                            Termbase = m.TermbaseName,
                            DeclaredDirection = m.DeclaredDirection,
                            Entries = m.Count,
                            Samples = m.Samples
                        })
                        .ToList();
                    if (reportable.Count > 0) response.DirectionMismatches = reportable;
                }

                if (response.IssuesFound == 0)
                    response.Note = "Every termbase term found in a source segment has its expected translation " +
                        "in the target. (Longest-match-wins span matching; entries shorter than 3 characters " +
                        "are not checked.)";
                else
                    response.Note = $"{response.IssuesFound} segment-level finding(s) across " +
                        $"{groups.Count} distinct term(s), grouped per term and ranked by SIGNAL rather " +
                        "than raw count: multi-word and project-termbase entries first, because those are " +
                        "curated. A term affecting a very large share of segments ranks LOWER - that " +
                        "pattern usually means the project consistently uses a different translation than " +
                        "the termbase, which is a decision to put to the user, not a defect to fix. " +
                        "Matching tolerates inflected target forms via shared word-start stems, so " +
                        "residual false positives are rarer but still possible. Pass termbases=[...] " +
                        "(names or ids from list_resources) to restrict the check to a curated client " +
                        "termbase.";

                if (response.DirectionMismatches != null)
                {
                    int flagged = response.DirectionMismatches.Sum(m => m.Entries);
                    response.Note = (response.Note ?? "") +
                        $" SEPARATELY: {flagged} entr(ies) have a stored direction that contradicts their " +
                        "termbase's – see directionMismatches. Read the sampled pairs. Any whose TEXT is the " +
                        "wrong way round (source column holding the termbase's target language) is indexed " +
                        "under the wrong language, so no source segment can match it and THIS CHECK SAID " +
                        "NOTHING ABOUT IT – not because the document honours the term, but because the term " +
                        "was never looked at. Those entries still appear in the termbase and still answer " +
                        "lookup_term, so nothing else signals it either, and a term locked precisely because " +
                        "it was a known defect source is exactly the kind that ends up here. Entries where " +
                        "only the language labels are wrong, and the terms are correctly oriented, were " +
                        "checked normally and need nothing beyond a label fix – both kinds are in this list " +
                        "and only the pairs themselves tell you which is which, so do not report them all as " +
                        "broken.";
                }
            }

            response.Returned = q.Type == "terminology"
                ? (response.TermGroups?.Count ?? 0)
                : response.Issues.Count;
            if (response.Truncated && q.Type != "terminology")
                response.Note = $"Only {response.Returned} of {response.IssuesFound} issues returned – raise 'limit' for more. "
                    + (response.Note ?? "");
            return response;
        }

        /// <summary>Counts U+00A0 in a string. Deliberately only the no-break
        /// space, not the whole Unicode space family: the narrow no-break space
        /// and friends are separate characters a style guide may or may not
        /// want, and folding them together would hide exactly the substitution
        /// this check exists to catch.</summary>
        private static int CountNbsp(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int n = 0;
            foreach (var c in text) if (c == '\u00a0') n++;
            return n;
        }

        private static List<string> ExtractNumbers(string text)
        {
            // Number tokens incl. decimal/thousand separators; normalized by
            // stripping separators so 1.234,56 == 1,234.56 == 1234.56 digits-wise.
            var result = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(text ?? "", @"\p{Nd}+(?:[.,  ]\p{Nd}+)*"))
            {
                result.Add(System.Text.RegularExpressions.Regex.Replace(m.Value, @"[.,  ]", ""));
            }
            return result;
        }

        /// <summary>
        /// Counts termbases, how many are read-enabled, and how many of those may
        /// actually be sent to the AI, across both Supervertaler termbases and the
        /// Trados project's own. Returns false when the state can't be determined –
        /// callers then say nothing rather than warn on a guess.
        ///
        /// Termbase activation is per project, so a project where everything is
        /// switched off looks identical over the bridge to one with no termbases
        /// attached: TermLens simply returns no matches, and nothing in the MCP
        /// surface says why. Field report: a whole job was translated with two
        /// relevant termbases silently inactive, and it only came to light
        /// because the translator happened to ask.
        ///
        /// <paramref name="aiEnabled"/> counts termbases that are read-enabled AND
        /// AI-enabled, because both are required for a term to reach a prompt: the
        /// in-memory index only holds read-enabled termbases, and every AI path
        /// then filters that by <see cref="AiSettings.IsTermbaseAiEnabled"/>. The
        /// two ticks being separate is the point – see issue #58, where a machine
        /// with two read-enabled termbases (one of them a 221-term job-specific
        /// glossary) had been sending an empty glossary in every prompt.
        /// </summary>
        private bool TryCountTermbaseActivation(out int total, out int readEnabled, out int aiEnabled)
        {
            readEnabled = 0;
            aiEnabled = 0;
            total = 0;
            bool known = false;

            var aiCfg = _settings?.AiSettings ?? SettingsService.Current?.AiSettings ?? new AiSettings();

            try
            {
                var dbPath = ResolveSupervertalerDbPath();
                if (!string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
                {
                    var settings = SettingsService.Current;
                    var disabled = settings?.DisabledTermbaseIds != null
                        ? new HashSet<long>(settings.DisabledTermbaseIds) : new HashSet<long>();
                    using (var tbReader = new TermbaseReader(dbPath))
                    {
                        if (tbReader.Open())
                        {
                            foreach (var tb in tbReader.GetTermbases() ?? new List<TermbaseInfo>())
                            {
                                total++;
                                if (disabled.Contains(tb.Id)) continue;
                                readEnabled++;
                                if (aiCfg.IsTermbaseAiEnabled(tb.Id)) aiEnabled++;
                            }
                            known = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SupervertalerBridge] termbase enable-count (supervertaler) threw: {ex.Message}");
            }

            try
            {
                foreach (var info in TermLensEditorViewPart.GetMultiTermInfos()
                         ?? new List<Models.MultiTermTermbaseInfo>())
                {
                    total++;
                    known = true;
                    if (!info.IsEnabled || info.LoadMode == Models.MultiTermLoadMode.Failed) continue;
                    readEnabled++;
                    if (aiCfg.IsTermbaseAiEnabled(info.SyntheticId)) aiEnabled++;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SupervertalerBridge] termbase enable-count (studio) threw: {ex.Message}");
            }

            return known;
        }

        /// <summary>
        /// A termbase-setup warning, or null when terminology can actually reach
        /// the AI (or the state is unknown).
        ///
        /// Two distinct failures, deliberately worded differently. Read and AI are
        /// separate ticks in separate places, and telling someone to check the Read
        /// tick when Read is already on sends them round in a circle – which is
        /// exactly what the single old warning would have done, had it fired at all
        /// for the AI case. It didn't: it only ever tested Read (issue #58).
        /// </summary>
        private string BridgeTermbaseWarning()
        {
            int total, readEnabled, aiEnabled;
            if (!TryCountTermbaseActivation(out total, out readEnabled, out aiEnabled)) return null;
            if (total == 0) return null;

            if (readEnabled == 0)
                return $"No termbase is read-enabled for this project ({total} available), so terminology lookups " +
                       "and TermLens will return nothing at all. This is almost always a misconfiguration – tell " +
                       "the user before relying on terminology, and point them at Supervertaler settings > " +
                       "Termbases to switch the relevant termbases back on.";

            if (aiEnabled == 0)
                return $"{readEnabled} termbase(s) are read-enabled, but NONE of them is enabled for AI, so every " +
                       "prompt goes out with an empty glossary while TermLens still shows term matches on screen. " +
                       "Read and AI are separate ticks: the AI one is the 'AI' column in the termbase grid at " +
                       "Supervertaler settings > Termbases. Termbases default to NOT being sent to the AI, so this " +
                       "is the out-of-the-box state rather than something the user chose. Tell them before relying " +
                       "on terminology – a prompt carrying no glossary is indistinguishable from a model that " +
                       "ignored one.";

            return null;
        }

        /// <summary>
        /// Logs the same "read-enabled but nothing AI-enabled" warning into a batch
        /// run's own log, for the far larger population who never call the MCP
        /// bridge. Uses the assembled term lists rather than re-counting termbases:
        /// terms loaded but none surviving the AI filter is the precise condition,
        /// and it costs nothing at the point where both lists already exist.
        /// </summary>
        private static void WarnIfNoAiTermbases(BatchTranslateControl batchControl, int loadedTerms, int aiTerms)
        {
            if (batchControl == null || loadedTerms <= 0 || aiTerms > 0) return;
            try
            {
                batchControl.AppendLog(
                    $"Warning: {loadedTerms} terms are loaded, but no termbase is enabled for AI – this run will " +
                    "send NO glossary. Tick the 'AI' column in Settings > Termbases for the termbases you want the " +
                    "AI to use (that is a separate tick from Read, and it is off by default).", true);
            }
            catch { /* a warning that cannot be logged must not stop the batch */ }
        }

        /// <summary>
        /// Bridge delegate for GET /v1/resources (MCP list_resources). Lists the
        /// TMs (Studio project TMs + Supervertaler bridged TMs) and Supervertaler
        /// termbases with their settings flags. Only the active-file path and
        /// language read hop to the UI thread.
        /// </summary>
        private BridgeResourcesResponse BridgeListResources()
        {
            var response = new BridgeResourcesResponse
            {
                Available = true,
                Tms = new List<BridgeTmResource>(),
                Termbases = new List<BridgeTermbaseResource>()
            };

            // Studio project TMs (file + GroupShare) – needs the active file path.
            string activeFilePath = null;
            try
            {
                var ctrl = _control?.Value;
                if (ctrl != null && !ctrl.IsDisposed && (ctrl.InvokeRequired || (UiThread.InvokeRequired && UiThread.IsAvailable)))
                    activeFilePath = (string)ctrl.Invoke(new Func<string>(
                        () => ResolveProjectAnchorPathCore()));
                else
                    activeFilePath = ResolveProjectAnchorPathCore();
            }
            catch { }

            if (!string.IsNullOrEmpty(activeFilePath))
            {
                try
                {
                    foreach (var entry in Core.TmSearcher.FindProjectTms(activeFilePath) ?? new List<string>())
                    {
                        response.Tms.Add(new BridgeTmResource
                        {
                            Name = Core.TmSearcher.DisplayName(entry),
                            Kind = ServerTmClient.IsServerTmUri(entry) ? "studio-server" : "studio-file"
                        });
                    }
                }
                catch (Exception ex)
                {
                    response.Note = "Studio TMs could not be enumerated: " + ex.Message;
                }
            }
            else
            {
                response.Note = "No document open in the editor – Studio project TMs not listed.";
            }

            var dbPath = ResolveSupervertalerDbPath();
            if (dbPath != null)
            {
                try
                {
                    using (var tmReader = new TmReader(dbPath))
                    {
                        if (tmReader.Open())
                        {
                            foreach (var tm in tmReader.GetBridgedTms() ?? new List<TmInfo>())
                            {
                                response.Tms.Add(new BridgeTmResource
                                {
                                    Name = tm.Name,
                                    Kind = "supervertaler",
                                    Languages = $"{tm.SourceLang} → {tm.TargetLang}",
                                    Entries = (int)Math.Min(tm.EntryCount, int.MaxValue)
                                });
                            }
                        }
                    }

                    var settings = SettingsService.Current;
                    var write = settings.WriteTermbaseIds != null
                        ? new HashSet<long>(settings.WriteTermbaseIds) : new HashSet<long>();
                    var disabled = settings.DisabledTermbaseIds != null
                        ? new HashSet<long>(settings.DisabledTermbaseIds) : new HashSet<long>();
                    using (var tbReader = new TermbaseReader(dbPath))
                    {
                        if (tbReader.Open())
                        {
                            foreach (var tb in tbReader.GetTermbases() ?? new List<TermbaseInfo>())
                            {
                                response.Termbases.Add(new BridgeTermbaseResource
                                {
                                    Name = tb.Name,
                                    Languages = $"{tb.SourceLang} → {tb.TargetLang}",
                                    Terms = tb.TermCount,
                                    // The settings' Project tick is the flag TermLens
                                    // (pink chips) and the quick-add actions use – the
                                    // DB's is_project_termbase column is a separate,
                                    // often stale field and must not decide this.
                                    IsProjectTermbase = tb.Id == settings.ProjectTermbaseId,
                                    ReadEnabled = !disabled.Contains(tb.Id),
                                    WriteEnabled = write.Contains(tb.Id),
                                    Kind = "supervertaler"
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    response.Note = ((response.Note ?? "") + " Supervertaler resources error: " + ex.Message).Trim();
                }
            }

            // Trados project termbases (.ttb for Studio 2026, MultiTerm .sdltb
            // for 2024), as detected and loaded by TermLens for the open
            // document. Read-only for the AI (add_term can't write them).
            try
            {
                foreach (var info in TermLensEditorViewPart.GetMultiTermInfos()
                         ?? new List<Models.MultiTermTermbaseInfo>())
                {
                    var ext = "";
                    try { ext = Path.GetExtension(info.FilePath ?? "").ToLowerInvariant(); } catch { }
                    response.Termbases.Add(new BridgeTermbaseResource
                    {
                        Name = info.Name,
                        Languages = $"{info.SourceIndexName} → {info.TargetIndexName}",
                        Terms = info.TermCount,
                        IsProjectTermbase = true,
                        ReadEnabled = info.IsEnabled
                            && info.LoadMode != Models.MultiTermLoadMode.Failed,
                        WriteEnabled = false,
                        Kind = ext == ".ttb" ? "trados-ttb" : "multiterm"
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SupervertalerBridge] studio termbase listing threw: {ex.Message}");
            }

            // Derived from the list just built rather than re-queried, so the
            // warning can never contradict the rows above it.
            if (response.Termbases.Count > 0 && !response.Termbases.Any(t => t.ReadEnabled))
                response.Note = ((response.Note ?? "") + " No termbase is read-enabled, so every terminology " +
                    "lookup will come back empty – say so before relying on terminology. The user can switch " +
                    "them back on in Supervertaler settings > Termbases.").Trim();

            return response;
        }

        /// <summary>
        /// Bridge delegate for GET /v1/studio-tm-search (MCP search_studio_tm).
        /// Concordance search across the Trados project's native .sdltm TMs and
        /// GroupShare server TMs (the same TMs SuperSearch uses), which are the
        /// TMs most users actually work with – distinct from search_tm, which
        /// covers only Supervertaler bridged TMs. The active file path (needed
        /// to locate the project's TMs) is read on the UI thread; the TM search
        /// itself runs synchronously on the calling thread.
        /// </summary>
        private BridgeTmSearchResponse BridgeSearchStudioTm(BridgeStudioTmQuery q)
        {
            if (q == null || string.IsNullOrWhiteSpace(q.Query))
                return new BridgeTmSearchResponse { Ok = false, Error = "empty query" };

            // Resolve the active file path on the UI thread.
            string activeFilePath = null;
            var ctrl = _control?.Value;
            try
            {
                if (ctrl != null && !ctrl.IsDisposed && (ctrl.InvokeRequired || (UiThread.InvokeRequired && UiThread.IsAvailable)))
                    activeFilePath = (string)ctrl.Invoke(new Func<string>(
                        () => ResolveProjectAnchorPathCore()));
                else
                    activeFilePath = ResolveProjectAnchorPathCore();
            }
            catch { /* fall through to the no-path error below */ }

            if (string.IsNullOrEmpty(activeFilePath))
                return new BridgeTmSearchResponse
                {
                    Ok = false,
                    Error = "No document is open in the Trados editor, so the project's TMs can't be located."
                };

            List<string> tms;
            try
            {
                tms = Core.TmSearcher.FindProjectTms(activeFilePath);
            }
            catch (Exception ex)
            {
                return new BridgeTmSearchResponse { Ok = false, Error = "could not enumerate project TMs: " + ex.Message };
            }

            if (tms == null || tms.Count == 0)
                return new BridgeTmSearchResponse
                {
                    Ok = true,
                    Matches = new List<BridgeTmMatch>(),
                    Note = "No translation memories are attached to this Trados project."
                };

            var scope = (q.In ?? "both").ToLowerInvariant() == "source"
                ? Core.SearchScope.SourceOnly
                : (q.In ?? "both").ToLowerInvariant() == "target"
                    ? Core.SearchScope.TargetOnly
                    : Core.SearchScope.SourceAndTarget;

            var response = new BridgeTmSearchResponse { Ok = true, Matches = new List<BridgeTmMatch>() };
            try
            {
                var results = Core.TmSearcher.Search(
                    tms, q.Query, scope,
                    caseSensitive: false, useRegex: false, wholeWord: false,
                    progress: null, ct: System.Threading.CancellationToken.None);

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in results)
                {
                    if (response.Matches.Count >= q.Limit) break;
                    var key = (r.SourceText ?? "") + "" + (r.TargetText ?? "");
                    if (!seen.Add(key)) continue;
                    response.Matches.Add(new BridgeTmMatch
                    {
                        Score = r.MatchScore,
                        Source = r.SourceText ?? "",
                        Target = r.TargetText ?? "",
                        TmName = r.FileName
                    });
                }

                if (response.Matches.Count == 0)
                    response.Note = $"No concordance matches for '{q.Query}' in the project's " +
                                    $"{tms.Count} TM(s). Try a shorter or more distinctive phrase.";
            }
            catch (Exception ex)
            {
                return new BridgeTmSearchResponse { Ok = false, Error = "studio TM search failed: " + ex.Message };
            }

            return response;
        }

        /// <summary>
        /// Bridge delegate for GET /v1/files (MCP get_files). Lists the files
        /// of the document open in the editor (one for normal documents, many
        /// for merged documents) with per-file segment counts. Marshals to the
        /// UI thread.
        /// </summary>
        private BridgeFilesResponse BuildBridgeFiles()
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeFilesResponse { Available = false };

            if (ctrl.InvokeRequired)
            {
                return (BridgeFilesResponse)ctrl.Invoke(new Func<BridgeFilesResponse>(() => BuildBridgeFiles()));
            }
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies – marshal via the UI thread we
            // captured at startup instead. See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BuildBridgeFiles());

            var response = new BridgeFilesResponse { Available = false, Files = new List<BridgeFileInfo>() };
            if (_activeDocument == null)
            {
                response.Note = "No document is open in the Trados editor.";
                return response;
            }

            try
            {
                response.Available = true;
                EnsureBridgeFileMapFresh();

                string activeFileId = null;
                try
                {
                    var af = _activeDocument.ActiveFile;
                    if (af != null)
                        activeFileId = TryGetStringProp(af, "Id") ?? TryGetStringProp(af, "FileId");
                }
                catch { }

                foreach (var kv in _fileIdToName)
                {
                    response.Files.Add(new BridgeFileInfo
                    {
                        Id = kv.Key,
                        Name = kv.Value,
                        Segments = LookupSegmentCount(kv.Key),
                        IsActive = kv.Key == activeFileId
                    });
                }

                if (response.Files.Count == 0)
                {
                    // Single-file document without a Files collection: report
                    // the active file so the tool always returns something.
                    response.Files.Add(new BridgeFileInfo
                    {
                        Id = activeFileId ?? "",
                        Name = GetFileName() ?? "(unknown file)",
                        Segments = 0,
                        IsActive = true
                    });
                    response.Note = "Single-file document.";
                }
                else if (!_perFileMappingWorked && response.Files.Count > 1)
                {
                    response.Note = "Multiple files, but segments could not be attributed to them – " +
                                    "the get_segments 'file' filter is unavailable for this document.";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SupervertalerBridge] BuildBridgeFiles threw: {ex.Message}");
                return new BridgeFilesResponse { Available = false, Note = "error reading files: " + ex.Message };
            }

            return response;
        }

        /// <summary>
        /// Bridge delegate for GET /v1/inconsistencies (MCP find_inconsistencies).
        /// Groups repeated source texts (tag-stripped, trimmed) and reports the
        /// groups whose non-empty targets differ. Marshals to the UI thread.
        /// </summary>
        private BridgeInconsistenciesResponse BuildBridgeInconsistencies(int limit, int offset)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeInconsistenciesResponse { Available = false };

            if (ctrl.InvokeRequired)
            {
                return (BridgeInconsistenciesResponse)ctrl.Invoke(new Func<BridgeInconsistenciesResponse>(() => BuildBridgeInconsistencies(limit, offset)));
            }
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies – marshal via the UI thread we
            // captured at startup instead. See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BuildBridgeInconsistencies(limit, offset));

            var response = new BridgeInconsistenciesResponse
            {
                Available = false,
                Groups = new List<BridgeInconsistencyGroup>()
            };
            if (_activeDocument == null)
            {
                response.Note = "No document is open in the Trados editor.";
                return response;
            }

            try
            {
                response.Available = true;
                EnsureBridgeFileMapFresh();
                bool attributeFiles = _perFileMappingWorked && _fileIdToName.Count > 1;

                // source text → occurrences, in document order.
                var groups = new Dictionary<string, List<BridgeInconsistencyOccurrence>>(StringComparer.Ordinal);
                var order = new List<string>();
                int processed = 0;

                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    processed++;
                    if (processed % 200 == 0)
                        System.Windows.Forms.Application.DoEvents();

                    string sourceKey, target, status, id;
                    string fileName = null;
                    try
                    {
                        // Compare on plain text: tags stripped, whitespace
                        // collapsed – "same sentence, different bolding" still
                        // counts as a repetition.
                        var sourceSer = Core.SegmentTagHandler.Serialize(pair.Source);
                        sourceKey = System.Text.RegularExpressions.Regex.Replace(
                            Core.SegmentTagHandler.StripTagPlaceholders(sourceSer.SerializedText ?? ""),
                            @"\s+", " ").Trim();
                        if (sourceKey.Length == 0) continue;

                        target = pair.Target != null
                            ? System.Text.RegularExpressions.Regex.Replace(
                                Core.SegmentTagHandler.StripTagPlaceholders(
                                    Core.SegmentTagHandler.Serialize(pair.Target).SerializedText ?? ""),
                                @"\s+", " ").Trim()
                            : "";
                        status = (pair.Properties?.ConfirmationLevel
                            ?? Sdl.Core.Globalization.ConfirmationLevel.Unspecified).ToString();

                        var puId = _activeDocument.GetParentParagraphUnit(pair)
                            ?.Properties?.ParagraphUnitId.Id ?? "";
                        var segId = pair.Properties?.Id.Id ?? "";
                        id = puId + ":" + segId;

                        if (attributeFiles)
                        {
                            string fid;
                            if (_puIdToFileId.TryGetValue(puId, out fid) && fid != null)
                                _fileIdToName.TryGetValue(fid, out fileName);
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    List<BridgeInconsistencyOccurrence> list;
                    if (!groups.TryGetValue(sourceKey, out list))
                    {
                        list = new List<BridgeInconsistencyOccurrence>();
                        groups[sourceKey] = list;
                        order.Add(sourceKey);
                    }
                    list.Add(new BridgeInconsistencyOccurrence
                    {
                        Id = id,
                        Target = string.IsNullOrEmpty(target) ? null : target,
                        Status = status,
                        FileName = fileName
                    });
                }

                // Inconsistent = more than one DISTINCT non-empty target.
                // Repeated-but-untranslated segments are consistent (so far).
                foreach (var key in order)
                {
                    var occurrences = groups[key];
                    if (occurrences.Count < 2) continue;
                    var distinctTargets = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var o in occurrences)
                        if (!string.IsNullOrEmpty(o.Target))
                            distinctTargets.Add(o.Target);
                    if (distinctTargets.Count < 2) continue;

                    // Count every qualifying group, but only materialise the
                    // requested page. Without an offset the groups past the cap
                    // were unreachable at any 'limit' – on a 375-group job that
                    // silently hid the cross-file terminology drift, which was
                    // the part that mattered.
                    int index = response.GroupsFound;
                    response.GroupsFound++;
                    if (index < offset) continue;
                    if (response.Groups.Count >= limit)
                    {
                        response.Truncated = true;
                        continue;
                    }
                    response.Groups.Add(new BridgeInconsistencyGroup
                    {
                        Source = key,
                        Occurrences = occurrences
                    });
                }

                response.Returned = response.Groups.Count;
                response.Offset = offset;
                if (response.GroupsFound == 0)
                    response.Note = "No inconsistencies: every repeated source segment has a single " +
                                    "consistent translation (or is not translated yet).";
                else if (response.Truncated)
                {
                    int nextOffset = offset + response.Returned;
                    response.Note = $"Groups {offset + 1}–{nextOffset} of {response.GroupsFound} returned. " +
                                    $"Call again with offset={nextOffset} for the next page" +
                                    (limit < 500 ? ", or raise 'limit' (max 500)" : "") + ".";
                }
                else if (offset > 0)
                    response.Note = $"Groups {offset + 1}–{offset + response.Returned} of " +
                                    $"{response.GroupsFound} returned – this is the last page.";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SupervertalerBridge] BuildBridgeInconsistencies threw: {ex.Message}");
                return new BridgeInconsistenciesResponse
                {
                    Available = false,
                    Note = "error finding inconsistencies: " + ex.Message
                };
            }

            return response;
        }

        /// <summary>
        /// Bridge delegate for POST /v1/add-term (MCP add_term). Inserts a term
        /// into the user's configured Write termbases via the same InsertTermBatch
        /// path as the Alt+Down quick-add, then refreshes the TermLens index.
        /// Marshals to the UI thread.
        /// </summary>
        private BridgeAddTermResponse BridgeAddTerm(BridgeAddTermRequest req)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeAddTermResponse { Ok = false, Error = "ai assistant disposed" };

            if (ctrl.InvokeRequired)
            {
                return (BridgeAddTermResponse)ctrl.Invoke(new Func<BridgeAddTermResponse>(() => BridgeAddTerm(req)));
            }
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies – marshal via the UI thread we
            // captured at startup instead. See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeAddTerm(req));

            try
            {
                var settings = SettingsService.Current;

                if (settings.WriteTermbaseIds == null || settings.WriteTermbaseIds.Count == 0)
                    return new BridgeAddTermResponse
                    {
                        Ok = false,
                        Error = "No write termbase is configured. The user must tick the 'Write' column for at " +
                                "least one termbase in the Supervertaler Termbases settings."
                    };

                if (string.IsNullOrEmpty(settings.TermbasePath) || !File.Exists(settings.TermbasePath))
                    return new BridgeAddTermResponse
                    {
                        Ok = false,
                        Error = "Supervertaler database not found – check the termbase path in settings."
                    };

                var writeTermbases = new List<TermbaseInfo>();
                List<TermbaseInfo> allTermbases = null;
                using (var reader = new TermbaseReader(settings.TermbasePath))
                {
                    if (reader.Open())
                    {
                        allTermbases = reader.GetTermbases() ?? new List<TermbaseInfo>();
                        foreach (var id in settings.WriteTermbaseIds)
                        {
                            var tb = reader.GetTermbaseById(id);
                            if (tb != null) writeTermbases.Add(tb);
                        }
                    }
                }

                if (writeTermbases.Count == 0)
                    return new BridgeAddTermResponse { Ok = false, Error = "no write termbases found" };

                // Optional targeting: names or numeric ids. Unknown / non-write
                // names are reported per termbase rather than failing the call,
                // so a mixed list still writes where it validly can.
                var results = new List<BridgeAddTermResult>();
                var targets = writeTermbases;
                if (req.Termbases != null && req.Termbases.Count > 0)
                {
                    targets = new List<TermbaseInfo>();
                    foreach (var wanted in req.Termbases)
                    {
                        if (string.IsNullOrWhiteSpace(wanted)) continue;
                        var w = wanted.Trim();
                        var hit = writeTermbases.Find(t =>
                            string.Equals(t.Name, w, StringComparison.OrdinalIgnoreCase)
                            || (long.TryParse(w, out var wid) && t.Id == wid));
                        if (hit != null)
                        {
                            if (!targets.Contains(hit)) targets.Add(hit);
                            continue;
                        }
                        var known = allTermbases?.Find(t =>
                            string.Equals(t.Name, w, StringComparison.OrdinalIgnoreCase)
                            || (long.TryParse(w, out var kid) && t.Id == kid));
                        results.Add(new BridgeAddTermResult
                        {
                            Termbase = w,
                            Status = "error",
                            Detail = known != null
                                ? "this termbase is not Write-enabled – the user must tick its 'Write' " +
                                  "column in the Supervertaler Termbases settings"
                                : "no Supervertaler termbase with this name or id. Trados project " +
                                  "termbases (.ttb / MultiTerm) are read-only from here and cannot be targeted."
                        });
                    }
                    if (targets.Count == 0)
                        return new BridgeAddTermResponse
                        {
                            Ok = false,
                            Error = "none of the requested termbases can be written to – see results",
                            Results = results
                        };
                }
                else if (!string.IsNullOrWhiteSpace(req.Scope)
                    && !string.Equals(req.Scope, "both", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(req.Scope, "all", StringComparison.OrdinalIgnoreCase))
                {
                    bool wantProject = string.Equals(req.Scope, "project", StringComparison.OrdinalIgnoreCase);
                    bool wantBackground = string.Equals(req.Scope, "background", StringComparison.OrdinalIgnoreCase);
                    if (!wantProject && !wantBackground)
                        return new BridgeAddTermResponse
                        {
                            Ok = false,
                            Error = $"unknown scope '{req.Scope}' – use 'project', 'background', or 'both'"
                        };

                    // The settings' Project tick, not the DB's is_project_termbase
                    // column – same source of truth as TermLens's pink chips.
                    targets = writeTermbases.Where(t => (t.Id == settings.ProjectTermbaseId) == wantProject).ToList();
                    if (targets.Count == 0)
                        return new BridgeAddTermResponse
                        {
                            Ok = false,
                            Error = wantProject
                                ? "scope 'project' was requested, but none of the Write-enabled termbases " +
                                  "carries the Project flag – tick 'Project' for this job's termbase in the " +
                                  "Supervertaler Termbases settings, or pass an explicit 'termbases' list"
                                : "scope 'background' was requested, but every Write-enabled termbase " +
                                  "carries the Project flag – pass an explicit 'termbases' list if you " +
                                  "meant one of those"
                        };
                }

                string projSrcLang = "";
                try { projSrcLang = _activeDocument?.ActiveFile?.SourceFile?.Language?.DisplayName ?? ""; }
                catch { /* leave empty if unavailable */ }

                var source = req.Source.Trim();
                var target = req.Target.Trim();

                var outcomes = TermbaseReader.InsertTermBatchDetailed(
                    settings.TermbasePath, source, target,
                    req.Definition ?? "", req.Domain ?? "", req.Notes ?? "",
                    targets,
                    projectSourceLang: projSrcLang,
                    explicitSourceLang: req.SourceLang,
                    explicitTargetLang: req.TargetLang);

                var insertedEntries = new List<TermEntry>();
                var addedTo = new List<string>();
                foreach (var o in outcomes)
                {
                    var tb = targets.Find(t => t.Id == o.TermbaseId);
                    if (o.Status == TermbaseReader.TermInsertOutcome.StatusAdded)
                    {
                        addedTo.Add(o.TermbaseName);
                        results.Add(new BridgeAddTermResult
                        {
                            Termbase = o.TermbaseName,
                            Status = "added",
                            Role = tb == null ? null : (tb.Id == settings.ProjectTermbaseId ? "project" : "background"),
                            Stored = new BridgeStoredTerm
                            {
                                Source = o.StoredSource,
                                Target = o.StoredTarget,
                                SourceLang = tb?.SourceLang,
                                TargetLang = tb?.TargetLang,
                                Definition = string.IsNullOrEmpty(req.Definition) ? null : req.Definition,
                                Domain = string.IsNullOrEmpty(req.Domain) ? null : req.Domain,
                                Notes = string.IsNullOrEmpty(req.Notes) ? null : req.Notes,
                                Reoriented = o.Swapped
                            }
                        });
                        if (tb != null)
                            insertedEntries.Add(new TermEntry
                            {
                                Id = o.NewId,
                                // Stored orientation, not the caller's – the old
                                // code indexed the caller's order even when the
                                // insert had swapped it for this termbase.
                                SourceTerm = o.StoredSource,
                                TargetTerm = o.StoredTarget,
                                SourceLang = tb.SourceLang,
                                TargetLang = tb.TargetLang,
                                TermbaseId = tb.Id,
                                TermbaseName = tb.Name,
                                IsProjectTermbase = tb.IsProjectTermbase,
                                Ranking = tb.Ranking,
                                Definition = req.Definition ?? "",
                                Domain = req.Domain ?? "",
                                Notes = req.Notes ?? "",
                                Forbidden = false,
                                CaseSensitive = false,
                                TargetSynonyms = new List<string>()
                            });
                    }
                    else
                    {
                        bool isDuplicate = o.Status == TermbaseReader.TermInsertOutcome.StatusDuplicate;
                        results.Add(new BridgeAddTermResult
                        {
                            Termbase = o.TermbaseName,
                            Status = isDuplicate ? "duplicate" : "error",
                            Role = tb == null ? null : (tb.Id == settings.ProjectTermbaseId ? "project" : "background"),
                            Detail = o.Detail,
                            Existing = isDuplicate && o.ExistingId >= 0
                                ? new BridgeExistingTerm
                                {
                                    Id = o.ExistingId,
                                    Source = o.ExistingSource,
                                    Target = o.ExistingTarget
                                }
                                : null
                        });
                    }
                }

                // Incremental TermLens index update – terms appear immediately.
                if (insertedEntries.Count > 0)
                    TermLensEditorViewPart.NotifyTermInserted(insertedEntries);

                var response = new BridgeAddTermResponse
                {
                    Ok = addedTo.Count > 0,
                    AddedTo = addedTo.Count > 0 ? addedTo : null,
                    Results = results
                };
                if (addedTo.Count == 0)
                    response.Error = results.TrueForAll(r => r.Status == "duplicate")
                        ? "the term already exists in every targeted termbase – see results"
                        : "nothing was added – see results for the per-termbase reasons";

                // Stale-project-termbase check: a Write-enabled termbase carrying the
                // Project flag is meant to belong to THIS job. If its name shares no
                // word with the currently open project's name, it's likely a leftover
                // from a previous job that was never un-ticked. Only project-flagged
                // termbases are checked – a deliberately generic background termbase
                // (e.g. "BEIJER") never matches a job-specific project name and would
                // otherwise fire on every call.
                var projectName = GetProjectName();
                if (!string.IsNullOrWhiteSpace(projectName))
                {
                    string[] Tokenize(string s) => System.Text.RegularExpressions.Regex
                        .Split(s.ToLowerInvariant(), @"[^a-z0-9]+")
                        .Where(t => t.Length > 0).ToArray();
                    var projectTokens = new HashSet<string>(Tokenize(projectName));
                    var staleNames = targets
                        .Where(t => t.Id == settings.ProjectTermbaseId && addedTo.Contains(t.Name))
                        .Where(t => !Tokenize(t.Name).Any(projectTokens.Contains))
                        .Select(t => t.Name)
                        .Distinct()
                        .ToList();
                    if (staleNames.Count > 0)
                        response.Note = (response.Note != null ? response.Note + " " : "") +
                            $"note: write-enabled project termbase '{string.Join("', '", staleNames)}' " +
                            $"does not appear to match the open project '{projectName}' – check it's not a " +
                            "leftover from a previous job.";
                }

                return response;
            }
            catch (Exception ex)
            {
                return new BridgeAddTermResponse { Ok = false, Error = "add term failed: " + ex.Message };
            }
        }

        private BridgeEditTermResponse BridgeUpdateTerm(BridgeEditTermRequest req) => BridgeEditTerm(req, delete: false);
        private BridgeEditTermResponse BridgeDeleteTerm(BridgeEditTermRequest req) => BridgeEditTerm(req, delete: true);

        /// <summary>
        /// Bridge delegate for POST /v1/save-document (MCP save_document): saves
        /// the document open in the editor – the same as the user pressing
        /// Ctrl+S. Ends the "now press Ctrl+S in Studio" hand-off after AI
        /// writes, and lets save+batch-task chains run from chat. UI thread.
        /// </summary>
        private BridgeResultResponse BridgeSaveDocument()
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeResultResponse { Ok = false, Error = "ai assistant disposed" };
            if (ctrl.InvokeRequired)
                return (BridgeResultResponse)ctrl.Invoke(new Func<BridgeResultResponse>(() => BridgeSaveDocument()));
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies – marshal via the UI thread we
            // captured at startup instead. See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeSaveDocument());

            if (_activeDocument == null)
                return new BridgeResultResponse { Ok = false, Error = "no document is open in the Trados editor" };
            if (_editorController == null)
                return new BridgeResultResponse { Ok = false, Error = "editor controller unavailable" };

            try
            {
                _editorController.Save(_activeDocument);
                if (ReferenceEquals(_bridgeUnsavedWritesDoc, _activeDocument))
                    _bridgeUnsavedWritesDoc = null;
                return new BridgeResultResponse
                {
                    Ok = true,
                    Note = "Document saved (all files of a merged document). Batch tasks and exports now see " +
                           "the current state."
                };
            }
            catch (Exception ex)
            {
                return new BridgeResultResponse { Ok = false, Error = "save failed: " + ex.Message };
            }
        }

        /// <summary>
        /// Bridge delegate for POST /v1/import-termbase (MCP import_project_termbase).
        /// Copies a Trados project termbase (.sdltb / .ttb) into a Supervertaler
        /// termbase — the same operation as Settings → Termbases → "Import
        /// .sdltb/.ttb…", which was UI-only. Over the bridge the only alternative
        /// was one <c>add_term</c> round-trip per term (issue #59).
        ///
        /// Write gate: an EXISTING destination must be Write-enabled, the same rule
        /// add_term follows — the Write tick is how a user says which termbases an
        /// assistant may write to, and a bulk import is the last thing that should
        /// bypass it. A destination that does not exist yet is created, because
        /// asking for a new termbase by name is unambiguous consent to fill it; the
        /// response says so, and the new termbase is NOT Write-enabled, so routine
        /// add_term calls still won't reach it until the user ticks it.
        ///
        /// Re-running is safe: <see cref="TermbaseReader.ImportRows"/> does a
        /// bidirectional duplicate check per row, so a second run adds nothing and
        /// reports the count as duplicates.
        /// </summary>
        private BridgeImportTermbaseResponse BridgeImportTermbase(BridgeImportTermbaseRequest req)
        {
            string delimitedSource = null;
            Core.DelimitedTermFileResult delimitedParse = null;
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeImportTermbaseResponse { Ok = false, Error = "ai assistant disposed" };
            if (ctrl.InvokeRequired)
                return (BridgeImportTermbaseResponse)ctrl.Invoke(
                    new Func<BridgeImportTermbaseResponse>(() => BridgeImportTermbase(req)));
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeImportTermbase(req));

            try
            {
                var dbPath = ResolveSupervertalerDbPath();
                if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
                    return new BridgeImportTermbaseResponse
                    { Ok = false, Error = "no Supervertaler termbase database is configured" };

                // A delimited export is not a Trados termbase and carries none of
                // its metadata, so it becomes the same ImportedTermbase the
                // readers produce and then follows the identical path.
                delimitedSource = Core.DelimitedTermFile.LooksDelimited(req?.Termbase)
                    ? req.Termbase
                    : null;

                // ── 1. Which project termbase to read ──────────────────────
                // Skipped for a delimited file: it is a path the caller gave
                // us, not one of the project's attached termbases, and
                // demanding the project have one would refuse a valid import
                // for a reason that has nothing to do with it.
                var available = delimitedSource != null
                    ? new List<Models.MultiTermTermbaseInfo>()
                    : (TermLensEditorViewPart.GetMultiTermInfos()
                       ?? new List<Models.MultiTermTermbaseInfo>());
                if (delimitedSource == null && available.Count == 0)
                    return new BridgeImportTermbaseResponse
                    {
                        Ok = false,
                        Error = "the open project has no Trados termbase (.sdltb / .ttb) attached"
                    };

                Models.MultiTermTermbaseInfo chosen = null;
                var wanted = (req.Termbase ?? "").Trim();
                if (delimitedSource != null)
                {
                    // chosen stays null - the file itself is the source.
                }
                else if (wanted.Length == 0)
                {
                    if (available.Count > 1)
                        return new BridgeImportTermbaseResponse
                        {
                            Ok = false,
                            Error = "the project has more than one Trados termbase – name the one to import in " +
                                    "'termbase': " + string.Join(", ", available.Select(t => t.Name))
                        };
                    chosen = available[0];
                }
                else
                {
                    chosen = available.FirstOrDefault(t =>
                                 string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase))
                             ?? available.FirstOrDefault(t =>
                                 string.Equals(t.FilePath, wanted, StringComparison.OrdinalIgnoreCase));
                    if (chosen == null)
                        return new BridgeImportTermbaseResponse
                        {
                            Ok = false,
                            Error = $"no Trados termbase called '{wanted}' in this project. Available: " +
                                    string.Join(", ", available.Select(t => t.Name))
                        };
                }

                // ── 2. Read it ─────────────────────────────────────────────
                // Through the WAL-safe snapshot the UI path uses: a .ttb open in
                // Studio can have uncheckpointed changes, and the original must
                // never be touched.
                Models.ImportedTermbase imported;

                if (delimitedSource != null)
                {
                    // A text export declares no languages, so the caller must
                    // say which side is which. Refused rather than guessed: the
                    // two columns are often identical-looking terminology and
                    // getting the direction wrong writes every pair backwards.
                    if (string.IsNullOrWhiteSpace(req.SourceLang) || string.IsNullOrWhiteSpace(req.TargetLang))
                        return new BridgeImportTermbaseResponse
                        {
                            Ok = false,
                            Error = "sourceLang and targetLang are required when importing a delimited "
                                  + "file - it carries no language metadata of its own."
                        };

                    var parsed = Core.DelimitedTermFile.Parse(
                        delimitedSource, req.SourceLang, req.TargetLang, req.FieldMap);

                    if (!parsed.Ok)
                        return new BridgeImportTermbaseResponse
                        {
                            Ok = false,
                            Error = parsed.Error ?? "the file could not be read as a term list",
                            FieldMap = parsed.Mapping,
                            Warnings = parsed.Warnings
                        };

                    imported = Core.DelimitedTermFile.ToImportedTermbase(
                        parsed, delimitedSource, req.SourceLang, req.TargetLang);
                    delimitedParse = parsed;
                }
                else
                using (var snapshot = Core.TtbImportSnapshot.Prepare(chosen.FilePath))
                using (var reader = Core.TermbaseReaderFactory.Create(snapshot.ReadPath))
                {
                    if (!reader.Open())
                        return new BridgeImportTermbaseResponse
                        {
                            Ok = false,
                            Error = "could not open the termbase: " + (reader.LastError ?? "unknown error")
                        };
                    imported = reader.LoadForImport();
                }

                if (imported == null || imported.Languages.Count == 0)
                    return new BridgeImportTermbaseResponse
                    {
                        Ok = false,
                        Error = "the termbase could not be read, or declares no languages. A .sdltb needs the " +
                                "32-bit MultiTerm/Access engine, available only in the Studio 2024 build; in " +
                                "Studio 2026 convert it to .ttb first."
                    };
                imported.Name = Path.GetFileNameWithoutExtension(
                    delimitedSource ?? chosen.FilePath);

                var languageList = imported.Languages
                    .Select(l => $"{l.Name} ({Core.LanguageUtils.CanonicalLocale(l.Locale ?? l.Name)})")
                    .ToList();

                // ── 3. Language pair ───────────────────────────────────────
                // chosen is null for a delimited file - there is no Trados
                // termbase to take an index name from, and req.SourceLang /
                // req.TargetLang are required on that path anyway.
                var srcLang = ResolveImportLanguage(imported,
                    req.SourceLang, chosen?.SourceIndexName, GetDocumentSourceLanguage());
                var tgtLang = ResolveImportLanguage(imported,
                    req.TargetLang, chosen?.TargetIndexName, GetDocumentTargetLanguage());

                if (srcLang == null || tgtLang == null)
                    return new BridgeImportTermbaseResponse
                    {
                        Ok = false,
                        Error = "could not decide the language pair – pass 'sourceLang' and 'targetLang'",
                        AvailableLanguages = languageList
                    };
                if (srcLang.Id == tgtLang.Id)
                    return new BridgeImportTermbaseResponse
                    {
                        Ok = false,
                        Error = "source and target resolved to the same language",
                        AvailableLanguages = languageList
                    };

                // ── 4. Destination ─────────────────────────────────────────
                var intoName = req.Into.Trim();
                long destId = -1;
                bool created = false;
                using (var tbReader = new TermbaseReader(dbPath))
                {
                    if (tbReader.Open())
                    {
                        var hit = (tbReader.GetTermbases() ?? new List<TermbaseInfo>())
                            .FirstOrDefault(t => string.Equals(t.Name, intoName, StringComparison.OrdinalIgnoreCase));
                        if (hit != null) destId = hit.Id;
                    }
                }

                if (destId >= 0)
                {
                    var settings = SettingsService.Current;
                    var writeIds = settings?.WriteTermbaseIds ?? new List<long>();
                    if (!writeIds.Contains(destId))
                        return new BridgeImportTermbaseResponse
                        {
                            Ok = false,
                            Error = $"'{intoName}' exists but is not Write-enabled. The user must tick its " +
                                    "'Write' column on Settings → Termbases, or choose a new name to import " +
                                    "into a fresh termbase."
                        };
                }
                else if (!req.DryRun)
                {
                    destId = TermbaseReader.CreateTermbase(dbPath, intoName,
                        Core.LanguageUtils.CanonicalLocale(srcLang.Locale ?? srcLang.Name),
                        Core.LanguageUtils.CanonicalLocale(tgtLang.Locale ?? tgtLang.Name));
                    // A termbase an assistant created is the LAST one that should
                    // start feeding prompts unasked (#62).
                    Core.NewTermbaseDefaults.Apply(destId);
                    created = true;
                }

                // ── 5. Field map ───────────────────────────────────────────
                var fieldMap = Core.TermbaseImporter.SuggestFieldMap(imported.DiscoveredFields);
                var badMappings = new List<string>();
                if (req.FieldMap != null)
                {
                    foreach (var entry in req.FieldMap)
                    {
                        if (string.IsNullOrWhiteSpace(entry)) continue;
                        var eq = entry.LastIndexOf('=');
                        if (eq <= 0 || eq == entry.Length - 1)
                        {
                            badMappings.Add($"'{entry}' is not in the form Field=target");
                            continue;
                        }
                        var name = entry.Substring(0, eq).Trim();
                        var value = entry.Substring(eq + 1).Trim();
                        Core.ImportFieldTarget target;
                        if (Enum.TryParse(value, true, out target))
                            fieldMap[name] = target;
                        else
                            badMappings.Add($"'{value}' is not a known target for field '{name}'");
                    }
                }

                // ── 6. Run ─────────────────────────────────────────────────
                var options = new Core.ImportOptions
                {
                    SourceLanguageId = srcLang.Id,
                    TargetLanguageId = tgtLang.Id,
                    DestinationTermbaseId = destId,
                    FieldMap = fieldMap
                };
                var summary = Core.TermbaseImporter.Import(imported, options, dbPath, req.DryRun);
                foreach (var bad in badMappings) summary.Warnings.Add(bad + " – kept the automatic mapping.");
                if (delimitedParse != null)
                {
                    // The parser's findings matter most on a dry run: in a format
                    // with no schema this report is the only place a wrong column
                    // or an invisible character can be caught before it is stored.
                    foreach (var w in delimitedParse.Warnings) summary.Warnings.Add(w);
                    foreach (var s in delimitedParse.Skipped) summary.Warnings.Add(s);
                }

                if (!req.DryRun && summary.Added > 0)
                {
                    // Same refresh the UI import does, so the new terms are matched
                    // immediately instead of after a restart.
                    try { TermLensEditorViewPart.NotifyTermAdded(); } catch { }
                }

                return new BridgeImportTermbaseResponse
                {
                    Ok = true,
                    DryRun = req.DryRun,
                    From = imported.Name,
                    Format = imported.Format,
                    Into = intoName,
                    CreatedDestination = created,
                    SourceLang = srcLang.Name,
                    TargetLang = tgtLang.Name,
                    ConceptsTotal = summary.ConceptsTotal,
                    RowsBuilt = summary.RowsBuilt,
                    Added = summary.Added,
                    Duplicates = summary.Duplicates,
                    SynonymsAdded = summary.SynonymsAdded,
                    // For a delimited file the interesting mapping is which COLUMN
                    // became which field, not which MultiTerm field did - and it is
                    // the thing most worth checking before a write, since a wrong
                    // column is silent afterwards.
                    FieldMap = delimitedParse != null
                        ? delimitedParse.Mapping
                        : fieldMap.Select(kv => $"{kv.Key} = {kv.Value}").OrderBy(s => s).ToList(),
                    Samples = delimitedParse == null ? null
                        : delimitedParse.Rows.Take(5)
                            .Select(r => r.Source + " \u2192 " + r.Target
                                       + (r.TargetSynonyms.Count > 0
                                            ? " (+" + r.TargetSynonyms.Count + " synonym(s))" : ""))
                            .ToList(),
                    AvailableLanguages = languageList,
                    Warnings = summary.Warnings.Count > 0 ? summary.Warnings : null,
                    Note = req.DryRun
                        ? "Dry run – nothing was written. 'added' and 'duplicates' are 0 because whether a row " +
                          "already exists is decided by the database at write time."
                        : (created ? $"Created '{intoName}'. It is Read-enabled, so its terms show in TermLens " +
                                     "straight away, but NOT enabled for AI and NOT Write-enabled: its terms " +
                                     "will not be sent to a model, and add_term will not write to it, until " +
                                     "the user ticks those columns on Settings → Termbases." : null)
                };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] BridgeImportTermbase threw: {ex}");
                return new BridgeImportTermbaseResponse { Ok = false, Error = "import failed: " + ex.Message };
            }
        }

        /// <summary>
        /// Picks the <see cref="Models.ImportLanguage"/> a caller means, trying in
        /// order: an explicit request value, the index name TermLens already resolved
        /// for this project, then the document's own language. Matching is by
        /// canonical locale first, then by name, then by prefix — "en" should find
        /// "English (United Kingdom)" without the caller having to know the exact
        /// index name stored in the file.
        /// </summary>
        private static Models.ImportLanguage ResolveImportLanguage(
            Models.ImportedTermbase tb, params string[] candidates)
        {
            if (tb?.Languages == null || tb.Languages.Count == 0) return null;

            foreach (var raw in candidates ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var want = Core.LanguageUtils.CanonicalLocale(raw.Trim()) ?? raw.Trim();

                var hit = tb.Languages.FirstOrDefault(l => string.Equals(
                              Core.LanguageUtils.CanonicalLocale(l.Locale ?? l.Name), want,
                              StringComparison.OrdinalIgnoreCase))
                          ?? tb.Languages.FirstOrDefault(l => string.Equals(
                              l.Name, raw.Trim(), StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;

                // Prefix: "en" matches "en-GB"; "en-GB" matches a bare "en".
                var bare = want.Split('-')[0];
                hit = tb.Languages.FirstOrDefault(l =>
                {
                    var loc = Core.LanguageUtils.CanonicalLocale(l.Locale ?? l.Name) ?? "";
                    return loc.Split('-')[0].Equals(bare, StringComparison.OrdinalIgnoreCase);
                });
                if (hit != null) return hit;
            }

            return null;
        }

        /// <summary>
        /// Bridge delegate for POST /v1/update-term and /v1/delete-term (MCP
        /// update_term / delete_term). Edits SUPERVERTALER termbases only, with
        /// the same gate as add_term: the termbase must be Write-enabled. The
        /// entry is identified by its exact current source+target pair (all
        /// exact matches are affected – identical duplicates go together);
        /// Trados project termbases (.ttb/.sdltb) stay read-only by design.
        /// The response echoes exactly what changed, so the chat transcript
        /// doubles as the audit log. Marshals to the UI thread.
        /// </summary>
        /// <summary>
        /// One side of a term pair compared the way the termbase stores it:
        /// trimmed and case-insensitive. Kept as a method so both orientations
        /// are tested by identical rules.
        /// </summary>
        private static bool TermSideEquals(string stored, string given)
        {
            return string.Equals((stored ?? "").Trim(), given, StringComparison.OrdinalIgnoreCase);
        }

        private BridgeEditTermResponse BridgeEditTerm(BridgeEditTermRequest req, bool delete)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeEditTermResponse { Ok = false, Error = "ai assistant disposed" };
            if (ctrl.InvokeRequired)
                return (BridgeEditTermResponse)ctrl.Invoke(new Func<BridgeEditTermResponse>(() => BridgeEditTerm(req, delete)));
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies – marshal via the UI thread we
            // captured at startup instead. See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeEditTerm(req, delete));

            try
            {
                var newSource = string.IsNullOrWhiteSpace(req.NewSource) ? null : req.NewSource.Trim();
                var newTarget = string.IsNullOrWhiteSpace(req.NewTarget) ? null : req.NewTarget.Trim();
                var newNotes = string.IsNullOrWhiteSpace(req.NewNotes) ? null : req.NewNotes.Trim();
                var newDefinition = string.IsNullOrWhiteSpace(req.NewDefinition) ? null : req.NewDefinition.Trim();
                var newDomain = string.IsNullOrWhiteSpace(req.NewDomain) ? null : req.NewDomain.Trim();
                if (!delete && newSource == null && newTarget == null
                    && newNotes == null && newDefinition == null && newDomain == null)
                    return new BridgeEditTermResponse
                    {
                        Ok = false,
                        Error = "nothing to change – provide 'newSource', 'newTarget', 'newNotes', " +
                                "'newDefinition' and/or 'newDomain'"
                    };

                var settings = SettingsService.Current;
                if (settings.WriteTermbaseIds == null || settings.WriteTermbaseIds.Count == 0)
                    return new BridgeEditTermResponse
                    {
                        Ok = false,
                        Error = "No write termbase is configured. The user must tick the 'Write' column for at " +
                                "least one termbase in the Supervertaler Termbases settings."
                    };
                if (string.IsNullOrEmpty(settings.TermbasePath) || !File.Exists(settings.TermbasePath))
                    return new BridgeEditTermResponse
                    {
                        Ok = false,
                        Error = "Supervertaler database not found – check the termbase path in settings."
                    };

                var writeIds = new HashSet<long>(settings.WriteTermbaseIds);
                var source = req.Source.Trim();
                var target = req.Target.Trim();
                var tbFilter = string.IsNullOrWhiteSpace(req.Termbase) ? null : req.Termbase.Trim();

                // Resolve the entry: exact current source+target (case-insensitive),
                // optionally restricted to one termbase by name.
                // Item2 records that the pair was given the OTHER way round from
                // how this termbase stores it. Tracked per match because a single
                // call can reach several termbases that declare opposite
                // directions - and the rename below has to swap for exactly those.
                List<Tuple<TermEntry, bool>> matches;
                using (var reader = new TermbaseReader(settings.TermbasePath))
                {
                    if (!reader.Open())
                        return new BridgeEditTermResponse { Ok = false, Error = "could not open Supervertaler database" };
                    // SearchTerm already looks in both columns, so one query finds
                    // the entry whichever way round the caller named it.
                    matches = (reader.SearchTerm(source) ?? new List<TermEntry>())
                        .Where(e => e != null
                            && (tbFilter == null
                                || (e.TermbaseName ?? "").IndexOf(tbFilter, StringComparison.OrdinalIgnoreCase) >= 0))
                        .Select(e =>
                            TermSideEquals(e.SourceTerm, source) && TermSideEquals(e.TargetTerm, target)
                                ? Tuple.Create(e, false)
                                : TermSideEquals(e.SourceTerm, target) && TermSideEquals(e.TargetTerm, source)
                                    ? Tuple.Create(e, true)
                                    : null)
                        .Where(m => m != null)
                        .ToList();
                }

                if (matches.Count == 0)
                    return new BridgeEditTermResponse
                    {
                        Ok = false,
                        Error = $"no entry “{source} → {target}” found" +
                                (tbFilter != null ? $" in a termbase matching “{tbFilter}”" : "") +
                                " – both terms must match a stored entry exactly (either way round); " +
                                "use lookup_term to see the stored form"
                    };

                var editable = matches.Where(m => writeIds.Contains(m.Item1.TermbaseId)).ToList();
                if (editable.Count == 0)
                {
                    var where = string.Join(", ", matches.Select(m => m.Item1.TermbaseName).Distinct());
                    return new BridgeEditTermResponse
                    {
                        Ok = false,
                        Error = $"the entry exists in: {where} – but none of those termbases is Write-enabled. " +
                                "The user must tick the 'Write' column for that termbase in the Supervertaler " +
                                "Termbases settings before the AI may modify it."
                    };
                }

                var details = new List<string>();
                foreach (var match in editable)
                {
                    var e = match.Item1;

                    // The new terms arrive in the orientation the CALLER used to
                    // name the entry. Where that was the reverse of this
                    // termbase's, they have to go back the other way before being
                    // written - accepting a reversed pair and then storing a
                    // rename as given would flip the entry, which is the exact
                    // corruption the strict match used to rule out.
                    var wantSource = match.Item2 ? newTarget : newSource;
                    var wantTarget = match.Item2 ? newSource : newTarget;

                    if (delete)
                    {
                        if (!TermbaseReader.DeleteTerm(settings.TermbasePath, e.Id)) continue;
                        TermLensEditorViewPart.NotifyTermDeleted(e.Id);
                        details.Add($"{e.TermbaseName}: deleted “{e.SourceTerm} → {e.TargetTerm}”");
                    }
                    else
                    {
                        var s = wantSource ?? e.SourceTerm;
                        var t = wantTarget ?? e.TargetTerm;
                        var definition = newDefinition ?? e.Definition ?? "";
                        var domain = newDomain ?? e.Domain ?? "";
                        var notes = newNotes ?? e.Notes ?? "";
                        // Preserve every other field of the entry.
                        if (!TermbaseReader.UpdateTerm(settings.TermbasePath, e.Id, s, t,
                                definition, domain, notes,
                                e.IsNonTranslatable, e.SourceAbbreviation, e.TargetAbbreviation,
                                e.Url, e.Client, e.Forbidden, e.Project))
                            continue;
                        // Refresh TermLens's in-memory index: replace the old entry.
                        TermLensEditorViewPart.NotifyTermDeleted(e.Id);
                        var updated = new TermEntry
                        {
                            Id = e.Id,
                            SourceTerm = s,
                            TargetTerm = t,
                            SourceLang = e.SourceLang,
                            TargetLang = e.TargetLang,
                            TermbaseId = e.TermbaseId,
                            TermbaseName = e.TermbaseName,
                            IsProjectTermbase = e.IsProjectTermbase,
                            Ranking = e.Ranking,
                            Definition = definition,
                            Domain = domain,
                            Notes = notes,
                            Url = e.Url,
                            Forbidden = e.Forbidden,
                            CaseSensitive = e.CaseSensitive,
                            IsNonTranslatable = e.IsNonTranslatable,
                            Client = e.Client,
                            Project = e.Project,
                            SourceAbbreviation = e.SourceAbbreviation,
                            TargetAbbreviation = e.TargetAbbreviation,
                            TargetSynonyms = e.TargetSynonyms ?? new List<string>()
                        };
                        TermLensEditorViewPart.NotifyTermInserted(new List<TermEntry> { updated });
                        var changeParts = new List<string>();
                        if (s != e.SourceTerm || t != e.TargetTerm)
                            changeParts.Add($"“{e.SourceTerm} → {e.TargetTerm}” is now “{s} → {t}”");
                        if (match.Item2)
                            changeParts.Add("matched in the termbase's own direction (" +
                                            (e.SourceLang ?? "?") + " " + "\u2192" + " " + (e.TargetLang ?? "?") +
                                            "), the reverse of how you named it");
                        if (newNotes != null) changeParts.Add("notes updated");
                        if (newDefinition != null) changeParts.Add("definition updated");
                        if (newDomain != null) changeParts.Add("domain updated");
                        details.Add($"{e.TermbaseName}: " + string.Join(", ", changeParts));
                    }
                }

                var skippedNote = matches.Count > editable.Count
                    ? $" ({matches.Count - editable.Count} match(es) in non-Write termbases were left untouched: " +
                      string.Join(", ", matches.Where(m => !writeIds.Contains(m.Item1.TermbaseId)).Select(m => m.Item1.TermbaseName).Distinct()) + ")"
                    : "";

                return new BridgeEditTermResponse
                {
                    Ok = details.Count > 0,
                    Error = details.Count == 0 ? "the database operation failed – nothing was changed" : null,
                    Changed = details.Count,
                    Details = details.Count > 0 ? details : null,
                    Note = details.Count > 0
                        ? "Changes are live in the shared Supervertaler database (also used by the Supervertaler " +
                          "Workbench) – no save step needed." + skippedNote
                        : null
                };
            }
            catch (Exception ex)
            {
                return new BridgeEditTermResponse
                {
                    Ok = false,
                    Error = (delete ? "delete" : "update") + " term failed: " + ex.Message
                };
            }
        }

        /// <summary>Resolve a bridge segment reference to a live ISegmentPair:
        /// either a full "puId:segId" id, or a per-file display number plus an
        /// optional file (required when a merged document has several files).
        /// Returns null with an error message when unresolvable. UI thread.</summary>
        private ISegmentPair ResolveBridgeSegment(string id, string file, string number, out string error)
        {
            error = null;
            if (!string.IsNullOrEmpty(id))
            {
                var sep = id.LastIndexOf(':');
                if (sep <= 0 || sep == id.Length - 1)
                {
                    error = "malformed id – expected \"<paragraphUnitId>:<segmentId>\"";
                    return null;
                }
                var index = BuildSegmentPairIndex(null);
                ISegmentPair pair;
                if (!index.TryGetValue(KeyOf(id.Substring(0, sep), id.Substring(sep + 1)), out pair) || pair == null)
                {
                    error = "segment not found in the open document";
                    return null;
                }
                return pair;
            }

            if (string.IsNullOrEmpty(number))
            {
                error = "provide either 'id' or 'number' (+ 'file' in merged documents)";
                return null;
            }

            string filterFileId = null;
            if (!string.IsNullOrEmpty(file))
            {
                EnsureBridgeFileMapFresh();
                filterFileId = ResolveBridgeFileId(file);
                if (filterFileId == null)
                {
                    error = $"no file matching '{file}' – call get_files for the list";
                    return null;
                }
            }

            ISegmentPair found = null;
            int matches = 0;
            foreach (var pair in _activeDocument.SegmentPairs)
            {
                try
                {
                    var segId = pair.Properties?.Id.Id ?? "";
                    if (!segId.Equals(number, StringComparison.OrdinalIgnoreCase)) continue;
                    if (filterFileId != null)
                    {
                        var puId = _activeDocument.GetParentParagraphUnit(pair)
                            ?.Properties?.ParagraphUnitId.Id ?? "";
                        string fid;
                        if (!_puIdToFileId.TryGetValue(puId, out fid) || fid != filterFileId) continue;
                    }
                    matches++;
                    if (found == null) found = pair;
                }
                catch { }
            }

            if (found == null)
            {
                error = $"no segment number {number}" + (file != null ? $" in file '{file}'" : "");
                return null;
            }
            if (matches > 1 && filterFileId == null)
            {
                error = $"segment number {number} exists in more than one file of this merged document – " +
                        "specify 'file' to disambiguate";
                return null;
            }
            return found;
        }

        /// <summary>
        /// Bridge delegate for POST /v1/go-to-segment (MCP go_to_segment).
        /// Moves Studio's editor to the given segment so the user sees what
        /// the AI is talking about. Marshals to the UI thread.
        /// </summary>
        private BridgeResultResponse BridgeGoToSegment(BridgeGoToRequest req)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeResultResponse { Ok = false, Error = "ai assistant disposed" };
            if (ctrl.InvokeRequired)
                return (BridgeResultResponse)ctrl.Invoke(new Func<BridgeResultResponse>(() => BridgeGoToSegment(req)));
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies – marshal via the UI thread we
            // captured at startup instead. See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeGoToSegment(req));

            if (_activeDocument == null)
                return new BridgeResultResponse { Ok = false, Error = "no document is open in the Trados editor" };

            try
            {
                string error;
                var pair = ResolveBridgeSegment(req?.Id, req?.File, req?.Number, out error);
                if (pair == null)
                    return new BridgeResultResponse { Ok = false, Error = error };

                var puId = _activeDocument.GetParentParagraphUnit(pair)
                    ?.Properties?.ParagraphUnitId.Id ?? "";
                var segId = pair.Properties?.Id.Id ?? "";
                _activeDocument.SetActiveSegmentPair(puId, segId, true);
                return new BridgeResultResponse
                {
                    Ok = true,
                    Note = $"Studio's editor is now on segment {segId}."
                };
            }
            catch (Exception ex)
            {
                return new BridgeResultResponse { Ok = false, Error = "navigation failed: " + ex.Message };
            }
        }

        /// <summary>
        /// Bridge delegate for GET /v1/comments (MCP get_comments). Returns the
        /// Trados comments on a segment in stable index order (source-side
        /// markers first, then target-side), so update_comment can address one.
        /// Marshals to the UI thread.
        /// </summary>
        private BridgeCommentsResponse BridgeGetComments(string id)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeCommentsResponse { Ok = false, Error = "ai assistant disposed" };
            if (ctrl.InvokeRequired)
                return (BridgeCommentsResponse)ctrl.Invoke(new Func<BridgeCommentsResponse>(() => BridgeGetComments(id)));
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies – marshal via the UI thread we
            // captured at startup instead. See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeGetComments(id));

            if (_activeDocument == null)
                return new BridgeCommentsResponse { Ok = false, Error = "no document is open in the Trados editor" };

            try
            {
                string error;
                var pair = ResolveBridgeSegment(id, null, null, out error);
                if (pair == null)
                    return new BridgeCommentsResponse { Ok = false, Error = error };

                var response = new BridgeCommentsResponse { Ok = true, Comments = new List<BridgeCommentInfo>() };
                int index = 0;
                foreach (var c in EnumerateSegmentComments(pair))
                {
                    response.Comments.Add(new BridgeCommentInfo
                    {
                        Index = index++,
                        Author = c.Author,
                        Date = c.Date != DateTime.MinValue ? c.Date.ToString("yyyy-MM-dd HH:mm") : null,
                        Severity = c.Severity.ToString(),
                        Text = c.Text ?? ""
                    });
                }
                if (response.Comments.Count == 0)
                    response.Note = "This segment has no comments.";
                return response;
            }
            catch (Exception ex)
            {
                return new BridgeCommentsResponse { Ok = false, Error = "get comments failed: " + ex.Message };
            }
        }

        /// <summary>Comments on a segment pair in stable order: source-side
        /// markers first, then target-side, walking nested markup depth-first.
        /// The same order every time, so an index addresses one comment.</summary>
        private static List<Sdl.FileTypeSupport.Framework.NativeApi.IComment> EnumerateSegmentComments(ISegmentPair pair)
        {
            var list = new List<Sdl.FileTypeSupport.Framework.NativeApi.IComment>();
            CollectCommentObjects(pair?.Source, list);
            CollectCommentObjects(pair?.Target, list);
            return list;
        }

        private static void CollectCommentObjects(IAbstractMarkupDataContainer container,
            List<Sdl.FileTypeSupport.Framework.NativeApi.IComment> list)
        {
            if (container == null) return;
            foreach (var item in container)
            {
                if (item is ICommentMarker marker)
                {
                    try
                    {
                        var props = marker.Comments;
                        for (int i = 0; i < (props?.Count ?? 0); i++)
                        {
                            var c = props.GetItem(i);
                            if (c != null) list.Add(c);
                        }
                    }
                    catch { }
                    CollectCommentObjects(marker, list);
                }
                else if (item is IAbstractMarkupDataContainer nested)
                {
                    CollectCommentObjects(nested, list);
                }
            }
        }

        /// <summary>
        /// Bridge delegate for POST /v1/add-comment (MCP add_comment). Adds a
        /// Studio comment on the whole segment via the same API as the editor's
        /// Add Comment command. Marshals to the UI thread.
        /// </summary>
        private BridgeResultResponse BridgeAddComment(BridgeAddCommentRequest req)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeResultResponse { Ok = false, Error = "ai assistant disposed" };
            if (ctrl.InvokeRequired)
                return (BridgeResultResponse)ctrl.Invoke(new Func<BridgeResultResponse>(() => BridgeAddComment(req)));
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies – marshal via the UI thread we
            // captured at startup instead. See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeAddComment(req));

            if (_activeDocument == null)
                return new BridgeResultResponse { Ok = false, Error = "no document is open in the Trados editor" };
            if (string.IsNullOrWhiteSpace(req?.Text))
                return new BridgeResultResponse { Ok = false, Error = "missing 'text'" };

            try
            {
                string error;
                var pair = ResolveBridgeSegment(req.Id, null, null, out error);
                if (pair == null)
                    return new BridgeResultResponse { Ok = false, Error = error };

                var severity = Sdl.FileTypeSupport.Framework.NativeApi.Severity.Low;
                if (!string.IsNullOrEmpty(req.Severity)
                    && !Enum.TryParse(req.Severity, true, out severity))
                    return new BridgeResultResponse
                    {
                        Ok = false,
                        Error = $"unknown severity '{req.Severity}' – use Low, Medium, or High"
                    };

                _activeDocument.AddCommentOnSegment(pair, req.Text, severity);
                return new BridgeResultResponse
                {
                    Ok = true,
                    Note = "Comment added. It is part of the document's unsaved changes until the user saves in Studio."
                };
            }
            catch (Exception ex)
            {
                return new BridgeResultResponse { Ok = false, Error = "add comment failed: " + ex.Message };
            }
        }

        /// <summary>
        /// Bridge delegate for POST /v1/update-comment (MCP update_comment).
        /// Rewrites the text of an existing comment, addressed by the index
        /// get_comments returned. The edit runs inside ProcessSegmentPair so
        /// Studio registers the document as modified. Marshals to the UI thread.
        /// </summary>
        private BridgeResultResponse BridgeUpdateComment(BridgeUpdateCommentRequest req)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeResultResponse { Ok = false, Error = "ai assistant disposed" };
            if (ctrl.InvokeRequired)
                return (BridgeResultResponse)ctrl.Invoke(new Func<BridgeResultResponse>(() => BridgeUpdateComment(req)));
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies – marshal via the UI thread we
            // captured at startup instead. See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeUpdateComment(req));

            if (_activeDocument == null)
                return new BridgeResultResponse { Ok = false, Error = "no document is open in the Trados editor" };
            bool hasText = !string.IsNullOrWhiteSpace(req?.Text);
            bool hasSeverity = !string.IsNullOrWhiteSpace(req?.Severity);
            if (!hasText && !hasSeverity)
                return new BridgeResultResponse { Ok = false, Error = "nothing to change – provide 'text' and/or 'severity'" };

            var newSeverity = Sdl.FileTypeSupport.Framework.NativeApi.Severity.Low;
            if (hasSeverity && !Enum.TryParse(req.Severity, true, out newSeverity))
                return new BridgeResultResponse
                {
                    Ok = false,
                    Error = $"unknown severity '{req.Severity}' – use Low, Medium, or High"
                };

            try
            {
                string error;
                var pair = ResolveBridgeSegment(req.Id, null, null, out error);
                if (pair == null)
                    return new BridgeResultResponse { Ok = false, Error = error };

                string failure = null;
                bool updated = false;
                _activeDocument.ProcessSegmentPair(pair, "Supervertaler MCP",
                    (sp, cancel) =>
                    {
                        var comments = EnumerateSegmentComments(sp);
                        if (req.CommentIndex < 0 || req.CommentIndex >= comments.Count)
                        {
                            failure = $"comment index {req.CommentIndex} out of range – this segment has " +
                                      $"{comments.Count} comment(s); call get_comments first";
                            return;
                        }
                        if (hasText) comments[req.CommentIndex].Text = req.Text;
                        if (hasSeverity) comments[req.CommentIndex].Severity = newSeverity;
                        updated = true;
                    });

                if (!updated)
                    return new BridgeResultResponse { Ok = false, Error = failure ?? "comment not updated" };
                var whatChanged = hasText && hasSeverity ? "Comment text and severity replaced."
                    : hasText ? "Comment text replaced." : "Comment severity replaced.";
                return new BridgeResultResponse
                {
                    Ok = true,
                    Note = whatChanged + " Part of the document's unsaved changes until the user saves."
                };
            }
            catch (Exception ex)
            {
                return new BridgeResultResponse { Ok = false, Error = "update comment failed: " + ex.Message };
            }
        }

        /// <summary>
        /// Bridge delegate for POST /v1/delete-comment (MCP delete_comment).
        /// Removes a whole comment from a segment – addressed exactly like
        /// update_comment (segment id + the commentIndex from get_comments), or
        /// all of them with all=true. Studio's per-comment version history goes
        /// with the comment; version-level surgery stays in the UI. When a
        /// comment marker is left with no comments, the marker itself is
        /// unwrapped so no empty annotation lingers on the segment.
        /// </summary>
        private BridgeResultResponse BridgeDeleteComment(BridgeDeleteCommentRequest req)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeResultResponse { Ok = false, Error = "ai assistant disposed" };
            if (ctrl.InvokeRequired)
                return (BridgeResultResponse)ctrl.Invoke(new Func<BridgeResultResponse>(() => BridgeDeleteComment(req)));
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeDeleteComment(req));

            if (_activeDocument == null)
                return new BridgeResultResponse { Ok = false, Error = "no document is open in the Trados editor" };
            if (req == null)
                return new BridgeResultResponse { Ok = false, Error = "missing request body" };
            if (!req.All && req.CommentIndex < 0)
                return new BridgeResultResponse
                {
                    Ok = false,
                    Error = "missing 'commentIndex' – call get_comments for the segment's comment list, " +
                            "or pass all=true to remove every comment on the segment"
                };

            try
            {
                string error;
                var pair = ResolveBridgeSegment(req.Id, null, null, out error);
                if (pair == null)
                    return new BridgeResultResponse { Ok = false, Error = error };

                string failure = null;
                int removed = 0;
                _activeDocument.ProcessSegmentPair(pair, "Supervertaler MCP",
                    (sp, cancel) =>
                    {
                        var comments = EnumerateSegmentComments(sp);
                        if (!req.All && req.CommentIndex >= comments.Count)
                        {
                            failure = $"comment index {req.CommentIndex} out of range – this segment has " +
                                      $"{comments.Count} comment(s); call get_comments first";
                            return;
                        }
                        if (comments.Count == 0)
                        {
                            failure = "this segment has no comments";
                            return;
                        }

                        // Same enumeration order as get_comments/update_comment,
                        // so indices line up across all three tools.
                        var target = req.All ? null : comments[req.CommentIndex];
                        removed = RemoveCommentObjects(sp.Source, target)
                                + RemoveCommentObjects(sp.Target, target);
                    });

                if (removed == 0)
                    return new BridgeResultResponse { Ok = false, Error = failure ?? "comment not removed" };
                return new BridgeResultResponse
                {
                    Ok = true,
                    Note = (req.All ? $"Removed all {removed} comment(s) from the segment. "
                                    : "Comment removed. ") +
                           "Part of the document's unsaved changes until the user saves (save_document)."
                };
            }
            catch (Exception ex)
            {
                return new BridgeResultResponse { Ok = false, Error = "delete comment failed: " + ex.Message };
            }
        }

        /// <summary>
        /// Removes <paramref name="target"/> (or every comment when it is null)
        /// from the comment markers under <paramref name="container"/>. A marker
        /// left with no comments is unwrapped: its children are spliced into the
        /// parent so the text survives without a dangling annotation. Returns how
        /// many comments were removed.
        /// </summary>
        private static int RemoveCommentObjects(IAbstractMarkupDataContainer container,
            Sdl.FileTypeSupport.Framework.NativeApi.IComment target)
        {
            if (container == null) return 0;
            int removed = 0;

            // Snapshot first – the collection is mutated while walking it.
            var items = new List<IAbstractMarkupData>();
            foreach (var item in container) items.Add(item);

            foreach (var item in items)
            {
                if (item is ICommentMarker marker)
                {
                    try
                    {
                        // ICommentProperties exposes Delete(IComment) – collect the
                        // victims first, then delete (the collection is live).
                        var props = marker.Comments;
                        var doomed = new List<Sdl.FileTypeSupport.Framework.NativeApi.IComment>();
                        for (int i = 0; i < (props?.Count ?? 0); i++)
                        {
                            var c = props.GetItem(i);
                            if (c == null) continue;
                            if (target == null || ReferenceEquals(c, target))
                                doomed.Add(c);
                        }
                        foreach (var c in doomed)
                        {
                            props.Delete(c);
                            removed++;
                        }
                    }
                    catch { }

                    // Recurse before possibly unwrapping this marker.
                    removed += RemoveCommentObjects(marker, target);

                    try
                    {
                        if ((marker.Comments?.Count ?? 0) == 0)
                        {
                            // Move the marker's children up into the parent, then drop it.
                            var children = new List<IAbstractMarkupData>();
                            foreach (var child in marker) children.Add(child);
                            var index = container.IndexOf(marker);
                            foreach (var child in children) child.RemoveFromParent();
                            marker.RemoveFromParent();
                            for (int i = 0; i < children.Count; i++)
                                container.Insert(index + i, children[i]);
                        }
                    }
                    catch { /* leave the (now comment-less) marker in place */ }
                }
                else if (item is IAbstractMarkupDataContainer nested)
                {
                    removed += RemoveCommentObjects(nested, target);
                }
            }
            return removed;
        }

        /// <summary>
        /// Bridge delegate for POST /v1/run-task (MCP pretranslate / update_tm /
        /// export_target). Runs one of Studio's own batch tasks on the project's
        /// target files via RunAutomaticTask, mirroring the run_verification
        /// pattern. Project reference is grabbed on the UI thread; the task runs
        /// on the calling (bridge) background thread.
        ///
        /// Safety: update-tm and export-target only READ the sdlxliff (results
        /// reflect the last save). pretranslate WRITES into the sdlxliff, so it
        /// can conflict with the document open in the editor – the caller is
        /// told to save and, if it errors, to close the document and retry.
        /// </summary>
        private BridgeRunTaskResponse BridgeRunTask(BridgeRunTaskRequest req)
        {
            var task = (req?.Task ?? "").Trim().ToLowerInvariant();

            string templateId;
            string friendly;
            string safetyNote;
            switch (task)
            {
                case "pretranslate":
                    templateId = Sdl.ProjectAutomation.Core.AutomaticTaskTemplateIds.PreTranslateFiles;
                    friendly = "Pre-translate";
                    safetyNote = "Pre-translation wrote TM matches INTO the document on disk. If the document " +
                        "is open in the editor you may need to close and reopen it to see the results. It ran " +
                        "against the last-saved state, so unsaved edits weren't considered.";
                    break;
                case "update-tm":
                case "update_tm":
                    templateId = Sdl.ProjectAutomation.Core.AutomaticTaskTemplateIds.UpdateMainTranslationMemories;
                    friendly = "Update main translation memories";
                    safetyNote = "Confirmed segments from the last-saved state were written to the project's " +
                        "main TM(s). If you just confirmed segments in the editor, save first and run again to " +
                        "include them.";
                    break;
                case "export-target":
                case "export_target":
                    templateId = Sdl.ProjectAutomation.Core.AutomaticTaskTemplateIds.GenerateTargetTranslations;
                    friendly = "Generate target translations";
                    safetyNote = "The translated target files were generated from the last-saved state into the " +
                        "project's target-language folder. In Studio, right-click a file and choose Open Target, " +
                        "or open the project folder to find them.";
                    break;
                case "analyze":
                case "analyse":
                case "analyze-files":
                    templateId = Sdl.ProjectAutomation.Core.AutomaticTaskTemplateIds.AnalyzeFiles;
                    friendly = "Analyse Files";
                    safetyNote = "Analyse Files ran on the last-saved state and wrote the leverage breakdown " +
                        "(perfect/exact/fuzzy/new/repetitions) into the project. get_project_statistics will now " +
                        "return those bands. If you translated or confirmed segments since the last save, save the " +
                        "document first and run this again for up-to-date numbers.";
                    break;
                default:
                    return new BridgeRunTaskResponse
                    {
                        Ok = false,
                        Error = $"unknown task '{req?.Task}' – use analyze, pretranslate, update-tm, or export-target"
                    };
            }

            Sdl.ProjectAutomation.FileBased.FileBasedProject project = null;
            Guid[] fileIds = null;
            string grabError = null;
            var ctrl = _control?.Value;
            Action grab = () =>
            {
                try
                {
                    project = _activeDocument?.Project as Sdl.ProjectAutomation.FileBased.FileBasedProject;
                    if (project == null) { grabError = "no file-based project is open in the editor"; return; }
                    fileIds = project.GetTargetLanguageFiles()
                        .Where(f => f.Role != Sdl.ProjectAutomation.Core.FileRole.Reference)
                        .Select(f => f.Id).ToArray();
                }
                catch (Exception ex) { grabError = ex.Message; }
            };
            try
            {
                if (ctrl != null && !ctrl.IsDisposed && (ctrl.InvokeRequired || (UiThread.InvokeRequired && UiThread.IsAvailable))) UiThread.Invoke(grab);
                else grab();
            }
            catch (Exception ex) { grabError = ex.Message; }

            if (grabError != null)
                return new BridgeRunTaskResponse { Ok = false, Error = grabError, Task = task };
            if (project == null || fileIds == null || fileIds.Length == 0)
                return new BridgeRunTaskResponse { Ok = false, Error = "no target files to process", Task = task };

            // Only one batch task at a time – they mutate the project on disk and
            // running two concurrently would clobber each other.
            var busy = GetRunningJob();
            if (busy != null)
                return new BridgeRunTaskResponse
                {
                    Ok = false,
                    Task = task,
                    Error = $"a batch task ({busy.Friendly}, jobId \"{busy.Id}\") is still running – " +
                            "wait for it to finish (poll get_task_status) before starting another."
                };

            // Batch tasks can run for minutes on a real project – longer than an
            // MCP tool call is allowed to block – so run in the background and
            // return immediately. The AI polls get_task_status for completion.
            var job = new RunTaskJob
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                Task = task,
                Friendly = friendly,
                Status = "running",
                StartedUtc = DateTime.UtcNow,
                FileCount = fileIds.Length,
                SafetyNote = safetyNote
            };
            RegisterJob(job);

            var projectForJob = project;
            var fileIdsForJob = fileIds;
            var templateForJob = templateId;
            System.Threading.ThreadPool.QueueUserWorkItem(
                _ => RunTaskJobBody(job, projectForJob, fileIdsForJob, templateForJob));

            return new BridgeRunTaskResponse
            {
                Ok = true,
                Started = true,
                JobId = job.Id,
                Task = task,
                FilesProcessed = fileIds.Length,
                Note = $"Started {friendly} on {fileIds.Length} file(s) in the background – it can take a while on " +
                       $"large projects. Poll get_task_status with jobId \"{job.Id}\" to see when it finishes" +
                       (task == "analyze"
                           ? "; once it reports done, get_project_statistics will show the leverage bands."
                           : ".")
            };
        }

        // ── Async batch-task jobs (run-task) ────────────────────────────────
        //
        // Batch tasks (analyse, pre-translate, update-TM, generate-target) run on
        // a background thread and report progress via a small in-memory job
        // registry that get_task_status reads. Same behaviour as the old
        // synchronous path (RunAutomaticTask off the UI thread), just non-blocking.

        private sealed class RunTaskJob
        {
            public readonly object Sync = new object();
            public string Id;
            public string Task;
            public string Friendly;
            public string Status;          // running | done | failed
            public DateTime StartedUtc;
            public DateTime? FinishedUtc;
            public int FileCount;
            public List<string> Messages = new List<string>();
            public string Error;
            public string Note;
            public string SafetyNote;
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RunTaskJob> _runTaskJobs
            = new System.Collections.Concurrent.ConcurrentDictionary<string, RunTaskJob>();

        private void RegisterJob(RunTaskJob job)
        {
            _runTaskJobs[job.Id] = job;
            // Bound memory: keep only the 20 most recent finished jobs.
            if (_runTaskJobs.Count > 20)
            {
                foreach (var old in _runTaskJobs.Values
                    .Where(j => j.FinishedUtc != null)
                    .OrderBy(j => j.FinishedUtc.Value)
                    .Take(_runTaskJobs.Count - 20))
                {
                    _runTaskJobs.TryRemove(old.Id, out _);
                }
            }
        }

        private RunTaskJob GetRunningJob()
        {
            foreach (var j in _runTaskJobs.Values)
                lock (j.Sync) { if (j.Status == "running") return j; }
            return null;
        }

        private void RunTaskJobBody(RunTaskJob job, Sdl.ProjectAutomation.FileBased.FileBasedProject project,
            Guid[] fileIds, string templateId)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BridgeLog.Write($"[run-task] {job.Friendly} (job {job.Id}) started on {fileIds.Length} file(s)");
            try
            {
                EventHandler<Sdl.ProjectAutomation.Core.TaskStatusEventArgs> onStatus = (s, e) => { };
                EventHandler<Sdl.ProjectAutomation.Core.TaskMessageEventArgs> onMsg = (s, e) =>
                {
                    try
                    {
                        var m = e?.Message?.ToString();
                        if (!string.IsNullOrWhiteSpace(m))
                            lock (job.Sync) { if (job.Messages.Count < 50) job.Messages.Add(m.Trim()); }
                    }
                    catch { }
                };

                var result = project.RunAutomaticTask(fileIds, templateId, onStatus, onMsg);

                bool failed = false;
                try
                {
                    var status = result?.Status.ToString() ?? "";
                    failed = status.IndexOf("Fail", StringComparison.OrdinalIgnoreCase) >= 0
                          || status.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0
                          || status.IndexOf("Reject", StringComparison.OrdinalIgnoreCase) >= 0
                          || status.IndexOf("Invalid", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch { }

                sw.Stop();
                lock (job.Sync)
                {
                    job.FinishedUtc = DateTime.UtcNow;
                    if (failed)
                    {
                        job.Status = "failed";
                        job.Error = $"{job.Friendly} did not complete successfully" +
                            (job.Task == "pretranslate" ? " – if the document is open in the editor, close it and try again." : "");
                    }
                    else
                    {
                        job.Status = "done";
                        job.Note = $"{job.Friendly} completed on {job.FileCount} file(s). {job.SafetyNote}";
                    }
                }
                BridgeLog.Write($"[run-task] {job.Friendly} (job {job.Id}) {job.Status} in {sw.Elapsed.TotalSeconds:F1}s");
            }
            catch (Exception ex)
            {
                sw.Stop();
                lock (job.Sync)
                {
                    job.FinishedUtc = DateTime.UtcNow;
                    job.Status = "failed";
                    job.Error = $"{job.Friendly} failed: {ex.Message}" +
                        (job.Task == "pretranslate" ? " (if the document is open in the editor, close it and retry)" : "");
                }
                BridgeLog.Write($"[run-task] {job.Friendly} (job {job.Id}) threw after {sw.Elapsed.TotalSeconds:F1}s: {ex.Message}");
            }
        }

        /// <summary>Bridge delegate for GET /v1/task-status (MCP get_task_status).</summary>
        private BridgeTaskStatusResponse BridgeGetTaskStatus(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return new BridgeTaskStatusResponse { Ok = true, Found = false, Error = "no jobId given" };

            if (!_runTaskJobs.TryGetValue(jobId.Trim(), out var job))
                return new BridgeTaskStatusResponse
                {
                    Ok = true,
                    Found = false,
                    JobId = jobId,
                    Error = "no batch task with that jobId (it may have expired – only the most recent jobs are kept)"
                };

            lock (job.Sync)
            {
                var end = job.FinishedUtc ?? DateTime.UtcNow;
                return new BridgeTaskStatusResponse
                {
                    Ok = true,
                    Found = true,
                    JobId = job.Id,
                    Task = job.Task,
                    Status = job.Status,
                    Running = job.Status == "running",
                    FilesProcessed = job.FileCount,
                    ElapsedSeconds = (int)Math.Round((end - job.StartedUtc).TotalSeconds),
                    Messages = job.Messages.Count > 0 ? new List<string>(job.Messages) : null,
                    Note = job.Status == "failed" ? job.Error : job.Note
                };
            }
        }

        /// <summary>
        /// Bridge delegate for POST /v1/find-replace (MCP find_and_replace).
        /// Replaces text in segment TARGETS across the open document, using the
        /// same tag-safe per-IText replacement as SuperSearch's Replace All: a
        /// match that straddles an inline tag boundary is skipped (reported)
        /// rather than corrupting the segment. Supports dry-run (preview),
        /// case/whole-word/regex options, and file/status filters. Marshals to
        /// the UI thread.
        /// </summary>
        private BridgeFindReplaceResponse BridgeFindReplace(BridgeFindReplaceRequest req)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeFindReplaceResponse { Ok = false, Error = "ai assistant disposed" };
            if (ctrl.InvokeRequired)
                return (BridgeFindReplaceResponse)ctrl.Invoke(new Func<BridgeFindReplaceResponse>(() => BridgeFindReplace(req)));
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies – marshal via the UI thread we
            // captured at startup instead. See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeFindReplace(req));

            if (_activeDocument == null)
                return new BridgeFindReplaceResponse { Ok = false, Error = "no document is open in the Trados editor" };
            if (string.IsNullOrEmpty(req?.Find))
                return new BridgeFindReplaceResponse { Ok = false, Error = "missing 'find'" };

            // Decode before validating, so the pattern that gets checked is the
            // one that will actually run.
            var find = req.DecodeEntities ? Core.EntityEscapes.Decode(req.Find) : req.Find;

            // Validate a regex up front so a bad pattern fails clearly.
            if (req.Regex)
            {
                try { var _ = new System.Text.RegularExpressions.Regex(find); }
                catch (Exception ex)
                {
                    return new BridgeFindReplaceResponse { Ok = false, Error = "invalid regex: " + ex.Message };
                }
            }

            // Confirmation-status policy. Writing content through
            // ProcessSegmentPair makes Studio demote the segment to Draft, which
            // is right mid-translation and wrong for a consistency sweep over a
            // finished file – a single replace would quietly turn 2,889
            // Translated segments into Draft ones. Default to putting each
            // segment's status back.
            var statusMode = string.IsNullOrWhiteSpace(req.SetStatus) ? "preserve" : req.SetStatus.Trim();
            bool preserveStatus = statusMode.Equals("preserve", StringComparison.OrdinalIgnoreCase);
            Sdl.Core.Globalization.ConfirmationLevel forcedLevel = default;
            if (!preserveStatus
                && !Enum.TryParse(statusMode, true, out forcedLevel))
            {
                return new BridgeFindReplaceResponse
                {
                    Ok = false,
                    Error = $"unknown setStatus '{statusMode}' – use 'preserve' (default) or one of: " +
                            "Unspecified, Draft, Translated, RejectedTranslation, ApprovedTranslation, " +
                            "RejectedSignOff, ApprovedSignOff"
                };
            }

            var response = new BridgeFindReplaceResponse
            {
                Ok = true,
                DryRun = req.DryRun,
                StatusMode = preserveStatus ? "preserve" : forcedLevel.ToString(),
                Changes = new List<BridgeFindReplaceChange>(),
                SkippedTagSpanning = new List<string>()
            };

            // File filter (merged documents).
            string filterFileId = null;
            bool attributeFiles = false;
            if (!string.IsNullOrEmpty(req.File))
            {
                EnsureBridgeFileMapFresh();
                filterFileId = ResolveBridgeFileId(req.File);
                if (filterFileId == null)
                    return new BridgeFindReplaceResponse
                    {
                        Ok = false,
                        Error = $"no file matching '{req.File}' – call get_files for the list"
                    };
                attributeFiles = true;
            }
            else if (_fileIdToName.Count > 1)
            {
                EnsureBridgeFileMapFresh();
                attributeFiles = _perFileMappingWorked && _fileIdToName.Count > 1;
            }

            var replace = req.Replace ?? "";
            // Entity decoding is what makes a non-breaking space insertable at
            // all: "(\d) (V|mm|%)" -> "$1&nbsp;$2" is the whole point of the
            // feature, and a literal U+00A0 in the argument would not survive.
            // It applies to 'find' as well, or the character would be writable
            // but not searchable - you could create non-breaking spaces and
            // then have no way to audit or remove them again.
            if (req.DecodeEntities) replace = Core.EntityEscapes.Decode(replace);
            int processed = 0;

            // Status writes are deferred to a second pass. Setting the level
            // inline, straight after ProcessSegmentPair, does NOT work: reading
            // ConfirmationLevel back at that point still returns the PRE-edit
            // value, and Studio applies its own demotion to Draft after our
            // callback returns. So an inline write gets overwritten, and a
            // "has it actually changed?" guard never fires in the first place.
            var pendingStatus =
                new List<KeyValuePair<ISegmentPair, Sdl.Core.Globalization.ConfirmationLevel>>();

            foreach (var pair in _activeDocument.SegmentPairs)
            {
                processed++;
                if (processed % 100 == 0)
                    System.Windows.Forms.Application.DoEvents();

                try
                {
                    if (pair.Target == null) continue;

                    if (!string.IsNullOrEmpty(req.Status))
                    {
                        var st = (pair.Properties?.ConfirmationLevel
                            ?? Sdl.Core.Globalization.ConfirmationLevel.Unspecified).ToString();
                        if (!st.Equals(req.Status, StringComparison.OrdinalIgnoreCase)) continue;
                    }

                    var puId = _activeDocument.GetParentParagraphUnit(pair)
                        ?.Properties?.ParagraphUnitId.Id ?? "";
                    if (filterFileId != null)
                    {
                        string fid;
                        if (!_puIdToFileId.TryGetValue(puId, out fid) || fid != filterFileId) continue;
                    }

                    // Work from the CONCATENATED TEXT NODES, not pair.Target.ToString().
                    // ToString() renders the markup too (<cf size=8> and friends), so the
                    // "expected" string carried tag syntax the per-node simulation below
                    // could never reproduce – and every segment containing any tag was
                    // rejected as tag-spanning, however safely the match sat inside a
                    // single text node. Confirmed live: "Overzicht aansluitingen" inside
                    // one <t1>…</t1> wrapper was refused while the same phrase in untagged
                    // segments was replaced. Both sides of the comparison must be the same
                    // kind of string for the check to mean anything.
                    var iTexts = new List<IText>();
                    FindReplaceCollectTexts(pair.Target, iTexts);
                    var currentTarget = string.Concat(iTexts.Select(t => t.Properties.Text ?? ""));
                    var expected = FindReplacePerform(currentTarget, find, replace,
                        req.CaseSensitive, req.Regex, req.WholeWord);
                    if (expected == currentTarget) continue; // no match here

                    var segId = pair.Properties?.Id.Id ?? "";
                    var id = puId + ":" + segId;
                    string fileName = null;
                    if (attributeFiles)
                    {
                        string fid;
                        if (_puIdToFileId.TryGetValue(puId, out fid) && fid != null)
                            _fileIdToName.TryGetValue(fid, out fileName);
                    }

                    if (pair.Properties?.IsLocked == true)
                    {
                        response.SkippedLocked++;
                        continue;
                    }

                    // Tag-boundary safety: replacing across the whole plain text must give
                    // the same result as replacing inside each text node separately. When
                    // it does not, the match really does straddle a tag boundary and only
                    // a manual edit is safe. (iTexts was collected above, with the plain
                    // text this compares against.)
                    var simulated = string.Concat(iTexts.Select(t =>
                        FindReplacePerform(t.Properties.Text ?? "", find, replace,
                            req.CaseSensitive, req.Regex, req.WholeWord)));
                    if (iTexts.Count == 0 || simulated != expected)
                    {
                        response.SkippedTagSpanning.Add(fileName != null ? $"{fileName}:{segId}" : segId);
                        continue;
                    }

                    response.SegmentsChanged++;
                    if (response.Changes.Count < 100)
                    {
                        response.Changes.Add(new BridgeFindReplaceChange
                        {
                            Id = id,
                            Number = segId,
                            FileName = fileName,
                            Before = currentTarget.Length > 200 ? currentTarget.Substring(0, 200) + "…" : currentTarget,
                            After = expected.Length > 200 ? expected.Substring(0, 200) + "…" : expected
                        });
                    }

                    if (!req.DryRun)
                    {
                        // Read the status before the edit: ProcessSegmentPair is
                        // what demotes it, so this is the only chance to know
                        // what to put back.
                        var levelBefore = pair.Properties?.ConfirmationLevel
                            ?? Sdl.Core.Globalization.ConfirmationLevel.Unspecified;

                        _activeDocument.ProcessSegmentPair(pair, "Supervertaler MCP",
                            (sp, cancel) =>
                            {
                                var liveTexts = new List<IText>();
                                FindReplaceCollectTexts(sp.Target, liveTexts);
                                foreach (var t in liveTexts)
                                {
                                    var oldVal = t.Properties.Text ?? "";
                                    var newVal = FindReplacePerform(oldVal, find, replace,
                                        req.CaseSensitive, req.Regex, req.WholeWord);
                                    if (!string.Equals(oldVal, newVal, StringComparison.Ordinal))
                                        t.Properties.Text = newVal;
                                }
                            });
                        BridgeRecordWrite(id); // coverage: this segment was written this session

                        pendingStatus.Add(
                            new KeyValuePair<ISegmentPair, Sdl.Core.Globalization.ConfirmationLevel>(
                                pair, preserveStatus ? levelBefore : forcedLevel));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SupervertalerBridge] find-replace segment threw: {ex.Message}");
                }
            }

            // Second pass: now that every content edit has been committed and
            // Studio has applied whatever demotion it wanted to, write the
            // status we actually want. Unconditional — see the note above on
            // why comparing against the current value is useless here.
            foreach (var kv in pendingStatus)
            {
                try
                {
                    var sp = kv.Key;
                    if (sp.Properties == null) continue;
                    sp.Properties.ConfirmationLevel = kv.Value;
                    _activeDocument.UpdateSegmentPairProperties(sp, sp.Properties);
                    if (preserveStatus && kv.Value != Sdl.Core.Globalization.ConfirmationLevel.Draft)
                        response.StatusRestored++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SupervertalerBridge] find-replace status restore threw: {ex.Message}");
                }
            }

            response.Returned = response.Changes.Count;
            response.Truncated = response.SegmentsChanged > response.Returned;

            var verb = req.DryRun ? "would change" : "changed";
            response.Note = $"{(req.DryRun ? "Preview: " : "")}{verb} {response.SegmentsChanged} segment(s).";
            if (response.SkippedTagSpanning.Count > 0)
                response.Note += $" {response.SkippedTagSpanning.Count} segment(s) skipped because the match " +
                    "straddles inline formatting/tags – those need a manual edit in Studio.";
            if (response.SkippedLocked > 0)
                response.Note += $" {response.SkippedLocked} locked segment(s) skipped.";
            if (!req.DryRun && response.StatusRestored > 0)
                response.Note += $" Confirmation status preserved on {response.StatusRestored} segment(s) " +
                    "(editing content would otherwise have demoted them to Draft) – pass setStatus to change that.";
            else if (!req.DryRun && !preserveStatus && response.SegmentsChanged > 0)
                response.Note += $" All changed segments were set to {forcedLevel}.";
            if (!req.DryRun && response.SegmentsChanged > 0)
            {
                response.Note += " Changes are in the open document; the user still needs to save in Studio.";
                _bridgeUnsavedWritesDoc = _activeDocument;
            }
            if (req.DryRun && response.SegmentsChanged > 0)
                response.Note += " Nothing was written – call again with dryRun=false to apply.";
            return response;
        }

        private static void FindReplaceCollectTexts(IAbstractMarkupDataContainer container, List<IText> sink)
        {
            if (container == null) return;
            foreach (var item in container)
            {
                if (item is IText t) sink.Add(t);
                else if (item is IAbstractMarkupDataContainer inner) FindReplaceCollectTexts(inner, sink);
            }
        }

        /// <summary>Text replacement matching SuperSearch's PerformReplace:
        /// regex, whole-word (\b-bounded literal), or plain substring, all with
        /// optional case sensitivity. Bad regex/pattern yields the input
        /// unchanged (validated up front by the caller).</summary>
        private static string FindReplacePerform(string text, string search, string replace,
            bool caseSensitive, bool useRegex, bool wholeWord)
        {
            var opts = caseSensitive
                ? System.Text.RegularExpressions.RegexOptions.None
                : System.Text.RegularExpressions.RegexOptions.IgnoreCase;

            if (useRegex)
            {
                try { return System.Text.RegularExpressions.Regex.Replace(text, search, replace, opts); }
                catch { return text; }
            }
            if (wholeWord)
            {
                try
                {
                    return System.Text.RegularExpressions.Regex.Replace(text,
                        @"\b" + System.Text.RegularExpressions.Regex.Escape(search) + @"\b",
                        (replace ?? "").Replace("$", "$$"), opts);
                }
                catch { return text; }
            }
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(search)) return text;

            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var sb = new System.Text.StringBuilder();
            int pos = 0;
            while (pos < text.Length)
            {
                int idx = text.IndexOf(search, pos, comparison);
                if (idx < 0) { sb.Append(text, pos, text.Length - pos); break; }
                sb.Append(text, pos, idx - pos);
                sb.Append(replace);
                pos = idx + search.Length;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Bridge delegate for POST /v1/verify (MCP run_verification). Runs
        /// Studio's own "Verify Files" batch task (QA Checker 3.0, tag and term
        /// verifiers, etc.) and returns its findings mapped to file + segment
        /// number. This is the ONLY way to get Studio's native QA results –
        /// the editor's F8 Messages pane has no read API; running the batch
        /// task and parsing its XML report is RWS's own blessed pattern.
        ///
        /// The project reference is grabbed on the UI thread; the task itself
        /// runs on the calling (bridge) thread, which is a background thread –
        /// correct, since batch tasks must not run on the UI thread. Findings
        /// reflect the LAST SAVED state of the files (batch tasks read the
        /// sdlxliff on disk), so unsaved editor edits aren't included.
        /// </summary>
        private BridgeVerifyResponse BridgeRunVerification()
        {
            Sdl.ProjectAutomation.FileBased.FileBasedProject project = null;
            Guid[] fileIds = null;
            var fileNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string grabError = null;
            bool stale = false;

            var ctrl = _control?.Value;
            Action grab = () =>
            {
                try
                {
                    stale = BridgeHasUnsavedWrites;
                    project = _activeDocument?.Project as Sdl.ProjectAutomation.FileBased.FileBasedProject;
                    if (project == null) { grabError = "no file-based project is open in the editor"; return; }
                    var targets = project.GetTargetLanguageFiles()
                        .Where(f => f.Role != Sdl.ProjectAutomation.Core.FileRole.Reference)
                        .ToList();
                    fileIds = targets.Select(f => f.Id).ToArray();
                    foreach (var f in targets) fileNames[f.Id.ToString()] = f.Name;
                }
                catch (Exception ex) { grabError = ex.Message; }
            };
            try
            {
                if (ctrl != null && !ctrl.IsDisposed && (ctrl.InvokeRequired || (UiThread.InvokeRequired && UiThread.IsAvailable))) UiThread.Invoke(grab);
                else grab();
            }
            catch (Exception ex) { grabError = ex.Message; }

            if (grabError != null)
                return new BridgeVerifyResponse { Ok = false, Error = grabError };
            if (project == null || fileIds == null || fileIds.Length == 0)
                return new BridgeVerifyResponse { Ok = false, Error = "no target files to verify" };

            string reportXml = null;
            Guid? reportId = null;
            try
            {
                Sdl.ProjectAutomation.Core.AutomaticTask result;
                try
                {
                    EventHandler<Sdl.ProjectAutomation.Core.TaskStatusEventArgs> onStatus = (s, e) => { };
                    EventHandler<Sdl.ProjectAutomation.Core.TaskMessageEventArgs> onMsg = (s, e) => { };
                    result = project.RunAutomaticTask(
                        fileIds,
                        Sdl.ProjectAutomation.Core.AutomaticTaskTemplateIds.VerifyFiles,
                        onStatus, onMsg);
                }
                catch (Exception ex)
                {
                    return new BridgeVerifyResponse { Ok = false, Error = "verify task failed to run: " + ex.Message };
                }

                var report = result?.Reports?.FirstOrDefault();
                if (report == null)
                    return new BridgeVerifyResponse
                    {
                        Ok = true,
                        FindingsCount = 0,
                        Findings = new List<BridgeVerifyFinding>(),
                        Note = "Verification ran but produced no report."
                    };

                reportId = report.Id;
                var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
                project.SaveTaskReportAs(reportId.Value, path, Sdl.ProjectAutomation.Core.ReportFormat.Xml);
                reportXml = File.ReadAllText(path);
                try { File.Delete(path); } catch { }
            }
            finally
            {
                // Best-effort: remove the report we just generated so repeated
                // MCP verify calls don't clutter the project's Reports view.
                if (reportId != null)
                {
                    try
                    {
                        new Sdl.ProjectAutomation.FileBased.Reports.Operations.ProjectReportsOperations(project)
                            .RemoveReports(new List<Guid> { reportId.Value });
                    }
                    catch { }
                }
            }

            var response = new BridgeVerifyResponse
            {
                Ok = true,
                Stale = stale,
                Findings = new List<BridgeVerifyFinding>()
            };
            try
            {
                var findings = ParseVerifyReport(reportXml, fileNames);
                response.FindingsCount = findings.Count;
                response.Findings = findings.Take(200).ToList();
                response.Returned = response.Findings.Count;
                response.Truncated = findings.Count > response.Returned;
                if (findings.Count == 0)
                    response.Note = "Studio's verification found no issues in the last-saved state of the files.";
                else
                    response.Note = "These are Trados Studio's own QA Checker findings, from the LAST SAVED " +
                        "state of the document – if you have unsaved edits, ask the user to save (Ctrl+S) and " +
                        "run again. Triage each against the source before fixing; some are false positives.";
                if (stale)
                    response.Note = "STALE RESULTS: you have applied edits to this document that are not saved " +
                        "yet, so these findings describe the file BEFORE those edits and some will already be " +
                        "fixed. Save the document (save_document, or ask the user for Ctrl+S) and run this " +
                        "again before reporting any of it. " + response.Note;
                if (response.Truncated)
                    response.Note = $"Showing {response.Returned} of {findings.Count} findings. " + response.Note;
            }
            catch (Exception ex)
            {
                response.Note = "Verification ran but its report could not be parsed: " + ex.Message;
            }
            return response;
        }

        /// <summary>Defensive parse of a Trados Verify Files XML report into
        /// findings. Schema-tolerant (element names vary across versions): pulls
        /// the segment number, severity, and message text from whatever the
        /// report provides, mapping each file's guid to its name.</summary>
        private static List<BridgeVerifyFinding> ParseVerifyReport(string xml, Dictionary<string, string> fileNames)
        {
            var findings = new List<BridgeVerifyFinding>();
            if (string.IsNullOrWhiteSpace(xml)) return findings;

            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(xml);

            foreach (System.Xml.XmlNode fileNode in doc.SelectNodes("//*[local-name()='file']"))
            {
                var guid = fileNode.Attributes?["guid"]?.Value;
                var fname = fileNode.Attributes?["name"]?.Value;
                if (string.IsNullOrEmpty(fname) && !string.IsNullOrEmpty(guid))
                    fileNames.TryGetValue(guid, out fname);

                foreach (System.Xml.XmlNode msg in fileNode.SelectNodes(".//*[local-name()='Message']"))
                {
                    // Skip messages the user has already ignored/dismissed.
                    var ignored = FirstDescendantText(msg, "Ignored");
                    if (!string.IsNullOrEmpty(ignored)
                        && ignored.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Exact schema (Studio Verify report): SegmentId is the grid
                    // number, ParagraphUnitId + SegmentId form the addressable id,
                    // Origin is the QA rule, Text is the message, ErrorLevel the
                    // severity. Generic fallbacks retained for version drift.
                    var number = FirstDescendantText(msg, "SegmentId");
                    var puId = FirstDescendantText(msg, "ParagraphUnitId");
                    var origin = FirstDescendantText(msg, "Origin");
                    var severity = FirstDescendantText(msg, "ErrorLevel")
                        ?? FirstMatchingValue(msg, n => n.Contains("sever") || n.Contains("level"));
                    var text = FirstDescendantText(msg, "Text")
                        ?? FirstDescendantText(msg, "Description")
                        ?? FirstDescendantText(msg, "MessageText");
                    if (string.IsNullOrWhiteSpace(text))
                        text = (msg.InnerText ?? "").Trim();

                    string id = null;
                    if (!string.IsNullOrWhiteSpace(puId) && !string.IsNullOrWhiteSpace(number))
                        id = puId.Trim() + ":" + number.Trim();

                    findings.Add(new BridgeVerifyFinding
                    {
                        File = fname,
                        Number = string.IsNullOrWhiteSpace(number) ? null : number.Trim(),
                        Id = id,
                        Severity = NormalizeSeverity(severity),
                        Origin = string.IsNullOrWhiteSpace(origin) ? null : origin.Trim(),
                        Message = (text ?? "").Trim()
                    });
                }
            }
            return findings;
        }

        private static string FirstDescendantText(System.Xml.XmlNode node, string localName)
        {
            var hit = node.SelectSingleNode($".//*[local-name()='{localName}']");
            var t = hit?.InnerText?.Trim();
            return string.IsNullOrEmpty(t) ? null : t;
        }

        /// <summary>First attribute value, then first child-element text, whose
        /// (lower-cased) name satisfies <paramref name="nameMatches"/>. Used to
        /// pull a field like severity out of a schema-variable report node.</summary>
        private static string FirstMatchingValue(System.Xml.XmlNode node, Func<string, bool> nameMatches)
        {
            if (node.Attributes != null)
                foreach (System.Xml.XmlAttribute a in node.Attributes)
                    if (nameMatches(a.Name.ToLowerInvariant()) && !string.IsNullOrWhiteSpace(a.Value))
                        return a.Value.Trim();
            foreach (System.Xml.XmlNode child in node.ChildNodes)
                if (child.NodeType == System.Xml.XmlNodeType.Element
                    && nameMatches(child.LocalName.ToLowerInvariant())
                    && !string.IsNullOrWhiteSpace(child.InnerText))
                    return child.InnerText.Trim();
            return null;
        }

        /// <summary>Maps whatever the report used (name or numeric code) to
        /// Error / Warning / Note, best-effort. Trados severities are commonly
        /// 0/1/2 or Error/Warning/Note/Information.</summary>
        private static string NormalizeSeverity(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var v = raw.Trim();
            var low = v.ToLowerInvariant();
            if (low.Contains("err")) return "Error";
            if (low.Contains("warn")) return "Warning";
            if (low.Contains("note") || low.Contains("info")) return "Note";
            if (v == "2") return "Error";
            if (v == "1") return "Warning";
            if (v == "0") return "Note";
            return v; // unknown – pass through verbatim
        }

        /// <summary>
        /// Resolves the supervertaler.db path for the bridge's TM/termbase
        /// endpoints, using the same priority as TermLens: the user-set
        /// termbase path first, then the shared user-data root, then the
        /// legacy default locations. Returns null if nothing exists.
        /// </summary>
        private string ResolveSupervertalerDbPath()
        {
            try
            {
                var candidates = new List<string>();
                if (!string.IsNullOrEmpty(_settings?.TermbasePath))
                    candidates.Add(_settings.TermbasePath);
                candidates.Add(Path.Combine(UserDataPath.ResourcesDir, "supervertaler.db"));
                candidates.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Supervertaler_Data", "resources", "supervertaler.db"));
                candidates.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Supervertaler", "resources", "supervertaler.db"));

                return candidates.FirstOrDefault(File.Exists);
            }
            catch
            {
                return null;
            }
        }

        // ─── AutoPrompt ──────────────────────────────────────────────

        /// <summary>
        /// True when the most recent AutoPrompt run had no termbase enabled for AI, so the
        /// generated prompt's glossary was derived from the document rather than from
        /// approved terminology. Surfaced only in the saved prompt's YAML description –
        /// never in the prompt body, which goes verbatim to the translating AI.
        /// </summary>
        private bool _lastAutoPromptGlossaryDerived;

        private void OnGeneratePromptRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                if (_activeDocument == null)
                {
                    AddErrorMessage("No document open. Open a document in Trados first.");
                    return;
                }

                var aiSettings = _settings?.AiSettings;
                if (aiSettings == null)
                {
                    AddErrorMessage("AI settings not configured. Open Settings \u2192 AI Settings to configure a provider.");
                    return;
                }

                // Resolve provider/API key
                var provider = aiSettings.SelectedProvider ?? LlmModels.ProviderOpenAi;
                string apiKey;
                string baseUrl = null;
                string model = aiSettings.GetSelectedModel();

                if (provider == LlmModels.ProviderOllama)
                {
                    apiKey = "ollama";
                    baseUrl = aiSettings.OllamaEndpoint ?? "http://localhost:11434";
                }
                else if (provider == LlmModels.ProviderCustomOpenAi)
                {
                    var profile = aiSettings.GetActiveCustomProfile();
                    if (profile == null)
                    {
                        AddErrorMessage("No custom OpenAI profile configured.");
                        return;
                    }
                    apiKey = profile.ApiKey;
                    baseUrl = profile.Endpoint;
                    model = profile.Model;
                }
                else
                {
                    apiKey = LlmClient.ResolveApiKey(provider, aiSettings.ApiKeys);
                }

                if (string.IsNullOrEmpty(apiKey))
                {
                    AddErrorMessage($"No API key configured for {provider}. Open Settings \u2192 AI Settings to add one.");
                    return;
                }

                // Gather language pair
                var sourceLang = GetDocumentSourceLanguage();
                var targetLang = GetDocumentTargetLanguage();
                if (string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
                {
                    AddErrorMessage("Cannot determine source/target language from the document.");
                    return;
                }

                // Phase 1: Collect all source segments
                var docCtx = CollectDocumentContext();
                var sourceSegments = docCtx.Item1;
                if (sourceSegments == null || sourceSegments.Count == 0)
                {
                    AddErrorMessage("No segments found in the document.");
                    return;
                }

                // Phase 2: Document analysis (domain, tone)
                var analysis = DocumentAnalyzer.Analyze(sourceSegments);

                // Phase 3: Gather termbase terms (filtered by AI-disabled list)
                var allTerms = TermLensEditorViewPart.GetCurrentTermbaseTerms();
                var aiCfgA = aiSettings ?? new AiSettings();
                var termbaseTerms = allTerms.Where(t => aiCfgA.IsTermbaseAiEnabled(t.TermbaseId)).ToList();

                // Phase 3b: Filter terms to only those relevant to the document
                var totalTermCount = termbaseTerms.Count;
                termbaseTerms = PromptGenerator.FilterRelevantTerms(termbaseTerms, sourceSegments);

                // Diagnostic log: record exactly which termbase terms TermScan injected into
                // the prompt, so the injection is auditable after the fact (open the BridgeLog
                // file in %TEMP%). Logging must never interfere with prompt generation.
                try
                {
                    var injectedTerms = string.Join(", ", termbaseTerms
                        .Select(t => t.SourceTerm)
                        .Where(s => !string.IsNullOrWhiteSpace(s)));
                    var injectedFrom = string.Join(", ", termbaseTerms
                        .Select(t => t.TermbaseName ?? "Default")
                        .Distinct());
                    BridgeLog.Write(
                        $"[AutoPrompt] TermScan injected {termbaseTerms.Count} of {totalTermCount} " +
                        $"enabled term(s) from termbase(s): {injectedFrom}. Terms: {injectedTerms}");
                }
                catch { /* never break prompt generation on a logging failure */ }

                // The mirror of the large-termbase warning below: terms are loaded and
                // NONE of them may be sent to the AI. Note what this does NOT mean: the
                // generated prompt still gets a glossary. Every domain template lists
                // "PROJECT-SPECIFIC GLOSSARY (MANDATORY, LOCKED)" among the sections the
                // prompt MUST contain, universal rule 4 orders it to lock every recurring
                // term, and the whole document is in the meta-prompt – so the model fills
                // that table from the source text instead. The one countervailing line
                // (PromptGenerator.BuildTerminologySection's "should include an empty
                // section") is a lone "should" against three "MUST"s and loses.
                //
                // That is why this stop is worth showing (issue #58): the risk is not an
                // absent glossary but a model-authored one that is indistinguishable from
                // a termbase-backed one in the finished .md. Read and AI are separate
                // ticks, and AI is off by default, so this is the out-of-the-box state.
                if (allTerms.Count > 0 && totalTermCount == 0)
                {
                    var parentNoAi = _control.Value.FindForm();
                    var warnNoAi =
                        $"{allTerms.Count:N0} terms are loaded, but none of your termbases is enabled for AI, " +
                        "so none of your own terminology will be sent.\n\n" +
                        "AutoPrompt will still produce a PROJECT-SPECIFIC GLOSSARY section – but the model " +
                        "will derive it from the document text, not from your approved terms. Review it " +
                        "before relying on it.\n\n" +
                        "Read and AI are separate ticks. Enable a termbase for AI with the “AI” column in the " +
                        "termbase grid on Settings → Termbases; termbases are not sent to the AI by default.\n\n" +
                        "Generate the prompt anyway?";
                    var choiceNoAi = MessageBox.Show(
                        parentNoAi, warnNoAi, "No termbase enabled for AI – AutoPrompt",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (choiceNoAi != DialogResult.Yes)
                        return;
                }

                // AutoPrompt works best with a small, project-focused termbase. Warn based on
                // the SIZE of the termbase(s) enabled for AI (totalTermCount, before TermScan
                // filtering) – not on how many terms survive filtering. A large or general-
                // purpose termbase, even when filtered, injects many incidental whole-word
                // matches (common words that merely appear in the document) and is the wrong
                // tool for AutoPrompt. Threshold is deliberately low: a useful AutoPrompt
                // glossary is typically a few dozen carefully chosen terms, not hundreds.
                const int LargeTermbaseWarningThreshold = 50;
                if (totalTermCount > LargeTermbaseWarningThreshold)
                {
                    var parent = _control.Value.FindForm();
                    var warn =
                        $"The termbase(s) you have enabled for AI contain {totalTermCount:N0} terms.\n\n" +
                        "AutoPrompt works best with a small, project-focused termbase – typically " +
                        "a few dozen carefully chosen terms. Large or general-purpose termbases inject " +
                        "many incidental matches (common words that merely appear in the document), " +
                        "which crowd the prompt with terms that are not relevant to this project.\n\n" +
                        "Tip: on Settings → Termbases, untick the “AI” column for large or " +
                        "general termbases and enable only a compact, project-specific one.\n\n" +
                        "Generate the prompt anyway?";
                    var choice = MessageBox.Show(
                        parent, warn, "Large termbase – AutoPrompt",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (choice != DialogResult.Yes)
                        return;
                }

                // Phase 3c: AI context detection + optional steering. Ask the model
                // to read a sample of the source and classify the domain + describe
                // the text type, then let the translator confirm or correct it and
                // add a short briefing. This replaces the keyword DocumentAnalyzer as
                // the authoritative domain source (its stats are still used), and
                // mirrors the Supervertaler Workbench AutoPrompt flow.
                string detectedDomain = analysis.PrimaryDomain;   // keyword fallback
                string contextDescription = "";
                try
                {
                    var sample = DocumentContextClassifier.BuildSample(sourceSegments);
                    if (!string.IsNullOrEmpty(sample))
                    {
                        string classifyResp = null;
                        using (var classifyClient = new LlmClient(provider, model, apiKey, baseUrl))
                        using (var busy = new Controls.AutoPromptBusyForm(
                            () => classifyClient.SendPromptAsync(
                                DocumentContextClassifier.BuildUserPrompt(sample),
                                DocumentContextClassifier.SystemPrompt,
                                maxTokens: 300, suppressLog: true)))
                        {
                            busy.ShowDialog(_control.Value.FindForm());
                            classifyResp = busy.Result;
                        }
                        DocumentContextClassifier.Parse(classifyResp, out var aiDomain, out var aiDesc);
                        if (!string.IsNullOrEmpty(aiDomain))
                        {
                            detectedDomain = aiDomain;
                            contextDescription = aiDesc;
                        }
                        BridgeLog.Write(
                            $"[AutoPrompt] AI-detected domain: {detectedDomain}" +
                            (string.IsNullOrEmpty(contextDescription) ? "" : $" ({contextDescription})"));
                    }
                }
                catch (Exception ex)
                {
                    // Classification is best-effort; on any failure fall back to the
                    // keyword domain and continue.
                    BridgeLog.Write($"[AutoPrompt] Context classification failed: {ex.Message} – using keyword domain '{detectedDomain}'.");
                }

                // Confirm-context dialog: let the user override the domain / add a briefing.
                string userContextHint = "";
                using (var ctxDlg = new Controls.AutoPromptContextDialog(detectedDomain, contextDescription))
                {
                    if (ctxDlg.ShowDialog(_control.Value.FindForm()) != DialogResult.OK)
                        return;
                    detectedDomain = ctxDlg.SelectedDomain;
                    userContextHint = ctxDlg.ContextHint;
                }

                // Analysis summary for the meta-prompt: the AI's context description
                // (authoritative text-type read) plus the factual document stats
                // DocumentAnalyzer still provides. No keyword tone line — the AI's
                // description now carries the tone/register signal.
                string analysisSummary = string.IsNullOrEmpty(contextDescription)
                    ? $"{analysis.SegmentCount:N0} segments | {analysis.WordCount:N0} words"
                    : $"Context: {contextDescription} | {analysis.SegmentCount:N0} segments | {analysis.WordCount:N0} words";

                // Phase 4: Gather TM reference pairs from translated segments
                // Respects the "Include TM matches" toggle in AI Settings
                var includeTm = aiSettings.IncludeTmMatches;
                var tmPairs = includeTm ? CollectTmReferencePairs() : new List<TmMatch>();

                // Phase 4b: SuperMemory KB context (if enabled)
                string kbContext = null;
                if (aiSettings.IncludeSuperMemoryContext && aiSettings.IncludeSuperMemoryInAutoPrompt)
                {
                    var projectName = TermLensEditorViewPart.GetCurrentProjectName();
                    kbContext = LoadKbContextForPrompt(projectName, sourceLang, targetLang)?.Trim();
                }

                // Phase 5: Build meta-prompt
                var ctx = new PromptGenerationContext
                {
                    SourceLang = sourceLang,
                    TargetLang = targetLang,
                    DetectedDomain = detectedDomain,
                    AnalysisSummary = analysisSummary,
                    SegmentCount = sourceSegments.Count,
                    SourceSegments = sourceSegments,
                    TermbaseTerms = termbaseTerms,
                    TotalTermCount = totalTermCount,
                    TmPairs = tmPairs,
                    KbContext = kbContext,
                    UserContextHint = userContextHint
                };

                // Record how this run's glossary will be sourced, so the saved prompt can
                // carry that provenance in its YAML `description` (library panel and
                // QuickLauncher tooltip only). It deliberately does NOT go into the prompt
                // body: that is shipped verbatim to the translating AI, where a caveat
                // beside a LOCKED glossary would undermine it. See BuildTerminologySection.
                _lastAutoPromptGlossaryDerived = termbaseTerms.Count == 0;

                var metaPrompt = PromptGenerator.BuildMetaPrompt(ctx);
                var displayText = PromptGenerator.BuildDisplayMessage(ctx);

                // Phase 6: Send via chat (switches to AI Assistant panel)
                // Use 32768 tokens for prompt generation – comprehensive prompts with
                // large glossaries and TM pairs can exceed 16K tokens.
                // showAsStatus: true → display as assistant-styled (gray) bubble since the
                // user clicked a button, not typed this message themselves
                _control.Value.SubmitMessage(metaPrompt, displayText, maxTokens: 32768,
                    showAsStatus: true);
            });
        }

        /// <summary>
        /// Collects source/target pairs from human-confirmed segments to use as
        /// <summary>
        /// Bridge delegate for GET /v1/prompt-context (MCP get_prompt_context).
        /// Hands an external AI the document context AutoPrompt reasons over –
        /// source text, relevant termbase terms, TM pairs, the keyword domain, and
        /// the current Default Translation Prompt – so it can act as the user's
        /// prompt engineer, with no LLM calls made by the plugin. Marshals to the
        /// UI thread. maxSegmentsOverride: -1 = use the AI Settings default,
        /// 0 = whole document, &gt;0 = cap.
        /// </summary>
        private BridgePromptContextResponse BuildPromptContext(int maxSegmentsOverride)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgePromptContextResponse { Ok = false, Error = "ai assistant disposed" };
            if (ctrl.InvokeRequired)
                return (BridgePromptContextResponse)ctrl.Invoke(new Func<BridgePromptContextResponse>(() => BuildPromptContext(maxSegmentsOverride)));
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies – marshal via the UI thread we
            // captured at startup instead. See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BuildPromptContext(maxSegmentsOverride));

            if (_activeDocument == null)
                return new BridgePromptContextResponse { Ok = false, Error = "no document is open in the Trados editor" };

            try
            {
                var aiSettings = _settings?.AiSettings ?? new AiSettings();

                // Full source (CollectDocumentContext does not cap).
                var allSource = CollectDocumentContext().Item1 ?? new List<string>();
                var total = allSource.Count;

                // Cap: override (-1 = use setting), 0 = whole document, >0 = cap.
                int cap = maxSegmentsOverride >= 0 ? maxSegmentsOverride : aiSettings.PromptContextMaxSegments;
                bool truncated = cap > 0 && total > cap;
                var included = truncated ? allSource.Take(cap).ToList() : allSource;

                var analysis = DocumentAnalyzer.Analyze(allSource);

                // Relevant termbase terms (AI-enabled, filtered to the document).
                var terms = new List<BridgeTextPair>();
                try
                {
                    var all = TermLensEditorViewPart.GetCurrentTermbaseTerms() ?? new List<TermEntry>();
                    var enabled = all.Where(t => aiSettings.IsTermbaseAiEnabled(t.TermbaseId)).ToList();
                    foreach (var t in PromptGenerator.FilterRelevantTerms(enabled, allSource))
                        if (!string.IsNullOrWhiteSpace(t.SourceTerm))
                            terms.Add(new BridgeTextPair { Source = t.SourceTerm, Target = t.TargetTerm });
                }
                catch { }

                // A few representative confirmed TM pairs (respects the setting).
                var tmPairs = new List<BridgeTextPair>();
                try
                {
                    if (aiSettings.IncludeTmMatches)
                        foreach (var m in CollectTmReferencePairs().Take(20))
                            tmPairs.Add(new BridgeTextPair { Source = m.SourceText, Target = m.TargetText });
                }
                catch { }

                // The current Default Translation Prompt as a baseline to refine.
                string defaultPrompt = null;
                try
                {
                    var def = new PromptLibrary().GetAllPrompts().FirstOrDefault(p =>
                        string.Equals(p.Name, "Default Translation Prompt", StringComparison.OrdinalIgnoreCase));
                    defaultPrompt = def?.Content;
                }
                catch { }

                return new BridgePromptContextResponse
                {
                    Ok = true,
                    SourceLang = GetDocumentSourceLanguage(),
                    TargetLang = GetDocumentTargetLanguage(),
                    SegmentCount = total,
                    ReturnedSegments = included.Count,
                    WordCount = analysis?.WordCount ?? 0,
                    Domain = analysis?.PrimaryDomain,
                    Truncated = truncated,
                    SourceText = string.Join("\n", included),
                    Terms = terms.Count > 0 ? terms : null,
                    TmPairs = tmPairs.Count > 0 ? tmPairs : null,
                    CurrentDefaultPrompt = defaultPrompt,
                    Note = truncated
                        ? $"Showing the first {included.Count} of {total} segments (capped by the AI Settings prompt-context limit). Pass maxSegments=0 for the whole document."
                        : null
                };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] BuildPromptContext threw: {ex.Message}");
                return new BridgePromptContextResponse { Ok = false, Error = "prompt-context error: " + ex.Message };
            }
        }

        /// <summary>
        /// TM reference pairs for the prompt generator. Only includes segments that
        /// are Translated, ApprovedTranslation, or ApprovedSignOff – i.e., segments
        /// a translator has explicitly confirmed. Unconfirmed AI-generated translations
        /// are excluded to avoid feeding unverified output back as "correct" references.
        /// Samples up to 50 diverse pairs, spread evenly across the document.
        /// </summary>
        private List<TmMatch> CollectTmReferencePairs()
        {
            var pairs = new List<TmMatch>();
            if (_activeDocument == null) return pairs;

            try
            {
                // First pass: collect all confirmed translated segments
                var candidates = new List<TmMatch>();
                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    // #97: reference text, read not reproduced - plain on both sides.
                    var sourceText = pair.Source != null ? SegmentTagHandler.GetFinalText(pair.Source) : "";
                    var targetText = pair.Target != null
                        ? SegmentTagHandler.GetFinalText(pair.Target) : "";

                    // Only include segments that have a non-empty translation
                    if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(targetText))
                        continue;

                    // Skip very short segments (headers, numbers)
                    if (sourceText.Length < 20) continue;

                    // Only include human-confirmed segments – not unconfirmed AI output
                    var confirmLevel = pair.Properties?.ConfirmationLevel
                        ?? Sdl.Core.Globalization.ConfirmationLevel.Unspecified;
                    if (confirmLevel < Sdl.Core.Globalization.ConfirmationLevel.Translated)
                        continue;

                    candidates.Add(new TmMatch
                    {
                        SourceText = sourceText,
                        TargetText = targetText,
                        MatchPercentage = 100
                    });
                }

                // Second pass: sample evenly across the document for diversity
                if (candidates.Count <= 50)
                {
                    pairs = candidates;
                }
                else
                {
                    var step = (double)candidates.Count / 50;
                    for (int i = 0; i < 50; i++)
                    {
                        var idx = (int)(i * step);
                        if (idx < candidates.Count)
                            pairs.Add(candidates[idx]);
                    }
                }
            }
            catch (Exception)
            {
                // Document may not be accessible
            }

            return pairs;
        }

        private void OnSaveAsPromptRequested(object sender, string promptContent)
        {
            SafeInvoke(() =>
            {
                if (string.IsNullOrWhiteSpace(promptContent))
                    return;

                // Try to extract the prompt from delimiters (in case the full AI response is passed)
                var extracted = PromptGenerator.ParseGeneratedPrompt(promptContent);
                var content = extracted ?? promptContent;

                // Default name = project name, with version number if it already exists
                var defaultName = GetProjectName() ?? "Custom Translation Prompt";
                var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allPrompts = _promptLibrary?.GetAllPrompts();
                if (allPrompts != null)
                    foreach (var p in allPrompts)
                        existingNames.Add(p.Name);

                if (existingNames.Contains(defaultName))
                {
                    int version = 2;
                    while (existingNames.Contains(defaultName + " v" + version))
                        version++;
                    defaultName = defaultName + " v" + version;
                }

                // Ask user for a name
                using (var dlg = new SavePromptDialog(defaultName))
                {
                    if (dlg.ShowDialog(_control.Value.FindForm()) != DialogResult.OK)
                        return;

                    var name = dlg.PromptName;
                    if (string.IsNullOrWhiteSpace(name))
                        return;

                    // Provenance lives here, in YAML frontmatter, not in the prompt body:
                    // PromptLibrary parses `description` into a field shown in the library
                    // panel and the QuickLauncher tooltip, so it reaches the translator
                    // without ever being sent to the translating AI.
                    var template = new PromptTemplate
                    {
                        Name = name,
                        Category = "Translate",
                        Content = content,
                        Description = _lastAutoPromptGlossaryDerived
                            ? "Generated by AutoPrompt – glossary derived from the document " +
                              "(no termbase was enabled for AI)"
                            : "Generated by AutoPrompt"
                    };

                    _promptLibrary.SavePrompt(template);
                    PopulateBatchPromptDropdown();

                    // Confirmation in chat
                    var confirmMsg = new ChatMessage
                    {
                        Role = ChatRole.Assistant,
                        Content = $"Prompt saved as **\"{name}\"** in the Translate category. " +
                                  "You can select it from the Prompt dropdown on the Batch Operations tab."
                    };
                    _chatHistory.Add(confirmMsg);
                    _control.Value.AddMessage(confirmMsg);
                    SaveChatHistory();
                }
            });
        }

        private void OnSaveToMemoryBank(object sender, string assistantContent)
        {
            SafeInvoke(() =>
            {
                if (string.IsNullOrWhiteSpace(assistantContent))
                    return;

                var vaultDir = ActiveMemoryBankDir;
                var bankName = ActiveMemoryBankName;

                if (!Directory.Exists(vaultDir))
                {
                    ShowSuperMemoryMessage(
                        $"Memory bank **{bankName}** does not exist yet.\n\n" +
                        $"Expected location:\n`{vaultDir}`");
                    return;
                }

                // Find the preceding user message in chat history
                string userQuestion = null;
                for (int i = _chatHistory.Count - 1; i >= 0; i--)
                {
                    if (_chatHistory[i].Role == ChatRole.User)
                    {
                        userQuestion = _chatHistory[i].Content;
                        break;
                    }
                }

                // Write inbox note
                var inboxDir = Path.Combine(vaultDir, MemoryBankReader.ReferenceFolder);
                Directory.CreateDirectory(inboxDir);

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var fileName = $"chat-save-{stamp}.md";
                var filePath = Path.Combine(inboxDir, fileName);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# Chat – saved to memory bank");
                sb.AppendLine($"*Saved on {DateTime.Now:yyyy-MM-dd HH:mm} from Supervertaler*");
                sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(userQuestion))
                {
                    sb.AppendLine("## Question");
                    sb.AppendLine();
                    sb.AppendLine(userQuestion);
                    sb.AppendLine();
                }

                sb.AppendLine("## Answer");
                sb.AppendLine();
                sb.AppendLine(assistantContent);

                // Normalise to CRLF before writing. StringBuilder.AppendLine
                // emits CRLF, but the content it wraps arrives with bare LF -
                // models emit LF, and so does any string we build with "\n" -
                // so the file ended up mixed, and Markdown editors announce it.
                // These are new files, so picking one convention is enough;
                // CRLF matches what the rest of the bank uses. (Editing an
                // EXISTING bank file is the opposite problem - see
                // BankFileEditorDialog, which detects and restores whatever
                // that file already had.)
                var noteText = sb.ToString()
                    .Replace("\r\n", "\n")
                    .Replace("\r", "\n")
                    .Replace("\n", "\r\n");
                File.WriteAllText(filePath, noteText, new System.Text.UTF8Encoding(false));

                // Confirmation in chat.
                //
                // Says the folder, not just the bank, and says what the folder
                // does. This used to read "run Process Inbox to compile it into
                // the knowledge base", which was wrong twice: Process Inbox is
                // not implemented in this plugin (the toolbar event was dead and
                // was removed), and reference/ is never read into a prompt -
                // MemoryBankReader globs the bank root with TopDirectoryOnly.
                // A note the user believes is feeding the AI, that silently is
                // not, is the worst version of this.
                ShowSuperMemoryMessage(
                    $"Saved to memory bank **{bankName}**:\n" +
                    $"`reference/{fileName}`\n\n" +
                    "The `reference` folder is the audit trail - it is kept so a claim "
                    + "can be traced back to what it came from, and it is **not read into "
                    + "prompts**. Anything you want the AI to use goes in a `.md` at the "
                    + "bank root - `brief.md`, `terminology.md`, `style.md`, or one of "
                    + "your own "
                    + "(Settings \u2192 Library).");

                RefreshSuperMemoryInboxCount();
            });
        }

        // ─── Prompt Library ─────────────────────────────────────────

        private void PopulateBatchPromptDropdown()
        {
            SafeInvoke(() =>
            {
                _promptLibrary?.Refresh();
                var prompts = _promptLibrary?.GetAllPrompts();

                // Use per-project active prompt if set, else global
                var selectedPath = _settings?.AiSettings?.SelectedPromptPath ?? "";
                string activePromptPath = null;
                var projectPath = TermLensEditorViewPart.GetCurrentProjectPath();
                if (!string.IsNullOrEmpty(projectPath))
                {
                    try
                    {
                        var ps = Settings.ProjectSettings.Load(projectPath);
                        if (ps != null && !string.IsNullOrEmpty(ps.ActivePromptPath))
                        {
                            activePromptPath = ps.ActivePromptPath;
                            selectedPath = activePromptPath;
                        }
                    }
                    catch { }
                }

                var mode = _control.Value.BatchTranslateControl.CurrentMode;
                var categoryFilter = mode == BatchMode.Proofread ? "Proofread" : "Translate";
                var projectName = TermLensEditorViewPart.GetCurrentProjectName();
                _control.Value.BatchTranslateControl.SetPrompts(
                    prompts, selectedPath, categoryFilter, projectName, activePromptPath);
            });
        }

        /// <summary>
        /// Static-event handler wired in Initialize. Runs whenever the user toggles
        /// the active prompt in the Prompt Manager, regardless of which code path
        /// opened the Settings dialog.
        /// </summary>
        private void OnActivePromptChangedGlobal(object sender, string newPath)
        {
            RefreshBatchPromptDropdownWithActive(newPath);
        }

        /// <summary>
        /// Live variant of <see cref="PopulateBatchPromptDropdown"/>: refreshes the
        /// Batch Translate dropdown using an in-memory active-prompt path (typically
        /// the pending value from the Prompt Manager while the Settings dialog is
        /// still open). The change is NOT persisted here – the normal on-close
        /// refresh reads from disk, so a Cancel naturally snaps back.
        /// </summary>
        private void RefreshBatchPromptDropdownWithActive(string activePath)
        {
            SafeInvoke(() =>
            {
                try
                {
                    // Use the existing cache – the prompt library on disk hasn't
                    // changed (the user is just toggling active in memory), so a
                    // rescan is unnecessary and would slow down each right-click.
                    var prompts = _promptLibrary?.GetAllPrompts();
                    if (prompts == null) return;
                    if (_control?.Value?.BatchTranslateControl == null) return;

                    var normalisedActive = string.IsNullOrEmpty(activePath) ? null : activePath;
                    var selectedPath = normalisedActive ?? (_settings?.AiSettings?.SelectedPromptPath ?? "");

                    var mode = _control.Value.BatchTranslateControl.CurrentMode;
                    var categoryFilter = mode == BatchMode.Proofread ? "Proofread" : "Translate";
                    var projectName = TermLensEditorViewPart.GetCurrentProjectName();

                    _control.Value.BatchTranslateControl.SetPrompts(
                        prompts, selectedPath, categoryFilter, projectName, normalisedActive);
                }
                catch
                {
                    // Swallow – a stale settings dialog or disposed control shouldn't
                    // surface an error to the user for a UI-refresh helper.
                }
            });
        }

        /// <summary>
        /// Resolves the custom prompt content for the currently selected prompt.
        /// Applies variable substitution for source/target language.
        /// </summary>
        private string ResolveCustomPromptContent(string sourceLang, string targetLang)
        {
            var selectedPath = _settings?.AiSettings?.SelectedPromptPath;
            if (string.IsNullOrEmpty(selectedPath) || _promptLibrary == null)
                return null;

            // Marker-tolerant (#100): the stored path may predate a rename.
            var prompt = PromptPaths.Find(_promptLibrary, selectedPath);
            if (prompt == null || string.IsNullOrWhiteSpace(prompt.Content))
                return null;

            return PromptLibrary.ApplyVariables(prompt.Content, sourceLang, targetLang);
        }

        // ─── SuperMemory ─────────────────────────────────────────────

        /// <summary>
        /// Loads SuperMemory KB context for the current project/document.
        /// Returns the formatted prompt section, or null if KB is empty/unavailable.
        /// </summary>
        private string LoadKbContextForPrompt(string projectName, string sourceLang, string targetLang, string queryText = null)
        {
            try
            {
                // Check if memory-bank context is enabled in settings
                if (_settings?.AiSettings?.IncludeSuperMemoryContext == false)
                    return null;

                var reader = EnsureKbReader();
                if (reader == null || !reader.VaultExists) return null;

                var ctx = reader.LoadContext(
                    projectName, DetectDocumentDomain(), sourceLang, targetLang,
                    tokenBudget: 24000, queryText: queryText);

                if (ctx == null) return null;

                return MemoryBankReader.FormatForPrompt(ctx);
            }
            catch
            {
                return null; // KB is optional – never block translation
            }
        }

        /// <summary>
        /// Returns the reader for the active bank, re-creating it if the bank
        /// changed (the SuperMemory toolbar dropdown can swap banks without a
        /// restart). First run lands here too. UI thread only – the shared
        /// <see cref="_kbReader"/> field is not synchronised.
        /// </summary>
        private MemoryBankReader EnsureKbReader()
        {
            var bankName = ActiveMemoryBankName;
            if (_kbReader == null || !string.Equals(_kbReaderBankName, bankName, StringComparison.Ordinal))
            {
                _kbReader = new MemoryBankReader(ActiveMemoryBankDir);
                _kbReaderBankName = bankName;
            }
            return _kbReader;
        }

        /// <summary>
        /// Best-effort domain of the open document, used to pick the right
        /// 03_DOMAINS article. Returns null when nothing is open or analysis
        /// fails – the bank still loads, just without domain gating.
        /// </summary>
        private string DetectDocumentDomain()
        {
            try
            {
                if (_activeDocument == null) return null;

                var docCtx = CollectDocumentContext();
                if (docCtx.Item1 == null || docCtx.Item1.Count == 0) return null;

                return DocumentAnalyzer.Analyze(docCtx.Item1)?.PrimaryDomain;
            }
            catch
            {
                return null; // domain detection is best-effort
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  SuperMemory over the bridge (issues #51, #22)
        //
        //  The bank has fed the plugin's own prompts since day one, but was
        //  invisible to anything driving Supervertaler over MCP. These three
        //  methods put the same knowledge on the bridge, so an external AI
        //  client sees what the in-Trados chat sees.
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Bridge: the memory-bank context for the current project, formatted
        /// exactly as it would be injected into a translation prompt, plus the
        /// article paths that produced it so the AI can cite its sources.
        /// </summary>
        private BridgeSuperMemoryContextResponse BridgeGetSuperMemoryContext(BridgeSuperMemoryQuery query)
        {
            // Domain detection reads the open document, so this has to run on
            // the UI thread like the other bridge snapshot builders.
            var ctrl = _control?.Value;
            if (ctrl != null && !ctrl.IsDisposed && ctrl.InvokeRequired)
            {
                return (BridgeSuperMemoryContextResponse)ctrl.Invoke(
                    new Func<BridgeSuperMemoryContextResponse>(() => BridgeGetSuperMemoryContext(query)));
            }
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeGetSuperMemoryContext(query));

            if (_settings?.AiSettings?.IncludeSuperMemoryContext == false)
            {
                return new BridgeSuperMemoryContextResponse
                {
                    Available = false,
                    Note = "SuperMemory context is switched off in AI Settings."
                };
            }

            // An explicitly requested bank wins over the active one. Before
            // this existed the argument was simply not a parameter, so a caller
            // naming a bank got the ACTIVE bank's contents back — with a "bank"
            // field in the response that reads exactly like confirmation. That
            // is how another filing's locked terminology reaches a prompt with
            // nothing to signal it.
            string requestedBank = query != null ? (query.Bank ?? "").Trim() : "";
            MemoryBankReader reader;
            string bankName;

            if (requestedBank.Length > 0)
            {
                var known = Settings.UserDataPath.ListMemoryBanks() ?? new List<string>();
                var match = known.FirstOrDefault(b =>
                    string.Equals(b, requestedBank, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    // Refused, not substituted: a wrong answer here is invisible.
                    return new BridgeSuperMemoryContextResponse
                    {
                        Available = false,
                        Bank = ActiveMemoryBankName,
                        Note = "No memory bank named '" + requestedBank + "'. Available: "
                             + (known.Count > 0 ? string.Join(", ", known) : "(none)")
                             + ". The active bank is '" + ActiveMemoryBankName
                             + "'; omit 'bank' to read that one."
                    };
                }
                bankName = match;
                reader = new MemoryBankReader(Settings.UserDataPath.GetMemoryBankDir(match));
            }
            else
            {
                reader = EnsureKbReader();
                bankName = ActiveMemoryBankName;
            }

            if (reader == null || !reader.VaultExists)
            {
                return new BridgeSuperMemoryContextResponse
                {
                    Available = false,
                    Bank = bankName,
                    Note = "No memory bank found at "
                         + (requestedBank.Length > 0
                                ? Settings.UserDataPath.GetMemoryBankDir(bankName)
                                : ActiveMemoryBankDir)
                };
            }

            // Deliberately smaller than the 24k the in-Trados chat uses. There
            // the block is injected into a system prompt: it happens once and
            // is thrown away. Here it is a tool RESULT, so it lands in the
            // client's conversation and is re-sent on every following turn –
            // a 24k-token answer ate a large slice of the window on the first
            // call. Callers that genuinely want the lot can pass tokenBudget.
            const int mcpDefaultTokenBudget = 6000;
            var budget = query != null && query.TokenBudget > 0
                ? query.TokenBudget
                : mcpDefaultTokenBudget;
            var domain = !string.IsNullOrWhiteSpace(query?.Domain) ? query.Domain : DetectDocumentDomain();

            var ctx = reader.LoadContext(
                GetProjectName(),
                domain,
                GetDocumentSourceLanguage(),
                GetDocumentTargetLanguage(),
                tokenBudget: budget,
                manualClientProfile: ResolveClientProfileFileName(reader, query?.Client),
                queryText: query?.Query);

            if (ctx == null)
            {
                return new BridgeSuperMemoryContextResponse
                {
                    Available = false,
                    Bank = bankName,
                    Domain = domain,
                    Note = "The memory bank has no content matching this project, domain or language pair."
                };
            }

            var sources = new List<string>();
            if (!string.IsNullOrEmpty(ctx.ClientProfilePath)) sources.Add(ctx.ClientProfilePath);
            if (!string.IsNullOrEmpty(ctx.DomainArticlePath)) sources.Add(ctx.DomainArticlePath);
            if (!string.IsNullOrEmpty(ctx.StyleGuidePath)) sources.Add(ctx.StyleGuidePath);
            if (ctx.TerminologyPaths != null) sources.AddRange(ctx.TerminologyPaths);
            if (ctx.ExtraPaths != null) sources.AddRange(ctx.ExtraPaths);

            // Say what did not fit. The default budget is deliberately small
            // (see above), so trimming is normal rather than exceptional — which
            // is exactly why it has to be reported: a caller that silently
            // receives two of a bank's three articles will translate against
            // rules it was never shown and neither side will know.
            var trimmed = ctx.TrimmedPaths != null && ctx.TrimmedPaths.Count > 0
                ? new List<string>(ctx.TrimmedPaths)
                : null;

            return new BridgeSuperMemoryContextResponse
            {
                Available = true,
                Bank = bankName,
                Client = ctx.ClientName,
                Domain = ctx.DomainName ?? domain,
                DetectionMethod = ctx.DetectionMethod,
                Context = MemoryBankReader.FormatForPrompt(ctx),
                Sources = sources,
                Trimmed = trimmed,
                Note = AppendTrimNote(BuildClientDetectionNote(reader, ctx), ctx, budget)
            };
        }

        /// <summary>
        /// Adds a plain-English sentence about what the token budget cut, so the
        /// omission is legible to a model reading the note as well as to code
        /// reading the <c>trimmed</c> array. Says how to get the rest, because a
        /// warning a caller cannot act on is just noise.
        /// </summary>
        private static string AppendTrimNote(string note, KbContext ctx, int budget)
        {
            if (ctx == null) return note;

            var parts = new List<string>();
            if (ctx.TrimmedPaths != null && ctx.TrimmedPaths.Count > 0)
            {
                parts.Add(ctx.TrimmedPaths.Count == 1
                    ? "1 article did not fit the " + budget + "-token budget and was left out: "
                        + ctx.TrimmedPaths[0]
                    : ctx.TrimmedPaths.Count + " articles did not fit the " + budget
                        + "-token budget and were left out: " + string.Join(", ", ctx.TrimmedPaths));
            }
            if (ctx.ClientProfileTruncated)
                parts.Add("The client brief was truncated to fit.");

            if (parts.Count == 0) return note;

            parts.Add("Call again with a larger tokenBudget to get the rest.");
            var trimNote = string.Join(" ", parts);

            return string.IsNullOrWhiteSpace(note) ? trimNote : note.TrimEnd() + " " + trimNote;
        }

        /// <summary>
        /// Turns a loosely-typed client name ("GE", "ge healthcare") into the
        /// exact 01_CLIENTS filename <see cref="MemoryBankReader.LoadContext"/>
        /// expects. Returns null when nothing matches, which simply leaves
        /// auto-detection to do its job.
        /// </summary>
        private static string ResolveClientProfileFileName(MemoryBankReader reader, string wanted)
        {
            if (reader == null || string.IsNullOrWhiteSpace(wanted)) return null;

            var clients = reader.GetIndexSnapshot()
                .Where(e => string.Equals(e.Folder, "01_CLIENTS", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Exact filename first, then the frontmatter client name, then a
            // loose contains-match in either direction so "GE" finds
            // "GE HealthCare.md" and "GE HealthCare" finds "GE.md".
            var exact = clients.FirstOrDefault(e =>
                string.Equals(Path.GetFileNameWithoutExtension(e.FileName), wanted, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact.FileName;

            var byFrontmatter = clients.FirstOrDefault(e =>
                string.Equals(e.GetFrontmatter("client"), wanted, StringComparison.OrdinalIgnoreCase));
            if (byFrontmatter != null) return byFrontmatter.FileName;

            var loose = clients.FirstOrDefault(e =>
            {
                var name = Path.GetFileNameWithoutExtension(e.FileName) ?? "";
                return name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0
                    || wanted.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
            });
            return loose?.FileName;
        }

        /// <summary>
        /// Explains an unresolved client profile instead of silently omitting
        /// it. Without this the AI receives client: null with no indication
        /// that a whole category of knowledge was left out, and cannot know to
        /// ask. Lists the available profiles so it can offer the user a choice.
        /// </summary>
        private static string BuildClientDetectionNote(MemoryBankReader reader, KbContext ctx)
        {
            if (ctx == null || !string.IsNullOrEmpty(ctx.ClientName)) return null;

            var names = reader.GetIndexSnapshot()
                .Where(e => string.Equals(e.Folder, "01_CLIENTS", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.GetFrontmatter("client") ?? Path.GetFileNameWithoutExtension(e.FileName))
                .Where(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith("_"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (names.Count == 0)
            {
                return "No client profile was loaded: this memory bank has no articles in 01_CLIENTS. "
                     + "Domain, style and terminology knowledge is unaffected.";
            }

            return "No client profile was loaded, because the Trados project name does not match any "
                 + "client in this memory bank. Domain, style and terminology knowledge is unaffected. "
                 + "If this job is for one of these clients, call this tool again with the client argument "
                 + "to load their profile: " + string.Join(", ", names) + ".";
        }

        /// <summary>
        /// Bridge: free-text search across the active bank – the "where did I
        /// write about X?" question, as opposed to the automatic per-segment
        /// retrieval above.
        /// </summary>
        private BridgeSuperMemorySearchResponse BridgeSearchSuperMemory(BridgeSuperMemorySearchQuery query)
        {
            var bankName = ActiveMemoryBankName;

            if (query == null || string.IsNullOrWhiteSpace(query.Query))
                return new BridgeSuperMemorySearchResponse { Available = false, Note = "missing 'q'" };

            // Deliberately NOT the shared _kbReader: searching reads every
            // article body, which is too slow to run on the UI thread, and the
            // cached reader is not safe to touch from the listener thread.
            var reader = new MemoryBankReader(ActiveMemoryBankDir);

            // The shared bank is injected into every prompt, so it has to be
            // searchable too. Without it, knowledge that lives only in
            // _shared/terminology.md answered "no matches" - which reads as
            // "you never wrote that down" while the model was being handed that
            // very text in its system prompt. Skipped when _shared IS the
            // active bank, so its hits are not returned twice.
            MemoryBankReader sharedReader = null;
            if (!UserDataPath.IsSharedBankName(bankName))
            {
                var candidate = new MemoryBankReader(
                    UserDataPath.GetMemoryBankDir(MemoryBankReader.SharedBankName));
                if (candidate.VaultExists) sharedReader = candidate;
            }

            if (!reader.VaultExists && sharedReader == null)
            {
                return new BridgeSuperMemorySearchResponse
                {
                    Available = false,
                    Bank = bankName,
                    Note = "No memory bank found at " + ActiveMemoryBankDir
                };
            }

            var limit = query.Limit > 0 ? query.Limit : 10;

            // Each reader returns its own best `limit`, so merging and taking
            // `limit` still yields the true overall top-N.
            var found = new List<KbSearchHit>();
            var banksSearched = new List<string>();
            if (reader.VaultExists)
            {
                found.AddRange(reader.Search(query.Query, limit));
                banksSearched.Add(reader.BankName ?? bankName);
            }
            if (sharedReader != null)
            {
                found.AddRange(sharedReader.Search(query.Query, limit));
                banksSearched.Add(MemoryBankReader.SharedBankName);
            }

            // Ties break towards the active bank: where the two layers disagree
            // the active one overrides the shared defaults, so it should also be
            // the one the reader sees first.
            var hits = found
                .OrderByDescending(h => h.Score)
                .ThenBy(h => string.Equals(h.Bank, MemoryBankReader.SharedBankName,
                                           StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(h => h.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();

            return new BridgeSuperMemorySearchResponse
            {
                Available = true,
                Bank = bankName,
                BanksSearched = banksSearched,
                Hits = hits.Select(h => new BridgeSuperMemorySearchHit
                {
                    Bank = h.Bank,
                    Path = h.RelativePath,
                    Folder = h.Folder,
                    Title = h.Title,
                    Score = h.Score,
                    Snippet = h.Snippet
                }).ToList(),
                Note = hits.Count == 0
                    ? "No matches in " + string.Join(" or ", banksSearched)
                      + ". The search covers each bank's brief.md, terminology.md and style.md; "
                      + "reference/ is source material and is deliberately not indexed."
                    : null
            };
        }

        /// <summary>
        /// Bridge: every memory bank on disk and which one is active, so an AI
        /// client can tell the user which knowledge it is actually working from.
        /// </summary>
        private BridgeSuperMemoryBanksResponse BridgeListSuperMemoryBanks()
        {
            var activeName = ActiveMemoryBankName;
            var names = UserDataPath.ListMemoryBanks() ?? new List<string>();

            var banks = new List<BridgeSuperMemoryBank>();
            var hasShared = false;
            foreach (var name in names)
            {
                var count = 0;
                try
                {
                    var bankReader = new MemoryBankReader(UserDataPath.GetMemoryBankDir(name));
                    if (bankReader.VaultExists) count = bankReader.GetIndexSnapshot().Count;
                }
                catch { /* a bank we cannot read still deserves a listing */ }

                var isShared = UserDataPath.IsSharedBankName(name);
                if (isShared) hasShared = true;

                banks.Add(new BridgeSuperMemoryBank
                {
                    Name = name,
                    // _shared sits in this list because it is a folder under the
                    // same root, but it is an overlay, not a sibling. Labelling
                    // the role stops a client reading its active:false as
                    // "this knowledge is not in play".
                    Role = isShared ? "shared" : "bank",
                    Active = string.Equals(name, activeName, StringComparison.OrdinalIgnoreCase),
                    AlwaysLoaded = isShared,
                    Articles = count
                });
            }

            return new BridgeSuperMemoryBanksResponse
            {
                Available = banks.Count > 0,
                Root = UserDataPath.MemoryBanksRoot,
                ActiveBank = activeName,
                Banks = banks,
                Note = banks.Count == 0
                    ? "No memory banks found under " + UserDataPath.MemoryBanksRoot
                    : (hasShared
                        ? "'" + MemoryBankReader.SharedBankName + "' is not one of the user's project banks: "
                          + "it holds their house defaults and is loaded on top of whichever bank is active, "
                          + "so its content reaches you even though it reports active:false. The active bank "
                          + "overrides it wherever the two disagree."
                        : null)
            };
        }

        // ══════════════════════════════════════════════════════════════
        //  Memory-bank dropdown: populate + live switching
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Rebuilds the Memory Bank dropdown in the SuperMemory toolbar from
        /// the current on-disk bank list, pre-selecting the active bank. Safe
        /// to call repeatedly – the toolbar suppresses its own change event
        /// while the combo is being repopulated, so no accidental switch fires.
        /// </summary>
        private void RefreshMemoryBankDropdown()
        {
            try
            {
                var banks = UserDataPath.ListMemoryBanks();
                var activeName = ActiveMemoryBankName;

                // Make sure the active bank is always visible in the list, even
                // if the on-disk directory hasn't been created yet (e.g. just
                // after a fresh install before the default bank's sub-folders
                // are written). This keeps the combo from looking empty on day one.
                if (!string.IsNullOrWhiteSpace(activeName) &&
                    !banks.Contains(activeName, StringComparer.Ordinal))
                {
                    banks.Insert(0, activeName);
                }

                _control.Value.SuperMemoryToolbar?.SetMemoryBanks(banks, activeName);
                RefreshLegacyBankNotice();
            }
            catch
            {
                // Non-critical – the rest of the AI Assistant still works
                // against the active bank via ActiveMemoryBankDir.
            }
        }

        /// <summary>
        /// Handles the user picking a different bank from the toolbar dropdown.
        /// Persists the new active bank, invalidates the cached <see cref="MemoryBankReader"/>,
        /// restarts the inbox watcher against the new bank, and drops a system
        /// banner into the chat so the user sees confirmation of the switch.
        /// </summary>
        /// <summary>
        /// Shows the toolbar's convert prompt when the active bank is still on
        /// the old seven-folder layout. Such a bank has none of the three files
        /// the reader looks for, so it contributes NOTHING to a prompt - and
        /// would do so without a word, which is the failure mode worth spending
        /// UI on.
        /// </summary>
        private void RefreshLegacyBankNotice()
        {
            try
            {
                var isLegacy = UserDataPath.IsLegacyBankLayout(ActiveMemoryBankDir);
                _control?.Value?.SuperMemoryToolbar?.SetLegacyBank(isLegacy);
            }
            catch { /* the notice is advisory; never break the panel over it */ }
        }

        /// <summary>
        /// Converts the active legacy bank in place, after telling the user
        /// exactly what will happen. Conversion is lossless - the old folders are
        /// moved to reference/_legacy, not deleted - so the confirmation can
        /// promise that honestly.
        /// </summary>
        private void OnConvertLegacyBank(object sender, EventArgs e)
        {
            try
            {
                var bankDir = ActiveMemoryBankDir;
                var bankName = ActiveMemoryBankName;

                if (!UserDataPath.IsLegacyBankLayout(bankDir))
                {
                    MessageBox.Show(
                        "This bank has already been converted.",
                        "SuperMemory", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    RefreshLegacyBankNotice();
                    return;
                }

                var answer = MessageBox.Show(
                    "Convert the memory bank '" + bankName + "' to the new layout?\n\n" +
                    "Right now this bank uses the old folder structure, which means it is " +
                    "NOT being read - the AI sees nothing from it.\n\n" +
                    "Converting folds its articles into brief.md, terminology.md and " +
                    "style.md. Nothing is deleted: the original folders are moved to " +
                    "reference\\_legacy so you can check the result.\n\n" +
                    "The conversion copies text across as-is. It does not tidy it up - " +
                    "you will want to read through the result and prune it, especially " +
                    "the terminology, which reads best as a table.",
                    "Convert memory bank",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);

                if (answer != DialogResult.OK) return;

                string error;
                int folded;
                if (!UserDataPath.TryConvertLegacyBank(bankDir, out error, out folded))
                {
                    MessageBox.Show(error ?? "The bank could not be converted.",
                        "SuperMemory", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // The cached reader was built when the bank had no readable
                // files; drop it so the next turn sees the converted content.
                _kbReader = null;
                RefreshLegacyBankNotice();

                MessageBox.Show(
                    "Converted '" + bankName + "': " + folded + " article(s) folded into the " +
                    "three files.\n\nThe originals are in reference\\_legacy. Open the bank " +
                    "and read through what came across before relying on it.",
                    "SuperMemory", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not convert the bank: " + ex.Message,
                    "SuperMemory", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// The open project's .sdlproj path, read from the active document itself.
        ///
        /// <para>NOT TermLensEditorViewPart.GetCurrentProjectPath() here. That
        /// returns a path tracked by the OTHER view part's own
        /// ActiveDocumentChanged handler, and handler order between two view
        /// parts on the same event is not guaranteed - so when this one runs
        /// first the tracked path is still the PREVIOUS project, and the bank
        /// lagged one project behind on every switch. The document knows its own
        /// project and cannot be stale.</para>
        /// </summary>
        private string CurrentProjectPathFromDocument()
        {
            try
            {
                var fbp = _activeDocument?.Project as Sdl.ProjectAutomation.FileBased.FileBasedProject;
                var path = fbp?.FilePath;
                if (!string.IsNullOrEmpty(path)) return path;
            }
            catch { }
            // No document open: fall back to the tracked path, which is right
            // whenever there is no switch in flight.
            try { return TermLensEditorViewPart.GetCurrentProjectPath(); }
            catch { return null; }
        }

        /// <summary>
        /// Record <paramref name="bankName"/> against the open Trados project.
        /// Silent when no project is open - there is nothing to key it to, and
        /// the global setting already holds it.
        /// </summary>
        private void RememberBankForProject(string bankName)
        {
            RecordBankAgainst(CurrentProjectPathFromDocument(), bankName, null);
        }

        /// <summary>
        /// Record <paramref name="bankName"/> against whichever project Studio
        /// currently has selected.
        ///
        /// <para>Static and internal because the active bank can be changed from
        /// THREE places - the SuperMemory toolbar dropdown, the Library tab's
        /// "Set as active", and creating a bank - and every one of them has to
        /// record, or the choice is silently forgotten the next time the project
        /// is opened. Only the dropdown did, which is why no project on the
        /// author's machine had a bank recorded a full day after the feature
        /// shipped.</para>
        /// </summary>
        internal static void RememberBankForCurrentProject(string bankName)
        {
            FileBasedProject project = null;
            try { project = SdlTradosStudio.Application?.GetController<ProjectsController>()?.CurrentProject; }
            catch { }

            string path = null;
            string name = null;
            try { path = project?.FilePath; } catch { }
            try { name = project?.GetProjectInfo()?.Name; } catch { }

            if (string.IsNullOrEmpty(path))
            {
                try { path = TermLensEditorViewPart.GetCurrentProjectPath(); } catch { }
            }
            RecordBankAgainst(path, bankName, name);
        }

        private static void RecordBankAgainst(string projectPath, string bankName, string projectName)
        {
            try
            {
                if (string.IsNullOrEmpty(projectPath)) return;

                var ps = Settings.ProjectSettings.Load(projectPath) ?? new Settings.ProjectSettings();
                ps.MemoryBankName = bankName ?? "";
                if (string.IsNullOrEmpty(ps.ProjectPath)) ps.ProjectPath = projectPath;
                if (string.IsNullOrEmpty(ps.ProjectName))
                {
                    if (string.IsNullOrEmpty(projectName))
                    {
                        try { projectName = TermLensEditorViewPart.GetCurrentProjectName(); }
                        catch { }
                    }
                    ps.ProjectName = projectName ?? "";
                }
                Settings.ProjectSettings.Save(projectPath, ps);
            }
            catch { /* a project we cannot record against still works this session */ }
        }

        /// <summary>
        /// Point SuperMemory at the bank this project uses, when the open project
        /// changes.
        ///
        /// <para>A project with no bank recorded CLEARS to none rather than
        /// inheriting the last one used. A bank feeds every prompt, so carrying
        /// the previous client's bank into a new job silently supplies the wrong
        /// terminology and style - and no bank is better than the wrong one.
        /// Either way it is announced in the chat rather than changing under
        /// you.</para>
        /// </summary>
        private void ApplyProjectMemoryBank()
        {
            try
            {
                var projectPath = CurrentProjectPathFromDocument();
                if (string.IsNullOrEmpty(projectPath)) return;
                if (string.Equals(projectPath, _bankProjectPath, StringComparison.OrdinalIgnoreCase))
                    return;                      // same project, nothing to do
                _bankProjectPath = projectPath;

                string wanted = null;
                try { wanted = Settings.ProjectSettings.Load(projectPath)?.MemoryBankName; }
                catch { }

                var current = _settings?.AiSettings?.ActiveMemoryBankName ?? "";
                var target = wanted ?? "";
                if (string.Equals(current, target, StringComparison.Ordinal)) return;

                SettingsService.Update(s =>
                {
                    if (s.AiSettings == null) s.AiSettings = new AiSettings();
                    s.AiSettings.ActiveMemoryBankName = target;
                });

                _kbReader = null;
                _kbReaderBankName = null;
                try { RefreshMemoryBankDropdown(); } catch { }

                var projectName = TermLensEditorViewPart.GetCurrentProjectName() ?? "this project";
                SafeInvoke(() => ShowSuperMemoryMessage(
                    target.Length > 0
                        ? "Switched to memory bank **" + target + "** for **" + projectName + "**."
                        : "**No memory bank** is set for **" + projectName + "**, so SuperMemory is "
                          + "contributing nothing to prompts. Pick one from the SuperMemory dropdown "
                          + "if this project should have one." + "\n\n*The previous project's bank is "
                          + "deliberately not carried over: it would feed another client's terminology "
                          + "into every request without saying so.*"));
            }
            catch { }
        }

        private void OnMemoryBankChanged(object sender, MemoryBankChangedEventArgs e)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.BankName)) return;

            var newName = e.BankName;
            var oldName = ActiveMemoryBankName;
            if (string.Equals(newName, oldName, StringComparison.Ordinal))
                return;

            // User-initiated action (dropdown selection) – re-engage
            // auto-scroll so the "Switched to memory bank X" confirmation
            // and any follow-up heal prompt chat messages land in view.
            _control.Value.ReengageAutoScroll();

            try
            {
                // 1. Persist the new active bank to settings
                // Under one lock, so the read-modify-write cannot interleave
                // with another writer — the shape of the defect this replaced.
                SettingsService.Update(s =>
                {
                    if (s.AiSettings == null) s.AiSettings = new AiSettings();
                    s.AiSettings.ActiveMemoryBankName = newName;
                });

                // Also against the PROJECT, so opening this job again picks the
                // same bank rather than whichever one was last used anywhere.
                RememberBankForProject(newName);

                // 2. Invalidate the cached reader – the next LoadKbContextForPrompt
                //    call will lazily recreate it against the new bank directory.
                _kbReader = null;
                _kbReaderBankName = null;

                // 3. Restart the inbox watcher against the new bank
                try { _inboxWatcher?.Dispose(); } catch { }
                _inboxWatcher = null;
                StartInboxWatcher();

                // 4. Refresh the inbox count display (now reading from the new bank)
                RefreshSuperMemoryInboxCount();

                // 5. User-visible confirmation in the chat history
                _control.Value.AddMessage(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = $"Switched to memory bank **{newName}**. The next chat turn will read from this bank."
                });

                // 6. Heal-on-activation: if the newly active bank is missing
                //    any canonical template files (06_TEMPLATES/compile.md,
                //    06_TEMPLATES/lint.md), offer to restore them from the
                //    bundled defaults. This catches banks created before the
                //    template bundling shipped, as well as any banks where the
                //    user deleted a critical template by mistake.
                CheckAndOfferTemplateHealing(newName);
            }
            catch (Exception ex)
            {
                AddErrorMessage($"Could not switch memory bank: {ex.Message}");
            }
        }

        /// <summary>
        /// Tracks which bank names have already been offered a template-heal
        /// prompt during the current Trados session, so that switching between
        /// two broken banks does not fire the dialog repeatedly. The set is
        /// cleared whenever the plugin is reloaded (i.e. when Trados restarts).
        /// </summary>
        private readonly HashSet<string> _healPromptsShownThisSession =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Inspects the active bank for missing canonical template files
        /// (compile.md, lint.md). If any are missing, shows a one-time
        /// confirmation dialog offering to restore them from the built-in
        /// defaults. Safe to call multiple times: subsequent calls for the
        /// same bank are no-ops once the user has either healed it or
        /// declined healing during the session.
        /// </summary>
        /// <param name="bankName">
        /// Name of the bank being activated. Used as the key for the
        /// per-session "already asked" tracker.
        /// </param>
        private void CheckAndOfferTemplateHealing(string bankName)
        {
            if (string.IsNullOrWhiteSpace(bankName)) return;

            try
            {
                var bankDir = UserDataPath.GetMemoryBankDir(bankName);
                if (!Directory.Exists(bankDir)) return;

                // 06_TEMPLATES held the AI prompts for Process Inbox and Health
                // Check. A bank on the new layout has neither, because converting
                // moves the old folders into reference/_legacy - so this fired
                // immediately after a successful conversion and offered to write
                // 06_TEMPLATES back into the bank that had just been cleaned up.
                // Accepting would have re-created exactly what the conversion
                // removed.
                if (!UserDataPath.IsLegacyBankLayout(bankDir)) return;

                var missing = UserDataPath.GetMissingCanonicalTemplates(bankDir);
                if (missing.Count == 0) return;

                // Only ask once per session per bank.
                if (_healPromptsShownThisSession.Contains(bankName)) return;
                _healPromptsShownThisSession.Add(bankName);

                var parent = _control.Value.FindForm();
                var missingList = string.Join(", ", missing);
                var message =
                    $"Memory bank \"{bankName}\" is missing the following template file(s) in 06_TEMPLATES:\n\n" +
                    $"    {missingList}\n\n" +
                    "These templates are the AI prompts that drive Process Inbox and Health Check. " +
                    "Without them, those features cannot run against this bank.\n\n" +
                    "Restore them from the built-in defaults now?";

                var result = MessageBox.Show(
                    parent,
                    message,
                    "Missing memory bank templates",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);

                if (result != DialogResult.Yes)
                {
                    _control.Value.AddMessage(new ChatMessage
                    {
                        Role = ChatRole.Assistant,
                        Content = $"Left memory bank **{bankName}** as-is. Process Inbox and Health Check will not work until the missing template files ({missingList}) are restored – switch away and back, or create a fresh bank, to see the restore prompt again."
                    });
                    return;
                }

                string writeError;
                var count = UserDataPath.WriteMemoryBankTemplates(bankDir, overwrite: false, out writeError);
                if (count > 0)
                {
                    _control.Value.AddMessage(new ChatMessage
                    {
                        Role = ChatRole.Assistant,
                        Content = $"Restored {count} template file(s) to memory bank **{bankName}**. Process Inbox and Health Check will now work against this bank."
                    });
                }
                else if (!string.IsNullOrEmpty(writeError))
                {
                    AddErrorMessage($"Could not restore templates: {writeError}");
                }
                else
                {
                    AddErrorMessage("Could not restore templates: no files were written. The bundled template resources may be missing from this build of the plugin.");
                }
            }
            catch (Exception ex)
            {
                AddErrorMessage($"Could not check memory bank templates: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the "+ New memory bank…" sentinel selection from the toolbar
        /// dropdown. Prompts the user for a name, sanitises it, creates the
        /// bank on disk (with the full <see cref="UserDataPath.SkeletonFolders"/>
        /// layout), refreshes the dropdown with the new bank visible, and
        /// switches to it by reusing <see cref="OnMemoryBankChanged"/>.
        /// </summary>
        private void OnNewMemoryBankRequested(object sender, EventArgs e)
        {
            // User-initiated action (+ New memory bank sentinel) – re-engage
            // auto-scroll so the "Created memory bank X" confirmation lands
            // in view after the dialog closes.
            _control.Value.ReengageAutoScroll();

            var parent = _control.Value.FindForm();
            string rawName;

            // Retry on validation errors (empty / invalid / already exists)
            // until the user either gives us something usable or cancels.
            while (true)
            {
                rawName = PromptForNewBankName(parent);
                if (rawName == null)
                {
                    // User cancelled. Dropdown has already been reverted to
                    // the previously active bank by the toolbar, so there is
                    // nothing else to undo here.
                    return;
                }

                string sanitised;
                string error;
                if (UserDataPath.TryCreateMemoryBank(rawName, out sanitised, out error))
                {
                    try
                    {
                        // Repopulate the dropdown so the new bank is visible,
                        // pre-selected as the active bank. We do NOT fire
                        // MemoryBankChanged from SetMemoryBanks (it is
                        // suppressed), so we drive the switch ourselves via
                        // OnMemoryBankChanged.
                        var banks = UserDataPath.ListMemoryBanks();
                        _control.Value.SuperMemoryToolbar?.SetMemoryBanks(banks, sanitised);

                        OnMemoryBankChanged(this, new MemoryBankChangedEventArgs(sanitised));

                        // Replace the generic "Switched to…" banner that
                        // OnMemoryBankChanged just added with one that makes
                        // the creation explicit, so the user sees confirmation
                        // of what actually happened.
                        _control.Value.AddMessage(new ChatMessage
                        {
                            Role = ChatRole.Assistant,
                            Content = $"Created memory bank **{sanitised}** with brief.md, terminology.md, style.md and a reference folder, and switched to it. Open the folder from the toolbar to fill it in."
                        });
                    }
                    catch (Exception ex)
                    {
                        AddErrorMessage($"Bank created but could not be activated: {ex.Message}");
                    }
                    return;
                }

                // Creation failed – tell the user why and loop back to the
                // prompt so they can adjust the name without losing their
                // typing flow.
                MessageBox.Show(
                    parent,
                    error ?? "Could not create the memory bank.",
                    "Create memory bank",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Shows a small modal dialog asking the user to name a new memory
        /// bank, with a live sanitisation preview underneath the text box so
        /// they can see what folder name will actually be created.
        /// </summary>
        /// <returns>The raw user input, or null if the dialog was cancelled.</returns>
        private string PromptForNewBankName(IWin32Window parent)
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Create new memory bank";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ClientSize = new System.Drawing.Size(420, 170);
                dlg.ShowInTaskbar = false;

                var lblInstructions = new Label
                {
                    Text = "Short name for the new bank (lowercase letters, digits,\nhyphens or underscores). Example: legal, medical, eu-procurement.",
                    Location = new System.Drawing.Point(12, 12),
                    Size = new System.Drawing.Size(396, 34),
                    AutoSize = false
                };

                var txtName = new TextBox
                {
                    Location = new System.Drawing.Point(12, 54),
                    Size = new System.Drawing.Size(396, 22),
                };

                var lblPreview = new Label
                {
                    Text = "Folder name: –",
                    Location = new System.Drawing.Point(12, 82),
                    Size = new System.Drawing.Size(396, 18),
                    ForeColor = System.Drawing.Color.FromArgb(120, 120, 120)
                };

                var btnOk = new Button
                {
                    Text = "Create",
                    DialogResult = DialogResult.OK,
                    Location = new System.Drawing.Point(232, 125),
                    Size = new System.Drawing.Size(85, 28),
                    Enabled = false
                };

                var btnCancel = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Location = new System.Drawing.Point(323, 125),
                    Size = new System.Drawing.Size(85, 28)
                };

                txtName.TextChanged += (s, e) =>
                {
                    var safe = UserDataPath.SanitizeBankName(txtName.Text);
                    if (string.IsNullOrEmpty(safe))
                    {
                        lblPreview.Text = "Folder name: –";
                        lblPreview.ForeColor = System.Drawing.Color.FromArgb(120, 120, 120);
                        btnOk.Enabled = false;
                    }
                    else
                    {
                        lblPreview.Text = "Folder name: " + safe;
                        lblPreview.ForeColor = System.Drawing.Color.FromArgb(30, 90, 158);
                        btnOk.Enabled = true;
                    }
                };

                dlg.Controls.Add(lblInstructions);
                dlg.Controls.Add(txtName);
                dlg.Controls.Add(lblPreview);
                dlg.Controls.Add(btnOk);
                dlg.Controls.Add(btnCancel);
                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;

                var result = parent != null ? dlg.ShowDialog(parent) : dlg.ShowDialog();
                if (result != DialogResult.OK) return null;
                return txtName.Text;
            }
        }

        private void RefreshSuperMemoryInboxCount()
        {
            try
            {
                var inboxDir = Path.Combine(ActiveMemoryBankDir, MemoryBankReader.ReferenceFolder);
                if (!Directory.Exists(inboxDir))
                {
                    _control.Value.UpdateInboxCount(0);
                    return;
                }

                // Count every file in the inbox – not just .md – so the
                // Process Inbox button lights up whenever the user has
                // dropped anything in. Process Inbox itself handles only
                // Markdown; it shows a routing message for TMX/DOCX/PDF/etc.
                // pointing the user at Distill. See OnProcessInbox for the
                // actual per-file-type logic.
                //
                // .md files with "compiled: true" in their frontmatter are
                // excluded (they have already been processed into structured
                // articles and are now just archived inbox receipts).
                var files = Directory.GetFiles(inboxDir, "*", SearchOption.TopDirectoryOnly);
                int count = 0;
                foreach (var f in files)
                {
                    try
                    {
                        var ext = Path.GetExtension(f);
                        if (string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase))
                        {
                            // Only count uncompiled .md files.
                            var head = ReadFileHead(f, 500);
                            if (head.IndexOf("compiled: true", StringComparison.OrdinalIgnoreCase) < 0)
                                count++;
                        }
                        else
                        {
                            // Non-.md files are always counted – they indicate
                            // material the user wants to hand off to Distill
                            // (or a Markdown file they forgot to rename).
                            count++;
                        }
                    }
                    catch { count++; } // if can't stat the file, count it
                }
                _control.Value.UpdateInboxCount(count);
            }
            catch
            {
                _control.Value.UpdateInboxCount(0);
            }
        }

        /// <summary>
        /// Watches the bank's reference/ folder. The count is no longer shown - the
        /// toolbar label went with the inbox - but the watcher is harmless and keeps
        /// the refresh hook alive for whatever wants it next.
        /// </summary>
        private void StartInboxWatcher()
        {
            try
            {
                var inboxDir = Path.Combine(ActiveMemoryBankDir, MemoryBankReader.ReferenceFolder);
                if (!Directory.Exists(inboxDir)) return;

                // Watch every file type, not just *.md – users drop TMX, PDF,
                // DOCX into the inbox too, and the Process Inbox button needs
                // to reflect that (it will route non-.md files to Distill via
                // a helpful message rather than silently ignoring them).
                _inboxWatcher = new FileSystemWatcher(inboxDir, "*.*")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };

                // Debounce: FileSystemWatcher fires multiple events per file operation.
                // Use a timer to coalesce them into a single refresh.
                var debounceTimer = new System.Windows.Forms.Timer { Interval = 500 };
                debounceTimer.Tick += (s, e) =>
                {
                    debounceTimer.Stop();
                    RefreshSuperMemoryInboxCount();
                };

                EventHandler triggerRefresh = (s, e) =>
                {
                    if (_control.Value.InvokeRequired)
                        _control.Value.BeginInvoke(new Action(() => { debounceTimer.Stop(); debounceTimer.Start(); }));
                    else
                    { debounceTimer.Stop(); debounceTimer.Start(); }
                };

                _inboxWatcher.Created += (s, e) => triggerRefresh(s, e);
                _inboxWatcher.Deleted += (s, e) => triggerRefresh(s, e);
                _inboxWatcher.Renamed += (s, e) => triggerRefresh(s, e);
            }
            catch
            {
                // Non-critical – toolbar still works via manual refresh
            }
        }

        private static string ReadFileHead(string path, int maxChars)
        {
            using (var sr = new StreamReader(path))
            {
                var buf = new char[maxChars];
                int read = sr.Read(buf, 0, maxChars);
                return new string(buf, 0, read);
            }
        }



        /// <summary>
        /// Overview button: generate a self-contained HTML overview of the active
        /// memory bank from its frontmatter index and open it in the browser.
        /// Metadata only – no LLM call – so it is fast and free.
        /// </summary>
        /// <summary>
        /// Bank report: what this bank actually contributes to a prompt.
        ///
        /// Computed from the files themselves - no AI call, no metadata index.
        /// The previous Overview rendered an HTML page out of article
        /// frontmatter, and a three-file bank has none. The questions worth
        /// answering now are "what is in here", "how much of the prompt does it
        /// take", and "is anything obviously wrong".
        /// </summary>
        private void OnOverview(object sender, EventArgs e)
        {
            try
            {
                var bankDir = ActiveMemoryBankDir;
                var bankName = ActiveMemoryBankName;

                if (!Directory.Exists(bankDir))
                {
                    ShowSuperMemoryMessage("Memory bank **" + bankName + "** does not exist yet.\n\n" +
                        "Expected location:\n`" + bankDir + "`");
                    return;
                }

                if (UserDataPath.IsLegacyBankLayout(bankDir))
                {
                    ShowSuperMemoryMessage(
                        "**" + bankName + "** still uses the old folder layout, so nothing in it is " +
                        "being read - it contributes nothing to the AI's context.\n\n" +
                        "Use the Convert button in the toolbar first.");
                    return;
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("### SuperMemory report - " + bankName);
                sb.AppendLine();

                var warnings = new List<string>();

                foreach (var f in MemoryBankReader.BankFiles)
                {
                    int n = -1;
                    try
                    {
                        var fp = Path.Combine(bankDir, f);
                        if (File.Exists(fp)) n = File.ReadAllLines(fp).Length;
                    }
                    catch { }

                    sb.AppendLine(n < 0
                        ? "- `" + f + "` - **missing**"
                        : "- `" + f + "` - " + n + " lines");

                    if (n < 0 && f == MemoryBankReader.BriefFile)
                        warnings.Add("No `brief.md`, so the AI is told nothing about who this client is.");
                }

                // The table is the format that makes a wrong entry findable, so a
                // terminology file that is not one is worth saying out loud.
                try
                {
                    var termPath = Path.Combine(bankDir, MemoryBankReader.TerminologyFile);
                    if (File.Exists(termPath))
                    {
                        int rows = 0;
                        foreach (var line in File.ReadAllLines(termPath))
                        {
                            var t = line.Trim();
                            if (!t.StartsWith("|") || !t.EndsWith("|")) continue;
                            if (t.Replace("|", "").Replace("-", "").Replace(":", "").Trim().Length == 0) continue;
                            if (t.IndexOf("Source", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                t.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            rows++;
                        }
                        sb.AppendLine();
                        sb.AppendLine(rows > 0
                            ? "**" + rows + " term row(s)** in the table."
                            : "No table rows found in `terminology.md`.");
                        if (rows == 0)
                            warnings.Add("`terminology.md` has no table rows - if it is still prose from a " +
                                "conversion, rewriting it as a table is what makes a wrong entry findable.");
                    }
                }
                catch { }

                // Stray root files: searchable, but never sent to the AI.
                try
                {
                    var strays = Directory.GetFiles(bankDir, "*.md", SearchOption.TopDirectoryOnly)
                        .Select(Path.GetFileName)
                        .Where(n => !MemoryBankReader.BankFiles.Contains(n, StringComparer.OrdinalIgnoreCase))
                        .ToList();
                    if (strays.Count > 0)
                        warnings.Add("These sit in the bank root but are never sent to the AI - fold them " +
                            "into the three files or move them to `reference/`: " +
                            string.Join(", ", strays.Select(n => "`" + n + "`")));
                }
                catch { }

                // What actually reaches a prompt, shared layer included.
                try
                {
                    var ctx = EnsureKbReader()?.LoadContext(GetProjectName(), null,
                        GetDocumentSourceLanguage(), GetDocumentTargetLanguage());
                    sb.AppendLine();
                    if (ctx == null || !ctx.HasContent)
                    {
                        sb.AppendLine("**Nothing would be sent to the AI from this bank.**");
                    }
                    else
                    {
                        sb.AppendLine("**~" + ctx.EstimatedTokens + " tokens** would be added to a prompt.");
                        bool shared =
                            !string.IsNullOrWhiteSpace(ctx.SharedBriefText) ||
                            !string.IsNullOrWhiteSpace(ctx.SharedTerminologyText) ||
                            !string.IsNullOrWhiteSpace(ctx.SharedStyleText);
                        sb.AppendLine(shared
                            ? "Includes the `" + MemoryBankReader.SharedBankName +
                              "` bank, which this one overrides where they disagree."
                            : "No `" + MemoryBankReader.SharedBankName +
                              "` bank found - house defaults are not being applied.");
                    }
                }
                catch { }

                if (warnings.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("**Worth fixing**");
                    foreach (var w in warnings) sb.AppendLine("- " + w);
                }

                ShowSuperMemoryMessage(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                AddErrorMessage("Could not build the bank report: " + ex.Message);
            }
        }

        /// <summary>
        /// Harvests the open document's tracked changes into the active bank.
        ///
        /// Same work as the MCP get_tracked_changes tool with save=true, which
        /// until now was the only way to reach it - so a translator not driving
        /// Trados from Claude Desktop had no idea the feature existed. It runs at
        /// the end of a review pass, which is when this panel is already open.
        /// </summary>
        private void OnHarvestTrackedChanges(object sender, EventArgs e)
        {
            try
            {
                if (_activeDocument == null)
                {
                    ShowSuperMemoryMessage("No document is open in the Trados editor.");
                    return;
                }

                var bankName = ActiveMemoryBankName;
                var result = BridgeGetTrackedChanges(new BridgeTrackedChangesQuery { Save = true, Limit = 1 });

                if (result == null || !result.Available)
                {
                    ShowSuperMemoryMessage(result?.Note ?? "Could not read the document's tracked changes.");
                    return;
                }

                if (result.SegmentsWithChanges == 0)
                {
                    // Worth being explicit: an empty harvest almost always means
                    // Track Changes was off while editing, not that the review
                    // made no changes.
                    ShowSuperMemoryMessage(
                        "**No tracked changes found** in this document.\n\n" +
                        "Studio only records them when Track Changes was switched ON while you " +
                        "were editing (Review \u2192 Track Changes). Nothing was written.");
                    return;
                }

                if (string.IsNullOrEmpty(result.SavedTo))
                {
                    ShowSuperMemoryMessage(
                        $"Found {result.SegmentsWithChanges} segment(s) with tracked changes, but the " +
                        $"harvest could not be saved.\n\n{result.Note}");
                    return;
                }

                ShowSuperMemoryMessage(
                    $"**Harvested {result.SegmentsWithChanges} segment(s)** with tracked changes into " +
                    $"memory bank **{bankName}**.\n\n" +
                    $"`{result.SavedTo}`\n\n" +
                    "This is source material and nothing reads it automatically. Open it, or ask me " +
                    "what keeps recurring in it, then put the decisions worth keeping into " +
                    "`terminology.md` and `style.md`. A change that appears once is an edit; one that " +
                    "appears nine times is a rule.");
            }
            catch (Exception ex)
            {
                AddErrorMessage("Could not harvest tracked changes: " + ex.Message);
            }
        }

        /// <summary>Opens the active bank's folder. The files are meant to be
        /// edited by hand, so reaching them must not require knowing the path.</summary>
        private void OnOpenBankFolder(object sender, EventArgs e)
        {
            try
            {
                var dir = ActiveMemoryBankDir;
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AddErrorMessage("Could not open the bank folder: " + ex.Message);
            }
        }


        private void CollectVaultFiles(string dir, string vaultRoot,
            System.Text.StringBuilder sb, ref int count, string skipSubDir)
        {
            foreach (var f in Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var fileName = Path.GetFileName(f);
                    // Skip example/template files – they're shipped scaffolding, not real content
                    if (fileName.StartsWith("_EXAMPLE_", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var relPath = f.Substring(vaultRoot.Length).TrimStart('\\', '/');
                    sb.AppendLine($"## File: {relPath}");
                    sb.AppendLine(File.ReadAllText(f));
                    sb.AppendLine();
                    count++;
                }
                catch { }
            }
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var subDirName = Path.GetFileName(subDir);
                if (string.Equals(subDirName, skipSubDir, StringComparison.OrdinalIgnoreCase))
                    continue;
                CollectVaultFiles(subDir, vaultRoot, sb, ref count, skipSubDir);
            }
        }

        private void RunSuperMemoryAgent(string systemPrompt, string userMessage,
            string displayText, PromptLogFeature feature, string promptName,
            Action<string> postProcess)
        {
            // Resolve provider / API key
            var aiSettings = _settings?.AiSettings;
            if (aiSettings == null)
            {
                AddErrorMessage("AI settings not configured. Open Settings \u2192 AI Settings to configure a provider.");
                return;
            }

            var provider = aiSettings.SelectedProvider ?? LlmModels.ProviderOpenAi;
            string apiKey;
            string baseUrl = null;
            string model = aiSettings.GetSelectedModel();

            if (provider == LlmModels.ProviderOllama)
            {
                apiKey = "ollama";
                baseUrl = aiSettings.OllamaEndpoint ?? "http://localhost:11434";
            }
            else if (provider == LlmModels.ProviderCustomOpenAi)
            {
                var profile = aiSettings.GetActiveCustomProfile();
                if (profile == null)
                {
                    AddErrorMessage("No custom OpenAI profile configured.");
                    return;
                }
                apiKey = profile.ApiKey;
                baseUrl = profile.Endpoint;
                model = profile.Model;
            }
            else
            {
                apiKey = LlmClient.ResolveApiKey(provider, aiSettings.ApiKeys);
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                AddErrorMessage($"No API key configured for {provider}. Open Settings \u2192 AI Settings to add one.");
                return;
            }

            // Show status message – unless the caller has already displayed
            // its own progress bubble (in which case displayText is left
            // empty). This lets slow operations like OnHealthCheck, which
            // need to scan the vault before the displayText would know how
            // many files it is going to process, show an upfront "scanning
            // memory bank..." bubble before calling into us. SetThinking is
            // idempotent so it is safe to call even if the caller already
            // set thinking.
            if (!string.IsNullOrEmpty(displayText))
            {
                _control.Value.AddMessage(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = displayText
                });
            }
            _control.Value.SetThinking(true);
            _control.Value.SetSuperMemoryBusy(true);

            // Cancel any pending chat request
            _chatCts?.Cancel();
            _chatCts = new CancellationTokenSource();
            var ct = _chatCts.Token;

            var capturedProvider = provider;
            var capturedModel = model;
            var capturedKey = apiKey;
            var capturedBaseUrl = baseUrl;

            Task.Run(async () =>
            {
                try
                {
                    var client = new LlmClient(capturedProvider, capturedModel, capturedKey,
                        capturedBaseUrl, ollamaTimeoutMinutes: aiSettings.OllamaTimeoutMinutes);
                    var response = await client.SendPromptAsync(
                        userMessage, systemPrompt,
                        maxTokens: 16384, cancellationToken: ct,
                        feature: feature, promptName: promptName);

                    SafeInvoke(() =>
                    {
                        var responseMsg = new ChatMessage
                        {
                            Role = ChatRole.Assistant,
                            Content = response?.Trim() ?? "(No response)"
                        };
                        _chatHistory.Add(responseMsg);
                        _control.Value.AddMessage(responseMsg);
                        _control.Value.SetThinking(false);
                        _control.Value.SetSuperMemoryBusy(false);
                        SaveChatHistory();

                        // Run post-processing (e.g. write files from compile response)
                        try
                        {
                            postProcess?.Invoke(response ?? "");
                        }
                        catch (Exception pex)
                        {
                            AddErrorMessage($"SuperMemory post-processing error: {pex.Message}");
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    SafeInvoke(() =>
                    {
                        _control.Value.SetThinking(false);
                        _control.Value.SetSuperMemoryBusy(false);
                    });
                }
                catch (Exception ex)
                {
                    SafeInvoke(() =>
                    {
                        _control.Value.SetThinking(false);
                        _control.Value.SetSuperMemoryBusy(false);
                        AddErrorMessage($"SuperMemory error: {ex.Message}");
                    });
                }
            });
        }



        // ─── Auto-indexing ──────────────────────────────────────────

        /// <summary>
        /// Rebuilds the master index files in <c>05_INDICES/</c> by scanning
        /// all content folders for article frontmatter. No LLM call – this is
        /// a pure file-scan operation and completes in under a second even on
        /// large banks.
        /// </summary>
        private void RebuildIndices(string vaultDir)
        {
            try
            {
                var indicesDir = Path.Combine(vaultDir, "05_INDICES");
                Directory.CreateDirectory(indicesDir);

                var today = DateTime.Now.ToString("yyyy-MM-dd");

                // ── Master Terminology Index ───────────────────────
                var termDir = Path.Combine(vaultDir, "02_TERMINOLOGY");
                var termSb = new System.Text.StringBuilder();
                termSb.AppendLine("---");
                termSb.AppendLine("title: Master Terminology Index");
                termSb.AppendLine("type: index");
                termSb.AppendLine($"updated: {today}");
                termSb.AppendLine("---");
                termSb.AppendLine();
                termSb.AppendLine("# Master Terminology Index");
                termSb.AppendLine();
                termSb.AppendLine("| Source | Target | Domain | Client | Confidence | Status |");
                termSb.AppendLine("|--------|--------|--------|--------|------------|--------|");

                if (Directory.Exists(termDir))
                {
                    foreach (var file in Directory.GetFiles(termDir, "*.md", SearchOption.AllDirectories))
                    {
                        var fn = Path.GetFileName(file);
                        if (fn.StartsWith("_EXAMPLE_", System.StringComparison.OrdinalIgnoreCase)) continue;
                        if (file.Contains("_archive")) continue;

                        var head = MemoryBankReader.ReadHead(file, 2048);
                        var fm = MemoryBankReader.ParseFrontmatter(head);

                        var src = fm.ContainsKey("term_source") ? fm["term_source"] : "";
                        var tgt = fm.ContainsKey("term_target") ? fm["term_target"] : "";
                        var domain = fm.ContainsKey("domain") ? fm["domain"] : "";
                        var client = fm.ContainsKey("client") ? fm["client"] : "";
                        var confidence = fm.ContainsKey("confidence") ? fm["confidence"] : "";
                        var status = fm.ContainsKey("status") ? fm["status"] : "";

                        if (string.IsNullOrWhiteSpace(src) && string.IsNullOrWhiteSpace(tgt))
                        {
                            // Fall back to title if term_source/term_target are missing
                            var title = fm.ContainsKey("title") ? fm["title"] : Path.GetFileNameWithoutExtension(fn);
                            src = title;
                        }

                        termSb.AppendLine($"| {Escape(src)} | {Escape(tgt)} | {Escape(domain)} | {Escape(client)} | {confidence} | {status} |");
                    }
                }

                File.WriteAllText(Path.Combine(indicesDir, "master-terminology.md"),
                    termSb.ToString(), new System.Text.UTF8Encoding(false));

                // ── Client Summary ─────────────────────────────────
                var clientDir = Path.Combine(vaultDir, "01_CLIENTS");
                var clientSb = new System.Text.StringBuilder();
                clientSb.AppendLine("---");
                clientSb.AppendLine("title: Client Summary");
                clientSb.AppendLine("type: index");
                clientSb.AppendLine($"updated: {today}");
                clientSb.AppendLine("---");
                clientSb.AppendLine();
                clientSb.AppendLine("# Client Summary");
                clientSb.AppendLine();

                if (Directory.Exists(clientDir))
                {
                    foreach (var file in Directory.GetFiles(clientDir, "*.md", SearchOption.AllDirectories))
                    {
                        var fn = Path.GetFileName(file);
                        if (fn.StartsWith("_EXAMPLE_", System.StringComparison.OrdinalIgnoreCase)) continue;
                        if (file.Contains("_archive")) continue;

                        var head = MemoryBankReader.ReadHead(file, 2048);
                        var fm = MemoryBankReader.ParseFrontmatter(head);

                        var title = fm.ContainsKey("title") ? fm["title"]
                            : fm.ContainsKey("client") ? fm["client"]
                            : Path.GetFileNameWithoutExtension(fn);
                        var tldr = fm.ContainsKey("tldr") ? fm["tldr"] : null;

                        // If no tldr, extract the first non-frontmatter, non-heading paragraph
                        if (string.IsNullOrWhiteSpace(tldr))
                            tldr = ExtractFirstParagraph(head);

                        clientSb.AppendLine($"## {title}");
                        clientSb.AppendLine();
                        if (!string.IsNullOrWhiteSpace(tldr))
                            clientSb.AppendLine(tldr);
                        else
                            clientSb.AppendLine("*(No summary available)*");
                        clientSb.AppendLine();
                    }
                }

                File.WriteAllText(Path.Combine(indicesDir, "client-summary.md"),
                    clientSb.ToString(), new System.Text.UTF8Encoding(false));

                // ── Domain Summary ─────────────────────────────────
                var domainDir = Path.Combine(vaultDir, "03_DOMAINS");
                var domainSb = new System.Text.StringBuilder();
                domainSb.AppendLine("---");
                domainSb.AppendLine("title: Domain Summary");
                domainSb.AppendLine("type: index");
                domainSb.AppendLine($"updated: {today}");
                domainSb.AppendLine("---");
                domainSb.AppendLine();
                domainSb.AppendLine("# Domain Summary");
                domainSb.AppendLine();

                if (Directory.Exists(domainDir))
                {
                    foreach (var file in Directory.GetFiles(domainDir, "*.md", SearchOption.AllDirectories))
                    {
                        var fn = Path.GetFileName(file);
                        if (fn.StartsWith("_EXAMPLE_", System.StringComparison.OrdinalIgnoreCase)) continue;
                        if (file.Contains("_archive")) continue;

                        var head = MemoryBankReader.ReadHead(file, 2048);
                        var fm = MemoryBankReader.ParseFrontmatter(head);

                        var title = fm.ContainsKey("title") ? fm["title"]
                            : fm.ContainsKey("domain") ? fm["domain"]
                            : Path.GetFileNameWithoutExtension(fn);
                        var tldr = fm.ContainsKey("tldr") ? fm["tldr"] : null;

                        if (string.IsNullOrWhiteSpace(tldr))
                            tldr = ExtractFirstParagraph(head);

                        domainSb.AppendLine($"## {title}");
                        domainSb.AppendLine();
                        if (!string.IsNullOrWhiteSpace(tldr))
                            domainSb.AppendLine(tldr);
                        else
                            domainSb.AppendLine("*(No summary available)*");
                        domainSb.AppendLine();
                    }
                }

                File.WriteAllText(Path.Combine(indicesDir, "domain-summary.md"),
                    domainSb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch
            {
                // Non-critical – indices are a convenience, not a requirement
            }
        }

        /// <summary>Escapes pipe characters for Markdown table cells.</summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("|", "\\|");
        }

        /// <summary>
        /// Extracts the first non-empty paragraph after the frontmatter block
        /// and any heading lines. Used as a fallback when no <c>tldr:</c> is
        /// available in the frontmatter.
        /// </summary>
        private static string ExtractFirstParagraph(string head)
        {
            if (string.IsNullOrEmpty(head)) return null;

            var lines = head.Split('\n');
            bool pastFrontmatter = false;
            bool inFrontmatter = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                if (!pastFrontmatter)
                {
                    if (line == "---" && !inFrontmatter)
                    { inFrontmatter = true; continue; }
                    if (line == "---" && inFrontmatter)
                    { pastFrontmatter = true; continue; }
                    continue;
                }

                // Skip headings and empty lines
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("#")) continue;

                // Found a content line – return it (trimmed to ~200 chars)
                return line.Length > 200 ? line.Substring(0, 200) + "…" : line;
            }

            return null;
        }

        private void WriteVaultFileTracked(string vaultDir, string relativePath,
            string content, List<Tuple<string, bool>> results)
        {
            try
            {
                relativePath = relativePath.Replace('/', '\\');
                var fullPath = Path.Combine(vaultDir, relativePath);
                bool isNew = !File.Exists(fullPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllText(fullPath, content);
                results.Add(Tuple.Create(relativePath, isNew));
            }
            catch { }
        }

        private void WriteVaultFile(string vaultDir, string relativePath, string content, List<string> writtenFiles)
        {
            try
            {
                // Normalize path separators
                relativePath = relativePath.Replace('/', '\\');
                var fullPath = Path.Combine(vaultDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllText(fullPath, content);
                writtenFiles.Add(relativePath);
            }
            catch { }
        }

        private void ShowSuperMemoryMessage(string text)
        {
            _chatHistory.Add(new ChatMessage { Role = ChatRole.Assistant, Content = text });
            _control.Value.AddMessage(new ChatMessage { Role = ChatRole.Assistant, Content = text });
            SaveChatHistory();
        }

        // ─── Distill ────────────────────────────────────────────────

        private const string DistillSystemPrompt =
@"You are a translation knowledge extraction specialist. Your job is to analyse source material provided by a professional translator and distil it into structured SuperMemory knowledge base articles.

## Your task

1. **Identify the source type**: translation memory (TMX), termbase/glossary, style guide, client brief, reference document, or mixed.
2. **Extract knowledge** that is valuable for future translation work:
   - **Terminology decisions** with reasoning (why this term, not that one)
   - **Domain knowledge** (industry concepts, product names, regulatory terms)
   - **Client preferences** (tone, register, specific phrasings, forbidden terms)
   - **Style patterns** (sentence structure, punctuation conventions, number formatting)
   - **Translation pitfalls** (false friends, tricky constructions, common mistakes)

## Source-specific guidance

- **TMX / translation memory**: Focus on *patterns* across segments, not individual translations. Look for consistent terminology choices, recurring constructions, client-specific style. Group findings by theme.
- **Termbases / glossaries**: Organise by domain or client. Include definitions, usage notes, and any context that helps a translator pick the right term. Flag ambiguous or overlapping terms.
- **Documents / style guides**: Extract domain knowledge, preferred phrasing, style conventions, and any rules that should be followed.
- **Mixed / other**: Use your best judgement to categorise and extract.

## Output format

Output one or more knowledge base articles using `### FILE: <relative-path>` markers. Each article is a Markdown file with YAML frontmatter.

**IMPORTANT:** Always write articles to the `00_INBOX/` folder. The user will review them before moving them to the correct vault location using Process Inbox.

Use these vault paths:
- `00_INBOX/<filename>.md` – ALL distilled articles go here for review

Each article must have this frontmatter structure:
```
---
title: <descriptive title>
type: terminology|domain|style|client|reference
domain: <subject area, e.g. medical-imaging, patent-law, legal, marketing>
client: <client name if known, omit if generic>
language_pair: <e.g. nl-BE → en-US>
confidence: high|medium|low
tags: [<relevant tags>]
source: distilled
sources:
  - <original filename 1>
  - <original filename 2>
created: <today's date YYYY-MM-DD>
updated: <today's date YYYY-MM-DD>
tldr: <one-sentence summary of what this article covers – max 150 characters>
---
```

### Confidence scoring

Assign a confidence level based on the quality and authority of the source material:
- **high** – derived from an authoritative source: official client glossary, published style guide, large TMX with consistent patterns, or confirmed by multiple corroborating sources.
- **medium** – derived from a single source of reasonable quality: a short PDF, a single reference document, a small TMX.
- **low** – derived from ambiguous or incomplete material, or when the extraction required significant inference. Flag uncertain terminology decisions explicitly.

### Source traceability

Always list the original source filename(s) in the `sources:` frontmatter field. When recording terminology decisions, **always quote the exact source and target terms verbatim** – do not paraphrase or generalise term pairs.

## Guidelines

- Keep articles **focused and concise** – one topic per article where possible.
- Use bullet points and tables for terminology lists.
- Include the *reasoning* behind translation choices, not just the choices themselves.
- When in doubt, create separate articles rather than one huge article.
- Write in English (the knowledge base language), but include source/target examples in their original languages.
- If the source material is too large to fully process, prioritise the most valuable and non-obvious knowledge.
- Always include a `tldr:` – this is used for fast scanning during context loading.";




        /// <summary>
        /// True when <paramref name="filePath"/> sits directly in
        /// <c>&lt;bankDir&gt;/00_INBOX/</c> (not in a sub-folder, not in
        /// <c>_archive/</c>, not anywhere else on disk). Used by
        /// <see cref="PostProcessDistillResponse"/> to decide whether a
        /// Distill source file is part of the inbox lifecycle and should
        /// be archived after processing.
        /// </summary>
        private static bool IsDirectlyInsideInbox(string filePath, string bankDir)
        {
            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(bankDir)) return false;
            try
            {
                var inboxDir = Path.GetFullPath(Path.Combine(bankDir, "00_INBOX"));
                var fileDir = Path.GetFullPath(Path.GetDirectoryName(filePath) ?? "");
                return string.Equals(fileDir, inboxDir, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // ─── Batch Translate ────────────────────────────────────────

        // ── Translate via Workbench (64-bit large-file offload, #42 – Design B) ──
        //
        // Hands the active document's .sdlxliff to the headless 64-bit Workbench,
        // which translates it round-trip (tags preserved) and writes a translated
        // .sdlxliff. The document is CLOSED first (so the file is free and Trados
        // does no heavy work), then the translated file is swapped in and the
        // document REOPENED. A .sv-backup copy of the original is kept.
        private void OnTranslateViaWorkbenchRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                var batchControl = _control.Value.BatchTranslateControl;
                var doc = _activeDocument;
                if (doc == null || _editorController == null)
                { batchControl.AppendLog("No document open.", true); return; }

                var aiSettings = _settings.AiSettings;
                if (aiSettings == null)
                { batchControl.AppendLog("AI settings not configured. Open Settings to configure a provider.", true); return; }

                // Provider / key / model (mirrors Batch Translate).
                var provider = aiSettings.SelectedProvider ?? LlmModels.ProviderOpenAi;
                string apiKey; string baseUrl = null; string model = aiSettings.GetSelectedModel();
                if (provider == LlmModels.ProviderOllama)
                { apiKey = "ollama"; baseUrl = aiSettings.OllamaEndpoint ?? "http://localhost:11434"; }
                else if (provider == LlmModels.ProviderCustomOpenAi)
                {
                    var profile = aiSettings.GetActiveCustomProfile();
                    if (profile == null) { batchControl.AppendLog("No custom OpenAI profile configured.", true); return; }
                    apiKey = profile.ApiKey; baseUrl = profile.Endpoint; model = profile.Model;
                }
                else apiKey = LlmClient.ResolveApiKey(provider, aiSettings.ApiKeys);
                if (string.IsNullOrEmpty(apiKey))
                { batchControl.AppendLog($"No API key configured for {provider}. Open Settings → AI Settings to add one.", true); return; }

                var sourceLang = GetDocumentSourceLanguage();
                var targetLang = GetDocumentTargetLanguage();
                if (string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
                { batchControl.AppendLog("Cannot determine source/target language from document.", true); return; }

                // The bilingual .sdlxliff for the active file, and its ProjectFile (to reopen).
                var projFile = doc.ActiveFile;
                var sdlxliffPath = projFile?.LocalFilePath;
                if (projFile == null || string.IsNullOrEmpty(sdlxliffPath) || !File.Exists(sdlxliffPath))
                {
                    batchControl.AppendLog("Could not find the .sdlxliff file for the active document. " +
                        "Open a single file in the editor and try again.", true);
                    return;
                }

                // Locate the 64-bit Workbench engine: settings override -> PATH -> common locations.
                var exe = Core.WorkbenchOffload.ResolveWorkbenchExe(aiSettings.WorkbenchExePath);
                if (string.IsNullOrEmpty(exe))
                {
                    exe = PromptLocateWorkbench(aiSettings);
                    if (string.IsNullOrEmpty(exe)) return;
                }

                var scope = batchControl.GetSelectedScope();
                string scopeStr;
                if (scope == BatchScope.EmptyOnly || scope == BatchScope.FilteredEmptyOnly) scopeStr = "EmptyOnly";
                else if (scope == BatchScope.NotFinalized) scopeStr = "NotFinalized";
                else scopeStr = "All";

                // System prompt = same prompt + termbase + document context as Batch Translate.
                // Document context is collected here (while the doc is still open) and capped
                // like the normal batch; it's cheap for the media-heavy/text-light files this
                // targets, so there's no reason to drop it.
                var allTerms = TermLensEditorViewPart.GetCurrentTermbaseTerms();
                var termbaseTerms = allTerms.Where(t => aiSettings.IsTermbaseAiEnabled(t.TermbaseId)).ToList();
                WarnIfNoAiTermbases(batchControl, allTerms.Count, termbaseTerms.Count);
                var selectedPromptPath = batchControl.GetSelectedPromptPath();
                aiSettings.SelectedPromptPath = selectedPromptPath; SettingsService.Save();
                var customPromptContent = ResolveCustomPromptContent(sourceLang, targetLang);
                var customSystemPrompt = aiSettings.CustomSystemPrompt;
                List<string> docSegments = aiSettings.IncludeDocumentContext ? CollectDocumentContext().Item1 : null;
                var maxDocSegs = aiSettings.DocumentContextMaxSegments > 0 ? aiSettings.DocumentContextMaxSegments : 500;
                var projectName = GetProjectName();
                var kbContext = LoadKbContextForPrompt(projectName, sourceLang, targetLang);
                var systemPrompt = TranslationPrompt.BuildSystemPrompt(
                    sourceLang, targetLang, customPromptContent, termbaseTerms, customSystemPrompt,
                    aiSettings.IncludeDocumentContext ? docSegments : null, maxDocSegs,
                    aiSettings.IncludeTermMetadata, kbContext);

                var modelInfo = LlmModels.FindModel(model);
                int maxTokens = modelInfo?.DefaultMaxTokens ?? 16384;
                int batchSize = aiSettings.BatchSize > 0 ? aiSettings.BatchSize : 20;

                var confirm = MessageBox.Show(
                    "Translate this document in the 64-bit Supervertaler Workbench?\n\n" +
                    "The current document will be CLOSED, translated externally (scope: " + scopeStr + "), " +
                    "then reopened. A backup of the original .sdlxliff is kept.\n\nFile: " +
                    Path.GetFileName(sdlxliffPath),
                    "Translate via Workbench", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (confirm != DialogResult.OK) return;

                var config = new Core.WorkbenchOffload.OffloadJob
                {
                    SourceLang = sourceLang,
                    TargetLang = targetLang,
                    Provider = provider,
                    Model = model,
                    BaseUrl = baseUrl,
                    ApiKey = apiKey,
                    SystemPrompt = systemPrompt,
                    Scope = scopeStr,
                    RetryUntilComplete = batchControl.IsRetryEnabled,
                    BatchSize = batchSize,
                    MaxTokens = maxTokens,
                };

                var workDir = Path.Combine(Path.GetTempPath(), "Supervertaler", "offload",
                    DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                var outPath = Path.Combine(workDir, "translated.sdlxliff");

                _batchCts = new CancellationTokenSource();
                var ct = _batchCts.Token;

                // Top-level progress window. The Batch log lives inside the editor,
                // which is closed during the offload, so it would be invisible – this
                // floating window is the user's feedback while the document is closed.
                var progress = new Controls.OffloadProgressForm(Path.GetFileName(sdlxliffPath));
                progress.CancelRequested += (s2, e2) => { try { _batchCts.Cancel(); } catch { } };

                // Close the document so the file is free and Trados isn't holding it.
                batchControl.SetRunning(true);
                batchControl.AppendLog($"Closing document and offloading to 64-bit Workbench ({Path.GetFileName(exe)})…");
                try { _editorController.Close(doc); }
                catch (Exception ex)
                {
                    // A failure here is almost always 32-bit Trados in a degraded /
                    // low-memory state: closing the document calls into the native SDK,
                    // which throws an AccessViolation ("Attempted to read or write
                    // protected memory… memory is corrupt") once the process is poisoned.
                    // The offload is aborted and the file is left untouched – the user
                    // just needs a fresh Trados session. Show a plain-language message
                    // instead of the cryptic SDK text.
                    bool unstable = ex is AccessViolationException
                        || (ex.Message != null
                            && (ex.Message.IndexOf("protected memory", StringComparison.OrdinalIgnoreCase) >= 0
                                || ex.Message.IndexOf("memory is corrupt", StringComparison.OrdinalIgnoreCase) >= 0));
                    batchControl.AppendLog(unstable
                        ? "✗ Offload cancelled: Trados couldn't close the document (low-memory / unstable state). " +
                          "Restart Trados Studio and try again – your file is untouched."
                        : "Could not close the document: " + ex.Message, true);
                    batchControl.SetRunning(false);
                    progress.Dispose();
                    MessageBox.Show(
                        unstable
                            ? "Trados is in a low-memory / unstable state and couldn't close the document, " +
                              "so Translate via Workbench was cancelled. Your file has not been changed.\n\n" +
                              "Please save any other work, restart Trados Studio, reopen the project, and run " +
                              "Translate via Workbench again on the fresh session."
                            : "Could not close the document:\n\n" + ex.Message,
                        "Translate via Workbench",
                        MessageBoxButtons.OK,
                        unstable ? MessageBoxIcon.Warning : MessageBoxIcon.Error);
                    return;
                }
                progress.Show();

                Task.Run(() =>
                {
                    Core.WorkbenchOffload.OffloadResult res = null;
                    try
                    {
                        res = Core.WorkbenchOffload.RunSdlxliff(exe, sdlxliffPath, outPath, config, workDir,
                            line => progress.UpdateProgress(line), ct);
                    }
                    catch (Exception ex)
                    {
                        progress.UpdateProgress("Error: " + ex.Message);
                    }

                    SafeInvoke(() =>
                    {
                        progress.SetStatus("Applying translation and reopening…");
                        batchControl.SetRunning(false);
                        bool applied = false;
                        try
                        {
                            if (res != null && res.Translated > 0 && File.Exists(outPath))
                            {
                                if (res.Errors != null)
                                    foreach (var er in res.Errors) batchControl.AppendLog("  " + er, true);
                                try { File.Copy(sdlxliffPath, sdlxliffPath + ".sv-backup", true); } catch { }
                                CopyWithRetry(outPath, sdlxliffPath);
                                batchControl.AppendLog($"✓ Workbench translated {res.Translated} segment(s) (failed: {res.Failed}). Reopening…");
                                applied = true;
                            }
                            else
                            {
                                if (res?.Errors != null)
                                    foreach (var er in res.Errors) batchControl.AppendLog("  " + er, true);
                                batchControl.AppendLog("Workbench returned no translations; the original file is unchanged.", true);
                            }
                        }
                        catch (Exception ex)
                        {
                            batchControl.AppendLog("Failed to swap in the translated file: " + ex.Message +
                                " (the original is backed up next to it).", true);
                        }

                        // Reopen the document either way, so the user is back where they were.
                        try { _editorController.Open(projFile, EditingMode.Translation); }
                        catch (Exception ex)
                        { batchControl.AppendLog("Please reopen the document manually – auto-reopen failed: " + ex.Message, true); }
                        if (applied) batchControl.AppendLog("Done.");
                        if (applied && res != null && (res.InputTokens > 0 || res.OutputTokens > 0))
                            try { LogOffloadUsage(provider, model, res.InputTokens, res.OutputTokens, res.Translated); } catch { }
                        progress.Finish();
                    });
                });
            });
        }

        private static void CopyWithRetry(string src, string dest)
        {
            for (int i = 0; i < 5; i++)
            {
                try { File.Copy(src, dest, true); return; }
                catch { System.Threading.Thread.Sleep(300); }
            }
            File.Copy(src, dest, true); // final attempt – throw if still locked
        }

        /// <summary>
        /// Record an offloaded Batch Translate run in Trados's own Token Usage & Costs.
        /// The AI calls happened in Workbench, so without this the cost would be missing
        /// from the Trados ledger. Fired after reopen so project attribution is available.
        /// (Cost is computed at the no-cache rate – a slight overestimate – since the
        /// engine reports total tokens, not the cache breakdown.)
        /// </summary>
        private void LogOffloadUsage(string provider, string model, long inputTokens, long outputTokens, int segs)
        {
            var modelInfo = LlmModels.FindModel(model);
            var entry = new PromptLogEntry
            {
                Timestamp = DateTime.Now,
                Feature = PromptLogFeature.BatchTranslate,
                PromptName = "via Workbench · " + segs + " segments",
                Provider = provider,
                Model = model,
                DisplayModel = modelInfo?.DisplayName ?? model,
                ActualRegularInputTokens = (int)inputTokens,
                ActualOutputTokens = (int)outputTokens,
                ActualCost = TokenEstimator.ComputeActualCost(model, (int)inputTokens, 0, 0, (int)outputTokens),
                IsCostKnown = TokenEstimator.HasPricing(model),
            };
            LlmClient.FirePromptCompleted(entry);
        }

        /// <summary>
        /// Friendly "Workbench not found" flow: offer to locate the executable (which is
        /// then remembered in <see cref="AiSettings.WorkbenchExePath"/>) or to open the
        /// download page. Returns the chosen exe path, or null if the user cancelled.
        /// </summary>
        private string PromptLocateWorkbench(AiSettings aiSettings)
        {
            var choice = MessageBox.Show(
                "Supervertaler Workbench (the 64-bit engine for the large-file offload) wasn't found.\n\n" +
                "• Yes – locate it now (pick Supervertaler.exe, or supervertaler-debug.exe).\n" +
                "• No – open the download page.\n" +
                "• Cancel – do nothing.\n\n" +
                "Your choice is remembered, so you only need to do this once.",
                "Supervertaler Workbench not found",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (choice == DialogResult.Yes)
            {
                using (var dlg = new OpenFileDialog
                {
                    Title = "Locate Supervertaler Workbench",
                    Filter = "Supervertaler (Supervertaler.exe;supervertaler*.exe)|Supervertaler.exe;supervertaler*.exe|" +
                             "Executables (*.exe)|*.exe",
                })
                {
                    if (dlg.ShowDialog() == DialogResult.OK && File.Exists(dlg.FileName))
                    {
                        aiSettings.WorkbenchExePath = dlg.FileName;
                        try { SettingsService.Save(); } catch { }
                        return dlg.FileName;
                    }
                }
                return null;
            }
            if (choice == DialogResult.No)
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo("https://supervertaler.com") { UseShellExecute = true });
                }
                catch { }
            }
            return null;
        }

        private void OnBatchTranslateRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                var batchControl = _control.Value.BatchTranslateControl;

                if (_activeDocument == null)
                {
                    batchControl.AppendLog("No document open.", true);
                    return;
                }

                var aiSettings = _settings.AiSettings;
                if (aiSettings == null)
                {
                    batchControl.AppendLog("AI settings not configured. Open Settings to configure a provider.", true);
                    return;
                }

                // Resolve API key
                var provider = aiSettings.SelectedProvider ?? LlmModels.ProviderOpenAi;
                string apiKey;
                string baseUrl = null;
                string model = aiSettings.GetSelectedModel();

                if (provider == LlmModels.ProviderOllama)
                {
                    apiKey = "ollama";
                    baseUrl = aiSettings.OllamaEndpoint ?? "http://localhost:11434";
                }
                else if (provider == LlmModels.ProviderCustomOpenAi)
                {
                    var profile = aiSettings.GetActiveCustomProfile();
                    if (profile == null)
                    {
                        batchControl.AppendLog("No custom OpenAI profile configured.", true);
                        return;
                    }
                    apiKey = profile.ApiKey;
                    baseUrl = profile.Endpoint;
                    model = profile.Model;
                }
                else
                {
                    apiKey = LlmClient.ResolveApiKey(provider, aiSettings.ApiKeys);
                }

                if (string.IsNullOrEmpty(apiKey))
                {
                    batchControl.AppendLog(
                        $"No API key configured for {provider}. Open Settings \u2192 AI Settings to add one.", true);
                    return;
                }

                // Get language pair from the document
                var sourceLang = GetDocumentSourceLanguage();
                var targetLang = GetDocumentTargetLanguage();

                if (string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
                {
                    batchControl.AppendLog("Cannot determine source/target language from document.", true);
                    return;
                }

                // Collect segments based on selected scope
                var scope = batchControl.GetSelectedScope();
                var segments = CollectSegments(scope);

                // Apply segment limit if set
                var maxSeg = batchControl.GetMaxSegments();
                if (maxSeg > 0 && segments.Count > maxSeg)
                {
                    batchControl.AppendLog($"Limit: processing first {maxSeg} of {segments.Count} segments.");
                    segments = segments.GetRange(0, maxSeg);
                }

                if (segments.Count == 0)
                {
                    batchControl.AppendLog("No segments to translate.", true);
                    return;
                }

                // Get termbase terms for prompt injection (filtered by AI-disabled list)
                // Fallback-served MultiTerm termbases only answer per-segment lookups, so
                // terms for segments the user never visited would silently miss the prompt
                // (#38). Query them for this batch before assembling the term list.
                TermLensEditorViewPart.PrewarmFallbackTermsFor(segments.Select(sg => sg.SourceText));

                var allTerms = TermLensEditorViewPart.GetCurrentTermbaseTerms();
                var aiCfgC = _settings?.AiSettings ?? new AiSettings();
                var termbaseTerms = allTerms.Where(t => aiCfgC.IsTermbaseAiEnabled(t.TermbaseId)).ToList();
                WarnIfNoAiTermbases(batchControl, allTerms.Count, termbaseTerms.Count);

                // Resolve custom prompt from library selection
                var selectedPromptPath = batchControl.GetSelectedPromptPath();
                aiSettings.SelectedPromptPath = selectedPromptPath;
                SettingsService.Save();

                var customPromptContent = ResolveCustomPromptContent(sourceLang, targetLang);
                var customSystemPrompt = aiSettings.CustomSystemPrompt;

                // Collect document context for AI document type analysis
                List<string> docSegments = null;
                if (aiSettings.IncludeDocumentContext)
                {
                    var docCtx = CollectDocumentContext();
                    docSegments = docCtx.Item1;
                }

                int batchSize = aiSettings.BatchSize > 0 ? aiSettings.BatchSize : 20;

                // Load SuperMemory KB context
                var projectName = GetProjectName();
                var kbContext = LoadKbContextForPrompt(projectName, sourceLang, targetLang);

                // Start the batch translation
                batchControl.SetRunning(true);

                var kbSummary = "";
                if (kbContext != null)
                {
                    try
                    {
                        _kbReader?.RefreshIndex();
                        var kbCtx = _kbReader?.LoadContext(projectName, null, sourceLang, targetLang);
                        if (kbCtx != null)
                            kbSummary = " | " + kbCtx.GetSummary();
                    }
                    catch { }
                }

                batchControl.AppendLog(
                    $"Starting: {segments.Count} segments, provider={provider}, model={model}, " +
                    $"batch size={batchSize}{kbSummary}");

                // Warn if document context will be truncated for the AI. Truncation
                // happens silently inside TranslationPrompt.BuildSystemPrompt – without
                // this warning, users would have no way to know their middle-of-document
                // segments aren't visible to the AI, which can hurt terminology
                // consistency on long jobs.
                if (aiSettings.IncludeDocumentContext && docSegments != null)
                {
                    var maxDocSegs = aiSettings.DocumentContextMaxSegments > 0
                        ? aiSettings.DocumentContextMaxSegments : 500;
                    if (docSegments.Count > maxDocSegs)
                    {
                        int firstCount = (int)(maxDocSegs * 0.8);
                        int lastCount = maxDocSegs - firstCount;
                        int omitted = docSegments.Count - maxDocSegs;
                        batchControl.AppendLog(
                            $"⚠ Document context truncated: {docSegments.Count} segments " +
                            $"in document, but only {maxDocSegs} fit in the AI context window " +
                            $"(segments 1–{firstCount} and " +
                            $"{docSegments.Count - lastCount + 1}–{docSegments.Count} sent; " +
                            $"the middle {omitted} segments are omitted). " +
                            $"To send the whole document, raise “Max segments” in " +
                            $"Settings → AI Settings → AI Context.");
                    }
                }

                // Start backup TMX – written every 10 segments so translations survive a crash
                if (batchControl.IsTmxBackupEnabled)
                {
                    var backupPath = Settings.UserDataPath.BatchBackupFilePath(
                        DateTime.Now, TermLensEditorViewPart.GetCurrentProjectName());
                    _batchBackup = new BatchTranslationBackup(
                        backupPath, sourceLang, targetLang,
                        GetType().Assembly.GetName().Version?.ToString());
                    batchControl.AppendLog($"Backup TMX: {backupPath}");
                }

                // Warn-only monthly-budget pre-flight (advisory; never blocks).
                if (!Core.UsageBudget.Preflight(null, aiSettings, segments != null ? segments.Count : 0))
                    return;

                _batchCts = new CancellationTokenSource();
                _batchTranslator = new BatchTranslator();

                _batchTranslator.Progress += OnBatchProgress;
                _batchTranslator.SegmentTranslated += OnBatchSegmentTranslated;
                _batchTranslator.Completed += OnBatchCompleted;

                var ct = _batchCts.Token;

                // Warm the usage-attribution cache on the UI thread before the
                // background batch starts, so the off-thread usage logger reads the
                // cached snapshot instead of touching the Trados model off-thread.
                try { TermLensEditorViewPart.GetCurrentUsageContext(); } catch { }

                Task.Run(async () =>
                {
                    try
                    {
                        await _batchTranslator.TranslateAsync(
                            segments, sourceLang, targetLang,
                            aiSettings, termbaseTerms, batchSize, ct,
                            customPromptContent, customSystemPrompt,
                            docSegments, kbContext,
                            retryUntilComplete: batchControl.IsRetryEnabled);
                    }
                    catch (Exception ex)
                    {
                        SafeInvoke(() =>
                        {
                            batchControl.AppendLog($"Unexpected error: {ex.Message}", true);
                            batchControl.SetRunning(false);
                        });
                    }
                });
            });
        }

        private void OnOpenBackupFolderRequested(object sender, EventArgs e)
        {
            try
            {
                var dir = Settings.UserDataPath.BatchBackupsDir;
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", dir);
            }
            catch { }
        }

        private void OnBatchStopRequested(object sender, EventArgs e)
        {
            _batchCts?.Cancel();
            _proofreadCts?.Cancel();
            SafeInvoke(() => _control.Value.BatchTranslateControl.AppendLog("Cancellation requested..."));
        }

        private void OnBatchScopeChanged(object sender, EventArgs e)
        {
            UpdateBatchSegmentCounts();
        }

        private void OnBatchProgress(object sender, BatchProgressEventArgs e)
        {
            SafeInvoke(() =>
            {
                _control.Value.BatchTranslateControl.ReportProgress(e.Current, e.Total, e.Message, e.IsError);

                // Re-activate the Supervertaler Assistant pane at every batch
                // boundary. When ProcessSegmentPair writes a translation to a
                // segment, Trados' built-in Translation Results pane reacts to
                // the active-segment change and steals focus. Without this
                // counter-Activate, the user's Supervertaler Assistant tab
                // loses front position on every batch boundary.
                //
                // Trigger on both "Translating batch ..." (next batch starting)
                // AND "✓ Batch X complete" (this batch's writes just landed).
                // Trados' focus steal happens DURING/AFTER ProcessSegmentPair
                // writes, which fire just before "✓ Batch ... complete" is
                // logged. Activating only on next-batch-start was too late on
                // the last batch and on slow API runs where the steal landed
                // mid-gap and stuck. The synchronous Activate handles inline
                // steals; the deferred Activate (posted via BeginInvoke) wins
                // against steals queued for a later UI tick — same pattern as
                // OnNavigateToSegment.
                if (!string.IsNullOrEmpty(e.Message) &&
                    (e.Message.StartsWith("Translating batch ", StringComparison.Ordinal) ||
                     e.Message.StartsWith("Proofreading batch ", StringComparison.Ordinal) ||
                     e.Message.StartsWith("✓ Batch ", StringComparison.Ordinal)))
                {
                    try { Activate(); } catch { }
                    try
                    {
                        _control.Value.BeginInvoke((Action)(() =>
                        {
                            try { Activate(); } catch { }
                        }));
                    }
                    catch { /* control may not be available */ }
                }
            });
        }

        private void OnBatchSegmentTranslated(object sender, BatchSegmentResultEventArgs e)
        {
            // Run SYNCHRONOUSLY on the UI thread so e.WriteSucceeded is set
            // before BatchTranslator reads it back. SafeInvoke uses BeginInvoke
            // (asynchronous) and would return before the write attempt, leaving
            // the flag at its default true and the final completion summary
            // over-reporting success on write failures.
            void DoWrite()
            {
                try
                {
                    // Capture to avoid NullReferenceException if the user switches projects
                    // while batch translation is running (OnActiveDocumentChanged can null
                    // _activeDocument between the null check and ProcessSegmentPair).
                    var doc = _activeDocument;
                    if (e.SegmentPairRef == null || doc == null) { e.WriteSucceeded = false; return; }

                    // All segments now store ISegmentPair for ProcessSegmentPair.
                    // This avoids the editor buffer issue (Selection.Target.Replace
                    // loses changes) and ensures correct soft return handling for
                    // Excel/Visio segments with literal newlines.
                    var pair = e.SegmentPairRef as ISegmentPair;
                    if (pair == null) { e.WriteSucceeded = false; return; }

                    doc.ProcessSegmentPair(pair, "Supervertaler",
                        (sp, cancel) =>
                        {
                            // Tagged segments: reconstruct with full tag handling
                            if (e.HasTags && e.TagMap != null && e.TagMap.Count > 0)
                            {
                                bool reconstructed = SegmentTagHandler.ReconstructTarget(
                                    sp.Target, sp.Source, e.Translation, e.TagMap);

                                if (!reconstructed)
                                {
                                    // Fall back to plain text (strip placeholders)
                                    var plainTranslation = SegmentTagHandler.StripTagPlaceholders(e.Translation);
                                    var textTemplate = SegmentTagHandler.FindFirstText(sp.Source);
                                    if (textTemplate != null && !string.IsNullOrEmpty(plainTranslation))
                                    {
                                        sp.Target.Clear();
                                        var textClone = (IText)textTemplate.Clone();
                                        textClone.Properties.Text = plainTranslation;
                                        sp.Target.Add(textClone);
                                    }
                                }
                                return;
                            }

                            // Non-tagged segments: clone IText from source and set text.
                            // For segments with literal \n (Excel, Visio), the cloned IText
                            // preserves the text properties so Trados renders soft returns
                            // instead of paragraph marks.
                            var textTpl = SegmentTagHandler.FindFirstText(sp.Source);
                            if (textTpl != null && !string.IsNullOrEmpty(e.Translation))
                            {
                                sp.Target.Clear();
                                var textClone = (IText)textTpl.Clone();
                                textClone.Properties.Text = e.Translation;
                                sp.Target.Add(textClone);
                            }
                        });

                    // Back up to TMX regardless of tag complexity
                    _batchBackup?.AddSegment(e.SourceText, e.Translation);
                }
                catch (Exception ex)
                {
                    e.WriteSucceeded = false;
                    _control.Value.BatchTranslateControl.AppendLog(
                        $"Failed to write segment {e.SegmentIndex}: {ex.Message}", true);
                }
            }

            var ctrl = _control.Value;
            if (ctrl.InvokeRequired)
                ctrl.Invoke(new Action(DoWrite));
            else
                DoWrite();
        }

        private void OnBatchCompleted(object sender, BatchCompletedEventArgs e)
        {
            // Flush any remaining segments to the backup TMX before reporting completion
            var backup = _batchBackup;
            _batchBackup = null;
            backup?.Flush();

            SafeInvoke(() =>
            {
                _control.Value.BatchTranslateControl.ReportCompleted(
                    e.Translated, e.Failed, e.Skipped,
                    e.TotalTime, e.WasCancelled);

                if (backup != null && backup.Count > 0)
                {
                    _control.Value.BatchTranslateControl.AppendLog(
                        $"✓ Backup TMX saved: {backup.Count} segments → {backup.FilePath}");
                }

                // Update segment counts (some may now be filled)
                UpdateBatchSegmentCounts();

                // Persist the run's output. Batch writes land in the in-memory document,
                // so a finished run stays unsaved until Studio's AutoSave next fires or
                // the user saves by hand – a long run can complete and still be only in
                // memory minutes later. One save here closes that window.
                //
                // Saving after every batch was considered and rejected: Save() is
                // synchronous on the UI thread (the Studio API requires it), so it would
                // freeze Trados at every batch boundary to guard a gap that Studio's own
                // AutoSave and the backup TMX already cover between them. Once per run
                // costs one freeze and needs no throttling.
                //
                // Runs on cancellation too – segments written before the user stopped are
                // exactly the ones worth persisting.
                if (e.Translated > 0)
                {
                    var saveSw = System.Diagnostics.Stopwatch.StartNew();
                    var saveResult = BridgeSaveDocument();
                    saveSw.Stop();

                    if (saveResult != null && saveResult.Ok)
                    {
                        _control.Value.BatchTranslateControl.AppendLog(
                            $"✓ Project saved ({saveSw.Elapsed.TotalSeconds:F1}s)");
                    }
                    else
                    {
                        // Never fatal: the translations are in the document either way.
                        _control.Value.BatchTranslateControl.AppendLog(
                            "⚠ Could not save the project automatically: " +
                            (saveResult?.Error ?? "unknown error") +
                            " – your translations are in the document; save with Ctrl+S.",
                            true);
                    }
                }

                // Final counter-Activate for the last batch. The mid-run fix
                // in OnBatchProgress only fires while progress messages are
                // arriving; once the run ends, Trados' Translation Results
                // pane can still steal focus on the last segment write. Use
                // the same dual sync + deferred pattern as OnNavigateToSegment.
                try { Activate(); } catch { }
                try
                {
                    _control.Value.BeginInvoke((Action)(() =>
                    {
                        try { Activate(); } catch { }
                    }));
                }
                catch { /* control may not be available */ }
            });

            // Clean up
            if (_batchTranslator != null)
            {
                _batchTranslator.Progress -= OnBatchProgress;
                _batchTranslator.SegmentTranslated -= OnBatchSegmentTranslated;
                _batchTranslator.Completed -= OnBatchCompleted;
                _batchTranslator = null;
            }

            _batchCts?.Dispose();
            _batchCts = null;
        }

        // ─── Clipboard Mode ──────────────────────────────────────

        private void OnCopyToClipboardRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                var batchControl = _control.Value.BatchTranslateControl;

                if (_activeDocument == null)
                {
                    batchControl.AppendLog("No document open.", true);
                    return;
                }

                var sourceLang = GetDocumentSourceLanguage();
                var targetLang = GetDocumentTargetLanguage();

                if (string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
                {
                    batchControl.AppendLog("Cannot determine source/target language from document.", true);
                    return;
                }

                var aiSettings = _settings?.AiSettings;

                // Collect segments based on mode and scope
                List<BatchSegment> segments;
                if (batchControl.CurrentMode == BatchMode.Proofread)
                {
                    var proofScope = batchControl.GetSelectedProofreadScope();
                    segments = CollectProofreadSegments(proofScope);
                }
                else
                {
                    var scope = batchControl.GetSelectedScope();
                    segments = CollectSegments(scope);
                }

                if (segments.Count == 0)
                {
                    batchControl.AppendLog("No segments to copy.", true);
                    return;
                }

                // Apply the Limit spinner – same as the API batch path
                var clipLimit = batchControl.GetMaxSegments();
                if (clipLimit > 0 && segments.Count > clipLimit)
                    segments = segments.Take(clipLimit).ToList();

                // Get termbase terms (filtered by AI-disabled list)
                // Fallback-served MultiTerm termbases only answer per-segment lookups, so
                // terms for segments the user never visited would silently miss the prompt
                // (#38). Query them for this batch before assembling the term list.
                TermLensEditorViewPart.PrewarmFallbackTermsFor(segments.Select(sg => sg.SourceText));

                var allTerms = TermLensEditorViewPart.GetCurrentTermbaseTerms();
                var aiCfgB = aiSettings ?? new AiSettings();
                var termbaseTerms = allTerms.Where(t => aiCfgB.IsTermbaseAiEnabled(t.TermbaseId)).ToList();
                WarnIfNoAiTermbases(batchControl, allTerms.Count, termbaseTerms.Count);

                // Persist the prompt dropdown selection before resolving
                var selectedPromptPath = batchControl.GetSelectedPromptPath();
                if (aiSettings != null)
                    aiSettings.SelectedPromptPath = selectedPromptPath;
                SettingsService.Save();

                // Resolve custom prompt
                var customPromptContent = ResolveCustomPromptContent(sourceLang, targetLang);
                var customSystemPrompt = aiSettings?.CustomSystemPrompt;

                var includeTermMeta = aiSettings?.IncludeTermMetadata ?? true;
                var includeDocContext = aiSettings != null && aiSettings.IncludeDocumentContext;
                var maxDocSegs = aiSettings?.DocumentContextMaxSegments ?? 500;

                // Format for clipboard. Proofread mode uses the same full bilingual
                // document context the API path uses, so the clipboard text really
                // is "what would be sent to the AI". Translate mode keeps its
                // source-only document context (target text doesn't exist yet).
                string clipboardText;
                if (batchControl.CurrentMode == BatchMode.Proofread)
                {
                    var bilingualDocSegments = includeDocContext
                        ? CollectBilingualDocumentContext()
                        : null;

                    clipboardText = ClipboardRelay.FormatForProofreading(
                        segments, sourceLang, targetLang,
                        customPromptContent, termbaseTerms, customSystemPrompt,
                        bilingualDocSegments, includeTermMeta);
                }
                else
                {
                    List<string> docSegments = null;
                    if (includeDocContext)
                    {
                        var docCtx = CollectDocumentContext();
                        docSegments = docCtx.Item1;
                    }

                    clipboardText = ClipboardRelay.FormatForTranslation(
                        segments, sourceLang, targetLang,
                        customPromptContent, termbaseTerms, customSystemPrompt,
                        docSegments, maxDocSegs, includeTermMeta);
                }

                // Copy to clipboard
                System.Windows.Forms.Clipboard.SetText(clipboardText);

                // Store segments for paste
                _clipboardSegments = segments;

                // Enable paste button
                batchControl.EnablePasteButton(true);

                var mode = batchControl.CurrentMode == BatchMode.Proofread
                    ? "proofreading" : "translation";
                batchControl.AppendLog(
                    $"Copied {segments.Count} segments to clipboard for {mode}. " +
                    $"Paste into your LLM, then copy the response and click \u201cPaste from Clipboard\u201d.");
            });
        }

        /// <summary>
        /// Shows a read-only dialog with EXACTLY what would be sent to the AI for
        /// the current Batch Translate / Batch Proofread configuration. Reuses the
        /// same ClipboardRelay assembly the Copy-to-Clipboard path uses, so what
        /// the user sees in the preview is identical to what the LLM would receive.
        /// Does NOT trigger an actual API call.
        /// </summary>

        /// <summary>
        /// Extract the drawings, show each to a vision model with what the
        /// document says about it, diff the signs it reads against the signs the
        /// text cites, and write the lot to figures.md.
        ///
        /// <para>One action rather than four links. Extract, analyse, diff,
        /// write is one thing the user wants; five peer links read as five
        /// unrelated choices.</para>
        ///
        /// <para>The diff is the part worth having. A reference sign printed in
        /// the drawings with no basis in the description is an Art. 84 / Rule 42
        /// objection - on SEDA-026 that is ST 05, in Figures 13 and 14 and in no
        /// segment of the text. It exists only as pixels, so no amount of
        /// parsing reaches it.</para>
        ///
        /// <para>Writes for review. Nothing here goes straight into a prompt:
        /// figures.md is read into every request once it exists, so a
        /// confidently wrong caption would be invisible and everywhere.</para>
        /// </summary>
        /// <summary>
        /// 0 when idle, 1 while a figure analysis is running. Interlocked rather
        /// than a bool: the click arrives on the UI thread but the run finishes
        /// on a pool thread, and a second click while the first was on figure 11
        /// started a concurrent run - both finished, both wrote figures.md, 28
        /// paid requests instead of 14.
        /// </summary>
        private int _figureAnalysisRunning;

        private void OnAnalyseFiguresRequested(object sender, EventArgs e)
        {
            var batchControl = _control.Value.BatchTranslateControl;

            if (System.Threading.Interlocked.CompareExchange(
                    ref _figureAnalysisRunning, 1, 0) != 0)
            {
                batchControl.AppendLog(
                    "A figure analysis is already running - wait for it to finish.", true);
                return;
            }

            var started = false;
            try
            {

            var projectPath = TermLensEditorViewPart.GetCurrentProjectPath();
            if (string.IsNullOrEmpty(projectPath))
            {
                batchControl.AppendLog("No project open.", true);
                return;
            }

            string folder = "";
            try { folder = Settings.ProjectSettings.Load(projectPath)?.ReferenceImagesFolder ?? ""; }
            catch { }
            if (string.IsNullOrEmpty(folder))
            {
                batchControl.AppendLog(
                    "No reference images folder set - use the Reference images folder link first.", true);
                return;
            }

            var bankName = ActiveMemoryBankName;
            if (string.IsNullOrWhiteSpace(bankName))
            {
                batchControl.AppendLog("No memory bank is active.", true);
                return;
            }

            var anchorPath = ResolveProjectAnchorPathCore();
            if (string.IsNullOrEmpty(anchorPath))
            {
                batchControl.AppendLog("No project open.", true);
                return;
            }

            // Signs the TEXT cites, for the diff. Whole document, as the
            // numerals report does - a partial inventory would report absences
            // that are only absences from the part we looked at.
            var textSigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rawSourceText = "";
            if (_activeDocument != null)
            {
                var sources = new List<string>();
                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    var s = TermLensEditorViewPart.GetPlainText(pair?.Source);
                    if (!string.IsNullOrWhiteSpace(s)) sources.Add(s);
                }
                var inv = Core.NumeralInventory.Extract(sources);
                foreach (var n in inv.Numerals) textSigns.Add(n.ToString());
                foreach (var k in inv.LetterPoints.Keys) textSigns.Add(k);
                foreach (var k in inv.LabelSeries.Keys) textSigns.Add(k);

                // The inventory is deliberately narrow - parenthesised numerals,
                // (A) points, ST nn - so it does not pick up bare mentions. The
                // model reads whatever is printed on the drawing, so the diff
                // needs the raw text as well: "zone X" is in the description six
                // times, and X was reported as absent because it never appears
                // in brackets.
                rawSourceText = string.Join(" ", sources);
            }

            var docxFiles = FindProjectDocx(anchorPath);
            if (docxFiles.Count == 0)
            {
                batchControl.AppendLog("No Word documents found beside this project.", true);
                return;
            }

            // Count first, so the question can say what it will cost.
            var figureCount = 0;
            foreach (var f in docxFiles)
            {
                try { figureCount += Core.DocxImageExtractor.Extract(f).Images.Count; }
                catch { }
            }
            if (figureCount == 0)
            {
                batchControl.AppendLog("No images found in this project's Word documents.", true);
                return;
            }

            // Only ask when there is something to lose. figures.md is the file
            // the user is told to read and correct, and this replaces it.
            var existing = Path.Combine(UserDataPath.GetMemoryBankDir(bankName), "figures.md");
            if (File.Exists(existing))
            {
                var answer = MessageBox.Show(_control.Value.FindForm(),
                    "This will send " + figureCount + " image(s) to "
                        + (_settings?.AiSettings?.SelectedProvider ?? "the AI provider")
                        + " and REPLACE the existing figures.md in memory bank \""
                        + bankName + "\"." + "''' + N + N + '''"
                        + "Any corrections you have made to that file will be lost."
                        + "''' + N + N + '''Continue?",
                    "Analyse images",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                {
                    batchControl.AppendLog("Figure analysis cancelled - figures.md left alone.");
                    return;
                }
            }

            batchControl.AppendLog("Analysing " + figureCount
                + " image(s) - one AI request each. This may take a minute.");

            started = true;
            Task.Run(async () =>
            {
                try
                {
                    var visions = new List<Core.FigureVision>();
                    Core.DocxImageSet lastSet = null;
                    string lastDoc = null;

                    string clientError;
                    using (var client = CreateLlmClient(out clientError))
                    {
                        if (client == null)
                        {
                            SafeInvoke(() => batchControl.AppendLog(clientError, true));
                            return;
                        }

                        foreach (var f in docxFiles)
                        {
                            var set = Core.DocxImageExtractor.Extract(f, false, folder);
                            if (set.Images.Count == 0) continue;
                            lastSet = set; lastDoc = f;

                            for (int i = 0; i < set.Images.Count; i++)
                            {
                                var img = set.Images[i];
                                var file = i < set.SavedFiles.Count ? set.SavedFiles[i] : null;
                                if (file == null) continue;

                                var n = i + 1; var of = set.Images.Count;
                                SafeInvoke(() => batchControl.AppendLog(
                                    "  figure " + n + " of " + of + "\u2026"));

                                var v = await Core.FigureAnalyzer.AnalyseAsync(
                                    client,
                                    Path.Combine(folder, file),
                                    img.Label ?? ("image " + img.Ordinal),
                                    img.Descriptions).ConfigureAwait(false);
                                visions.Add(v);
                            }
                        }
                    }

                    if (visions.Count == 0)
                    {
                        SafeInvoke(() => batchControl.AppendLog("No figures to analyse.", true));
                        return;
                    }

                    var path = WriteFiguresWithVision(bankName, lastDoc, lastSet, visions, textSigns, rawSourceText);

                    SafeInvoke(() =>
                    {
                        var failed = visions.Count(v => !string.IsNullOrEmpty(v.Error));
                        batchControl.AppendLog("Figure analysis complete: " + visions.Count
                            + " figure(s)" + (failed > 0 ? ", " + failed + " failed" : "")
                            + ". Written to " + path);

                        var drawingsOnly = DrawingsOnlySigns(visions, textSigns, rawSourceText);
                        ShowSuperMemoryMessage(
                            "Analysed **" + visions.Count + "** figure(s) and wrote **figures.md** to "
                            + "memory bank **" + bankName + "**."
                            + (drawingsOnly.Count > 0
                                ? "\n\n\u26A0 **" + drawingsOnly.Count + " reference sign(s) appear in the "
                                  + "drawings but nowhere in the text:** " + string.Join(", ", drawingsOnly)
                                  + ". That is worth raising with the client before filing."
                                : "\n\nEvery sign read in the drawings also appears in the text.")
                            + "\n\n*This file is read into every prompt from now on. Read it first \u2014 a "
                            + "wrong caption would be invisible and everywhere.*");
                    });
                }
                catch (Exception ex)
                {
                    SafeInvoke(() => batchControl.AppendLog("Figure analysis failed: " + ex.Message, true));
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _figureAnalysisRunning, 0);
                }
            });
            }
            finally
            {
                // Released here only when the run never started - an early
                // return would otherwise leave the flag set for the session and
                // the button dead until Studio restarts.
                if (!started)
                    System.Threading.Interlocked.Exchange(ref _figureAnalysisRunning, 0);
            }
        }

        /// <summary>
        /// Build a client from the configured provider, or null with the reason
        /// in <paramref name="error"/>. Mirrors the resolution the batch and
        /// AutoPrompt paths do inline; the figure pass runs on a background
        /// thread and cannot put a message box up from there.
        /// </summary>
        private LlmClient CreateLlmClient(out string error)
        {
            error = null;
            var aiSettings = _settings?.AiSettings;
            if (aiSettings == null) { error = "AI settings not configured."; return null; }

            var provider = aiSettings.SelectedProvider ?? LlmModels.ProviderOpenAi;
            var model = aiSettings.GetSelectedModel();
            string apiKey;
            string baseUrl = null;

            if (provider == LlmModels.ProviderOllama)
            {
                apiKey = "ollama";
                baseUrl = aiSettings.OllamaEndpoint ?? "http://localhost:11434";
            }
            else if (provider == LlmModels.ProviderCustomOpenAi)
            {
                var profile = aiSettings.GetActiveCustomProfile();
                if (profile == null) { error = "No custom OpenAI profile configured."; return null; }
                apiKey = profile.ApiKey;
                baseUrl = profile.Endpoint;
                model = profile.Model;
            }
            else
            {
                apiKey = LlmClient.ResolveApiKey(provider, aiSettings.ApiKeys);
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                error = "No API key configured for " + provider
                      + ". Open Settings > AI Settings to add one.";
                return null;
            }

            return new LlmClient(provider, model, apiKey, baseUrl);
        }

        /// <summary>
        /// Write figures.md with what the document says AND what the model saw,
        /// followed by the diff between signs printed on the drawings and signs
        /// the text cites.
        ///
        /// <para>The diff section leads, because it is the finding: a reference
        /// sign in the drawings with no basis in the description is an Art. 84 /
        /// Rule 42 point, and it is the one thing here a reader cannot get any
        /// other way.</para>
        /// </summary>
        private string WriteFiguresWithVision(string bankName, string docPath,
            Core.DocxImageSet set, List<Core.FigureVision> visions, HashSet<string> textSigns,
            string rawSourceText)
        {
            var bankDir = UserDataPath.GetMemoryBankDir(bankName);
            Directory.CreateDirectory(bankDir);
            var outPath = Path.Combine(bankDir, "figures.md");

            // Did anything turn out to BE a figure? The heading follows that.
            var anyLabelled = set != null
                && set.Images.Any(i => !string.IsNullOrEmpty(i.Label));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# " + VisualNoun(anyLabelled, true, true));
            sb.AppendLine();
            sb.AppendLine("*Written by Supervertaler on "
                + DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                + " from " + (docPath == null ? "the project" : Path.GetFileName(docPath))
                + ", with the drawings examined by AI. Regenerate from Batch Operations "
                + "\u2192 Analyse figures.*");
            sb.AppendLine();

            // The finding first.
            var drawingsOnly = DrawingsOnlySigns(visions, textSigns, rawSourceText);
            sb.AppendLine("## Reference signs in the "
                        + VisualNoun(anyLabelled, true, false) + " but not in the text");
            sb.AppendLine();
            if (drawingsOnly.Count == 0)
            {
                sb.AppendLine("None. Every sign read in the drawings also appears in the "
                            + "description.");
            }
            else
            {
                sb.AppendLine("**" + string.Join(", ", drawingsOnly) + "**");
                sb.AppendLine();
                sb.AppendLine("A reference sign carried in the drawings with no basis in the "
                            + "description is an Art. 84 / Rule 42 point. Raise it with the "
                            + "client rather than inventing a description for it.");
                sb.AppendLine();
                sb.AppendLine("*Read off the drawings by an AI. Check each one against the "
                            + "image before relying on it.*");
            }
            sb.AppendLine();

            sb.AppendLine("## The " + VisualNoun(anyLabelled, true, false));
            sb.AppendLine();
            if (set != null && set.Method == Core.LabelingMethod.Ordinal)
            {
                sb.AppendLine("Image *N* carries figure *N*, checked for all "
                            + set.Images.Count + ".");
                sb.AppendLine();
            }

            var noun = VisualNoun(anyLabelled, false, false);
            sb.AppendLine("| " + VisualNoun(anyLabelled, false, true)
                        + " | File | What the document says | What the " + noun
                        + " shows | Signs on the " + noun + " |");
            sb.AppendLine("|---|---|---|---|---|");

            for (int i = 0; i < visions.Count; i++)
            {
                var v = visions[i];
                var img = (set != null && i < set.Images.Count) ? set.Images[i] : null;

                var said = "";
                if (img != null && img.Descriptions != null && img.Descriptions.Count > 0)
                    said = string.Join(" ", img.Descriptions);
                if (said.Length == 0) said = "\u2014";

                var saw = !string.IsNullOrEmpty(v.Error)
                    ? "*not analysed: " + v.Error + "*"
                    : (string.IsNullOrWhiteSpace(v.Caption) ? "\u2014" : v.Caption);

                var signs = v.SignsInDrawing.Count > 0
                    ? string.Join(", ", v.SignsInDrawing)
                    : "\u2014";

                sb.AppendLine("| " + Cell(v.Label) + " | " + Cell(v.FileName) + " | "
                            + Cell(said) + " | " + Cell(saw) + " | " + Cell(signs) + " |");
            }
            sb.AppendLine();

            var failed = visions.Count(x => !string.IsNullOrEmpty(x.Error));
            if (failed > 0)
            {
                sb.AppendLine("*" + failed + " figure(s) could not be analysed; their rows say why. "
                            + "They are listed rather than dropped, so the gap is visible.*");
                sb.AppendLine();
            }

            sb.AppendLine("## How to read this");
            sb.AppendLine();
            sb.AppendLine("\"What the document says\" is quoted from the source text and is exact. "
                        + "\"What the drawing shows\" and \"Signs on the drawing\" were produced by "
                        + "an AI looking at the image, and can be wrong. This file is read into every "
                        + "prompt, so correct anything that is wrong here rather than leaving it: a "
                        + "mistaken caption would otherwise be repeated into every request silently.");

            var text = sb.ToString()
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace("\n", "\r\n");
            File.WriteAllText(outPath, text, new System.Text.UTF8Encoding(false));
            return outPath;
        }

        /// <summary>Table-cell safe: pipes escaped, newlines flattened.</summary>
        private static string Cell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\u2014";
            return s.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
        }

        /// <summary>
        /// "Figures" or "Images", by what was actually identified. A document
        /// whose pictures carry no figure labels should not be handed a file
        /// headed "Figures" listing "Image 03".
        /// </summary>
        private static string VisualNoun(bool anyLabelled, bool plural, bool capital)
        {
            var word = anyLabelled
                ? (plural ? "figures" : "figure")
                : (plural ? "images" : "image");
            return capital ? char.ToUpperInvariant(word[0]) + word.Substring(1) : word;
        }

        /// <summary>Word documents beside the project - its folder and the one
        /// above, because a patent keeps its drawings next to the Studio folder.</summary>
        private List<string> FindProjectDocx(string anchorPath)
        {
            var found = new List<string>();
            try
            {
                var d = Path.GetDirectoryName(anchorPath);
                var dirs = new List<string>();
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) dirs.Add(d);
                var up = Path.GetDirectoryName(d);
                if (!string.IsNullOrEmpty(up) && Directory.Exists(up)
                    && !string.Equals(up, d, StringComparison.OrdinalIgnoreCase))
                    dirs.Add(up);

                foreach (var dir in dirs)
                    foreach (var f in Directory.GetFiles(dir, "*.docx", SearchOption.TopDirectoryOnly))
                    {
                        if (Path.GetFileName(f).StartsWith("~$")) continue;
                        if (!found.Contains(f)) found.Add(f);
                    }
            }
            catch { }
            return found;
        }

        /// <summary>Signs the model read in a drawing that the text never cites.
        /// The finding this whole feature exists to produce.</summary>
        private static List<string> DrawingsOnlySigns(
            List<Core.FigureVision> visions, HashSet<string> textSigns, string rawSourceText)
        {
            var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in visions)
                foreach (var s in v.SignsInDrawing)
                {
                    var sign = (s ?? "").Trim();
                    if (sign.Length == 0) continue;
                    if (textSigns.Contains(sign)) continue;
                    if (AppearsInText(rawSourceText, sign)) continue;
                    seen.Add(sign);
                }
            return seen.ToList();
        }

        /// <summary>
        /// Does this sign appear anywhere in the source text as a whole word?
        ///
        /// <para>The second gate on the diff, and it exists because of a real
        /// false positive: the model read X off Figures 12-14, the inventory had
        /// no X because the text writes "zone X" rather than "(X)", and the
        /// report accused the drawings of carrying an uncited sign. Sending a
        /// translator to their client over a sign that is in the description six
        /// times is worse than missing one.</para>
        ///
        /// <para>It errs towards suppressing. A sign present anywhere in the
        /// text is not reported, which can hide a genuine case where the letter
        /// occurs coincidentally - but a false accusation costs more than a
        /// missed hint in a list the file already tells you to verify.</para>
        /// </summary>
        private static bool AppearsInText(string rawSourceText, string sign)
        {
            if (string.IsNullOrEmpty(rawSourceText) || string.IsNullOrEmpty(sign)) return false;
            try
            {
                return System.Text.RegularExpressions.Regex.IsMatch(
                    rawSourceText,
                    @"(?<![\w])" + System.Text.RegularExpressions.Regex.Escape(sign) + @"(?![\w])",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Extract the project's document images into the reference images
        /// folder, named for the figure each one is.
        ///
        /// <para>Until this existed the folder setting and the inventory never
        /// met: the plugin knew image 3 was FIG. 3 and had no way to put a file
        /// anywhere, so the extraction was done by hand. That manual step is
        /// what the feature exists to remove.</para>
        ///
        /// <para>When more than one document carries images each gets its own
        /// sub-folder, because "Figure 01.png" from two documents is the same
        /// name and the second would silently replace the first.</para>
        /// </summary>
        private void OnExtractImagesRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                var batchControl = _control.Value.BatchTranslateControl;

                var projectPath = TermLensEditorViewPart.GetCurrentProjectPath();
                if (string.IsNullOrEmpty(projectPath))
                {
                    batchControl.AppendLog("No project open.", true);
                    return;
                }

                string folder = "";
                try { folder = Settings.ProjectSettings.Load(projectPath)?.ReferenceImagesFolder ?? ""; }
                catch { }

                if (string.IsNullOrEmpty(folder))
                {
                    batchControl.AppendLog(
                        "No reference images folder set - use the Reference images folder link first.",
                        true);
                    return;
                }

                var anchorPath = ResolveProjectAnchorPathCore();
                if (string.IsNullOrEmpty(anchorPath))
                {
                    batchControl.AppendLog("No project open.", true);
                    return;
                }

                var docxFiles = new List<string>();
                try
                {
                    var d = Path.GetDirectoryName(anchorPath);
                    var dirs = new List<string>();
                    if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) dirs.Add(d);
                    var up = Path.GetDirectoryName(d);
                    if (!string.IsNullOrEmpty(up) && Directory.Exists(up)
                        && !string.Equals(up, d, StringComparison.OrdinalIgnoreCase))
                        dirs.Add(up);

                    foreach (var dir in dirs)
                        foreach (var f in Directory.GetFiles(dir, "*.docx", SearchOption.TopDirectoryOnly))
                        {
                            if (Path.GetFileName(f).StartsWith("~$")) continue;
                            if (!docxFiles.Contains(f)) docxFiles.Add(f);
                        }
                }
                catch { }

                // Which documents actually carry images? Counting first decides
                // whether one folder is enough or each needs its own.
                var withImages = new List<string>();
                foreach (var f in docxFiles)
                {
                    try { if (Core.DocxImageExtractor.Extract(f).Images.Count > 0) withImages.Add(f); }
                    catch { }
                }

                if (withImages.Count == 0)
                {
                    batchControl.AppendLog(
                        "No images found in this project's Word documents.", true);
                    return;
                }

                var total = 0;
                var lines = new List<string>();
                foreach (var f in withImages)
                {
                    var target = withImages.Count == 1
                        ? folder
                        : Path.Combine(folder, Path.GetFileNameWithoutExtension(f));

                    var set = Core.DocxImageExtractor.Extract(f, false, target);
                    total += set.SavedFiles.Count;

                    lines.Add("**" + Path.GetFileName(f) + "** \u2192 "
                        + set.SavedFiles.Count + " file(s)"
                        + (withImages.Count > 1
                            ? " in `" + Path.GetFileName(target) + "`" : "")
                        + (set.Method == Core.LabelingMethod.Refused
                            ? " \u2014 named by position, not by figure: the labels could not be checked"
                            : ""));
                }

                batchControl.AppendLog("Extracted " + total + " image(s) to " + folder + ".");

                ShowSuperMemoryMessage(
                    "Extracted **" + total + "** image(s) to:\n`" + folder + "`\n\n"
                    + string.Join("\n", lines)
                    + "\n\nNamed for the figure each one is, zero-padded so they sort. "
                    + "Re-running overwrites them.");
            });
        }

        /// <summary>
        /// Write the figure inventory to <c>figures.md</c> at the active memory
        /// bank's root, where every prompt reads it.
        ///
        /// <para>Not <c>reference/</c>: that is the audit trail and
        /// MemoryBankReader never reads it into a prompt, so a figure inventory
        /// saved there is invisible to the model - which is the one thing it
        /// exists for. The bank root is globbed for <c>*.md</c>, so this file
        /// reaches every request.</para>
        ///
        /// <para>Worth having before any vision pass exists: what the document
        /// says each figure shows is real context on its own. The column that
        /// needs a model to look at the drawings is named as missing rather than
        /// left to look complete.</para>
        /// </summary>
        private void OnWriteFiguresFileRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                var batchControl = _control.Value.BatchTranslateControl;

                var bankName = ActiveMemoryBankName;
                if (string.IsNullOrWhiteSpace(bankName))
                {
                    batchControl.AppendLog("No memory bank is active.", true);
                    return;
                }

                var bankDir = UserDataPath.GetMemoryBankDir(bankName);
                if (string.IsNullOrEmpty(bankDir) || !Directory.Exists(bankDir))
                {
                    batchControl.AppendLog("Memory bank folder not found: " + bankDir, true);
                    return;
                }

                var anchorPath = ResolveProjectAnchorPathCore();
                if (string.IsNullOrEmpty(anchorPath))
                {
                    batchControl.AppendLog("No project open.", true);
                    return;
                }

                // Same sweep as the Document images report: the project folder
                // and its parent, because a patent keeps its drawings beside the
                // Studio folder rather than inside it.
                var docxFiles = new List<string>();
                try
                {
                    var d = Path.GetDirectoryName(anchorPath);
                    var dirs = new List<string>();
                    if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) dirs.Add(d);
                    var up = Path.GetDirectoryName(d);
                    if (!string.IsNullOrEmpty(up) && Directory.Exists(up)
                        && !string.Equals(up, d, StringComparison.OrdinalIgnoreCase))
                        dirs.Add(up);

                    foreach (var dir in dirs)
                        foreach (var f in Directory.GetFiles(dir, "*.docx", SearchOption.TopDirectoryOnly))
                        {
                            if (Path.GetFileName(f).StartsWith("~$")) continue;
                            if (!docxFiles.Contains(f)) docxFiles.Add(f);
                        }
                }
                catch { }

                // Decided after the sweep below, so the heading can follow what
                // the documents turned out to contain.
                var anyLabelled = false;
                foreach (var f in docxFiles)
                {
                    try
                    {
                        if (Core.DocxImageExtractor.Extract(f).Images
                                .Any(i => !string.IsNullOrEmpty(i.Label)))
                        { anyLabelled = true; break; }
                    }
                    catch { }
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# " + VisualNoun(anyLabelled, true, true));
                sb.AppendLine();
                sb.AppendLine("*Written by Supervertaler on "
                    + DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                    + ". Regenerate from Batch Operations \u2192 Write figures.md.*");
                sb.AppendLine();

                var wrote = 0;
                var refused = 0;

                foreach (var f in docxFiles)
                {
                    var set = Core.DocxImageExtractor.Extract(f);
                    if (set.Images.Count == 0) continue;

                    sb.AppendLine("## " + Path.GetFileName(f));
                    sb.AppendLine();

                    if (set.Method == Core.LabelingMethod.Refused)
                    {
                        refused++;
                        sb.AppendLine("**Figure labels could not be established.** " + set.Warning);
                        sb.AppendLine();
                        sb.AppendLine("The images are listed in document order, unlabelled. Do not "
                                    + "assume image *N* is figure *N* here.");
                        sb.AppendLine();
                    }
                    else if (set.Method == Core.LabelingMethod.Ordinal)
                    {
                        sb.AppendLine("Image *N* carries figure *N*, checked for all "
                                    + set.Images.Count + ".");
                        sb.AppendLine();
                    }

                    sb.AppendLine("| " + VisualNoun(anyLabelled, false, true)
                                + " | Source part | What the document says it shows |");
                    sb.AppendLine("|---|---|---|");
                    foreach (var img in set.Images)
                    {
                        // Not truncated: the chat table cuts at 110 characters
                        // because a docked panel is narrow. This file is read by
                        // the model and by a Markdown previewer, and both want
                        // the whole sentence.
                        var desc = "";
                        if (img.Descriptions != null && img.Descriptions.Count > 0)
                            desc = string.Join(" ", img.Descriptions);
                        if (string.IsNullOrWhiteSpace(desc)) desc = "\u2014";
                        desc = desc.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();

                        var part = (img.PartName ?? "").Replace("/word/", "").Replace("|", "\\|");

                        sb.AppendLine("| " + (img.Label ?? ("image " + img.Ordinal))
                            + " | " + part + " | " + desc + " |");
                        wrote++;
                    }
                    sb.AppendLine();
                }

                if (wrote == 0)
                {
                    batchControl.AppendLog(
                        "No images found in this project's Word documents - nothing written.", true);
                    return;
                }

                sb.AppendLine("## What is not here");
                sb.AppendLine();
                sb.AppendLine("What each drawing **actually shows** \u2014 the parts visible in it, and "
                            + "any reference sign printed on the drawing but absent from the text \u2014 "
                            + "is not in this file. Establishing that needs a pass that looks at the "
                            + "images, which does not exist yet (issue #69). Everything above comes "
                            + "from the document's own text.");

                var outPath = Path.Combine(bankDir, "figures.md");
                try
                {
                    // CRLF throughout: AppendLine emits CRLF but the text it wraps
                    // arrives with bare LF, and a mixed file makes Markdown editors
                    // complain. Same reasoning as the chat-save writer.
                    var text = sb.ToString()
                        .Replace("\r\n", "\n")
                        .Replace("\r", "\n")
                        .Replace("\n", "\r\n");
                    File.WriteAllText(outPath, text, new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    batchControl.AppendLog("Could not write figures.md: " + ex.Message, true);
                    return;
                }

                batchControl.AppendLog("figures.md written to memory bank " + bankName
                    + " (" + wrote + " figure(s)"
                    + (refused > 0 ? ", " + refused + " document(s) unlabelled" : "")
                    + "). It is read into every prompt.");

                ShowSuperMemoryMessage(
                    "Wrote **figures.md** to memory bank **" + bankName + "** \u2014 "
                    + wrote + " figure(s).\n\nUnlike a chat save, this sits at the bank root, "
                    + "so it is read into every prompt.");
            });
        }

        /// <summary>
        /// Choose the folder holding this project's drawings, and remember it
        /// against the open Trados project.
        ///
        /// <para>The same setting lives in Settings &gt; Library on a memory bank
        /// node. Two sessions running failed to find it there and one reported it
        /// as not existing, so it is reachable from the tab that actually
        /// consumes it as well.</para>
        /// </summary>
        private void OnReferenceImagesFolderRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                var batchControl = _control.Value.BatchTranslateControl;

                var projectPath = TermLensEditorViewPart.GetCurrentProjectPath();
                if (string.IsNullOrEmpty(projectPath))
                {
                    batchControl.AppendLog(
                        "No project open - there is nothing to attach a folder to.", true);
                    return;
                }

                string current = "";
                try { current = Settings.ProjectSettings.Load(projectPath)?.ReferenceImagesFolder ?? ""; }
                catch { }

                // Start where the drawings usually are: beside the project rather
                // than inside the Studio folder.
                var start = current;
                if (string.IsNullOrEmpty(start))
                {
                    try
                    {
                        var suggestions = Core.ReferenceImages.Suggest(projectPath);
                        if (suggestions != null && suggestions.Count > 0) start = suggestions[0];
                    }
                    catch { }
                }

                // FolderPicker, not FolderBrowserDialog: the latter is the
                // Windows 2000-era tree with nowhere to paste a path.
                var chosen = Controls.FolderPicker.Show(
                    _control.Value.FindForm(),
                    "Choose the folder holding this project's drawings",
                    start);
                if (string.IsNullOrEmpty(chosen)) return;

                var found = 0;
                try { found = Core.ReferenceImages.List(chosen)?.Count ?? 0; }
                catch { }

                if (found == 0)
                {
                    var go = MessageBox.Show(_control.Value.FindForm(),
                        "No images found in" + "\n\n" + chosen + "\n\nUse it anyway?",
                        "Reference images",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2);
                    if (go != DialogResult.Yes) return;
                }

                try
                {
                    var ps = Settings.ProjectSettings.Load(projectPath) ?? new Settings.ProjectSettings();
                    ps.ReferenceImagesFolder = chosen;
                    if (string.IsNullOrEmpty(ps.ProjectPath)) ps.ProjectPath = projectPath;
                    if (string.IsNullOrEmpty(ps.ProjectName))
                        ps.ProjectName = TermLensEditorViewPart.GetCurrentProjectName() ?? "";
                    Settings.ProjectSettings.Save(projectPath, ps);
                }
                catch (Exception ex)
                {
                    batchControl.AppendLog("Could not save the folder: " + ex.Message, true);
                    return;
                }

                batchControl.AppendLog("Reference images folder set: " + chosen
                    + " (" + found + " image file(s)).");
            });
        }

        /// <summary>
        /// Reports the images in this project's Word documents, each with any
        /// figure label and the text it sits among, plus the reference-images
        /// folder if one is set. No AI call.
        ///
        /// <para>It scans the project folder AND its parent, which is not
        /// tidiness. A patent job keeps the drawings beside the Studio folder,
        /// in a separate "Figures as filed" document: the file being translated
        /// has no images in it at all. A report that looked only at the active
        /// file would say "no images" on exactly the documents this feature
        /// exists for.</para>
        /// </summary>
        private void OnDocumentImagesRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                var batchControl = _control.Value.BatchTranslateControl;

                var anchorPath = ResolveProjectAnchorPathCore();
                if (string.IsNullOrEmpty(anchorPath))
                {
                    batchControl.AppendLog("No project open.", true);
                    return;
                }

                // The project folder and one level up. Deduplicated, because a
                // single-file project can have both resolve to the same place.
                var dirs = new List<string>();
                try
                {
                    var d = Path.GetDirectoryName(anchorPath);
                    if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) dirs.Add(d);
                    var up = Path.GetDirectoryName(d);
                    if (!string.IsNullOrEmpty(up) && Directory.Exists(up)
                        && !string.Equals(up, d, StringComparison.OrdinalIgnoreCase))
                        dirs.Add(up);
                }
                catch { }

                var docxFiles = new List<string>();
                foreach (var d in dirs)
                {
                    try
                    {
                        foreach (var f in Directory.GetFiles(d, "*.docx", SearchOption.TopDirectoryOnly))
                        {
                            // Word's lock files are not documents.
                            if (Path.GetFileName(f).StartsWith("~$")) continue;
                            if (!docxFiles.Contains(f)) docxFiles.Add(f);
                        }
                    }
                    catch { }
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("## Document images");
                sb.AppendLine();

                int totalImages = 0, totalLabelled = 0, totalAnchored = 0;

                if (docxFiles.Count == 0)
                {
                    sb.AppendLine("No Word documents found beside this project.");
                    sb.AppendLine();
                }

                foreach (var f in docxFiles)
                {
                    var set = Core.DocxImageExtractor.Extract(f);
                    var images = set.Images;
                    var labelled = images.Count(i => !string.IsNullOrEmpty(i.Label));
                    var anchored = images.Count(i => !string.IsNullOrWhiteSpace(i.Anchor));
                    var described = images.Count(i => i.Descriptions != null && i.Descriptions.Count > 0);
                    totalImages += images.Count;
                    totalLabelled += labelled;
                    totalAnchored += anchored;

                    sb.AppendLine("### " + Path.GetFileName(f));
                    sb.AppendLine();

                    if (images.Count == 0)
                    {
                        sb.AppendLine("No images.");
                        sb.AppendLine();
                        continue;
                    }

                    sb.AppendLine("**" + images.Count + " image(s)** \u2013 "
                        + labelled + " with a figure label, "
                        + described + " with a description in the text, "
                        + anchored + " with surrounding text.");
                    sb.AppendLine();

                    // Say how the labels were arrived at. The old report could
                    // not, and so announced success over four figures collapsed
                    // onto FIG. 3.
                    if (set.Method == Core.LabelingMethod.Ordinal)
                    {
                        sb.AppendLine("Labels paired by position and checked: image *N* carries "
                                    + "figure *N*, verified for all " + images.Count + ".");
                    }
                    else if (set.Method == Core.LabelingMethod.Refused)
                    {
                        sb.AppendLine("> \u26A0 **Labels withheld.** " + set.Warning);
                    }
                    else if (set.Method == Core.LabelingMethod.Proximity)
                    {
                        sb.AppendLine("*Labels taken from nearby text \u2013 right for captioned "
                                    + "inline images, a guess on a document of plates.*");
                    }
                    sb.AppendLine();

                    // A list, not a table. This goes to a narrow docked panel;
                    // figures.md keeps the table because it is read full width.
                    foreach (var img in images)
                    {
                        // The description matched by figure number, not the text
                        // the image happens to sit among. On a patent the latter
                        // is the plate label and some empty paragraphs; the
                        // former is hundreds of paragraphs away and is the point.
                        // Prefer the longest: a patent carries a short entry in
                        // the figure list and a longer one in the detailed
                        // description, and the longer one names the parts.
                        string cell = null;
                        // Counted here, appended AFTER truncation: adding it now
                        // loses it on exactly the long rows that have more than
                        // one description, which is all of figures 8-14.
                        int extra = 0;
                        if (img.Descriptions != null && img.Descriptions.Count > 0)
                        {
                            foreach (var d in img.Descriptions)
                                if (cell == null || d.Length > cell.Length) cell = d;
                            extra = img.Descriptions.Count - 1;
                        }

                        if (string.IsNullOrWhiteSpace(cell))
                        {
                            cell = (img.Anchor ?? "").Trim();
                            if (cell.Length > 0) cell = "*sits among:* " + cell;
                        }

                        cell = (cell ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                        if (cell.Length > 110) cell = cell.Substring(0, 107) + "\u2026";
                        if (cell.Length == 0) cell = "*(nothing found)*";
                        cell = cell.Replace("|", "\\|");
                        if (extra > 0) cell += " *(+" + extra + " more)*";

                        var head = img.Label ?? ("image " + img.Ordinal);
                        var part = (img.PartName ?? "").Replace("/word/media/", "");
                        sb.AppendLine("**" + head + "**"
                            + (part.Length > 0 ? "  \u00b7  " + part : ""));
                        sb.AppendLine();
                        sb.AppendLine(cell);
                        sb.AppendLine();
                    }
                    sb.AppendLine();
                }

                // The folder half of the picture.
                string folder = "";
                try
                {
                    var projectPath = TermLensEditorViewPart.GetCurrentProjectPath();
                    if (!string.IsNullOrEmpty(projectPath))
                        folder = Settings.ProjectSettings.Load(projectPath)?.ReferenceImagesFolder ?? "";
                }
                catch { }

                sb.AppendLine("### Reference images folder");
                sb.AppendLine();
                if (string.IsNullOrEmpty(folder))
                {
                    // The row is real, but it only appears once a bank node or a
                    // figures.md is SELECTED - so naming the path alone reads as a
                    // dead end to anyone who opens Library and sees prompt folders.
                    sb.AppendLine("Not set for this project. Use the "
                                + "**Reference images folder** link on the Batch Operations "
                                + "tab, just below this report's button. Remembered per Trados "
                                + "project. (It is also on a memory bank in Settings > Library, "
                                + "but that route is easy to miss.)");
                }
                else
                {
                    var listed = Core.ReferenceImages.List(folder);
                    sb.AppendLine("`" + folder + "` \u2013 " + listed.Count + " image file(s).");
                }
                sb.AppendLine();

                // What is here, and what is still missing to use it. NOT a
                // recommendation: the shape of a job varies per client and per
                // document, and one sample is not a rule.
                sb.AppendLine("### What this means");
                sb.AppendLine();
                if (totalImages == 0)
                {
                    sb.AppendLine("Nothing visual was found in these Word files. If the job has "
                                + "drawings, they are somewhere this does not look yet \u2013 a PDF, a "
                                + "file elsewhere, or a document type not read here. Point the "
                                + "Reference images folder at them so they are at least on record.");
                }
                else
                {
                    sb.AppendLine("Found **" + totalImages + "** image(s): "
                        + totalLabelled + " carry a figure label in the text, "
                        + totalAnchored + " sit among text that could anchor them.");
                    sb.AppendLine();

                    if (totalLabelled < totalImages)
                    {
                        sb.AppendLine("- " + (totalImages - totalLabelled) + " have no label in the "
                                    + "document. Where the number is drawn inside the picture, only "
                                    + "looking at the image can recover it.");
                    }
                    if (totalAnchored < totalImages)
                    {
                        sb.AppendLine("- " + (totalImages - totalAnchored) + " have no surrounding "
                                    + "text, so nothing ties them to a place in the translation.");
                    }
                    if (totalLabelled == totalImages && totalAnchored == totalImages
                        && totalImages > 0)
                    {
                        sb.AppendLine("- Every image has both a label and surrounding text.");
                    }
                }
                sb.AppendLine();
                sb.AppendLine("*None of this reaches the AI yet. Getting any visual in a document "
                            + "through to the model \u2013 labelled or not, anchored or not \u2013 is issue "
                            + "#69.*");

                var markdown = sb.ToString().TrimEnd();

                _control.Value.SwitchToChatTab();
                _chatHistory.Add(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = markdown
                });
                _control.Value.AddMessage(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = markdown
                });
                SaveChatHistory();

                batchControl.AppendLog("Document images: " + totalImages
                    + " across " + docxFiles.Count + " document(s) - see the Chat tab.");
            });
        }

        /// <summary>
        /// Lists every parenthesised reference numeral in the open document,
        /// with the sentences citing each, and posts the result into the chat.
        /// Makes no AI call.
        ///
        /// <para>Passes a NULL reconciliation, deliberately, rather than an
        /// empty one. <c>NumeralInventory.Reconcile</c> given no drawings puts
        /// every numeral into <c>TextOnly</c>, and the formatter then reports
        /// the lot as "cited in the text but not found in the drawings" - a
        /// confident finding about drawings that nothing has ever looked at.
        /// Null omits that section, which is the truth: no vision pass exists
        /// yet. See issue #69.</para>
        /// </summary>
        private void OnReferenceNumeralsRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                var batchControl = _control.Value.BatchTranslateControl;

                if (_activeDocument == null)
                {
                    batchControl.AppendLog("No document open.", true);
                    return;
                }

                // The whole document, never the batch Scope. An inventory of
                // part of a patent is worse than no inventory, because it reads
                // as complete - it would report "34 distinct numerals" having
                // looked at a third of the file.
                var sources = new List<string>();
                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    var text = TermLensEditorViewPart.GetPlainText(pair?.Source);
                    if (!string.IsNullOrWhiteSpace(text)) sources.Add(text);
                }

                var report = Core.NumeralInventory.Extract(sources);
                var markdown = Core.NumeralInventory.Format(report, null);

                markdown += "\n\n*Scanned all " + sources.Count
                          + " segments of the open document. A reference numeral here means a "
                          + "1-3 digit number in parentheses, such as (12). Nothing has examined "
                          + "the drawings themselves, so this says what the text cites, not what "
                          + "the figures contain.*";

                // Switch BEFORE adding. A bubble measured while its TabPage is
                // unselected sees Visible == false all the way up the parent
                // chain, and the "Show full response" link gets no height and no
                // position - so a truncated report simply stopped mid-table with
                // no way to see the rest.
                _control.Value.SwitchToChatTab();

                // Into the history as well as onto the screen. Without this the
                // report is visible to the user but invisible to the assistant -
                // so "which numerals are only cited once?" could not be answered
                // about a table sitting right there - and it vanishes on restart.
                _chatHistory.Add(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = markdown
                });
                _control.Value.AddMessage(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = markdown
                });
                SaveChatHistory();

                batchControl.AppendLog(
                    report.HasAny
                        ? "Reference numerals: " + report.Citations.Count
                          + " distinct numerals across " + sources.Count
                          + " segments - see the Chat tab."
                        : "Reference numerals: none found in this document.");
            });
        }

        private void OnPreviewPromptRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                var batchControl = _control.Value.BatchTranslateControl;

                if (_activeDocument == null)
                {
                    batchControl.AppendLog("No document open.", true);
                    return;
                }

                var sourceLang = GetDocumentSourceLanguage();
                var targetLang = GetDocumentTargetLanguage();

                if (string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
                {
                    batchControl.AppendLog("Cannot determine source/target language from document.", true);
                    return;
                }

                var aiSettings = _settings?.AiSettings;

                // Collect segments based on mode and scope
                List<BatchSegment> segments;
                if (batchControl.CurrentMode == BatchMode.Proofread)
                {
                    var proofScope = batchControl.GetSelectedProofreadScope();
                    segments = CollectProofreadSegments(proofScope);
                }
                else
                {
                    var scope = batchControl.GetSelectedScope();
                    segments = CollectSegments(scope);
                }

                if (segments.Count == 0)
                {
                    batchControl.AppendLog("No segments matched the current scope.", true);
                    return;
                }

                // Apply the Limit spinner so the preview reflects what would actually be sent
                var clipLimit = batchControl.GetMaxSegments();
                if (clipLimit > 0 && segments.Count > clipLimit)
                    segments = segments.Take(clipLimit).ToList();

                // Termbase terms, prompt path, custom prompt \u2014 same flow as the copy path
                // Fallback-served MultiTerm termbases only answer per-segment lookups, so
                // terms for segments the user never visited would silently miss the prompt
                // (#38). Query them for this batch before assembling the term list.
                TermLensEditorViewPart.PrewarmFallbackTermsFor(segments.Select(sg => sg.SourceText));

                var allTerms = TermLensEditorViewPart.GetCurrentTermbaseTerms();
                var aiCfgB = aiSettings ?? new AiSettings();
                var termbaseTerms = allTerms.Where(t => aiCfgB.IsTermbaseAiEnabled(t.TermbaseId)).ToList();
                WarnIfNoAiTermbases(batchControl, allTerms.Count, termbaseTerms.Count);

                var selectedPromptPath = batchControl.GetSelectedPromptPath();
                if (aiSettings != null)
                    aiSettings.SelectedPromptPath = selectedPromptPath;
                SettingsService.Save();

                var customPromptContent = ResolveCustomPromptContent(sourceLang, targetLang);
                var customSystemPrompt = aiSettings?.CustomSystemPrompt;

                var includeTermMeta = aiSettings?.IncludeTermMetadata ?? true;
                var includeDocContext = aiSettings != null && aiSettings.IncludeDocumentContext;
                var maxDocSegs = aiSettings?.DocumentContextMaxSegments ?? 500;

                string promptText;
                if (batchControl.CurrentMode == BatchMode.Proofread)
                {
                    var bilingualDocSegments = includeDocContext
                        ? CollectBilingualDocumentContext()
                        : null;

                    promptText = ClipboardRelay.FormatForProofreading(
                        segments, sourceLang, targetLang,
                        customPromptContent, termbaseTerms, customSystemPrompt,
                        bilingualDocSegments, includeTermMeta);
                }
                else
                {
                    List<string> docSegments = null;
                    if (includeDocContext)
                    {
                        var docCtx = CollectDocumentContext();
                        docSegments = docCtx.Item1;
                    }

                    promptText = ClipboardRelay.FormatForTranslation(
                        segments, sourceLang, targetLang,
                        customPromptContent, termbaseTerms, customSystemPrompt,
                        docSegments, maxDocSegs, includeTermMeta);
                }

                var modeLabel = batchControl.CurrentMode == BatchMode.Proofread
                    ? "proofreading" : "translation";
                var title = $"Prompt preview \u2013 {modeLabel} ({segments.Count} segments)";
                var headerText = "This is exactly what will be sent to the AI for this batch: " +
                    "the assembled system prompt (including the active custom prompt, termbase entries, " +
                    "language-specific checks, and the full bilingual document context for proofread), " +
                    "followed by the numbered segment list. No LLM call is made by this preview.";

                using (var dlg = new Controls.PromptPreviewDialog(title, headerText, promptText))
                {
                    dlg.ShowDialog();
                }
            });
        }

        // Guards the clipboard paste-back writeback against re-entrancy: like the
        // re-import loop it pumps the message queue (Application.DoEvents) so the
        // Cancel button and the editor grid stay live, which means a second paste
        // could otherwise arrive while one is still running.
        private bool _clipboardPasteInProgress;

        private void OnPasteFromClipboardRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                var batchControl = _control.Value.BatchTranslateControl;

                if (_clipboardSegments == null || _clipboardSegments.Count == 0)
                {
                    batchControl.AppendLog("No segments pending \u2013 click \u201cCopy to Clipboard\u201d first.", true);
                    return;
                }

                if (_activeDocument == null)
                {
                    batchControl.AppendLog("No document open.", true);
                    return;
                }

                if (_clipboardPasteInProgress)
                {
                    batchControl.AppendLog("A clipboard paste is already running \u2013 please wait for it to finish.", true);
                    return;
                }

                var text = System.Windows.Forms.Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text))
                {
                    batchControl.AppendLog("Clipboard is empty \u2013 copy the LLM response first.", true);
                    return;
                }

                var targetLang = GetDocumentTargetLanguage();

                if (batchControl.CurrentMode == BatchMode.Translate)
                {
                    // Parse translations
                    var parsed = ClipboardRelay.ParseTranslationResponse(
                        text, _clipboardSegments.Count, targetLang, GetDocumentSourceLanguage());

                    if (parsed.Count == 0)
                    {
                        batchControl.AppendLog(
                            "Could not parse any translations from the clipboard. " +
                            "Make sure the LLM response uses the numbered segment format.", true);
                        return;
                    }

                    // Write translations back to Trados. This mutates the live
                    // Trados document one segment at a time on the UI thread; on
                    // 32-bit Trados Studio 2024 a large paste-back would spike
                    // memory and crash (reported: RAM ~1.8 GB, grid looping,
                    // crash). We use the same safe writeback cadence as the
                    // bilingual re-import: a progress window + Cancel, message
                    // pumping so the editor grid stays live, and the 32-bit
                    // memory watchdog (compact on the soft limit, stop gracefully
                    // on the hard limit). All a no-op on 64-bit Studio 2026.
                    int success = 0;
                    int failed = 0;
                    int tagWarnings = 0;
                    int processed = 0;
                    bool cancelled = false, stoppedForMemory = false;

                    var progress = new Controls.ReimportProgressForm(
                        "Paste from Clipboard",
                        "Writing translations back into Trados…",
                        parsed.Count);
                    progress.CancelRequested += (s2, e2) => cancelled = true;

                    _clipboardPasteInProgress = true;
                    batchControl.EnablePasteButton(false);
                    try
                    {
                        try { progress.Show(_control.Value); progress.BringToFront(); } catch { }

                        foreach (var pt in parsed)
                        {
                            if (cancelled) break;

                            // Map 1-based segment number to 0-based index
                            var segIdx = pt.Number - 1;
                            if (segIdx < 0 || segIdx >= _clipboardSegments.Count)
                            {
                                failed++;
                                processed++;
                                continue;
                            }

                            var seg = _clipboardSegments[segIdx];
                            var pair = seg.SegmentPairRef as ISegmentPair;
                            if (pair == null)
                            {
                                failed++;
                                processed++;
                                continue;
                            }

                            try
                            {
                                _activeDocument.ProcessSegmentPair(pair, "Supervertaler",
                                    (sp, cancel) =>
                                    {
                                        if (seg.HasTags && seg.TagMap != null && seg.TagMap.Count > 0)
                                        {
                                            // Validate tags
                                            if (!SegmentTagHandler.ValidateTagsPresent(pt.Translation, seg.TagMap))
                                                tagWarnings++;

                                            bool reconstructed = SegmentTagHandler.ReconstructTarget(
                                                sp.Target, sp.Source, pt.Translation, seg.TagMap);

                                            if (!reconstructed)
                                            {
                                                var plainTranslation = SegmentTagHandler.StripTagPlaceholders(pt.Translation);
                                                var textTemplate = SegmentTagHandler.FindFirstText(sp.Source);
                                                if (textTemplate != null && !string.IsNullOrEmpty(plainTranslation))
                                                {
                                                    sp.Target.Clear();
                                                    var textClone = (IText)textTemplate.Clone();
                                                    textClone.Properties.Text = plainTranslation;
                                                    sp.Target.Add(textClone);
                                                }
                                            }
                                        }
                                        else
                                        {
                                            var textTpl = SegmentTagHandler.FindFirstText(sp.Source);
                                            if (textTpl != null && !string.IsNullOrEmpty(pt.Translation))
                                            {
                                                sp.Target.Clear();
                                                var textClone = (IText)textTpl.Clone();
                                                textClone.Properties.Text = pt.Translation;
                                                sp.Target.Add(textClone);
                                            }
                                        }
                                    });
                                success++;
                            }
                            catch (Exception ex)
                            {
                                batchControl.AppendLog(
                                    $"Failed to write segment {pt.Number}: {ex.Message}", true);
                                failed++;
                            }

                            processed++;
                            // Every 20 writes: advance the bar, pump the message
                            // queue so Cancel + the editor grid stay live, and run
                            // the 32-bit memory watchdog (a no-op on 64-bit).
                            if (processed % 20 == 0)
                            {
                                try { progress.SetProgress(processed, $"Writing translations… {processed} of {parsed.Count}"); } catch { }
                                System.Windows.Forms.Application.DoEvents();
                                if (cancelled) break;
                                if (Core.MemoryGuard.IsOverSoftLimit())
                                    Core.MemoryGuard.CollectAndCompact();
                                if (Core.MemoryGuard.IsOverHardLimit())
                                {
                                    stoppedForMemory = true;
                                    break;
                                }
                            }
                        }
                    }
                    finally
                    {
                        _clipboardPasteInProgress = false;
                        try { progress.Finish(); } catch { }
                    }

                    // Report results
                    var msg = (stoppedForMemory ? "Paste stopped early – imported "
                            : cancelled ? "Paste cancelled – imported "
                            : "Imported ")
                        + $"{success} translation{(success != 1 ? "s" : "")}";
                    if (failed > 0) msg += $", {failed} failed";
                    if (tagWarnings > 0) msg += $", {tagWarnings} tag warning{(tagWarnings != 1 ? "s" : "")}";
                    var missing = _clipboardSegments.Count - parsed.Count;
                    if (missing > 0) msg += $", {missing} segment{(missing != 1 ? "s" : "")} not found in response";
                    batchControl.AppendLog(msg + ".", stoppedForMemory || cancelled);

                    if (stoppedForMemory)
                    {
                        MessageBox.Show(_control.Value,
                            $"Applied {success} translation(s), then stopped to avoid a 32-bit memory crash " +
                            "in Trados Studio 2024.\n\n" +
                            "The applied segments are now translated in Trados; the rest were left untouched.\n\n" +
                            "To finish them, either:\n" +
                            "  •  copy the still-untranslated segments to the clipboard again as a smaller batch and paste back, or\n" +
                            "  •  continue in Trados Studio 2026 (64-bit), where this limit does not apply.",
                            "Paste stopped (memory limit)",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                else
                {
                    // Proofread mode: log the response for manual review
                    batchControl.AppendLog(
                        "Proofreading response received. Review the results in your LLM.");
                }

                // Clear clipboard segments and disable paste
                _clipboardSegments = null;
                batchControl.EnablePasteButton(false);

                // Update segment counts
                UpdateBatchSegmentCounts();
            });
        }

        // ─── Proofreading ─────────────────────────────────────────

        private void OnProofreadRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                var batchControl = _control.Value.BatchTranslateControl;

                if (_activeDocument == null)
                {
                    batchControl.AppendLog("No document open.", true);
                    return;
                }

                var aiSettings = _settings.AiSettings;
                if (aiSettings == null)
                {
                    batchControl.AppendLog("AI settings not configured. Open Settings to configure a provider.", true);
                    return;
                }

                // Resolve API key (same pattern as batch translate)
                var provider = aiSettings.SelectedProvider ?? LlmModels.ProviderOpenAi;
                string apiKey;
                string baseUrl = null;
                string model = aiSettings.GetSelectedModel();

                if (provider == LlmModels.ProviderOllama)
                {
                    apiKey = "ollama";
                    baseUrl = aiSettings.OllamaEndpoint ?? "http://localhost:11434";
                }
                else if (provider == LlmModels.ProviderCustomOpenAi)
                {
                    var profile = aiSettings.GetActiveCustomProfile();
                    if (profile == null)
                    {
                        batchControl.AppendLog("No custom OpenAI profile configured.", true);
                        return;
                    }
                    apiKey = profile.ApiKey;
                    baseUrl = profile.Endpoint;
                    model = profile.Model;
                }
                else
                {
                    apiKey = LlmClient.ResolveApiKey(provider, aiSettings.ApiKeys);
                }

                if (string.IsNullOrEmpty(apiKey))
                {
                    batchControl.AppendLog(
                        $"No API key configured for {provider}. Open Settings \u2192 AI Settings to add one.", true);
                    return;
                }

                // Get language pair
                var sourceLang = GetDocumentSourceLanguage();
                var targetLang = GetDocumentTargetLanguage();

                if (string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
                {
                    batchControl.AppendLog("Cannot determine source/target language from document.", true);
                    return;
                }

                // Collect segments based on proofread scope
                var proofScope = batchControl.GetSelectedProofreadScope();
                var segments = CollectProofreadSegments(proofScope);

                // Apply segment limit if set
                var maxSeg = batchControl.GetMaxSegments();
                if (maxSeg > 0 && segments.Count > maxSeg)
                {
                    batchControl.AppendLog($"Limit: proofreading first {maxSeg} of {segments.Count} segments.");
                    segments = segments.GetRange(0, maxSeg);
                }

                if (segments.Count == 0)
                {
                    batchControl.AppendLog("No segments to proofread.", true);
                    return;
                }

                // Get termbase terms for prompt injection (filtered by AI-disabled list)
                // Fallback-served MultiTerm termbases only answer per-segment lookups, so
                // terms for segments the user never visited would silently miss the prompt
                // (#38). Query them for this batch before assembling the term list.
                TermLensEditorViewPart.PrewarmFallbackTermsFor(segments.Select(sg => sg.SourceText));

                var allTerms = TermLensEditorViewPart.GetCurrentTermbaseTerms();
                var aiCfgC = _settings?.AiSettings ?? new AiSettings();
                var termbaseTerms = allTerms.Where(t => aiCfgC.IsTermbaseAiEnabled(t.TermbaseId)).ToList();
                WarnIfNoAiTermbases(batchControl, allTerms.Count, termbaseTerms.Count);

                // Resolve custom prompt from library selection
                var selectedPromptPath = batchControl.GetSelectedPromptPath();
                aiSettings.SelectedPromptPath = selectedPromptPath;
                SettingsService.Save();

                var customPromptContent = ResolveCustomPromptContent(sourceLang, targetLang);

                // Collect FULL bilingual document context (source + target for every
                // segment in the document, no truncation). The proofreader needs both
                // sides to verify cross-document target consistency – source-only
                // context can't catch "rendered as X here, Y there" claims.
                List<(string source, string target)> docSegments = null;
                if (aiSettings.IncludeDocumentContext)
                {
                    docSegments = CollectBilingualDocumentContext();
                }

                int batchSize = aiSettings.BatchSize > 0 ? aiSettings.BatchSize : 20;

                // Initialize the report
                _currentReport = new ProofreadingReport();

                // Start proofreading
                batchControl.SetRunning(true);
                batchControl.AppendLog(
                    $"Starting proofreading: {segments.Count} segments, provider={provider}, model={model}, " +
                    $"batch size={batchSize}" +
                    (docSegments != null ? $", bilingual context: {docSegments.Count} segments" : ", no document context"));

                _proofreadCts = new CancellationTokenSource();
                _batchProofreader = new BatchProofreader();

                _batchProofreader.Progress += OnBatchProgress;
                _batchProofreader.SegmentProofread += OnProofreadSegmentResult;
                _batchProofreader.Completed += OnProofreadCompleted;

                var ct = _proofreadCts.Token;

                Task.Run(async () =>
                {
                    try
                    {
                        await _batchProofreader.ProofreadAsync(
                            segments, sourceLang, targetLang,
                            aiSettings, termbaseTerms, batchSize, ct,
                            customPromptContent,
                            docSegments);
                    }
                    catch (Exception ex)
                    {
                        SafeInvoke(() =>
                        {
                            batchControl.AppendLog($"Unexpected error: {ex.Message}", true);
                            batchControl.SetRunning(false);
                        });
                    }
                });
            });
        }

        private void OnProofreadSegmentResult(object sender, ProofreadSegmentEventArgs e)
        {
            SafeInvoke(() =>
            {
                if (_currentReport != null && e.Issue != null)
                {
                    _currentReport.Issues.Add(e.Issue);
                }

                var batchControl = _control.Value.BatchTranslateControl;
                if (e.Issue != null)
                {
                    if (e.Issue.IsOk)
                    {
                        batchControl.AppendLog($"\u2713 Seg {e.Issue.SegmentNumber}: OK");
                    }
                    else
                    {
                        var desc = Truncate(e.Issue.IssueDescription, 80);
                        batchControl.AppendLog($"\u26A0 Seg {e.Issue.SegmentNumber}: {desc}");
                    }
                }
            });
        }

        private void OnProofreadCompleted(object sender, ProofreadCompletedEventArgs e)
        {
            SafeInvoke(() =>
            {
                if (_currentReport != null)
                {
                    _currentReport.Duration = e.Elapsed;
                    _currentReport.TotalSegmentsChecked = e.TotalChecked;

                    _control.Value.ReportsControl.SetResults(_currentReport);
                    _control.Value.UpdateReportsBadge(_currentReport.IssueCount);

                    if (_currentReport.IssueCount > 0)
                    {
                        _control.Value.SwitchToReportsTab();
                    }
                }

                _control.Value.BatchTranslateControl.ReportProofreadCompleted(
                    e.TotalChecked, e.IssueCount, e.OkCount,
                    e.Elapsed, e.Cancelled);
            });

            // Clean up
            if (_batchProofreader != null)
            {
                _batchProofreader.Progress -= OnBatchProgress;
                _batchProofreader.SegmentProofread -= OnProofreadSegmentResult;
                _batchProofreader.Completed -= OnProofreadCompleted;
                _batchProofreader = null;
            }

            _proofreadCts?.Dispose();
            _proofreadCts = null;
        }

        private void OnNavigateToSegment(object sender, NavigateToSegmentEventArgs e)
        {
            SafeInvoke(() =>
            {
                if (_activeDocument == null) return;
                if (string.IsNullOrEmpty(e.ParagraphUnitId) || string.IsNullOrEmpty(e.SegmentId))
                    return;

                try
                {
                    _activeDocument.SetActiveSegmentPair(e.ParagraphUnitId, e.SegmentId, true);
                }
                catch (Exception)
                {
                    // Segment may no longer be accessible
                    return;
                }

                // Re-activate the Supervertaler Assistant pane after navigation.
                // Same focus-steal scenario as the v4.19.66 batch-boundary fix:
                // SetActiveSegmentPair fires Trados' active-segment-changed event,
                // and the built-in Translation Results pane reacts by re-running
                // its TM/MT lookups for the new segment, which on Trados 18 brings
                // its tab to the front. Without this counter-Activate, every click
                // on a Reports issue card kicks the user away from the Reports
                // tab to Translation Results — exactly when they want to read the
                // issue details and act on them.
                //
                // The synchronous Activate() handles the case where Trados raises
                // its event inline. The deferred one (posted via BeginInvoke on
                // the control) handles the case where Trados queues the focus
                // steal for a later UI tick — by posting our Activate after, we
                // run after the steal has already happened and reliably win.
                try { Activate(); } catch { }
                try
                {
                    _control.Value.BeginInvoke((Action)(() =>
                    {
                        try { Activate(); } catch { }
                    }));
                }
                catch { /* control may not be available */ }
            });
        }

        private void OnClearReports(object sender, EventArgs e)
        {
            _currentReport = null;
            _control.Value.ReportsControl.ClearResults();
            _control.Value.UpdateReportsBadge(0);
        }

        private void OnPromptCompleted(object sender, PromptLogEntry entry)
        {
            if (entry == null) return;

            // Note: the persistent token-usage ledger is now recorded by a global
            // subscriber wired in AppInitializer (UsageLogger.EnsureSubscribed), so
            // it works even when this pane was never opened. This handler only
            // updates the Reports-tab UI below (avoids double-counting usage).

            if (_settings?.AiSettings?.LogPromptsToReports != true) return;

            SafeInvoke(() =>
            {
                // Add card to Reports tab
                _control.Value.ReportsControl.AddPromptLog(entry);

                // Show summary line in chat for Chat/QuickLauncher calls
                if (entry.Feature == PromptLogFeature.Chat ||
                    entry.Feature == PromptLogFeature.QuickLauncher)
                {
                    _control.Value.AddSummaryLine(entry.SummaryLine);
                }
            });
        }

        /// <summary>
        /// Collects segments for proofreading based on the selected scope.
        /// Unlike batch translate, proofreading only targets segments that have
        /// a translation (non-empty target), filtering by confirmation level.
        /// </summary>
        private List<BatchSegment> CollectProofreadSegments(ProofreadScope scope)
        {
            var segments = new List<BatchSegment>();
            if (_activeDocument == null) return segments;

            try
            {
                // Use filtered or full segment pairs depending on scope
                var useFiltered = scope == ProofreadScope.Filtered
                    || scope == ProofreadScope.FilteredConfirmedOnly;
                var pairs = useFiltered
                    ? _activeDocument.FilteredSegmentPairs
                    : _activeDocument.SegmentPairs;

                // Build a map of (ParagraphUnitId + SegmentId) → per-file segment number.
                // In multi-file projects, segment numbering restarts per file.
                // We detect file boundaries by tracking the IDocumentProperties file association.
                var segmentNumberMap = new Dictionary<string, int>();
                int fileSegIdx = 0;
                Sdl.FileTypeSupport.Framework.BilingualApi.IFileProperties lastFile = null;
                foreach (var allPair in _activeDocument.SegmentPairs)
                {
                    try
                    {
                        var parentPu = _activeDocument.GetParentParagraphUnit(allPair);
                        var sid = allPair.Properties?.Id.Id;

                        // Trados' segment ID IS the number shown in the editor grid –
                        // it's preserved across merges (the surviving segment keeps its
                        // ID, the retired one is gone) and assigned fresh on splits, so
                        // using it as the per-file number keeps our Reports tab numbering
                        // aligned with what the user sees in Trados even after
                        // merging or splitting. Falling back to iteration count only when
                        // the ID isn't parseable as an int (older formats / exotic filters).
                        //
                        // File-boundary detection: a segment ID that resets to a low number
                        // (or, in the non-parseable case, looks suspiciously like a restart)
                        // means we've crossed into the next file in a multi-file project.
                        int segIdNum;
                        bool parsed = int.TryParse(sid, out segIdNum);

                        if (parsed && segIdNum <= fileSegIdx && fileSegIdx > 0)
                            fileSegIdx = 0;

                        if (parsed)
                            fileSegIdx = segIdNum;
                        else
                            fileSegIdx++;

                        if (!string.IsNullOrEmpty(sid))
                        {
                            var puId = parentPu?.Properties?.ParagraphUnitId.Id ?? "";
                            segmentNumberMap[puId + "|" + sid] = fileSegIdx;
                        }
                    }
                    catch
                    {
                        fileSegIdx++;
                    }
                }

                int index = 0;
                foreach (var pair in pairs)
                {
                    // Locked segments are never proofread – same rule as batch
                    // translate. Worse here: a locked segment has a target, so
                    // the proofreader would not only read it but could rewrite
                    // content someone locked precisely to protect it.
                    if (pair.Properties?.IsLocked == true)
                    {
                        index++;
                        continue;
                    }

                    var targetText = pair.Target != null
                        ? SegmentTagHandler.GetFinalText(pair.Target) : "";

                    // Skip segments with empty target – nothing to proofread
                    if (string.IsNullOrWhiteSpace(targetText))
                    {
                        index++;
                        continue;
                    }

                    var sourceText = pair.Source != null
                        ? SegmentTagHandler.GetFinalText(pair.Source) : "";
                    // Strip Unicode line/paragraph separators – see comment in SendChatMessage
                    sourceText = sourceText.Replace("\u2028", " ").Replace("\u2029", " ");
                    if (string.IsNullOrWhiteSpace(sourceText))
                    {
                        index++;
                        continue;
                    }

                    // Filter by confirmation level based on scope
                    bool include = false;
                    var confirmLevel = pair.Properties?.ConfirmationLevel
                        ?? Sdl.Core.Globalization.ConfirmationLevel.Unspecified;

                    switch (scope)
                    {
                        case ProofreadScope.ConfirmedOnly:
                        case ProofreadScope.FilteredConfirmedOnly:
                            // "Translated only" – segments at exactly Translated status
                            include = confirmLevel == Sdl.Core.Globalization.ConfirmationLevel.Translated;
                            break;
                        case ProofreadScope.TranslatedAndConfirmed:
                            // "Translated + Approved" – Translated, Approved, and Signed-off
                            include = confirmLevel >= Sdl.Core.Globalization.ConfirmationLevel.Translated;
                            break;
                        case ProofreadScope.AllSegments:
                        case ProofreadScope.Filtered:
                            include = true;
                            break;
                    }

                    if (include)
                    {
                        // Get paragraph unit ID and segment ID for navigation
                        string paragraphUnitId = null;
                        string segmentId = null;
                        try
                        {
                            var parentPU = _activeDocument.GetParentParagraphUnit(pair);
                            paragraphUnitId = parentPU.Properties.ParagraphUnitId.Id;
                            segmentId = pair.Properties.Id.Id;
                        }
                        catch { }

                        // Use actual per-file segment number, not filtered/cross-file index
                        int actualSegNum = index + 1;
                        var mapKey = (paragraphUnitId ?? "") + "|" + (segmentId ?? "");
                        if (segmentNumberMap.TryGetValue(mapKey, out var docNum))
                            actualSegNum = docNum;

                        segments.Add(new BatchSegment
                        {
                            Index = actualSegNum - 1, // 0-based for BatchSegment.Index
                            SourceText = sourceText,
                            ExistingTarget = targetText,
                            SegmentPairRef = new[] { paragraphUnitId, segmentId }
                        });
                    }

                    index++;
                }
            }
            catch (Exception)
            {
                // Document may not be accessible during transitions
            }

            return segments;
        }

        /// <summary>
        /// True when a segment's status counts as "finalized" work that the
        /// "All unfinished segments" scope should leave alone — Translated,
        /// Approved (translation), or Approved (sign-off). NOT finalized:
        /// Unspecified (Not Translated), Draft, and Rejected. Compared by enum
        /// NAME (matching ConfirmationLevel.ToString(), as ImportExportControl
        /// does) so it's robust to the enum's numeric ordering, where Rejected
        /// actually sorts above Translated.
        /// </summary>
        private bool IsFinalizedStatus(ISegmentPair pair)
        {
            var cl = pair?.Properties?.ConfirmationLevel
                ?? Sdl.Core.Globalization.ConfirmationLevel.Unspecified;
            var name = cl.ToString();
            return name == "Translated"
                || name == "ApprovedTranslation"
                || name == "ApprovedSignOff";
        }

        private List<BatchSegment> CollectSegments(BatchScope scope)
        {
            var segments = new List<BatchSegment>();
            if (_activeDocument == null) return segments;

            try
            {
                // Use filtered or full segment pairs depending on scope
                var useFiltered = scope == BatchScope.Filtered
                    || scope == BatchScope.FilteredEmptyOnly;
                var emptyOnly = scope == BatchScope.EmptyOnly
                    || scope == BatchScope.FilteredEmptyOnly;
                // "All unfinished segments": every status except the finalized
                // ones (Translated / Approved / Signed off). Runs over ALL pairs
                // (not the display filter) and does not restrict to empty targets,
                // so drafts and rejected segments get re-translated too.
                var notFinalizedScope = scope == BatchScope.NotFinalized;
                var pairs = useFiltered
                    ? _activeDocument.FilteredSegmentPairs
                    : _activeDocument.SegmentPairs;

                int index = 0;
                foreach (var pair in pairs)
                {
                    // Locked means locked: never send a locked segment's content
                    // to the AI, in any scope. Locked segments used to slip in
                    // (they typically have empty targets, so EmptyOnly picked
                    // them up first) and the batch would translate them and jump
                    // the editor to them – burning tokens on content someone
                    // locked precisely so it would be left alone.
                    if (pair.Properties?.IsLocked == true)
                    {
                        index++;
                        continue;
                    }

                    var targetText = pair.Target != null
                        ? SegmentTagHandler.GetFinalText(pair.Target) : "";

                    // Serialize source with tag placeholders (if segment has inline tags)
                    var sourceSegment = pair.Source;
                    var serialization = SegmentTagHandler.Serialize(sourceSegment);
                    var sourceText = serialization.HasTags
                        ? serialization.SerializedText
                        : (sourceSegment?.ToString() ?? "");

                    // Strip Unicode line/paragraph separators – see comment in SendChatMessage
                    sourceText = sourceText.Replace("\u2028", " ").Replace("\u2029", " ");

                    if (string.IsNullOrWhiteSpace(SegmentTagHandler.StripTagPlaceholders(sourceText)))
                    {
                        index++;
                        continue;
                    }

                    bool include = !emptyOnly || string.IsNullOrWhiteSpace(targetText);

                    // "All unfinished segments" filters out finalized statuses.
                    if (include && notFinalizedScope && IsFinalizedStatus(pair))
                        include = false;

                    if (include)
                    {
                        // Always store ISegmentPair so ProcessSegmentPair can be used
                        // for all segments. This ensures correct handling of literal
                        // newlines (Excel, Visio) which need IText cloning from source
                        // to produce soft returns instead of paragraph marks.
                        segments.Add(new BatchSegment
                        {
                            Index = index,
                            SourceText = sourceText,
                            ExistingTarget = targetText,
                            SegmentPairRef = pair,
                            HasTags = serialization.HasTags,
                            TagMap = serialization.HasTags ? serialization.TagMap : null
                        });
                    }

                    index++;
                }
            }
            catch (Exception)
            {
                // Document may not be accessible during transitions
            }

            return segments;
        }

        private void UpdateBatchSegmentCounts()
        {
            SafeInvoke(() =>
            {
                if (_activeDocument == null)
                {
                    _control.Value.BatchTranslateControl.UpdateSegmentCounts(0, 0);
                    return;
                }

                try
                {
                    int total = 0;
                    int empty = 0;
                    int notFinalized = 0;

                    foreach (var pair in _activeDocument.SegmentPairs)
                    {
                        total++;
                        // Locked segments are excluded from batching, so keep
                        // the scope counters consistent with what a run will
                        // actually process.
                        if (pair.Properties?.IsLocked == true)
                            continue;
                        var targetText = pair.Target != null
                            ? SegmentTagHandler.GetFinalText(pair.Target) : "";
                        if (string.IsNullOrWhiteSpace(targetText))
                            empty++;
                        if (!IsFinalizedStatus(pair))
                            notFinalized++;
                    }

                    // Get filtered count from Trados display filter
                    int filtered = _activeDocument.FilteredSegmentPairsCount;

                    _control.Value.BatchTranslateControl.UpdateSegmentCounts(empty, total, filtered, notFinalized);
                }
                catch (Exception)
                {
                    _control.Value.BatchTranslateControl.UpdateSegmentCounts(0, 0);
                }
            });

            // Piggyback the Import / Export tab's file list AND segment
            // counter onto the same document-change events. Single-file
            // documents see no UI change; multi-file documents get a
            // checklist populated with the merged-in files.
            UpdateImportExportFileList();
            UpdateImportExportSegmentCount();
        }

        /// <summary>Update the "Segments: N" label on the Import / Export
        /// tab. In multi-file mode the count reflects ONLY segments in
        /// the currently-checked files; in single-file mode it's the
        /// active document's full count. Always runs on the UI thread
        /// via SafeInvoke and degrades to 0 on any SDK hiccup.</summary>
        private void UpdateImportExportSegmentCount()
        {
            SafeInvoke(() =>
            {
                var ctrl = _control?.Value?.ImportExportControl;
                if (ctrl == null) return;

                if (_activeDocument == null)
                {
                    ctrl.UpdateSegmentCount(0);
                    return;
                }

                // Honour the file selection when in multi-file mode.
                // GetSelectedFileIds returns an empty list for single-
                // file documents (the UI is hidden); in that case empty
                // means "no file filter — count everything".
                //
                // In multi-file mode, empty selection means the user
                // unchecked everything → count = 0 (so the "None" button
                // visibly does something).
                //
                // When per-file attribution couldn't be built (SDK didn't
                // expose enough info), we silently drop the filter and
                // count everything regardless of selection. Better to
                // show a meaningful total than a misleading 0; the export
                // path uses the same fallback so behaviour stays
                // consistent.
                var selected = ctrl.GetSelectedFileIds();
                bool multiFileVisible = ctrl.IsMultiFileUiVisible;
                HashSet<string> filter;
                if (!_perFileMappingWorked)
                {
                    filter = null;     // attribution failed → can't filter
                }
                else if (!multiFileVisible)
                {
                    filter = null;     // single-file mode — no filtering
                }
                else
                {
                    // Multi-file mode: empty selection = 0 segments.
                    // Non-empty selection = those files only.
                    filter = new HashSet<string>(selected, StringComparer.Ordinal);
                }

                int total = 0;
                try
                {
                    foreach (var pair in _activeDocument.SegmentPairs)
                    {
                        if (filter != null)
                        {
                            if (filter.Count == 0) { total = 0; break; }
                            var fileId = GetFileIdForSegment(pair);
                            if (string.IsNullOrEmpty(fileId) || !filter.Contains(fileId)) continue;
                        }
                        total++;
                    }
                }
                catch { total = 0; }

                ctrl.UpdateSegmentCount(total);
            });
        }

        // Cached map of fileId → set of "{puId}/{segId}" composite keys
        // for the active document. Built once per ActiveDocumentChanged,
        // queried per segment via GetFileIdForSegment. Bypasses the
        // brittle "look for a FileId on the segment / pu via reflection"
        // pattern that fails on Studio 18 (the property genuinely doesn't
        // exist there) in favour of walking each file's own SegmentPairs
        // collection — which we can usually access via reflection through
        // IFile.ParagraphUnits[].SegmentPairs.
        private readonly Dictionary<string, HashSet<string>> _fileIdToSegmentKeys =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _fileIdToName =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Return the list of files in the active document, plus
        /// the currently-active file's id (for the [Active only] quick-
        /// select button). For a single-file document the list has one
        /// entry — the ImportExportControl uses that as a signal to hide
        /// the multi-file UI. All access is via reflection so the code
        /// stays decoupled from per-SDK-version IFile type names.</summary>
        private List<Controls.ImportExportControl.FileEntry> EnumerateActiveDocumentFiles(out string activeFileId)
        {
            activeFileId = "";
            var entries = new List<Controls.ImportExportControl.FileEntry>();
            if (_activeDocument == null) return entries;

            // Refresh the per-file segment map. Cheap on single-file
            // documents (one iteration); a bit more on multi-file, but
            // it only runs on document changes / file-list refresh.
            RefreshFileToSegmentMap();

            // Active file id for the quick-select button.
            try
            {
                var af = _activeDocument.ActiveFile;
                if (af != null)
                    activeFileId = TryGetStringProp(af, "Id") ?? TryGetStringProp(af, "FileId") ?? "";
            }
            catch { }

            // Try _activeDocument.Files first (multi-file document
            // exposes them); fall back to ActiveFile only.
            var filesEnum = TryGetEnumerable(_activeDocument, "Files");
            if (filesEnum == null)
            {
                var af = _activeDocument.ActiveFile;
                if (af != null)
                {
                    var id = TryGetStringProp(af, "Id") ?? TryGetStringProp(af, "FileId") ?? "";
                    activeFileId = id;
                    entries.Add(new Controls.ImportExportControl.FileEntry
                    {
                        FileId = id,
                        FileName = TryGetStringProp(af, "Name") ?? "(unknown file)",
                        SegmentCount = LookupSegmentCount(id)
                    });
                }
                return entries;
            }

            foreach (var f in filesEnum)
            {
                if (f == null) continue;
                var id = TryGetStringProp(f, "Id") ?? TryGetStringProp(f, "FileId") ?? "";
                var name = TryGetStringProp(f, "Name") ?? "(unknown file)";
                entries.Add(new Controls.ImportExportControl.FileEntry
                {
                    FileId = id,
                    FileName = name,
                    SegmentCount = LookupSegmentCount(id)
                });
            }
            return entries;
        }

        private int LookupSegmentCount(string fileId)
        {
            if (string.IsNullOrEmpty(fileId)) return 0;
            return _fileIdToSegmentKeys.TryGetValue(fileId, out var keys) ? keys.Count : 0;
        }

        /// <summary>True when the most recent <see cref="RefreshFileToSegmentMap"/>
        /// call produced at least one attributed segment. When false, callers
        /// should ignore per-file filters and operate on the full segment
        /// list — the SDK didn't give us enough info to attribute segments
        /// to files, so filtering would silently drop everything.</summary>
        private bool _perFileMappingWorked = false;

        /// <summary>Rebuild the (fileId → segment-key set) map.
        ///
        /// Trados Studio 18 + 19 don't expose per-file segment enumeration
        /// at the SDK level (verified via the v4.20.8/9 diagnostics —
        /// ProjectFile has no ParagraphUnits, and paragraph-unit context
        /// metadata contains ZERO file-identifying strings, only style /
        /// header-footer info). So we go around the SDK: each ProjectFile
        /// has a <c>LocalFilePath</c> pointing at its on-disk SDLXLIFF,
        /// which is XML. We extract every GUID from each SDLXLIFF — Trados
        /// paragraph-unit ids are GUIDs and are globally unique, so the
        /// set of GUIDs in file A's SDLXLIFF is exactly the set of PU ids
        /// belonging to file A. Then for each <c>SegmentPair</c> we get the
        /// parent PU's id and look up which file's GUID set contains it.
        ///
        /// One-time cost: ~tens of MB of file I/O + a regex scan, run only
        /// when the active document changes. Sets <see cref="_perFileMappingWorked"/>
        /// to true iff at least one segment got attributed.</summary>
        private void RefreshFileToSegmentMap()
        {
            _fileIdToSegmentKeys.Clear();
            _fileIdToName.Clear();
            _puIdToFileId.Clear();
            _perFileMappingWorked = false;
            if (_activeDocument == null) return;

            var filesEnum = TryGetEnumerable(_activeDocument, "Files");
            if (filesEnum == null) return;

            // Step 1: scan each file's SDLXLIFF for GUIDs → puId→fileId map.
            // Build _fileIdToName + _fileIdToSegmentKeys (empty sets) at
            // the same time so the rest of the API has something to read
            // even if scanning fails for one file.
            var puIdToFileId = new Dictionary<string, string>(StringComparer.Ordinal);
            int totalGuids = 0;
            foreach (var f in filesEnum)
            {
                if (f == null) continue;
                var fileId = TryGetStringProp(f, "Id") ?? TryGetStringProp(f, "FileId") ?? "";
                if (string.IsNullOrEmpty(fileId)) continue;
                var name = TryGetStringProp(f, "Name") ?? "";
                _fileIdToName[fileId] = name;
                _fileIdToSegmentKeys[fileId] = new HashSet<string>(StringComparer.Ordinal);

                var local = TryGetStringProp(f, "LocalFilePath") ?? "";
                if (string.IsNullOrEmpty(local) || !System.IO.File.Exists(local)) continue;

                try
                {
                    var content = System.IO.File.ReadAllText(local);
                    foreach (System.Text.RegularExpressions.Match m in SdlxliffGuidRe.Matches(content))
                    {
                        var g = m.Value;
                        // First-wins. A GUID present in two files would be a
                        // Trados bug — they're globally unique paragraph-unit
                        // ids — but defend against it by not overwriting.
                        if (!puIdToFileId.ContainsKey(g))
                        {
                            puIdToFileId[g] = fileId;
                            totalGuids++;
                        }
                    }
                }
                catch { }
            }

            if (puIdToFileId.Count == 0) return;

            // Step 2: walk every segment pair, attribute via PU id lookup.
            int attributed = 0;
            try
            {
                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    if (pair == null) continue;

                    object pu = null;
                    try { pu = _activeDocument.GetParentParagraphUnit(pair); }
                    catch { }
                    if (pu == null) continue;

                    var puId = TryGetParagraphUnitId(pu);
                    string segId = "";
                    try { segId = pair.Properties?.Id.Id ?? ""; } catch { }
                    if (string.IsNullOrEmpty(puId) || string.IsNullOrEmpty(segId)) continue;

                    string fid;
                    if (!puIdToFileId.TryGetValue(puId, out fid)) continue;

                    var key = puId + "/" + segId;
                    _fileIdToSegmentKeys[fid].Add(key);
                    _puIdToFileId[puId] = fid;
                    attributed++;
                }
            }
            catch { }

            _perFileMappingWorked = attributed > 0;
        }

        /// <summary>Regex matching the standard "8-4-4-4-12" GUID pattern.
        /// Compiled once. Used to extract paragraph-unit ids from on-disk
        /// SDLXLIFF files in <see cref="RefreshFileToSegmentMap"/>.</summary>
        private static readonly System.Text.RegularExpressions.Regex SdlxliffGuidRe =
            new System.Text.RegularExpressions.Regex(
                @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // PU id → file id cache. Many segment pairs share the same PU;
        // walking the context stack for each one would be wasteful. Built
        // lazily inside RefreshFileToSegmentMap, cleared on doc change.
        private readonly Dictionary<string, string> _puIdToFileId =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Per-file attribution candidate set. Built from a
        /// ProjectFile's Name / OriginalName / LocalFilePath (with and
        /// without the .sdlxliff suffix and basename variants). The
        /// matcher tries to find any of these strings inside a PU's
        /// context-stack strings.</summary>
        private sealed class FileMatchEntry
        {
            public string FileId;
            public string Name;
            public readonly HashSet<string> Candidates =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static void AddCandidate(HashSet<string> set, string s)
        {
            if (!string.IsNullOrEmpty(s)) set.Add(s);
        }

        private static string StripSdlxliffExt(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.EndsWith(".sdlxliff", StringComparison.OrdinalIgnoreCase))
                return s.Substring(0, s.Length - 9);
            return s;
        }

        /// <summary>Walk a paragraph unit's context stack and try to match
        /// any context-string against any file's candidate set. Returns the
        /// matching file id, or null if no context contains an identifiable
        /// file reference. Pure reflection — no compile-time IContextInfo
        /// type reference (avoids Studio-version coupling).</summary>
        private static string MatchFileFromPuContexts(object pu, List<FileMatchEntry> files)
        {
            try
            {
                var props = pu.GetType().GetProperty("Properties")?.GetValue(pu, null);
                if (props == null) return null;
                var contexts = props.GetType().GetProperty("Contexts")?.GetValue(props, null);
                if (contexts == null) return null;

                // Prefer the IEnumerable<IContextInfo> nested "Contexts"
                // collection; fall back to the IContextProperties root
                // itself if it implements IEnumerable directly.
                System.Collections.IEnumerable list = null;
                try { list = contexts.GetType().GetProperty("Contexts")?.GetValue(contexts, null) as System.Collections.IEnumerable; } catch { }
                if (list == null) { try { list = contexts as System.Collections.IEnumerable; } catch { } }
                if (list == null) return null;

                foreach (var ctx in list)
                {
                    if (ctx == null) continue;
                    var ctxStrings = CollectContextStrings(ctx);
                    if (ctxStrings.Count == 0) continue;

                    foreach (var entry in files)
                    {
                        foreach (var fcand in entry.Candidates)
                        {
                            if (fcand.Length < 4) continue; // too generic
                            foreach (var cs in ctxStrings)
                            {
                                if (string.IsNullOrEmpty(cs)) continue;
                                if (cs.IndexOf(fcand, StringComparison.OrdinalIgnoreCase) >= 0)
                                    return entry.FileId;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Pluck every string that could plausibly identify the
        /// source file from a single IContextInfo: surface string
        /// properties + a small set of likely metadata keys (FilePath,
        /// OriginalFilePath, etc.).</summary>
        private static List<string> CollectContextStrings(object ctx)
        {
            var result = new List<string>(12);
            var type = ctx.GetType();
            foreach (var propName in new[] { "Description", "DisplayName", "Code", "DisplayCode", "ContextType" })
            {
                try
                {
                    var v = type.GetProperty(propName)?.GetValue(ctx, null) as string;
                    if (!string.IsNullOrEmpty(v)) result.Add(v);
                }
                catch { }
            }
            foreach (var key in new[]
            {
                "FilePath", "OriginalFilePath", "Path", "FileName",
                "OriginalName", "SourceFilePath", "Source", "File"
            })
            {
                try
                {
                    var v = TryGetContextMetaData(ctx, key);
                    if (!string.IsNullOrEmpty(v)) result.Add(v);
                }
                catch { }
            }
            return result;
        }

        private static string TryGetParagraphUnitId(object pu)
        {
            try
            {
                var propsProp = pu.GetType().GetProperty("Properties");
                if (propsProp == null) return "";
                var props = propsProp.GetValue(pu, null);
                if (props == null) return "";
                var puIdProp = props.GetType().GetProperty("ParagraphUnitId");
                if (puIdProp == null) return "";
                var puId = puIdProp.GetValue(props, null);
                if (puId == null) return "";
                var idProp = puId.GetType().GetProperty("Id");
                if (idProp == null) return "";
                return idProp.GetValue(puId, null) as string ?? "";
            }
            catch { return ""; }
        }

        private static System.Collections.IEnumerable TryGetEnumerable(object obj, string propName)
        {
            if (obj == null || string.IsNullOrEmpty(propName)) return null;
            try
            {
                var prop = obj.GetType().GetProperty(propName);
                if (prop == null) return null;
                return prop.GetValue(obj, null) as System.Collections.IEnumerable;
            }
            catch { return null; }
        }

        private static System.Collections.IEnumerable TryInvokeEnumerable(object obj, string methodName)
        {
            if (obj == null || string.IsNullOrEmpty(methodName)) return null;
            try
            {
                var method = obj.GetType().GetMethod(methodName, Type.EmptyTypes);
                if (method == null) return null;
                return method.Invoke(obj, null) as System.Collections.IEnumerable;
            }
            catch { return null; }
        }

        /// <summary>Get the file id a segment pair belongs to, using the
        /// precomputed map built by <see cref="RefreshFileToSegmentMap"/>.
        /// Returns empty string when the map is empty (SDK didn't expose
        /// ParagraphUnits) or the segment isn't found.</summary>
        private string GetFileIdForSegment(Sdl.FileTypeSupport.Framework.BilingualApi.ISegmentPair pair)
        {
            if (pair == null || _fileIdToSegmentKeys.Count == 0) return "";

            string puId = "", segId = "";
            try
            {
                var pu = _activeDocument?.GetParentParagraphUnit(pair);
                puId = pu?.Properties?.ParagraphUnitId.Id ?? "";
            }
            catch { }
            try { segId = pair.Properties?.Id.Id ?? ""; } catch { }
            if (string.IsNullOrEmpty(puId) || string.IsNullOrEmpty(segId)) return "";

            var key = puId + "/" + segId;
            foreach (var kv in _fileIdToSegmentKeys)
            {
                if (kv.Value.Contains(key)) return kv.Key;
            }
            return "";
        }

        /// <summary>Read a string-typed property by name via reflection
        /// from an arbitrary SDK object. Returns null if the property
        /// doesn't exist, isn't a string, or throws.</summary>
        private static string TryGetStringProp(object obj, string propName)
        {
            if (obj == null || string.IsNullOrEmpty(propName)) return null;
            try
            {
                var prop = obj.GetType().GetProperty(propName);
                if (prop == null) return null;
                var val = prop.GetValue(obj, null);
                return val?.ToString();
            }
            catch { return null; }
        }

        /// <summary>Push the active document's file list (plus the
        /// active-file id for the quick-select button) into the Import /
        /// Export tab. Single-file documents result in an empty/one-item
        /// list — the control hides the multi-file UI in that case.</summary>
        private void UpdateImportExportFileList()
        {
            SafeInvoke(() =>
            {
                var ctrl = _control?.Value?.ImportExportControl;
                if (ctrl == null) return;
                string activeFileId;
                var files = EnumerateActiveDocumentFiles(out activeFileId);
                ctrl.SetFileList(files, activeFileId);
            });
        }

        private void UpdateBatchProviderDisplay()
        {
            SafeInvoke(() =>
            {
                var ai = _settings?.AiSettings;
                if (ai == null)
                {
                    _control.Value.BatchTranslateControl.UpdateProviderDisplay("Not configured", "");
                    return;
                }

                var provider = ai.SelectedProvider ?? "Not configured";
                var model = ai.GetSelectedModel() ?? "";

                if (provider == LlmModels.ProviderCustomOpenAi)
                {
                    var profile = ai.GetActiveCustomProfile();
                    if (profile != null)
                    {
                        provider = string.IsNullOrEmpty(profile.Name) ? "Custom" : profile.Name;
                        model = profile.Model ?? "";
                    }
                }

                _control.Value.BatchTranslateControl.UpdateProviderDisplay(provider, model);
            });
        }

        // ─── QuickLauncher entry point ────────────────────────────────────

        /// <summary>
        /// Called by QuickLauncherAction when the user selects a QuickLauncher prompt from the
        /// editor right-click menu. The prompt content must already have all variables substituted
        /// before this is called. Submits the message to the AI Assistant chat.
        /// </summary>
        /// <param name="expandedPrompt">Full prompt text sent to the AI.</param>
        /// <param name="displayPrompt">
        /// Optional shorter version shown in the chat bubble. Pass null to show the full prompt.
        /// Use this when the prompt contains a large {{PROJECT}} expansion.
        /// </param>
        public static void RunQuickLauncherPrompt(string expandedPrompt, string displayPrompt = null, string promptName = null)
        {
            if (string.IsNullOrWhiteSpace(expandedPrompt)) return;

            var instance = _currentInstance;
            if (instance == null) return;

            // Activate the Supervertaler Assistant panel so it is visible even
            // when auto-hidden, unpinned, or behind another dock tab. Matches
            // the SuperSearchAction pattern. SubmitMessage will then switch to
            // the Chat tab (index 0) inside the panel.
            try { instance.Activate(); }
            catch { /* Activate may not be available in all Trados versions */ }

            instance.SafeInvoke(() =>
            {
                _control.Value.SubmitMessage(expandedPrompt, displayPrompt, promptName);
            });
        }

        /// <summary>
        /// Activates the Supervertaler Assistant panel and switches to the
        /// SuperSearch tab. Used by <c>SuperSearchAction</c> (Alt+S) when
        /// SuperSearch is hosted as a tab rather than its own ViewPart. No-op
        /// if the tab isn't present (setting off, or unlicensed).
        /// </summary>
        public static void ActivateSuperSearchTab()
        {
            var instance = _currentInstance;
            if (instance == null) return;

            try { instance.Activate(); }
            catch { /* Activate may not be available in all Trados versions */ }

            instance.SafeInvoke(() => _control.Value.SwitchToSuperSearchTab());
        }

        // ─── Text transforms (local find/replace, no AI call) ─────────

        /// <summary>
        /// Applies a text transform to the active target segment.
        /// Runs the find/replace rules from the prompt's Replacements list
        /// directly on the target text without calling an AI provider.
        /// Uses ProcessContentWithDocument to commit changes through the
        /// Trados document model (same mechanism as batch translate).
        /// Returns a message describing what happened (for status display).
        /// </summary>
        public static string RunTextTransform(PromptTemplate transform)
        {
            if (transform == null || transform.Replacements.Count == 0)
                return "No replacements defined.";

            var instance = _currentInstance;
            if (instance == null)
                return "AI Assistant not initialised.";

            if (instance._activeDocument == null)
                return "No document open.";

            var pair = instance._activeDocument.ActiveSegmentPair;
            if (pair?.Target == null)
                return "No active segment.";

            // Count occurrences first (on plain text) to report accurately
            var plainText = pair.Target.ToString() ?? "";
            if (string.IsNullOrEmpty(plainText))
                return "Target segment is empty.";

            int totalReplacements = 0;
            foreach (var r in transform.Replacements)
            {
                if (string.IsNullOrEmpty(r.Find)) continue;
                int idx = 0;
                while ((idx = plainText.IndexOf(r.Find, idx, StringComparison.Ordinal)) >= 0)
                {
                    totalReplacements++;
                    idx += r.Find.Length;
                }
            }

            if (totalReplacements == 0)
                return "No matches found \u2014 target unchanged.";

            // Apply replacements through ProcessContentWithDocument so the
            // Trados editor commits the changes (direct IText property writes
            // do not persist). This modifies IText nodes in-place inside the
            // document model, preserving all formatting tags.
            string result = null;
            string cleanedText = null;
            instance.SafeInvoke(() =>
            {
                try
                {
                    // Capture replacements for use inside the delegate
                    var replacements = transform.Replacements;

                    instance._activeDocument.ProcessSegmentPair(pair, "Supervertaler",
                        (sp, cancel) =>
                        {
                            foreach (var item in sp.Target.AllSubItems)
                            {
                                var textItem = item as IText;
                                if (textItem == null) continue;

                                var text = textItem.Properties.Text;
                                if (string.IsNullOrEmpty(text)) continue;

                                foreach (var r in replacements)
                                {
                                    if (string.IsNullOrEmpty(r.Find)) continue;
                                    text = text.Replace(r.Find, r.Replace);
                                }

                                // Collapse runs of multiple spaces into a single space
                                // (replacing an invisible char with a space next to an
                                // existing space would otherwise leave double spaces)
                                while (text.Contains("  "))
                                    text = text.Replace("  ", " ");

                                if (text != textItem.Properties.Text)
                                    textItem.Properties.Text = text;
                            }

                            // Capture the cleaned plain text for clipboard
                            cleanedText = sp.Target.ToString();
                        });

                    // Copy the cleaned text to the clipboard
                    if (!string.IsNullOrEmpty(cleanedText))
                    {
                        try { Clipboard.SetText(cleanedText); } catch { /* clipboard may be locked */ }
                    }

                    result = $"\u2713 {totalReplacements} replacement{(totalReplacements == 1 ? "" : "s")} applied (copied to clipboard).";
                }
                catch (Exception ex)
                {
                    result = "Failed to update target: " + ex.Message;
                }
            });

            return result ?? "Transform applied.";
        }

        /// <summary>
        /// Shows the result of a text transform as a brief MessageBox.
        /// </summary>
        public static void ShowTransformResult(string transformName, string result)
        {
            MessageBox.Show(result, "Supervertaler \u2014 " + transformName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ─── Legacy entry point (AiTranslateSegmentAction compatibility) ──

        /// <summary>
        /// Legacy redirect – calls HandleTranslateActiveSegment (Ctrl+T pipeline).
        /// Kept because Trados caches action types and removing the method causes crashes.
        /// </summary>
        public static void HandleAiTranslateSegment()
        {
            HandleTranslateActiveSegment();
        }

        // ─── Original HandleAiTranslateSegment body (replaced) ──────
        // The old standalone translation logic has been replaced by the
        // unified batch pipeline below (HandleTranslateActiveSegment).
        // This dead code block is kept only to preserve line structure
        // for any pending merges.  It will be cleaned up in a future release.

        private static void _LegacyHandleAiTranslateSegment_Removed()
        {
            var instance = _currentInstance;
            if (instance == null) return;

            instance.SafeInvoke(() =>
            {
                try
                {
                    if (instance._activeDocument?.ActiveSegmentPair == null)
                    {
                        MessageBox.Show("No active segment.",
                            "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    var settings = instance._settings;
                    var aiSettings = settings?.AiSettings;
                    if (aiSettings == null)
                    {
                        MessageBox.Show(
                            "AI settings not configured.\n\nOpen Settings \u2192 AI Settings to configure a provider.",
                            "Supervertaler \u2014 AI Translate",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // Resolve API key
                    var provider = aiSettings.SelectedProvider ?? LlmModels.ProviderOpenAi;
                    string apiKey;
                    string baseUrl = null;
                    string model = aiSettings.GetSelectedModel();

                    if (provider == LlmModels.ProviderOllama)
                    {
                        apiKey = "ollama";
                        baseUrl = aiSettings.OllamaEndpoint ?? "http://localhost:11434";
                    }
                    else if (provider == LlmModels.ProviderCustomOpenAi)
                    {
                        var profile = aiSettings.GetActiveCustomProfile();
                        if (profile == null)
                        {
                            MessageBox.Show("No custom OpenAI profile configured.",
                                "Supervertaler \u2014 AI Translate",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        apiKey = profile.ApiKey;
                        baseUrl = profile.Endpoint;
                        model = profile.Model;
                    }
                    else
                    {
                        apiKey = LlmClient.ResolveApiKey(provider, aiSettings.ApiKeys);
                    }

                    if (string.IsNullOrEmpty(apiKey))
                    {
                        MessageBox.Show(
                            $"No API key configured for {provider}.\n\nOpen Settings \u2192 AI Settings to add one.",
                            "Supervertaler \u2014 AI Translate",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    var sourceLang = instance.GetDocumentSourceLanguage();
                    var targetLang = instance.GetDocumentTargetLanguage();
                    if (string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
                    {
                        MessageBox.Show("Cannot determine source/target language.",
                            "Supervertaler \u2014 AI Translate",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // Serialize source with tag placeholders if segment has inline tags
                    var sourceSegment = instance._activeDocument.ActiveSegmentPair.Source;
                    var serialization = SegmentTagHandler.Serialize(sourceSegment);
                    var hasTags = serialization.HasTags;
                    var tagMap = hasTags ? serialization.TagMap : null;
                    var sourceText = hasTags
                        ? serialization.SerializedText
                        : (sourceSegment?.ToString() ?? "");

                    if (string.IsNullOrWhiteSpace(SegmentTagHandler.StripTagPlaceholders(sourceText)))
                    {
                        MessageBox.Show("Active segment has no source text.",
                            "Supervertaler \u2014 AI Translate",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    // Get termbase terms for prompt injection (filtered by AI-disabled list)
                    // Single segment, but a fallback-served MultiTerm termbase still needs an
                    // explicit lookup for it to reach the prompt (#38).
                    TermLensEditorViewPart.PrewarmFallbackTermsFor(new[] { sourceText });

                    var allTbTerms = TermLensEditorViewPart.GetCurrentTermbaseTerms();
                    var aiCfgE = settings?.AiSettings ?? new AiSettings();
                    var termbaseTerms = allTbTerms.Where(t => aiCfgE.IsTermbaseAiEnabled(t.TermbaseId)).ToList();

                    // Resolve custom prompt from settings
                    var customPromptContent = instance.ResolveCustomPromptContent(sourceLang, targetLang);
                    var customSystemPrompt = aiSettings.CustomSystemPrompt;

                    // Collect document context for AI document type analysis
                    List<string> singleDocSegments = null;
                    if (aiSettings.IncludeDocumentContext)
                    {
                        var docCtx = instance.CollectDocumentContext();
                        singleDocSegments = docCtx.Item1;
                    }

                    // Log to batch translate panel for visibility
                    var batchControl = _control.Value.BatchTranslateControl;
                    batchControl.AppendLog($"Translating segment: \"{Truncate(sourceText, 60)}\"...");

                    // Run async – single segment, reuse TranslationPrompt + LlmClient
                    var capturedAiSettings = aiSettings;
                    Task.Run(async () =>
                    {
                        try
                        {
                            var systemPrompt = TranslationPrompt.BuildSystemPrompt(
                                sourceLang, targetLang,
                                customPromptContent, termbaseTerms, customSystemPrompt,
                                singleDocSegments,
                                capturedAiSettings.DocumentContextMaxSegments,
                                capturedAiSettings.IncludeTermMetadata);

                            var client = new LlmClient(
                                capturedAiSettings.SelectedProvider,
                                capturedAiSettings.GetSelectedModel(),
                                apiKey, baseUrl,
                                ollamaTimeoutMinutes: capturedAiSettings.OllamaTimeoutMinutes);

                            // For single segment, send it directly (not numbered batch format)
                            var userPrompt = $"Translate the following segment:\n\n{sourceText}";

                            var response = await client.SendPromptAsync(userPrompt, systemPrompt,
                                feature: PromptLogFeature.Translate);

                            if (!string.IsNullOrWhiteSpace(response))
                            {
                                // Clean up the response (remove potential numbering or quotes)
                                var translation = response.Trim();
                                if (translation.StartsWith("1. "))
                                    translation = translation.Substring(3).Trim();
                                if (translation.Length >= 2 &&
                                    ((translation.StartsWith("\"") && translation.EndsWith("\"")) ||
                                     (translation.StartsWith("\u201c") && translation.EndsWith("\u201d"))))
                                    translation = translation.Substring(1, translation.Length - 2);

                                // Capture tag state for use in UI thread
                                var capturedHasTags = hasTags;
                                var capturedTagMap = tagMap;

                                instance.SafeInvoke(() =>
                                {
                                    try
                                    {
                                        // If source had tags, try to reconstruct with proper tags
                                        if (capturedHasTags && capturedTagMap != null &&
                                            capturedTagMap.Count > 0)
                                        {
                                            var pair = instance._activeDocument.ActiveSegmentPair;
                                            if (pair != null)
                                            {
                                                bool reconstructed = SegmentTagHandler.ReconstructTarget(
                                                    pair.Target, pair.Source,
                                                    translation, capturedTagMap);

                                                if (reconstructed)
                                                {
                                                    batchControl.AppendLog(
                                                        $"Done (with tags): \"{Truncate(SegmentTagHandler.StripTagPlaceholders(translation), 60)}\"");
                                                    return;
                                                }
                                            }

                                            // Reconstruction failed – strip placeholders, use plain text
                                            translation = SegmentTagHandler.StripTagPlaceholders(translation);
                                        }

                                        // If translation contains newlines, use ProcessSegmentPair
                                        // with text cloning to preserve soft returns (e.g. Excel, Visio).
                                        // The editor's Replace() API converts \n to paragraph marks.
                                        if (translation.IndexOf('\n') >= 0 || translation.IndexOf('\r') >= 0)
                                        {
                                            var activePair = instance._activeDocument.ActiveSegmentPair;
                                            if (activePair != null)
                                            {
                                                instance._activeDocument.ProcessSegmentPair(activePair, "Supervertaler",
                                                    (sp, cancel) =>
                                                    {
                                                        var textTpl = SegmentTagHandler.FindFirstText(sp.Source);
                                                        if (textTpl != null)
                                                        {
                                                            sp.Target.Clear();
                                                            var textClone = (IText)textTpl.Clone();
                                                            textClone.Properties.Text = translation;
                                                            sp.Target.Add(textClone);
                                                        }
                                                    });
                                                batchControl.AppendLog(
                                                    $"Done: \"{Truncate(translation, 60)}\"");
                                                return;
                                            }
                                        }

                                        instance._activeDocument.Selection.Target.Replace(
                                            translation, "Supervertaler");
                                        batchControl.AppendLog(
                                            $"Done: \"{Truncate(translation, 60)}\"");
                                    }
                                    catch (Exception ex)
                                    {
                                        batchControl.AppendLog(
                                            $"Failed to write translation: {ex.Message}", true);
                                    }
                                });
                            }
                            else
                            {
                                instance.SafeInvoke(() =>
                                    batchControl.AppendLog("Empty response from AI provider.", true));
                            }
                        }
                        catch (Exception ex)
                        {
                            instance.SafeInvoke(() =>
                                batchControl.AppendLog(
                                    $"AI translate failed: {ex.Message}", true));
                        }
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Unexpected error: {ex.Message}",
                        "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }

        // ─── Ctrl+T: Translate Active Segment via Batch Pipeline ──

        /// <summary>
        /// Translates the active segment using the batch translate pipeline
        /// (same provider, prompt, and termbase settings as the Batch Translate tab).
        /// Called by TranslateActiveSegmentAction (Ctrl+T).
        /// </summary>
        public static void HandleTranslateActiveSegment()
        {
            var instance = _currentInstance;
            if (instance == null)
            {
                // The Assistant pane was never opened this session, so the
                // instance/active-document tracking isn't wired up yet (#41). Run
                // a pane-independent fallback so Ctrl+T / right-click still work.
                // The pane-open path below is left untouched to avoid any
                // regression to the common case.
                TranslateActiveSegmentStandalone();
                return;
            }

            instance.SafeInvoke(() =>
            {
                try
                {
                    if (instance._activeDocument?.ActiveSegmentPair == null)
                    {
                        MessageBox.Show("No active segment.",
                            "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    // Don't start if a batch is already running
                    if (instance._batchTranslator != null)
                    {
                        _control.Value.BatchTranslateControl.AppendLog(
                            "A batch translation is already running.", true);
                        return;
                    }

                    var settings = instance._settings;
                    var aiSettings = settings?.AiSettings;
                    if (aiSettings == null)
                    {
                        MessageBox.Show(
                            "AI settings not configured.\n\nOpen Settings \u2192 AI Settings to configure a provider.",
                            "Supervertaler",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // Resolve provider (same logic as batch translate)
                    var provider = aiSettings.SelectedProvider ?? LlmModels.ProviderOpenAi;
                    string apiKey;
                    string baseUrl = null;
                    string model = aiSettings.GetSelectedModel();

                    if (provider == LlmModels.ProviderOllama)
                    {
                        apiKey = "ollama";
                        baseUrl = aiSettings.OllamaEndpoint ?? "http://localhost:11434";
                    }
                    else if (provider == LlmModels.ProviderCustomOpenAi)
                    {
                        var profile = aiSettings.GetActiveCustomProfile();
                        if (profile == null)
                        {
                            _control.Value.BatchTranslateControl.AppendLog(
                                "No custom OpenAI profile configured.", true);
                            return;
                        }
                        apiKey = profile.ApiKey;
                        baseUrl = profile.Endpoint;
                        model = profile.Model;
                    }
                    else
                    {
                        apiKey = LlmClient.ResolveApiKey(provider, aiSettings.ApiKeys);
                    }

                    if (string.IsNullOrEmpty(apiKey))
                    {
                        _control.Value.BatchTranslateControl.AppendLog(
                            $"No API key configured for {provider}. Open Settings \u2192 AI Settings to add one.", true);
                        return;
                    }

                    var sourceLang = instance.GetDocumentSourceLanguage();
                    var targetLang = instance.GetDocumentTargetLanguage();
                    if (string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
                    {
                        _control.Value.BatchTranslateControl.AppendLog(
                            "Cannot determine source/target language from document.", true);
                        return;
                    }

                    // Collect only the active segment
                    var pair = instance._activeDocument.ActiveSegmentPair;
                    var sourceSegment = pair.Source;
                    var serialization = SegmentTagHandler.Serialize(sourceSegment);
                    var hasTags = serialization.HasTags;
                    var sourceText = hasTags
                        ? serialization.SerializedText
                        : (sourceSegment?.ToString() ?? "");

                    if (string.IsNullOrWhiteSpace(SegmentTagHandler.StripTagPlaceholders(sourceText)))
                    {
                        _control.Value.BatchTranslateControl.AppendLog(
                            "Active segment has no source text.");
                        return;
                    }

                    // Always store ISegmentPair so ProcessSegmentPair can be used
                    // directly (avoids async SetActiveSegmentPair issues and ensures
                    // correct soft return handling for Excel/Visio segments).
                    var segments = new List<BatchSegment>
                    {
                        new BatchSegment
                        {
                            Index = 0,
                            SourceText = sourceText,
                            ExistingTarget = pair.Target != null
                                ? SegmentTagHandler.GetFinalText(pair.Target) : "",
                            SegmentPairRef = pair,
                            HasTags = hasTags,
                            TagMap = hasTags ? serialization.TagMap : null
                        }
                    };

                    // Get termbase terms (same filtering as batch translate)
                    var allTerms = TermLensEditorViewPart.GetCurrentTermbaseTerms();
                    var aiCfgD = aiSettings ?? new AiSettings();
                    var termbaseTerms = allTerms.Where(t => aiCfgD.IsTermbaseAiEnabled(t.TermbaseId)).ToList();

                    // Resolve custom prompt (from batch translate tab selection)
                    var batchControl = _control.Value.BatchTranslateControl;
                    var selectedPromptPath = batchControl.GetSelectedPromptPath();
                    aiSettings.SelectedPromptPath = selectedPromptPath;

                    var customPromptContent = instance.ResolveCustomPromptContent(sourceLang, targetLang);
                    var customSystemPrompt = aiSettings.CustomSystemPrompt;

                    // The same document context and memory-bank context a batch
                    // run gets. These two blocks were the entire quality gap
                    // between single-segment and batch translation: identical
                    // prompt, provider and termbase, but the model received one
                    // isolated sentence - no register, no disambiguation, no
                    // consistency anchor. (Reported by a user.)
                    List<string> docSegments = null;
                    if (aiSettings.IncludeDocumentContext)
                        docSegments = instance.CollectDocumentContext().Item1;
                    var kbContext = instance.LoadKbContextForPrompt(
                        instance.GetProjectName(), sourceLang, targetLang);

                    // Log and run
                    batchControl.AppendLog(
                        $"Ctrl+T: translating \"{Truncate(SegmentTagHandler.StripTagPlaceholders(sourceText), 60)}\"...");

                    instance._batchCts = new CancellationTokenSource();
                    instance._batchTranslator = new BatchTranslator();

                    instance._batchTranslator.SegmentTranslated += instance.OnBatchSegmentTranslated;
                    instance._batchTranslator.Completed += instance.OnBatchCompleted;

                    var ct = instance._batchCts.Token;

                    Task.Run(async () =>
                    {
                        try
                        {
                            await instance._batchTranslator.TranslateAsync(
                                segments, sourceLang, targetLang,
                                aiSettings, termbaseTerms, 1, ct,
                                customPromptContent, customSystemPrompt,
                                docSegments, kbContext);
                        }
                        catch (Exception ex)
                        {
                            instance.SafeInvoke(() =>
                            {
                                batchControl.AppendLog($"Ctrl+T failed: {ex.Message}", true);
                                batchControl.SetRunning(false);
                            });
                        }
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Unexpected error: {ex.Message}",
                        "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }

        /// <summary>
        /// Pane-independent Ctrl+T fallback used when the Assistant ViewPart has not
        /// been initialized this session (#41). Reads the active document from the
        /// EditorController and settings from disk, runs the same BatchTranslator
        /// pipeline with a local write-back, and marshals to the UI thread via the
        /// captured SynchronizationContext. Never opens the Assistant pane.
        /// </summary>
        private static void TranslateActiveSegmentStandalone()
        {
            try
            {
                // Captured on the UI thread (editor action runs on it) so the
                // BatchTranslator's background events can write on the UI thread.
                var ui = System.Threading.SynchronizationContext.Current;

                var editor = SdlTradosStudio.Application.GetController<EditorController>();
                var doc = editor?.ActiveDocument;
                if (doc?.ActiveSegmentPair == null)
                {
                    MessageBox.Show("No active segment.",
                        "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var aiSettings = SettingsService.Current?.AiSettings;
                if (aiSettings == null)
                {
                    MessageBox.Show(
                        "AI settings not configured.\n\nOpen Settings → AI Settings to configure a provider.",
                        "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var provider = aiSettings.SelectedProvider ?? LlmModels.ProviderOpenAi;
                string apiKey;
                string baseUrl = null;
                string model = aiSettings.GetSelectedModel();
                if (provider == LlmModels.ProviderOllama)
                {
                    apiKey = "ollama";
                    baseUrl = aiSettings.OllamaEndpoint ?? "http://localhost:11434";
                }
                else if (provider == LlmModels.ProviderCustomOpenAi)
                {
                    var profile = aiSettings.GetActiveCustomProfile();
                    if (profile == null)
                    {
                        MessageBox.Show("No custom OpenAI profile configured.",
                            "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    apiKey = profile.ApiKey; baseUrl = profile.Endpoint; model = profile.Model;
                }
                else
                {
                    apiKey = LlmClient.ResolveApiKey(provider, aiSettings.ApiKeys);
                }
                if (string.IsNullOrEmpty(apiKey))
                {
                    MessageBox.Show(
                        $"No API key configured for {provider}.\n\nOpen Settings → AI Settings to add one.",
                        "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string sourceLang = "", targetLang = "";
                try
                {
                    var f = doc.ActiveFile;
                    sourceLang = f?.SourceFile?.Language?.DisplayName ?? "";
                    targetLang = f?.Language?.DisplayName ?? "";
                }
                catch { }

                var pair = doc.ActiveSegmentPair;
                var serialization = SegmentTagHandler.Serialize(pair.Source);
                var hasTags = serialization.HasTags;
                var sourceText = hasTags ? serialization.SerializedText : (pair.Source?.ToString() ?? "");
                if (string.IsNullOrWhiteSpace(SegmentTagHandler.StripTagPlaceholders(sourceText)))
                {
                    BridgeLog.Write("Ctrl+T: active segment has no source text.");
                    return;
                }

                var segments = new List<BatchSegment>
                {
                    new BatchSegment
                    {
                        Index = 0,
                        SourceText = sourceText,
                        ExistingTarget = pair.Target != null ? SegmentTagHandler.GetFinalText(pair.Target) : "",
                        SegmentPairRef = pair,
                        HasTags = hasTags,
                        TagMap = hasTags ? serialization.TagMap : null
                    }
                };

                // Single segment, but a fallback-served MultiTerm termbase still needs an
                // explicit lookup for it to reach the prompt (#38).
                TermLensEditorViewPart.PrewarmFallbackTermsFor(new[] { sourceText });

                var allTerms = TermLensEditorViewPart.GetCurrentTermbaseTerms();
                var termbaseTerms = allTerms.Where(t => aiSettings.IsTermbaseAiEnabled(t.TermbaseId)).ToList();

                // Document context, same as a batch run. SuperMemory context is
                // pane state and the pane has never been opened on this path,
                // so that block stays out here - still a far cry from the bare
                // sentence this used to send.
                List<string> docSegments = null;
                if (aiSettings.IncludeDocumentContext)
                    docSegments = CollectDocumentSourceSegments(doc);

                // Resolve the selected custom prompt from disk (no pane needed).
                string customPromptContent = null;
                try
                {
                    var lib = TermLensEditorViewPart.GetPromptLibrary();
                    var sel = aiSettings.SelectedPromptPath;
                    if (!string.IsNullOrEmpty(sel) && lib != null)
                    {
                        var p = PromptPaths.Find(lib, sel);   // marker-tolerant (#100)
                        if (p != null && !string.IsNullOrWhiteSpace(p.Content))
                            customPromptContent = PromptLibrary.ApplyVariables(p.Content, sourceLang, targetLang);
                    }
                }
                catch { }
                var customSystemPrompt = aiSettings.CustomSystemPrompt;

                BridgeLog.Write($"Ctrl+T (standalone): translating \"{Truncate(SegmentTagHandler.StripTagPlaceholders(sourceText), 60)}\"...");

                var worker = new BatchTranslator();
                worker.SegmentTranslated += (s, e) =>
                {
                    Action doWrite = () => WriteTranslatedSegment(doc, e);
                    if (ui != null) ui.Send(_ => doWrite(), null);   // sync: WriteSucceeded set before worker reads it
                    else doWrite();
                };
                worker.Completed += (s, e) =>
                    BridgeLog.Write($"Ctrl+T (standalone): done ({e.Translated} translated, {e.Failed} failed).");

                var cts = new CancellationTokenSource();
                Task.Run(async () =>
                {
                    try
                    {
                        await worker.TranslateAsync(
                            segments, sourceLang, targetLang,
                            aiSettings, termbaseTerms, 1, cts.Token,
                            customPromptContent, customSystemPrompt,
                            docSegments);
                    }
                    catch (Exception ex)
                    {
                        BridgeLog.Write($"Ctrl+T (standalone) failed: {ex.Message}");
                        if (ui != null)
                            ui.Post(_ => MessageBox.Show($"Translate failed: {ex.Message}",
                                "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Error), null);
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unexpected error: {ex.Message}",
                    "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Pane-independent single-segment write-back, mirroring the tag-aware logic
        /// in OnBatchSegmentTranslated (tagged → ReconstructTarget with plain-text
        /// fallback; untagged → clone the source IText). Sets e.WriteSucceeded.
        /// </summary>
        private static void WriteTranslatedSegment(IStudioDocument doc, BatchSegmentResultEventArgs e)
        {
            try
            {
                if (e.SegmentPairRef == null || doc == null) { e.WriteSucceeded = false; return; }
                var pair = e.SegmentPairRef as ISegmentPair;
                if (pair == null) { e.WriteSucceeded = false; return; }

                doc.ProcessSegmentPair(pair, "Supervertaler", (sp, cancel) =>
                {
                    if (e.HasTags && e.TagMap != null && e.TagMap.Count > 0)
                    {
                        bool reconstructed = SegmentTagHandler.ReconstructTarget(
                            sp.Target, sp.Source, e.Translation, e.TagMap);
                        if (!reconstructed)
                        {
                            var plain = SegmentTagHandler.StripTagPlaceholders(e.Translation);
                            var tpl = SegmentTagHandler.FindFirstText(sp.Source);
                            if (tpl != null && !string.IsNullOrEmpty(plain))
                            {
                                sp.Target.Clear();
                                var clone = (IText)tpl.Clone();
                                clone.Properties.Text = plain;
                                sp.Target.Add(clone);
                            }
                        }
                        return;
                    }
                    var textTpl = SegmentTagHandler.FindFirstText(sp.Source);
                    if (textTpl != null && !string.IsNullOrEmpty(e.Translation))
                    {
                        sp.Target.Clear();
                        var clone = (IText)textTpl.Clone();
                        clone.Properties.Text = e.Translation;
                        sp.Target.Add(clone);
                    }
                });
            }
            catch (Exception ex)
            {
                e.WriteSucceeded = false;
                BridgeLog.Write($"Ctrl+T write error: {ex.Message}");
            }
        }

        // ─── AutoTagger ───────────────────────────────────────────

        /// <summary>
        /// AutoTagger: place the active source segment's inline tags into the
        /// existing (tag-free) target via the AI, without changing the words.
        /// Validated before writing; on failure the segment is left untouched.
        ///
        /// Deliberately independent of the Supervertaler Assistant pane: it reads
        /// the active document from the EditorController and settings from disk, so
        /// it works (and never pops the pane open) even when the pane was never
        /// opened this session. Runs from the editor action on the UI thread.
        /// </summary>
        public static void HandleAutoTagActiveSegment()
        {
            try
            {
                // Captured on the UI thread (editor actions run on it) so the
                // background LLM call can marshal the write-back without needing
                // the Assistant pane's control to exist.
                var ui = System.Threading.SynchronizationContext.Current;

                var editor = SdlTradosStudio.Application.GetController<EditorController>();
                var doc = editor?.ActiveDocument;
                if (doc?.ActiveSegmentPair == null)
                {
                    MessageBox.Show("No active segment.",
                        "AutoTagger", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var aiSettings = SettingsService.Current?.AiSettings;
                if (aiSettings == null)
                {
                    MessageBox.Show(
                        "AI settings not configured.\n\nOpen Settings → AI Settings to configure a provider.",
                        "AutoTagger", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Resolve provider/model/key
                var provider = aiSettings.SelectedProvider ?? LlmModels.ProviderOpenAi;
                string apiKey;
                string baseUrl = null;
                string model = aiSettings.GetSelectedModel();
                if (provider == LlmModels.ProviderOllama)
                {
                    apiKey = "ollama";
                    baseUrl = aiSettings.OllamaEndpoint ?? "http://localhost:11434";
                }
                else if (provider == LlmModels.ProviderCustomOpenAi)
                {
                    var profile = aiSettings.GetActiveCustomProfile();
                    if (profile == null)
                    {
                        MessageBox.Show("No custom OpenAI profile configured.",
                            "AutoTagger", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    apiKey = profile.ApiKey; baseUrl = profile.Endpoint; model = profile.Model;
                }
                else
                {
                    apiKey = LlmClient.ResolveApiKey(provider, aiSettings.ApiKeys);
                }
                if (string.IsNullOrEmpty(apiKey))
                {
                    MessageBox.Show(
                        $"No API key configured for {provider}.\n\nOpen Settings → AI Settings to add one.",
                        "AutoTagger", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var pair = doc.ActiveSegmentPair;
                var serialization = SegmentTagHandler.Serialize(pair.Source);
                if (!serialization.HasTags)
                {
                    MessageBox.Show("The source segment has no inline tags, so there is nothing to place.",
                        "AutoTagger", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var plainTarget = pair.Target != null ? SegmentTagHandler.GetFinalText(pair.Target) : "";
                if (string.IsNullOrWhiteSpace(plainTarget))
                {
                    MessageBox.Show("Translate this segment first – AutoTagger places tags into an existing translation.",
                        "AutoTagger", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var serializedSource = serialization.SerializedText;
                var tagMap = serialization.TagMap;
                var userPrompt = AutoTagger.BuildUserPrompt(
                    aiSettings.GetAutoTaggerInstruction(), serializedSource, plainTarget);

                BridgeLog.Write($"AutoTagger: placing {AutoTagger.ExtractTags(serializedSource).Count} tag(s)...");

                var client = new LlmClient(provider, model, apiKey, baseUrl);

                Task.Run(async () =>
                {
                    string finalMarker = null;
                    string failReason = "no result";
                    try
                    {
                        for (int attempt = 0; attempt < 2 && finalMarker == null; attempt++)
                        {
                            var resp = await client.SendPromptAsync(
                                userPrompt, AutoTagger.SystemPrompt,
                                feature: PromptLogFeature.AutoTag);
                            var candidate = (resp ?? "").Trim();
                            if (AutoTagger.Validate(serializedSource, candidate, plainTarget, out failReason))
                                finalMarker = AutoTagger.ReinsertTagsIntoExactTarget(candidate, plainTarget) ?? candidate;
                        }
                    }
                    catch (Exception ex) { failReason = ex.Message; }

                    Action apply = () =>
                    {
                        if (finalMarker == null)
                        {
                            BridgeLog.Write($"AutoTagger: target left unchanged – {failReason}");
                            MessageBox.Show(
                                "AutoTagger couldn't place the tags reliably, so the target was left unchanged.\n\n"
                                + "Reason: " + failReason,
                                "AutoTagger", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        bool ok = WriteAutoTaggedTarget(doc, pair, finalMarker, tagMap);
                        BridgeLog.Write(ok ? "AutoTagger: tags placed." : "AutoTagger: write failed.");
                        if (!ok)
                            MessageBox.Show("AutoTagger could not write the tagged target.",
                                "AutoTagger", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    };

                    if (ui != null) ui.Post(_ => apply(), null);
                    else apply();
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unexpected error: {ex.Message}",
                    "AutoTagger", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Writes a reconstructed (tagged) target into the segment via ProcessSegmentPair.
        /// Returns true when ReconstructTarget succeeded. Unlike the batch path it does
        /// NOT fall back to plain text – AutoTagger must never strip the existing tags.
        /// </summary>
        private static bool WriteAutoTaggedTarget(IStudioDocument doc, ISegmentPair pair, string markerText, Dictionary<int, TagInfo> tagMap)
        {
            try
            {
                if (pair == null || doc == null) return false;
                bool wrote = false;
                doc.ProcessSegmentPair(pair, "Supervertaler", (sp, cancel) =>
                {
                    wrote = SegmentTagHandler.ReconstructTarget(sp.Target, sp.Source, markerText, tagMap);
                });
                return wrote;
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"AutoTagger write error: {ex.Message}");
                return false;
            }
        }

        // ─── Helpers ──────────────────────────────────────────────

        /// <summary>
        /// Extracts TM match information from the active segment's translation origin.
        /// Returns the current match info if it originated from a translation memory.
        /// </summary>
        private List<TmMatch> GetTmMatches()
        {
            var matches = new List<TmMatch>();
            try
            {
                var pair = _activeDocument?.ActiveSegmentPair;
                if (pair == null) return matches;

                var origin = pair.Properties?.TranslationOrigin;
                if (origin == null) return matches;

                // Only include actual TM-originated matches
                var originType = origin.OriginType;
                if (string.IsNullOrEmpty(originType)) return matches;

                // Include TM matches and auto-propagated segments (which originate from TM)
                if (originType == "tm" || originType == "auto-propagated")
                {
                    var sourceText = pair.Source != null ? SegmentTagHandler.GetFinalText(pair.Source) : null;   // #97
                    var targetText = pair.Target != null
                        ? SegmentTagHandler.GetFinalText(pair.Target) : null;

                    if (!string.IsNullOrEmpty(sourceText) && !string.IsNullOrEmpty(targetText))
                    {
                        matches.Add(new TmMatch
                        {
                            SourceText = sourceText,
                            TargetText = targetText,
                            MatchPercentage = origin.MatchPercent,
                            TmName = origin.OriginSystem ?? ""
                        });
                    }
                }
            }
            catch (Exception)
            {
                // Segment properties may not be accessible during transitions
            }
            return matches;
        }

        /// <summary>
        /// Maps a tool name to a user-friendly status message shown in the thinking indicator.
        /// </summary>
        private static string FormatToolStatus(string toolName)
        {
            switch (toolName)
            {
                case "studio_list_projects": return "Checking Trados projects\u2026";
                case "studio_get_project": return "Looking up project details\u2026";
                case "studio_get_project_statistics": return "Reading project statistics\u2026";
                case "studio_get_file_status": return "Checking file status\u2026";
                case "studio_list_project_termbases": return "Listing project termbases\u2026";
                case "studio_get_tm_info": return "Reading TM details\u2026";
                case "studio_search_tm": return "Searching translation memory\u2026";
                case "studio_list_tms": return "Listing translation memories\u2026";
                case "studio_list_project_templates": return "Listing project templates\u2026";
                default: return "Querying Trados Studio\u2026";
            }
        }

        private void AddErrorMessage(string text)
        {
            var msg = new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = text
            };
            _chatHistory.Add(msg);
            _control.Value.AddMessage(msg);
        }

        /// <summary>
        /// Returns the last N messages for the API context window, constrained by
        /// a character budget (~50K tokens ≈ 200K chars) to prevent runaway costs
        /// from accumulated large prompts (e.g. {{PROJECT}} expansions).
        /// Always includes at least the most recent message.
        /// </summary>
        private static List<ChatMessage> BuildMessageWindow(List<ChatMessage> history, int maxMessages)
        {
            const int maxChars = 200_000; // ~50K tokens

            if (history.Count == 0)
                return new List<ChatMessage>();

            // Start from the most recent message and work backwards
            var result = new List<ChatMessage>();
            var totalChars = 0;
            var startIdx = Math.Max(0, history.Count - maxMessages);

            for (int i = history.Count - 1; i >= startIdx; i--)
            {
                var msgLen = history[i].Content?.Length ?? 0;

                // Always include the most recent message
                if (i == history.Count - 1)
                {
                    result.Insert(0, history[i]);
                    totalChars += msgLen;
                    continue;
                }

                // Stop adding older messages if we'd exceed the budget
                if (totalChars + msgLen > maxChars)
                    break;

                result.Insert(0, history[i]);
                totalChars += msgLen;
            }

            return result;
        }

        // ─── Chat History Persistence ─────────────────────────────

        private void SaveChatHistory()
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(List<ChatMessage>));
                var path = UserDataPath.ChatHistoryFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                    serializer.WriteObject(fs, _chatHistory);
            }
            catch { /* ignore save failures */ }
        }

        private void LoadChatHistory()
        {
            try
            {
                var path = UserDataPath.ChatHistoryFilePath;
                if (!File.Exists(path)) return;
                var serializer = new DataContractJsonSerializer(typeof(List<ChatMessage>));
                List<ChatMessage> history;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                    history = (List<ChatMessage>)serializer.ReadObject(fs);
                if (history == null || history.Count == 0) return;
                _chatHistory.AddRange(history);
                foreach (var msg in history)
                    _control.Value.AddMessage(msg);
            }
            catch { /* ignore load failures – start with empty history */ }
        }

        private string BuildLangPairString()
        {
            var src = GetDocumentSourceLanguage();
            var tgt = GetDocumentTargetLanguage();
            if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(tgt))
                return $"{LanguageUtils.ShortenLanguageName(src)} \u2192 {LanguageUtils.ShortenLanguageName(tgt)}";
            return null;
        }

        private string GetDocumentSourceLanguage()
        {
            try
            {
                var file = _activeDocument?.ActiveFile;
                if (file != null)
                {
                    var lang = file.SourceFile?.Language;
                    if (lang != null)
                    {
                        _cachedSourceLang = lang.DisplayName;
                        return _cachedSourceLang;
                    }
                }
            }
            catch (Exception) { }
            return _cachedSourceLang;
        }

        private string GetDocumentTargetLanguage()
        {
            try
            {
                var file = _activeDocument?.ActiveFile;
                if (file != null)
                {
                    var lang = file.Language;
                    if (lang != null)
                    {
                        _cachedTargetLang = lang.DisplayName;
                        return _cachedTargetLang;
                    }
                }
            }
            catch (Exception) { }
            return _cachedTargetLang;
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;
            return text.Substring(0, maxLength) + "\u2026";
        }

        private void SafeInvoke(Action action)
        {
            var ctrl = _control.Value;
            if (ctrl.InvokeRequired)
                ctrl.BeginInvoke(action);
            else
                action();
        }

        // ─── Document Context Helpers ─────────────────────────────

        /// <summary>
        /// Collects all source segment texts from the active document.
        /// Also determines the 0-based index of the active segment.
        /// Returns (segments, activeIndex) where activeIndex is -1 if not found.
        /// </summary>
        /// <summary>
        /// Collects the full document as bilingual (source, target) pairs for use as
        /// proofreading context. Unlike <see cref="CollectDocumentContext"/> which is
        /// source-only and used by Batch Translate / chat, this also includes the
        /// existing target so the proofreader can verify target-side consistency
        /// across the whole document – not just within the current 20-segment batch.
        /// </summary>
        private List<(string source, string target)> CollectBilingualDocumentContext()
        {
            var segments = new List<(string source, string target)>();

            if (_activeDocument == null)
                return segments;

            try
            {
                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    // #97: the proofreader sees its segments as <tN>; its context must match.
                    var sourceText = SegmentTagHandler.ToModelText(pair.Source);
                    var targetText = SegmentTagHandler.ToModelText(pair.Target);
                    segments.Add((sourceText, targetText));
                }
            }
            catch (Exception)
            {
                // Document may not be accessible during transitions
            }

            return segments;
        }

        /// <summary>Source-side document context over a caller-supplied
        /// document, for the pane-less Ctrl+T fallback (#41) where the instance
        /// tracking behind <see cref="CollectDocumentContext"/> was never wired
        /// up. Same collection, minus the active-index bookkeeping the batch
        /// prompt does not use.</summary>
        private static List<string> CollectDocumentSourceSegments(IStudioDocument doc)
        {
            var segments = new List<string>();
            if (doc == null) return segments;
            try
            {
                foreach (var pair in doc.SegmentPairs)
                    segments.Add(SegmentTagHandler.ToModelText(pair.Source));   // #97
            }
            catch { /* document may not be accessible during transitions */ }
            return segments;
        }

        private Tuple<List<string>, int> CollectDocumentContext()
        {
            var segments = new List<string>();
            int activeIndex = -1;

            if (_activeDocument == null)
                return Tuple.Create(segments, activeIndex);

            try
            {
                var activePair = _activeDocument.ActiveSegmentPair;
                string activeSegId = null;
                string activePuId = null;

                if (activePair != null)
                {
                    try
                    {
                        activeSegId = activePair.Properties.Id.Id;
                        var parentPU = _activeDocument.GetParentParagraphUnit(activePair);
                        activePuId = parentPU.Properties.ParagraphUnitId.Id;
                    }
                    catch { }
                }

                int index = 0;
                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    // #97: placeholders, as the batch segments - never <cf ...>.
                    var sourceText = SegmentTagHandler.ToModelText(pair.Source);
                    segments.Add(sourceText);

                    // Match against active segment
                    if (activeIndex < 0 && activePuId != null && activeSegId != null)
                    {
                        try
                        {
                            var parentPU = _activeDocument.GetParentParagraphUnit(pair);
                            var puId = parentPU.Properties.ParagraphUnitId.Id;
                            var segId = pair.Properties.Id.Id;

                            if (puId == activePuId && segId == activeSegId)
                                activeIndex = index;
                        }
                        catch { }
                    }

                    index++;
                }
            }
            catch (Exception)
            {
                // Document may not be accessible during transitions
            }

            return Tuple.Create(segments, activeIndex);
        }

        /// <summary>
        /// Gets surrounding segments (source + target) around the active segment.
        /// Returns a list of [source, target] string arrays.
        /// </summary>
        /// <param name="serializeTags">When true, source and target are serialized
        /// with the <c>&lt;t1/&gt;</c>-style markers get_segments/update_segments use.
        /// The MCP bridge needs this; the chat/prompt path keeps the historical
        /// plain-text rendering, so an LLM writing prose is not shown tag noise.</param>
        /// <summary>
        /// Serializes one side of a segment pair the way get_segments does: the
        /// normalised <c>&lt;t1/&gt;</c> / <c>&lt;t2&gt;…&lt;/t2&gt;</c> markers that
        /// update_segments accepts, with semantic names applied. Use this for
        /// anything the MCP bridge returns, so every tool describes a segment the
        /// same way and a marker copied from one tool is valid in another.
        /// U+2028/U+2029 are flattened, as the chat path has always done.
        /// </summary>
        private static string SerializeForBridge(ISegment side)
        {
            if (side == null) return "";
            try
            {
                var ser = SegmentTagHandler.Serialize(side);
                var text = Core.Export.BilingualTagNamer.ApplySemanticNames(
                    ser.SerializedText ?? "", ser.TagMap);
                return (text ?? "").Replace("\u2028", " ").Replace("\u2029", " ");
            }
            catch
            {
                // Never let a serialization quirk blank the whole snapshot.
                return (side.ToString() ?? "").Replace("\u2028", " ").Replace("\u2029", " ");
            }
        }

        /// <param name="serializeTags">When true, source and target are serialized
        /// with the <c>&lt;t1/&gt;</c>-style markers get_segments/update_segments use.
        /// The MCP bridge needs this; the chat/prompt path keeps the historical
        /// plain-text rendering, so an LLM writing prose is not shown tag noise.</param>
        private List<string[]> GetSurroundingSegments(int count, bool serializeTags = false)
        {
            var result = new List<string[]>();
            if (_activeDocument == null || count <= 0)
                return result;

            try
            {
                var activePair = _activeDocument.ActiveSegmentPair;
                if (activePair == null) return result;

                string activeSegId = null;
                string activePuId = null;
                try
                {
                    activeSegId = activePair.Properties.Id.Id;
                    var parentPU = _activeDocument.GetParentParagraphUnit(activePair);
                    activePuId = parentPU.Properties.ParagraphUnitId.Id;
                }
                catch { return result; }

                if (activePuId == null || activeSegId == null)
                    return result;

                // Collect all pairs into a list for random access
                var allPairs = new List<Tuple<string, string>>(); // source, target
                int activeIdx = -1;
                int idx = 0;

                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    var src = serializeTags
                        ? SerializeForBridge(pair.Source)
                        : (pair.Source?.ToString() ?? "");
                    var tgt = pair.Target == null ? ""
                        : serializeTags
                            ? SerializeForBridge(pair.Target)
                            : SegmentTagHandler.GetFinalText(pair.Target);
                    allPairs.Add(Tuple.Create(src, tgt));

                    if (activeIdx < 0)
                    {
                        try
                        {
                            var parentPU = _activeDocument.GetParentParagraphUnit(pair);
                            var puId = parentPU.Properties.ParagraphUnitId.Id;
                            var segId = pair.Properties.Id.Id;
                            if (puId == activePuId && segId == activeSegId)
                                activeIdx = idx;
                        }
                        catch { }
                    }

                    idx++;
                }

                if (activeIdx < 0) return result;

                // Collect 'count' segments before and after
                int start = Math.Max(0, activeIdx - count);
                int end = Math.Min(allPairs.Count - 1, activeIdx + count);

                for (int i = start; i <= end; i++)
                {
                    if (i == activeIdx) continue; // skip the active segment itself
                    result.Add(new[] { allPairs[i].Item1, allPairs[i].Item2 });
                }
            }
            catch (Exception)
            {
                // Document may not be accessible during transitions
            }

            return result;
        }

        /// <summary>
        /// Gets the Trados project name from the active document.
        /// </summary>
        private string GetProjectName()
        {
            try
            {
                var project = _activeDocument?.Project as FileBasedProject;
                var name = project?.GetProjectInfo()?.Name;
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Gets the file name of the active document.
        /// </summary>
        private string GetFileName()
        {
            try
            {
                return _activeDocument?.ActiveFile?.Name;
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Lets go of everything this ViewPart holds open inside the memory-bank
        /// folders, so another part of the plugin can rename or move one.
        ///
        /// <para>The inbox watcher is the reason this is needed: a
        /// FileSystemWatcher holds a handle on the directory it watches, and
        /// Windows will not rename a folder above an open handle. Renaming the
        /// ACTIVE bank therefore failed with "Access to the path is denied" and
        /// blamed Obsidian, when the program holding it open was this one.</para>
        ///
        /// <para>Always pair with <see cref="ReacquireMemoryBankHandles"/> in a
        /// finally block: leaving the watcher off means the inbox count silently
        /// stops updating for the rest of the session.</para>
        /// </summary>
        public static void ReleaseMemoryBankHandles()
        {
            var instance = _currentInstance;
            if (instance == null) return;

            try { instance._inboxWatcher?.Dispose(); } catch { }
            instance._inboxWatcher = null;

            // The cached reader is bound to a bank directory by name, so it has
            // to go too or it would keep answering from the old path.
            instance._kbReader = null;
            instance._kbReaderBankName = null;
        }

        /// <summary>
        /// Re-attaches after <see cref="ReleaseMemoryBankHandles"/>. Safe to call
        /// even if the bank has been renamed or removed - StartInboxWatcher reads
        /// the active bank from settings each time.
        /// </summary>
        public static void ReacquireMemoryBankHandles()
        {
            var instance = _currentInstance;
            if (instance == null) return;

            try { instance.StartInboxWatcher(); } catch { }
            try { instance.RefreshSuperMemoryInboxCount(); } catch { }
        }

        /// <summary>
        /// Brings this panel into line with the settings after the dialog has
        /// committed. Called by <see cref="SettingsDialog"/> for every gear
        /// icon, including ones in other panels — which is why it is static and
        /// tolerates never having been opened.
        ///
        /// <para>No reload from disk: the dialog wrote the shared instance, so
        /// there is nothing newer on disk to fetch. The reload existed only
        /// because this panel used to hold a copy.</para>
        ///
        /// <para>Absorbed the block that used to sit inline in
        /// <c>OnSettingsRequested</c>, which did the same work minus the bank
        /// dropdown — so a bank created in the dialog appeared only if you had
        /// opened Settings from the *other* panel.</para>
        /// </summary>
        public static void RefreshAfterSettingsChanged()
        {
            var instance = _currentInstance;
            if (instance == null) return;
            instance.UpdateProviderDisplay();
            instance.UpdateBatchProviderDisplay();
            instance.PopulateBatchPromptDropdown();
            // Pick up any bank-list changes (new bank added via settings dialog,
            // rename, etc.) and re-select the active bank without firing the
            // toolbar's change event.
            instance.RefreshMemoryBankDropdown();
        }

        /// <summary>
        /// Called by the launcher tab to activate/focus the AI Assistant panel.
        /// </summary>
        public static void Focus()
        {
            if (_currentInstance != null)
                _control.Value.FocusInput();
        }

        public override void Dispose()
        {
            _chatCts?.Cancel();
            _chatCts?.Dispose();

            // Cancel any running batch translation
            _batchCts?.Cancel();
            _batchCts?.Dispose();
            _batchCts = null;

            if (_batchTranslator != null)
            {
                _batchTranslator.Progress -= OnBatchProgress;
                _batchTranslator.SegmentTranslated -= OnBatchSegmentTranslated;
                _batchTranslator.Completed -= OnBatchCompleted;
                _batchTranslator = null;
            }

            // Cancel any running proofreading
            _proofreadCts?.Cancel();
            _proofreadCts?.Dispose();
            _proofreadCts = null;

            if (_batchProofreader != null)
            {
                _batchProofreader.Progress -= OnBatchProgress;
                _batchProofreader.SegmentProofread -= OnProofreadSegmentResult;
                _batchProofreader.Completed -= OnProofreadCompleted;
                _batchProofreader = null;
            }

            if (_inboxWatcher != null)
            {
                _inboxWatcher.EnableRaisingEvents = false;
                _inboxWatcher.Dispose();
                _inboxWatcher = null;
            }

            if (_supervertalerBridge != null)
            {
                try { _supervertalerBridge.Dispose(); } catch { /* never let bridge cleanup break Dispose */ }
                _supervertalerBridge = null;
            }

            if (_editorController != null)
            {
                try { _editorController.ActiveDocumentChanged -= OnActiveDocumentChanged; }
                catch { }
            }

            if (_activeDocument != null)
            {
                try { _activeDocument.ActiveSegmentChanged -= OnActiveSegmentChanged; }
                catch { }
                try { _activeDocument.DocumentFilterChanged -= OnDocumentFilterChanged; }
                catch { }
            }

            base.Dispose();
        }

        // ════════════════════════════════════════════════════════════════════
        // Import / Export tab (v4.20.7) — bilingual review file export &
        // round-trip re-import. The tab UI fires the four events handled
        // below; the heavy lifting lives in
        // <see cref="Core.Export.BilingualExporter"/> and
        // <see cref="Core.Export.BilingualImporter"/>.
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Walks the active Trados document, builds an <see cref="Core.Export.ExportSegment"/>
        /// list with source/target/status, picks a file path, and writes the
        /// bilingual file plus its sidecar manifest. Adds the result to the
        /// recent-exports list.
        /// </summary>
        private void OnBilingualExportRequested(object sender, ExportRequestedEventArgs e)
        {
            SafeInvoke(() =>
            {
                var ctrl = _control.Value.ImportExportControl;
                if (_activeDocument == null)
                {
                    ctrl.AppendLog("No document open.", true);
                    return;
                }

                // Multi-file: build the file-id filter from the UI
                // selection. Empty filter means "no UI restriction" —
                // either single-file (UI hidden) or user has the "All"
                // quick-select active. Null = include everything.
                //
                // When per-file attribution failed (SDK didn't expose
                // enough info to map segments to files), we silently
                // drop the filter and export everything from selected
                // files' segments… well, every segment, since we can't
                // tell them apart. Better than emitting an empty file.
                // The diagnostic is also dumped to the log so we can
                // refine the matcher if this path gets hit.
                var selectedIds = ctrl.GetSelectedFileIds();
                HashSet<string> filter;
                if (selectedIds.Count == 0)
                {
                    filter = null;
                }
                else if (!_perFileMappingWorked)
                {
                    filter = null;
                    ctrl.AppendLog("Note: this document didn't expose per-file segment attribution. " +
                                   "Exporting all segments in the active view (file selection is ignored).");
                    // One-shot: dump diagnostic to the in-tab log so the next
                    // round can refine the matcher. Doesn't block the export.
                    try { DumpMultiFileDiagnostic(ctrl); } catch { }
                }
                else
                {
                    filter = new HashSet<string>(selectedIds, StringComparer.Ordinal);
                }

                // v4.20.18: honour the "Include locked segments" checkbox.
                // Off → locked segments are skipped entirely. On → they're
                // included and visually flagged with 🔒 in the Status
                // column by the renderers.
                bool includeLocked = ctrl.IncludeLockedSegments;

                // v4.20.24: honour the confirmation-status checkboxes.
                // If the user has ticked specific statuses, the collector
                // filters segments accordingly. An empty / null set means
                // "no filter — include every status".
                var statusFilter = ctrl.GetSelectedStatuses();

                // #90: honour the "Include Trados comments" checkbox. Off means the
                // collector leaves the field empty, and the renderers drop the whole
                // column of their own accord.
                bool includeComments = ctrl.IncludeComments;

                List<Core.Export.ExportSegment> segments;
                try
                {
                    segments = CollectBilingualExportSegments(filter, includeLocked, statusFilter, includeComments);
                }
                catch (Exception ex)
                {
                    ctrl.AppendLog("Could not enumerate segments: " + ex.Message, true);
                    return;
                }

                if (segments.Count == 0)
                {
                    ctrl.AppendLog("No segments to export.", true);
                    return;
                }

                var opts = e.Options;
                var srcLang = GetDocumentSourceLanguage();
                var tgtLang = GetDocumentTargetLanguage();
                opts.SourceLanguageDisplay = !string.IsNullOrEmpty(srcLang)
                    ? Core.LanguageUtils.ShortenLanguageName(srcLang)
                    : "Source";
                opts.TargetLanguageDisplay = !string.IsNullOrEmpty(tgtLang)
                    ? Core.LanguageUtils.ShortenLanguageName(tgtLang)
                    : "Target";
                opts.ProjectName = SafeGetProjectName();
                opts.ToolVersion = SafeGetPluginVersion();

                // Group by source file so we can branch on output mode.
                var groups = new List<KeyValuePair<string, List<Core.Export.ExportSegment>>>();
                {
                    var byFile = new Dictionary<string, List<Core.Export.ExportSegment>>(StringComparer.Ordinal);
                    foreach (var seg in segments)
                    {
                        var key = string.IsNullOrEmpty(seg.SourceFileName)
                            ? SafeGetActiveFileName()
                            : seg.SourceFileName;
                        if (!byFile.TryGetValue(key, out var list))
                        {
                            list = new List<Core.Export.ExportSegment>();
                            byFile[key] = list;
                            groups.Add(new KeyValuePair<string, List<Core.Export.ExportSegment>>(key, list));
                        }
                        list.Add(seg);
                    }
                }

                bool multiFile = groups.Count > 1;
                bool separatePerFile = multiFile
                    && ctrl.SelectedOutputMode == MultiFileOutputMode.SeparatePerFile;

                if (separatePerFile)
                {
                    // ── Output mode: one bilingual DOCX per source file.
                    // Ask the user for a target FOLDER (not a file). We
                    // synthesise per-file file names from the project +
                    // source filename + layout.
                    string targetDir = Controls.FolderPicker.Show(
                        _control.Value,
                        "Pick a folder. One bilingual " + opts.Format +
                        " will be created per source file inside it.");
                    if (string.IsNullOrEmpty(targetDir)) return;

                    ctrl.SetBusy(true);
                    int filesWritten = 0;
                    try
                    {
                        var exporter = new Core.Export.BilingualExporter();
                        foreach (var grp in groups)
                        {
                            var perFileOpts = ClonePerFileOpts(opts, sourceFileName: grp.Key);
                            // Deliberately NOT renumbered. Splitting a file out used
                            // to restart at 1, which is the same fault this numbering
                            // work exists to remove: the number a reader sees has to be
                            // the one Studio shows, whether they got one file or ten.
                            // Studio's numbers are unique across the document, so a
                            // per-file manifest still keys on them cleanly.
                            var fileName = Core.Export.BilingualExporter.DefaultFileName(perFileOpts);
                            var path = Path.Combine(targetDir, fileName);
                            var manifest = exporter.Export(grp.Value, perFileOpts, path);
                            ctrl.AddHistoryEntry(DateTime.Now, opts.Format.ToString(), path);
                            ctrl.AppendLog(
                                $"Exported {grp.Value.Count} segments from {grp.Key} → " +
                                Path.GetFileName(path));
                            filesWritten++;
                        }
                        ctrl.AppendLog(
                            $"Wrote {filesWritten} bilingual file(s) into {targetDir}.");
                    }
                    catch (Exception ex)
                    {
                        ctrl.AppendLog("Export failed: " + ex.Message, true);
                    }
                    finally
                    {
                        ctrl.SetBusy(false);
                    }
                    return;
                }

                // ── Output mode: one combined DOCX. When exactly one source
                // file is involved (single-file document, or a merged doc
                // with one file ticked), the manifest – and the suggested
                // file name – carry that file's name, which may differ from
                // the ACTIVE file if the user ticked a non-active one.
                // Per-segment file attribution lives on each
                // ExportSegment.SourceFileName either way.
                opts.IsMultiFileCombined = multiFile;
                opts.SourceFileName = multiFile
                    ? $"(multi-file: {groups.Count} files)"
                    : groups[0].Key;

                var defaultName = Core.Export.BilingualExporter.DefaultFileName(opts);
                string targetPath;
                using (var dlg = new SaveFileDialog())
                {
                    dlg.FileName = defaultName;
                    dlg.Title = multiFile
                        ? "Save combined bilingual review file (all selected files)"
                        : "Save bilingual review file";
                    switch (opts.Format)
                    {
                        case Core.Export.ExportFormat.Docx:
                            dlg.Filter = "Word document (*.docx)|*.docx";
                            dlg.DefaultExt = "docx";
                            break;
                        case Core.Export.ExportFormat.Text:
                            dlg.Filter = "Bilingual Text (*.txt)|*.txt";
                            dlg.DefaultExt = "txt";
                            break;
                        case Core.Export.ExportFormat.Html:
                            dlg.Filter = "HTML (*.html)|*.html";
                            dlg.DefaultExt = "html";
                            break;
                    }
                    if (dlg.ShowDialog(_control.Value) != DialogResult.OK) return;
                    targetPath = dlg.FileName;
                }

                ctrl.SetBusy(true);
                try
                {
                    var exporter = new Core.Export.BilingualExporter();
                    var manifest = exporter.Export(segments, opts, targetPath);
                    ctrl.AddHistoryEntry(DateTime.Now, opts.Format.ToString(), targetPath);
                    var fileCountSuffix = multiFile ? $" ({groups.Count} source files)" : "";
                    ctrl.AppendLog(
                        $"Exported {segments.Count} segments to {Path.GetFileName(targetPath)} " +
                        $"({opts.Format}, {opts.Layout}){fileCountSuffix}.");
                    ctrl.AppendLog("Sidecar manifest: " +
                        Path.GetFileName(Core.Export.ExportManifest.SidecarPathFor(targetPath)));
                }
                catch (Exception ex)
                {
                    ctrl.AppendLog("Export failed: " + ex.Message, true);
                }
                finally
                {
                    ctrl.SetBusy(false);
                }
            });
        }

        /// <summary>One-shot diagnostic: dump everything we can introspect
        /// about the active document's file structure to a TEMP FILE
        /// (and to the tab log). We use a temp file rather than just the
        /// log because the multi-file UI may have pushed the log area
        /// off-screen on some layouts. Returns the temp-file path so the
        /// caller can show it in a MessageBox.</summary>
        private string DumpMultiFileDiagnostic(Controls.ImportExportControl ctrl)
        {
            var sb = new System.Text.StringBuilder();
            void Log(string s) { sb.AppendLine(s); try { ctrl.AppendLog(s); } catch { } }

            try
            {
                Log("── DIAG: active document SDK shape ──");
                var doc = _activeDocument;
                if (doc == null) { Log("  _activeDocument is null"); }
                else
                {
                    Log($"  _activeDocument type: {doc.GetType().FullName}");

                    // 1) Walk doc properties returning IEnumerable
                    Log("  -- IEnumerable properties on _activeDocument --");
                    foreach (var prop in doc.GetType().GetProperties())
                    {
                        if (prop.GetIndexParameters().Length > 0) continue;
                        if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType)) continue;
                        if (prop.PropertyType == typeof(string)) continue;
                        try
                        {
                            var val = prop.GetValue(doc, null) as System.Collections.IEnumerable;
                            int n = 0;
                            if (val != null) foreach (var _ in val) { n++; if (n > 100000) break; }
                            Log($"    {prop.Name}  ({prop.PropertyType.Name})  → {n} items");
                        }
                        catch (Exception ex)
                        {
                            Log($"    {prop.Name}  ({prop.PropertyType.Name})  → threw {ex.GetType().Name}");
                        }
                    }

                    // 2) For first file in doc.Files, dump every property + every no-arg method
                    var filesProp = doc.GetType().GetProperty("Files");
                    if (filesProp != null)
                    {
                        var filesEnum = filesProp.GetValue(doc, null) as System.Collections.IEnumerable;
                        if (filesEnum != null)
                        {
                            object firstFile = null;
                            int fileCount = 0;
                            foreach (var f in filesEnum) { if (firstFile == null) firstFile = f; fileCount++; }
                            Log($"  .Files contains {fileCount} item(s)");

                            if (firstFile != null)
                            {
                                Log($"  -- First file type: {firstFile.GetType().FullName} --");
                                Log("  -- Properties on first file --");
                                foreach (var prop in firstFile.GetType().GetProperties())
                                {
                                    if (prop.GetIndexParameters().Length > 0) continue;
                                    try
                                    {
                                        var val = prop.GetValue(firstFile, null);
                                        string valStr;
                                        if (val == null) valStr = "null";
                                        else if (val is System.Collections.IEnumerable enu && !(val is string))
                                        {
                                            int n = 0; foreach (var _ in enu) { n++; if (n > 100000) break; }
                                            valStr = $"<enumerable, {n} items, type={prop.PropertyType.Name}>";
                                        }
                                        else valStr = val.ToString();
                                        if (valStr != null && valStr.Length > 200) valStr = valStr.Substring(0, 200) + "…";
                                        Log($"    {prop.Name}  ({prop.PropertyType.Name}) = {valStr}");
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"    {prop.Name}  → threw {ex.GetType().Name}");
                                    }
                                }

                                Log("  -- No-arg methods on first file --");
                                foreach (var method in firstFile.GetType().GetMethods(
                                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                                {
                                    if (method.GetParameters().Length != 0) continue;
                                    if (method.IsSpecialName) continue;
                                    if (method.DeclaringType == typeof(object)) continue;
                                    Log($"    {method.Name}() → {method.ReturnType.Name}");
                                }
                            }
                        }
                    }

                    // 3) Methods on _activeDocument (one-arg too) — we're
                    //    looking for something like GetFile(pair) or
                    //    GetActiveFile(pair) that maps a segment to its
                    //    parent file.
                    try
                    {
                        Log("  -- Methods on _activeDocument (no-arg + one-arg, non-property) --");
                        foreach (var method in doc.GetType().GetMethods(
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                        {
                            if (method.IsSpecialName) continue;
                            if (method.DeclaringType == typeof(object)) continue;
                            var pars = method.GetParameters();
                            if (pars.Length > 1) continue;
                            string sig = string.Join(", ",
                                pars.Select(p => p.ParameterType.Name + " " + p.Name).ToArray());
                            Log($"    {method.Name}({sig}) → {method.ReturnType.Name}");
                        }
                    }
                    catch (Exception ex) { Log("  doc-methods dump threw: " + ex.Message); }

                    // 4) Walk the first 3 segment pairs, dump full property
                    //    chain so we can see where (if anywhere) FileId is
                    //    hiding. Wrap EVERY access in try/catch so a single
                    //    threw doesn't kill the whole section (which is
                    //    probably what happened last time).
                    try
                    {
                        Log("  -- First 3 segment pairs --");
                        int pi = 0;
                        foreach (var pair in doc.SegmentPairs)
                        {
                            if (pair == null) { pi++; continue; }
                            Log($"  PAIR[{pi}] type={SafeTypeName(pair)}");
                            DumpObjectPropsSafe(pair, "    pair.", Log);

                            try
                            {
                                if (pair.Properties != null)
                                {
                                    Log($"    pair.Properties type={SafeTypeName(pair.Properties)}");
                                    DumpObjectPropsSafe(pair.Properties, "      Properties.", Log);
                                    try
                                    {
                                        var sid = pair.Properties.Id;
                                        Log($"      Properties.Id type={SafeTypeName(sid)}");
                                        DumpObjectPropsSafe(sid, "        Id.", Log);
                                    }
                                    catch (Exception ex) { Log("      Properties.Id threw: " + ex.Message); }
                                }
                            }
                            catch (Exception ex) { Log("    pair.Properties access threw: " + ex.Message); }

                            try
                            {
                                var pu = doc.GetParentParagraphUnit(pair);
                                if (pu != null)
                                {
                                    Log($"    parentPU type={SafeTypeName(pu)}");
                                    DumpObjectPropsSafe(pu, "      pu.", Log);
                                    try
                                    {
                                        if (pu.Properties != null)
                                        {
                                            Log($"      pu.Properties type={SafeTypeName(pu.Properties)}");
                                            DumpObjectPropsSafe(pu.Properties, "        Properties.", Log);
                                            try
                                            {
                                                var puid = pu.Properties.ParagraphUnitId;
                                                Log($"        ParagraphUnitId type={SafeTypeName(puid)}");
                                                DumpObjectPropsSafe(puid, "          PUId.", Log);
                                            }
                                            catch (Exception ex) { Log("        ParagraphUnitId threw: " + ex.Message); }

                                            // Walk INTO the Contexts collection so we can see
                                            // whether file-identifying strings live there.
                                            try
                                            {
                                                var ctxProp = pu.Properties.GetType().GetProperty("Contexts");
                                                var ctxRoot = ctxProp?.GetValue(pu.Properties, null);
                                                if (ctxRoot != null)
                                                {
                                                    Log($"        Contexts root type={SafeTypeName(ctxRoot)}");
                                                    DumpObjectPropsSafe(ctxRoot, "          ctxRoot.", Log);
                                                    System.Collections.IEnumerable ctxList = null;
                                                    try { ctxList = ctxRoot.GetType().GetProperty("Contexts")?.GetValue(ctxRoot, null) as System.Collections.IEnumerable; } catch { }
                                                    if (ctxList == null) { try { ctxList = ctxRoot as System.Collections.IEnumerable; } catch { } }
                                                    if (ctxList != null)
                                                    {
                                                        int ci = 0;
                                                        foreach (var c in ctxList)
                                                        {
                                                            if (c == null) { ci++; continue; }
                                                            Log($"          CTX[{ci}] type={SafeTypeName(c)}");
                                                            DumpObjectPropsSafe(c, "            ", Log);
                                                            // Try the well-known metadata keys explicitly.
                                                            foreach (var key in new[]
                                                            {
                                                                "FilePath","OriginalFilePath","Path","FileName",
                                                                "OriginalName","SourceFilePath","Source","File",
                                                                "ParagraphFormatting"
                                                            })
                                                            {
                                                                try
                                                                {
                                                                    var v = TryGetContextMetaData(c, key);
                                                                    if (!string.IsNullOrEmpty(v))
                                                                    {
                                                                        var trunc = v.Length > 200 ? v.Substring(0, 200) + "…" : v;
                                                                        Log($"            metadata[{key}] = {trunc}");
                                                                    }
                                                                }
                                                                catch { }
                                                            }
                                                            ci++;
                                                            if (ci >= 8) break;
                                                        }
                                                    }
                                                }
                                            }
                                            catch (Exception ex) { Log("        Contexts dump threw: " + ex.Message); }
                                        }
                                    }
                                    catch (Exception ex) { Log("      pu.Properties access threw: " + ex.Message); }
                                }
                            }
                            catch (Exception ex) { Log("    GetParentParagraphUnit threw: " + ex.Message); }

                            pi++;
                            if (pi >= 3) break;
                        }
                    }
                    catch (Exception ex) { Log("  pair walk threw: " + ex.Message); }
                }

                Log("── /DIAG ──");
            }
            catch (Exception ex)
            {
                Log("DIAG outer fail: " + ex.Message);
            }

            // Write to a temp file. Stable name so user can locate it.
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "supervertaler-trados-diag.txt");
                System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
                ctrl.AppendLog("Diagnostic written to: " + path);
                return path;
            }
            catch (Exception ex)
            {
                ctrl.AppendLog("Could not write diagnostic file: " + ex.Message, true);
                return null;
            }
        }

        private static string SafeTypeName(object obj)
        {
            try { return obj?.GetType().FullName ?? "null"; } catch { return "<threw>"; }
        }

        /// <summary>Like <see cref="DumpObjectProps"/> but wrapping every
        /// access in its own try/catch so a single threw doesn't kill
        /// the whole enumeration. Used by the v4.20.8 multi-file
        /// diagnostic.</summary>
        private static void DumpObjectPropsSafe(object obj, string indent, Action<string> log)
        {
            if (obj == null) { log(indent + "(null)"); return; }
            System.Reflection.PropertyInfo[] props;
            try { props = obj.GetType().GetProperties(); }
            catch (Exception ex) { log(indent + "GetProperties threw: " + ex.Message); return; }
            foreach (var prop in props)
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                string valStr;
                try
                {
                    var val = prop.GetValue(obj, null);
                    if (val == null) valStr = "null";
                    else if (val is System.Collections.IEnumerable enu && !(val is string))
                    {
                        int n = 0;
                        try { foreach (var _ in enu) { n++; if (n > 100000) break; } }
                        catch { valStr = "<enumerable, iteration threw>"; goto write; }
                        valStr = $"<enumerable, {n} items, type={prop.PropertyType.Name}>";
                    }
                    else valStr = val.ToString();
                    if (valStr != null && valStr.Length > 200) valStr = valStr.Substring(0, 200) + "…";
                }
                catch (Exception ex)
                {
                    valStr = "<threw: " + ex.GetType().Name + ": " + ex.Message + ">";
                }
                write:
                log($"{indent}{prop.Name}  ({prop.PropertyType.Name}) = {valStr}");
            }
        }

        private static void DumpObjectProps(object obj, string indent, Action<string> log)
        {
            if (obj == null) return;
            foreach (var prop in obj.GetType().GetProperties())
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                try
                {
                    var val = prop.GetValue(obj, null);
                    string valStr;
                    if (val == null) valStr = "null";
                    else if (val is System.Collections.IEnumerable enu && !(val is string))
                    {
                        int n = 0; foreach (var _ in enu) { n++; if (n > 100000) break; }
                        valStr = $"<enumerable, {n} items, type={prop.PropertyType.Name}>";
                    }
                    else valStr = val.ToString();
                    if (valStr != null && valStr.Length > 200) valStr = valStr.Substring(0, 200) + "…";
                    log($"{indent}{prop.Name}  ({prop.PropertyType.Name}) = {valStr}");
                }
                catch (Exception ex)
                {
                    log($"{indent}{prop.Name}  → threw {ex.GetType().Name}");
                }
            }
        }

        /// <summary>Clone an <see cref="Core.Export.ExportOptions"/> with a
        /// different per-file SourceFileName. Used in the SeparatePerFile
        /// output mode so each emitted file's manifest records the right
        /// source filename and the default file name generator picks up
        /// the per-file stem.</summary>
        private static Core.Export.ExportOptions ClonePerFileOpts(
            Core.Export.ExportOptions src, string sourceFileName)
        {
            return new Core.Export.ExportOptions
            {
                Format = src.Format,
                Layout = src.Layout,
                SourceLanguageDisplay = src.SourceLanguageDisplay,
                TargetLanguageDisplay = src.TargetLanguageDisplay,
                ProjectName = src.ProjectName,
                SourceFileName = sourceFileName ?? "",
                ToolVersion = src.ToolVersion,
                IncludeLocked = src.IncludeLocked,
                IncludedStatuses = src.IncludedStatuses
            };
        }

        /// <summary>
        /// Reads a round-tripped DOCX or Markdown file, loads its sidecar
        /// manifest if present, computes the diff against the current Trados
        /// document state, confirms with the user, and applies accepted
        /// changes via <c>ProcessSegmentPair</c> (same writeback path the
        /// batch AI translator uses).
        /// </summary>
        // Guards the bilingual re-import writeback against re-entrancy: the loop
        // pumps the message queue (Application.DoEvents) so the Cancel button and
        // the progress bar stay live, which means a second import request could
        // otherwise arrive while one is still running.
        private bool _reimportInProgress;

        private void OnBilingualImportRequested(object sender, ImportRequestedEventArgs e)
        {
            SafeInvoke(() =>
            {
                var ctrl = _control.Value.ImportExportControl;
                if (_reimportInProgress)
                {
                    ctrl.AppendLog("A re-import is already running — please wait for it to finish.", true);
                    return;
                }
                if (_activeDocument == null)
                {
                    ctrl.AppendLog("No document open.", true);
                    return;
                }

                if (!File.Exists(e.FilePath))
                {
                    ctrl.AppendLog("File does not exist: " + e.FilePath, true);
                    return;
                }

                var sidecarPath = Core.Export.ExportManifest.SidecarPathFor(e.FilePath);
                Core.Export.ExportManifest manifest;
                try
                {
                    manifest = File.Exists(sidecarPath)
                        ? Core.Export.ExportManifest.Load(sidecarPath)
                        : null;
                }
                catch (Exception ex)
                {
                    ctrl.AppendLog("Could not read sidecar manifest: " + ex.Message, true);
                    manifest = null;
                }

                if (manifest == null)
                {
                    // Build a fallback "manifest" purely from current document
                    // state. This loses source-tamper protection but lets the
                    // user re-import files that were generated before
                    // manifests existed or whose sidecars got deleted.
                    ctrl.AppendLog(
                        "No sidecar manifest found — falling back to current-document mapping. " +
                        "Source-tamper detection will be disabled for this import.", true);
                    manifest = BuildManifestFromCurrentDocument();
                }

                // Lookups for the importer to query current state.
                var currentTargetMap = SnapshotCurrentTargets();
                var lockedMap = SnapshotLockedSegments();
                var sourceTagCountMap = SnapshotSourceTagCounts();

                var importer = new Core.Export.BilingualImporter();
                var result = importer.Build(
                    e.FilePath, manifest,
                    currentTargetLookup: (pu, sg) =>
                    {
                        string val;
                        return currentTargetMap.TryGetValue(KeyOf(pu, sg), out val) ? val : null;
                    },
                    isWriteable: (pu, sg) => !lockedMap.Contains(KeyOf(pu, sg)),
                    currentSourceTagCountLookup: (pu, sg) =>
                    {
                        int n;
                        return sourceTagCountMap.TryGetValue(KeyOf(pu, sg), out n) ? n : 0;
                    });

                if (result.TotalImported == 0)
                {
                    ctrl.AppendLog("No segments parsed from the file. " +
                        "Check that it's a Supervertaler-exported DOCX or Bilingual Text (.txt) file.", true);
                    return;
                }

                // Strict-mode flag from the UI checkbox. When OFF, the
                // writeback loop treats TagMismatch like Changed so the
                // edit is applied verbatim (with a per-segment warning).
                bool strict = ctrl.StrictTagIntegrityCheck;

                // Confirmation prompt. Surface the tag-mismatch count
                // separately so the user can see at a glance whether any
                // segments would be skipped for safety.
                var tagMismatch = result.TagMismatchCount;
                var sourceMismatch = result.SourceMismatchCount;
                var nonTagIssues = result.IssueCount - tagMismatch - sourceMismatch;

                // Called out separately, and named. This is not "a segment that
                // could not be updated" - it means the row came back different from
                // the one that was sent, so its target cannot be trusted to belong
                // to that segment. A deleted row or a sorted table produces exactly
                // this, and the number column alone would not notice.
                var sourceLine = "";
                if (sourceMismatch > 0)
                {
                    var nums = result.SourceMismatchNumbers;
                    var shown = string.Join(", ", nums.GetRange(0, Math.Min(6, nums.Count)));
                    if (nums.Count > 6) shown += ", and more";
                    sourceLine = "  " + sourceMismatch + " row(s) no longer match the exported file "
                               + "- will be SKIPPED (segment " + shown + ")" + Environment.NewLine;
                }
                var tagLine = tagMismatch > 0
                    ? (strict
                        ? $"  {tagMismatch} tag-mismatch (will be SKIPPED — would break Trados QA)\n"
                        : $"  {tagMismatch} tag-mismatch (will be applied — strict check is OFF)\n")
                    : "";
                var msg = $"Read {result.TotalImported} segments from the file.\n\n" +
                          $"  {result.ChangedCount} change(s) to apply\n" +
                          $"  {result.UnchangedCount} unchanged\n" +
                          tagLine +
                          sourceLine +
                          $"  {nonTagIssues} other issue(s) (missing or locked)\n\n" +
                          "Apply the changes to the active Trados document?";
                var dr = MessageBox.Show(_control.Value, msg, "Re-import bilingual file",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (dr != DialogResult.OK) return;

                int applied = 0, failed = 0, skippedTagMismatch = 0;

                // Build a (puId/segId → pair) index ONCE. The previous code
                // called FindSegmentPair for every changed segment, and each
                // call re-scanned the whole document's SegmentPairs (an
                // expensive GetParentParagraphUnit per pair) — O(n²), ≈1.4M SDK
                // model calls on a 1000+-segment merged multi-file document.
                // That froze the UI thread for minutes and, on 32-bit Trados
                // Studio 2024, exhausted the ~2 GB address space into a silent
                // crash.
                var pairIndex = BuildSegmentPairIndex(ctrl);

                // Workload size for the progress bar (only segments that will
                // actually be written back).
                int toApply = 0;
                foreach (var d in result.Diffs)
                    if (d.Kind == Core.Export.ImportChangeKind.Changed
                        || (d.Kind == Core.Export.ImportChangeKind.TagMismatch && !strict))
                        toApply++;

                bool cancelled = false, stoppedForMemory = false;
                var progress = new Controls.ReimportProgressForm(Path.GetFileName(e.FilePath), toApply);
                progress.CancelRequested += (s2, e2) => cancelled = true;

                _reimportInProgress = true;
                ctrl.SetBusy(true);
                try
                {
                    try { progress.Show(_control.Value); progress.BringToFront(); } catch { }
                    int processed = 0;
                    foreach (var d in result.Diffs)
                    {
                        if (cancelled) break;

                        // Strict mode: skip TagMismatch entirely.
                        // Permissive mode: treat TagMismatch as Changed.
                        bool isWriteableNow =
                            d.Kind == Core.Export.ImportChangeKind.Changed
                            || (d.Kind == Core.Export.ImportChangeKind.TagMismatch && !strict);

                        if (!isWriteableNow)
                        {
                            if (d.Kind == Core.Export.ImportChangeKind.TagMismatch && strict)
                            {
                                ctrl.AppendLog(
                                    $"Segment {d.Number}: skipped — {d.Detail}. " +
                                    "Restore the tag in the bilingual file, edit the segment " +
                                    "directly in Trados, or turn off strict tag-integrity check.",
                                    true);
                                skippedTagMismatch++;
                            }
                            continue;
                        }

                        if (d.Kind == Core.Export.ImportChangeKind.TagMismatch && !strict)
                        {
                            ctrl.AppendLog(
                                $"Segment {d.Number}: applying despite tag mismatch — {d.Detail}. " +
                                "Strict tag-integrity check is OFF; verify Trados QA after import.",
                                true);
                        }
                        ISegmentPair pair;
                        if (!pairIndex.TryGetValue(KeyOf(d.ParagraphUnitId, d.SegmentId), out pair) || pair == null)
                        {
                            ctrl.AppendLog($"Segment {d.Number}: not found in document, skipped.", true);
                            failed++;
                            processed++;
                            continue;
                        }
                        try
                        {
                            _activeDocument.ProcessSegmentPair(pair, "Supervertaler",
                                (sp, cancel) =>
                                {
                                    // v4.20.7-tag: try the tag-aware reconstruction
                                    // path first. We re-serialize the live source to get
                                    // a fresh TagMap with the same numbering the
                                    // proofreader saw at export time (deterministic given
                                    // the source). SegmentTagHandler.ReconstructTarget
                                    // then parses the proofreader's <tN>...</tN> markers
                                    // and rebuilds the target with the correct cloned
                                    // tags wrapped around the translated text.
                                    //
                                    // Fall back to plain-text writeback when:
                                    //   - the source has no tags (nothing to reconstruct)
                                    //   - ReconstructTarget returns false (proofreader
                                    //     broke the tag structure — mismatched <tN>,
                                    //     unknown tag number, etc.)
                                    bool reconstructed = false;
                                    if (d.NewTarget != null)
                                    {
                                        // Serialise BOTH source and target. Source tag
                                        // references stay valid throughout reconstruction
                                        // (source isn't modified). Target tag references
                                        // need pre-cloning because ReconstructTarget calls
                                        // sp.Target.Clear() internally — without cloning,
                                        // those references could be invalidated before
                                        // the parsed tags get used. Combining both maps
                                        // lets a proofreader's edit reference tags that
                                        // originated in either the source OR the target
                                        // cell (the common case for the user's reported
                                        // "moved bold to a different word" scenario:
                                        // the bold only exists in target, source TagMap
                                        // is empty, so without including target tags
                                        // ReconstructTarget can't find the <tN> entry
                                        // and falls back to plain text — stripping all
                                        // formatting).
                                        var sourceSer = Core.SegmentTagHandler.Serialize(sp.Source);
                                        var targetSer = Core.SegmentTagHandler.Serialize(sp.Target);
                                        var combinedMap = BuildCombinedTagMap(sourceSer.TagMap, targetSer.TagMap);

                                        // Resolve any semantic-name markers (<b>, <i>, …)
                                        // that BilingualTagNamer.ApplySemanticNames wrote on
                                        // export back into the matching <tN>…</tN> form
                                        // SegmentTagHandler.ReconstructTarget understands.
                                        // Positional matching against the combined TagMap.
                                        var resolved = Core.Export.BilingualTagNamer.ResolveSemanticNames(
                                            d.NewTarget, combinedMap);

                                        bool hasAnyMarker = combinedMap.Count > 0
                                            || resolved.IndexOf("<t", StringComparison.Ordinal) >= 0;
                                        if (hasAnyMarker)
                                        {
                                            reconstructed = Core.SegmentTagHandler.ReconstructTarget(
                                                sp.Target, sp.Source, resolved, combinedMap);
                                        }
                                    }

                                    if (!reconstructed)
                                    {
                                        // Plain-text fallback: strip any stray <tN> markers
                                        // (and any leftover semantic markers) and write a
                                        // single IText cloned from source.
                                        var plain = Core.SegmentTagHandler.StripTagPlaceholders(d.NewTarget ?? "");
                                        // Also strip residual <b>/<i>/<u>/<bi> markers that
                                        // didn't resolve to a TagMap entry.
                                        plain = System.Text.RegularExpressions.Regex.Replace(
                                            plain, @"</?(?:bi|b|i|u)>", "",
                                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                                        var textTpl = Core.SegmentTagHandler.FindFirstText(sp.Source);
                                        if (textTpl != null)
                                        {
                                            sp.Target.Clear();
                                            var clone = (IText)textTpl.Clone();
                                            clone.Properties.Text = plain;
                                            sp.Target.Add(clone);
                                        }
                                    }
                                });
                            applied++;
                        }
                        catch (Exception ex)
                        {
                            ctrl.AppendLog($"Segment {d.Number}: write failed — {ex.Message}", true);
                            failed++;
                        }

                        processed++;
                        // Every 20 writes: advance the bar, pump the message
                        // queue so Cancel + repainting stay live, and run the
                        // 32-bit memory watchdog (a no-op on 64-bit Studio 2026).
                        if (processed % 20 == 0)
                        {
                            try { progress.SetProgress(processed, $"Applying changes… {processed} of {toApply}"); } catch { }
                            System.Windows.Forms.Application.DoEvents();
                            if (cancelled) break;
                            if (Core.MemoryGuard.IsOverSoftLimit())
                                Core.MemoryGuard.CollectAndCompact();
                            if (Core.MemoryGuard.IsOverHardLimit())
                            {
                                stoppedForMemory = true;
                                break;
                            }
                        }
                    }
                }
                finally
                {
                    _reimportInProgress = false;
                    try { progress.Finish(); } catch { }
                    ctrl.SetBusy(false);
                }

                var otherIssues = result.IssueCount - skippedTagMismatch - result.SourceMismatchCount;
                var summary = (stoppedForMemory ? "Re-import stopped early: "
                        : cancelled ? "Re-import cancelled: "
                        : "Re-import complete: ")
                    + $"{applied} applied, {failed} failed";
                if (skippedTagMismatch > 0)
                    summary += $", {skippedTagMismatch} skipped (tag mismatch)";
                if (result.SourceMismatchCount > 0)
                    summary += $", {result.SourceMismatchCount} skipped (row no longer matches the export)";
                if (otherIssues > 0)
                    summary += $", {otherIssues} other issue(s) skipped";
                summary += ".";
                ctrl.AppendLog(summary, stoppedForMemory || cancelled);

                if (stoppedForMemory)
                {
                    MessageBox.Show(_control.Value,
                        $"Applied {applied} change(s), then stopped to avoid a 32-bit memory crash " +
                        "in Trados Studio 2024.\n\n" +
                        "The remaining changes were NOT applied. To finish them, either:\n" +
                        "  •  re-run this same re-import in Trados Studio 2026 (64-bit), or\n" +
                        "  •  open fewer files at once (or one file at a time) and re-import again.\n\n" +
                        "Re-importing is safe to repeat — already-applied segments simply show as unchanged.",
                        "Re-import stopped (memory limit)",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
        }

        private void OnImportExportOpenFile(object sender, string filePath)
        {
            try { System.Diagnostics.Process.Start(filePath); }
            catch (Exception ex)
            {
                _control.Value.ImportExportControl.AppendLog(
                    "Could not open file: " + ex.Message, true);
            }
        }

        private void OnImportExportOpenFolder(object sender, string filePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                    System.Diagnostics.Process.Start("explorer.exe", dir);
            }
            catch (Exception ex)
            {
                _control.Value.ImportExportControl.AppendLog(
                    "Could not open folder: " + ex.Message, true);
            }
        }

        // ─── Trados SDK helpers for the Import / Export tab ─────────────

        /// <summary>Walks the active document and returns one
        /// <see cref="Core.Export.ExportSegment"/> per non-empty source
        /// segment, with stable Trados (paragraph-unit-id, segment-id) keys
        /// in the manifest.</summary>
        private List<Core.Export.ExportSegment> CollectBilingualExportSegments(
            HashSet<string> fileIdFilter = null,
            bool includeLocked = true,
            HashSet<string> statusFilter = null,
            bool includeComments = true)
        {
            var result = new List<Core.Export.ExportSegment>();
            if (_activeDocument == null) return result;

            // Build a (fileId → fileName) lookup once so we can attribute
            // each segment to a human-readable file name. Single-file
            // documents end up with one entry; multi-file documents with
            // one per merged file.
            var fileIdToName = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                string _af;
                foreach (var f in EnumerateActiveDocumentFiles(out _af))
                    if (!string.IsNullOrEmpty(f.FileId))
                        fileIdToName[f.FileId] = f.FileName ?? "";
            }
            catch { }

            // Defensive: if attribution didn't work, the filter would
            // drop every segment (because every fid would be empty).
            // Treat that the same as "no filter" so we at least export
            // something. The caller already shows a log warning.
            var effectiveFilter = (fileIdFilter != null && _perFileMappingWorked)
                ? fileIdFilter : null;

            int number = 1;
            foreach (var pair in _activeDocument.SegmentPairs)
            {
                // Multi-file mode: skip segments outside the selected
                // files. Null filter = include everything (single-file
                // mode and "All files checked" mode).
                if (effectiveFilter != null)
                {
                    var fid = GetFileIdForSegment(pair);
                    if (string.IsNullOrEmpty(fid) || !effectiveFilter.Contains(fid)) continue;
                }
                if (pair?.Source == null) continue;

                // v4.20.18: read the segment's locked flag once. Used both
                // to honour the includeLocked filter (skip when off) and
                // to set ExportSegment.IsLocked so the renderers can
                // visually mark the row.
                bool isLocked = false;
                try { isLocked = pair.Properties?.IsLocked ?? false; }
                catch { }
                if (isLocked && !includeLocked) continue;

                // v4.20.7-tag: serialize source + target through SegmentTagHandler
                // so inline tags (cf bold/italic, field codes, page numbers, etc.)
                // come out as numbered <tN>...</tN> / <tN/> placeholders in the
                // bilingual file. This is the same serialization the batch AI
                // translator uses; importantly the numbering is deterministic
                // given the source segment, so re-import can regenerate the
                // matching TagMap and call SegmentTagHandler.ReconstructTarget
                // to put the tags back where the proofreader moved them to.
                //
                // After serialising we run the result through
                // BilingualTagNamer.ApplySemanticNames so recognised cf
                // pairs (bold/italic/underline) become <b>/<i>/<u>/<bi> —
                // matching the Workbench's "With Tags" Bilingual Table
                // export style. Unrecognised tags keep their numbered
                // <tN> form.
                var sourceSer = Core.SegmentTagHandler.Serialize(pair.Source);
                var sourceText = Core.Export.BilingualTagNamer.ApplySemanticNames(
                    sourceSer.SerializedText ?? "", sourceSer.TagMap);
                if (string.IsNullOrWhiteSpace(Core.SegmentTagHandler.StripTagPlaceholders(sourceText))) continue;

                string targetText = "";
                if (pair.Target != null)
                {
                    var targetSer = Core.SegmentTagHandler.Serialize(pair.Target);
                    targetText = Core.Export.BilingualTagNamer.ApplySemanticNames(
                        targetSer.SerializedText ?? "", targetSer.TagMap);
                }

                IParagraphUnit parentParagraphUnit = null;
                string puId = "", segId = "";
                try
                {
                    parentParagraphUnit = _activeDocument.GetParentParagraphUnit(pair);
                    puId = parentParagraphUnit?.Properties?.ParagraphUnitId.Id ?? "";
                }
                catch { }
                try { segId = pair.Properties?.Id.Id ?? ""; } catch { }

                var status = "";
                try
                {
                    status = pair.Properties?.ConfirmationLevel.ToString() ?? "";
                }
                catch { }

                // v4.20.24: confirmation-status filter. When the user has
                // ticked specific statuses in the UI, skip any segment
                // whose status isn't in the chosen set. Empty / null
                // filter = include everything (matches pre-v4.20.24
                // behaviour). Comparison is case-insensitive on the
                // enum's ToString() form.
                if (statusFilter != null && statusFilter.Count > 0
                    && !statusFilter.Contains(status ?? ""))
                {
                    continue;
                }

                // Detect paragraph-level formatting (Heading 1 bold, whole-
                // paragraph italic, etc.) so the bilingual file can render
                // segments with the same visual styling Trados shows in its
                // editor. Only meaningful when the segment has no inline
                // tags — inline cf-bold/italic is already serialised as
                // <b>/<i> markers and applying paragraph-level bold ON TOP
                // would over-style mixed-formatting segments. The detector
                // is best-effort: it reads IText run formatting + parent
                // paragraph context, with both probes wrapped in try/catch
                // so an SDK quirk on any one probe doesn't lose the whole
                // segment.
                bool pBold = false, pItalic = false, pUnderline = false;
                if (!sourceSer.HasTags)
                {
                    DetectParagraphLevelFormatting(pair.Source, parentParagraphUnit,
                        out pBold, out pItalic, out pUnderline);

                    // The context says what the paragraph STYLE asks for. A cf
                    // formatting tag wrapping the paragraph can override it -
                    // and does, in documents where body text was styled as a
                    // heading and then un-bolded run by run. Word's precedence
                    // puts the run above the style, so an explicit value here
                    // wins. Silence leaves the context's answer standing.
                    bool? rBold, rItalic, rUnderline;
                    DetectEnclosingRunFormatting(pair.Source,
                        out rBold, out rItalic, out rUnderline);
                    if (rBold.HasValue)      pBold      = rBold.Value;
                    if (rItalic.HasValue)    pItalic    = rItalic.Value;
                    if (rUnderline.HasValue) pUnderline = rUnderline.Value;
                }

                // Tag the segment with its source-file identity. Single-
                // file documents end up with the only file's id+name on
                // every row; multi-file documents have the correct per-
                // segment attribution.
                var segFileId = GetFileIdForSegment(pair);
                string segFileName = null;
                if (!string.IsNullOrEmpty(segFileId))
                    fileIdToName.TryGetValue(segFileId, out segFileName);
                if (string.IsNullOrEmpty(segFileName))
                    segFileName = SafeGetActiveFileName();

                // The counter survives only as a fallback for a segment with no
                // id - which should not happen, but must not break an export.
                var fallbackNumber = number++;

                result.Add(new Core.Export.ExportSegment
                {
                    // Studio's own number, so a reader can look a row up in the grid
                    // rather than counting rows. A split segment is "209a", and the
                    // rows after it keep matching Studio instead of drifting by one.
                    Number = !string.IsNullOrEmpty(segId)
                        ? Core.Export.SegmentNumber.Canonical(segId)
                        : fallbackNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ParagraphUnitId = puId,
                    SegmentId = segId,
                    SourceText = sourceText,
                    TargetText = targetText,
                    Status = status,
                    SourceHash = Core.Export.BilingualExporter.HashPrefix(sourceText),
                    IsBold = pBold,
                    IsItalic = pItalic,
                    IsUnderline = pUnderline,
                    SourceFileId = segFileId ?? "",
                    SourceFileName = segFileName ?? "",
                    IsLocked = isLocked,
                    Comments = includeComments ? CollectSegmentComments(pair) : ""
                });
            }
            return result;
        }

        /// <summary>Gather the Trados comments attached to a segment pair,
        /// one per line, formatted "Author (yyyy-MM-dd): text". Comments
        /// live as ICommentMarker containers in the segment's markup tree
        /// (Studio wraps the commented selection — or the whole segment
        /// content for segment-scope comments — on whichever side the
        /// comment was made). Both source and target are walked; file-scope
        /// comments are not per-segment and are not included. Best-effort:
        /// any SDK quirk yields an empty string, never an exception.</summary>
        private static string CollectSegmentComments(ISegmentPair pair)
        {
            var lines = new List<string>();
            try
            {
                AppendCommentsFrom(pair?.Source, lines);
                AppendCommentsFrom(pair?.Target, lines);
            }
            catch { }
            return string.Join("\n", lines);
        }

        private static void AppendCommentsFrom(IAbstractMarkupDataContainer container, List<string> lines)
        {
            if (container == null) return;
            foreach (var item in container)
            {
                if (item is ICommentMarker marker)
                {
                    try
                    {
                        var props = marker.Comments;
                        for (int i = 0; i < (props?.Count ?? 0); i++)
                        {
                            var c = props.GetItem(i);
                            if (c == null) continue;
                            var text = (c.Text ?? "").Trim();
                            if (text.Length == 0) continue;
                            lines.Add(FormatComment(c.Author, c.Date, text));
                        }
                    }
                    catch { }
                    AppendCommentsFrom(marker, lines); // nested markers
                }
                else if (item is IAbstractMarkupDataContainer nested)
                {
                    AppendCommentsFrom(nested, lines);
                }
            }
        }

        private static string FormatComment(string author, DateTime date, string text)
        {
            author = (author ?? "").Trim();
            // Trados leaves Date at default when unset — treat anything
            // before 1900 as "no date".
            var dateStr = date.Year > 1900 ? date.ToString("yyyy-MM-dd") : "";
            if (author.Length > 0 && dateStr.Length > 0) return $"{author} ({dateStr}): {text}";
            if (author.Length > 0) return $"{author}: {text}";
            if (dateStr.Length > 0) return $"{dateStr}: {text}";
            return text;
        }

        /// <summary>Build a synthetic manifest from the current document — used
        /// when the user picks a file without a sidecar JSON. Loses tamper
        /// detection but lets the round-trip still work in best-effort mode.</summary>
        private Core.Export.ExportManifest BuildManifestFromCurrentDocument()
        {
            var m = new Core.Export.ExportManifest
            {
                ProjectName = SafeGetProjectName(),
                SourceFileName = SafeGetActiveFileName(),
                SourceLanguage = GetDocumentSourceLanguage() ?? "",
                TargetLanguage = GetDocumentTargetLanguage() ?? "",
                ExportTimestampUtc = DateTime.UtcNow,
                Format = "",
                Layout = "",
                ToolVersion = SafeGetPluginVersion()
            };

            int n = 1;
            foreach (var pair in _activeDocument.SegmentPairs)
            {
                if (pair?.Source == null) continue;
                var src = pair.Source.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(src)) continue;

                string puId = "", segId = "";
                try { puId = _activeDocument.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id ?? ""; } catch { }
                try { segId = pair.Properties?.Id.Id ?? ""; } catch { }

                var fallbackNumber = n++;
                m.Segments.Add(new Core.Export.ExportManifestSegment
                {
                    // Same rule as the export itself: Studio's number, with the
                    // counter only as a fallback for a segment carrying no id.
                    Number = !string.IsNullOrEmpty(segId)
                        ? Core.Export.SegmentNumber.Canonical(segId)
                        : fallbackNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ParagraphUnitId = puId,
                    SegmentId = segId,
                    SourceHash = Core.Export.BilingualExporter.HashPrefix(src),
                    Status = ""
                });
            }
            return m;
        }

        /// <summary>Snapshot the current target text for every segment, keyed
        /// by <c>"{puId}/{segId}"</c>. Used by the importer's diff pass.</summary>
        private Dictionary<string, string> SnapshotCurrentTargets()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_activeDocument == null) return map;
            foreach (var pair in _activeDocument.SegmentPairs)
            {
                if (pair?.Source == null) continue;
                string puId = "", segId = "";
                try { puId = _activeDocument.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id ?? ""; } catch { }
                try { segId = pair.Properties?.Id.Id ?? ""; } catch { }
                if (string.IsNullOrEmpty(puId) || string.IsNullOrEmpty(segId)) continue;

                // CRITICAL: serialise the target the SAME WAY the export
                // path does — Serialize() into numbered <tN>/</tN> markers
                // and then ApplySemanticNames() to convert recognised
                // cf-bold / cf-italic / cf-underline pairs into the friendly
                // <b>/<i>/<u> form. Without this, every segment whose
                // current target contains any inline formatting registers
                // as "changed" on re-import even when the proofreader
                // touched nothing, because the live ToString() value
                // would be plain text while the DOCX cell contains the
                // semantic markers.
                var targetText = "";
                if (pair.Target != null)
                {
                    try
                    {
                        var targetSer = Core.SegmentTagHandler.Serialize(pair.Target);
                        targetText = Core.Export.BilingualTagNamer.ApplySemanticNames(
                            targetSer.SerializedText ?? "", targetSer.TagMap);
                    }
                    catch
                    {
                        // Fall back to plain text on any serialisation
                        // hiccup — better an over-reporting diff than
                        // losing the segment from the lookup entirely.
                        targetText = pair.Target.ToString() ?? "";
                    }
                }
                map[KeyOf(puId, segId)] = targetText ?? "";
            }
            return map;
        }

        /// <summary>Detect paragraph-level bold / italic / underline styling
        /// for a segment. Reads the parent
        /// <c>IParagraphUnit.Properties.Contexts</c> list and inspects each
        /// context via two complementary probes:
        ///
        /// 1. <c>IContextInfo.DisplayStyle</c> — a
        ///    <see cref="System.Drawing.FontStyle"/>? that Trados Studio
        ///    uses to render context-styled text in its editor (Heading 1
        ///    bold, Title italic, etc.). The string form of FontStyle is
        ///    e.g. "Bold", "Bold, Italic" — we match against those names.
        ///    This is the path that catches DOCX heading paragraphs.
        ///
        /// 2. <c>IContextInfo.Formatting</c> — a formatting-group
        ///    collection that some file types populate with explicit
        ///    bold/italic/underline entries. Walked via reflection by
        ///    <see cref="ExtractBoldItalicUnderline"/>.
        ///
        /// Inline cf bold/italic tags around part of a segment are
        /// handled separately by SegmentTagHandler; the caller skips this
        /// probe for tag-bearing segments to avoid double-applying
        /// styling. All access is through reflection on the context's
        /// runtime type — no strongly-typed reference to SDK formatting
        /// interfaces is made anywhere in the method signatures, so the
        /// class loads cleanly even if any of those types ship in
        /// different assemblies across SDK versions.</summary>
        private static void DetectParagraphLevelFormatting(
            ISegment sourceSegment,
            IParagraphUnit parentParagraphUnit,
            out bool isBold,
            out bool isItalic,
            out bool isUnderline)
        {
            isBold = false; isItalic = false; isUnderline = false;

            try
            {
                var contexts = parentParagraphUnit?.Properties?.Contexts;
                if (contexts == null) return;
                System.Collections.IEnumerable list = null;
                try { list = contexts.Contexts as System.Collections.IEnumerable; } catch { }
                if (list == null)
                {
                    try { list = contexts as System.Collections.IEnumerable; } catch { }
                }
                if (list == null) return;

                foreach (var ctx in list)
                {
                    if (ctx == null) continue;

                    // Probe 1: DisplayStyle. Universal across DOCX / PPTX /
                    // Excel etc. — this is the field that drives Trados'
                    // own editor rendering of context-styled text.
                    try
                    {
                        var dsProp = ctx.GetType().GetProperty("DisplayStyle");
                        if (dsProp != null)
                        {
                            var ds = dsProp.GetValue(ctx, null);
                            if (ds != null)
                            {
                                var dsStr = ds.ToString() ?? "";
                                if (dsStr.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) >= 0)
                                    isBold = true;
                                if (dsStr.IndexOf("Italic", StringComparison.OrdinalIgnoreCase) >= 0)
                                    isItalic = true;
                                if (dsStr.IndexOf("Underline", StringComparison.OrdinalIgnoreCase) >= 0)
                                    isUnderline = true;
                            }
                        }
                    }
                    catch { }

                    // Probe 2: Formatting collection. File-type-dependent
                    // fallback for SDLXLIFFs that publish their paragraph
                    // styling via an explicit IFormattingGroup rather than
                    // via DisplayStyle.
                    try
                    {
                        var fmtProp = ctx.GetType().GetProperty("Formatting");
                        if (fmtProp != null)
                        {
                            var fmt = fmtProp.GetValue(ctx, null);
                            if (fmt != null)
                            {
                                ExtractBoldItalicUnderline(fmt, ref isBold, ref isItalic, ref isUnderline);
                            }
                        }
                    }
                    catch { }

                    // Probe 3: ParagraphFormatting metadata. THE one for
                    // DOCX. Trados encodes the paragraph's Word run-
                    // property block as a metadata string under the key
                    // "ParagraphFormatting" — looks like
                    //   <w:pPr><w:rPr><w:b/><w:bCs/></w:rPr></w:pPr>
                    // for a paragraph-wide bold paragraph (Heading 1
                    // included). We don't need to interpret the style
                    // name itself — we just look for the Word formatting
                    // markers w:b (bold), w:i (italic), w:u (underline)
                    // directly. Catches every DOCX case I've seen,
                    // regardless of style-name conventions.
                    try
                    {
                        var paraFmt = TryGetContextMetaData(ctx, "ParagraphFormatting");
                        if (!string.IsNullOrEmpty(paraFmt))
                        {
                            // <w:b/> = bold on, <w:b w:val="false"/> = bold off.
                            // We only flip the flag ON for explicit-true markers;
                            // explicit-false stays unset.
                            if (HasWordPropertyOn(paraFmt, "b"))      isBold = true;
                            if (HasWordPropertyOn(paraFmt, "i"))      isItalic = true;
                            if (HasWordPropertyOn(paraFmt, "u"))      isUnderline = true;
                        }
                    }
                    catch { }

                    // Probe 4: style-name heuristic. Fallback for files
                    // where ParagraphFormatting isn't populated but the
                    // style name is recognisable as a heading.
                    try
                    {
                        if (!isBold && ContextLooksLikeHeading(ctx))
                            isBold = true;
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>Read a single string-valued entry from a Trados
        /// IContextInfo's metadata bag. Trados encodes per-context
        /// key/value pairs (e.g. "ParagraphFormatting", "StartsAt") as
        /// metadata; the SDK exposes them via different access patterns
        /// across SDK versions. We try each known pattern in sequence
        /// and return the first non-null/non-empty match. All access
        /// goes through reflection so no compile-time SDK type is
        /// referenced.</summary>
        private static string TryGetContextMetaData(object ctx, string key)
        {
            if (ctx == null || string.IsNullOrEmpty(key)) return null;
            var type = ctx.GetType();

            // Pattern A: GetMetaData(string) instance method.
            try
            {
                var method = type.GetMethod("GetMetaData", new[] { typeof(string) });
                if (method != null)
                {
                    var result = method.Invoke(ctx, new object[] { key });
                    var s = result?.ToString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }

            // Pattern B: MetaData property → dictionary-like → indexer.
            try
            {
                var prop = type.GetProperty("MetaData");
                if (prop != null)
                {
                    var dict = prop.GetValue(ctx, null);
                    if (dict != null)
                    {
                        var indexer = dict.GetType().GetMethod("get_Item", new[] { typeof(string) });
                        if (indexer != null)
                        {
                            var result = indexer.Invoke(dict, new object[] { key });
                            var s = result?.ToString();
                            if (!string.IsNullOrEmpty(s)) return s;
                        }
                    }
                }
            }
            catch { }

            // Pattern C: MetaDataCount + GetMetaDataItem(index) enumeration.
            // The interface returns IMetaDataItem with Key/Value string
            // properties. Walk it linearly until we find the key.
            try
            {
                var countProp = type.GetProperty("MetaDataCount");
                var getItem = type.GetMethod("GetMetaDataItem", new[] { typeof(int) });
                if (countProp != null && getItem != null)
                {
                    var count = (int)(countProp.GetValue(ctx, null) ?? 0);
                    for (int i = 0; i < count; i++)
                    {
                        var item = getItem.Invoke(ctx, new object[] { i });
                        if (item == null) continue;
                        var itemType = item.GetType();
                        var itemKey = itemType.GetProperty("Key")?.GetValue(item, null) as string;
                        if (!string.Equals(itemKey, key, StringComparison.Ordinal)) continue;
                        var itemVal = itemType.GetProperty("Value")?.GetValue(item, null) as string;
                        if (!string.IsNullOrEmpty(itemVal)) return itemVal;
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>Detect whether a Word run-property fragment carries an
        /// "on" marker for the given short element name (e.g. "b" for
        /// bold, "i" for italic, "u" for underline). Word represents
        /// these as:
        ///   <c>&lt;w:b/&gt;</c>                — element-only, defaults to on
        ///   <c>&lt;w:b w:val="true"/&gt;</c>   — explicit on
        ///   <c>&lt;w:b w:val="false"/&gt;</c>  — explicit off (only relevant
        ///                                       when an inherited style was
        ///                                       on; we treat it as off here)
        ///   <c>&lt;w:u w:val="single"/&gt;</c> — underline style (treated as on
        ///                                       for any non-"none" value)
        /// Returns true for the on cases, false otherwise. Conservative
        /// — when in doubt, returns false.</summary>
        private static bool HasWordPropertyOn(string paraFormatting, string elementShortName)
        {
            if (string.IsNullOrEmpty(paraFormatting)) return false;
            // Look for <w:b ... /> or <w:b/> patterns. The fragment is
            // raw XML text (possibly with mixed casing); use ordinal
            // case-insensitive matching.
            var openMarker = "<w:" + elementShortName;
            int idx = 0;
            while ((idx = paraFormatting.IndexOf(openMarker, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = idx + openMarker.Length;
                if (end >= paraFormatting.Length) break;
                char next = paraFormatting[end];
                // Need the element name to end here — not be the start of
                // a longer name like "bCs" or "bdr".
                if (next == '/' || next == ' ' || next == '>' || next == '\t' || next == '\n')
                {
                    // Find the end of this tag.
                    int tagEnd = paraFormatting.IndexOf('>', end);
                    if (tagEnd < 0) return false;
                    var tag = paraFormatting.Substring(idx, tagEnd - idx + 1);
                    // Self-closing or no w:val? Treat as on.
                    int valIdx = tag.IndexOf("w:val=", StringComparison.OrdinalIgnoreCase);
                    if (valIdx < 0) return true;
                    // Extract the value inside the quotes.
                    int q1 = tag.IndexOf('"', valIdx);
                    int q2 = q1 >= 0 ? tag.IndexOf('"', q1 + 1) : -1;
                    if (q1 < 0 || q2 < 0) return true; // can't parse; assume on
                    var val = tag.Substring(q1 + 1, q2 - q1 - 1).Trim().ToLowerInvariant();
                    if (val == "false" || val == "0" || val == "off" || val == "none") return false;
                    return true;
                }
                idx = end;
            }
            return false;
        }

        // Regex matching DOCX heading-style context names. Catches
        // "Heading 1" / "heading2" / "h1" / "h-1" / "Title" / "Subtitle".
        // Anchored loosely (look-around for word boundaries) to avoid
        // matching unrelated words containing "title" or "heading" as
        // substrings of longer style names.
        private static readonly System.Text.RegularExpressions.Regex HeadingStyleRe =
            new System.Text.RegularExpressions.Regex(
                @"(?<![a-z])(?:heading\s*-?\s*[1-9]?|h-?[1-6]|title|subtitle)(?![a-z])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Probe a context's string-typed name fields for an
        /// indication that the parent paragraph is a heading-style
        /// paragraph (Heading 1-6, Title, Subtitle, etc.). Reads
        /// Description / DisplayName / Code / DisplayCode via reflection,
        /// so it stays decoupled from the specific IContextInfo SDK
        /// type's shape.</summary>
        private static bool ContextLooksLikeHeading(object ctx)
        {
            if (ctx == null) return false;
            var type = ctx.GetType();
            foreach (var propName in new[] { "Description", "DisplayName", "Code", "DisplayCode" })
            {
                try
                {
                    var prop = type.GetProperty(propName);
                    if (prop == null) continue;
                    var val = prop.GetValue(ctx, null) as string;
                    if (string.IsNullOrEmpty(val)) continue;
                    if (HeadingStyleRe.IsMatch(val)) return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>Extract bold/italic/underline flags from any
        /// formatting-collection-like object using pure reflection. Accepts
        /// <c>object</c> instead of a typed <c>IFormattingGroup</c>
        /// parameter on purpose — referencing
        /// <c>Sdl.FileTypeSupport.Framework.Formatting.IFormattingGroup</c>
        /// in a method signature forces the CLR to resolve the type at
        /// class-load time, and that interface isn't shipped in Studio 18's
        /// runtime assemblies. A typed reference here makes the entire
        /// AiAssistantViewPart class fail to load with a silent
        /// TypeLoadException — the ViewPart disappears from the Trados UI
        /// with no visible error. Pure reflection sidesteps that.</summary>
        private static void ExtractBoldItalicUnderline(
            object fmt,
            ref bool isBold, ref bool isItalic, ref bool isUnderline)
        {
            if (fmt == null) return;
            try
            {
                var type = fmt.GetType();
                var keysProp = type.GetProperty("Keys");
                if (keysProp == null) return;
                var keys = keysProp.GetValue(fmt, null) as System.Collections.IEnumerable;
                if (keys == null) return;

                // Indexer: find the get_Item(string) method.
                var indexer = type.GetMethod("get_Item", new[] { typeof(string) });

                foreach (var keyObj in keys)
                {
                    var lc = (keyObj?.ToString() ?? "").ToLowerInvariant();
                    if (string.IsNullOrEmpty(lc)) continue;

                    object valObj = null;
                    if (indexer != null)
                    {
                        try { valObj = indexer.Invoke(fmt, new object[] { keyObj?.ToString() }); }
                        catch { continue; }
                    }
                    var val = (valObj?.ToString() ?? "").ToLowerInvariant();
                    bool isOn = val.IndexOf("true", StringComparison.Ordinal) >= 0
                             || val.IndexOf("single", StringComparison.Ordinal) >= 0;
                    if (!isOn) continue;
                    if (lc.Contains("bold")) isBold = true;
                    else if (lc.Contains("italic")) isItalic = true;
                    else if (lc.Contains("underline")) isUnderline = true;
                }
            }
            catch { }
        }

        /// <summary>
        /// Reads the character formatting that actually applies to a segment,
        /// as a tri-state: true = explicitly on, false = explicitly off,
        /// null = not specified at this level.
        ///
        /// This exists because paragraph context alone is not enough to know
        /// whether text is bold. A paragraph can carry a bold paragraph STYLE
        /// while its runs individually switch bold back off — Word's own
        /// precedence, where the run wins over the style. Trados records the
        /// override as a &lt;cf bold=False&gt; formatting tag pair, and —
        /// this is the part that makes it non-obvious — when that tag
        /// encloses several whole segments, EACH of those segments becomes a
        /// literal CHILD of the ITagPair in the document tree, rather than
        /// containing the tag itself. So the paragraph context (style-level)
        /// and the ancestor tag pair (run-level override) can disagree, and
        /// only the tree walk here sees the override.
        ///
        /// Real example, from a client's patent application: body paragraphs
        /// were styled "Subtitle" (a bold style) and then un-bolded run by
        /// run. Word and the Trados editor both render them as plain text and
        /// the author never noticed. Their contexts are byte-identical to a
        /// genuine bold heading in the same document, so context alone
        /// reported two dozen ordinary paragraphs as bold headings.
        ///
        /// ISegment implements IAbstractMarkupData (confirmed by reflecting
        /// on Sdl.FileTypeSupport.Framework.Core.dll — Type.GetProperty on an
        /// INTERFACE only returns members declared directly on it, so a naive
        /// property probe on ISegment's own declared members misses this;
        /// the concrete runtime type does implement Parent correctly), which
        /// is what makes the walk below valid: Parent returns the enclosing
        /// container, and when that container is itself an ITagPair — also
        /// IAbstractMarkupData — the walk continues outward. It stops
        /// naturally at IParagraph, which is a container but not markup data.
        ///
        /// Innermost formatting wins, matching how character formatting
        /// actually cascades.
        /// </summary>
        private static void DetectEnclosingRunFormatting(
            ISegment sourceSegment,
            out bool? isBold, out bool? isItalic, out bool? isUnderline)
        {
            isBold = null; isItalic = null; isUnderline = null;
            if (sourceSegment == null) return;

            try
            {
                IAbstractMarkupData node = sourceSegment;
                // Bounded so a malformed tree cannot spin forever.
                for (int depth = 0; node != null && depth < 64; depth++)
                {
                    if (node is ITagPair tagPair)
                    {
                        var fmt = tagPair.StartTagProperties?.Formatting;
                        if (fmt != null)
                        {
                            if (isBold      == null) isBold      = ReadBoolFormatting(fmt, "Bold");
                            if (isItalic    == null) isItalic    = ReadBoolFormatting(fmt, "Italic");
                            if (isUnderline == null) isUnderline = ReadBoolFormatting(fmt, "Underline");
                        }
                    }

                    if (isBold != null && isItalic != null && isUnderline != null) break;

                    // IAbstractMarkupData.Parent returns IAbstractMarkupDataContainer.
                    // Continuing the walk requires that container to ALSO be
                    // markup data (true for ITagPair, false for IParagraph),
                    // which is exactly the SDK's own stopping point for "this
                    // is the top of the formatting tree".
                    node = node.Parent as IAbstractMarkupData;
                }
            }
            catch { }
        }

        /// <summary>
        /// Reads a boolean formatting value (Bold / Italic / Underline) from
        /// an IFormattingGroup, distinguishing "explicitly false" from "not
        /// present" — the distinction the whole fix depends on. An older
        /// version of this reader used loose reflection to look for a "Keys"
        /// property on IFormattingGroup and always came back empty: the SDK
        /// implements IFormattingGroup as IDictionary&lt;string,
        /// IFormattingItem&gt; with an EXPLICIT interface implementation, so
        /// Keys is invisible to Type.GetProperty("Keys") on the concrete
        /// class even though it works fine when called through the
        /// documented Contains(string) / this[string] surface used here.
        /// </summary>
        private static bool? ReadBoolFormatting(
            Sdl.FileTypeSupport.Framework.Formatting.IFormattingGroup fmt, string name)
        {
            if (fmt == null || !fmt.Contains(name)) return null;
            return (fmt[name] as Sdl.FileTypeSupport.Framework.Formatting.AbstractBooleanFormatting)?.Value;
        }

        /// <summary>Snapshot of (puId/segId) keys that should not be silently
        /// overwritten on re-import. Currently empty in the MVP — Trados'
        /// own segment-protection will reject writes to locked segments
        /// inside <c>ProcessSegmentPair</c>, so the worst case is a per-
        /// segment "write failed" log line rather than data corruption.
        /// The per-confirmation-level conflict policy is a Phase-3 follow-up
        /// once the right enum values for "locked / rejected" are confirmed
        /// against the live SDK enum (`Sdl.Core.Globalization.ConfirmationLevel`
        /// has different value names across Studio SDK versions).</summary>
        /// <summary>v4.20.18: actually reads pair.Properties.IsLocked
        /// from every segment in the active document. Was previously a
        /// stub returning an empty set — meaning re-import would happily
        /// overwrite locked segments. Now the BilingualImporter's
        /// isWriteable predicate sees the real picture and refuses to
        /// write back to locked segments (they show up as the "locked"
        /// counter in the re-import dialog's "other issues" line).</summary>
        private HashSet<string> SnapshotLockedSegments()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (_activeDocument == null) return set;
            try
            {
                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    if (pair == null) continue;
                    bool locked = false;
                    try { locked = pair.Properties?.IsLocked ?? false; }
                    catch { }
                    if (!locked) continue;
                    string puId = "", segId = "";
                    try { puId = _activeDocument.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id ?? ""; }
                    catch { }
                    try { segId = pair.Properties?.Id.Id ?? ""; }
                    catch { }
                    if (string.IsNullOrEmpty(puId) || string.IsNullOrEmpty(segId)) continue;
                    set.Add(KeyOf(puId, segId));
                }
            }
            catch { }
            return set;
        }

        /// <summary>Snapshot of (puId/segId) → live source's STRUCTURAL
        /// tag count. Used by the importer's tag-integrity check.
        /// "Structural" = tags that DON'T map to a semantic name via
        /// BilingualTagNamer.DetectSemantic; i.e. field codes, page
        /// numbers, custom format pairs, line breaks — anything that
        /// stays as <tN> in the exported bilingual file rather than
        /// becoming <b>/<i>/<u>/<bi>. These are the tags whose count
        /// must round-trip exactly because Trados file structure depends
        /// on them. Semantic formatting tags (bold/italic/underline) are
        /// intentionally excluded — the proofreader can add or remove
        /// them at will without breaking Trados QA.</summary>
        private Dictionary<string, int> SnapshotSourceTagCounts()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            if (_activeDocument == null) return map;
            foreach (var pair in _activeDocument.SegmentPairs)
            {
                if (pair?.Source == null) continue;
                string puId = "", segId = "";
                try { puId = _activeDocument.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id ?? ""; } catch { }
                try { segId = pair.Properties?.Id.Id ?? ""; } catch { }
                if (string.IsNullOrEmpty(puId) || string.IsNullOrEmpty(segId)) continue;

                int structuralCount = 0;
                try
                {
                    var ser = Core.SegmentTagHandler.Serialize(pair.Source);
                    if (ser?.TagMap != null)
                    {
                        foreach (var kv in ser.TagMap)
                        {
                            // A tag is "structural" if BilingualTagNamer
                            // can't assign it a semantic short-name —
                            // i.e. it's NOT a cf bold / italic / underline
                            // / bi pair. Standalone tags (line breaks,
                            // page-number placeholders, etc.) are always
                            // structural since DetectSemantic only
                            // recognises ITagPair entries.
                            if (Core.Export.BilingualTagNamer.DetectSemantic(kv.Value) == null)
                                structuralCount++;
                        }
                    }
                }
                catch { structuralCount = 0; }

                map[KeyOf(puId, segId)] = structuralCount;
            }
            return map;
        }

        /// <summary>Find the live <c>ISegmentPair</c> for a given
        /// (paragraph-unit, segment) id pair. Returns <c>null</c> if not
        /// found.</summary>
        private static string KeyOf(string puId, string segId) =>
            (puId ?? "") + "/" + (segId ?? "");

        /// <summary>Build a one-shot (puId/segId → pair) lookup for the whole
        /// active document, so the re-import writeback can resolve each segment
        /// in O(1) instead of re-scanning every SegmentPair per change (the old
        /// FindSegmentPair path — O(n²), which froze/crashed on large merged
        /// multi-file documents). Keeps the FIRST pair for a given key, exactly
        /// matching FindSegmentPair's old "first match wins" behaviour, and logs
        /// when keys collide across merged files (paragraph-unit ids are only
        /// unique within a single .sdlxliff, so a merged multi-file document CAN
        /// collide — full file-aware routing is a planned follow-up).</summary>
        private Dictionary<string, ISegmentPair> BuildSegmentPairIndex(Controls.ImportExportControl ctrl)
        {
            var index = new Dictionary<string, ISegmentPair>(StringComparer.Ordinal);
            if (_activeDocument == null) return index;
            int collisions = 0;
            foreach (var pair in _activeDocument.SegmentPairs)
            {
                string puId = "", segId = "";
                try { puId = _activeDocument.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id ?? ""; } catch { }
                try { segId = pair.Properties?.Id.Id ?? ""; } catch { }
                if (string.IsNullOrEmpty(puId) || string.IsNullOrEmpty(segId)) continue;
                var key = KeyOf(puId, segId);
                if (index.ContainsKey(key)) { collisions++; continue; } // first wins (matches old FindSegmentPair)
                index[key] = pair;
            }
            if (collisions > 0 && ctrl != null)
                ctrl.AppendLog(
                    $"Note: {collisions} segment(s) share a paragraph/segment id across the merged files. " +
                    "Re-import writes to the first match for those; if a translation lands in the wrong file, " +
                    "re-import one file at a time. (File-aware routing is a planned improvement.)", true);
            return index;
        }

        /// <summary>Build a unified <see cref="Core.TagInfo"/> dictionary
        /// that combines source-side and target-side tag references. Source
        /// tags pass through unchanged; target tags are pre-cloned via
        /// <see cref="IAbstractMarkupData.Clone"/> so they survive the
        /// <c>sp.Target.Clear()</c> that ReconstructTarget runs internally.
        /// On numbering collisions (source and target both have <c>&lt;tN&gt;</c>
        /// for the same N), target wins because the proofreader's edits
        /// live in the target cell.</summary>
        private static Dictionary<int, Core.TagInfo> BuildCombinedTagMap(
            Dictionary<int, Core.TagInfo> sourceTagMap,
            Dictionary<int, Core.TagInfo> targetTagMap)
        {
            var combined = new Dictionary<int, Core.TagInfo>();

            if (sourceTagMap != null)
            {
                foreach (var kv in sourceTagMap)
                    combined[kv.Key] = kv.Value;
            }

            if (targetTagMap != null)
            {
                foreach (var kv in targetTagMap)
                {
                    var clone = CloneTagInfo(kv.Value);
                    if (clone != null)
                        combined[kv.Key] = clone;
                }
            }

            return combined;
        }

        /// <summary>Deep-clone a <see cref="Core.TagInfo"/> so its
        /// <c>OriginalMarkup</c> reference can be used after the original
        /// segment is cleared. Returns null if cloning fails (rare —
        /// IAbstractMarkupData.Clone is generally well-behaved).</summary>
        private static Core.TagInfo CloneTagInfo(Core.TagInfo info)
        {
            if (info?.OriginalMarkup == null) return null;
            try
            {
                var clonedMarkup = info.OriginalMarkup.Clone() as IAbstractMarkupData;
                if (clonedMarkup == null) return null;
                return new Core.TagInfo
                {
                    TagType = info.TagType,
                    OriginalMarkup = clonedMarkup,
                    IsLineBreak = info.IsLineBreak
                };
            }
            catch
            {
                return null;
            }
        }

        private string SafeGetProjectName()
        {
            try
            {
                var pc = SdlTradosStudio.Application.GetController<ProjectsController>();
                return pc?.CurrentProject?.GetProjectInfo()?.Name ?? "Trados project";
            }
            catch { return "Trados project"; }
        }

        private string SafeGetActiveFileName()
        {
            try
            {
                var name = _activeDocument?.ActiveFile?.Name;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            return "";
        }

        private static string SafeGetPluginVersion()
        {
            try
            {
                return typeof(AiAssistantViewPart).Assembly.GetName().Version?.ToString() ?? "";
            }
            catch { return ""; }
        }
    }
}

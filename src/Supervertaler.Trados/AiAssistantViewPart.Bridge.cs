using System;
using System.Collections.Generic;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Licensing;

namespace Supervertaler.Trados
{
    /// <summary>
    /// The Supervertaler bridge (MCP / Workbench), owned by the plugin rather than
    /// by the AI Assistant pane.
    ///
    /// <para><b>Why.</b> The bridge used to start inside the pane, and the pane
    /// only exists once Studio sets up its Editor view. In Studio 2024/2026 the
    /// bridge loader forces the pane into being at startup, but in Studio 2022 it
    /// cannot (GetController returns null until the view is shown), so there the
    /// MCP tools failed with "handshake file not found" until a document was
    /// opened - even tools that only read disk (list_projects, templates, banks)
    /// or create a project. Now <see cref="AppInitializer"/> starts the bridge as
    /// Studio starts, in every version.</para>
    ///
    /// <para><b>How.</b> One bridge per Studio process, in <see cref="_bridge"/>.
    /// Its pane handlers call the pane through <see cref="_currentInstance"/> at
    /// request time; until the pane has finished initialising, the bridge's gate
    /// (<see cref="SupervertalerBridge.NeedsNoEditor"/>) answers those endpoints
    /// with <see cref="SupervertalerBridge.EditorNotReadyMessage"/>. Handlers that
    /// need no pane (create/open project, task status, banks, term DB path) are
    /// static and work at once. When the pane initialises it finds the bridge
    /// running and only refreshes the handshake (project/document name).</para>
    ///
    /// <para><b>Failure.</b> Starting is attempted once per session (plus once more
    /// if the licence was missing at startup and is activated later). Any
    /// exception is caught and written to the diagnostic log once; Studio and the
    /// rest of the plugin carry on without a bridge, as before.</para>
    /// </summary>
    public partial class AiAssistantViewPart
    {
        private static readonly object _bridgeLock = new object();
        private static SupervertalerBridge _bridge;
        private static bool _bridgeStartAttempted;
        private static bool _bridgeLicenceHooked;

        /// <summary>
        /// Starts the bridge if it is not running yet. Idempotent and safe to call
        /// from Studio startup, from the pane, and on a licence change. Never throws.
        /// Call on the UI thread: the handshake reads the open document's name.
        /// </summary>
        internal static void EnsureBridgeStarted(string reason)
        {
            try
            {
                lock (_bridgeLock)
                {
                    if (_bridge != null)
                    {
                        // Already up (from Studio startup): the pane has just appeared,
                        // so the handshake can now name the project and document.
                        try { _bridge.RefreshInstanceFile(); } catch { }
                        return;
                    }
                    if (_bridgeStartAttempted)
                        return;   // one attempt per session; its failure is already logged

                    if (!LicenseManager.Instance.HasAssistantAccess)
                    {
                        // Noted once: the pane asks again on every licence change.
                        if (!_bridgeLicenceHooked)
                            BridgeLog.Write($"EnsureBridgeStarted({reason}): HasAssistantAccess=false (tier={LicenseManager.Instance.CurrentTier}) – bridge not started until a licence or trial is active");
                        HookLicenceChange();
                        return;
                    }

                    _bridgeStartAttempted = true;
                    BridgeLog.Write($"EnsureBridgeStarted({reason}): starting (tier={LicenseManager.Instance.CurrentTier})");
                    var bridge = CreateBridge();
                    bridge.Start();
                    if (bridge.IsRunning)
                    {
                        _bridge = bridge;
                        DiagnosticLog.WriteAlways("Bridge", $"started ({reason}) on port {bridge.Port}");
                    }
                    else
                    {
                        try { bridge.Dispose(); } catch { }
                        DiagnosticLog.WriteAlways("Bridge",
                            $"could not start ({reason}); MCP tools will not reach this Studio this session. Details in bridge.log.");
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    BridgeLog.Write($"EnsureBridgeStarted({reason}) THREW: {ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}");
                    DiagnosticLog.WriteAlways("Bridge", $"could not start ({reason}): {ex.GetType().Name}: {ex.Message}");
                }
                catch { }
            }
        }

        /// <summary>The licence was missing when the bridge was first wanted: start it
        /// once access appears. Hooked once.</summary>
        private static void HookLicenceChange()
        {
            if (_bridgeLicenceHooked) return;
            _bridgeLicenceHooked = true;
            LicenseManager.Instance.LicenseStateChanged += (s, e) =>
            {
                if (!LicenseManager.Instance.HasAssistantAccess) return;
                try
                {
                    if (UiThread.InvokeRequired && UiThread.IsAvailable)
                        UiThread.Invoke(() => EnsureBridgeStarted("licence activated"));
                    else
                        EnsureBridgeStarted("licence activated");
                }
                catch { }
            };
        }

        /// <summary>Stops the bridge (pane Dispose at Studio shutdown).</summary>
        private static void StopBridge()
        {
            lock (_bridgeLock)
            {
                if (_bridge == null) return;
                try { _bridge.Dispose(); } catch { /* never let bridge cleanup break Dispose */ }
                _bridge = null;
            }
        }

        /// <summary>True once the pane has wired itself to the Editor - what the
        /// gated endpoints need.</summary>
        internal static bool PaneReady
        {
            get
            {
                var p = _currentInstance;
                return p != null && p._fullyInitialized;
            }
        }

        /// <summary>The pane, for a gated endpoint. The gate has checked
        /// <see cref="PaneReady"/>; this throws only in the moment it might change.</summary>
        private static AiAssistantViewPart Pane =>
            _currentInstance ?? throw new InvalidOperationException(SupervertalerBridge.EditorNotReadyMessage);

        private static SupervertalerBridge CreateBridge() => new SupervertalerBridge(
            getContext: () => Pane.BuildBridgeContextSnapshot(),
            insertText: t => Pane.BridgeInsertTranslation(t),
            getProject: () => Pane.BuildBridgeProjectSnapshot(),
            getSegments: q => Pane.BuildBridgeSegments(q),
            getDbPath: ResolveSupervertalerDbPath,
            updateSegments: r => Pane.BridgeUpdateSegments(r),
            addTerm: r => Pane.BridgeAddTerm(r),
            getFiles: () => Pane.BuildBridgeFiles(),
            findInconsistencies: (a, b) => Pane.BuildBridgeInconsistencies(a, b),
            searchStudioTm: q => Pane.BridgeSearchStudioTm(q),
            runQaCheck: q => Pane.BridgeRunQaCheck(q),
            compareTm: q => Pane.BridgeCompareTm(q),
            listResources: () => Pane.BridgeListResources(),
            goToSegment: r => Pane.BridgeGoToSegment(r),
            getComments: id => Pane.BridgeGetComments(id),
            addComment: r => Pane.BridgeAddComment(r),
            updateComment: r => Pane.BridgeUpdateComment(r),
            deleteComment: r => Pane.BridgeDeleteComment(r),
            runVerification: () => Pane.BridgeRunVerification(),
            findReplace: r => Pane.BridgeFindReplace(r),
            runTask: r => Pane.BridgeRunTask(r),
            getTaskStatus: BridgeGetTaskStatus,
            getPromptContext: n => Pane.BuildPromptContext(n),
            updateTerm: r => Pane.BridgeUpdateTerm(r),
            deleteTerm: r => Pane.BridgeDeleteTerm(r),
            saveDocument: () => Pane.BridgeSaveDocument(),
            getSuperMemoryContext: q => Pane.BridgeGetSuperMemoryContext(q),
            searchSuperMemory: BridgeSearchSuperMemory,
            listSuperMemoryBanks: BridgeListSuperMemoryBanks,
            markReviewed: r => Pane.BridgeMarkReviewed(r),
            getCoverage: () => Pane.BridgeGetCoverage(),
            getTrackedChanges: q => Pane.BridgeGetTrackedChanges(q),
            importTermbase: r => Pane.BridgeImportTermbase(r),
            // Handshake identity: no pane yet = no project/document to name.
            getInstanceInfo: () => _currentInstance?.BuildBridgeInstanceInfo(),
            createProject: BridgeCreateProject,
            openProject: BridgeOpenProject,
            editorReady: () => PaneReady);

        // ─── open_project ──────────────────────────────────────────────────────

        /// <summary>How long the request waits for the open to finish before it
        /// answers with the jobId instead. Opening normally takes a second or two;
        /// longer means Studio is loading many files or showing a message.</summary>
        private static readonly TimeSpan OpenProjectWait = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Bridge: POST /v1/open-project. Runs as a job (get_task_status reports
        /// it) so a Studio message box cannot hold the request: the reply comes
        /// when the open is done, or after <see cref="OpenProjectWait"/> with the
        /// jobId and what Studio is doing.
        /// </summary>
        private static BridgeTaskStatusResponse BridgeOpenProject(BridgeOpenProjectRequest req)
        {
            const string task = "open-project";
            req = req ?? new BridgeOpenProjectRequest();
            if (string.IsNullOrWhiteSpace(req.Project))
                return new BridgeTaskStatusResponse { Ok = false, Task = task, Error = "Say which project to open: its name or its .sdlproj file." };

            RunTaskJob job;
            lock (_jobStartLock)
            {
                var busy = GetRunningJob();
                if (busy != null)
                    return new BridgeTaskStatusResponse
                    {
                        Ok = false,
                        Task = task,
                        Error = $"a batch task ({busy.Friendly}, jobId \"{busy.Id}\") is still running – " +
                                "wait for it to finish (poll get_task_status) before opening a project."
                    };
                job = new RunTaskJob
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Task = task,
                    Friendly = "Open project",
                    Status = "running",
                    StartedUtc = DateTime.UtcNow,
                    Progress = "starting"
                };
                RegisterJob(job);
            }

            var done = new System.Threading.ManualResetEventSlim(false);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { OpenProjectJobBody(job, req); }
                finally { done.Set(); }
            });
            done.Wait(OpenProjectWait);
            // Not disposed: the job may still Set() it after we stop waiting.

            var status = BridgeGetTaskStatus(job.Id);
            if (status.Running)
                status.Note = "Still opening – Studio is " + (status.Progress ?? "busy") +
                              ". Poll get_task_status with jobId \"" + job.Id + "\" until it is done.";
            if (status.Status == "failed") status.Ok = false;
            if (status.Status == "failed" && status.Error == null) status.Error = status.Note;
            return status;
        }

        private static void OpenProjectJobBody(RunTaskJob job, BridgeOpenProjectRequest req)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BridgeLog.Write($"[open-project] job {job.Id} started: \"{req.Project}\"" +
                            (req.AllFiles ? " + all files" : req.Files != null && req.Files.Count > 0 ? $" + {req.Files.Count} named file(s)" : ""));
            ProjectOpenerStudio.Result result;
            try
            {
                result = ProjectOpenerStudio.Run(req.Project, req.AllFiles, req.Files ?? new List<string>(),
                    req.TargetLanguage, p => { lock (job.Sync) job.Progress = p; });
            }
            catch (Exception ex)
            {
                result = new ProjectOpenerStudio.Result { Ok = false, Error = "Opening the project failed: " + ex.Message };
            }
            sw.Stop();
            lock (job.Sync)
            {
                job.FinishedUtc = DateTime.UtcNow;
                job.ProjectFile = result.ProjectFile;
                if (result.Ok)
                {
                    job.Status = "done";
                    job.Files = result.OpenedFiles;
                    job.FileCount = result.OpenedFiles.Count;
                    job.Note = $"Project \"{result.ProjectName}\" is open in Studio" +
                               (result.OpenedFiles.Count > 0
                                   ? $", with {result.OpenedFiles.Count} {result.Language} file(s) open in the Editor as one document. " +
                                     "The editor tools (get_segments, update_segments …) now work on it."
                                   : ". No files were opened in the Editor; ask for them by name, or all of them, to translate.");
                }
                else
                {
                    job.Status = "failed";
                    job.Error = result.Error;
                }
            }
            BridgeLog.Write($"[open-project] job {job.Id} {job.Status} in {sw.Elapsed.TotalSeconds:F1}s" +
                            (result.Ok ? "" : ": " + result.Error));
        }
    }
}

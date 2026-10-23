using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace CapCutBatchClient
{
    public sealed class BatchRunner
    {
        private readonly AppConfig config;
        private readonly ICapCutAutomation automation;
        private readonly TaskStore store;
        private readonly AppLogger logger;
        private readonly IFinalVideoPostProcessor finalVideoPostProcessor;
        private readonly IFinalOutputQualityValidator outputQualityValidator;
        private readonly ITaskSamplePreprocessor samplePreprocessor;
        private readonly CapCutDraftTransactionCoordinator draftTransactions;
        private readonly ManualResetEvent pauseGate = new ManualResetEvent(true);
        private readonly object sync = new object();
        private CancellationTokenSource cancellation;
        private Thread worker;
        private IList<VideoTask> activeTasks;
        private Timer handoffHeartbeat;
        private bool stopRequested;

        public event Action<VideoTask> TaskChanged;
        public event Action<bool, bool> RunStateChanged;
        // Raised only after terminal handoff status has been persisted. The
        // workbench-launched UI uses it to exit cleanly before the next project
        // is allowed to launch another formal client.
        public event Action BatchFinished;

        public bool IsRunning { get { lock (sync) return worker != null && worker.IsAlive; } }
        public bool IsPaused { get; private set; }
        public bool IsStopping { get { lock (sync) return stopRequested; } }

        public BatchRunner(
            AppConfig config,
            ICapCutAutomation automation,
            TaskStore store,
            AppLogger logger,
            ISourceVideoRecycler sourceRecycler = null,
            IFinalVideoPostProcessor finalVideoPostProcessor = null,
            ITaskSamplePreprocessor samplePreprocessor = null,
            IFinalOutputQualityValidator outputQualityValidator = null)
        {
            this.config = config;
            this.automation = automation;
            this.store = store;
            this.logger = logger;
            this.finalVideoPostProcessor = finalVideoPostProcessor ?? new FfmpegFirstFramePostProcessor(logger);
            this.outputQualityValidator = outputQualityValidator ??
                (finalVideoPostProcessor == null ? (IFinalOutputQualityValidator)new FfmpegOutputQualityValidator(logger) : null);
            this.samplePreprocessor = samplePreprocessor ?? (automation as ITaskSamplePreprocessor) ??
                new FfmpegSamplePreprocessor(logger);
            this.draftTransactions = new CapCutDraftTransactionCoordinator(logger);
        }

        public void Start(IList<VideoTask> tasks)
        {
            lock (sync) {
                if (worker != null && worker.IsAlive) return;
                activeTasks = tasks;
                cancellation = new CancellationTokenSource();
                IsPaused = false;
                stopRequested = false;
                pauseGate.Set();
                worker = new Thread(new ThreadStart(delegate { WorkerMain(cancellation.Token); }));
                worker.IsBackground = true;
                worker.Name = "CapCut batch runner";
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start();
            }
            RaiseRunState();
        }

        public void Pause()
        {
            if (!IsRunning) return;
            IsPaused = true;
            pauseGate.Reset();
            logger.Info("批处理已暂停；当前剪映操作完成后停在下一步骤前");
            RaiseRunState();
        }

        public void Resume()
        {
            if (!IsRunning) return;
            IsPaused = false;
            pauseGate.Set();
            logger.Info("批处理继续");
            RaiseRunState();
        }

        public void Stop()
        {
            lock (sync) {
                stopRequested = true;
                if (cancellation != null) cancellation.Cancel();
                IsPaused = false;
                pauseGate.Set();
            }
            // Stop is cooperative.  Do not mark active tasks Waiting or exit
            // the process here: doing so bypasses ProcessTasks' finally block
            // and leaves an unreleased draft lease behind.  The worker owns
            // the transition to stopped/recoverable after CapCut is closed
            // and the original draft directory is restored.
            try {
                if (activeTasks != null) {
                    foreach (VideoTask task in activeTasks.Where(task =>
                        task.GetState() != TaskState.Waiting &&
                        task.GetState() != TaskState.Completed &&
                        task.GetState() != TaskState.Failed)) {
                        draftTransactions.RequestStop(task,
                            task.HandoffAttemptId ?? "",
                            "recover_and_release_lease");
                        task.Error = "正在安全停止；草稿和检查点将保留，可安全继续。";
                        task.FinishedAt = "";
                    }
                    store.Save(activeTasks);
                    HandoffStatusWriter.WriteAll(activeTasks, true, false);
                }
            } catch (Exception ex) {
                logger.Warn("停止客户端时写入终态失败: " + ex.Message);
            }
            logger.Info("已请求停止批处理");
            RaiseRunState();
        }

        public void RunBlocking(IList<VideoTask> tasks, CancellationToken token)
        {
            activeTasks = tasks;
            pauseGate.Set();
            ProcessTasks(token);
        }

        private void WorkerMain(CancellationToken token)
        {
            try { ProcessTasks(token); }
            catch (Exception ex) {
                // A background worker exception used to make the desktop client
                // disappear without a final batch-log entry.  Keep the UI alive
                // and leave a complete diagnostic trail instead.  ProcessTasks
                // normally owns task-state changes, so this is deliberately a
                // last-resort logger rather than a second state transition.
                logger.Error("CapCut batch worker encountered an unhandled exception: " + ex);
                if (RuntimeModes.IsolatedLabRuntime && activeTasks != null) {
                    int recovered = 0;
                    foreach (VideoTask task in activeTasks.Where(task =>
                        task.GetState() != TaskState.Waiting &&
                        task.GetState() != TaskState.Completed &&
                        task.GetState() != TaskState.Failed)) {
                        task.SetState(TaskState.Waiting);
                        task.Error = "内测后台异常已记录；任务已恢复为等待，可重新开始。";
                        task.FinishedAt = "";
                        recovered++;
                    }
                    if (recovered > 0) {
                        store.Save(activeTasks);
                        HandoffStatusWriter.WriteAll(activeTasks, false, false);
                        logger.Warn("内测后台异常后已恢复 " + recovered + " 条任务为等待状态。");
                    }
                }
            }
            finally {
                lock (sync) {
                    IsPaused = false;
                    stopRequested = false;
                    worker = null;
                    if (cancellation != null) { cancellation.Dispose(); cancellation = null; }
                }
                RaiseRunState();
                var finished = BatchFinished;
                if (finished != null) finished();
            }
        }

        private void ProcessTasks(CancellationToken token)
        {
            logger.Info("批处理开始");
            CapCutDraftMigrationReport migrationReport =
                new CapCutDraftTransactionMigrator(logger).Scan(activeTasks);
            logger.Info("CapCut事务迁移预扫描完成: " +
                migrationReport.Items.Count + " 条任务，未执行任何删除或移动");
            List<VideoTask> waiting = activeTasks
                .Where(task => task.GetState() == TaskState.Waiting ||
                    IsResumableTemporaryOutput(task) ||
                    IsDraftReleaseOnlyTask(task))
                .OrderBy(item => CategoryCatalog.ProcessingOrder(item.ProjectName))
                .ThenBy(item => item.MaterialName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.LanguageName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.VoiceTaskNumber, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Sequence)
                .ThenBy(item => item.CreatedAt)
                .ToList();
            bool stopRequested = false;
            bool sessionOpened = false;
            bool editorAvailable = false;

            if (waiting.Count == 0) {
                logger.Info("没有等待处理的任务");
                HandoffStatusWriter.WriteAll(activeTasks, false, false);
                return;
            }
            try {
                // A release-only manifest contains already-produced outputs
                // and exists solely to repair the CapCut draft lease.  It is
                // deliberately not a production-count contract.
                if (!waiting.All(IsDraftReleaseOnlyTask))
                    ValidateHandoffContracts(waiting);
            } catch (Exception contractError) {
                VideoTask blocked = waiting[0];
                blocked.SetState(TaskState.Failed);
                blocked.Error = contractError.Message;
                blocked.FinishedAt = DateTime.Now.ToString("o");
                Persist(blocked);
                HandoffStatusWriter.WriteAll(activeTasks, false, false);
                logger.Error(contractError.Message);
                return;
            }
            HandoffStatusWriter.WriteAll(activeTasks, true, false);
            StartHandoffHeartbeat();

            try {
                foreach (VideoTask task in waiting) {
                    bool draftStarted = false;
                    bool capCutSessionMayBeOwned = false;
                    bool draftRootLeaseAcquired = false;
                    ITaskScopedDraftRoot taskDraftRoot =
                        automation as ITaskScopedDraftRoot;
                    bool recycleFailure = false;
                    OutputArtifactBaseline outputBaseline = null;
                    OutputArtifactBaseline temporaryBaseline = null;
                    DateTime exportStartedUtc = DateTime.MinValue;
                    DateTime postProcessStartedUtc = DateTime.MinValue;
                    bool outputVerified = false;
                    string temporaryOutputPath = "";
                    // A new Attempt must start a new transaction generation
                    // after the prior lifecycle has restored the user's draft
                    // root.  Stable task checkpoints are intentionally kept.
                    draftTransactions.BeginOrResumeAttempt(task,
                        task.HandoffAttemptId ?? "");
                    CapCutDraftTransaction draftTransaction = draftTransactions.LoadOrCreate(task);
                    // Rehydrate the durable creation checkpoint before any UI
                    // command.  This is what makes Safe Continue resume an
                    // already-issued create command instead of clicking again.
                    if (draftTransaction != null &&
                        draftTransaction.DraftCreationCheckpoint != null &&
                        (String.Equals(draftTransaction.Phase, "create_intent_committed", StringComparison.OrdinalIgnoreCase) ||
                         String.Equals(draftTransaction.Phase, "create_command_sent", StringComparison.OrdinalIgnoreCase) ||
                         String.Equals(draftTransaction.Phase, "draft_discovered", StringComparison.OrdinalIgnoreCase) ||
                         String.Equals(draftTransaction.DraftCreationCheckpoint.CreationPhase,
                             "create_intent_committed", StringComparison.OrdinalIgnoreCase) ||
                         String.Equals(draftTransaction.DraftCreationCheckpoint.CreationPhase,
                             "create_command_sent", StringComparison.OrdinalIgnoreCase) ||
                         String.Equals(draftTransaction.DraftCreationCheckpoint.CreationPhase,
                             "draft_discovered", StringComparison.OrdinalIgnoreCase) ||
                         String.Equals(draftTransaction.DraftCreationCheckpoint.CreationPhase,
                             "editor_window_transition", StringComparison.OrdinalIgnoreCase) ||
                         String.Equals(draftTransaction.DraftCreationCheckpoint.CreationPhase,
                             "editor_window_ready", StringComparison.OrdinalIgnoreCase) ||
                         String.Equals(draftTransaction.DraftCreationCheckpoint.CreationPhase,
                             "creation_completed", StringComparison.OrdinalIgnoreCase))) {
                        task.DraftCreationCheckpoint = draftTransaction.DraftCreationCheckpoint;
                        task.DraftCreationPhase = draftTransaction.DraftCreationCheckpoint.CreationPhase;
                    }
                    try {
                        WaitIfPaused(token);
                        ValidateTask(task);
                        // A verified final video may outlive CapCut draft
                        // housekeeping.  Treat the row as a release-only
                        // recovery item: reacquire the exact lease, restore
                        // the user's original draft root, and never reopen
                        // CapCut or rerun production.
                        if (IsDraftReleaseOnlyTask(task)) {
                            bool released = ReleaseCompletedDraftOnly(task,
                                taskDraftRoot, token);
                            // Release-only work is post-production housekeeping.
                            // Do not fall through to the normal task-finally
                            // release path: if the retry itself fails, that
                            // path would see outputVerified=false and could
                            // incorrectly downgrade a quality-passed output
                            // to production failure.  It would also continue
                            // to the next CapCut task while the lease is still
                            // pending.  Keep the lease pending and stop the
                            // queue; the next Safe Continue retries this exact
                            // release-only item idempotently.
                            draftRootLeaseAcquired = false;
                            if (!released) stopRequested = true;
                            if (!released) break;
                            continue;
                        }
                        string resumableTemporaryOutputPath;
                        bool resumeExportArtifact = TryGetResumableTemporaryOutput(
                            task, out resumableTemporaryOutputPath);
                        IDirectDraftSubtitleStyling directDraftRecovery =
                            automation as IDirectDraftSubtitleStyling;
                        bool resumeDirectDraft = !resumeExportArtifact &&
                            HasDirectDraftCheckpoint(task) &&
                            directDraftRecovery != null;
                        bool resumeCaptionDraft = !resumeExportArtifact &&
                            !resumeDirectDraft && HasCaptionDraftCheckpoint(task) &&
                            directDraftRecovery != null;
                        ITimelineAssemblyCheckpointing timelineRecovery =
                            automation as ITimelineAssemblyCheckpointing;
                        // Restore an existing verified task-scoped sample before
                        // stage selection: a refreshed handoff may retain only its
                        // public library path although the draft is fully assembled.
                        ITaskSampleRecovery sampleRecovery = samplePreprocessor as ITaskSampleRecovery;
                        if (!resumeExportArtifact && !resumeDirectDraft && !resumeCaptionDraft &&
                            timelineRecovery != null && sampleRecovery != null &&
                            sampleRecovery.TryRestorePreparedSample(task, token))
                            Persist(task);
                        bool resumeTimelineAssembly = !resumeExportArtifact &&
                            !resumeDirectDraft && !resumeCaptionDraft &&
                            HasTimelineAssemblyCheckpoint(task) &&
                            timelineRecovery != null;
                        bool resumeAudioTimeline = !resumeExportArtifact &&
                            !resumeDirectDraft && !resumeCaptionDraft &&
                            !resumeTimelineAssembly &&
                            HasAudioTimelineCheckpoint(task) &&
                            timelineRecovery != null;
                        bool resumeVideoTimeline = !resumeExportArtifact &&
                            !resumeDirectDraft && !resumeCaptionDraft &&
                            !resumeTimelineAssembly && !resumeAudioTimeline &&
                            HasVideoTimelineCheckpoint(task) &&
                            timelineRecovery != null;
                        bool resumeDraftCreation = !resumeExportArtifact &&
                            !resumeDirectDraft && !resumeCaptionDraft &&
                            !resumeTimelineAssembly && !resumeAudioTimeline && !resumeVideoTimeline &&
                            task.DraftCreationCheckpoint != null &&
                            (String.Equals(task.DraftCreationPhase, "create_intent_committed", StringComparison.OrdinalIgnoreCase) ||
                             String.Equals(task.DraftCreationPhase, "create_command_sent", StringComparison.OrdinalIgnoreCase) ||
                             String.Equals(task.DraftCreationPhase, "draft_discovered", StringComparison.OrdinalIgnoreCase) ||
                             String.Equals(task.DraftCreationPhase, "editor_window_transition", StringComparison.OrdinalIgnoreCase) ||
                             String.Equals(task.DraftCreationPhase, "editor_window_ready", StringComparison.OrdinalIgnoreCase) ||
                             String.Equals(task.DraftCreationPhase, "creation_completed", StringComparison.OrdinalIgnoreCase));
                        if (!resumeExportArtifact) {
                            IDraftTaskIdentity draftIdentity =
                                automation as IDraftTaskIdentity;
                            if (draftIdentity != null)
                                draftIdentity.SetDraftTaskIdentity(task.VideoPath);
                            ICaptionPlacementTask captionPlacement =
                                automation as ICaptionPlacementTask;
                            if (captionPlacement != null)
                                captionPlacement.SetCaptionPlacement(
                                    task.CaptionPlacement,
                                    task.CaptionTargetY);
                            if (taskDraftRoot != null) {
                                RunStep(task, TaskState.AcquiringDraftRoot,
                                    "获取本条任务专属 CapCut 草稿目录",
                                    config.Timeouts.LaunchSeconds,
                                    delegate(int timeout, CancellationToken ct) {
                                        taskDraftRoot.AcquireTaskDraftRoot(task, ct);
                                    }, token, 0);
                                draftRootLeaseAcquired =
                                    taskDraftRoot.HasTaskDraftRootLease;
                                if (taskDraftRoot.CurrentDraftRootLease != null)
                                    draftTransactions.MarkLease(task,
                                        taskDraftRoot.CurrentDraftRootLease.LeaseId,
                                        task.HandoffAttemptId ?? "");
                                // A released attempt can retain a creation
                                // checkpoint whose old C: path no longer
                                // exists in the newly acquired root. Do not
                                // wait for a draft that cannot be rebound;
                                // preserve the old root in lease evidence and
                                // start one clean creation command in this
                                // generation.
                                if (resumeDraftCreation &&
                                    task.DraftCreationCheckpoint != null &&
                                    !IsCreationCheckpointPresentInActiveRoot(
                                        taskDraftRoot.CurrentDraftRootLease,
                                        task.DraftCreationCheckpoint)) {
                                    draftTransactions.ResetReleasedCreationForNewAttempt(
                                        task, task.HandoffAttemptId ?? "",
                                        "活动草稿根中没有已发现草稿，旧创建检查点仅保留在历史租约证据");
                                    task.DraftCreationCheckpoint = null;
                                    task.DraftCreationPhase = "idle";
                                    resumeDraftCreation = false;
                                }
                                // A previous attempt can have committed the
                                // CapCut create command and flushed an empty
                                // draft before the client stopped.  The lease
                                // preparation result is the filesystem fact
                                // that owns this draft.  Bind it into the
                                // durable creation checkpoint now, so the
                                // next phase resumes the same editor instead
                                // of clicking "开始创作" a second time.
                                IDraftRootPreparation preparation =
                                    taskDraftRoot as IDraftRootPreparation;
                                if (!resumeExportArtifact &&
                                    !resumeDirectDraft && !resumeCaptionDraft &&
                                    !resumeTimelineAssembly &&
                                    !resumeAudioTimeline && !resumeVideoTimeline &&
                                    !resumeDraftCreation && preparation != null &&
                                    preparation.CurrentDraftRootPreparation != null &&
                                    String.Equals(
                                        preparation.CurrentDraftRootPreparation.Mode,
                                        "empty_bootstrap",
                                        StringComparison.OrdinalIgnoreCase)) {
                                    DraftRootPreparationResult prepared =
                                        preparation.CurrentDraftRootPreparation;
                                    DraftRootLeaseRecord preparedLease =
                                        taskDraftRoot.CurrentDraftRootLease;
                                    if (preparedLease == null ||
                                        String.IsNullOrWhiteSpace(prepared.DraftId) ||
                                        String.IsNullOrWhiteSpace(prepared.ActiveDraftPath))
                                        throw new CapCutOperationFailure(
                                            "draft_creation",
                                            "CAPCUT_RECOVERY_DRAFT_BINDING_LOST",
                                            CapCutFailureClassification.HardSafety,
                                            "空白启动草稿已准备但缺少稳定的草稿身份或活动路径");
                                    task.DraftCreationCheckpoint =
                                        new DraftCreationCheckpoint {
                                            StableTaskArchiveKey = prepared.StableTaskArchiveKey ??
                                                preparedLease.StableTaskArchiveKey ?? "",
                                            LeaseId = preparedLease.LeaseId ?? "",
                                            AttemptId = task.HandoffAttemptId ?? "",
                                            BaselineIndexHash = "",
                                            BaselineDraftIds = new string[0],
                                            CreateCommandIssuedAt = "",
                                            CreateCommandId = "recovered-empty-" +
                                                Guid.NewGuid().ToString("N"),
                                            DiscoveredDraftId = prepared.DraftId,
                                            DiscoveredDraftPath = prepared.ActiveDraftPath,
                                            CreationPhase = "draft_discovered",
                                            VerifiedAt = DateTime.Now.ToString("o"),
                                            DraftRoot = preparedLease.ActiveDraftRoot ??
                                                preparedLease.TaskDraftRoot ?? ""
                                        };
                                    task.DraftCreationPhase = "draft_discovered";
                                    draftTransactions.CaptureCreation(task,
                                        task.DraftCreationCheckpoint);
                                    Persist(task);
                                    resumeDraftCreation = true;
                                    logger.Info("已从稳定归档接管空白启动草稿：" +
                                        prepared.DraftId + "；不会再次点击开始创作");
                                }
                                Persist(task);
                                if (!resumeDirectDraft && !resumeCaptionDraft &&
                                    !resumeTimelineAssembly && timelineRecovery != null) {
                                    TimelineAssemblyCheckpoint promoted;
                                    // The persisted checkpoint can lag behind
                                    // the real CapCut draft when the process
                                    // exits immediately after a media write.
                                    // Probe the highest committed stage first;
                                    // otherwise a complete (video+audio+sample)
                                    // draft is sent to the video-only verifier,
                                    // which correctly rejects its second video
                                    // segment but is wrong for recovery.
                                    if (timelineRecovery.TryCaptureExistingTimelineAssemblyCheckpoint(
                                            task, out promoted)) {
                                        task.TimelineAssemblyCheckpoint = promoted;
                                        resumeTimelineAssembly = true;
                                        resumeAudioTimeline = false;
                                        resumeVideoTimeline = false;
                                        resumeDraftCreation = false;
                                        task.DraftTransactionPhase =
                                            "timeline_assembled";
                                        draftTransactions.Mark(task,
                                            "timeline_assembled",
                                            task.HandoffAttemptId ?? "");
                                        Persist(task);
                                    } else {
                                    // A video checkpoint is only the persisted
                                    // state from the last successful write; the
                                    // draft itself may have advanced to audio
                                    // before the process exited. Always probe
                                    // the real draft for that higher stage
                                    // before invoking the video-only verifier.
                                    bool hadVideoCheckpoint = resumeVideoTimeline;
                                    // Always inspect the real draft for audio,
                                    // even when an older checkpoint already
                                    // says that this task is at the audio
                                    // stage.  A previous run can have written
                                    // the audio before its checkpoint (or a
                                    // later Attempt can have lost the in-memory
                                    // flag).  Skipping this probe and falling
                                    // through to the video verifier makes the
                                    // valid video+audio draft look like an
                                    // illegal video checkpoint.
                                    bool audioFactCaptured =
                                        timelineRecovery.TryCaptureExistingAudioTimelineCheckpoint(
                                            task, out promoted);
                                    if (audioFactCaptured) {
                                        task.TimelineAssemblyCheckpoint = promoted;
                                        resumeAudioTimeline = true;
                                        resumeVideoTimeline = false;
                                        resumeDraftCreation = false;
                                        task.DraftTransactionPhase =
                                            "audio_timeline_committed";
                                        draftTransactions.Mark(task,
                                            "audio_timeline_committed",
                                            task.HandoffAttemptId ?? "");
                                        Persist(task);
                                    } else if (!resumeAudioTimeline && !hadVideoCheckpoint &&
                                               timelineRecovery.TryCaptureExistingVideoTimelineCheckpoint(
                                            task, out promoted)) {
                                        task.TimelineAssemblyCheckpoint = promoted;
                                        resumeVideoTimeline = true;
                                        resumeDraftCreation = false;
                                        task.DraftTransactionPhase =
                                            "video_timeline_committed";
                                        draftTransactions.Mark(task,
                                            "video_timeline_committed",
                                            task.HandoffAttemptId ?? "");
                                        Persist(task);
                                    }
                                    }
                                }
                            }
                            IDraftCreationPersistence creationPersistence =
                                automation as IDraftCreationPersistence;
                            if (creationPersistence != null)
                                creationPersistence.BindDraftCreationPersistence(
                                    delegate(DraftCreationCheckpoint checkpoint) {
                                        task.DraftCreationCheckpoint = checkpoint;
                                        task.DraftCreationPhase = checkpoint == null
                                            ? task.DraftCreationPhase : checkpoint.CreationPhase;
                                        draftTransactions.CaptureCreation(task, checkpoint);
                                        Persist(task);
                                    });
                            IMediaImportPersistence importPersistence =
                                automation as IMediaImportPersistence;
                            if (importPersistence != null)
                                importPersistence.BindMediaImportPersistence(
                                    delegate(string phase, string commandId, string importPath) {
                                         task.ImportPhase = phase ?? "import_waiting";
                                         task.ImportCommandId = commandId ?? "";
                                         task.ImportPath = importPath ?? "";
                                         draftTransactions.RecordImportProgress(task,
                                             task.ImportPhase,
                                             task.HandoffAttemptId ?? "",
                                             task.ImportCommandId ?? "",
                                             task.ImportPath ?? "");
                                         Persist(task);
                                     }, task.ImportPhase ?? "import_waiting",
                                     task.ImportCommandId ?? "",
                                     task.ImportPath ?? "");
                        }
                        // A successful draft cleanup intentionally closes the
                        // CapCut session. Before the next independent draft,
                        // attach to the editor again or launch it afresh.
                        // A direct-draft checkpoint owns its own fresh CapCut
                        // launch inside ReopenAndVerifyDirectDraftSubtitleStyle.
                        // Starting the generic editor here first makes the
                        // isolated safety gate see our just-launched window as
                        // a pre-existing user editor and abort every retry.
                        if (ShouldLaunchEditorBeforeTask(
                            resumeExportArtifact, resumeDirectDraft,
                            resumeCaptionDraft, resumeTimelineAssembly,
                            resumeAudioTimeline, resumeVideoTimeline,
                            editorAvailable)) {
                            RunStep(task, TaskState.Launching, "确认或重新启动剪映并最大化", config.Timeouts.LaunchSeconds,
                                delegate(int timeout, CancellationToken ct) { automation.LaunchOrAttach(timeout, ct); }, token);
                            sessionOpened = true;
                            editorAvailable = true;
                        }
                        task.Error = "";
                        task.StartedAt = DateTime.Now.ToString("o");
                        task.FinishedAt = "";
                        Persist(task);
                        outputBaseline = OutputArtifactBaseline.Capture(task.OutputPath);
                        if (resumeExportArtifact) {
                            temporaryOutputPath = task.TemporaryOutputPath;
                            task.TemporaryOutputPath = temporaryOutputPath;
                            RunStep(task, TaskState.RecoveringExportArtifact,
                                "验证并接续本条已生成的CapCut临时成片", config.Timeouts.ExportSeconds,
                                delegate(int timeout, CancellationToken ct) {
                                    PromoteAndVerifyExistingTemporaryOutput(
                                        resumableTemporaryOutputPath, temporaryOutputPath, task, ct);
                                }, token, 0);
                            logger.Info("已跳过草稿创建和CapCut导出，直接从FFmpeg第0帧替换继续: " +
                                Path.GetFileName(temporaryOutputPath));
                        } else {
                            // Every non-artifact-resume task may launch CapCut,
                            // including the direct-draft checkpoint path that
                            // launches internally rather than through the
                            // generic LaunchOrAttach step.  Remember this
                            // independently from editorAvailable so cleanup
                            // failure cannot leave the verified editor open and
                            // block the next queue item.
                            capCutSessionMayBeOwned = true;
                            if (resumeDirectDraft) {
                                string subtitleStyle = String.IsNullOrWhiteSpace(task.SubtitleStylePreset)
                                    ? config.DefaultSubtitleStylePreset
                                    : task.SubtitleStylePreset;
                                if (!directDraftRecovery.UsesDirectDraftSubtitleStyle(subtitleStyle))
                                    throw new InvalidOperationException(
                                        "当前任务的草稿恢复检查点与字幕样式模式不一致");
                                directDraftRecovery.RestoreDirectDraftCheckpoint(
                                    task.DirectDraftCheckpoint, task.VideoPath);
                                draftStarted = true;
                                ReopenDirectDraftInFreshSessions(task,
                                    "从已验证检查点精确重开同一字幕草稿",
                                    directDraftRecovery, token);
                                logger.Info("已跳过导入、时间线、字幕识别和样式写入，仅从草稿重开断点继续");
                            } else if (resumeCaptionDraft) {
                                string subtitleStyle = String.IsNullOrWhiteSpace(task.SubtitleStylePreset)
                                    ? config.DefaultSubtitleStylePreset
                                    : task.SubtitleStylePreset;
                                if (!directDraftRecovery.UsesDirectDraftSubtitleStyle(subtitleStyle))
                                    throw new InvalidOperationException(
                                        "当前任务的字幕检查点与字幕样式模式不一致");
                                directDraftRecovery.RestoreCaptionDraftCheckpoint(
                                    task.CaptionDraftCheckpoint, task);
                                draftStarted = true;
                                RunStep(task, TaskState.ApplyingSubtitleStyle,
                                    "从字幕检查点直接写入字体、字号和预设样式",
                                    config.Timeouts.TimelineSeconds,
                                    delegate(int timeout, CancellationToken ct) {
                                        directDraftRecovery.ApplyDirectDraftSubtitleStyle(timeout, ct);
                                    }, token, 0);
                                task.DirectDraftCheckpoint =
                                    directDraftRecovery.CaptureDirectDraftCheckpoint();
                                Persist(task);
                                ReopenDirectDraftInFreshSessions(task,
                                    "重新打开同一草稿并验证字幕样式",
                                    directDraftRecovery, token);
                                logger.Info("已跳过素材导入和字幕识别，仅从字幕已保存检查点继续样式写入");
                            } else if (resumeTimelineAssembly) {
                                timelineRecovery.RestoreAndReopenTimelineAssembly(
                                    task.TimelineAssemblyCheckpoint, task,
                                    config.Timeouts.LaunchSeconds, token);
                                draftStarted = true;
                                ContinueProjectAfterTimeline(task, token);
                                logger.Info("已跳过视频、音频和样品导入，仅从已验证时间线断点继续字幕流程");
                            } else if (resumeAudioTimeline) {
                                timelineRecovery.RestoreAndReopenAudioTimeline(
                                    task.TimelineAssemblyCheckpoint, task,
                                    config.Timeouts.LaunchSeconds, token);
                                draftStarted = true;
                                ContinueProjectAfterAudioTimeline(task, token);
                                logger.Info("已跳过主视频和独立音频导入，直接从样品准备继续");
                            } else if (resumeVideoTimeline) {
                                timelineRecovery.RestoreAndReopenVideoTimeline(
                                    task.TimelineAssemblyCheckpoint, task,
                                    config.Timeouts.LaunchSeconds, token);
                                draftStarted = true;
                                ContinueProjectAfterVideoTimeline(task, token);
                                logger.Info("已跳过主视频导入、时间线拖入和静音，直接从独立音频导入继续");
                            } else if (resumeDraftCreation) {
                                IResumableDraftCreation resumableCreation =
                                    automation as IResumableDraftCreation;
                                if (resumableCreation == null)
                                    throw new CapCutOperationFailure("draft_creation",
                                        "CAPCUT_DRAFT_CREATION_RECOVERY_UNAVAILABLE",
                                        CapCutFailureClassification.HardSafety,
                                        "当前客户端不支持恢复已提交的草稿创建命令");
                                resumableCreation.RestoreDraftCreationCheckpoint(
                                    task.DraftCreationCheckpoint);
                                RunStep(task, TaskState.CreatingProject,
                                    "接管已提交的 CapCut 草稿创建事务",
                                    config.Timeouts.CreateProjectSeconds,
                                    delegate(int timeout, CancellationToken ct) {
                                        resumableCreation.ResumeDraftCreation(timeout, ct);
                                    }, token, 0);
                                draftStarted = true;
                                // Creation recovery only proves that the
                                // editor/draft exists. It is not a production
                                // checkpoint. Continue through the same
                                // stage-aware media pipeline as a fresh draft;
                                // the old branch fell through to Exporting and
                                // therefore appeared to do nothing in CapCut.
                                ContinueFromVerifiedStage(task, token);
                                logger.Info("已接管上一尝试的草稿创建检查点，未再次点击开始创作");
                            } else {
                                SyncDraftCreationState(task);
                                Persist(task);
                                RunStep(task, TaskState.CreatingProject, "创建本条独立草稿", config.Timeouts.CreateProjectSeconds,
                                    delegate(int timeout, CancellationToken ct) { automation.CreateProject(timeout, ct); }, token, 0);
                                SyncDraftCreationState(task);
                                Persist(task);
                                draftStarted = true;
                                BuildProject(task, token);
                            }
                            RunStep(task, TaskState.Exporting, "CapCut 导出本条唯一临时成片", config.Timeouts.ExportSeconds,
                                delegate(int timeout, CancellationToken ct) {
                                    // Every real click on Export owns a fresh filename.
                                    // RunStep can retry this delegate, so choosing the
                                    // name outside it reused a0001 and triggered CapCut's
                                    // native "file already exists" dialog.
                                    temporaryOutputPath = CreateUniqueCapCutTemporaryPath(task);
                                    task.TemporaryOutputPath = temporaryOutputPath;
                                    Persist(task);
                                    temporaryBaseline = OutputArtifactBaseline.Capture(temporaryOutputPath);
                                    exportStartedUtc = DateTime.UtcNow;
                                    automation.ExportVideo(temporaryOutputPath, timeout, ct);
                                }, token);
                            VerifyNewStableOutput(temporaryOutputPath, temporaryBaseline, exportStartedUtc, token);
                        }

                        postProcessStartedUtc = DateTime.UtcNow;
                        task.PostProcessWorkingPath = PostProcessPaths.WorkingOutputPath(
                            task.OutputPath, Path.GetFileNameWithoutExtension(temporaryOutputPath));
                        Persist(task);
                        RunStep(task, TaskState.PostProcessing, "FFmpeg 用原主视频第0帧替换成片第0帧", config.Timeouts.ExportSeconds,
                            delegate(int timeout, CancellationToken ct) {
                                finalVideoPostProcessor.ReplaceFirstFrame(
                                    temporaryOutputPath, task.VideoPath, task.OutputPath, timeout, ct);
                            }, token, 0);
                        VerifyNewStableOutput(task.OutputPath, outputBaseline, postProcessStartedUtc, token);
                        if (outputQualityValidator != null) {
                            OutputQualityResult quality = null;
                            RunStep(task, TaskState.VerifyingOutputQuality, "上传前成片确定性质检",
                                config.Timeouts.ExportSeconds,
                                delegate(int timeout, CancellationToken ct) {
                                    quality = outputQualityValidator.Validate(task, timeout, ct);
                                }, token, 0);
                            task.QualityStatus = quality == null ? "" : quality.Status;
                            task.QualityReportPath = quality == null ? "" : quality.ReportPath;
                            task.QualityCheckedAt = DateTime.Now.ToString("o");
                            task.QualityWarnings = quality == null
                                ? new List<string>() : new List<string>(quality.Warnings);
                            Persist(task);
                        }
                        outputVerified = true;
                        task.ProductionStatus = "completed";
                        task.DraftReleaseStatus = "pending";
                        draftTransactions.Mark(task, "release_pending",
                            task.HandoffAttemptId ?? "");
                        string draftCleanupWarning = "";
                        string draftCleanupStatus = "completed";
                        if (task.PreserveDraftAfterSuccess) {
                            logger.Info("Isolated test draft retained for visual review.");
                            draftCleanupStatus = "retained";
                            automation.CloseSession();
                        } else {
                            try {
                                // Draft cleanup is best-effort after a verified final file.  A
                                // locked CapCut draft must never hide a valid output from the
                                // upload worker or stop unrelated remaining tasks.
                                RunStep(task, TaskState.CleaningDraft, "返回首页并删除本条唯一新增草稿", config.Timeouts.CreateProjectSeconds,
                                    delegate(int timeout, CancellationToken ct) { automation.DeleteCurrentDraft(timeout, ct); }, token, 0);
                            } catch (Exception cleanupError) {
                                draftCleanupWarning = "最终成片已验证并等待上传；草稿待清理: " + cleanupError.Message;
                                draftCleanupStatus = "pending";
                                logger.Warn(draftCleanupWarning);
                            }
                        }
                        // DeleteCurrentDraft closes CapCut before attempting filesystem
                        // cleanup.  Whether that cleanup succeeds or not, the next task
                        // must launch a fresh editor session.
                        draftStarted = false;
                        editorAvailable = false;

                        logger.Info("本地成片、CapCut 临时成片和原生成视频均已保留，等待百度云文件存在且大小一致后统一清理");

                        task.SetState(TaskState.Completed);
                        task.DirectDraftCheckpoint = null;
                        task.CaptionDraftCheckpoint = null;
                        task.TimelineAssemblyCheckpoint = null;
                        // Do not place a post-success cleanup warning in Error.
                        // Dashboard/OpenClaw treat Error as a production fault.
                        task.Error = "";
                        task.FailureFingerprint = "";
                        task.DraftCleanupStatus = draftCleanupStatus;
                        task.DraftCleanupWarning = draftCleanupWarning;
                        task.DraftReleaseStatus = draftCleanupStatus == "completed"
                            ? "ready" : "pending";
                        task.DraftCleanupUpdatedAt = DateTime.Now.ToString("o");
                        task.FinishedAt = DateTime.Now.ToString("o");
                        Persist(task);
                        draftTransactions.Complete(task, "completed",
                            task.DraftReleaseStatus == "released" ||
                            task.DraftReleaseStatus == "ready"
                                ? "released" : "pending");
                        logger.Info("任务完成: " + Path.GetFileName(task.OutputPath));
                    } catch (OperationCanceledException) {
                        if (task.GetState() != TaskState.Completed) {
                            task.SetState(TaskState.Waiting);
                            // A user stop or cooperative cancellation is a
                            // recoverable lifecycle boundary, never a
                            // production failure.  Older runs left a prior
                            // Failed/productionStatus value on the row, so a
                            // later Safe Continue appeared to resume a
                            // failed task even though no new operation had
                            // failed in this attempt.
                            task.ProductionStatus = "pending";
                            task.DraftReleaseStatus = "pending";
                            if (task.DraftCleanupStatus == "" ||
                                task.DraftCleanupStatus == "completed")
                                task.DraftCleanupStatus = "release_pending";
                            task.Error = "任务已停止，可再次启动继续";
                            task.FailureFingerprint = "CAPCUT_STOPPED_RECOVERABLE";
                            task.FinishedAt = "";
                            draftTransactions.RequestStop(task,
                                task.HandoffAttemptId ?? "",
                                "recover_and_release_lease");
                            Persist(task);
                        }
                        logger.Warn("批处理已停止");
                        stopRequested = true;
                    } catch (Exception ex) {
                        SyncDraftCreationState(task);
                        recycleFailure = task.GetState() == TaskState.RecyclingSource;
                        bool postProcessFailure = task.GetState() == TaskState.PostProcessing && !outputVerified;
                        bool qualityFailure = task.GetState() == TaskState.VerifyingOutputQuality;
                        var recoverableReopen = ex as RecoverableDraftReopenException;
                        CapCutOperationFailure operationFailure = ex as CapCutOperationFailure;
                        bool recoverableWindowTransition = IsRecoverableWindowTransition(
                            operationFailure);
                        if (qualityFailure) {
                            task.QualityStatus = "blocked";
                            task.QualityCheckedAt = DateTime.Now.ToString("o");
                            task.QualityReportPath = String.IsNullOrWhiteSpace(task.OutputPath)
                                ? "" : task.OutputPath + ".quality.json";
                        }
                        task.SetState(recoverableReopen == null && !recoverableWindowTransition
                            ? TaskState.Failed : TaskState.Waiting);
                        task.FailureFingerprint = recoverableReopen == null && !recoverableWindowTransition
                            ? (operationFailure == null
                                ? KnownCapCutFailures.Match(ex.Message)
                                : operationFailure.ErrorCode)
                            : (operationFailure == null
                                ? recoverableReopen.ErrorCode
                                : operationFailure.ErrorCode);
                        task.Error = recycleFailure
                            ? "成片与草稿清理已完成，但原视频未移入已完成源视频目录: " + ex.Message
                            : ex.Message;
                        task.FinishedAt = recoverableReopen == null && !recoverableWindowTransition
                            ? DateTime.Now.ToString("o") : "";
                        if (recoverableWindowTransition) {
                            task.ProductionStatus = "pending";
                            task.DraftReleaseStatus = "pending";
                            task.Error = ex.Message;
                            task.FailureFingerprint = operationFailure.ErrorCode;
                            task.FinishedAt = "";
                        }
                        Persist(task);
                        if (recoverableReopen == null && !recoverableWindowTransition) {
                            logger.Error("任务失败: " + Path.GetFileName(task.VideoPath) + " / " + task.Error);
                        } else {
                            logger.Warn("任务已进入可恢复等待，不会新建草稿或增加导出尝试号: " +
                                Path.GetFileName(task.VideoPath) + " / " +
                                (operationFailure == null ? recoverableReopen.ErrorCode : operationFailure.ErrorCode));
                            stopRequested = true;
                        }
                        if (postProcessFailure) {
                            stopRequested = true;
                            logger.Error("FFmpeg 首帧后处理未完整通过，已保留临时成片、草稿和源视频并停止后续任务");
                        }
                        if (qualityFailure) {
                            stopRequested = true;
                            logger.Error("上传前成片质检未通过；已阻止上传并保留源视频、草稿、临时成片和最终成片");
                        }
                        if (task.DeleteSourceAfterSuccess) {
                            stopRequested = true;
                            logger.Error("需要回收源视频的任务未完整满足安全前置条件，后续任务已暂停，必须人工确认后重新启动");
                        }
                    } finally {
                        if (draftStarted) {
                            logger.Warn("任务未完整成功，已保留当前草稿、临时成片和源视频，未执行任何清理");
                            stopRequested = true;
                        }
                    }
                    // Every queue item is isolated in a new CapCut process.  In
                    // particular, an exception before draft cleanup must not
                    // leave a background-only process for the following item.
                    if (capCutSessionMayBeOwned || editorAvailable || sessionOpened) {
                        try {
                            automation.CloseSession();
                        } catch (Exception closeError) {
                            logger.Warn("Failed to close the CapCut session after this task: " + closeError.Message);
                        } finally {
                            editorAvailable = false;
                            sessionOpened = false;
                        }
                    }
                    if (!draftRootLeaseAcquired && taskDraftRoot != null)
                        draftRootLeaseAcquired =
                            taskDraftRoot.HasTaskDraftRootLease;
                    if (draftRootLeaseAcquired && taskDraftRoot != null) {
                        TaskState stateBeforeRestore = task.GetState();
                        try {
                            task.SetState(TaskState.RestoringDraftRoot);
                            Persist(task);
                            taskDraftRoot.ReleaseTaskDraftRoot(
                                CancellationToken.None);
                            task.DraftReleaseStatus = "released";
                            if (task.DraftCleanupStatus == "release_pending")
                                task.DraftCleanupStatus = "completed";
                            task.DraftCleanupWarning = "";
                            task.DraftCleanupUpdatedAt = DateTime.Now.ToString("o");
                            task.SetState(stateBeforeRestore);
                            Persist(task);
                            draftTransactions.Mark(task, "original_draft_restored",
                                task.HandoffAttemptId ?? "");
                            if (stateBeforeRestore == TaskState.Completed &&
                                String.Equals(task.DraftReleaseStatus ?? "", "released",
                                    StringComparison.OrdinalIgnoreCase)) {
                                draftTransactions.Complete(task, "completed", "released");
                            }
                            draftRootLeaseAcquired = false;
                        } catch (Exception restoreError) {
                            bool verifiedProduction = stateBeforeRestore == TaskState.Completed &&
                                outputVerified && String.Equals(task.QualityStatus ?? "",
                                    "passed", StringComparison.OrdinalIgnoreCase) &&
                                File.Exists(task.OutputPath);
                            if (verifiedProduction) {
                                // Draft release is post-production housekeeping.  Do
                                // not turn a verified final video into a production
                                // failure; pause the queue until the lease can be
                                // released safely on the next continue.
                                task.SetState(TaskState.Completed);
                                task.ProductionStatus = "completed";
                                task.DraftReleaseStatus = "pending";
                                task.DraftCleanupStatus = "release_pending";
                                task.DraftCleanupWarning =
                                    "成片已完成并通过质检，草稿恢复待处理: " + restoreError.Message;
                                task.DraftCleanupUpdatedAt = DateTime.Now.ToString("o");
                                task.Error = "";
                                task.FailureFingerprint = "CAPCUT_DRAFT_RELEASE_PENDING";
                                draftTransactions.Mark(task, "release_pending",
                                    task.HandoffAttemptId ?? "", "",
                                    CapCutFailureClassification.MaintenancePending,
                                    "CAPCUT_DRAFT_RELEASE_PENDING", restoreError.Message);
                                if (String.IsNullOrWhiteSpace(task.FinishedAt))
                                    task.FinishedAt = DateTime.Now.ToString("o");
                                Persist(task);
                                logger.Warn("成片已完成，草稿目录恢复待处理，已暂停下一条: " +
                                    restoreError.Message);
                            } else {
                                // Keep the first production error as the task's
                                // primary fact.  Draft-root cleanup is a
                                // separate maintenance operation; allowing its
                                // marker/path error to overwrite the original
                                // failure makes every later retry look like a
                                // new identity problem and hides the real cause.
                                string primaryTaskError = task.Error ?? "";
                                string primaryFailureFingerprint =
                                    task.FailureFingerprint ?? "";
                                bool stopWasRequested = IsStopping || stopRequested;
                                // A user stop/close is not a production failure.
                                // If the release probe itself cannot finish
                                // before shutdown, keep the task recoverable and
                                // let the next Safe Continue resume this exact
                                // lease transaction.  Only an autonomous task
                                // failure is allowed to become Failed here.
                                task.SetState(stopWasRequested
                                    ? TaskState.Waiting : TaskState.Failed);
                                task.ProductionStatus = stopWasRequested
                                    ? "pending" : "failed";
                                task.DraftReleaseStatus = stopWasRequested
                                    ? "pending" : "failed";
                                task.DraftCleanupStatus = stopWasRequested
                                    ? "release_pending" : "release_failed";
                                task.DraftCleanupWarning =
                                    "草稿目录恢复失败（附属维护错误）: " +
                                        restoreError.Message;
                                task.DraftCleanupUpdatedAt = DateTime.Now.ToString("o");
                                task.Error = stopWasRequested
                                    ? "已请求停止；草稿目录恢复待处理: " + restoreError.Message
                                    : (String.IsNullOrWhiteSpace(primaryTaskError)
                                        ? "draft_root_restore_failed: " + restoreError.Message
                                        : primaryTaskError);
                                task.FailureFingerprint = stopWasRequested
                                    ? "CAPCUT_DRAFT_RELEASE_PENDING"
                                    : (String.IsNullOrWhiteSpace(primaryFailureFingerprint)
                                        ? "CAPCUT_DRAFT_ROOT_RESTORE_FAILED"
                                        : primaryFailureFingerprint);
                                task.FinishedAt = stopWasRequested
                                    ? "" : DateTime.Now.ToString("o");
                                Persist(task);
                                if (stopWasRequested) {
                                    logger.Warn(
                                        "已请求停止，草稿目录恢复待处理；任务保持可恢复等待，" +
                                        "下次安全继续将从当前租约事务接管: " + restoreError.Message);
                                } else {
                                    logger.Error(
                                        "恢复原 CapCut 草稿目录失败，已阻止下一任务；" +
                                        "首个任务错误保持为: " + task.Error +
                                        "; 草稿维护错误: " + restoreError.Message);
                                }
                            }
                            stopRequested = true;
                        }
                    }
                    if (stopRequested) break;
                }
            } catch (OperationCanceledException) {
                logger.Warn("批处理已停止");
            } catch (Exception ex) {
                VideoTask first = waiting[0];
                first.SetState(TaskState.Failed);
                first.Error = ex.Message;
                first.FinishedAt = DateTime.Now.ToString("o");
                Persist(first);
                logger.Error("无法启动类目批处理: " + ex.Message);
            } finally {
                StopHandoffHeartbeat();
                try {
                    if (sessionOpened) automation.CloseSession();
                } catch (Exception ex) {
                    logger.Warn("类目结束时关闭 CapCut 失败: " + ex.Message);
                }
            }
            store.Save(activeTasks);
            HandoffStatusWriter.WriteAll(activeTasks, false, false);
            logger.Info("批处理结束");
        }

        internal static bool IsDraftReleaseOnlyTask(VideoTask task)
        {
            if (task == null || task.GetState() != TaskState.Completed)
                return false;
            if (!String.Equals(task.ProductionStatus ?? "", "completed",
                    StringComparison.OrdinalIgnoreCase)) return false;
            string release = task.DraftReleaseStatus ?? "";
            string cleanup = task.DraftCleanupStatus ?? "";
            if (!(String.Equals(release, "pending", StringComparison.OrdinalIgnoreCase) ||
                  String.Equals(release, "release_pending", StringComparison.OrdinalIgnoreCase) ||
                  String.Equals(cleanup, "release_pending", StringComparison.OrdinalIgnoreCase)))
                return false;
            return !String.IsNullOrWhiteSpace(task.OutputPath) &&
                File.Exists(task.OutputPath) &&
                String.Equals(task.QualityStatus ?? "", "passed",
                    StringComparison.OrdinalIgnoreCase);
        }

        private bool ReleaseCompletedDraftOnly(VideoTask task,
            ITaskScopedDraftRoot taskDraftRoot, CancellationToken token)
        {
            if (taskDraftRoot == null) {
                task.DraftReleaseStatus = "pending";
                task.DraftCleanupStatus = "release_pending";
                task.DraftCleanupWarning = "成片已完成并通过质检，但草稿租约组件不可用";
                task.FailureFingerprint = "CAPCUT_DRAFT_RELEASE_PENDING";
                task.Error = "";
                Persist(task);
                return false;
            }
            try {
                task.SetState(TaskState.AcquiringDraftRoot);
                Persist(task);
                taskDraftRoot.AcquireTaskDraftRoot(task, token);
                task.SetState(TaskState.RestoringDraftRoot);
                Persist(task);
                taskDraftRoot.ReleaseTaskDraftRoot(CancellationToken.None);
                task.SetState(TaskState.Completed);
                task.ProductionStatus = "completed";
                task.DraftReleaseStatus = "released";
                task.DraftCleanupStatus = "completed";
                task.DraftCleanupWarning = "";
                task.DraftCleanupUpdatedAt = DateTime.Now.ToString("o");
                task.Error = "";
                task.FailureFingerprint = "";
                task.FinishedAt = String.IsNullOrWhiteSpace(task.FinishedAt)
                    ? DateTime.Now.ToString("o") : task.FinishedAt;
                Persist(task);
                draftTransactions.Complete(task, "completed", "released");
                logger.Info("已恢复已完成成片对应的 CapCut 草稿目录：" +
                    Path.GetFileName(task.OutputPath));
                return true;
            } catch (Exception ex) {
                // Release-only failures are housekeeping failures.  Preserve
                // the verified production fact and leave the lease pending for
                // the next idempotent continue operation.
                task.SetState(TaskState.Completed);
                task.ProductionStatus = "completed";
                task.DraftReleaseStatus = "pending";
                task.DraftCleanupStatus = "release_pending";
                task.DraftCleanupWarning = "成片已完成并通过质检，草稿恢复待处理: " + ex.Message;
                task.DraftCleanupUpdatedAt = DateTime.Now.ToString("o");
                task.Error = "";
                task.FailureFingerprint = "CAPCUT_DRAFT_RELEASE_PENDING";
                task.FinishedAt = String.IsNullOrWhiteSpace(task.FinishedAt)
                    ? DateTime.Now.ToString("o") : task.FinishedAt;
                Persist(task);
                logger.Warn("已完成成片的草稿目录仍待恢复：" + ex.Message);
                return false;
            }
        }

        // Keep an active handoff observable during longer exports and FFmpeg
        // verification. A missing heartbeat is then a genuine recovery signal,
        // not merely an idle UI step.
        private void StartHandoffHeartbeat()
        {
            StopHandoffHeartbeat();
            handoffHeartbeat = new Timer(delegate {
                try {
                    if (activeTasks != null && IsRunning)
                        HandoffStatusWriter.WriteAll(activeTasks, true, IsPaused);
                } catch {
                    // Status transport is best effort; do not interrupt export.
                }
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        private void StopHandoffHeartbeat()
        {
            Timer timer = Interlocked.Exchange(ref handoffHeartbeat, null);
            if (timer != null) timer.Dispose();
        }

        private void ValidateHandoffContracts(IList<VideoTask> waiting)
        {
            foreach (IGrouping<string, VideoTask> waitingGroup in waiting
                .Where(task => task.IsHandoffTask)
                .GroupBy(task => task.HandoffBatchId ?? "", StringComparer.OrdinalIgnoreCase)) {
                string batchId = waitingGroup.Key;
                List<VideoTask> batchTasks = activeTasks
                    .Where(task => task.IsHandoffTask &&
                        String.Equals(task.HandoffBatchId ?? "", batchId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                HashSet<int> expectedValues = new HashSet<int>(batchTasks
                    .Select(task => task.ProjectExpectedCount > 0 ? task.ProjectExpectedCount : task.ExpectedTaskCount)
                    .Where(value => value > 0));
                if (expectedValues.Count != 1) {
                    throw new InvalidOperationException("工作台交接清单缺少唯一的材料任务目标数，已阻止启动 CapCut");
                }
                int expected = expectedValues.First();
                int actual = batchTasks
                    .Where(task => !String.IsNullOrWhiteSpace(task.Id))
                    .Select(task => task.Id)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                bool hasPartialRetryContract = batchTasks.Any(task =>
                    (task.ProjectTaskIds != null && task.ProjectTaskIds.Count > 0) ||
                    (task.CompletedTaskIds != null && task.CompletedTaskIds.Count > 0));
                if (hasPartialRetryContract) {
                    List<string> released = TaskIdsReleasedFromAttempt(batchTasks);
                    if (released.Count > 0)
                        logger.Info("本轮补跑中已完成、正在单独清理草稿的任务仍计入本轮：" +
                            String.Join(", ", released.ToArray()));
                    ValidatePartialRetryContract(batchTasks, expected, actual, released);
                } else if (actual != expected) {
                    throw new InvalidOperationException(String.Format(
                        "工作台交接清单数量不完整：材料任务计划 {0} 条，当前唯一任务 {1} 条；已阻止启动 CapCut",
                        expected, actual));
                }
                HashSet<string> owners = new HashSet<string>(batchTasks
                    .Select(task => (task.RemotePipelineTaskId ?? "").Trim())
                    .Where(value => value.Length > 0), StringComparer.OrdinalIgnoreCase);
                if (owners.Count != 1) {
                    throw new InvalidOperationException("工作台交接清单没有唯一的远程材料任务身份，已阻止启动 CapCut");
                }
            }
        }

        // A task that finished inside this attempt and was then re-imported by
        // a release-only manifest (only its draft is left to delete) moves to
        // that manifest's batch, but it still belongs to the declared attempt.
        // On 10-19 the SPC German task did this and every later start of its
        // Italian/English siblings was refused as "declared 3, found 2".
        private List<string> TaskIdsReleasedFromAttempt(IList<VideoTask> batchTasks)
        {
            VideoTask first = batchTasks.FirstOrDefault();
            string owner = first == null ? "" : (first.RemotePipelineTaskId ?? "").Trim();
            if (owner.Length == 0) return new List<string>();
            HashSet<string> project = SetOf(first.ProjectTaskIds);
            HashSet<string> completed = SetOf(first.CompletedTaskIds);
            HashSet<string> present = SetOf(batchTasks.Select(task => task.Id));
            return activeTasks
                .Where(task => task.IsHandoffTask && task.ReleaseOnly &&
                    String.Equals(task.ProductionStatus ?? "", "completed", StringComparison.OrdinalIgnoreCase) &&
                    String.Equals((task.RemotePipelineTaskId ?? "").Trim(), owner, StringComparison.OrdinalIgnoreCase) &&
                    !String.IsNullOrWhiteSpace(task.Id))
                .Select(task => task.Id.Trim())
                .Where(id => project.Contains(id) && !completed.Contains(id) && !present.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void ValidatePartialRetryContract(IList<VideoTask> batchTasks, int expected, int actual,
            IList<string> releasedFromAttempt)
        {
            actual += releasedFromAttempt.Count;
            HashSet<int> attemptCounts = new HashSet<int>(batchTasks
                .Select(task => task.AttemptTaskCount)
                .Where(value => value > 0));
            if (attemptCounts.Count != 1 || attemptCounts.First() != actual) {
                throw new InvalidOperationException(String.Format(
                    "工作台补跑清单数量不一致：声明补跑 {0} 条，当前唯一任务 {1} 条；已阻止启动 CapCut",
                    attemptCounts.Count == 1 ? attemptCounts.First() : 0, actual));
            }

            List<string> projectIds = batchTasks[0].ProjectTaskIds ?? new List<string>();
            List<string> completedIds = batchTasks[0].CompletedTaskIds ?? new List<string>();
            string projectContract = ContractKey(projectIds);
            string completedContract = ContractKey(completedIds);
            if (batchTasks.Any(task => ContractKey(task.ProjectTaskIds) != projectContract ||
                                      ContractKey(task.CompletedTaskIds) != completedContract)) {
                throw new InvalidOperationException("工作台补跑清单的正式任务身份不一致，已阻止启动 CapCut");
            }

            HashSet<string> project = SetOf(projectIds);
            HashSet<string> completed = SetOf(completedIds);
            HashSet<string> attempt = SetOf(batchTasks.Select(task => task.Id).Concat(releasedFromAttempt));
            if (project.Count != expected || projectIds.Count != project.Count) {
                throw new InvalidOperationException(String.Format(
                    "工作台补跑清单缺少完整正式任务 ID：材料任务计划 {0} 条，正式任务 ID {1} 条；已阻止启动 CapCut",
                    expected, project.Count));
            }
            if (!completed.IsSubsetOf(project) || !attempt.IsSubsetOf(project)) {
                throw new InvalidOperationException("工作台补跑清单包含不属于当前正式项目的任务 ID，已阻止启动 CapCut");
            }
            if (completed.Overlaps(attempt)) {
                throw new InvalidOperationException("工作台补跑清单将已完成任务再次加入 CapCut，已阻止启动以避免重复剪辑");
            }
            completed.UnionWith(attempt);
            if (!completed.SetEquals(project)) {
                throw new InvalidOperationException(String.Format(
                    "工作台补跑清单覆盖不完整：正式目标 {0} 条，已完成 {1} 条，本次补跑 {2} 条；已阻止启动 CapCut",
                    expected, completedIds.Count, actual));
            }
        }

        private static HashSet<string> SetOf(IEnumerable<string> values)
        {
            return new HashSet<string>((values ?? new string[0])
                .Where(value => !String.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()), StringComparer.OrdinalIgnoreCase);
        }

        private static string ContractKey(IEnumerable<string> values)
        {
            return String.Join("|", SetOf(values).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        private void BuildProject(VideoTask task, CancellationToken token)
        {
            RunStep(task, TaskState.Importing, "导入第一条主视频", config.Timeouts.ImportSeconds,
                delegate(int timeout, CancellationToken ct) { automation.ImportVideo(task.VideoPath, timeout, ct); }, token, 0);
            RunStep(task, TaskState.PlacingOnTimeline, "主视频从零点加入时间线", config.Timeouts.TimelineSeconds,
                delegate(int timeout, CancellationToken ct) { automation.PlaceOnTimeline(timeout, ct); }, token, 0);
            RunStep(task, TaskState.MutingOriginal, "点击视频轨道静音按钮", config.Timeouts.TimelineSeconds,
                delegate(int timeout, CancellationToken ct) { automation.MuteOriginalVideo(timeout, ct); }, token, 0);

            ITimelineAssemblyCheckpointing videoCheckpointing =
                automation as ITimelineAssemblyCheckpointing;
            if (videoCheckpointing != null) {
                task.TimelineAssemblyCheckpoint =
                    videoCheckpointing.CaptureVideoTimelineCheckpoint(task);
                task.DraftTransactionPhase = "video_timeline_committed";
                draftTransactions.Mark(task, "video_timeline_committed",
                    task.HandoffAttemptId ?? "");
                Persist(task);
                logger.Info("已保存主视频时间线断点；异常退出后将直接从独立音频导入继续");
            }

            ContinueProjectAfterVideoTimeline(task, token);
        }

        private static bool IsCreationCheckpointPresentInActiveRoot(
            DraftRootLeaseRecord lease, DraftCreationCheckpoint checkpoint)
        {
            if (lease == null || checkpoint == null ||
                String.IsNullOrWhiteSpace(lease.ActiveDraftRoot) ||
                String.IsNullOrWhiteSpace(checkpoint.DiscoveredDraftId))
                return false;
            try {
                string index = Path.Combine(lease.ActiveDraftRoot,
                    "root_meta_info.json");
                if (!File.Exists(index)) return false;
                string text = File.ReadAllText(index);
                return text.IndexOf(checkpoint.DiscoveredDraftId,
                    StringComparison.OrdinalIgnoreCase) >= 0;
            } catch { return false; }
        }

        private void ContinueFromVerifiedStage(VideoTask task,
            CancellationToken token)
        {
            if (HasTimelineAssemblyCheckpoint(task)) {
                ContinueProjectAfterTimeline(task, token);
                return;
            }
            if (HasAudioTimelineCheckpoint(task)) {
                ContinueProjectAfterAudioTimeline(task, token);
                return;
            }
            if (HasVideoTimelineCheckpoint(task)) {
                ContinueProjectAfterVideoTimeline(task, token);
                return;
            }
            BuildProject(task, token);
        }

        private void ContinueProjectAfterVideoTimeline(VideoTask task,
            CancellationToken token)
        {

            if (PlaceAudioInFreshSessions(task, token)) {
                ContinueProjectAfterAudioTimeline(task, token);
                return;
            }

            ITimelineAssemblyCheckpointing audioCheckpointing =
                automation as ITimelineAssemblyCheckpointing;
            if (audioCheckpointing != null) {
                task.TimelineAssemblyCheckpoint =
                    audioCheckpointing.CaptureAudioTimelineCheckpoint(task);
                task.DraftTransactionPhase = "audio_timeline_committed";
                draftTransactions.Mark(task, "audio_timeline_committed",
                    task.HandoffAttemptId ?? "");
                Persist(task);
                logger.Info("已保存独立音频时间线断点；异常退出后将直接从样品准备继续");
            }

            ContinueProjectAfterAudioTimeline(task, token);
        }

        private void ContinueProjectAfterAudioTimeline(VideoTask task,
            CancellationToken token)
        {

            if (!String.IsNullOrWhiteSpace(task.SamplePath)) {
                RunStep(task, TaskState.PreparingSample, "Prepare task-scoped sample with FFmpeg", config.Timeouts.ExportSeconds,
                    delegate(int timeout, CancellationToken ct) { samplePreprocessor.Prepare(task, timeout, ct); }, token, 0);
                Persist(task);
            }

            if (!String.IsNullOrWhiteSpace(task.SamplePath)) {
                RunStep(task, TaskState.ImportingSample, "导入竖屏结尾样品", config.Timeouts.ImportSeconds,
                    delegate(int timeout, CancellationToken ct) { automation.ImportSample(task.SamplePath, timeout, ct); }, token, 0);
                RunStep(task, TaskState.PlacingSample, "完整样品接到主视频末尾", config.Timeouts.TimelineSeconds,
                    delegate(int timeout, CancellationToken ct) { automation.PlaceSampleAtEnd(timeout, ct); }, token, 0);
                RunStep(task, TaskState.VerifyingSampleDuration, "验证预处理样品与独立音频末端对齐", config.Timeouts.TimelineSeconds,
                    delegate(int timeout, CancellationToken ct) { automation.SetSampleSpeed(task.SampleSpeed, timeout, ct); }, token, 0);
                ITimelineAssemblyCheckpointing checkpointing =
                    automation as ITimelineAssemblyCheckpointing;
                if (checkpointing != null) {
                    task.TimelineAssemblyCheckpoint =
                        checkpointing.CaptureTimelineAssemblyCheckpoint(task);
                    Persist(task);
                    logger.Info("已保存时间线组装检查点；后续失败不会重新导入视频、音频或样品");
                }
            } else {
                logger.Info("独立音频不长于主视频，本任务明确跳过结尾样品导入");
            }

            ContinueProjectAfterTimeline(task, token);
        }

        private void ContinueProjectAfterTimeline(VideoTask task, CancellationToken token)
        {
            RunStep(task, TaskState.RecognizingCaptions, "识别项目语言字幕", config.Timeouts.CaptionSeconds,
                delegate(int timeout, CancellationToken ct) { automation.RunAutoCaption(timeout, ct); }, token, 0);
            string subtitleStyle = String.IsNullOrWhiteSpace(task.SubtitleStylePreset)
                ? config.DefaultSubtitleStylePreset
                : task.SubtitleStylePreset;
            IDirectDraftSubtitleStyling directDraftStyle = automation as IDirectDraftSubtitleStyling;
            if (directDraftStyle != null && directDraftStyle.UsesDirectDraftSubtitleStyle(subtitleStyle)) {
                RunStep(task, TaskState.SavingDraft, "保存并锁定本条唯一字幕草稿", config.Timeouts.TimelineSeconds,
                    delegate(int timeout, CancellationToken ct) { directDraftStyle.SaveDraftForDirectSubtitleStyle(timeout, ct); }, token, 0);
                task.CaptionDraftCheckpoint =
                    directDraftStyle.CaptureCaptionDraftCheckpoint(task);
                Persist(task);
                logger.Info("已保存字幕检查点；样式写入失败时不会重新导入素材或识别字幕");
                RunStep(task, TaskState.ApplyingSubtitleStyle, "直接写入字幕字体、字号和预设样式", config.Timeouts.TimelineSeconds,
                    delegate(int timeout, CancellationToken ct) { directDraftStyle.ApplyDirectDraftSubtitleStyle(timeout, ct); }, token, 0);
                task.DirectDraftCheckpoint = directDraftStyle.CaptureDirectDraftCheckpoint();
                Persist(task);
                logger.Info("已保存字幕草稿恢复检查点；重开失败时不会重新识别字幕或新建草稿");
                ReopenDirectDraftInFreshSessions(task, "重新打开同一草稿并验证字幕样式",
                    directDraftStyle, token);
            } else {
                RunStep(task, TaskState.ApplyingSubtitleStyle, "应用项目字幕样式", config.Timeouts.TimelineSeconds,
                    delegate(int timeout, CancellationToken ct) { automation.ApplySubtitleStyle(subtitleStyle, timeout, ct); }, token);
            }
            logger.Info("首帧处理已改为导出后 FFmpeg 第0帧替换；正式流程不再依赖首帧字幕坐标");
        }

        private static bool HasTimelineAssemblyCheckpoint(VideoTask task)
        {
            TimelineAssemblyCheckpoint checkpoint = task == null
                ? null : task.TimelineAssemblyCheckpoint;
            return checkpoint != null &&
                String.Equals(checkpoint.State, "timeline_assembled",
                    StringComparison.OrdinalIgnoreCase) &&
                !String.IsNullOrWhiteSpace(checkpoint.DraftDirectory) &&
                !String.IsNullOrWhiteSpace(checkpoint.DraftId) &&
                !String.IsNullOrWhiteSpace(checkpoint.MainVideoPath) &&
                !String.IsNullOrWhiteSpace(checkpoint.AudioPath) &&
                !String.IsNullOrWhiteSpace(checkpoint.SamplePath) &&
                !String.IsNullOrWhiteSpace(checkpoint.SemanticHash);
        }

        private static bool HasAudioTimelineCheckpoint(VideoTask task)
        {
            TimelineAssemblyCheckpoint checkpoint = task == null
                ? null : task.TimelineAssemblyCheckpoint;
            return checkpoint != null &&
                String.Equals(checkpoint.State, "audio_timeline_committed",
                    StringComparison.OrdinalIgnoreCase) &&
                !String.IsNullOrWhiteSpace(checkpoint.DraftDirectory) &&
                !String.IsNullOrWhiteSpace(checkpoint.DraftId) &&
                !String.IsNullOrWhiteSpace(checkpoint.MainVideoPath) &&
                !String.IsNullOrWhiteSpace(checkpoint.AudioPath) &&
                !String.IsNullOrWhiteSpace(checkpoint.SemanticHash) &&
                checkpoint.VideoTrackCount == 1 &&
                checkpoint.AudioTrackCount == 1 &&
                checkpoint.MuteVerified;
        }

        private static bool HasVideoTimelineCheckpoint(VideoTask task)
        {
            TimelineAssemblyCheckpoint checkpoint = task == null
                ? null : task.TimelineAssemblyCheckpoint;
            return checkpoint != null &&
                String.Equals(checkpoint.State, "video_timeline_committed",
                    StringComparison.OrdinalIgnoreCase) &&
                !String.IsNullOrWhiteSpace(checkpoint.DraftId) &&
                !String.IsNullOrWhiteSpace(checkpoint.MainVideoPath) &&
                !String.IsNullOrWhiteSpace(checkpoint.AudioPath) &&
                !String.IsNullOrWhiteSpace(checkpoint.SemanticHash) &&
                checkpoint.VideoTrackCount == 1 &&
                checkpoint.AudioTrackCount == 0 &&
                checkpoint.MuteVerified;
        }

        private static bool HasDirectDraftCheckpoint(VideoTask task)
        {
            DirectDraftCheckpoint checkpoint = task == null ? null : task.DirectDraftCheckpoint;
            return checkpoint != null &&
                String.Equals(checkpoint.State, "style_applied", StringComparison.OrdinalIgnoreCase) &&
                !String.IsNullOrWhiteSpace(checkpoint.DraftDirectory) &&
                !String.IsNullOrWhiteSpace(checkpoint.DraftId) &&
                !String.IsNullOrWhiteSpace(checkpoint.DraftName) &&
                !String.IsNullOrWhiteSpace(checkpoint.SourceVideoPath) &&
                checkpoint.PatchedFiles != null &&
                checkpoint.PatchedFiles.Length > 0;
        }

        private static bool HasCaptionDraftCheckpoint(VideoTask task)
        {
            CaptionDraftCheckpoint checkpoint = task == null
                ? null : task.CaptionDraftCheckpoint;
            return checkpoint != null &&
                String.Equals(checkpoint.State, "captions_saved",
                    StringComparison.OrdinalIgnoreCase) &&
                !String.IsNullOrWhiteSpace(checkpoint.DraftDirectory) &&
                !String.IsNullOrWhiteSpace(checkpoint.DraftId) &&
                !String.IsNullOrWhiteSpace(checkpoint.DraftName) &&
                !String.IsNullOrWhiteSpace(checkpoint.SourceVideoPath) &&
                checkpoint.CaptionCount > 0 &&
                !String.IsNullOrWhiteSpace(checkpoint.CaptionIdentityHash) &&
                !String.IsNullOrWhiteSpace(checkpoint.TimingAndTrackHash) &&
                !String.IsNullOrWhiteSpace(checkpoint.ProtectedMediaHash) &&
                checkpoint.VerifiedFiles != null &&
                checkpoint.VerifiedFiles.Length > 0;
        }

        internal static bool ShouldLaunchEditorBeforeTask(
            bool resumeExportArtifact,
            bool resumeDirectDraft,
            bool editorAvailable)
        {
            return ShouldLaunchEditorBeforeTask(resumeExportArtifact,
                resumeDirectDraft, false, false, false, false, editorAvailable);
        }

        internal static bool ShouldLaunchEditorBeforeTask(
            bool resumeExportArtifact,
            bool resumeDirectDraft,
            bool resumeTimelineAssembly,
            bool editorAvailable)
        {
            return ShouldLaunchEditorBeforeTask(resumeExportArtifact,
                resumeDirectDraft, false, resumeTimelineAssembly, false, false,
                editorAvailable);
        }

        internal static bool ShouldLaunchEditorBeforeTask(
            bool resumeExportArtifact,
            bool resumeDirectDraft,
            bool resumeCaptionDraft,
            bool resumeTimelineAssembly,
            bool editorAvailable)
        {
            return ShouldLaunchEditorBeforeTask(resumeExportArtifact,
                resumeDirectDraft, resumeCaptionDraft,
                resumeTimelineAssembly, false, false, editorAvailable);
        }

        internal static bool ShouldLaunchEditorBeforeTask(
            bool resumeExportArtifact,
            bool resumeDirectDraft,
            bool resumeCaptionDraft,
            bool resumeTimelineAssembly,
            bool resumeAudioTimeline,
            bool resumeVideoTimeline,
            bool editorAvailable)
        {
            return !resumeExportArtifact && !resumeDirectDraft &&
                !resumeCaptionDraft && !resumeTimelineAssembly &&
                !resumeAudioTimeline && !resumeVideoTimeline &&
                !editorAvailable;
        }

        private void RunStep(VideoTask task, TaskState state, string stepName, int timeoutSeconds, Action<int, CancellationToken> action, CancellationToken token, int? retryCountOverride = null)
        {
            WaitIfPaused(token);
            task.SetState(state);
            ITaskScopedDraftRoot scopedDraftRoot = automation as ITaskScopedDraftRoot;
            if (scopedDraftRoot != null && scopedDraftRoot.HasTaskDraftRootLease)
                scopedDraftRoot.UpdateTaskDraftRootStage(state.ToString());
            Persist(task);
            draftTransactions.Mark(task, TransactionPhaseFor(state) + ":intent",
                task.HandoffAttemptId ?? "");

            Exception last = null;
            int retryCount = retryCountOverride ?? config.RetryCount;
            for (int attempt = 0; attempt <= retryCount; attempt++) {
                token.ThrowIfCancellationRequested();
                Stopwatch watch = Stopwatch.StartNew();
                try {
                    logger.Info(String.Format("{0}: {1}（第 {2} 次）", Path.GetFileName(task.VideoPath), stepName, attempt + 1));
                    action(timeoutSeconds, token);
                    draftTransactions.Mark(task, TransactionPhaseFor(state) + ":committed",
                        task.HandoffAttemptId ?? "");
                    if (scopedDraftRoot != null && scopedDraftRoot.HasTaskDraftRootLease)
                        scopedDraftRoot.UpdateTaskDraftRootStage(state + ":completed");
                    logger.Info(String.Format("步骤完成: {0}，耗时 {1:0.0}s", stepName, watch.Elapsed.TotalSeconds));
                    return;
                } catch (OperationCanceledException) { throw; }
                catch (Exception ex) {
                    CapCutOperationFailure typedFailure = ex as CapCutOperationFailure ??
                        CapCutOperationFailure.FromLegacy(ex,
                            TransactionPhaseFor(state));
                    last = typedFailure ?? ex;
                    if (typedFailure != null)
                        draftTransactions.MarkFailure(task, typedFailure,
                            task.HandoffAttemptId ?? "");
                    if (scopedDraftRoot != null && scopedDraftRoot.HasTaskDraftRootLease)
                        scopedDraftRoot.UpdateTaskDraftRootStage(state + ":failed");
                    logger.Warn(String.Format("步骤失败: {0}（{1}/{2}）{3}", stepName, attempt + 1, retryCount + 1, ex.Message));
                    if (ex is StableExportArtifactException) {
                        logger.Warn("本轮CapCut文件已经生成并稳定；禁止再次点击导出，将保留断点供安全继续接续");
                        break;
                    }
                    if (IsHardSafetyError(last)) {
                        logger.Warn("检测到CapCut安全硬错误，停止本步骤的通用重试: " + ex.Message);
                        break;
                    }
                    if (attempt < retryCount) {
                        WaitWithCancellation(Math.Min(1500 * (attempt + 1), 5000), token);
                        WaitIfPaused(token);
                    }
                }
            }
            if (last is CapCutOperationFailure) throw last;
            throw new InvalidOperationException(stepName + "失败: " + (last == null ? "未知错误" : last.Message), last);
        }

        private void SyncDraftCreationState(VideoTask task)
        {
            IDraftCreationState creation = automation as IDraftCreationState;
            if (creation == null || task == null) return;
            task.DraftCreationPhase = creation.DraftCreationPhase;
            task.DraftCreationCheckpoint = creation.CaptureDraftCreationCheckpoint();
            draftTransactions.CaptureCreation(task, task.DraftCreationCheckpoint);
        }

        // Reopen the styled draft, retrying failures that leave the draft
        // untouched in a brand-new CapCut session: the same recovery an
        // operator gets from Safe Continue, without stopping the queue.  The
        // one-click rule still holds inside every session.
        internal int FreshReopenSessionDelayMilliseconds = 5000;

        internal void ReopenDirectDraftInFreshSessions(VideoTask task, string stepName,
            IDirectDraftSubtitleStyling directDraft, CancellationToken token)
        {
            int sessions = 1 + Math.Max(0, config.RetryCount);
            for (int session = 1; ; session++) {
                try {
                    RunStep(task, TaskState.VerifyingSubtitleStyle, stepName,
                        config.Timeouts.LaunchSeconds,
                        delegate(int timeout, CancellationToken ct) {
                            directDraft.ReopenAndVerifyDirectDraftSubtitleStyle(timeout, ct);
                        }, token, 0);
                    return;
                } catch (Exception ex) {
                    if (session >= sessions || !IsFreshSessionRetryableReopenFailure(ex)) throw;
                    automation.CloseSession();
                    ICapCutSessionClosure closure = automation as ICapCutSessionClosure;
                    if (closure != null && closure.LastCloseLeftOwnedProcesses) {
                        logger.Warn("草稿重开失败后本轮剪映进程未能退出，停止自动重试；已保留检查点，等待安全继续");
                        throw;
                    }
                    logger.Warn(String.Format(
                        "草稿重开失败（可安全重试）：已关闭剪映会话，{0} 秒后从检查点开启第 {1}/{2} 次全新会话。原因：{3}",
                        FreshReopenSessionDelayMilliseconds / 1000, session + 1, sessions, ex.Message));
                    WaitWithCancellation(FreshReopenSessionDelayMilliseconds, token);
                    WaitIfPaused(token);
                    directDraft.RestoreDirectDraftCheckpoint(task.DirectDraftCheckpoint, task.VideoPath);
                }
            }
        }

        // Import and place the task audio.  When the single drag of a session
        // did not land (CAPCUT_DRAFT_AUDIO_MISSING: the saved draft has no
        // audio track), redo it in a brand-new CapCut session from the
        // verified video checkpoint, as Safe Continue did on 2026-10-07 at
        // 16:59, instead of stopping the queue.  One drag per session still
        // holds, and audio that CapCut saved late is adopted, never dragged
        // again.  Returns true when that late audio was adopted (the draft is
        // then at the audio stage and reopened).
        internal bool PlaceAudioInFreshSessions(VideoTask task, CancellationToken token)
        {
            ITimelineAssemblyCheckpointing timeline = automation as ITimelineAssemblyCheckpointing;
            int sessions = 1 + Math.Max(0, config.RetryCount);
            for (int session = 1; ; session++) {
                try {
                    RunStep(task, TaskState.ImportingAudio, "导入项目独立音频", config.Timeouts.ImportSeconds,
                        delegate(int timeout, CancellationToken ct) { automation.ImportAudio(task.AudioPath, timeout, ct); }, token, 0);
                    RunStep(task, TaskState.PlacingAudioOnTimeline, "独立音频从零点对齐", config.Timeouts.TimelineSeconds,
                        delegate(int timeout, CancellationToken ct) { automation.PlaceAudioOnTimeline(timeout, ct); }, token, 0);
                    return false;
                } catch (Exception ex) {
                    TimelineAssemblyCheckpoint videoCheckpoint = task.TimelineAssemblyCheckpoint;
                    if (session >= sessions || timeline == null || !IsAudioMissingAfterDrag(ex) ||
                        videoCheckpoint == null ||
                        !String.Equals(videoCheckpoint.State, "video_timeline_committed",
                            StringComparison.OrdinalIgnoreCase))
                        throw;
                    automation.CloseSession();
                    ICapCutSessionClosure closure = automation as ICapCutSessionClosure;
                    if (closure != null && closure.LastCloseLeftOwnedProcesses) {
                        logger.Warn("独立音频未落盘后本轮剪映进程未能退出，停止自动重试；已保留主视频断点，等待安全继续");
                        throw;
                    }
                    logger.Warn(String.Format(
                        "独立音频拖入后草稿仍无音频轨：已关闭剪映会话，{0} 秒后从主视频时间线断点开启第 {1}/{2} 次全新会话。原因：{3}",
                        FreshReopenSessionDelayMilliseconds / 1000, session + 1, sessions, ex.Message));
                    WaitWithCancellation(FreshReopenSessionDelayMilliseconds, token);
                    WaitIfPaused(token);
                    TimelineAssemblyCheckpoint promoted;
                    if (timeline.TryCaptureExistingAudioTimelineCheckpoint(task, out promoted)) {
                        task.TimelineAssemblyCheckpoint = promoted;
                        task.DraftTransactionPhase = "audio_timeline_committed";
                        draftTransactions.Mark(task, "audio_timeline_committed",
                            task.HandoffAttemptId ?? "");
                        Persist(task);
                        logger.Info("上一会话拖入的独立音频已在草稿中落盘；不再拖入，直接从音频断点继续");
                        timeline.RestoreAndReopenAudioTimeline(promoted, task,
                            config.Timeouts.LaunchSeconds, token);
                        return true;
                    }
                    timeline.RestoreAndReopenVideoTimeline(videoCheckpoint, task,
                        config.Timeouts.LaunchSeconds, token);
                }
            }
        }

        // True only when the persisted draft simply has no audio track after
        // the drag.  Offset, duplicated or foreign audio, hard-safety failures
        // and cancellation stop for an operator.
        internal static bool IsAudioMissingAfterDrag(Exception error)
        {
            for (Exception current = error; current != null; current = current.InnerException) {
                if (current is OperationCanceledException || IsHardSafetyError(current)) return false;
            }
            for (Exception current = error; current != null; current = current.InnerException) {
                DraftTimelineValidationException validation = current as DraftTimelineValidationException;
                CapCutOperationFailure typed = current as CapCutOperationFailure;
                string code = validation != null ? validation.ErrorCode
                    : typed != null ? typed.ErrorCode : "";
                if (String.Equals(code, "CAPCUT_DRAFT_AUDIO_MISSING", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // True only for reopen failures that never edited the draft: CapCut's
        // window or homepage did not come up, the card could not be located (no
        // click), or the single click did not bind to a verified editor.  Any
        // hard-safety or identity failure in the chain stops for an operator.
        internal static bool IsFreshSessionRetryableReopenFailure(Exception error)
        {
            for (Exception current = error; current != null; current = current.InnerException) {
                if (current is OperationCanceledException || IsHardSafetyError(current)) return false;
            }
            for (Exception current = error; current != null; current = current.InnerException) {
                if (current is RecoverableDraftReopenException) return true;
                if (current is TimeoutException) {
                    string message = current.Message ?? "";
                    if (message == "等待剪映主窗口超时" ||
                        message.StartsWith("CAPCUT_HOME_NOT_READY:", StringComparison.Ordinal) ||
                        message.StartsWith("CAPCUT_TASK_DRAFT_CARD_NOT_LOCATED:", StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        private static bool IsHardSafetyError(Exception ex)
        {
            CapCutOperationFailure typed = ex as CapCutOperationFailure;
            if (typed != null)
                return typed.Classification == CapCutFailureClassification.HardSafety ||
                    typed.Classification == CapCutFailureClassification.UserActionRequired;
            string message = ex == null ? "" : (ex.Message ?? "");
            string[] hardCodes = {
                "CAPCUT_ACTIVE_DRAFT_ROOT_MARKER_MISSING",
                "CAPCUT_ACTIVE_DRAFT_ROOT_MARKER_UNREADABLE",
                "CAPCUT_ACTIVE_DRAFT_ROOT_IDENTITY_MISMATCH",
                "CAPCUT_ACTIVE_DRAFT_ROOT_PATH_MISMATCH",
                "CAPCUT_ACTIVE_DRAFT_ROOT_REPARSE_POINT_BLOCKED",
                "CAPCUT_DRAFT_PATH_OPEN_FAILED",
                "CAPCUT_DRAFT_CHECKPOINT_ARCHIVE_NOT_FOUND",
                "CAPCUT_DRAFT_CHECKPOINT_ARCHIVE_IDENTITY_MISMATCH",
                "CAPCUT_DRAFT_CHECKPOINT_HASH_MISMATCH",
                "CAPCUT_DRAFT_CREATION_COMMAND_UNCONFIRMED",
                "CAPCUT_DRAFT_CREATION_MULTIPLE_DRAFTS_BLOCKED",
                "CAPCUT_EDITOR_BINDING_IDENTITY_MISMATCH",
                "CAPCUT_AUDIO_CHECKPOINT_IDENTITY_MISMATCH",
                "CAPCUT_AUDIO_CHECKPOINT_DUPLICATED",
                "CAPCUT_AUDIO_CHECKPOINT_OFFSET",
                "CAPCUT_AUDIO_CHECKPOINT_PROMOTION_FAILED",
                "CAPCUT_MEDIA_STAGE_REGRESSION_BLOCKED",
                "CAPCUT_COMMITTED_MEDIA_UNKNOWN_BLOCKED",
                "CAPCUT_TASK_IDENTITY_CONFLICT",
                "CAPCUT_UNKNOWN_STALE_DRAFT_BLOCKED",
                "draft_root_switch_visible_capcut"
            };
            return hardCodes.Any(code => message.IndexOf(code,
                StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsRecoverableWindowTransition(
            CapCutOperationFailure failure)
        {
            if (failure == null) return false;
            string code = failure.ErrorCode ?? "";
            return failure.Classification == CapCutFailureClassification.Transient &&
                (String.Equals(code, "CAPCUT_DRAFT_CREATED_EDITOR_PENDING",
                    StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(code, "CAPCUT_EDITOR_WINDOW_NOT_READY",
                    StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(code, "CAPCUT_MAIN_WINDOW_TRANSITION_TIMEOUT",
                    StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(code, "CAPCUT_EDITOR_PROBE_TIMEOUT",
                    StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(code, "CAPCUT_EDITOR_REBIND_TIMEOUT",
                    StringComparison.OrdinalIgnoreCase));
        }

        private static string TransactionPhaseFor(TaskState state)
        {
            switch (state) {
                case TaskState.AcquiringDraftRoot: return "lease_acquired";
                case TaskState.Launching: return "editor_launching";
                case TaskState.CreatingProject: return "draft_creation";
                case TaskState.Importing:
                case TaskState.PlacingOnTimeline:
                case TaskState.MutingOriginal:
                    return "video_assembly";
                case TaskState.ImportingAudio:
                case TaskState.PlacingAudioOnTimeline:
                    return "audio_assembly";
                case TaskState.ImportingSample:
                case TaskState.PlacingSample:
                case TaskState.AdjustingSampleSpeed:
                case TaskState.PreparingSample:
                case TaskState.VerifyingSampleDuration:
                    return "sample_assembly";
                case TaskState.RecognizingCaptions: return "caption_recognition";
                case TaskState.SavingDraft: return "captions_saved";
                case TaskState.ApplyingSubtitleStyle:
                case TaskState.VerifyingSubtitleStyle:
                case TaskState.TrimmingFirstCaption: return "style_applied";
                case TaskState.Exporting:
                case TaskState.RecoveringExportArtifact:
                case TaskState.PostProcessing:
                case TaskState.VerifyingOutputQuality: return "export_verification";
                case TaskState.CleaningDraft:
                case TaskState.RestoringDraftRoot: return "release_pending";
                case TaskState.Completed: return "completed";
                case TaskState.Failed: return "failed";
                default: return "waiting";
            }
        }

        private void WaitIfPaused(CancellationToken token)
        {
            while (!pauseGate.WaitOne(250)) token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
        }

        private static void WaitWithCancellation(int milliseconds, CancellationToken token)
        {
            int elapsed = 0;
            while (elapsed < milliseconds) {
                token.ThrowIfCancellationRequested();
                int slice = Math.Min(200, milliseconds - elapsed);
                Thread.Sleep(slice);
                elapsed += slice;
            }
        }

        private static string BuildCapCutExportAttemptKey(VideoTask task)
        {
            string taskId = task == null ? "" : (task.Id ?? "").Trim();
            if (taskId.Length == 0) taskId = Guid.NewGuid().ToString("N");
            int attempt = task == null ? 0 : Math.Max(1, task.Attempts);
            // This prefix must survive CapCut's short filename limit.  The
            // output directory already scopes it to this task; the retained ID
            // fragment provides diagnosis without reusing a previous attempt.
            return "a" + attempt.ToString("D4") + "-" + taskId;
        }

        private string CreateUniqueCapCutTemporaryPath(VideoTask task)
        {
            if (task == null) throw new InvalidOperationException("任务为空");
            for (int probe = 0; probe < 1000; probe++) {
                task.Attempts++;
                string candidate = PostProcessPaths.CapCutTemporaryPath(
                    task.OutputPath, BuildCapCutExportAttemptKey(task));
                bool occupied = GetRecoveryTemporaryCandidates(candidate, false)
                    .Any(path => File.Exists(path));
                if (!occupied) {
                    logger.Info("已为本次真实导出预留唯一临时文件名: " +
                        Path.GetFileName(candidate));
                    return candidate;
                }
                logger.Warn("导出临时名称已存在，跳过且不覆盖: " +
                    Path.GetFileName(candidate));
            }
            throw new IOException("无法为本条任务生成唯一的 CapCut 临时导出文件名");
        }

        private static void ValidateTask(VideoTask task)
        {
            if (task == null) throw new InvalidOperationException("任务为空");
            if (String.IsNullOrWhiteSpace(task.VideoPath) || !File.Exists(task.VideoPath)) throw new FileNotFoundException("源视频不存在", task.VideoPath);
            if (!String.IsNullOrWhiteSpace(task.AudioPath) && !File.Exists(task.AudioPath)) throw new FileNotFoundException("独立音频不存在", task.AudioPath);
            if (!String.IsNullOrWhiteSpace(task.GroupId)) {
                if (String.IsNullOrWhiteSpace(task.AudioPath)) throw new InvalidOperationException("项目语言组必须设置独立音频");
                if (!String.IsNullOrWhiteSpace(task.SamplePath) && !File.Exists(task.SamplePath)) throw new FileNotFoundException("结尾样品不存在", task.SamplePath);
                if (!String.IsNullOrWhiteSpace(task.SamplePath) && task.SampleSpeed <= 0) throw new InvalidOperationException("结尾样品速度必须大于 0");
                if (String.IsNullOrWhiteSpace(task.OutputPrefix)) throw new InvalidOperationException("项目语言组没有输出前缀");
                if (task.Sequence < 1) throw new InvalidOperationException("输出序号必须从 1 开始");
            }
            if (String.IsNullOrWhiteSpace(task.OutputPath)) throw new InvalidOperationException("任务没有输出路径");
            Directory.CreateDirectory(Path.GetDirectoryName(task.OutputPath));
        }

        private static void VerifyNewStableOutput(
            string path,
            OutputArtifactBaseline baseline,
            DateTime exportStartedUtc,
            CancellationToken token)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("本轮导出成片不存在", path);
            long previousLength = -1;
            int stableSamples = 0;
            for (int sample = 0; sample < 8; sample++) {
                token.ThrowIfCancellationRequested();
                FileInfo file = new FileInfo(path);
                bool generatedThisRun = !baseline.Existed || file.Length != baseline.Length ||
                    file.LastWriteTimeUtc > baseline.LastWriteTimeUtc;
                if (!generatedThisRun || file.LastWriteTimeUtc < exportStartedUtc.AddSeconds(-2)) {
                    throw new InvalidOperationException("成片不是本轮新生成，已保留原视频");
                }
                if (file.Length > 0 && file.Length == previousLength) stableSamples++; else stableSamples = 0;
                previousLength = file.Length;
                if (stableSamples >= 2) return;
                Thread.Sleep(250);
            }
            throw new InvalidOperationException("成片大小尚未稳定，已保留原视频");
        }

        private bool IsResumableTemporaryOutput(VideoTask task)
        {
            string ignored;
            return TryGetResumableTemporaryOutput(task, out ignored);
        }

        private bool TryGetResumableTemporaryOutput(VideoTask task, out string temporaryOutputPath)
        {
            temporaryOutputPath = task == null ? "" : (task.TemporaryOutputPath ?? "").Trim();
            if (String.IsNullOrWhiteSpace(temporaryOutputPath)) return false;
            if (!String.IsNullOrWhiteSpace(task.OutputPath) && File.Exists(task.OutputPath)) return false;
            if (!MatchesTemporaryOutputIdentity(task, temporaryOutputPath)) return false;
            foreach (string candidate in GetRecoveryTemporaryCandidates(temporaryOutputPath)) {
                try {
                    if (!File.Exists(candidate)) continue;
                    bool isRegisteredPath = String.Equals(
                        Path.GetFullPath(candidate),
                        Path.GetFullPath(temporaryOutputPath),
                        StringComparison.OrdinalIgnoreCase);
                    if (!isRegisteredPath && task.Attempts <= 0) continue;
                    if (!MatchesTemporaryOutputIdentity(task, candidate)) continue;
                    FileInfo file = new FileInfo(candidate);
                    if (file.Length < 1024 || !String.Equals(file.Extension, ".mp4", StringComparison.OrdinalIgnoreCase)) continue;
                    // A safe retry can happen hours or days after a desktop
                    // export.  Do not reject a task-scoped temporary artifact
                    // merely because it predates the latest attempt; identity,
                    // size stability and the subsequent media validation are
                    // the actual safety gates.
                    temporaryOutputPath = candidate;
                    return true;
                } catch { }
            }
            return false;
        }

        private static bool MatchesTemporaryOutputIdentity(VideoTask task, string path)
        {
            if (task == null || String.IsNullOrWhiteSpace(path)) return false;
            string taskId = (task.Id ?? "").Trim();
            if (taskId.Length < 6) return false;
            string stem = Path.GetFileNameWithoutExtension(path) ?? "";
            const string marker = ".capcut-temp-";
            int markerIndex = stem.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) return false;
            string actualSuffix = stem.Substring(markerIndex + marker.Length);

            // Current exports intentionally put the attempt marker first so it
            // survives CapCut 7.2's 50-character filename limit.  Only six
            // task-id characters remain in a suffix such as a0001-e31211;
            // requiring eight characters here made a valid export invisible
            // to recovery and incorrectly sent the task back to draft reopen.
            string expectedSuffix = BuildCapCutExportAttemptKey(task);
            if (expectedSuffix.Length > 12) expectedSuffix = expectedSuffix.Substring(0, 12);
            if (String.Equals(actualSuffix, expectedSuffix, StringComparison.OrdinalIgnoreCase))
                return true;

            // Any previous numbered attempt is recoverable when its retained
            // task-id fragment still matches this exact task. The attempt
            // number is chronology, not identity.
            if (actualSuffix.Length >= 12 &&
                (actualSuffix[0] == 'a' || actualSuffix[0] == 'A') &&
                actualSuffix[5] == '-') {
                int parsedAttempt;
                string digits = actualSuffix.Substring(1, 4);
                string taskFragment = actualSuffix.Substring(6);
                if (Int32.TryParse(digits, out parsedAttempt) && parsedAttempt > 0 &&
                    taskFragment.Length >= 6 &&
                    taskId.StartsWith(taskFragment, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }

            // Preserve recovery compatibility with temporary artifacts created
            // before the attempt-first naming change.
            int legacyTokenLength = Math.Min(12, taskId.Length);
            string legacySuffix = taskId.Substring(0, legacyTokenLength);
            return String.Equals(actualSuffix, legacySuffix, StringComparison.OrdinalIgnoreCase);
        }

        private IEnumerable<string> GetRecoveryTemporaryCandidates(string expectedPath,
            bool includeSiblingAttempts = true)
        {
            var candidates = new List<string> { expectedPath };
            string fileName = Path.GetFileName(expectedPath);
            var directories = new List<string>();
            string expectedDirectory = Path.GetDirectoryName(expectedPath);
            if (!String.IsNullOrWhiteSpace(expectedDirectory)) directories.Add(expectedDirectory);
            if (!String.IsNullOrWhiteSpace(config.CapCutExportStagingDirectory)) {
                candidates.Add(Path.Combine(config.CapCutExportStagingDirectory, fileName));
                directories.Add(config.CapCutExportStagingDirectory);
            }
            string localStagingFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "capcut-export-staging.path");
            try {
                if (File.Exists(localStagingFile)) {
                    foreach (string raw in File.ReadAllLines(localStagingFile)) {
                        string directory = Environment.ExpandEnvironmentVariables((raw ?? "").Trim());
                        if (directory.Length > 0) {
                            candidates.Add(Path.Combine(directory, fileName));
                            directories.Add(directory);
                        }
                    }
                }
            } catch (Exception ex) {
                logger.Warn("读取本机私有导出目录失败，无法查找断点临时成片: " + ex.Message);
            }
            // A real export can finish on attempt a0001 and fail only while
            // closing CapCut's completion page. The task may already have
            // advanced its in-memory attempt counter. Discover all task-scoped
            // attempts with the same output stem, then let the strict identity
            // check decide which artifact can be resumed.
            const string marker = ".capcut-temp-";
            string expectedStem = Path.GetFileNameWithoutExtension(expectedPath) ?? "";
            int markerIndex = expectedStem.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (includeSiblingAttempts && markerIndex > 0) {
                string pattern = expectedStem.Substring(0, markerIndex) +
                    marker + "*.mp4";
                foreach (string directory in directories
                    .Where(value => !String.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)) {
                    try {
                        if (Directory.Exists(directory)) {
                            candidates.AddRange(Directory.GetFiles(directory, pattern,
                                SearchOption.TopDirectoryOnly));
                        }
                    } catch { }
                }
            }
            return candidates.Where(path => !String.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void PromoteAndVerifyExistingTemporaryOutput(
            string sourcePath, string targetPath, VideoTask task, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                throw new FileNotFoundException("未找到可接续的CapCut临时成片", sourcePath);
            if (String.IsNullOrWhiteSpace(targetPath))
                throw new InvalidDataException("可接续临时成片没有登记的正式任务路径");
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
            if (!String.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath),
                StringComparison.OrdinalIgnoreCase) && !File.Exists(targetPath)) {
                File.Copy(sourcePath, targetPath, false);
            }
            if (!File.Exists(targetPath))
                throw new FileNotFoundException("无法复制可接续临时成片到任务登记路径", targetPath);
            long previousLength = -1;
            int stableSamples = 0;
            for (int sample = 0; sample < 8; sample++) {
                token.ThrowIfCancellationRequested();
                FileInfo file = new FileInfo(targetPath);
                if (file.Length < 1024)
                    throw new InvalidDataException("可接续临时成片大小异常，已保留现场: " + targetPath);
                stableSamples = file.Length == previousLength ? stableSamples + 1 : 0;
                previousLength = file.Length;
                if (stableSamples >= 2) {
                    if (!MatchesTemporaryOutputIdentity(task, targetPath))
                        throw new InvalidDataException("临时成片身份与任务ID不匹配，已保留现场");
                    return;
                }
                Thread.Sleep(250);
            }
            throw new InvalidOperationException("可接续临时成片大小尚未稳定，已保留现场");
        }

        private sealed class OutputArtifactBaseline
        {
            public bool Existed;
            public long Length;
            public DateTime LastWriteTimeUtc;

            public static OutputArtifactBaseline Capture(string path)
            {
                var baseline = new OutputArtifactBaseline();
                if (!File.Exists(path)) return baseline;
                FileInfo file = new FileInfo(path);
                baseline.Existed = true;
                baseline.Length = file.Length;
                baseline.LastWriteTimeUtc = file.LastWriteTimeUtc;
                return baseline;
            }
        }

        private void Persist(VideoTask task)
        {
            task.UpdatedAt = DateTime.Now.ToString("o");
            store.Save(activeTasks);
            HandoffStatusWriter.WriteAll(activeTasks, IsRunning, IsPaused);
            var handler = TaskChanged;
            if (handler != null) handler(task);
        }

        private void RaiseRunState()
        {
            var handler = RunStateChanged;
            if (handler != null) handler(IsRunning, IsPaused);
        }
    }
}

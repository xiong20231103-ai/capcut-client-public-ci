using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using CapCutBatchClient;

internal static class CapCutCoreTests
{
    private static int failures;

    private static void Main()
    {
        Run("Pro export prompt requires heading and blocking action", delegate {
            if (!ExportEntitlementPolicy.IsBlockingPrompt("您 正 在 使 用 Pro 权益 字幕识别 获得免费Pro Get discount")) throw new Exception("real prompt missed");
            if (!ExportEntitlementPolicy.IsBlockingPrompt("您 止 在 便 用 Pro 功 能 获 得 pro Get discount")) throw new Exception("incident OCR spelling missed");
            if (ExportEntitlementPolicy.IsBlockingPrompt("Pro teams订阅用户可享受pro权益")) throw new Exception("account badge is not a blocker");
            if (ExportEntitlementPolicy.IsBlockingPrompt("字幕识别 Pro 视频已成功导出到本地")) throw new Exception("completion is not a blocker");
            if (ExportEntitlementPolicy.IsBlockingPrompt("您正在使用Pro权益")) throw new Exception("heading alone is insufficient");
            var failure = ExportEntitlementPolicy.Failure();
            if (failure.Classification != CapCutFailureClassification.UserActionRequired) throw new Exception("must stop retries");
            if (KnownCapCutFailures.Match(failure.Message) != "CAPCUT_PRO_ENTITLEMENT_BLOCKED") throw new Exception("status code lost");
        });
        Run("mobile pump trailer belongs only to ExampleBrand", delegate {
            if (!CategoryCatalog.IsMaterialAllowed("ExampleBrand", "MaterialPump")) throw new Exception("ExampleBrand mobile pump rejected");
            if (CategoryCatalog.IsMaterialAllowed("ProjectD", "MaterialPump")) throw new Exception("formal project accepted ExampleBrand material");
            if (CategoryCatalog.IsMaterialAllowed("ExampleBrand", "unknown-product")) throw new Exception("unknown material accepted");
        });
        Run("unique output naming", TestUniqueOutputName);
        Run("task draft identity includes every production key", TestTaskDraftIdentity);
        Run("stable CapCut identity survives attempt changes", TestStableCapCutIdentitySurvivesAttemptChange);
        Run("CapCut draft transaction persists creation intent", TestCapCutDraftTransactionPersistence);
        Run("CapCut transaction rotates active errors per attempt", TestCapCutTransactionRotatesAttemptErrors);
        Run("typed CapCut hard failures remain primary", TestTypedCapCutFailureClassification);
        Run("active draft marker validation accepts stable and legacy identities", TestActiveDraftMarkerValidation);
        Run("legacy config opts into local active draft storage", TestLegacyConfigEnablesLocalDraftStorage);
        Run("task draft root switches and restores exact CapCut config", TestTaskDraftRootRoundTrip);
        Run("task draft checkpoint migrates without touching original drafts", TestTaskDraftCheckpointMigration);
        Run("caption checkpoint migrates and rebinds to a new lease", TestCaptionDraftCheckpointMigration);
        Run("caption and timeline checkpoints migrate atomically", TestCaptionAndTimelineCheckpointMigration);
        Run("timeline checkpoint migrates and rebinds before captions", TestTimelineDraftCheckpointMigration);
        Run("zero-byte draft metadata falls back to the exact task index", TestDraftIdentityFallsBackToTaskIndex);
        Run("safe continue uses the prepared sample identity", TestPreparedSampleCheckpointEnvelopeRebind);
        Run("lost prepared identity restores the assembled draft without media writes", TestLostPreparedIdentityRecovery);
        Run("missing prepared artifact does not fabricate recovery", TestMissingPreparedIdentityRecovery);
        Run("invalid prepared artifact leaves task facts untouched", TestInvalidPreparedIdentityRecovery);
        Run("existing prepared identity cannot be overwritten by recovery", TestExistingPreparedIdentityRecovery);
        Run("invalid or unneeded sample recovery is rejected", TestUnneededPreparedIdentityRecovery);
        Run("foreign second segment is a hard identity failure", TestForeignSampleRecoveryFailure);
        Run("task draft checkpoint follows its previous scoped index", TestTaskDraftCheckpointUsesPreviousScopedIndex);
        Run("CapCut rewritten checkpoint card id is repaired from the sole verified draft", TestTaskDraftCheckpointRepairsRewrittenCardId);
        Run("empty CapCut bootstrap draft is quarantined beside the verified task draft", TestTaskDraftBootstrapIsQuarantined);
        Run("same-run bootstrap draft is quarantined before first reopen", TestSameRunBootstrapIsQuarantinedBeforeReopen);
        Run("unknown extra task draft is never guessed or quarantined", TestUnknownExtraTaskDraftIsBlocked);
        Run("two source-bound task drafts are rejected", TestMultipleSourceBoundTaskDraftsAreBlocked);
        Run("task draft lease blocks a different task and recovers the same task", TestTaskDraftRootConflictAndRecovery);
        Run("task draft lease reclaims a stale attempt for the same verified checkpoint", TestTaskDraftRootStaleAttemptRecovery);
        Run("stale empty draft lease is safely restored before a new attempt", TestStaleEmptyDraftLeaseRecovery);
        Run("marker loss on an empty same-task lease is recoverable", TestMarkerLossOnEmptySameTaskLease);
        Run("stale empty draft evidence crosses Windows volumes safely", TestCrossVolumeEmptyDraftEvidenceRecovery);
        Run("stale lease with unknown draft is preserved and blocked", TestUnknownStaleDraftLeaseIsBlocked);
        Run("live draft lease owner cannot be reclaimed", TestLiveDraftLeaseOwnerIsBlocked);
        Run("late CapCut process ownership excludes the pre-launch baseline", TestDelayedCapCutOwnershipPolicy);
        Run("unverified interrupted draft is retained before clean retry", TestInterruptedTaskDraftRetention);
        Run("known CapCut failures have stable unique fingerprints", TestKnownCapCutFailureCatalog);
        Run("CapCut transaction starts a new generation after release", TestTransactionGenerationReset);
        Run("runner acquires draft root before launch and restores after close", TestRunnerDraftRootLifecycle);
        Run("lab rerun naming does not grow recursively", TestLabRerunNameDoesNotGrow);
        Run("first caption at zero requires exact one-frame movement", TestFirstCaptionAtZeroFrameMath);
        Run("late first caption is skipped", TestLateFirstCaptionIsSkipped);
        Run("Q no-op blocks export", TestFirstCaptionNoOpBlocksExport);
        Run("first caption trim keeps right edge fixed", TestFirstCaptionRightEdgeMustStayFixed);
        Run("first caption scan includes calibrated zero edge", TestFirstCaptionScanStartsBeforeZero);
         Run("legacy home first-caption coordinates migrate without touching others", TestLegacyFirstCaptionMigration);
         Run("caption style skips non-caption timeline mirror", TestDirectDraftSubtitleStyleSkipsNonCaptionMirror);
         Run("project language output naming", TestProjectLanguageOutputName);
        Run("sample speed calculation", TestSampleSpeedCalculation);
        Run("sample selection is deterministic across library order", TestDeterministicSampleSelection);
        Run("video-only checkpoint ignores uncommitted sample path", TestVideoOnlyCheckpointIdentity);
        Run("audio checkpoint ignores uncommitted sample path", TestAudioCheckpointIdentity);
        Run("audio not longer than video skips sample", TestAudioNotLongerSkipsSample);
        Run("sample append requires a new verified timeline segment", TestSampleAppendVerification);
        Run("persisted draft proves sample append across visual track shifts", TestDraftTimelineAssemblyVerification);
        Run("persisted draft rejects replacement overlay duplicate and timing faults", TestDraftTimelineAssemblyFailures);
        Run("misplaced task sample is atomically merged into the main track", TestDraftTimelineSampleRepair);
        Run("persisted draft alone proves independent audio placement", TestDraftAudioPlacementVerification);
        Run("video-only timeline creates a durable audio-import checkpoint", TestDraftVideoTimelineCheckpoint);
        Run("timeline placement is verified before mute", TestDraftVideoPlacementCheckpoint);
        Run("stable archive without legacy checkpoint restores by exact source identity", TestStableArchiveVideoTimelineDiscovery);
        Run("empty stable archive is treated as no checkpoint", TestEmptyStableArchiveIsNoCheckpoint);
        Run("empty archived bootstrap is rebound without a second create click", TestEmptyArchivedBootstrapRebind);
        Run("Acquire preparation remains bound through active-root validation", TestArchivedCandidateAcquireValidation);
        Run("persisted draft rejects offset duplicate and cross-task audio", TestDraftAudioPlacementFailures);
        Run("sample speed must end at the independent audio", TestSampleAudioEndAlignment);
        Run("audio timeline vision detects the first lane above the legacy scan band", TestAudioTimelineVision);
        Run("caption recognition button is found dynamically inside the left panel", TestCaptionRecognitionButtonVision);
        Run("caption recognition progress overrides the painted start button", TestCaptionRecognitionProgressPolicy);
        Run("saved production failure screenshots replay through new vision gates", TestInteractionVisionFailureReplay);
        Run("rotated MOV is vertical", TestRotatedMovIsVertical);
        Run("task state persistence", TestTaskStore);
        Run("category sample routing", TestCategorySampleRouting);
        Run("category full-name matching", TestCategoryFullNameMatching);
        Run("SPC fixed material validation", TestSpcFixedMaterialValidation);
        Run("handoff rejects missing exact media", TestHandoffRejectsMissingMedia);
        Run("same-task handoff preserves recovery checkpoint", TestHandoffPreservesRecoveryCheckpoint);
        Run("home promotion dismissal is positively identified", TestHomePromotionPolicy);
        Run("native picker foreground recovery preserves identity", TestPickerRefocusPolicy);
        Run("rename dialog is never accepted as media picker", TestMediaFileDialogIdentity);
        Run("HDR notice acknowledgement is detected above editor centre", TestHdrNoticeVision);
        Run("remote handoff keeps receiver audio name", TestRemoteHandoffAudioNumberCompatibility);
        Run("owned handoff accepts task-scoped copied audio", TestOwnedHandoffAcceptsCopiedAudioName);
        Run("new handoff clears terminal stale handoff", TestNewHandoffClearsTerminalStaleHandoff);
        Run("handoff status persistence", TestHandoffStatusPersistence);
        Run("partial material handoff is blocked before CapCut launch", TestPartialMaterialHandoffIsBlocked);
        Run("partial retry handoff accepts a verified completed sibling", TestPartialMaterialRetryWithCompletedTaskIsAllowed);
        Run("partial retry counts a sibling moved to a release-only manifest", TestPartialRetryCountsSiblingReleasedFromAttempt);
        Run("partial retry still blocks a release-only task of another project", TestPartialRetryIgnoresForeignReleaseOnlyTask);        Run("partial retry manifest round-trip preserves project identity", TestPartialRetryManifestRoundTrip);
        Run("runner preserves failed draft and stops before next task", TestRunnerContinuesAfterFailure);
        Run("each video uses an independent draft", TestEachVideoUsesIndependentDraft);
        Run("one two four task queues complete independently", TestQueueSizesCompleteIndependently);
        Run("local completion keeps source temporary and final for cloud", TestSourceRecycleOrder);
        Run("FFmpeg failure preserves temporary output draft and source", TestPostProcessFailurePreservesEverything);
        Run("valid temporary export resumes without reopening CapCut", TestResumableTemporaryExportSkipsCapCut);
        Run("previous stable export attempt resumes after completion-page lock", TestPreviousTemporaryAttemptResumes);
        Run("CapCut temporary export name stays within native field limit", TestCapCutTemporaryExportNameLimit);
        Run("CapCut retry uses a fresh temporary export name", TestCapCutRetryUsesFreshTemporaryExportName);
        Run("CapCut export skips every existing candidate name", TestCapCutExportSkipsExistingCandidateName);
        Run("isolated draft retention is not reported as an error", TestIsolatedDraftRetentionIsNotReportedAsError);
        Run("FFmpeg retry uses a fresh working filename", TestFirstFrameWorkingPathUsesRetryMarker);
        Run("export failure preserves source video", TestExportFailurePreservesSource);
        Run("draft deletion failure preserves source video", TestDraftDeletionFailurePreservesSource);
        Run("verified output survives draft-root release failure", TestVerifiedOutputSurvivesDraftReleaseFailure);
        Run("draft archive path preflight blocks legacy MAX_PATH", TestDraftArchivePathPreflight);
        Run("protected media is retained before cloud cleanup", TestProtectedMediaIsNeverRecycled);
        Run("source deletion policy follows task origin", TestSourceDeletionPolicy);
        Run("workbench cannot enable Q no-op diagnostics", TestWorkbenchCannotEnableDiagnostics);
        Run("local A B C test launcher is isolated", TestLocalFirstCaptionTestLauncher);
        Run("local sample append test is isolated from handoff", TestLocalSampleAppendTestIsolation);
        Run("formal flow has no first-caption coordinate dependency", TestDiagnosticFailurePreservesDraftAndSource);
        Run("subtitle style failure blocks export", TestSubtitleStyleFailureBlocksExport);
        Run("direct draft subtitle style preserves identity timing and tracks", TestDirectDraftSubtitleStyle);
        Run("CapCut 9.2 draft style preserves every protected hash and rejects 9.3", TestCapCut92DraftCompatibility);
        Run("direct draft subtitle placement changes only caption y", TestDirectDraftSubtitlePlacement);
        Run("isolated handoff uses direct draft style for legacy preset labels", TestIsolatedHandoffUsesDirectDraftStyle);
        Run("direct draft reopen accepts only explicitly opened identity-locked editor", TestDirectDraftReopenPolicy);
        Run("draft activation accepts CapCut reopen metadata updates", TestDraftActivationEvidencePolicy);
        Run("direct draft checkpoint owns its CapCut launch", TestDirectDraftCheckpointLaunchPolicy);
        Run("draft card vision locates a dynamic first card", TestDraftCardVision);
        Run("isolated client only closes a separately-owned CapCut home shell", TestCapCutHomeShellPolicy);
        Run("editor onboarding hint dismissal requires exact ownership and content", TestEditorHintDismissal);
        Run("ffprobe JSON deserializes without case-insensitive member collisions", TestOutputQualityProbeDeserialization);
        Run("draft reopen retry accepts only untouched-draft failures", TestFreshSessionReopenClassifier);
        Run("draft reopen window timeout retries in a fresh session", TestFreshSessionReopenRetriesWindowTimeout);
        Run("draft reopen stops after the configured fresh sessions", TestFreshSessionReopenStopsWhenExhausted);
        Run("draft reopen does not retry an unsafe failure", TestFreshSessionReopenSkipsUnsafeFailure);
        Run("draft reopen stops when CapCut did not exit", TestFreshSessionReopenStopsWhenProcessRemains);
        Run("draft reopen stops when the checkpoint no longer verifies", TestFreshSessionReopenStopsOnCheckpointFailure);
        Run("audio missing after the drag is redone in a fresh session", TestAudioMissingRetriesInFreshSession);
        Run("audio saved late is adopted, never dragged again", TestAudioSavedLateIsAdopted);
        Run("audio placement stops on other audio failures", TestAudioPlacementStopsOnOtherFailures);
        Run("audio placement stops when sessions are exhausted", TestAudioPlacementStopsWhenExhausted);
        Run("audio placement stops when CapCut does not exit", TestAudioPlacementStopsWhenProcessRemains);
        Run("durable file writes the same bytes and replaces the whole file", TestDurableFileWrites);
        Run("release-only manifest keeps the workbench quality result", TestReleaseOnlyImportKeepsQuality);
        Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : failures + " TEST(S) FAILED");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    private static void TestUniqueOutputName()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "demo.mp4");
            File.WriteAllText(video, "video");
            string first = Path.Combine(dir, "demo_\u5b57\u5e55\u7248.mp4");
            File.WriteAllText(first, "existing");
            string actual = OutputNaming.UniqueCaptionPath(video, dir, ".mp4", new string[0]);
            Assert(Path.GetFileName(actual) == "demo_\u5b57\u5e55\u7248 (2).mp4", "unexpected output name: " + actual);
        });
    }

    private static void TestProjectLanguageOutputName()
    {
        WithTempDirectory(delegate(string dir) {
            string actual = OutputNaming.ProjectBatchPath("ProjectA西班牙", 4, dir, ".mp4");
            Assert(Path.GetFileName(actual) == "ProjectA西班牙4.mp4", "unexpected project output name: " + actual);
        });
    }

    private static void TestSampleSpeedCalculation()
    {
        double speed = SamplePlanner.CalculateSpeed(10.871678, 13.8448125 - 10.85);
        Assert(Math.Abs(speed - 3.63) < 0.001, "expected 3.63x, got " + speed);
    }

    private static void TestDeterministicSampleSelection()
    {
        var video = new MediaMetadata { Path = "E:\\video.mp4", DurationSeconds = 10.0 };
        var audio = new MediaMetadata { Path = "E:\\voice.mp3", DurationSeconds = 13.0 };
        var first = new List<MediaMetadata> {
            new MediaMetadata { Path = "G:\\samples\\b.mov", DurationSeconds = 8.0 },
            new MediaMetadata { Path = "G:\\samples\\a.mov", DurationSeconds = 9.0 }
        };
        var second = first.AsEnumerable().Reverse().ToList();
        SamplePlan left = SamplePlanner.CreateDeterministic(video, audio, first, "stable-task");
        SamplePlan right = SamplePlanner.CreateDeterministic(video, audio, second, "stable-task");
        Assert(String.Equals(left.SamplePath, right.SamplePath, StringComparison.OrdinalIgnoreCase),
            "reordering the sample library must not change the task assignment");
        Assert(Math.Abs(left.Speed - right.Speed) < 0.000001,
            "reordering the sample library must not change sample speed");
    }

    private static void TestVideoOnlyCheckpointIdentity()
    {
        var task = new VideoTask {
            Id = "task-1", RemotePipelineTaskId = "canonical-1", ProductionDate = "9.3",
            VideoPath = "E:\\video.mp4", AudioPath = "E:\\voice.mp3", SamplePath = "G:\\new.mov"
        };
        var checkpoint = new TimelineAssemblyCheckpoint {
            State = "video_timeline_committed", TaskId = task.Id,
            CanonicalTaskId = task.RemotePipelineTaskId, ProductionDate = task.ProductionDate,
            MainVideoPath = task.VideoPath, AudioPath = task.AudioPath, SamplePath = "G:\\old.mov"
        };
        Assert(TimelineCheckpointIdentityPolicy.StableIdentityMatches(checkpoint, task),
            "a video-only checkpoint must not fail because its uncommitted sample plan changed");
        checkpoint.State = "timeline_assembled";
        Assert(!TimelineCheckpointIdentityPolicy.StableIdentityMatches(checkpoint, task),
            "a fully assembled checkpoint must still require the exact sample identity");
    }

    private static void TestAudioCheckpointIdentity()
    {
        var task = new VideoTask {
            Id = "task-audio", RemotePipelineTaskId = "canonical-audio", ProductionDate = "9.3",
            VideoPath = "E:\\video.mp4", AudioPath = "E:\\voice.mp3", SamplePath = "G:\\new.mov"
        };
        var checkpoint = new TimelineAssemblyCheckpoint {
            State = "audio_timeline_committed", TaskId = task.Id,
            CanonicalTaskId = task.RemotePipelineTaskId, ProductionDate = task.ProductionDate,
            MainVideoPath = task.VideoPath, AudioPath = task.AudioPath, SamplePath = "G:\\old.mov"
        };
        Assert(TimelineCheckpointIdentityPolicy.StableIdentityMatches(checkpoint, task),
            "an audio checkpoint must not fail because its uncommitted sample plan changed");
        checkpoint.State = "timeline_assembled";
        Assert(!TimelineCheckpointIdentityPolicy.StableIdentityMatches(checkpoint, task),
            "a fully assembled checkpoint must still require the exact sample identity");
    }

    private static void TestActiveDraftMarkerValidation()
    {
        WithTempDirectory(delegate(string dir) {
            string active = Path.Combine(dir, "active");
            Directory.CreateDirectory(active);
            string markerName = ".codex-marker";
            var lease = new DraftRootLeaseRecord {
                ActiveDraftRoot = active,
                TaskDraftRoot = active,
                JunctionMarkerName = markerName,
                StableTaskArchiveKey = "canonical-task|8.31|local-task|c:\\media\\source.mp4",
                TaskIdentity = "batch|canonical-task|8.31|manifest|attempt|local-task",
                CanonicalTaskId = "canonical-task",
                ProductionDate = "8.31",
                LocalTaskId = "local-task"
            };
            var manager = new TaskDraftRootLeaseManager(
                AppConfig.CreateDefault(), new AppLogger(),
                Path.Combine(dir, "lease.json"), Path.Combine(dir, "roots"),
                Path.Combine(dir, "globalSetting"));
            string marker = Path.Combine(active, markerName);

            File.WriteAllText(marker, lease.StableTaskArchiveKey,
                new UTF8Encoding(false));
            DraftRootOwnershipValidationResult stable =
                manager.ValidateActiveRootOwnership(lease);
            Assert(stable.IsValid && stable.Status ==
                DraftRootOwnershipStatus.ValidStableIdentity,
                "stable archive marker must be accepted by the shared validator");

            File.WriteAllText(marker, lease.TaskIdentity,
                new UTF8Encoding(false));
            DraftRootOwnershipValidationResult attempt =
                manager.ValidateActiveRootOwnership(lease);
            Assert(attempt.IsValid && attempt.Status ==
                DraftRootOwnershipStatus.ValidAttemptIdentity,
                "current attempt marker must remain compatible");

            File.WriteAllText(marker,
                "batch|canonical-task|8.31|old-manifest|old-attempt|local-task",
                new UTF8Encoding(false));
            DraftRootOwnershipValidationResult legacy =
                manager.ValidateActiveRootOwnership(lease);
            Assert(legacy.IsValid && legacy.Status ==
                DraftRootOwnershipStatus.ValidLegacyIdentity,
                "same-task legacy marker must be accepted for migration");

            File.WriteAllText(marker, "other-task-marker",
                new UTF8Encoding(false));
            DraftRootOwnershipValidationResult wrong =
                manager.ValidateActiveRootOwnership(lease);
            Assert(!wrong.IsValid && wrong.Status ==
                DraftRootOwnershipStatus.IdentityMismatch &&
                wrong.ErrorCode == "CAPCUT_ACTIVE_DRAFT_ROOT_IDENTITY_MISMATCH",
                "a different task marker must be rejected with a structured error");

            DraftRootOwnershipValidationResult missing =
                manager.ValidateActiveRootOwnership(null);
            Assert(!missing.IsValid && missing.Status ==
                DraftRootOwnershipStatus.LeaseMissing,
                "a missing lease must return a safe diagnostic result");
        });
    }

    private static void TestTaskDraftIdentity()
    {
        var task = new VideoTask {
            Id = "local-id",
            RemotePipelineTaskId = "canonical-id",
            HandoffBatchId = "batch-id",
            ProductionDate = "7.29",
            HandoffManifestId = "manifest-a",
            HandoffAttemptId = "attempt-a"
        };
        string first = TaskDraftRootLeaseManager.BuildTaskIdentity(task);
        string stableFirst = TaskDraftRootLeaseManager.BuildStableTaskArchiveKey(task);
        task.HandoffAttemptId = "attempt-b";
        string second = TaskDraftRootLeaseManager.BuildTaskIdentity(task);
        string stableSecond = TaskDraftRootLeaseManager.BuildStableTaskArchiveKey(task);
        Assert(!String.Equals(first, second, StringComparison.Ordinal),
            "a new attempt must receive a different task draft identity");
        Assert(String.Equals(stableFirst, stableSecond, StringComparison.Ordinal),
            "a new attempt must reuse the stable task archive identity");
        Assert(String.Equals(stableFirst,
                CapCutTaskIdentity.FromTask(task).StableTaskArchiveKey,
                StringComparison.Ordinal),
            "lease and transaction layers must use the same stable identity source");
        Assert(first.Contains("batch-id") && first.Contains("canonical-id") &&
            first.Contains("7.29") && first.Contains("manifest-a"),
            "task draft identity must contain the immutable production keys");
    }

    private static void TestLegacyConfigEnablesLocalDraftStorage()
    {
        WithTempDirectory(delegate(string dir) {
            string path = Path.Combine(dir, "capcut.config.json");
            var config = AppConfig.CreateDefault();
            config.Save(path);
            string json = File.ReadAllText(path, Encoding.UTF8);
            const string property = "\"UseLocalActiveDraftDirectory\":true,";
            Assert(json.Contains(property),
                "the default config must persist the local active-directory flag");
            json = json.Replace(property, "");
            File.WriteAllText(path, json, new UTF8Encoding(false));
            var loaded = AppConfig.LoadOrCreate(path);
            Assert(loaded.UseLocalActiveDraftDirectory,
                "a legacy config without the property must opt into local storage");
        });
    }

    private static void TestTaskDraftRootRoundTrip()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string roots = Path.Combine(dir, "task-roots");
            string lease = Path.Combine(dir, "runtime", "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");
            Directory.CreateDirectory(originalRoot);
            WriteTestGlobalSetting(setting, originalRoot);
            byte[] before = File.ReadAllBytes(setting);
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            var task = NewDraftRootTask("attempt-roundtrip");
            var manager = new TaskDraftRootLeaseManager(
                config, new AppLogger(), lease, roots, setting);

            DraftRootLeaseRecord acquired =
                manager.Acquire(task, CancellationToken.None);
            Assert(acquired != null && manager.HasLease,
                "task draft lease must be active");
            Assert(SameTestPath(TaskDraftRootLeaseManager.ReadCustomDraftPath(setting),
                    originalRoot),
                "CapCut config must keep the fixed draft path while leased");
            Assert((File.GetAttributes(originalRoot) &
                    FileAttributes.ReparsePoint) != 0,
                "the fixed CapCut draft path must be an owned junction while leased");
            Assert(File.Exists(Path.Combine(originalRoot,
                    acquired.JunctionMarkerName)),
                "the fixed CapCut path must resolve to the leased task root");
            Assert(Directory.Exists(acquired.ProtectedOriginalDraftRoot),
                "the historical draft root must be retained beside the owned junction");
            Assert(Directory.GetDirectories(acquired.TaskDraftRoot).Length == 0,
                "a new task root must not inherit historical drafts");

            manager.Release(CancellationToken.None);
            Assert(!manager.HasLease && !File.Exists(lease),
                "released task draft lease must not remain active");
            Assert(SameTestPath(TaskDraftRootLeaseManager.ReadCustomDraftPath(setting),
                    originalRoot),
                "CapCut config must restore the original draft root");
            Assert(before.SequenceEqual(File.ReadAllBytes(setting)),
                "CapCut globalSetting must be restored byte-for-byte");
            Assert((File.GetAttributes(originalRoot) &
                    FileAttributes.ReparsePoint) == 0 &&
                    !Directory.Exists(acquired.ProtectedOriginalDraftRoot),
                "release must restore the real historical directory and remove its protected alias");
        });
    }

    private static void TestTaskDraftCheckpointMigration()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string roots = Path.Combine(dir, "task-roots");
            string lease = Path.Combine(dir, "runtime", "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");
            string draft = Path.Combine(originalRoot, "0729 (1)");
            Directory.CreateDirectory(draft);
            string draftContent = Path.Combine(draft, "draft_content.json");
            string draftMeta = Path.Combine(draft, "draft_meta_info.json");
            File.WriteAllText(draftContent,
                "{\"source\":\"" + draft.Replace("\\", "\\\\") + "\"}",
                new UTF8Encoding(false));
            // CapCut 9.2 may leave this sidecar empty after a safe close.  The
            // root index still carries the exact draft id/name and must be
            // sufficient for archive resolution.
            File.WriteAllText(draftMeta, "", new UTF8Encoding(false));
            WriteTestDraftIndex(originalRoot, "draft-id-one", draft);
            WriteTestGlobalSetting(setting, originalRoot);
            byte[] originalContent = File.ReadAllBytes(draftContent);
            byte[] originalIndex = File.ReadAllBytes(
                Path.Combine(originalRoot, "root_meta_info.json"));

            var task = NewDraftRootTask("attempt-migrate");
            task.DirectDraftCheckpoint = new DirectDraftCheckpoint {
                DraftId = "draft-id-one",
                DraftName = "0729 (1)",
                DraftDirectory = draft,
                SourceVideoPath = task.VideoPath,
                PatchedFiles = new[] { draftContent }
            };
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            var manager = new TaskDraftRootLeaseManager(
                config, new AppLogger(), lease, roots, setting);
            DraftRootLeaseRecord acquired =
                manager.Acquire(task, CancellationToken.None);

            Assert(task.DirectDraftCheckpoint.DraftDirectory.StartsWith(
                    acquired.TaskDraftRoot, StringComparison.OrdinalIgnoreCase),
                "checkpoint must be rebound to the dedicated task root");
            Assert(Directory.GetDirectories(acquired.TaskDraftRoot).Length == 1,
                "checkpoint task root must contain exactly one draft");
            Assert(task.DirectDraftCheckpoint.DraftRootLeaseId ==
                    acquired.LeaseId,
                "checkpoint must record the owning draft-root lease");
            var mountedIndexSerializer = new JavaScriptSerializer();
            var mountedIndex = mountedIndexSerializer.DeserializeObject(
                File.ReadAllText(Path.Combine(acquired.TaskDraftRoot,
                    "root_meta_info.json"), Encoding.UTF8))
                as Dictionary<string, object>;
            object mountedStoresRaw;
            object[] mountedStores = mountedIndex != null &&
                mountedIndex.TryGetValue("all_draft_store",
                    out mountedStoresRaw)
                ? mountedStoresRaw as object[] : null;
            var mountedStore = mountedStores != null &&
                mountedStores.Length == 1
                ? mountedStores[0] as Dictionary<string, object> : null;
            Assert(mountedStore != null &&
                    SameTestPath(Convert.ToString(
                        mountedStore["draft_fold_path"]),
                        Path.Combine(originalRoot, "0729 (1)")) &&
                    SameTestPath(Convert.ToString(
                        mountedIndex["root_path"]), originalRoot),
                "the task index must advertise the fixed junction-visible path");
            Assert(originalContent.SequenceEqual(File.ReadAllBytes(
                    Path.Combine(acquired.ProtectedOriginalDraftRoot,
                        "0729 (1)", "draft_content.json"))),
                "checkpoint migration must not modify the original draft");
            Assert(originalIndex.SequenceEqual(File.ReadAllBytes(
                    Path.Combine(acquired.ProtectedOriginalDraftRoot,
                        "root_meta_info.json"))),
                "checkpoint migration must not modify the historical root index");
            manager.Release(CancellationToken.None);
            Assert(originalContent.SequenceEqual(File.ReadAllBytes(draftContent)) &&
                    originalIndex.SequenceEqual(File.ReadAllBytes(
                        Path.Combine(originalRoot, "root_meta_info.json"))),
                "the historical root must return byte-for-byte after release");

            // Reproduce CapCut 2026.7 appending the same physical draft under
            // its junction-visible path with a second generated ID. The next
            // acquire must repair this deterministically before launch.
            string taskIndexPath = Path.Combine(acquired.TaskDraftRoot,
                "root_meta_info.json");
            var polluted = mountedIndexSerializer.DeserializeObject(
                File.ReadAllText(taskIndexPath, Encoding.UTF8))
                as Dictionary<string, object>;
            object[] trusted = (object[])polluted["all_draft_store"];
            var duplicate = new Dictionary<string, object>(
                (Dictionary<string, object>)trusted[0]);
            duplicate["draft_id"] = "generated-duplicate-id";
            polluted["all_draft_store"] = new object[] {
                trusted[0], duplicate
            };
            File.WriteAllText(taskIndexPath,
                mountedIndexSerializer.Serialize(polluted),
                new UTF8Encoding(false));

            var retry = new TaskDraftRootLeaseManager(
                config, new AppLogger(), lease, roots, setting);
            DraftRootLeaseRecord retried = retry.Acquire(task,
                CancellationToken.None);
            var repaired = mountedIndexSerializer.DeserializeObject(
                File.ReadAllText(taskIndexPath, Encoding.UTF8))
                as Dictionary<string, object>;
            object[] repairedStores =
                (object[])repaired["all_draft_store"];
            Assert(repairedStores.Length == 1 &&
                    Convert.ToString(((Dictionary<string, object>)
                        repairedStores[0])["draft_id"]) == "draft-id-one",
                "a repeated acquire must remove CapCut's duplicate index identity");
            retry.Release(CancellationToken.None);
        });
    }

    private static void TestCaptionDraftCheckpointMigration()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string roots = Path.Combine(dir, "task-roots");
            string lease = Path.Combine(dir, "runtime", "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");
            string draft = Path.Combine(originalRoot, "0810");
            Directory.CreateDirectory(draft);
            string draftContent = Path.Combine(draft, "draft_content.json");
            string draftMeta = Path.Combine(draft, "draft_meta_info.json");
            File.WriteAllText(draftContent, "{\"captions\":6}",
                new UTF8Encoding(false));
            File.WriteAllText(draftMeta,
                "{\"draft_id\":\"caption-draft-id\",\"draft_name\":\"0810\"}",
                new UTF8Encoding(false));
            WriteTestDraftIndex(originalRoot, "caption-draft-id", draft);
            WriteTestGlobalSetting(setting, originalRoot);

            var task = NewDraftRootTask("caption-old-attempt");
            task.CaptionDraftCheckpoint = new CaptionDraftCheckpoint {
                State = "captions_saved",
                DraftId = "caption-draft-id",
                DraftName = "0810",
                DraftDirectory = draft,
                SourceVideoPath = task.VideoPath,
                CaptionCount = 6,
                CaptionIdentityHash = "caption-hash",
                TimingAndTrackHash = "timing-hash",
                ProtectedMediaHash = "media-hash",
                VerifiedFiles = new[] { draftContent, draftMeta },
                TaskId = task.Id,
                CanonicalTaskId = task.RemotePipelineTaskId,
                ProductionDate = task.ProductionDate,
                ManifestId = task.HandoffManifestId,
                AttemptId = task.HandoffAttemptId
            };
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            var manager = new TaskDraftRootLeaseManager(config,
                new AppLogger(), lease, roots, setting);
            DraftRootLeaseRecord acquired = manager.Acquire(task,
                CancellationToken.None);

            Assert(task.CaptionDraftCheckpoint.DraftDirectory.StartsWith(
                    acquired.TaskDraftRoot, StringComparison.OrdinalIgnoreCase),
                "caption checkpoint must move into the current task root");
            Assert(task.CaptionDraftCheckpoint.DraftRootLeaseId == acquired.LeaseId &&
                    task.CaptionDraftCheckpoint.TaskDraftRoot == acquired.TaskDraftRoot,
                "caption checkpoint must bind to the new lease");
            Assert(task.CaptionDraftCheckpoint.VerifiedFiles.All(path =>
                    path.StartsWith(acquired.TaskDraftRoot,
                        StringComparison.OrdinalIgnoreCase) && File.Exists(path)),
                "every verified caption file must be rebound to the migrated draft");
            Assert(Directory.GetDirectories(acquired.TaskDraftRoot).Length == 1,
                "caption recovery root must expose one verified task draft");
            manager.Release(CancellationToken.None);
        });
    }

    private static void TestTimelineDraftCheckpointMigration()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string roots = Path.Combine(dir, "task-roots");
            string lease = Path.Combine(dir, "runtime", "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");
            string previousRoot = Path.Combine(dir, "previous-task-root");
            string draft = Path.Combine(previousRoot, "0811");
            Directory.CreateDirectory(originalRoot);
            Directory.CreateDirectory(draft);
            string draftContent = Path.Combine(draft, "draft_content.json");
            string draftMeta = Path.Combine(draft, "draft_meta_info.json");
            File.WriteAllText(draftContent, "{\"timeline\":\"verified\"}",
                new UTF8Encoding(false));
            File.WriteAllText(draftMeta,
                "{\"draft_id\":\"timeline-draft-id\",\"draft_name\":\"0811\"}",
                new UTF8Encoding(false));
            WriteTestDraftIndex(previousRoot, "timeline-draft-id", draft);
            WriteTestGlobalSetting(setting, originalRoot);

            var task = NewDraftRootTask("timeline-new-attempt");
            task.TimelineAssemblyCheckpoint = new TimelineAssemblyCheckpoint {
                State = "timeline_assembled",
                DraftId = "timeline-draft-id",
                DraftName = "0811",
                DraftDirectory = draft,
                MainVideoPath = task.VideoPath,
                AudioPath = task.AudioPath,
                SamplePath = task.SamplePath,
                SemanticHash = "timeline-semantic-hash",
                VerifiedFiles = new[] { draftContent, draftMeta },
                TaskDraftRoot = previousRoot,
                DraftRootLeaseId = "previous-timeline-lease",
                TaskId = task.Id,
                CanonicalTaskId = task.RemotePipelineTaskId,
                ProductionDate = task.ProductionDate,
                ManifestId = task.HandoffManifestId,
                AttemptId = "timeline-old-attempt"
            };
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            var manager = new TaskDraftRootLeaseManager(config,
                new AppLogger(), lease, roots, setting);
            DraftRootLeaseRecord acquired = manager.Acquire(task,
                CancellationToken.None);

            Assert(task.TimelineAssemblyCheckpoint.DraftDirectory.StartsWith(
                    acquired.TaskDraftRoot, StringComparison.OrdinalIgnoreCase),
                "timeline checkpoint must migrate into the current safe-continue root");
            Assert(task.TimelineAssemblyCheckpoint.DraftRootLeaseId == acquired.LeaseId &&
                    task.TimelineAssemblyCheckpoint.TaskDraftRoot == acquired.TaskDraftRoot,
                "timeline checkpoint must bind to the current lease");
            Assert(task.TimelineAssemblyCheckpoint.VerifiedFiles.All(path =>
                    path.StartsWith(acquired.TaskDraftRoot,
                        StringComparison.OrdinalIgnoreCase) && File.Exists(path)),
                "timeline verified files must follow the migrated draft");
            Assert(Directory.Exists(Path.Combine(acquired.TaskDraftRoot, "0811")),
                "the verified timeline draft itself must be copied before reopen");
            manager.Release(CancellationToken.None);
        });
    }

    private static void TestCaptionAndTimelineCheckpointMigration()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string previousRoot = Path.Combine(dir, "previous-task-root");
            string roots = Path.Combine(dir, "task-roots");
            string lease = Path.Combine(dir, "runtime", "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");
            string draft = Path.Combine(previousRoot, "0811");
            Directory.CreateDirectory(originalRoot);
            Directory.CreateDirectory(draft);
            string draftContent = Path.Combine(draft, "draft_content.json");
            string draftMeta = Path.Combine(draft, "draft_meta_info.json");
            File.WriteAllText(draftContent, "{\"captions\":6}",
                new UTF8Encoding(false));
            File.WriteAllBytes(draftMeta, new byte[0]);
            WriteTestDraftIndex(previousRoot, "shared-draft-id", draft);
            WriteTestGlobalSetting(setting, originalRoot);

            var task = NewDraftRootTask("atomic-new-attempt");
            task.CaptionDraftCheckpoint = new CaptionDraftCheckpoint {
                State = "captions_saved",
                DraftId = "shared-draft-id",
                DraftName = "0811",
                DraftDirectory = draft,
                SourceVideoPath = task.VideoPath,
                CaptionCount = 6,
                CaptionIdentityHash = "caption-hash",
                TimingAndTrackHash = "timing-hash",
                ProtectedMediaHash = "media-hash",
                VerifiedFiles = new[] { draftContent },
                TaskDraftRoot = previousRoot,
                DraftRootLeaseId = "old-caption-lease"
            };
            task.TimelineAssemblyCheckpoint = new TimelineAssemblyCheckpoint {
                State = "timeline_assembled",
                DraftId = "shared-draft-id",
                DraftName = "0811",
                DraftDirectory = draft,
                MainVideoPath = task.VideoPath,
                AudioPath = task.AudioPath,
                SamplePath = task.SamplePath,
                SemanticHash = "timeline-hash",
                VerifiedFiles = new[] { draftContent },
                TaskDraftRoot = previousRoot,
                DraftRootLeaseId = "old-timeline-lease"
            };
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            var manager = new TaskDraftRootLeaseManager(config,
                new AppLogger(), lease, roots, setting);
            DraftRootLeaseRecord acquired = manager.Acquire(task,
                CancellationToken.None);

            Assert(task.CaptionDraftCheckpoint.DraftDirectory ==
                    task.TimelineAssemblyCheckpoint.DraftDirectory,
                "caption and timeline checkpoints must point to one migrated draft");
            Assert(task.CaptionDraftCheckpoint.TaskDraftRoot == acquired.TaskDraftRoot &&
                    task.TimelineAssemblyCheckpoint.TaskDraftRoot == acquired.TaskDraftRoot,
                "all checkpoint layers must bind to the same current task root");
            Assert(task.CaptionDraftCheckpoint.DraftRootLeaseId == acquired.LeaseId &&
                    task.TimelineAssemblyCheckpoint.DraftRootLeaseId == acquired.LeaseId,
                "all checkpoint layers must bind to the same current lease");
            Assert(task.CaptionDraftCheckpoint.VerifiedFiles.All(File.Exists) &&
                    task.TimelineAssemblyCheckpoint.VerifiedFiles.All(File.Exists),
                "all checkpoint file lists must follow the migrated draft");
            manager.Release(CancellationToken.None);
        });
    }

    private static void TestDraftIdentityFallsBackToTaskIndex()
    {
        WithTempDirectory(delegate(string dir) {
            string root = Path.Combine(dir, "task-root");
            string draft = Path.Combine(root, "0811");
            Directory.CreateDirectory(draft);
            File.WriteAllBytes(Path.Combine(draft, "draft_meta_info.json"),
                new byte[0]);
            WriteTestDraftIndex(root, "indexed-draft-id", draft);
            string id, name;
            long created;
            bool resolved = DraftIndexIdentityPolicy.TryResolveSoleMatchingDraft(
                draft, out id, out name, out created);
            Assert(resolved,
                "a zero-byte draft_meta_info must not erase an identity that is exact in the isolated index");
            Assert(id == "indexed-draft-id" && name == "0811",
                "the fallback identity must be the sole matching task-index record");
        });
    }

    private static void TestTaskDraftCheckpointUsesPreviousScopedIndex()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string roots = Path.Combine(dir, "task-roots");
            string previousRoot = Path.Combine(dir,
                "previous-task-root");
            string lease = Path.Combine(dir, "runtime",
                "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");

            string historicalDraft = Path.Combine(originalRoot,
                "historical");
            Directory.CreateDirectory(historicalDraft);
            File.WriteAllText(Path.Combine(historicalDraft,
                "draft_meta_info.json"), "{}", new UTF8Encoding(false));
            WriteTestDraftIndex(originalRoot, "historical-id",
                historicalDraft);

            string checkpointDraft = Path.Combine(previousRoot, "0729");
            Directory.CreateDirectory(checkpointDraft);
            string checkpointContent = Path.Combine(checkpointDraft,
                "draft_content.json");
            File.WriteAllText(checkpointContent,
                "{\"checkpoint\":true}", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(checkpointDraft,
                "draft_meta_info.json"),
                "{\"draft_name\":\"0729\"}",
                new UTF8Encoding(false));
            WriteTestDraftIndex(previousRoot, "scoped-checkpoint-id",
                checkpointDraft);
            WriteTestGlobalSetting(setting, originalRoot);

            var task = NewDraftRootTask("attempt-scoped-index");
            task.DirectDraftCheckpoint = new DirectDraftCheckpoint {
                DraftId = "scoped-checkpoint-id",
                DraftName = "0729",
                DraftDirectory = checkpointDraft,
                TaskDraftRoot = previousRoot,
                SourceVideoPath = task.VideoPath,
                PatchedFiles = new[] { checkpointContent }
            };
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            var manager = new TaskDraftRootLeaseManager(config,
                new AppLogger(), lease, roots, setting);

            DraftRootLeaseRecord acquired = manager.Acquire(task,
                CancellationToken.None);
            var serializer = new JavaScriptSerializer();
            var index = serializer.DeserializeObject(File.ReadAllText(
                Path.Combine(acquired.TaskDraftRoot,
                    "root_meta_info.json"), Encoding.UTF8))
                as Dictionary<string, object>;
            object[] stores = (object[])index["all_draft_store"];
            var only = (Dictionary<string, object>)stores[0];
            Assert(stores.Length == 1 && Convert.ToString(
                    only["draft_id"]) == "scoped-checkpoint-id",
                "checkpoint migration must read the index beside the previous task-scoped draft, not the unrelated global index");
            Assert(Directory.Exists(task.DirectDraftCheckpoint.DraftDirectory) &&
                    task.DirectDraftCheckpoint.DraftDirectory.StartsWith(
                        acquired.TaskDraftRoot,
                        StringComparison.OrdinalIgnoreCase),
                "checkpoint must be rebound into the new attempt root");
            manager.Release(CancellationToken.None);
        });
    }

    private static void TestTaskDraftRootConflictAndRecovery()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string roots = Path.Combine(dir, "task-roots");
            string lease = Path.Combine(dir, "runtime", "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");
            Directory.CreateDirectory(originalRoot);
            WriteTestGlobalSetting(setting, originalRoot);
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            VideoTask first = NewDraftRootTask("attempt-one");
            VideoTask other = NewDraftRootTask("attempt-two");
            var firstManager = new TaskDraftRootLeaseManager(
                config, new AppLogger(), lease, roots, setting);
            firstManager.Acquire(first, CancellationToken.None);

            var recovering = new TaskDraftRootLeaseManager(
                config, new AppLogger(), lease, roots, setting);
            recovering.Acquire(first, CancellationToken.None);
            Assert(recovering.HasLease,
                "the same task must recover its existing lease after a crash");

            bool blocked = false;
            try {
                var conflicting = new TaskDraftRootLeaseManager(
                    config, new AppLogger(), lease, roots, setting,
                    delegate(int pid) { return true; },
                    delegate { return false; });
                conflicting.Acquire(other, CancellationToken.None);
            } catch (InvalidOperationException ex) {
                blocked = ex.Message.Contains("draft_root_lease_conflict");
            }
            Assert(blocked,
                "a different task must never inherit the active draft lease");
            recovering.Release(CancellationToken.None);
        });
    }

    private static void TestStaleEmptyDraftLeaseRecovery()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string roots = Path.Combine(dir, "task-roots");
            string lease = Path.Combine(dir, "runtime", "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");
            Directory.CreateDirectory(originalRoot);
            WriteTestGlobalSetting(setting, originalRoot);
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            VideoTask oldTask = NewDraftRootTask("attempt-stale-empty");
            var first = new TaskDraftRootLeaseManager(config,
                new AppLogger(), lease, roots, setting,
                delegate(int pid) { return false; }, delegate { return false; });
            DraftRootLeaseRecord oldLease = first.Acquire(oldTask,
                CancellationToken.None);

            // CapCut can persist the empty bootstrap draft in root_meta_info
            // before the real task draft is created. It must still be safely
            // reclaimable when the indexed entry points to that blank folder.
            string bootstrap = Path.Combine(oldLease.TaskDraftRoot, "0826");
            Directory.CreateDirectory(bootstrap);
            File.WriteAllText(Path.Combine(bootstrap, "draft_content.json"),
                "{\"duration\":0,\"tracks\":[],\"materials\":{\"videos\":[],\"audios\":[]}}", Encoding.UTF8);
            var bootstrapStore = new Dictionary<string, object>();
            bootstrapStore["draft_fold_path"] = bootstrap;
            bootstrapStore["draft_id"] = "bootstrap-id";
            var bootstrapIndex = new Dictionary<string, object>();
            bootstrapIndex["all_draft_store"] = new object[] { bootstrapStore };
            File.WriteAllText(Path.Combine(oldLease.TaskDraftRoot,
                "root_meta_info.json"),
                new JavaScriptSerializer().Serialize(bootstrapIndex), Encoding.UTF8);

            VideoTask nextTask = NewDraftRootTask("attempt-after-empty");
            var retry = new TaskDraftRootLeaseManager(config,
                new AppLogger(), lease, roots, setting,
                delegate(int pid) { return false; }, delegate { return false; });
            DraftRootLeaseRecord nextLease = retry.Acquire(nextTask,
                CancellationToken.None);
            Assert(nextLease.LeaseId != oldLease.LeaseId &&
                    nextLease.AttemptId == "attempt-after-empty",
                "an empty stale lease must be archived and replaced by a fresh lease");
            string historyRoot = Path.Combine(Path.GetDirectoryName(lease),
                "draft-root-lease-history");
            Assert(Directory.Exists(historyRoot) && Directory.GetFiles(
                    historyRoot, "*" + oldLease.LeaseId + ".json").Length == 1,
                "the stale empty lease must remain in recoverable history");
            retry.Release(CancellationToken.None);
        });
    }

    private static void TestMarkerLossOnEmptySameTaskLease()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string roots = Path.Combine(dir, "task-roots");
            string shortArchives = Path.Combine(dir, ".capcut-drafts");
            string lease = Path.Combine(dir, "runtime", "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");
            Directory.CreateDirectory(originalRoot);
            WriteTestGlobalSetting(setting, originalRoot);
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            config.UseLocalActiveDraftDirectory = true;

            VideoTask firstTask = NewDraftRootTask("marker-loss-first");
            firstTask.Id = "stable-marker-loss";
            firstTask.VideoPath = Path.Combine(dir, "source.mp4");
            var first = new TaskDraftRootLeaseManager(config, new AppLogger(),
                lease, roots, setting,
                delegate(int pid) { return false; }, delegate { return false; },
                shortArchives);
            DraftRootLeaseRecord oldLease = first.Acquire(firstTask,
                CancellationToken.None);

            // Simulate a hard stop between directory creation and the marker
            // flush. The protected original remains present and the active
            // root is genuinely empty; a new attempt for the same stable task
            // must recover it instead of surfacing MARKER_MISSING.
            foreach (string entry in Directory.GetFileSystemEntries(oldLease.ActiveDraftRoot)) {
                if (Directory.Exists(entry)) Directory.Delete(entry, true);
                else File.Delete(entry);
            }
            VideoTask retryTask = NewDraftRootTask("marker-loss-retry");
            retryTask.Id = firstTask.Id;
            retryTask.VideoPath = firstTask.VideoPath;
            retryTask.HandoffManifestId = "manifest-retry";
            var retry = new TaskDraftRootLeaseManager(config, new AppLogger(),
                lease, roots, setting,
                delegate(int pid) { return false; }, delegate { return false; },
                shortArchives);
            DraftRootLeaseRecord recovered = retry.Acquire(retryTask,
                CancellationToken.None);
            Assert(recovered != null && retry.HasLease &&
                    recovered.AttemptId == retryTask.HandoffAttemptId &&
                    Directory.Exists(recovered.ActiveDraftRoot),
                "same-task marker loss with an empty protected root must recover safely");
            retry.Release(CancellationToken.None);
        });
    }

    private static void TestCrossVolumeEmptyDraftEvidenceRecovery()
    {
        WithTempDirectory(delegate(string dir) {
            string sourceRoot = Path.GetPathRoot(Path.GetFullPath(dir));
            string alternateRoot = Path.GetPathRoot(
                Path.GetFullPath(Environment.CurrentDirectory));
            if (String.Equals(alternateRoot, sourceRoot,
                    StringComparison.OrdinalIgnoreCase))
                alternateRoot = null;
            if (String.IsNullOrWhiteSpace(alternateRoot)) return;

            string archiveOverride = Path.Combine(
                Path.GetFullPath(Environment.CurrentDirectory),
                ".capcut-drafts-test-" + Guid.NewGuid().ToString("N"));
            try {
                string originalRoot = Path.Combine(dir, "original-drafts");
                string roots = Path.Combine(dir, "task-roots");
                string lease = Path.Combine(dir, "runtime", "draft-root-lease.json");
                string setting = Path.Combine(dir, "globalSetting");
                Directory.CreateDirectory(originalRoot);
                WriteTestGlobalSetting(setting, originalRoot);
                var config = AppConfig.CreateDefault();
                config.EnableTaskScopedDraftRoot = true;
                VideoTask oldTask = NewDraftRootTask("attempt-cross-volume-empty");
                var first = new TaskDraftRootLeaseManager(config,
                    new AppLogger(), lease, roots, setting,
                    delegate(int pid) { return false; }, delegate { return false; },
                    archiveOverride);
                DraftRootLeaseRecord oldLease = first.Acquire(oldTask,
                    CancellationToken.None);

                // Marker-only roots are the exact acquire-failed state that
                // previously called Directory.Move(C:, E:) and blocked every
                // later Safe Continue before the new client could launch.
                VideoTask nextTask = NewDraftRootTask("attempt-after-cross-volume-empty");
                var retry = new TaskDraftRootLeaseManager(config,
                    new AppLogger(), lease, roots, setting,
                    delegate(int pid) { return false; }, delegate { return false; },
                    archiveOverride);
                DraftRootLeaseRecord nextLease = retry.Acquire(nextTask,
                    CancellationToken.None);
                Assert(nextLease.LeaseId != oldLease.LeaseId,
                    "cross-volume empty evidence must release the stale lease");
                string evidence = oldLease.DiagnosticRoot;
                string[] evidenceFiles = Directory.Exists(evidence)
                    ? Directory.GetFiles(evidence, "*", SearchOption.AllDirectories)
                    : new string[0];
                Assert(evidenceFiles.Any(path => String.Equals(
                            Path.GetFileName(path), oldLease.JunctionMarkerName,
                            StringComparison.OrdinalIgnoreCase)),
                    "the ownership marker must be copied and verified before the C: source is removed; evidence=" +
                    evidence + "; files=" + String.Join(",", evidenceFiles) +
                    "; dirs=" + String.Join(",", Directory.Exists(archiveOverride)
                        ? Directory.GetDirectories(archiveOverride, "*", SearchOption.AllDirectories)
                        : new string[0]));
                retry.Release(CancellationToken.None);
            } finally {
                if (Directory.Exists(archiveOverride))
                    Directory.Delete(archiveOverride, true);
            }
        });
    }

    private static void TestUnknownStaleDraftLeaseIsBlocked()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string roots = Path.Combine(dir, "task-roots");
            string lease = Path.Combine(dir, "runtime", "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");
            Directory.CreateDirectory(originalRoot);
            WriteTestGlobalSetting(setting, originalRoot);
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            var first = new TaskDraftRootLeaseManager(config,
                new AppLogger(), lease, roots, setting,
                delegate(int pid) { return false; }, delegate { return false; });
            DraftRootLeaseRecord oldLease = first.Acquire(
                NewDraftRootTask("attempt-unknown-old"), CancellationToken.None);
            string unknown = Path.Combine(oldLease.TaskDraftRoot, "unknown-draft");
            Directory.CreateDirectory(unknown);
            File.WriteAllText(Path.Combine(unknown, "draft_meta_info.json"),
                "{\"draft_id\":\"unknown\"}", new UTF8Encoding(false));

            bool blocked = false;
            try {
                var retry = new TaskDraftRootLeaseManager(config,
                    new AppLogger(), lease, roots, setting,
                    delegate(int pid) { return false; }, delegate { return false; });
                retry.Acquire(NewDraftRootTask("attempt-unknown-new"),
                    CancellationToken.None);
            } catch (InvalidOperationException ex) {
                blocked = ex.Message.Contains(
                    "CAPCUT_UNKNOWN_STALE_DRAFT_BLOCKED");
            }
            Assert(blocked && Directory.Exists(unknown),
                "unknown stale drafts must be retained and block automatic recovery");
            first.Release(CancellationToken.None);
        });
    }

    private static void TestLiveDraftLeaseOwnerIsBlocked()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string roots = Path.Combine(dir, "task-roots");
            string lease = Path.Combine(dir, "runtime", "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");
            Directory.CreateDirectory(originalRoot);
            WriteTestGlobalSetting(setting, originalRoot);
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            var first = new TaskDraftRootLeaseManager(config,
                new AppLogger(), lease, roots, setting,
                delegate(int pid) { return false; }, delegate { return false; });
            first.Acquire(NewDraftRootTask("attempt-live-old"),
                CancellationToken.None);
            bool blocked = false;
            try {
                var retry = new TaskDraftRootLeaseManager(config,
                    new AppLogger(), lease, roots, setting,
                    delegate(int pid) { return true; }, delegate { return false; });
                retry.Acquire(NewDraftRootTask("attempt-live-new"),
                    CancellationToken.None);
            } catch (InvalidOperationException ex) {
                blocked = ex.Message.Contains("draft_root_lease_conflict");
            }
            Assert(blocked, "a lease with a live owner must never be reclaimed");
            first.Release(CancellationToken.None);
        });
    }

    private static void TestDelayedCapCutOwnershipPolicy()
    {
        DateTime launch = new DateTime(2026, 8, 8, 10, 0, 0,
            DateTimeKind.Utc);
        var baseline = new HashSet<int> { 100 };
        Assert(!CapCutSessionOwnership.IsOwnedCandidate(100,
                launch.AddMinutes(-5), baseline, launch),
            "a process present before launch must remain user-owned");
        Assert(CapCutSessionOwnership.IsOwnedCandidate(200,
                launch.AddSeconds(30), baseline, launch),
            "a delayed CapCut process created after launch must remain task-owned");
        Assert(!CapCutSessionOwnership.IsOwnedCandidate(300,
                launch.AddSeconds(30), baseline, DateTime.MinValue),
            "no process can be adopted without a recorded batch launch");
    }

    private static void TestTaskDraftCheckpointRepairsRewrittenCardId()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string roots = Path.Combine(dir, "task-roots");
            string previousRoot = Path.Combine(dir, "previous-task-root");
            string lease = Path.Combine(dir, "runtime", "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");
            Directory.CreateDirectory(originalRoot);
            WriteTestDraftIndex(originalRoot, "historical-id",
                Path.Combine(originalRoot, "historical"));

            string checkpointDraft = Path.Combine(previousRoot, "0804");
            Directory.CreateDirectory(checkpointDraft);
            File.WriteAllText(Path.Combine(checkpointDraft,
                "draft_meta_info.json"),
                "{\"draft_id\":\"verified-internal-id\",\"draft_name\":\"0804\"}",
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(checkpointDraft,
                "draft_content.json"), "{\"checkpoint\":true}",
                new UTF8Encoding(false));
            // Reproduce CapCut 2026.7: only the homepage card ID changes;
            // its sole card still names and points at the exact same folder.
            WriteTestDraftIndex(previousRoot, "capcut-rewritten-card-id",
                checkpointDraft);
            WriteTestGlobalSetting(setting, originalRoot);

            var task = NewDraftRootTask("attempt-rewritten-index");
            task.DirectDraftCheckpoint = new DirectDraftCheckpoint {
                State = "style_applied",
                DraftId = "verified-internal-id",
                DraftName = "0804",
                DraftDirectory = checkpointDraft,
                TaskDraftRoot = previousRoot,
                SourceVideoPath = task.VideoPath
            };
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            var manager = new TaskDraftRootLeaseManager(config,
                new AppLogger(), lease, roots, setting);
            DraftRootLeaseRecord acquired = manager.Acquire(task,
                CancellationToken.None);

            var index = new JavaScriptSerializer().DeserializeObject(
                File.ReadAllText(Path.Combine(acquired.TaskDraftRoot,
                    "root_meta_info.json"), Encoding.UTF8))
                as Dictionary<string, object>;
            object[] stores = (object[])index["all_draft_store"];
            var only = (Dictionary<string, object>)stores[0];
            Assert(stores.Length == 1 && Convert.ToString(
                    only["draft_id"]) == "verified-internal-id",
                "the rewritten homepage card ID must be repaired from the sole verified draft checkpoint");
            Assert(Directory.GetDirectories(acquired.TaskDraftRoot)
                    .Count(path => File.Exists(Path.Combine(path,
                        "draft_meta_info.json"))) == 1,
                "repair must preserve exactly one draft and must not create a replacement draft");
            manager.Release(CancellationToken.None);
        });
    }

    private static void TestTaskDraftBootstrapIsQuarantined()
    {
        WithTempDirectory(delegate(string dir) {
            DraftBootstrapFixture fixture = CreateDraftBootstrapFixture(dir,
                false, false);
            var manager = new TaskDraftRootLeaseManager(fixture.Config,
                new AppLogger(), fixture.LeaseFile, fixture.RootsDirectory,
                fixture.SettingFile);
            DraftRootLeaseRecord lease = manager.Acquire(fixture.Task,
                CancellationToken.None);

            string[] active = Directory.GetDirectories(lease.TaskDraftRoot)
                .Where(path => File.Exists(Path.Combine(path,
                    "draft_meta_info.json"))).ToArray();
            Assert(active.Length == 1 && SameTestPath(active[0],
                    fixture.TargetDraft),
                "only the verified source-bound draft may remain active");
            string recycle = Path.Combine(lease.TaskDraftRoot,
                ".recycle_bin");
            Assert(Directory.Exists(recycle) &&
                    Directory.GetDirectories(recycle,
                        "b-*", SearchOption.TopDirectoryOnly).Length == 1,
                "the empty bootstrap draft must be retained in the task recycle area");
            var index = new JavaScriptSerializer().DeserializeObject(
                File.ReadAllText(Path.Combine(lease.TaskDraftRoot,
                    "root_meta_info.json"), Encoding.UTF8))
                as Dictionary<string, object>;
            object[] stores = (object[])index["all_draft_store"];
            Assert(stores.Length == 1 && Convert.ToString(
                    ((Dictionary<string, object>)stores[0])["draft_id"]) ==
                    fixture.CheckpointId,
                "the rebuilt task index must expose only the verified checkpoint");
            manager.Release(CancellationToken.None);
        });
    }

    private static void TestSameRunBootstrapIsQuarantinedBeforeReopen()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string roots = Path.Combine(dir, "task-roots");
            string leaseFile = Path.Combine(dir, "runtime",
                "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");
            Directory.CreateDirectory(originalRoot);
            WriteTestGlobalSetting(setting, originalRoot);
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            VideoTask task = NewDraftRootTask("attempt-same-run-bootstrap");
            var manager = new TaskDraftRootLeaseManager(config,
                new AppLogger(), leaseFile, roots, setting);
            DraftRootLeaseRecord lease = manager.Acquire(task,
                CancellationToken.None);

            string bootstrap = Path.Combine(lease.TaskDraftRoot, "0811");
            string target = Path.Combine(lease.TaskDraftRoot, "0811 (1)");
            Directory.CreateDirectory(bootstrap);
            Directory.CreateDirectory(target);
            string targetId = "same-run-target-id";
            string bootstrapId = "same-run-bootstrap-id";
            string source = Path.GetFullPath(task.VideoPath).Replace("\\", "/");
            File.WriteAllText(Path.Combine(target, "draft_meta_info.json"),
                "{\"draft_id\":\"" + targetId +
                "\",\"draft_name\":\"0811 (1)\",\"tm_duration\":10000000," +
                "\"draft_materials\":[{\"type\":0,\"value\":[{\"file_Path\":\"" +
                source.Replace("\\", "\\\\").Replace("\"", "\\\"") +
                "\"}]}]}", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(bootstrap, "draft_meta_info.json"),
                "{\"draft_id\":\"" + bootstrapId +
                "\",\"draft_name\":\"0811\",\"tm_duration\":0," +
                "\"draft_materials\":[{\"type\":0,\"value\":[]}]}",
                new UTF8Encoding(false));
            WriteTestDraftIndex(lease.TaskDraftRoot,
                new[] { bootstrapId, targetId },
                new[] { bootstrap, target });

            manager.ReconcileActiveDraft(targetId, "0811 (1)", target,
                task.VideoPath);

            Assert(!Directory.Exists(bootstrap) && Directory.Exists(target),
                "the empty same-run bootstrap must be quarantined before first reopen");
            Assert(Directory.GetDirectories(Path.Combine(lease.TaskDraftRoot,
                        ".recycle_bin"), "b-*", SearchOption.TopDirectoryOnly)
                    .Length == 1,
                "the same-run bootstrap must remain recoverable in the task recycle area");
            var index = new JavaScriptSerializer().DeserializeObject(
                File.ReadAllText(Path.Combine(lease.TaskDraftRoot,
                    "root_meta_info.json"), Encoding.UTF8))
                as Dictionary<string, object>;
            object[] stores = (object[])index["all_draft_store"];
            Assert(stores.Length == 1 && Convert.ToString(
                    ((Dictionary<string, object>)stores[0])["draft_id"]) ==
                    targetId,
                "first reopen must expose only the verified source-bound draft");
            manager.Release(CancellationToken.None);
        });
    }

    private static void TestUnknownExtraTaskDraftIsBlocked()
    {
        WithTempDirectory(delegate(string dir) {
            DraftBootstrapFixture fixture = CreateDraftBootstrapFixture(dir,
                true, false);
            bool blocked = false;
            try {
                var manager = new TaskDraftRootLeaseManager(fixture.Config,
                    new AppLogger(), fixture.LeaseFile,
                    fixture.RootsDirectory, fixture.SettingFile);
                manager.Acquire(fixture.Task, CancellationToken.None);
            } catch (InvalidDataException ex) {
                blocked = ex.Message.Contains("CAPCUT_UNKNOWN_EXTRA_DRAFT");
            }
            Assert(blocked,
                "an extra draft with materials must be preserved and blocked");
            Assert(Directory.Exists(fixture.BootstrapDraft),
                "an unknown extra draft must not be moved or deleted");
        });
    }

    private static void TestMultipleSourceBoundTaskDraftsAreBlocked()
    {
        WithTempDirectory(delegate(string dir) {
            DraftBootstrapFixture fixture = CreateDraftBootstrapFixture(dir,
                false, true);
            bool blocked = false;
            try {
                var manager = new TaskDraftRootLeaseManager(fixture.Config,
                    new AppLogger(), fixture.LeaseFile,
                    fixture.RootsDirectory, fixture.SettingFile);
                manager.Acquire(fixture.Task, CancellationToken.None);
            } catch (InvalidDataException ex) {
                blocked = ex.Message.Contains("CAPCUT_MULTIPLE_TASK_DRAFTS");
            }
            Assert(blocked,
                "two drafts containing the current source must never be guessed");
            Assert(Directory.Exists(fixture.TargetDraft) &&
                    Directory.Exists(fixture.BootstrapDraft),
                "identity conflicts must preserve every draft for diagnosis");
        });
    }

    private sealed class DraftBootstrapFixture
    {
        public AppConfig Config;
        public VideoTask Task;
        public string LeaseFile;
        public string RootsDirectory;
        public string SettingFile;
        public string TargetDraft;
        public string BootstrapDraft;
        public string CheckpointId;
    }

    private static DraftBootstrapFixture CreateDraftBootstrapFixture(
        string dir, bool unknownExtra, bool secondSourceMatch)
    {
        string originalRoot = Path.Combine(dir, "original-drafts");
        string roots = Path.Combine(dir, "task-roots");
        string leaseFile = Path.Combine(dir, "runtime",
            "draft-root-lease.json");
        string setting = Path.Combine(dir, "globalSetting");
        Directory.CreateDirectory(originalRoot);
        WriteTestGlobalSetting(setting, originalRoot);
        var config = AppConfig.CreateDefault();
        config.EnableTaskScopedDraftRoot = true;
        VideoTask task = NewDraftRootTask("attempt-bootstrap");
        var first = new TaskDraftRootLeaseManager(config,
            new AppLogger(), leaseFile, roots, setting);
        DraftRootLeaseRecord initial = first.Acquire(task,
            CancellationToken.None);
        first.Release(CancellationToken.None);

        string bootstrap = Path.Combine(initial.TaskDraftRoot, "0804");
        string target = Path.Combine(initial.TaskDraftRoot, "0804 (1)");
        Directory.CreateDirectory(bootstrap);
        Directory.CreateDirectory(target);
        string targetId = "verified-target-id";
        string bootstrapId = "bootstrap-id";
        string source = Path.GetFullPath(task.VideoPath).Replace("\\", "/");
        File.WriteAllText(Path.Combine(target, "draft_meta_info.json"),
            "{\"draft_id\":\"" + targetId +
            "\",\"draft_name\":\"0804 (1)\",\"tm_duration\":12000000," +
            "\"draft_materials\":[{\"type\":0,\"value\":[{\"file_Path\":\"" +
            source.Replace("\\", "\\\\").Replace("\"", "\\\"") +
            "\"}]}]}", new UTF8Encoding(false));
        string bootstrapValue = secondSourceMatch
            ? "[{\"file_Path\":\"" + source.Replace("\\", "\\\\") + "\"}]"
            : (unknownExtra ? "[{\"file_Path\":\"other.mp4\"}]" : "[]");
        File.WriteAllText(Path.Combine(bootstrap,
            "draft_meta_info.json"),
            "{\"draft_id\":\"" + bootstrapId +
            "\",\"draft_name\":\"0804\",\"tm_duration\":" +
            (unknownExtra || secondSourceMatch ? "1000000" : "0") +
            ",\"draft_materials\":[{\"type\":0,\"value\":" +
            bootstrapValue + "}]}", new UTF8Encoding(false));
        WriteTestDraftIndex(initial.TaskDraftRoot,
            new[] { bootstrapId, targetId },
            new[] { bootstrap, target });
        task.DirectDraftCheckpoint = new DirectDraftCheckpoint {
            State = "style_applied",
            DraftId = targetId,
            DraftName = "0804 (1)",
            DraftDirectory = target,
            TaskDraftRoot = initial.TaskDraftRoot,
            DraftRootLeaseId = initial.LeaseId,
            SourceVideoPath = task.VideoPath
        };
        return new DraftBootstrapFixture {
            Config = config,
            Task = task,
            LeaseFile = leaseFile,
            RootsDirectory = roots,
            SettingFile = setting,
            TargetDraft = target,
            BootstrapDraft = bootstrap,
            CheckpointId = targetId
        };
    }

    private static void TestTaskDraftRootStaleAttemptRecovery()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string roots = Path.Combine(dir, "task-roots");
            string lease = Path.Combine(dir, "runtime", "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");
            Directory.CreateDirectory(originalRoot);
            WriteTestGlobalSetting(setting, originalRoot);
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            VideoTask first = NewDraftRootTask("attempt-old");
            first.Id = "stable-local-task";
            var firstManager = new TaskDraftRootLeaseManager(
                config, new AppLogger(), lease, roots, setting,
                delegate(int pid) { return false; }, delegate { return false; });
            DraftRootLeaseRecord oldLease = firstManager.Acquire(first,
                CancellationToken.None);
            string draft = Path.Combine(oldLease.TaskDraftRoot, "0731");
            Directory.CreateDirectory(draft);
            File.WriteAllText(Path.Combine(draft, "draft_meta_info.json"),
                "{\"draft_id\":\"verified-draft\"}", new UTF8Encoding(false));
            WriteTestDraftIndex(oldLease.TaskDraftRoot, "verified-draft", draft);

            VideoTask retry = NewDraftRootTask("attempt-new");
            retry.Id = first.Id;
            retry.HandoffBatchId = "batch-safe-continue";
            retry.HandoffManifestId = "manifest-safe-continue";
            retry.VideoPath = first.VideoPath;
            retry.DirectDraftCheckpoint = new DirectDraftCheckpoint {
                State = "style_applied",
                DraftDirectory = draft,
                DraftId = "verified-draft",
                DraftName = "0731",
                SourceVideoPath = first.VideoPath,
                DraftRootLeaseId = oldLease.LeaseId,
                TaskDraftRoot = oldLease.TaskDraftRoot
            };
            var recovering = new TaskDraftRootLeaseManager(
                config, new AppLogger(), lease, roots, setting,
                delegate(int pid) { return false; }, delegate { return false; });
            DraftRootLeaseRecord reclaimed = recovering.Acquire(retry,
                CancellationToken.None);
            Assert(reclaimed.LeaseId == oldLease.LeaseId &&
                    reclaimed.AttemptId == "attempt-new" &&
                    reclaimed.ManifestId == "manifest-safe-continue",
                "a verified checkpoint must reclaim its stale lease and bind the new attempt");
            Assert(SameTestPath(retry.DirectDraftCheckpoint.TaskDraftRoot,
                    oldLease.TaskDraftRoot),
                "reclaimed checkpoint must remain bound to the original verified task root");
            recovering.Release(CancellationToken.None);
        });
    }

    private static void TestInterruptedTaskDraftRetention()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string roots = Path.Combine(dir, "task-roots");
            string lease = Path.Combine(dir, "runtime",
                "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");
            Directory.CreateDirectory(originalRoot);
            WriteTestGlobalSetting(setting, originalRoot);
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            VideoTask task = NewDraftRootTask("interrupted-attempt");
            var first = new TaskDraftRootLeaseManager(
                config, new AppLogger(), lease, roots, setting);
            DraftRootLeaseRecord acquired = first.Acquire(task,
                CancellationToken.None);
            string interrupted = Path.Combine(acquired.TaskDraftRoot,
                "interrupted-draft");
            Directory.CreateDirectory(interrupted);
            File.WriteAllText(Path.Combine(interrupted,
                "draft_meta_info.json"), "{\"draft_id\":\"interrupted\"}",
                new UTF8Encoding(false));
            first.Touch(TaskState.RecognizingCaptions + ":failed");
            first.Release(CancellationToken.None);

            var retry = new TaskDraftRootLeaseManager(
                config, new AppLogger(), lease, roots, setting);
            DraftRootLeaseRecord retried = retry.Acquire(task,
                CancellationToken.None);
            Assert(Directory.GetDirectories(retried.TaskDraftRoot)
                    .Length == 0,
                "an unverified interrupted draft must not remain active");
            string retainedRoot = Path.Combine(roots,
                "retained-incomplete");
            string[] retained = Directory.GetDirectories(retainedRoot);
            Assert(retained.Length == 1 &&
                Directory.Exists(Path.Combine(retained[0],
                    "interrupted-draft")) &&
                File.Exists(Path.Combine(retained[0],
                    "retained-incomplete.json")),
                "the interrupted draft and its evidence must be retained, not deleted");
            retry.Release(CancellationToken.None);
        });
    }

    private static void TestKnownCapCutFailureCatalog()
    {
        IList<KnownCapCutFailure> items = KnownCapCutFailures.All;
        Assert(items.Count >= 24,
            "the complete historical CapCut incident catalog must be present");
        Assert(items.Select(item => item.Fingerprint)
                .Distinct(StringComparer.Ordinal).Count() == items.Count,
            "CapCut incident fingerprints must be unique");
        Assert(items.All(item => !String.IsNullOrWhiteSpace(item.Trigger) &&
                !String.IsNullOrWhiteSpace(item.Regression)),
            "every known fault must name a deterministic trigger and regression gate");
        Assert(KnownCapCutFailures.Match(
                "draft_root_lease_conflict: another task owns it") ==
                "CAPCUT_DRAFT_ROOT_CONFLICT",
            "draft-root conflict must resolve to its stable incident fingerprint");
        Assert(KnownCapCutFailures.Match(
                "安全继续目标已变化") ==
                "CAPCUT_SAFE_CONTINUE_TARGET_CHANGED",
            "safe-continue target changes must retain the historical fingerprint");
        Assert(KnownCapCutFailures.Match(
                "draft_root_setting_ignored: CapCut still showed historical drafts") ==
                "CAPCUT_DRAFT_ROOT_SETTING_IGNORED",
            "ignored custom-draft settings must retain a stable incident fingerprint");
        Assert(KnownCapCutFailures.Match(
                "draft_checkpoint_index_identity_missing: checkpoint-id") ==
                "CAPCUT_CHECKPOINT_INDEX_SOURCE",
            "checkpoint index source mismatches must retain a stable incident fingerprint");
            Assert(KnownCapCutFailures.Match(
                "CAPCUT_DRAFT_AUDIO_OFFSET: independent audio starts late") ==
                "CAPCUT_DRAFT_AUDIO_OFFSET",
                "persisted audio offset must retain a stable incident fingerprint");
            Assert(KnownCapCutFailures.Match(
                "CAPCUT_MAIN_WINDOW_TRANSITION_TIMEOUT: replacement editor was late") ==
                "CAPCUT_MAIN_WINDOW_TRANSITION_TIMEOUT",
                "editor window transition failures must retain a structured fingerprint");
            Assert(KnownCapCutFailures.Match(
                "CAPCUT_PROCESS_EXITED_DURING_CREATE: process ended") ==
                "CAPCUT_PROCESS_EXITED_DURING_CREATE",
                "process exits during create must remain distinct from window transitions");
    }

    private static void TestRunnerDraftRootLifecycle()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "video.mp4");
            string audio = Path.Combine(dir, "audio.mp3");
            File.WriteAllText(video, "video");
            File.WriteAllText(audio, "audio");
            VideoTask task = NewGroupTask(video, audio, "",
                Path.Combine(dir, "out", "done.mp4"), 1);
            task.Id = "scoped-runner-task";
            task.HandoffBatchId = "batch-runner";
            task.RemotePipelineTaskId = "canonical-runner";
            task.ProductionDate = "7.29";
            task.HandoffManifestId = "manifest-runner";
            task.HandoffAttemptId = "attempt-runner";
            var automation = new ScopedMockAutomation();
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            var runner = new BatchRunner(config, automation,
                new TaskStore(Path.Combine(dir, "tasks.json")),
                new AppLogger(), null, new MockPostProcessor());

            runner.RunBlocking(new List<VideoTask> { task },
                CancellationToken.None);

            Assert(task.GetState() == TaskState.Completed,
                "scoped runner task must complete");
            Assert(automation.IdentitySetBeforeAcquire,
                "task identity must be fixed before acquiring the draft root");
            Assert(automation.AcquireCalls == 1 &&
                automation.ReleaseCalls == 1,
                "each task must acquire and release exactly one draft root");
            Assert(automation.CloseCallsSeenAtRelease > 0,
                "CapCut must be fully closed before the original root is restored");
            Assert(automation.Stages.Contains(TaskState.CreatingProject.ToString()) &&
                automation.Stages.Contains(TaskState.CreatingProject + ":completed") &&
                automation.Stages.Contains(TaskState.Exporting.ToString()) &&
                automation.Stages.Contains(TaskState.Exporting + ":completed"),
                "draft-root lease must persist the task phase before and after every CapCut step");
            Assert(!automation.HasTaskDraftRootLease,
                "no draft-root lease may survive a completed task");
        });
    }

    private static VideoTask NewDraftRootTask(string attempt)
    {
        return new VideoTask {
            Id = "task-" + attempt,
            RemotePipelineTaskId = "canonical-task",
            HandoffBatchId = "batch-729",
            ProductionDate = "7.29",
            HandoffManifestId = "manifest-729",
            HandoffAttemptId = attempt,
            VideoPath = Path.Combine(Path.GetTempPath(), "source-" + attempt + ".mp4")
        };
    }

    private static void WriteTestGlobalSetting(string path, string draftRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string escaped = Path.GetFullPath(draftRoot).Replace("\\", "\\\\");
        File.WriteAllText(path,
            "unrelated=keep\r\ncurrentCustomDraftPath=" + escaped +
            "\r\nlast=keep\r\n", new UTF8Encoding(false));
    }

    private static void WriteTestDraftIndex(string root, string draftId,
        string draftPath)
    {
        WriteTestDraftIndex(root, new[] { draftId },
            new[] { draftPath });
    }

    private static void WriteTestDraftIndex(string root, string[] draftIds,
        string[] draftPaths)
    {
        if (draftIds == null || draftPaths == null ||
            draftIds.Length != draftPaths.Length)
            throw new ArgumentException("test draft index arrays must match");
        object[] stores = draftIds.Select((draftId, index) =>
            (object)new Dictionary<string, object> {
                { "draft_id", draftId },
                { "draft_name", Path.GetFileName(draftPaths[index]) },
                { "draft_fold_path", draftPaths[index] },
                { "draft_root_path", root }
            }).ToArray();
        var value = new Dictionary<string, object> {
            { "all_draft_store", stores },
            { "draft_ids", stores.Length },
            { "root_path", root }
        };
        File.WriteAllText(Path.Combine(root, "root_meta_info.json"),
            new JavaScriptSerializer().Serialize(value),
            new UTF8Encoding(false));
    }

    private static bool SameTestPath(string left, string right)
    {
        return String.Equals(Path.GetFullPath(left).TrimEnd('\\'),
            Path.GetFullPath(right).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void TestLabRerunNameDoesNotGrow()
    {
        WithTempDirectory(delegate(string dir) {
            string original = Path.Combine(dir, "SubtitleStyleLab1.mp4");
            string first = OutputNaming.LabRerunPath(original, "20260726-120000");
            string second = OutputNaming.LabRerunPath(first, "20260726-120100");
            string third = OutputNaming.LabRerunPath(second, "20260726-120200");
            Assert(Path.GetFileName(third) == "SubtitleStyleLab1-lab-rerun-20260726-120200.mp4",
                "lab rerun name must replace, not append, the previous rerun suffix: " + third);
            Assert(third.Length <= 220, "lab rerun path must reserve room for downstream working files");
        });
    }

    private static void TestAudioTimelineVision()
    {
        using (var image = new Bitmap(1200, 800))
        using (var graphics = Graphics.FromImage(image)) {
            graphics.Clear(Color.FromArgb(18, 20, 23));
            int zero = 85;
            // A wide but shallow teal video title strip must not be treated as
            // an independent narration lane.
            using (var videoStrip = new SolidBrush(Color.FromArgb(0, 77, 82)))
                graphics.FillRectangle(videoStrip, zero, 590, 520, 14);
            // The real failure placed the first narration lane around 78% of
            // the editor height, above the old 84%-92% scan band.
            using (var lane = new SolidBrush(Color.FromArgb(14, 48, 88)))
                graphics.FillRectangle(lane, zero, 620, 520, 40);
            using (var wave = new Pen(Color.FromArgb(43, 130, 215), 2)) {
                for (int x = zero + 4; x < 600; x += 8)
                    graphics.DrawLine(wave, x, 624, x, 651);
            }

            AudioTimelineEvidence first = AudioTimelineVision.Analyze(image, zero);
            Assert(first.LaneCount == 1, "the first narration lane must be detected exactly once");
            Assert(first.IsAligned, "the first narration lane must be aligned to timeline zero");

            using (var secondLane = new SolidBrush(Color.FromArgb(14, 48, 88)))
                graphics.FillRectangle(secondLane, zero, 674, 520, 40);
            using (var secondWave = new Pen(Color.FromArgb(43, 130, 215), 2)) {
                for (int x = zero + 4; x < 600; x += 8)
                    graphics.DrawLine(secondWave, x, 678, x, 705);
            }
            AudioTimelineEvidence duplicate = AudioTimelineVision.Analyze(image, zero);
            Assert(duplicate.LaneCount == 2,
                "parallel narration lanes must be reported as duplicates, not accepted as one lane");
        }
    }

    private static void TestCaptionRecognitionButtonVision()
    {
        using (var image = new Bitmap(2048, 1152))
        using (var graphics = Graphics.FromImage(image)) {
            graphics.Clear(Color.FromArgb(17, 20, 23));
            // Current CapCut layout: the action moved far away from the stale
            // calibrated point but remains the unique wide cyan control in the
            // intelligent-caption panel.
            using (var buttonBrush = new SolidBrush(Color.FromArgb(18, 186, 210)))
                graphics.FillRectangle(buttonBrush, 312, 575, 356, 46);
            // Small cyan toolbar glyphs must not be mistaken for the action.
            using (var iconBrush = new SolidBrush(Color.FromArgb(20, 180, 205))) {
                graphics.FillRectangle(iconBrush, 150, 400, 22, 22);
                graphics.FillRectangle(iconBrush, 820, 460, 28, 28);
            }

            Rectangle button;
            string diagnostic;
            Assert(CaptionRecognitionButtonVision.TryFind(image, out button, out diagnostic),
                "dynamic caption button should be found: " + diagnostic);
            Assert(button.Left >= 300 && button.Right <= 680 &&
                button.Top >= 570 && button.Bottom <= 630,
                "dynamic caption button bounds are incorrect: " + button);
            Assert(CaptionRecognitionButtonVision.IsStable(button,
                    new Rectangle(button.X + 3, button.Y + 2, button.Width, button.Height), 8),
                "small panel repaint jitter should remain stable");

            // Two equally large action-looking buttons are ambiguous and must
            // fail closed rather than clicking a random control.
            using (var secondButton = new SolidBrush(Color.FromArgb(18, 186, 210)))
                graphics.FillRectangle(secondButton, 312, 700, 356, 46);
            Assert(!CaptionRecognitionButtonVision.TryFind(image, out button, out diagnostic),
                "ambiguous wide cyan controls must not be clicked");
        }
    }

    private static void TestCaptionRecognitionProgressPolicy()
    {
        Assert(CaptionRecognitionStatePolicy.ContainsProgressMarker(
                "字幕识别中…45%"),
            "dynamic Chinese percentage text must prove recognition is running");
        Assert(CaptionRecognitionStatePolicy.ContainsProgressMarker(
                "字 幕 识 别 中 ． 45 ％"),
            "OCR-spaced Chinese progress text must be normalized");
        Assert(CaptionRecognitionStatePolicy.ContainsProgressMarker(
                "Generating captions... 12%"),
            "localized dynamic progress text must be accepted");
        Assert(CaptionRecognitionStatePolicy.IsCommandAccepted(
                true, false, false, 0),
            "a progress dialog must override a start button still painted behind it");
        Assert(CaptionRecognitionStatePolicy.IsCommandAccepted(
                false, true, false, 0),
            "a newly-created subtitle track must prove command acceptance");
        Assert(CaptionRecognitionStatePolicy.IsCommandAccepted(
                false, false, true, 0),
            "a completion marker must prove command acceptance");
        Assert(CaptionRecognitionStatePolicy.IsCommandAccepted(
                false, false, false, 2),
            "two absent-button frames remain a valid secondary signal");
        Assert(!CaptionRecognitionStatePolicy.IsCommandAccepted(
                false, false, false, 0),
            "an unchanged button without progress evidence must still fail closed");
    }

    private static void TestInteractionVisionFailureReplay()
    {
        string logDirectory = Path.Combine(Environment.CurrentDirectory, "bin", "logs");
        string importedOnlyPath = Path.Combine(logDirectory, "capcut-import-timeout-AudioItem.png");
        if (File.Exists(importedOnlyPath)) {
            using (var importedOnly = new Bitmap(importedOnlyPath)) {
                AudioTimelineEvidence evidence = AudioTimelineVision.Analyze(
                    importedOnly, (int)Math.Round(importedOnly.Width * 0.071));
                Assert(evidence.ScanArea.Width > 0 && evidence.ScanArea.Height > 0,
                    "saved imported-only screenshot must remain readable as diagnostic evidence");
                // This production screenshot proved that blue pixels can be
                // ambiguous before audio placement. Its LaneCount is no
                // longer trusted: persisted draft tracks are the only gate.
            }
        }
        string beforeAudioPath = Path.Combine(logDirectory, "capcut-after-audio-import.png");
        if (File.Exists(beforeAudioPath)) {
            using (var beforeAudio = new Bitmap(beforeAudioPath)) {
                AudioTimelineEvidence evidence = AudioTimelineVision.Analyze(
                    beforeAudio, (int)Math.Round(beforeAudio.Width * 0.071));
                Assert(evidence.LaneCount == 0,
                    "saved pre-placement screenshot must not count the main video as narration; lanes=" +
                    evidence.LaneCount);
            }
        }
        string audioPath = Path.Combine(logDirectory, "capcut-after-audio-timeline.png");
        if (File.Exists(audioPath)) {
            using (var audio = new Bitmap(audioPath)) {
                AudioTimelineEvidence evidence = AudioTimelineVision.Analyze(
                    audio, (int)Math.Round(audio.Width * 0.071));
                Assert(evidence.LaneCount >= 1,
                    "saved post-placement screenshot must find the independent narration lane; lanes=" +
                    evidence.LaneCount);
            }
        }

        string captionPath = Path.Combine(logDirectory, "capcut-after-auto-caption.png");
        if (File.Exists(captionPath)) {
            using (var caption = new Bitmap(captionPath)) {
                Rectangle button;
                string diagnostic;
                Assert(CaptionRecognitionButtonVision.TryFind(caption, out button, out diagnostic),
                    "saved caption screenshot must find the visible start button: " + diagnostic);
                Assert(button.Width >= 45 && button.Left < caption.Width / 2,
                    "saved caption screenshot button must be in the left panel: " + button);
            }
        }

        string recognitionProgressPath = Path.Combine(logDirectory,
            "capcut-recognition-command-not-accepted.png");
        if (File.Exists(recognitionProgressPath)) {
            using (var recognitionProgress = new Bitmap(recognitionProgressPath)) {
                Assert(recognitionProgress.Width > 0 && recognitionProgress.Height > 0,
                    "the real 45% screenshot must remain readable");
                Assert(CaptionRecognitionStatePolicy.ContainsProgressMarker(
                        "字 幕 识 别 中 ． 45 ％"),
                    "the OCR text observed in the real screenshot must normalize to progress");
                Assert(CaptionRecognitionStatePolicy.IsCommandAccepted(
                        true, false, false, 0),
                    "the real progress dialog must override the still-painted start button");
            }
        }
    }

    private static void TestDraftCardVision()
    {
        using (var image = new Bitmap(1200, 800))
        using (var graphics = Graphics.FromImage(image)) {
            graphics.Clear(Color.FromArgb(16, 29, 31));
            // First row, first card: a newly saved all-black thumbnail with no
            // UIA metadata.
            using (var texture = new SolidBrush(Color.Black)) graphics.FillRectangle(texture, 95, 240, 220, 62);
            // A louder second column in the same row must not steal the
            // left-most-card result.
            using (var red = new SolidBrush(Color.Red)) graphics.FillRectangle(red, 360, 240, 55, 62);
            using (var cyan = new SolidBrush(Color.Cyan)) graphics.FillRectangle(cyan, 415, 240, 55, 62);
            // The exact production failure: a much stronger thumbnail in the
            // second row at the same X.  Row strength must never override the
            // top-most complete row selected from root_meta_info ordering.
            using (var yellow = new SolidBrush(Color.Yellow)) graphics.FillRectangle(yellow, 95, 335, 220, 78);
            using (var magenta = new SolidBrush(Color.Magenta)) graphics.FillRectangle(magenta, 360, 335, 110, 78);
            Rectangle card;
            string diagnostic;
            Assert(DraftCardVision.TryFindFirstCard(image, out card, out diagnostic), "vision should find first card: " + diagnostic);
            Assert(card.Left >= 80 && card.Left <= 180 && card.Right < 350 && card.Top >= 235 && card.Top <= 305,
                "vision returned unexpected first-card bounds: " + card);
            Assert(card.Height == 7, "vision must return the observed in-thumbnail sample band, not a guessed card height");
            int clickX = card.Left + (card.Width / 2);
            int clickY = card.Top + (card.Height / 2);
            Assert(clickY >= 240 && clickY <= 302, "vision click point must stay inside the top-row thumbnail: " + card);
            Assert(DraftCardVision.IsClickInsideEvidence(card, clickX, clickY),
                "click must stay inside the exact evidence band");
            Assert(!DraftCardVision.IsClickInsideEvidence(card, clickX, card.Bottom + 5),
                "a downward-offset click must be rejected");
            Assert(DraftCardVision.IsStable(card, new Rectangle(card.Left + 4, card.Top + 3, card.Width + 2, 7), 10),
                "small layout jitter should be considered stable");
            Assert(!DraftCardVision.IsStable(card, new Rectangle(card.Left, card.Top + 30, card.Width, 7), 10),
                "a reflowed draft region must not be considered stable");
            Assert(!DraftCardVision.IsHomeReady(true, false),
                "a blank CapCut QML shell must not be treated as a ready home page");
            Assert(!DraftCardVision.IsHomeReady(false, true),
                "a stray thumbnail-like region without the home action must not be clickable");
            Assert(DraftCardVision.IsHomeReady(true, true),
                "home page is ready only when the create action and first draft evidence both exist");
            Assert(DraftCardVision.PhysicalOpenClickCount == 1,
                "opening a draft card must issue exactly one physical mouse click");
            Assert(DraftCardVision.RequiredStableFrames == 2,
                "root-index-locked first card must not wait for a third QML animation frame");
            Assert(DraftCardVision.RequiredExactTitleStableFrames == 3,
                "exact-title draft location must survive delayed QML reflow before clicking");

            var twoFrameTracker = new DraftCardVision.TopRowStabilityTracker();
            Rectangle twoFrameAccepted;
            string twoFrameDiagnostic;
            Assert(!twoFrameTracker.Observe(card, 10, DraftCardVision.RequiredStableFrames,
                    out twoFrameAccepted, out twoFrameDiagnostic),
                "the first observed frame must not be clicked immediately");
            Assert(twoFrameTracker.Observe(card, 10, DraftCardVision.RequiredStableFrames,
                    out twoFrameAccepted, out twoFrameDiagnostic),
                "two stable root-index-locked frames must be accepted before a lower row can steal detection");

            var exactTitleTracker = new DraftCardVision.ExactTitleStabilityTracker();
            Rectangle exactAccepted;
            string exactDiagnostic;
            Rectangle preFocusCard = new Rectangle(254, 409, 94, 94);
            Rectangle postFocusCard = new Rectangle(254, 535, 94, 94);
            Assert(!exactTitleTracker.Observe(preFocusCard, 10,
                    DraftCardVision.RequiredExactTitleStableFrames,
                    out exactAccepted, out exactDiagnostic),
                "the first exact-title observation must not be clicked");
            Assert(!exactTitleTracker.Observe(postFocusCard, 10,
                    DraftCardVision.RequiredExactTitleStableFrames,
                    out exactAccepted, out exactDiagnostic),
                "a delayed QML reflow must reset exact-title stability instead of failing");
            Assert(!exactTitleTracker.Observe(postFocusCard, 10,
                    DraftCardVision.RequiredExactTitleStableFrames,
                    out exactAccepted, out exactDiagnostic),
                "the reflowed position must remain stable for three frames");
            Assert(exactTitleTracker.Observe(postFocusCard, 10,
                    DraftCardVision.RequiredExactTitleStableFrames,
                    out exactAccepted, out exactDiagnostic) &&
                    exactAccepted == postFocusCard,
                "the exact-title card must be accepted only after three stable post-reflow frames");

            var uniqueTracker = new DraftCardVision.UniqueCardStabilityTracker();
            Rectangle uniqueAccepted;
            string uniqueDiagnostic;
            Assert(!uniqueTracker.Observe(preFocusCard, 5, 3,
                    out uniqueAccepted, out uniqueDiagnostic),
                "the unique task-scoped draft must wait for layout stability");
            Assert(!uniqueTracker.Observe(postFocusCard, 5, 3,
                    out uniqueAccepted, out uniqueDiagnostic),
                "y=408 to y=534 must be treated as QML reflow, not a second row");
            Assert(uniqueDiagnostic.Contains("unique-card QML reflow") &&
                    !uniqueDiagnostic.Contains("rejected lower row"),
                "task-scoped locator must explain reflow without row rejection: " + uniqueDiagnostic);
            Assert(!uniqueTracker.Observe(postFocusCard, 5, 3,
                    out uniqueAccepted, out uniqueDiagnostic),
                "the reflowed unique card must remain stable for three frames");
            Assert(uniqueTracker.Observe(postFocusCard, 5, 3,
                    out uniqueAccepted, out uniqueDiagnostic) &&
                    uniqueAccepted == postFocusCard,
                "the unique card must be accepted at its final stable position");

            DraftCardVision.Detection detection;
            Assert(DraftCardVision.TryFindFirstCardDetailed(image, out detection, out diagnostic),
                "detailed detection should retain the top-row thumbnail bounds");
            Assert(detection.Thumbnail.Height > detection.Evidence.Height,
                "the actual click target must be the full thumbnail, not its thin evidence band");
            int thumbnailClickX = detection.Thumbnail.Left + detection.Thumbnail.Width / 2;
            int thumbnailClickY = detection.Thumbnail.Top + detection.Thumbnail.Height / 2;
            Assert(DraftCardVision.IsClickInsideThumbnail(detection.Thumbnail,
                    thumbnailClickX, thumbnailClickY),
                "the thumbnail-centre click must stay inside the full observed card");
            Assert(thumbnailClickY > detection.Evidence.Bottom,
                "the production click must move away from the unsafe top evidence band");
            using (var cover = new Bitmap(90, 160))
            using (var coverGraphics = Graphics.FromImage(cover)) {
                coverGraphics.Clear(Color.Black);
                double firstScore, nextScore;
                string coverDiagnostic;
                Assert(DraftCardVision.TryVerifyFirstCardCover(image, detection, cover,
                        out firstScore, out nextScore, out coverDiagnostic),
                    "the black first-row card should uniquely match the black target cover: " + coverDiagnostic);
                Assert(firstScore > nextScore,
                    "target cover must score above later cards in the same row");
            }

            // Task-scoped mode has no row semantics.  A stronger unrelated page
            // component may appear above the one real draft; the target cover
            // must locate the lower card directly.
            using (var uniquePage = new Bitmap(1200, 900))
            using (var uniqueGraphics = Graphics.FromImage(uniquePage))
            using (var uniqueCover = new Bitmap(90, 160)) {
                uniqueGraphics.Clear(Color.FromArgb(16, 29, 31));
                uniqueGraphics.FillRectangle(Brushes.Blue, 95, 250, 220, 60);
                uniqueGraphics.FillRectangle(Brushes.Yellow, 95, 410, 220, 76);
                using (var cg = Graphics.FromImage(uniqueCover))
                    cg.Clear(Color.Yellow);
                DraftCardVision.Detection uniqueDetection;
                double uniqueScore;
                string uniqueCoverDiagnostic;
                Assert(DraftCardVision.TryFindUniqueTaskCardByCover(
                        uniquePage, uniqueCover, out uniqueDetection,
                        out uniqueScore, out uniqueCoverDiagnostic),
                    "task-scoped cover locator must find its unique card: " +
                    uniqueCoverDiagnostic);
                Assert(uniqueDetection.Thumbnail.Top >= 400,
                    "task-scoped locator must choose the matching lower card without row semantics: " +
                    uniqueDetection.Thumbnail);
            }

            // Regression for the 2026-09-18 direct-style reopen failure: the
            // homepage had loaded and the only task draft was visibly present,
            // but its charcoal cover never crossed the old absolute-brightness
            // threshold. Candidate discovery must be relative to the page
            // background, then still require a unique target-cover match.
            using (var darkPage = new Bitmap(1200, 900))
            using (var darkPageGraphics = Graphics.FromImage(darkPage))
            using (var darkCover = new Bitmap(90, 144)) {
                darkPageGraphics.Clear(Color.FromArgb(9, 10, 10));
                using (var coverGraphics = Graphics.FromImage(darkCover)) {
                    coverGraphics.Clear(Color.FromArgb(22, 23, 25));
                    using (var darkPanel = new SolidBrush(Color.FromArgb(14, 16, 18)))
                        coverGraphics.FillRectangle(darkPanel, 5, 10, 80, 54);
                    using (var midPanel = new SolidBrush(Color.FromArgb(34, 36, 38)))
                        coverGraphics.FillRectangle(midPanel, 14, 25, 22, 36);
                    using (var accent = new SolidBrush(Color.FromArgb(45, 43, 39)))
                        coverGraphics.FillRectangle(accent, 58, 15, 12, 49);
                    using (var text = new SolidBrush(Color.FromArgb(47, 45, 41))) {
                        coverGraphics.FillRectangle(text, 8, 92, 50, 4);
                        coverGraphics.FillRectangle(text, 8, 101, 68, 5);
                        coverGraphics.FillRectangle(text, 8, 112, 42, 4);
                    }
                }
                // The second lower-page component is unrelated. It should not
                // steal the result merely because it is brighter.
                using (var unrelated = new SolidBrush(Color.FromArgb(38, 20, 18)))
                    darkPageGraphics.FillRectangle(unrelated, 360, 470, 120, 122);
                darkPageGraphics.DrawImage(darkCover,
                    new Rectangle(96, 460, 124, 134));

                Assert(darkCover.GetPixel(1, 1).R > 5 &&
                        darkCover.GetPixel(1, 1).R < 52,
                    "dark-cover regression fixture must stay below the old absolute-colour threshold");
                DraftCardVision.Detection darkDetection;
                double darkScore;
                string darkDiagnostic;
                Assert(DraftCardVision.TryFindUniqueTaskCardByCoverInLowerDraftArea(
                        darkPage, darkCover, out darkDetection, out darkScore,
                        out darkDiagnostic),
                    "background-relative locator must find a low-contrast charcoal draft cover: " +
                    darkDiagnostic);
                Assert(darkDetection.Thumbnail.Left < 260 &&
                        darkDetection.Thumbnail.Top >= 378 &&
                        darkDetection.Thumbnail.Top <= 500 &&
                        darkDetection.Thumbnail.Width >= 90,
                    "dark-cover locator returned an unsafe or unrelated card: " +
                    darkDetection.Thumbnail + "; " + darkDiagnostic);
            }

            // Reused source videos create visually identical sibling-language
            // drafts. The root index fixes the target at card one and the editor
            // identity gate validates it after opening, so duplicate covers must
            // not force every such task to fail once.
            using (var duplicatePage = new Bitmap(1200, 800))
            using (var duplicateGraphics = Graphics.FromImage(duplicatePage))
            using (var duplicateCover = new Bitmap(90, 160)) {
                duplicateGraphics.Clear(Color.FromArgb(16, 29, 31));
                duplicateGraphics.FillRectangle(Brushes.Black, 95, 240, 220, 62);
                duplicateGraphics.FillRectangle(Brushes.Black, 360, 240, 220, 62);
                using (var coverGraphics = Graphics.FromImage(duplicateCover)) coverGraphics.Clear(Color.Black);
                DraftCardVision.Detection duplicateDetection;
                Assert(DraftCardVision.TryFindFirstCardDetailed(duplicatePage, out duplicateDetection,
                        out diagnostic), "duplicate-cover row should still be detected: " + diagnostic);
                double firstScore, siblingScore;
                string coverDiagnostic;
                Assert(DraftCardVision.TryValidateFirstCardCover(duplicatePage, duplicateDetection,
                        duplicateCover, out firstScore, out siblingScore, out coverDiagnostic),
                    "identical sibling covers must remain a valid first-card plausibility check: " +
                    coverDiagnostic);
                Assert(Math.Abs(firstScore - siblingScore) < 0.0005,
                    "duplicate-cover fixture must exercise equal sibling scores");
            }

            var tracker = new DraftCardVision.TopRowStabilityTracker();
            Rectangle accepted;
            string trackingDiagnostic;
            Rectangle topRow = new Rectangle(95, 260, 220, 7);
            Rectangle lowerRow = new Rectangle(95, 355, 220, 7);
            Assert(!tracker.Observe(topRow, 10, 3, out accepted, out trackingDiagnostic),
                "one top-row observation is not yet stable");
            Assert(!tracker.Observe(lowerRow, 10, 3, out accepted, out trackingDiagnostic),
                "a later lower row must never replace the already-seen top row");
            Assert(trackingDiagnostic.Contains("rejected lower row"),
                "lower-row rejection must be diagnosable");
            Assert(!tracker.Observe(topRow, 10, 3, out accepted, out trackingDiagnostic),
                "the top row still needs its third stable frame");
            Assert(tracker.Observe(topRow, 10, 3, out accepted, out trackingDiagnostic) &&
                    accepted.Top == topRow.Top,
                "three top-row frames must accept the top row, never row two");
        }

        string replayPath = Environment.GetEnvironmentVariable("CAPCUT_DRAFT_CARD_SCREENSHOT");
        if (!String.IsNullOrWhiteSpace(replayPath) && File.Exists(replayPath)) {
            using (var replay = new Bitmap(replayPath)) {
                string expectedTitle = Environment.GetEnvironmentVariable("CAPCUT_DRAFT_TITLE");
                if (!String.IsNullOrWhiteSpace(expectedTitle)) {
                    DraftTitleOcrMatch titleMatch;
                    string titleDiagnostic;
                    string expectedCoverPath = Environment.GetEnvironmentVariable("CAPCUT_DRAFT_COVER");
                    bool exactTitleFound = DraftTitleOcr.TryFindUnique(replayPath,
                        expectedTitle, out titleMatch, out titleDiagnostic);
                    if (exactTitleFound) {
                        Assert(titleMatch.Bounds.Top > (int)(replay.Height * 0.35),
                            "exact draft title OCR must be below the More tools/header region: " +
                            titleMatch.Bounds + "; " + titleDiagnostic);
                    }
                    Assert(exactTitleFound || (!String.IsNullOrWhiteSpace(expectedCoverPath) &&
                            File.Exists(expectedCoverPath)),
                        "saved homepage needs exact OCR or a verified cover fallback: " +
                        titleDiagnostic);
                    if (!String.IsNullOrWhiteSpace(expectedCoverPath) && File.Exists(expectedCoverPath)) {
                        using (var expectedCover = new Bitmap(expectedCoverPath)) {
                            if (exactTitleFound) {
                                Rectangle ocrThumbnail;
                                double ocrCoverScore;
                                string ocrThumbnailDiagnostic;
                                Assert(DraftTitleOcr.TryResolveThumbnailAboveTitle(replay,
                                        titleMatch.Bounds, expectedCover, out ocrThumbnail,
                                        out ocrCoverScore, out ocrThumbnailDiagnostic),
                                    "OCR title must resolve its target-cover thumbnail: " +
                                    ocrThumbnailDiagnostic);
                                int ocrClickY = ocrThumbnail.Top + ocrThumbnail.Height / 2;
                                Assert(ocrClickY > (int)(replay.Height * 0.35),
                                    "OCR-anchored click must stay in the draft grid, never the toolbar/header: " +
                                    ocrThumbnail);
                            } else {
                                DraftCardVision.Detection lowerDetection;
                                double lowerScore;
                                string lowerDiagnostic;
                                Assert(DraftCardVision.TryFindUniqueTaskCardByCoverInLowerDraftArea(
                                        replay, expectedCover, out lowerDetection,
                                        out lowerScore, out lowerDiagnostic),
                                    "tiny-title homepage must locate the task cover only in the lower Drafts area: " +
                                    lowerDiagnostic + "; OCR=" + titleDiagnostic);
                                Assert(lowerDetection.Thumbnail.Top >= (int)(replay.Height * 0.42),
                                    "lower-area fallback escaped into the AI feature strip: " +
                                    lowerDetection.Thumbnail + "; " + lowerDiagnostic);
                            }
                        }
                    }
                }
                if (String.IsNullOrWhiteSpace(expectedTitle)) {
                Rectangle replayCard;
                string replayDiagnostic;
                Assert(DraftCardVision.TryFindFirstCard(replay, out replayCard, out replayDiagnostic),
                    "saved CapCut homepage should locate its first draft: " + replayDiagnostic);
                Assert(replayCard.Left < (int)(replay.Width * 0.15),
                    "saved homepage replay selected a later colourful card instead of the first card: " +
                    replayCard + "; " + replayDiagnostic);
                // In the current 2560x1440 CapCut home layout the first draft
                // row begins around y=534 and the second around y=687.  The old
                // 37% threshold (y=532) sat inside the first card itself and
                // incorrectly rejected a valid first-row result.
                Assert(replayCard.Top < (int)(replay.Height * 0.46),
                    "saved homepage replay selected the second row instead of the top row: " +
                    replayCard + "; " + replayDiagnostic);
                int replayX = replayCard.Left + replayCard.Width / 2;
                int replayY = replayCard.Top + replayCard.Height / 2;
                Assert(DraftCardVision.IsClickInsideEvidence(replayCard, replayX, replayY),
                    "saved homepage click point left the observed first-card evidence");

                string replayCoverPath = Environment.GetEnvironmentVariable("CAPCUT_DRAFT_COVER");
                if (!String.IsNullOrWhiteSpace(replayCoverPath) && File.Exists(replayCoverPath)) {
                    DraftCardVision.Detection replayDetection;
                    Assert(DraftCardVision.TryFindFirstCardDetailed(replay, out replayDetection,
                            out replayDiagnostic),
                        "saved homepage should expose detailed first-row candidates");
                    using (var replayCover = new Bitmap(replayCoverPath)) {
                        double firstScore, nextScore;
                        string coverDiagnostic;
                        Assert(DraftCardVision.TryVerifyFirstCardCover(replay, replayDetection,
                                replayCover, out firstScore, out nextScore, out coverDiagnostic),
                            "real 0728 target cover must uniquely match the homepage first card: " +
                            coverDiagnostic);

                        DraftCardVision.Detection taskScopedDetection;
                        double taskScopedScore;
                        string taskScopedDiagnostic;
                        Assert(DraftCardVision.TryFindUniqueTaskCardByCover(replay,
                                replayCover, out taskScopedDetection,
                                out taskScopedScore, out taskScopedDiagnostic),
                            "real task-scoped homepage must locate its unique draft by cover across the full draft area: " +
                            taskScopedDiagnostic);
                    }
                }
                }
            }
        }
    }

    private static void TestDirectDraftCheckpointLaunchPolicy()
    {
        Assert(!BatchRunner.ShouldLaunchEditorBeforeTask(false, true, false),
            "checkpoint resume must not pre-launch a generic CapCut editor");
        Assert(!BatchRunner.ShouldLaunchEditorBeforeTask(true, false, false),
            "validated temporary export recovery must not launch CapCut");
        Assert(!BatchRunner.ShouldLaunchEditorBeforeTask(false, false, true),
            "an already available owned editor must not be launched twice");
        Assert(BatchRunner.ShouldLaunchEditorBeforeTask(false, false, false),
            "a normal fresh task must still launch CapCut");
    }

    private static void TestEditorHintDismissal()
    {
        Func<long,long,bool,bool,string,string,double,double,bool,string,bool> allowed =
            CapCutImportDialogPolicy.CanDismissEditorHint;
        Assert(allowed(100,200,true,true,"CapCut","Qt622QWindowIcon",40000,2000000,true,
            "Edit Pilot 开启快捷编辑模式 关闭 下一步"), "known Chinese hint accepted");
        Assert(allowed(100,200,true,true,"CapCut","Qt622QWindowIcon",40000,2000000,true,
            "Edit Pilot Close Next"), "known English hint accepted");
        Assert(allowed(100,200,true,true,"CapCut","Qt622QWindowIcon",40000,2000000,true,
            "开 启 恢 壘 辑 式 可 画 咖 臨 选 中 片 段 快 发 起 对 话 关 闭"),
            "actual incident popup OCR accepted without guessing unreadable glyphs");
        Assert(!allowed(100,200,true,true,"CapCut","Qt622QWindowIcon",40000,2000000,true,
            "开启编辑 关闭"), "generic editing notice rejected");
        Assert(!allowed(100,100,true,true,"CapCut","Qt622QWindowIcon",40000,2000000,true,"Edit Pilot Close Next"), "editor itself never closed");
        Assert(!allowed(100,200,false,true,"CapCut","Qt622QWindowIcon",40000,2000000,true,"Edit Pilot Close Next"), "foreign owner rejected");
        Assert(!allowed(100,200,true,false,"CapCut","Qt622QWindowIcon",40000,2000000,true,"Edit Pilot Close Next"), "hung popup rejected");
        Assert(!allowed(100,200,true,true,"CapCut","#32770",40000,2000000,true,"Edit Pilot Close Next"), "file dialog rejected");
        Assert(!allowed(100,200,true,true,"Open","Qt622QWindowIcon",40000,2000000,true,"Edit Pilot Close Next"), "other title rejected");
        Assert(!allowed(100,200,true,true,"CapCut","Qt622QWindowIcon",1900000,2000000,true,"Edit Pilot Close Next"), "full editor-sized surface rejected");
        Assert(!allowed(100,200,true,true,"CapCut","Qt622QWindowIcon",40000,2000000,false,"Edit Pilot Close Next"), "out of editor bounds rejected");
        Assert(!allowed(100,200,true,true,"CapCut","Qt622QWindowIcon",40000,2000000,true,"Close Next"), "generic popup rejected");
        Assert(!allowed(100,200,true,true,"CapCut","Qt622QWindowIcon",40000,2000000,true,"Edit Pilot Next"), "missing dismiss control rejected");
        Assert(!allowed(100,200,true,true,"CapCut","Qt622QWindowIcon",40000,2000000,true,"Edit Pilot Close Next Error"), "hard error rejected");
    }

    private static void TestCapCutHomeShellPolicy()
    {
        Assert(CapCutHomeShellPolicy.Classify("HomePageX_QMLTYPE_171") == CapCutWindowRole.Home,
            "CapCut HomePageX class must be classified as homepage");
        Assert(CapCutHomeShellPolicy.Classify("MainWindow_QMLTYPE_484") == CapCutWindowRole.Editor,
            "CapCut MainWindow class must be classified as editor");
        Assert(CapCutHomeShellPolicy.Classify("AIShortsWebViewWindow_QMLTYPE_428") ==
                CapCutWindowRole.Transition,
            "CapCut AI shorts transition surface must never bind as the draft editor");
        Assert(!CapCutHomeShellPolicy.IsHome("MainWindow_QMLTYPE_484", true),
            "cyan pixels inside a MainWindow editor must never turn it back into a homepage");
        Assert(CapCutHomeShellPolicy.IsHome("UnknownQmlWindow", true),
            "unknown classes may still use independent homepage evidence");
        Assert(CapCutWindowStateClassifier.Classify(new CapCutWindowEvidence {
                Available = true,
                ClassName = "MainWindow_QMLTYPE_484",
                CyanAtCreateProjectCoordinate = true
            }) == CapCutUiState.Editor,
            "blue/cyan video pixels must never turn an editor into the homepage");
        Assert(CapCutWindowStateClassifier.Classify(new CapCutWindowEvidence {
                Available = true,
                ClassName = "UnknownQmlWindow",
                ExactCreateProjectControl = true,
                ExactExportControl = true
            }) == CapCutUiState.Editor,
            "editor evidence must win when home and editor surfaces coexist");
        Assert(CapCutWindowStateClassifier.Classify(new CapCutWindowEvidence {
                Available = true,
                ClassName = "UnknownQmlWindow",
                ExactCreateProjectControl = true
            }) == CapCutUiState.Home,
            "unknown windows require an exact semantic control to become home");
        Assert(CapCutWindowStateClassifier.Classify(new CapCutWindowEvidence {
                Available = true,
                ClassName = "UnknownQmlWindow",
                CyanAtCreateProjectCoordinate = true
            }) == CapCutUiState.Ambiguous,
            "colour-only evidence must remain ambiguous");
        Assert(CapCutHomeShellPolicy.CanBindEditor(new IntPtr(100), new IntPtr(200), true, false, true),
            "a distinct visible non-home window with matching task identity may bind as the editor");
        Assert(!CapCutHomeShellPolicy.CanBindEditor(new IntPtr(100), new IntPtr(100), true, false, true),
            "the homepage handle must never bind as the editor");
        Assert(CapCutHomeShellPolicy.CanBindEditor(new IntPtr(100), new IntPtr(100),
            true, false, true, true),
            "an in-place homepage-to-editor transition may bind the same HWND after editor role confirmation");
        Assert(!CapCutHomeShellPolicy.CanBindEditor(new IntPtr(100), new IntPtr(200), true, true, true),
            "a visible homepage shell must not bind as the editor");
        Assert(!CapCutHomeShellPolicy.CanBindEditor(new IntPtr(100), new IntPtr(200), false, false, true),
            "a temporarily hung editor must wait before binding");
        Assert(CapCutHomeShellPolicy.EditorInitializationTimeoutSeconds(15) == 45,
            "cold editor initialization must no longer inherit the old 15-second ceiling");
        Assert(CapCutHomeShellPolicy.EditorInitializationTimeoutSeconds(120) == 90,
            "editor initialization waits must remain bounded");
        Assert(CapCutHomeShellPolicy.DraftCreationTransitionTimeoutSeconds(30, true) == 90,
            "cold draft creation must allow the full 90-second editor transition budget");
        Assert(CapCutHomeShellPolicy.DraftCreationTransitionTimeoutSeconds(30, false) == 60,
            "warm draft creation must allow at least 60 seconds for CapCut transition");
        Assert(!CapCutHomeShellPolicy.CanProceedToExport(true, false, false, true),
            "a bound but unresponsive editor must not export");
        Assert(!CapCutHomeShellPolicy.CanProceedToExport(true, true, false, false),
            "editor identity and export readiness must remain separate gates");
        Assert(CapCutHomeShellPolicy.CanProceedToExport(true, true, false, true),
            "a bound responsive editor with a visible export entry may proceed");
        Assert(CapCutHomeShellPolicy.CanClose(new IntPtr(100), new IntPtr(200), 10, 20, true, true),
            "a verified home shell in a different process may be closed in isolated mode");
        Assert(!CapCutHomeShellPolicy.CanClose(new IntPtr(100), new IntPtr(200), 10, 10, true, true),
            "a same-process homepage shell must remain open because closing it can terminate the editor");
        Assert(!CapCutHomeShellPolicy.CanClose(new IntPtr(100), new IntPtr(200), 10, 20, false, true),
            "an unverified CapCut window must never be closed");
        Assert(!CapCutHomeShellPolicy.CanClose(new IntPtr(100), new IntPtr(200), 10, 20, true, false),
            "a homepage shell must not close until an editor is exactly bound");
        Assert(!CapCutHomeShellPolicy.CanClose(IntPtr.Zero, new IntPtr(200), 10, 20, true, true),
            "missing home-window identity must not be closed");
    }

    private static void TestOutputQualityProbeDeserialization()
    {
        const string json = "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":1440,\"height\":2560,\"avg_frame_rate\":\"30/1\"},{\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"width\":0,\"height\":0,\"avg_frame_rate\":\"0/0\"}],\"format\":{\"duration\":\"12.345\"}}";
        Assert(FfmpegOutputQualityValidator.CanDeserializeProbeJsonForTest(json),
            "ffprobe JSON must deserialize without an AmbiguousMatchException");
    }

    private static void TestAudioNotLongerSkipsSample()
    {
        var video = new MediaMetadata { Path = "video.mp4", DurationSeconds = 12.0, Width = 1080, Height = 1920 };
        var audio = new MediaMetadata { Path = "voice.wav", DurationSeconds = 10.0 };
        SamplePlan plan = SamplePlanner.Create(video, audio, null);
        Assert(!plan.RequiresSample, "audio not longer than video must not require a sample");
        Assert(String.IsNullOrWhiteSpace(plan.SamplePath) && plan.Speed == 0, "no-sample plan must be explicit");

        WithTempDirectory(delegate(string dir) {
            string videoPath = Path.Combine(dir, "video.mp4");
            string audioPath = Path.Combine(dir, "voice.wav");
            File.WriteAllText(videoPath, "video");
            File.WriteAllText(audioPath, "audio");
            VideoTask task = NewGroupTask(videoPath, audioPath, "", Path.Combine(dir, "out", "done.mp4"), 1);
            task.SampleSpeed = 0;
            var automation = new MockAutomation();
            var runner = new BatchRunner(AppConfig.CreateDefault(), automation,
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), null, new MockPostProcessor());
            runner.RunBlocking(new List<VideoTask> { task }, CancellationToken.None);
            Assert(task.GetState() == TaskState.Completed, "no-sample task should complete");
            Assert(automation.ImportSampleCalls == 0, "no-sample task must not import sample media");
        });
    }

    private static void TestSampleAppendVerification()
    {
        var before = new TimelineSnapshot {
            VideoRun = new System.Drawing.Rectangle(100, 700, 240, 36),
            VideoTrackCenterY = 718,
            VideoFingerprint = 400
        };
        var after = new TimelineSnapshot {
            VideoRun = new System.Drawing.Rectangle(100, 700, 320, 36),
            VideoTrackCenterY = 718,
            // The whole video run changes when a sample is appended, but the
            // original main-clip region remains identical.
            VideoFingerprint = 900,
            MainClipFingerprint = 400
        };
        TimelineAppendValidation.RequireSampleAppend(before, after);
        bool rejected = false;
        try {
            TimelineAppendValidation.RequireSampleAppend(before, new TimelineSnapshot {
                VideoRun = new System.Drawing.Rectangle(100, 700, 240, 36),
                VideoTrackCenterY = 718,
                VideoFingerprint = 400
            });
        } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "a drag without a new timeline segment must be rejected");

        bool replaced = false;
        try {
            TimelineAppendValidation.RequireSampleAppend(before, new TimelineSnapshot {
                VideoRun = new System.Drawing.Rectangle(100, 700, 320, 36),
                VideoTrackCenterY = 718,
                VideoFingerprint = 900,
                MainClipFingerprint = 760
            });
        } catch (InvalidOperationException) { replaced = true; }
        Assert(replaced, "a changed original main-clip region must still be rejected");

        bool movedTrack = false;
        try {
            TimelineAppendValidation.RequireSampleAppend(before, new TimelineSnapshot {
                VideoRun = new System.Drawing.Rectangle(100, 742, 320, 36),
                VideoTrackCenterY = 760,
                VideoFingerprint = 900,
                MainClipFingerprint = 400
            });
        } catch (InvalidOperationException) { movedTrack = true; }
        Assert(movedTrack, "an appended sample on a different video track must be rejected");

        bool movedStart = false;
        try {
            TimelineAppendValidation.RequireSampleAppend(before, new TimelineSnapshot {
                VideoRun = new System.Drawing.Rectangle(118, 700, 320, 36),
                VideoTrackCenterY = 718,
                VideoFingerprint = 900,
                MainClipFingerprint = 400
            });
        } catch (InvalidOperationException) { movedStart = true; }
        Assert(movedStart, "a changed main-video start must be rejected");
    }

    private static void TestDraftTimelineAssemblyVerification()
    {
        WithTempDirectory(delegate(string dir) {
            string main = Path.Combine(dir, "main.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            string sample = Path.Combine(dir, "prepared-sample.mp4");
            Directory.CreateDirectory(Path.Combine(dir, "Timelines", "timeline-1"));
            string json = BuildTimelineAssemblyJson(main, audio, sample, "valid");
            File.WriteAllText(Path.Combine(dir, "draft_content.json"), json, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "Timelines", "timeline-1", "draft_content.json"),
                json, new UTF8Encoding(false));

            var verifier = new DraftTimelineAssemblyVerifier();
            DraftTimelineAssemblyResult result = verifier.VerifyOnce(dir, main, audio, sample,
                12.266666, 15.300000, 3.000000);
            Assert(result.MainDurationUs == 12266666, "main duration must come from the persisted segment");
            Assert(result.SampleDurationUs == 3000000, "sample duration must come from the persisted segment");
            Assert(result.AudioDurationUs == 15300000, "audio duration must come from the persisted segment");
            Assert(!String.IsNullOrWhiteSpace(result.SemanticHash),
                "valid persisted tracks must produce a semantic checkpoint hash");

            // Regression evidence: the old screenshot detector moved from
            // Y=1138 to Y=1062. Draft semantics must remain valid regardless.
            DraftTimelineAssemblyResult second = verifier.VerifyOnce(dir, main, audio, sample,
                12.266666, 15.300000, 3.000000);
            Assert(result.SemanticHash == second.SemanticHash,
                "visual lane movement must not change persisted timeline identity");
        });
    }

    private static void TestDraftTimelineAssemblyFailures()
    {
        string[] scenarios = { "replaced", "overlay", "duplicate-audio", "timing", "extra" };
        string[] codes = {
            "CAPCUT_DRAFT_MAIN_REPLACED",
            "CAPCUT_DRAFT_SAMPLE_WRONG_TRACK",
            "CAPCUT_DRAFT_AUDIO_DUPLICATED",
            "CAPCUT_DRAFT_SAMPLE_WRONG_TRACK",
            "CAPCUT_DRAFT_UNEXPECTED_SEGMENT"
        };
        for (int index = 0; index < scenarios.Length; index++) {
            string scenario = scenarios[index];
            string expectedCode = codes[index];
            WithTempDirectory(delegate(string dir) {
                string main = Path.Combine(dir, "main.mp4");
                string audio = Path.Combine(dir, "voice.mp3");
                string sample = Path.Combine(dir, "prepared-sample.mp4");
                File.WriteAllText(Path.Combine(dir, "draft_content.json"),
                    BuildTimelineAssemblyJson(main, audio, sample, scenario), new UTF8Encoding(false));
                bool rejected = false;
                try {
                    new DraftTimelineAssemblyVerifier().VerifyOnce(dir, main, audio, sample,
                        12.266666, 15.300000, 3.000000);
                } catch (Exception ex) {
                    rejected = ex.Message.IndexOf(expectedCode,
                        StringComparison.OrdinalIgnoreCase) >= 0;
                }
                Assert(rejected, scenario + " must fail with " + expectedCode);
            });
        }
    }

    private static void TestDraftTimelineSampleRepair()
    {
        WithTempDirectory(delegate(string dir) {
            string main = Path.Combine(dir, "main.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            string sample = Path.Combine(dir, "prepared-sample.mp4");
            string timeline = Path.Combine(dir, "Timelines", "timeline-1");
            Directory.CreateDirectory(timeline);
            string overlay = BuildTimelineAssemblyJson(main, audio, sample,
                "overlay");
            string rootContent = Path.Combine(dir, "draft_content.json");
            string rootTemplate = Path.Combine(dir, "template-2.tmp");
            string timelineContent = Path.Combine(timeline,
                "draft_content.json");
            string timelineTemplate = Path.Combine(timeline,
                "template-2.tmp");
            foreach (string path in new[] { rootContent, rootTemplate,
                    timelineContent, timelineTemplate })
                File.WriteAllText(path, overlay, new UTF8Encoding(false));

            DraftTimelineRepairResult repaired =
                new DraftTimelineAssemblyRepairer().RepairMisplacedSample(
                    dir, main, audio, sample, 12.266666, 15.300000,
                    3.000000);
            Assert(Directory.Exists(repaired.BackupDirectory),
                "timeline repair must retain a recoverable backup");
            Assert(repaired.PatchedFiles.Length == 4,
                "root, active Timeline and both template copies must be patched");
            Assert(repaired.Assembly.MainDurationUs == 12266666 &&
                    repaired.Assembly.SampleDurationUs == 3000000,
                "repaired timeline must preserve media durations");
            Assert(repaired.Assembly.VerifiedFiles.Length == 2,
                "verifier must still require root and active Timeline agreement");

            var serializer = new JavaScriptSerializer();
            var document = serializer.DeserializeObject(
                File.ReadAllText(rootContent, Encoding.UTF8))
                as Dictionary<string, object>;
            object[] tracks = document["tracks"] as object[];
            var videoTracks = tracks.Cast<object>()
                .Select(raw => raw as Dictionary<string, object>)
                .Where(track => String.Equals(Convert.ToString(track["type"]),
                    "video", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert(videoTracks.Count == 1,
                "repair must leave exactly one video track");
            object[] segments = videoTracks[0]["segments"] as object[];
            Assert(segments.Length == 2,
                "repair must produce main then sample on one track");
            var sampleSegment = segments[1] as Dictionary<string, object>;
            var range = sampleSegment["target_timerange"]
                as Dictionary<string, object>;
            Assert(Convert.ToInt64(range["start"]) == 12266666,
                "sample must begin at the exact main-video end");
            Assert(Convert.ToInt32(sampleSegment["track_render_index"]) == 0,
                "sample must inherit the main-track render index");
        });

        WithTempDirectory(delegate(string dir) {
            string main = Path.Combine(dir, "main.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            string sample = Path.Combine(dir, "prepared-sample.mp4");
            string path = Path.Combine(dir, "draft_content.json");
            string unsafeJson = BuildTimelineAssemblyJson(main, audio,
                sample, "extra");
            File.WriteAllText(path, unsafeJson, new UTF8Encoding(false));
            string before = File.ReadAllText(path, Encoding.UTF8);
            bool rejected = false;
            try {
                new DraftTimelineAssemblyRepairer().RepairMisplacedSample(
                    dir, main, audio, sample, 12.266666, 15.300000,
                    3.000000);
            } catch (Exception ex) {
                rejected = ex.Message.IndexOf(
                    "CAPCUT_DRAFT_SAMPLE_REPAIR_UNSAFE",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ex.Message.IndexOf(
                    "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    StringComparison.OrdinalIgnoreCase) >= 0;
            }
            Assert(rejected, "unknown or extra segments must not be repaired");
            Assert(String.Equals(before,
                    File.ReadAllText(path, Encoding.UTF8),
                    StringComparison.Ordinal),
                "unsafe repair must not modify the draft");
        });
    }

    private static void TestDraftAudioPlacementVerification()
    {
        WithTempDirectory(delegate(string dir) {
            string main = Path.Combine(dir, "main.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            Directory.CreateDirectory(Path.Combine(dir, "Timelines", "timeline-1"));
            string json = BuildAudioPlacementJson(main, audio, "valid");
            File.WriteAllText(Path.Combine(dir, "draft_content.json"), json,
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "Timelines", "timeline-1", "draft_content.json"),
                json, new UTF8Encoding(false));

            DraftAudioPlacementResult result =
                new DraftTimelineAssemblyVerifier().VerifyAudioOnce(dir,
                    main, audio, 12.266666, 15.300000);
            Assert(result.MainDurationUs == 12266666,
                "persisted main-video duration must be verified before sample append");
            Assert(result.AudioDurationUs == 15300000,
                "persisted independent-audio duration must be verified");
            Assert(result.AudioSegmentId == "audio-segment",
                "the exact persisted audio segment must be returned");
            Assert(result.VerifiedFiles.Length == 2,
                "root and active Timeline must agree before visual automation continues");
        });
    }

    private static void TestDraftVideoTimelineCheckpoint()
    {
        WithTempDirectory(delegate(string dir) {
            string main = Path.Combine(dir, "main.mp4");
            Directory.CreateDirectory(Path.Combine(dir, "Timelines", "timeline-1"));
            string json = BuildVideoTimelineJson(main, false, false);
            File.WriteAllText(Path.Combine(dir, "draft_content.json"), json,
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "Timelines", "timeline-1",
                "draft_content.json"), json, new UTF8Encoding(false));
            DraftVideoTimelineResult result =
                new DraftTimelineAssemblyVerifier().VerifyVideoOnce(
                    dir, main, 12.266666);
            Assert(result.VideoTrackCount == 1 && result.AudioTrackCount == 0,
                "video checkpoint must contain one video track and no audio track");
            Assert(result.MuteVerified && !String.IsNullOrWhiteSpace(result.SemanticHash),
                "silent source media must establish a stable mute checkpoint");

            bool audibleRejected = false;
            File.WriteAllText(Path.Combine(dir, "draft_content.json"),
                BuildVideoTimelineJson(main, true, false), new UTF8Encoding(false));
            try {
                new DraftTimelineAssemblyVerifier().VerifyVideoOnce(
                    dir, main, 12.266666);
            } catch (Exception ex) {
                audibleRejected = ex.Message.Contains("CAPCUT_DRAFT_MAIN_NOT_MUTED");
            }
            Assert(audibleRejected,
                "an audible main clip must not be promoted to the audio-import checkpoint");

            bool audioRejected = false;
            File.WriteAllText(Path.Combine(dir, "draft_content.json"),
                BuildVideoTimelineJson(main, false, true), new UTF8Encoding(false));
            try {
                new DraftTimelineAssemblyVerifier().VerifyVideoOnce(
                    dir, main, 12.266666);
            } catch (Exception ex) {
                audioRejected = ex.Message.Contains("CAPCUT_DRAFT_UNEXPECTED_SEGMENT");
            }
            Assert(audioRejected,
                "a draft that already has audio is not a video-only checkpoint");
        });
    }

    private static void TestDraftVideoPlacementCheckpoint()
    {
        WithTempDirectory(delegate(string dir) {
            string main = Path.Combine(dir, "main.mp4");
            Directory.CreateDirectory(Path.Combine(dir, "Timelines", "timeline-1"));
            string json = BuildVideoTimelineJson(main, true, false);
            File.WriteAllText(Path.Combine(dir, "draft_content.json"), json,
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "Timelines", "timeline-1",
                "draft_content.json"), json, new UTF8Encoding(false));
            DraftTimelineAssemblyVerifier verifier =
                new DraftTimelineAssemblyVerifier();
            DraftVideoTimelineResult placement = verifier.VerifyVideoPlacementStable(
                dir, main, 12.266666, 3, CancellationToken.None);
            Assert(placement.VideoTrackCount == 1 && placement.AudioTrackCount == 0,
                "placement checkpoint must accept one persisted video before mute");
            bool muteRejected = false;
            try { verifier.VerifyVideoOnce(dir, main, 12.266666); }
            catch (Exception ex) {
                muteRejected = ex.Message.IndexOf("CAPCUT_DRAFT_MAIN_NOT_MUTED",
                    StringComparison.OrdinalIgnoreCase) >= 0;
            }
            Assert(muteRejected,
                "the muted checkpoint must reject an audible video until mute is verified");
            Assert(verifier.ObserveVideoPlacement(dir, main, 12.266666) == "video_present_valid",
                "root and mirror must count the same audible clip only once");
            string mirror = Path.Combine(dir, "Timelines", "timeline-1", "draft_content.json");
            File.WriteAllText(mirror, "{\"tracks\":[],\"materials\":{\"videos\":[]}}", new UTF8Encoding(false));
            Assert(verifier.ObserveVideoPlacement(dir, main, 12.266666) == "snapshot_pending",
                "unflushed mirror must never authorize another add");
            var serializer = new JavaScriptSerializer();
            var document = (Dictionary<string, object>)serializer.DeserializeObject(json);
            var tracks = (object[])document["tracks"];
            var track = (Dictionary<string, object>)tracks[0];
            var segments = (object[])track["segments"];
            track["segments"] = new object[] { segments[0], segments[0] };
            File.WriteAllText(Path.Combine(dir, "draft_content.json"), serializer.Serialize(document));
            bool duplicated = false;
            try { verifier.ObserveVideoPlacement(dir, main, 12.266666); }
            catch (DraftTimelineValidationException ex) { duplicated = ex.ErrorCode == "CAPCUT_VIDEO_SEGMENT_DUPLICATED"; }
            Assert(duplicated, "duplicate main must block before another action");
        });
    }

    private static void TestStableArchiveVideoTimelineDiscovery()
    {
        WithTempDirectory(delegate(string dir) {
            string live = Path.Combine(dir, "live");
            string draft = Path.Combine(live, "draft-one");
            Directory.CreateDirectory(draft);
            string main = Path.Combine(dir, "main.mp4");
            File.WriteAllText(main, "media");
            File.WriteAllText(Path.Combine(draft, "draft_content.json"),
                BuildVideoTimelineJson(main, false, false), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(draft, "draft_meta_info.json"),
                new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                    { "draft_id", "draft-id-one" }, { "draft_name", "draft-one" }
                }), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(live, "root_meta_info.json"),
                new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                    { "all_draft_store", new object[] {
                        new Dictionary<string, object> {
                            { "draft_id", "draft-id-one" },
                            { "draft_name", "draft-one" },
                            { "draft_fold_path", draft }
                        }
                    } }
                }), new UTF8Encoding(false));
            var task = new VideoTask { VideoPath = main };
            var lease = new DraftRootLeaseRecord {
                ArchiveDraftRoot = live,
                ArchiveStorageVersion = "3",
                StableTaskArchiveKey = "stable-key"
            };
            MethodInfo method = typeof(TaskDraftRootLeaseManager).GetMethod(
                "DiscoverArchivedVideoTimelineCandidate",
                BindingFlags.NonPublic | BindingFlags.Static);
            var checkpoint = method.Invoke(null, new object[] { task, lease })
                as DirectDraftCheckpoint;
            Assert(checkpoint != null && checkpoint.DraftId == "draft-id-one" &&
                    checkpoint.SourceVideoPath == Path.GetFullPath(main) &&
                    checkpoint.State == "video_timeline_candidate",
                "an exact single stable archive must be recoverable before the new checkpoint exists");
        });
    }

    private static void TestEmptyStableArchiveIsNoCheckpoint()
    {
        WithTempDirectory(delegate(string dir) {
            string live = Path.Combine(dir, "live");
            Directory.CreateDirectory(live);
            string main = Path.Combine(dir, "main.mp4");
            File.WriteAllText(main, "media");
            File.WriteAllText(Path.Combine(live, "root_meta_info.json"),
                new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                    { "all_draft_store", new object[0] },
                    { "draft_ids", 0 },
                    { "root_path", live }
                }), new UTF8Encoding(false));
            var task = new VideoTask { VideoPath = main };
            var lease = new DraftRootLeaseRecord {
                ArchiveDraftRoot = live,
                ArchiveStorageVersion = "3",
                StableTaskArchiveKey = "stable-key"
            };
            MethodInfo method = typeof(TaskDraftRootLeaseManager).GetMethod(
                "DiscoverArchivedVideoTimelineCandidate",
                BindingFlags.NonPublic | BindingFlags.Static);
            var checkpoint = method.Invoke(null, new object[] { task, lease })
                as DirectDraftCheckpoint;
            Assert(checkpoint == null,
                "an empty stable archive must mean no checkpoint, not a multiple-draft conflict");
        });
    }

    private static void TestEmptyArchivedBootstrapRebind()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string roots = Path.Combine(dir, "task-roots");
            string shortArchives = Path.Combine(dir, ".capcut-drafts");
            string leaseFile = Path.Combine(dir, "runtime", "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");
            Directory.CreateDirectory(originalRoot);
            WriteTestGlobalSetting(setting, originalRoot);
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            config.UseLocalActiveDraftDirectory = true;
            VideoTask task = NewDraftRootTask("empty-bootstrap-seed");
            task.Id = "empty-bootstrap-task";
            task.VideoPath = Path.Combine(dir, "source.mp4");
            File.WriteAllText(task.VideoPath, "media");

            var seed = new TaskDraftRootLeaseManager(config, new AppLogger(),
                leaseFile, roots, setting, delegate(int pid) { return false; },
                delegate { return false; }, shortArchives);
            DraftRootLeaseRecord seedLease = seed.Acquire(task,
                CancellationToken.None);
            seed.Release(CancellationToken.None);
            string live = seedLease.ArchiveDraftRoot;
            string draft = Path.Combine(live, "0907");
            Directory.CreateDirectory(draft);
            File.WriteAllText(Path.Combine(draft, "draft_content.json"),
                new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                    { "tracks", new object[0] },
                    { "materials", new Dictionary<string, object> {
                        { "videos", new object[0] }, { "audios", new object[0] }
                    } },
                    { "duration", 0 }
                }), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(draft, "draft_meta_info.json"),
                new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                    { "draft_id", "empty-bootstrap-id" }, { "draft_name", "0907" },
                    { "tm_duration", 0 }
                }), new UTF8Encoding(false));
            WriteTestDraftIndex(live, "empty-bootstrap-id", draft);

            task.HandoffAttemptId = "empty-bootstrap-retry";
            task.HandoffManifestId = "empty-bootstrap-manifest";
            var recovering = new TaskDraftRootLeaseManager(config, new AppLogger(),
                leaseFile, roots, setting, delegate(int pid) { return false; },
                delegate { return false; }, shortArchives);
            DraftRootLeaseRecord recovered = recovering.Acquire(task,
                CancellationToken.None);
            DraftRootPreparationResult prepared = recovering.CurrentPreparation;
            Assert(prepared != null && prepared.Mode == "empty_bootstrap" &&
                    prepared.DraftId == "empty-bootstrap-id" &&
                    Directory.Exists(prepared.ActiveDraftPath),
                "a durable empty bootstrap must be rebound as a create result");
            recovering.ValidateTaskRoot(task, recovered, false);
            recovering.Release(CancellationToken.None);
        });
    }

    private static void TestArchivedCandidateAcquireValidation()
    {
        WithTempDirectory(delegate(string dir) {
            string originalRoot = Path.Combine(dir, "original-drafts");
            string roots = Path.Combine(dir, "task-roots");
            string shortArchives = Path.Combine(dir, ".capcut-drafts");
            string leaseFile = Path.Combine(dir, "runtime",
                "draft-root-lease.json");
            string setting = Path.Combine(dir, "globalSetting");
            string main = Path.Combine(dir, "main.mp4");
            Directory.CreateDirectory(originalRoot);
            File.WriteAllText(main, "media");
            WriteTestGlobalSetting(setting, originalRoot);
            var config = AppConfig.CreateDefault();
            config.EnableTaskScopedDraftRoot = true;
            config.UseLocalActiveDraftDirectory = true;
            VideoTask task = NewDraftRootTask("attempt-candidate-seed");
            task.Id = "stable-candidate-task";
            task.VideoPath = main;

            var seed = new TaskDraftRootLeaseManager(config,
                new AppLogger(), leaseFile, roots, setting,
                delegate(int pid) { return false; },
                delegate { return false; }, shortArchives);
            DraftRootLeaseRecord seedLease = seed.Acquire(task,
                CancellationToken.None);
            seed.Release(CancellationToken.None);

            string live = seedLease.ArchiveDraftRoot;
            string draft = Path.Combine(live, "0903");
            Directory.CreateDirectory(draft);
            File.WriteAllText(Path.Combine(draft, "draft_content.json"),
                BuildVideoTimelineJson(main, false, false),
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(draft, "draft_meta_info.json"),
                new JavaScriptSerializer().Serialize(
                    new Dictionary<string, object> {
                        { "draft_id", "candidate-draft-id" },
                        { "draft_name", "0903" }
                    }), new UTF8Encoding(false));
            WriteTestDraftIndex(live, "candidate-draft-id", draft);

            task.HandoffAttemptId = "attempt-candidate-retry";
            task.HandoffManifestId = "manifest-candidate-retry";
            var recovering = new TaskDraftRootLeaseManager(config,
                new AppLogger(), leaseFile, roots, setting,
                delegate(int pid) { return false; },
                delegate { return false; }, shortArchives);
            DraftRootLeaseRecord recovered = recovering.Acquire(task,
                CancellationToken.None);
            DraftRootPreparationResult prepared =
                recovering.CurrentPreparation;
            Assert(prepared != null &&
                    prepared.Mode == "archived_candidate" &&
                    prepared.DraftId == "candidate-draft-id" &&
                    Directory.Exists(prepared.ActiveDraftPath),
                "Acquire must keep the archived candidate bound through immediate validation");
            Assert(task.TimelineAssemblyCheckpoint == null,
                "filesystem preparation must not promote a timeline before structural verification");
            recovering.ValidateTaskRoot(task, recovered, false);
            recovering.Release(CancellationToken.None);
        });
    }

    private static void TestDraftAudioPlacementFailures()
    {
        string[] scenarios = { "offset", "duplicate", "wrong-audio", "missing" };
        string[] codes = {
            "CAPCUT_DRAFT_AUDIO_OFFSET",
            "CAPCUT_DRAFT_AUDIO_DUPLICATED",
            "CAPCUT_DRAFT_AUDIO_IDENTITY_MISMATCH",
            "CAPCUT_DRAFT_AUDIO_MISSING"
        };
        for (int index = 0; index < scenarios.Length; index++) {
            string scenario = scenarios[index];
            string expectedCode = codes[index];
            WithTempDirectory(delegate(string dir) {
                string main = Path.Combine(dir, "main.mp4");
                string audio = Path.Combine(dir, "voice.mp3");
                File.WriteAllText(Path.Combine(dir, "draft_content.json"),
                    BuildAudioPlacementJson(main, audio, scenario),
                    new UTF8Encoding(false));
                bool rejected = false;
                try {
                    new DraftTimelineAssemblyVerifier().VerifyAudioOnce(dir,
                        main, audio, 12.266666, 15.300000);
                } catch (Exception ex) {
                    rejected = ex.Message.IndexOf(expectedCode,
                        StringComparison.OrdinalIgnoreCase) >= 0;
                }
                Assert(rejected, scenario + " must fail with " + expectedCode);
            });
        }
    }

    private static string BuildVideoTimelineJson(string main, bool hasAudio,
        bool includeAudioTrack)
    {
        var tracks = new List<object> {
            new Dictionary<string, object> {
                { "type", "video" },
                { "segments", new object[] {
                    new Dictionary<string, object> {
                        { "id", "main-segment" },
                        { "material_id", "main-material" },
                        { "volume", 1.0 },
                        { "target_timerange", new Dictionary<string, object> {
                            { "start", 0L }, { "duration", 12266666L } } }
                    }
                } }
            }
        };
        if (includeAudioTrack)
            tracks.Add(new Dictionary<string, object> {
                { "type", "audio" }, { "segments", new object[0] }
            });
        return new JavaScriptSerializer().Serialize(
            new Dictionary<string, object> {
                { "materials", new Dictionary<string, object> {
                    { "videos", new object[] {
                        new Dictionary<string, object> {
                            { "id", "main-material" }, { "path", main },
                            { "has_audio", hasAudio }, { "duration", 12266666L }
                        }
                    } },
                    { "audios", new object[0] }
                } },
                { "tracks", tracks.ToArray() }
            });
    }

    private static string BuildAudioPlacementJson(string main, string audio,
        string scenario)
    {
        var serializer = new JavaScriptSerializer();
        const string mainId = "main-material";
        const string audioId = "audio-material";
        string registeredAudio = scenario == "wrong-audio"
            ? Path.Combine(Path.GetDirectoryName(audio), "other-task.mp3")
            : audio;
        var videoSegment = new Dictionary<string, object> {
            { "id", "main-segment" }, { "material_id", mainId },
            { "target_timerange", new Dictionary<string, object> {
                { "start", 0L }, { "duration", 12266666L } } }
        };
        var audioSegment = new Dictionary<string, object> {
            { "id", "audio-segment" }, { "material_id", audioId },
            { "target_timerange", new Dictionary<string, object> {
                { "start", scenario == "offset" ? 250000L : 0L },
                { "duration", 15300000L } } }
        };
        var tracks = new List<object> {
            new Dictionary<string, object> {
                { "type", "video" }, { "segments", new object[] { videoSegment } }
            }
        };
        if (scenario != "missing") {
            tracks.Add(new Dictionary<string, object> {
                { "type", "audio" },
                { "segments", scenario == "duplicate"
                    ? new object[] { audioSegment, audioSegment }
                    : new object[] { audioSegment } }
            });
        }
        return serializer.Serialize(new Dictionary<string, object> {
            { "materials", new Dictionary<string, object> {
                { "videos", new object[] {
                    new Dictionary<string, object> {
                        { "id", mainId }, { "path", main }, { "duration", 12266666L }
                    }
                } },
                { "audios", new object[] {
                    new Dictionary<string, object> {
                        { "id", audioId }, { "path", registeredAudio }, { "duration", 15300000L }
                    }
                } }
            } },
            { "tracks", tracks.ToArray() }
        });
    }

    private static string BuildTimelineAssemblyJson(string main, string audio,
        string sample, string scenario)
    {
        var serializer = new JavaScriptSerializer();
        const string mainId = "main-material";
        const string sampleId = "sample-material";
        const string audioId = "audio-material";
        long sampleStart = scenario == "timing" ? 12766666 : 12266666;
        string firstMaterial = scenario == "replaced" ? sampleId : mainId;
        var first = new Dictionary<string, object> {
            { "id", "main-segment" }, { "material_id", firstMaterial },
            { "track_render_index", 0 },
            { "target_timerange", new Dictionary<string, object> {
                { "start", 0L }, { "duration", 12266666L } } }
        };
        var second = new Dictionary<string, object> {
            { "id", "sample-segment" }, { "material_id", sampleId },
            { "track_render_index", scenario == "overlay" ? 1 : 0 },
            { "target_timerange", new Dictionary<string, object> {
                { "start", sampleStart }, { "duration", 3000000L } } }
        };
        var videoSegments = new List<object> { first, second };
        if (scenario == "extra") videoSegments.Add(new Dictionary<string, object> {
            { "id", "extra-segment" }, { "material_id", sampleId },
            { "target_timerange", new Dictionary<string, object> {
                { "start", 15266666L }, { "duration", 100000L } } }
        });
        object[] videoTracks = scenario == "overlay"
            ? new object[] {
                new Dictionary<string, object> { { "type", "video" }, { "segments", new object[] { first } } },
                new Dictionary<string, object> { { "type", "video" }, { "segments", new object[] { second } } }
            }
            : new object[] {
                new Dictionary<string, object> { { "type", "video" }, { "segments", videoSegments.ToArray() } }
            };
        var audioSegment = new Dictionary<string, object> {
            { "id", "audio-segment" }, { "material_id", audioId },
            { "target_timerange", new Dictionary<string, object> {
                { "start", 0L }, { "duration", 15300000L } } }
        };
        object[] audioSegments = scenario == "duplicate-audio"
            ? new object[] { audioSegment, audioSegment }
            : new object[] { audioSegment };
        var tracks = new List<object>(videoTracks);
        tracks.Add(new Dictionary<string, object> {
            { "type", "audio" }, { "segments", audioSegments }
        });
        var root = new Dictionary<string, object> {
            { "materials", new Dictionary<string, object> {
                { "videos", new object[] {
                    new Dictionary<string, object> { { "id", mainId }, { "path", main }, { "duration", 12266666L } },
                    new Dictionary<string, object> { { "id", sampleId }, { "path", sample }, { "duration", 3000000L } }
                } },
                { "audios", new object[] {
                    new Dictionary<string, object> { { "id", audioId }, { "path", audio }, { "duration", 15300000L } }
                } }
            } },
            { "tracks", tracks.ToArray() }
        };
        return serializer.Serialize(root);
    }

    private static void TestSampleAudioEndAlignment()
    {
        var snapshot = new TimelineSnapshot {
            VideoRun = new System.Drawing.Rectangle(100, 700, 320, 36),
            AudioRun = new System.Drawing.Rectangle(100, 760, 320, 28),
            PixelsPerSecond = 72
        };
        TimelineAppendValidation.RequireSampleEndAligned(snapshot, 340, 420);
        bool rejected = false;
        try { TimelineAppendValidation.RequireSampleEndAligned(snapshot, 340, 398); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "a sample ending away from the audio end must be rejected");

        // Real 2026-08-05 regression: CapCut rendered the valid sample end six
        // pixels away from the audio, while ffprobe showed only a 4 ms delta.
        TimelineAppendValidation.RequireSampleEndAligned(snapshot, 340, 414,
            14.166667, 17.162375, 3.000000, 1.0);

        bool mediaRejected = false;
        try {
            TimelineAppendValidation.RequireSampleEndAligned(snapshot, 340, 420,
                14.166667, 17.500000, 3.000000, 1.0);
        } catch (InvalidOperationException) { mediaRejected = true; }
        Assert(mediaRejected, "a real media-duration mismatch must be rejected even when pixels align");
    }

    private static void TestRotatedMovIsVertical()
    {
        var metadata = new MediaMetadata { Width = 1920, Height = 1080, Orientation = 90 };
        Assert(metadata.IsVertical, "90-degree MOV rotation should produce a vertical display");
        Assert(metadata.DisplayWidth == 1080 && metadata.DisplayHeight == 1920, "display dimensions were not rotated");
    }

    private static void TestTaskStore()
    {
        WithTempDirectory(delegate(string dir) {
            string path = Path.Combine(dir, "tasks.json");
            var store = new TaskStore(path);
            var task = new VideoTask {
                VideoPath = "a.mp4",
                OutputPath = "b.mp4",
                Attempts = 3,
                DirectDraftCheckpoint = new DirectDraftCheckpoint {
                    State = "style_applied",
                    DraftDirectory = "draft",
                    DraftId = "draft-id",
                    DraftName = "0726 (15)",
                    SourceVideoPath = "a.mp4",
                    PatchedFiles = new[] { "draft_content.json" }
                }
            };
            task.SetState(TaskState.Failed);
            store.Save(new[] { task });
            VideoTask loaded = store.Load().Single();
            Assert(loaded.Id == task.Id, "task ID was not preserved");
            Assert(loaded.GetState() == TaskState.Failed, "task state was not preserved");
            Assert(loaded.Attempts == 3, "attempt count was not preserved");
            Assert(loaded.DirectDraftCheckpoint != null &&
                loaded.DirectDraftCheckpoint.DraftId == "draft-id" &&
                loaded.DirectDraftCheckpoint.DraftName == "0726 (15)",
                "direct-draft reopen checkpoint was not preserved");

            var legacyCompleted = new VideoTask {
                VideoPath = "legacy.mp4",
                OutputPath = "legacy-out.mp4",
                Error = "最终成片已验证并等待上传；草稿待清理: 草稿索引更新失败"
            };
            legacyCompleted.SetState(TaskState.Completed);
            store.Save(new[] { legacyCompleted });
            VideoTask migrated = store.Load().Single();
            Assert(String.IsNullOrWhiteSpace(migrated.Error),
                "legacy cleanup-only warning must not remain a production error");
            Assert(migrated.DraftCleanupStatus == "pending" &&
                migrated.DraftCleanupWarning.Contains("草稿待清理"),
                "legacy cleanup-only warning was not migrated into housekeeping state");
            Assert(!File.ReadAllText(path, Encoding.UTF8).Contains("\"Error\":\"最终成片"),
                "legacy cleanup warning migration was not persisted atomically");
        });
    }

    private static void TestRunnerContinuesAfterFailure()
    {
        WithTempDirectory(delegate(string dir) {
            string badVideo = Path.Combine(dir, "bad.mp4");
            string goodVideo = Path.Combine(dir, "good.mp4");
            File.WriteAllText(badVideo, "video");
            File.WriteAllText(goodVideo, "video");
            var tasks = new List<VideoTask> {
                new VideoTask { VideoPath = badVideo, OutputPath = Path.Combine(dir, "bad_out.mp4") },
                new VideoTask { VideoPath = goodVideo, OutputPath = Path.Combine(dir, "good_out.mp4") }
            };
            AppConfig config = AppConfig.CreateDefault();
            config.RetryCount = 0;
            var automation = new MockAutomation();
            var store = new TaskStore(Path.Combine(dir, "tasks.json"));
            var runner = new BatchRunner(config, automation, store, new AppLogger(), null, new MockPostProcessor());
            runner.RunBlocking(tasks, CancellationToken.None);

            Assert(tasks[0].GetState() == TaskState.Failed, "first task should fail");
            Assert(tasks[1].GetState() == TaskState.Waiting, "second task must remain waiting after the first task fails");
            Assert(automation.Exported.Count == 0, "a failed first task must stop before the next export");
            Assert(automation.DeleteCurrentDraftCalls == 0, "failed task draft must be preserved");
        });
    }

    private static void TestHandoffRejectsMissingMedia()
    {
        WithTempDirectory(delegate(string dir) {
            string manifest = Path.Combine(dir, "handoff.json");
            var payload = new Dictionary<string, object> {
                { "schemaVersion", 1 },
                { "batchId", "7.19-test" },
                { "statusFile", Path.Combine(dir, "status.json") },
                { "projectExpectedCount", 2 },
                { "attemptTaskCount", 1 },
                { "projectTaskIds", new[] { "done-task", "missing-task" } },
                { "completedTaskIds", new[] { "done-task" } },
                { "tasks", new[] {
                    new Dictionary<string, object> {
                        { "taskId", "missing-task" },
                        { "project", "ProjectA" },
                        { "material", "碳岩板" },
                        { "language", "西班牙语" },
                        { "number", "001" },
                        { "sequence", 1 },
                        { "videoPath", Path.Combine(dir, "missing.mp4") },
                        { "audioPath", Path.Combine(dir, "missing.mp3") },
                        { "finalOutputPath", Path.Combine(dir, "out.mp4") }
                    }
                } }
            };
            File.WriteAllText(manifest, new JavaScriptSerializer().Serialize(payload));
            var tasks = new List<VideoTask>();
            HandoffImportResult result = HandoffManifestImporter.Import(manifest, AppConfig.CreateDefault(), tasks);
            Assert(result.Added == 0, "missing media must not be imported");
            Assert(result.Issues.Count == 1, "missing media should produce one task issue");
            Assert(tasks.Count == 1 && tasks[0].GetState() == TaskState.Failed,
                "rejected handoff should be reported back as a failed task");
            Assert(tasks[0].ProjectExpectedCount == 2 && tasks[0].AttemptTaskCount == 1,
                "partial retry contract counts must survive handoff import");
            Assert(tasks[0].ProjectTaskIds.SequenceEqual(new[] { "done-task", "missing-task" }) &&
                   tasks[0].CompletedTaskIds.SequenceEqual(new[] { "done-task" }),
                "partial retry task identities must survive handoff import");
            Assert(File.Exists(Path.Combine(dir, "status.json")), "rejected handoff must write a status file");
        });
    }

    private static VideoTask PreparedRecoveryTask()
    {
        return new VideoTask { Id = "task-identity", SamplePath = "library.mov",
            VideoPath = "main.mp4", AudioPath = "audio.mp3", SampleSpeed = 2.47 };
    }

    private static void TestLostPreparedIdentityRecovery()
    {
        WithTempDirectory(delegate(string dir) {
            VideoTask task = PreparedRecoveryTask();
            task.VideoPath = Path.Combine(dir, "main.mp4");
            task.AudioPath = Path.Combine(dir, "audio.mp3");
            string prepared = Path.Combine(dir, "task-identity-3033ms.mp4");
            string draft = Path.Combine(dir, "draft_content.json");
            string json = BuildTimelineAssemblyJson(task.VideoPath, task.AudioPath, prepared, "valid");
            File.WriteAllText(draft, json);
            bool verified = false;
            bool restored = PreparedSampleRecoveryPolicy.Restore(task, prepared, 3.033334,
                delegate(string path) { return path == prepared; },
                delegate(string path, double duration) {
                    Assert(task.PreparedSamplePath == null && task.SamplePath == "library.mov",
                        "task must not change before the artifact is verified");
                    verified = true;
                });
            Assert(restored && verified && task.SamplePath == prepared &&
                task.OriginalSamplePath == "library.mov" && task.SampleSpeed == 1,
                "verified identity must be restored before full timeline verification");
            var result = new DraftTimelineAssemblyVerifier().VerifyOnce(dir,
                task.VideoPath, task.AudioPath, TimelineCheckpointIdentityPolicy.EffectiveSamplePath(task),
                12.266666, 15.300000, 3.000000);
            Assert(!String.IsNullOrWhiteSpace(result.SemanticHash), "assembled timeline must pass");
            Assert(File.ReadAllText(draft) == json && Directory.GetFiles(dir).Length == 1,
                "recovery must not rewrite the draft or generate/import media");
        });
    }

    private static void TestMissingPreparedIdentityRecovery()
    {
        VideoTask task = PreparedRecoveryTask();
        bool restored = PreparedSampleRecoveryPolicy.Restore(task, "expected.mp4", 3,
            delegate(string path) { return false; },
            delegate(string path, double duration) { throw new Exception("must not probe absent file"); });
        Assert(!restored && task.PreparedSamplePath == null && task.SamplePath == "library.mov",
            "missing artifact cannot change task identity");
    }

    private static void TestInvalidPreparedIdentityRecovery()
    {
        VideoTask task = PreparedRecoveryTask();
        bool rejected = false;
        try {
            PreparedSampleRecoveryPolicy.Restore(task, "expected.mp4", 3,
                delegate(string path) { return true; },
                delegate(string path, double duration) { throw new InvalidDataException("wrong duration/audio stream"); });
        } catch (InvalidDataException) { rejected = true; }
        Assert(rejected && task.PreparedSamplePath == null && task.SamplePath == "library.mov",
            "invalid existing media must fail without mutating facts");
    }

    private static void TestExistingPreparedIdentityRecovery()
    {
        VideoTask task = PreparedRecoveryTask(); task.PreparedSamplePath = "committed.mp4";
        Assert(!PreparedSampleRecoveryPolicy.Restore(task, "another.mp4", 3,
            delegate(string path) { throw new Exception("must not search for replacement"); },
            delegate(string path, double duration) { }), "existing identity stays authoritative");
        Assert(task.PreparedSamplePath == "committed.mp4", "must preserve committed identity");
    }

    private static void TestUnneededPreparedIdentityRecovery()
    {
        foreach (double seconds in new[] { 0.0, -1.0, Double.NaN, Double.PositiveInfinity }) {
            Assert(!PreparedSampleRecoveryPolicy.Restore(PreparedRecoveryTask(), "expected.mp4", seconds,
                delegate(string path) { throw new Exception("must not inspect artifacts"); },
                delegate(string path, double duration) { }), "invalid duration must not recover");
        }
        VideoTask task = PreparedRecoveryTask(); task.Id = "";
        Assert(!PreparedSampleRecoveryPolicy.Restore(task, "expected.mp4", 3,
            delegate(string path) { return true; }, delegate(string path, double duration) { }),
            "unknown task identity cannot recover a sample");
    }

    private static void TestForeignSampleRecoveryFailure()
    {
        WithTempDirectory(delegate(string dir) {
            string main = Path.Combine(dir, "main.mp4"), audio = Path.Combine(dir, "audio.mp3");
            File.WriteAllText(Path.Combine(dir, "draft_content.json"),
                BuildTimelineAssemblyJson(main, audio, Path.Combine(dir, "foreign.mp4"), "valid"));
            bool rejected = false;
            try { new DraftTimelineAssemblyVerifier().VerifyOnce(dir, main, audio,
                Path.Combine(dir, "expected.mp4"), 12.266666, 15.3, 3); }
            catch (DraftTimelineValidationException ex) {
                rejected = ex.ErrorCode == "CAPCUT_DRAFT_SAMPLE_IDENTITY_MISMATCH";
            }
            Assert(rejected, "foreign sample must not be classified as a lower audio-only stage");
        });
    }

    private static void TestPreparedSampleCheckpointEnvelopeRebind()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "video.mp4");
            string audio = Path.Combine(dir, "audio.mp3");
            string publicSample = Path.Combine(dir, "public-sample.mov");
            string preparedSample = Path.Combine(dir, "prepared-sample.mp4");
            var task = new VideoTask {
                Id = "stable-task",
                VideoPath = video,
                AudioPath = audio,
                SamplePath = publicSample,
                PreparedSamplePath = preparedSample,
                RemotePipelineTaskId = "canonical-task",
                ProductionDate = "8.11",
                HandoffBatchId = "new-batch",
                HandoffManifestId = "new-manifest",
                HandoffAttemptId = "new-attempt"
            };
            var checkpoint = new TimelineAssemblyCheckpoint {
                TaskId = task.Id,
                MainVideoPath = video,
                AudioPath = audio,
                SamplePath = preparedSample,
                CanonicalTaskId = task.RemotePipelineTaskId,
                ProductionDate = task.ProductionDate,
                BatchId = "old-batch",
                ManifestId = "old-manifest",
                AttemptId = "old-attempt"
            };

            Assert(TimelineCheckpointIdentityPolicy.StableIdentityMatches(checkpoint, task),
                "a safe-continue manifest must compare the verified prepared sample, not the public sample library path");
            TimelineCheckpointIdentityPolicy.RebindTimelineEnvelope(checkpoint, task);
            Assert(checkpoint.BatchId == "new-batch" &&
                   checkpoint.ManifestId == "new-manifest" &&
                   checkpoint.AttemptId == "new-attempt",
                "a stable checkpoint must rebind to the new execution envelope");

            task.PreparedSamplePath = Path.Combine(dir, "different-prepared.mp4");
            Assert(!TimelineCheckpointIdentityPolicy.StableIdentityMatches(checkpoint, task),
                "a different prepared sample must remain a hard identity mismatch");
        });
    }

    private static void TestHandoffPreservesRecoveryCheckpoint()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "same.mp4");
            var checkpoint = new DirectDraftCheckpoint { DraftId = "draft-one", DraftName = "0728 (10)" };
            var captionCheckpoint = new CaptionDraftCheckpoint {
                DraftId = "draft-one", DraftName = "0728 (10)",
                CaptionCount = 6, CaptionIdentityHash = "caption-hash"
            };
            var timelineCheckpoint = new TimelineAssemblyCheckpoint {
                DraftId = "draft-one", DraftName = "0728 (10)",
                SemanticHash = "timeline-hash"
            };
            var previous = new VideoTask {
                Id = "stable-task-id",
                VideoPath = video,
                RemotePipelineTaskId = "stable-canonical-id",
                ProductionDate = "7.28",
                Attempts = 2,
                Error = "reopen failed",
                DirectDraftCheckpoint = checkpoint,
                CaptionDraftCheckpoint = captionCheckpoint,
                TimelineAssemblyCheckpoint = timelineCheckpoint,
                TemporaryOutputPath = Path.Combine(dir, "old-temp.mp4")
            };
            previous.SetState(TaskState.Failed);
            var replacement = new VideoTask {
                Id = "stable-task-id",
                VideoPath = video,
                RemotePipelineTaskId = "stable-canonical-id",
                ProductionDate = "7.28",
                HandoffManifestId = "new-manifest",
                HandoffAttemptId = "new-attempt"
            };

            HandoffManifestImporter.PreserveRecoveryState(previous, replacement);
            Assert(replacement.GetState() == TaskState.Waiting,
                "a new handoff attempt must make the failed checkpoint runnable");
            Assert(String.IsNullOrWhiteSpace(replacement.Error) && String.IsNullOrWhiteSpace(replacement.FinishedAt),
                "a new handoff attempt must retire the old terminal error");
            Assert(Object.ReferenceEquals(replacement.DirectDraftCheckpoint, checkpoint),
                "verified draft checkpoint must survive manifest refresh");
            Assert(Object.ReferenceEquals(replacement.CaptionDraftCheckpoint,
                    captionCheckpoint) && Object.ReferenceEquals(
                    replacement.TimelineAssemblyCheckpoint, timelineCheckpoint),
                "caption and timeline checkpoints must survive the same-task manifest refresh");
            Assert(replacement.TemporaryOutputPath == previous.TemporaryOutputPath,
                "temporary export checkpoint must survive manifest refresh");
            Assert(replacement.HandoffManifestId == "new-manifest" && replacement.HandoffAttemptId == "new-attempt",
                "new handoff ownership must not be overwritten by recovery state");

            var unrelated = new VideoTask { VideoPath = Path.Combine(dir, "other.mp4") };
            HandoffManifestImporter.PreserveRecoveryState(previous, unrelated);
            Assert(unrelated.DirectDraftCheckpoint == null,
                "different source video must never inherit another task's draft checkpoint");
            Assert(unrelated.CaptionDraftCheckpoint == null &&
                    unrelated.TimelineAssemblyCheckpoint == null,
                "different source video must never inherit caption or timeline checkpoints");

            var changedIdentity = new VideoTask {
                Id = "new-attempt-task-id",
                VideoPath = video,
                RemotePipelineTaskId = "stable-canonical-id",
                ProductionDate = "7.28",
                OutputPath = Path.Combine(dir, "same-output.mp4")
            };
            previous.Id = "old-attempt-task-id";
            previous.OutputPath = changedIdentity.OutputPath;
            HandoffManifestImporter.PreserveRecoveryState(previous, changedIdentity);
            Assert(changedIdentity.DirectDraftCheckpoint == null,
                "a changed stable task id must not inherit another task's verified draft checkpoint");
            Assert(changedIdentity.CaptionDraftCheckpoint == null &&
                    changedIdentity.TimelineAssemblyCheckpoint == null,
                "a changed stable task id must not inherit caption or timeline checkpoints");

            var passive = new VideoTask {
                Id = previous.Id,
                VideoPath = video,
                RemotePipelineTaskId = previous.RemotePipelineTaskId,
                ProductionDate = previous.ProductionDate,
                HandoffBatchId = "same-batch",
                HandoffManifestId = "same-manifest",
                HandoffAttemptId = "same-attempt"
            };
            previous.HandoffBatchId = passive.HandoffBatchId;
            previous.HandoffManifestId = passive.HandoffManifestId;
            previous.HandoffAttemptId = passive.HandoffAttemptId;
            previous.Error = "still failed";
            previous.FinishedAt = "2026-07-28T18:00:00+08:00";
            HandoffManifestImporter.PreserveRecoveryState(previous, passive);
            Assert(passive.GetState() == TaskState.Failed && passive.Error == "still failed",
                "a passive refresh of the same handoff identity must not retry a failed task");
        });
    }

    private static void TestHomePromotionPolicy()
    {
        string text = "Pro 开通会员畅享80+高级功能 专属素材 免费试用7天";
        Assert(CapCutImportDialogPolicy.CanDismissHomePromotion(true,true,"CapCut","SplashDialogV2_QMLTYPE_436",true,405000,3563520,text), "incident offer is dismissible");
        Assert(!CapCutImportDialogPolicy.CanDismissHomePromotion(false,true,"CapCut","SplashDialogV2_QMLTYPE_436",true,405000,3563520,text), "foreign popup rejected");
        Assert(!CapCutImportDialogPolicy.CanDismissHomePromotion(true,true,"CapCut","SplashDialogV2_QMLTYPE_436",true,405000,3563520,"发生错误"), "unknown/error popup rejected");
        Assert(!CapCutImportDialogPolicy.CanDismissHomePromotion(true,true,"CapCut","#32770",true,405000,3563520,text), "native dialog rejected");
        Assert(!CapCutImportDialogPolicy.CanDismissHomePromotion(true,true,"CapCut","SplashDialogV2_QMLTYPE_436",false,405000,3563520,text), "outside home rejected");
        Assert(!CapCutImportDialogPolicy.CanDismissHomePromotion(true,false,"CapCut","SplashDialogV2_QMLTYPE_436",true,405000,3563520,text), "hung popup rejected");
    }

    private static void TestPickerRefocusPolicy()
    {
        Assert(FileDialogIdentityPolicy.CanRefocusPicker(true,true,true,"请选择媒体资源","#32770",true,true),
            "accepted background picker can acquire focus before typing");
        Assert(FileDialogIdentityPolicy.CanRefocusPicker(true,true,true,"Open","#32770",true,true),
            "English native picker accepted");
        Assert(!FileDialogIdentityPolicy.CanRefocusPicker(false,true,true,"Open","#32770",true,true), "replaced PID/start identity rejected");
        Assert(!FileDialogIdentityPolicy.CanRefocusPicker(true,false,true,"Open","#32770",true,true), "hidden picker rejected");
        Assert(!FileDialogIdentityPolicy.CanRefocusPicker(true,true,false,"Open","#32770",true,true), "disabled picker rejected");
        Assert(!FileDialogIdentityPolicy.CanRefocusPicker(true,true,true,"Open error","#32770",true,true), "error title rejected even with Open text");
        Assert(!FileDialogIdentityPolicy.CanRefocusPicker(true,true,true,"Rename","#32770",true,true), "rename rejected");
        Assert(!FileDialogIdentityPolicy.CanRefocusPicker(true,true,true,"Open","Qt622QWindowIcon",true,true), "editor rejected");
        Assert(!FileDialogIdentityPolicy.CanRefocusPicker(true,true,true,"Open","#32770",false,true), "missing field rejected");
        Assert(!FileDialogIdentityPolicy.CanRefocusPicker(true,true,true,"Open","#32770",true,false), "missing Open rejected");
    }

    private static void TestMediaFileDialogIdentity()
    {
        string[] titles = { "请选择媒体资源", "Open", "Select media" };
        Assert(!FileDialogIdentityPolicy.IsVerifiedMediaDialog("重命名", "#32770", false, titles),
            "generic rename dialog must not be accepted as a media picker");
        Assert(!FileDialogIdentityPolicy.IsVerifiedMediaDialog("Rename", "#32770", false, titles),
            "English rename dialog must not be accepted as a media picker");
        Assert(!FileDialogIdentityPolicy.IsVerifiedMediaDialog("重命名", "#32770", true, titles),
            "rename dialog must remain rejected even if it exposes a filename-labelled edit");
        Assert(FileDialogIdentityPolicy.IsSafeFileNameControl("1148", ""),
            "native file picker filename control should be accepted");
        Assert(FileDialogIdentityPolicy.IsVerifiedMediaDialog("请选择媒体资源", "#32770", true, titles),
            "verified media picker with a dedicated filename field should be accepted");
        Assert(!FileDialogIdentityPolicy.IsSafeFileNameControl("", "重命名"),
            "rename edit must never be treated as a filename picker field");
        Assert(CapCutImportDialogPolicy.IsCandidate("打开", "#32770",
            true, true, false, true, false, true),
            "a directly-owned native Open dialog must be accepted");
        Assert(CapCutImportDialogPolicy.IsCandidate("", "#32770",
            true, true, false, false, true, true),
            "a newly foregrounded empty-title picker with an editor owner chain must be accepted for worker verification");
        Assert(!CapCutImportDialogPolicy.IsCandidate("", "#32770",
            true, true, false, false, false, true),
            "a merely-new native dialog without an editor relationship must be rejected");
        Assert(!CapCutImportDialogPolicy.IsCandidate("另存为", "#32770",
            true, true, true, true, true, true),
            "Save As must remain rejected even when owned by CapCut");
        Assert(CapCutImportDialogPolicy.IsCandidate("选择文件资源", "#32770",
            true, true, false, false, true, false),
            "localized owner-bound existing picker must reach structured control verification");
        string mediaPath = Path.Combine(Path.GetTempPath(), "exact.mp4");
        string metadata = new JavaScriptSerializer().Serialize(new {
            draft_materials = new[] { new { value = new[] {
                new { file_Path = mediaPath, id = "exact-material" }
            } } }
        });
        Assert(MediaImportFacts.FindMaterialId(metadata, mediaPath) == "exact-material",
            "media bin fact must identify exact material");
        Assert(MediaImportFacts.FindMaterialId(metadata, Path.Combine(Path.GetTempPath(), "other.mp4")) == "",
            "another thumbnail/material must not confirm this import");
        Assert(MediaImportFacts.FindMaterialId("{\"draft_materials\":[]}", mediaPath) == "",
            "closed picker with empty media bin is not success");
        Assert(MediaImportFacts.IsUnresolved("path_submission_intent") &&
            MediaImportFacts.IsUnresolved("dialog_closed") && !MediaImportFacts.IsUnresolved("import_waiting"),
            "submitted but unverified commands must reconcile before resending");
        Assert(MediaImportFacts.CaptionSignature("{\"tracks\":[{\"type\":\"video\",\"segments\":[{\"id\":\"colored-video\"}]}]}") == "",
            "video color cannot prove caption completion");
        Assert(MediaImportFacts.CaptionSignature("{\"tracks\":[{\"type\":\"text\",\"segments\":[{\"id\":\"c1\",\"material_id\":\"t1\",\"target_timerange\":{\"start\":0,\"duration\":1000000}}]}]}") != "",
            "persisted timed text segment proves caption progress");
    }

    private static void TestHdrNoticeVision()
    {
        using (var bitmap = new Bitmap(2560, 1392)) {
            using (Graphics graphics = Graphics.FromImage(bitmap)) {
                graphics.Clear(Color.FromArgb(15, 15, 15));
                using (var brush = new SolidBrush(Color.FromArgb(20, 190, 198))) {
                    graphics.FillRectangle(brush, 1150, 615, 260, 24);
                }
            }
            System.Drawing.Point point;
            Assert(HdrNoticeVision.TryFindAcknowledgement(bitmap, out point),
                "real HDR acknowledgement vertical band should be detected");
            Assert(point.X >= 1150 && point.X <= 1410 && point.Y >= 615 && point.Y <= 639,
                "HDR acknowledgement click must remain inside the cyan button");
        }

        using (var unrelated = new Bitmap(2560, 1392)) {
            using (Graphics graphics = Graphics.FromImage(unrelated)) {
                graphics.Clear(Color.Black);
                graphics.FillRectangle(Brushes.Cyan, 2300, 20, 180, 25);
            }
            System.Drawing.Point ignored;
            Assert(!HdrNoticeVision.TryFindAcknowledgement(unrelated, out ignored),
                "top-right export controls must never match the centred HDR acknowledgement");
        }
    }

    private static void TestFirstCaptionAtZeroFrameMath()
    {
        int expected = FirstCaptionFrameValidation.ExpectedFramePixels(182, 195);
        Assert(expected == 13, "expected calibrated frame distance");
        Assert(FirstCaptionFrameValidation.StartsAtZero(187, 182, expected),
            "rounded caption edge inside the first frame should be eligible");
        FirstCaptionFrameValidation.RequireExactlyOneFrame(FirstCaptionFrameValidation.MovedPixels(187, 200), expected);
        FirstCaptionFrameValidation.RequireLeftEdgeOnly(187, 420, 200, 420, expected);
    }

    private static void TestLateFirstCaptionIsSkipped()
    {
        Assert(!FirstCaptionFrameValidation.StartsAtZero(195, 182, 13), "caption one frame late must not be trimmed");
    }

    private static void TestFirstCaptionNoOpBlocksExport()
    {
        bool blocked = false;
        try {
            FirstCaptionFrameValidation.RequireExactlyOneFrame(0, 13);
        } catch (InvalidOperationException) {
            blocked = true;
        }
        Assert(blocked, "a Q no-op must block export");
    }

    private static void TestFirstCaptionRightEdgeMustStayFixed()
    {
        bool blocked = false;
        try {
            FirstCaptionFrameValidation.RequireLeftEdgeOnly(187, 420, 200, 421, 13);
        } catch (InvalidOperationException) {
            blocked = true;
        }
        Assert(blocked, "moving the right edge must block export");
    }

    private static void TestFirstCaptionScanStartsBeforeZero()
    {
        double zero = 183.0 / 2560.0;
        double scan = FirstCaptionFrameValidation.CaptionScanLeftRatio(zero, true);
        Assert(scan < zero, "caption scan must begin left of the calibrated zero edge");
        Assert((int)(scan * 2560) <= 183 - 20,
            "caption scan must leave enough room to observe the true outer edge");
        Assert(Math.Abs(FirstCaptionFrameValidation.CaptionScanLeftRatio(0, false) - 0.06) < 0.000001,
            "missing calibration must use the safe fallback scan start");
    }

    private static void TestLegacyFirstCaptionMigration()
    {
        AppConfig config = AppConfig.CreateDefault();
        config.Locators["FirstCaptionAtZeroProbe"] = new LocatorRule {
            RelativeX = 0.078804001212120056, RelativeY = 0.78125
        };
        config.Locators["FirstCaptionLeftEdge"] = new LocatorRule {
            RelativeX = 0.074146002531051636, RelativeY = 0.78125
        };
        config.Locators["FirstCaptionOneFrameRight"] = new LocatorRule {
            RelativeX = 0.074922002851963043, RelativeY = 0.64133501052856445
        };
        config.Locators["Export"] = new LocatorRule { RelativeX = 0.91, RelativeY = 0.02 };
        Assert(config.ApplyLocalVisibleBoundsFirstCaptionCalibration(),
            "the exact legacy home triplet must migrate once");
        Assert(Math.Abs(config.GetRule("FirstCaptionLeftEdge").RelativeX - 183.0 / 2560.0) < 0.000001,
            "migrated zero edge must remain at physical x=183");
        Assert(Math.Abs(config.GetRule("FirstCaptionOneFrameRight").RelativeX - 185.0 / 2560.0) < 0.000001,
            "migrated next frame must remain at physical x=185");
        Assert(Math.Abs(config.GetRule("Export").RelativeX - 0.91) < 0.000001,
            "migration must not touch any other calibration");
        Assert(!config.ApplyLocalVisibleBoundsFirstCaptionCalibration(),
            "already migrated coordinates must not be rewritten again");
    }

    private static void TestRemoteHandoffAudioNumberCompatibility()
    {
        Assert(HandoffManifestImporter.IsTaskNumberCompatible(
            "ProjectB_净白板_英语_remote-inbox-20260721022128-d475c8cb-01_净白板-英语-2.mp3", "001"),
            "remote receiver audio should not be rejected by the legacy number rule");
        Assert(!HandoffManifestImporter.IsTaskNumberCompatible(
            "ProjectB_净白板_英语_002.mp3", "001"),
            "ordinary legacy audio must still validate its task number");
    }

    private static void TestOwnedHandoffAcceptsCopiedAudioName()
    {
        // Task-scoped lab and workbench copies retain the durable task id, not
        // the legacy CapCut output number.  Ownership is the authoritative
        // pairing evidence, so this must not be rejected by filename alone.
        Assert(!String.IsNullOrWhiteSpace("lab-20260724-spc-floor-001"),
            "test fixture must have a task owner");
        Assert(!HandoffManifestImporter.IsTaskNumberCompatible(
            "lab-20260724-spc-floor-001_existing-english.mp3", "001"),
            "fixture must prove it does not accidentally satisfy legacy naming");
        // Import now skips the legacy compatibility check whenever this owner
        // is present; that branch is exercised by the real manifest smoke test.
    }

    private static void TestNewHandoffClearsTerminalStaleHandoff()
    {
        WithTempDirectory(delegate(string dir) {
            string manifest = Path.Combine(dir, "handoff.json");
            File.WriteAllText(manifest, new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                { "schemaVersion", 1 }, { "batchId", "new-batch" }, { "tasks", new object[0] }
            }));
            var stale = new VideoTask { Id = "stale", IsHandoffTask = true, HandoffBatchId = "old-batch" };
            stale.SetState(TaskState.Failed);
            var manual = new VideoTask { Id = "manual", IsHandoffTask = false };
            var current = new VideoTask { Id = "current", IsHandoffTask = true, HandoffBatchId = "new-batch" };
            var tasks = new List<VideoTask> { stale, manual, current };
            HandoffManifestImporter.Import(manifest, AppConfig.CreateDefault(), tasks);
            Assert(tasks.All(item => item.Id != "stale"), "terminal stale handoff should be removed");
            Assert(tasks.Any(item => item.Id == "manual"), "manual task must remain");
            Assert(tasks.Any(item => item.Id == "current"), "current handoff must remain");
        });
    }

    private static void TestHandoffStatusPersistence()
    {
        WithTempDirectory(delegate(string dir) {
            string status = Path.Combine(dir, "status.json");
            var task = new VideoTask {
                Id = "handoff-task",
                IsHandoffTask = true,
                HandoffBatchId = "batch-one",
                HandoffStatusFile = status,
                CanonicalTaskId = "canonical-output-02",
                RunGroupId = "run-examplebrand-01",
                ContractBatchId = "examplebrand-batch-01",
                BatchKind = "examplebrand-natural-flow",
                RemotePipelineTaskId = "remote-cloud-001",
                ExpectedTaskCount = 24,
                ProjectName = "ProjectA",
                MaterialName = "碳岩板",
                LanguageName = "西班牙语"
            };
            task.SetState(TaskState.Failed);
            task.Error = "test failure";
            task.FailureFingerprint = "CAPCUT_MAIN_WINDOW_TRANSITION_TIMEOUT";
            HandoffStatusWriter.WriteAll(new[] { task }, false, false);
            Assert(File.Exists(status), "status file was not written");
            string content = File.ReadAllText(status);
            Assert(content.Contains("handoff-task"), "status file does not contain task ID");
            Assert(content.Contains("test failure"), "status file does not contain task error");
            Assert(content.Contains("remote-cloud-001"), "status file does not keep remote project ownership");
            Assert(content.Contains("expectedTaskCount"), "status file does not keep the expected task count");
            Assert(content.Contains("canonical-output-02") && content.Contains("run-examplebrand-01") &&
                   content.Contains("examplebrand-batch-01") && content.Contains("examplebrand-natural-flow"),
                "status file must keep the complete task-scoped production identity");
            Assert(content.Contains("CAPCUT_MAIN_WINDOW_TRANSITION_TIMEOUT"),
                "status file must publish the structured CapCut error code");
        });
    }

    private static void TestCategorySampleRouting()
    {
        Assert(CategoryCatalog.SampleDirectoryFor("ProjectB", "碳岩板", "hancai", "normal", "pu") == "normal",
            "entity normal material should use the entity normal library");
        Assert(CategoryCatalog.SampleDirectoryFor("ProjectB", "PU软石", "hancai", "normal", "pu") == "pu",
            "entity PU material should use the entity PU library");
        Assert(CategoryCatalog.SampleDirectoryFor("ProjectA", "碳岩板", "hancai", "normal", "pu") == "hancai",
            "non-entity categories should use the ExampleBrandC library");
        Assert(CategoryCatalog.SampleDirectoryFor("ProjectD", "碳岩板", "hancai", "normal", "pu") == "hancai",
            "real Huang category should use the ExampleBrandC library");
        Assert(CategoryCatalog.SampleDirectoryFor("SPC墙板", "晶岩墙板", "hancai", "normal", "pu") == "hancai",
            "SPC wall board should use the ExampleBrandC library");
    }

    private static void TestCategoryFullNameMatching()
    {
        WithTempDirectory(delegate(string dir) {
            string audioA = Path.Combine(dir, "ProjectB_碳岩板_英语_001_碳岩板-英语-1.mp3");
            string audioB = Path.Combine(dir, "ProjectB_金属板_英语_002_金属板-英语-1.mp3");
            string videoA = Path.Combine(dir, "ProjectB_ProjectB_碳岩板_英语_001_碳岩板-英语-1_0001.mp4");
            string videoB = Path.Combine(dir, "ProjectB_ProjectB_金属板_英语_002_金属板-英语-1_0001.mp4");
            string otherCategory = Path.Combine(dir, "ProjectA_ProjectA_碳岩板_西班牙语_003_碳岩板-西班牙语-4_0001.mp4");
            CategoryScanResult result = CategoryBatchMatcher.Discover(
                "ProjectB", new[] { videoA, videoB, otherCategory }, new[] { audioA, audioB });
            Assert(result.Groups.Count == 2, "same language with different full voice tasks must remain separate");
            Assert(result.Groups.All(group => group.Videos.Count == 1), "each voice task should match one video");
            Assert(result.Issues.Count == 0, "other categories in the same root must not create errors");
        });
    }

    private static void TestSpcFixedMaterialValidation()
    {
        string valid = "SPC地板_塑木地板_德语_001_测试.mp3";
        string invalid = "SPC地板_碳岩板_德语_002_测试.mp3";
        CategoryScanResult result = CategoryBatchMatcher.Discover(
            "SPC地板",
            new[] { "SPC地板_SPC地板_塑木地板_德语_001_测试_0001.mp4" },
            new[] { valid, invalid });
        Assert(result.Groups.Count == 1, "SPC floor must accept only plastic wood flooring");
        Assert(result.Issues.Any(issue => issue.Contains("碳岩板")), "invalid SPC material should be reported");
    }

    private static void TestEachVideoUsesIndependentDraft()
    {
        WithTempDirectory(delegate(string dir) {
            string firstVideo = Path.Combine(dir, "first.mp4");
            string secondVideo = Path.Combine(dir, "second.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            string sample = Path.Combine(dir, "sample.mp4");
            File.WriteAllText(firstVideo, "video");
            File.WriteAllText(secondVideo, "video");
            File.WriteAllText(audio, "audio");
            File.WriteAllText(sample, "sample");
            var tasks = new List<VideoTask> {
                NewGroupTask(firstVideo, audio, sample, Path.Combine(dir, "ProjectA西班牙1.mp4"), 1),
                NewGroupTask(secondVideo, audio, sample, Path.Combine(dir, "ProjectA西班牙2.mp4"), 2)
            };
            AppConfig config = AppConfig.CreateDefault();
            config.RetryCount = 0;
            var automation = new MockAutomation();
            var runner = new BatchRunner(config, automation, new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), null, new MockPostProcessor());
            runner.RunBlocking(tasks, CancellationToken.None);

            Assert(tasks.All(task => task.GetState() == TaskState.Completed), "all group tasks should complete");
            Assert(automation.CreateProjectCalls == 2, "each video should create its own draft");
            Assert(automation.AutoCaptionCalls == 2, "captions should be generated for every video");
            Assert(automation.ApplySubtitleStyleCalls == 2, "subtitle style should be applied for every video");
            Assert(automation.LastSubtitleStyle == "CompanyStandard", "unexpected subtitle style");
            Assert(automation.PreparedSampleCalls == 2, "every task must prepare its own sample before CapCut import");
            Assert(automation.ImportSampleCalls == 2, "each draft should import its own sample");
            Assert(automation.ReplaceMainVideoCalls == 0, "independent drafts must not replace old main videos");
            Assert(automation.ReplaceSampleCalls == 0, "independent drafts must not replace old samples");
            Assert(automation.ReturnToEditorCalls == 0, "independent drafts should close after export");
            Assert(automation.LaunchCalls == 2, "each task should verify or restore the CapCut editor before creating its draft");
            Assert(automation.DeleteCurrentDraftCalls == 2, "each task draft should be cleaned separately");
            Assert(automation.CloseSessionCalls == 2,
                "each independent task must close its owned CapCut session before the next task");
            Assert(automation.ResetProjectCalls == 0, "normal completion should not reset the whole session");
            Assert(automation.Exported.All(name => name.Contains(".capcut-temp-")),
                "CapCut must export only uniquely named temporary outputs");
            Assert(tasks.All(task => File.Exists(task.OutputPath) && File.Exists(task.TemporaryOutputPath)),
                "final and temporary outputs must both remain until cloud verification");
        });
    }

    private static void TestSourceRecycleOrder()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "source.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            string sample = Path.Combine(dir, "sample.mp4");
            File.WriteAllText(video, "video");
            File.WriteAllText(audio, "audio");
            File.WriteAllText(sample, "sample");
            VideoTask task = NewGroupTask(video, audio, sample, Path.Combine(dir, "out", "done.mp4"), 1);
            task.SourceRootPath = dir;
            task.DeleteSourceAfterSuccess = true;
            var tasks = new List<VideoTask> { task };
            AppConfig config = AppConfig.CreateDefault();
            config.RetryCount = 0;
            var automation = new MockAutomation();
            var recycler = new MockRecycler(automation);
            var runner = new BatchRunner(
                config, automation, new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), recycler, new MockPostProcessor());
            runner.RunBlocking(tasks, CancellationToken.None);
            Assert(task.GetState() == TaskState.Completed, "locally completed task should complete");
            Assert(!task.SourceRecycled && recycler.Paths.Count == 0,
                "local completion must not delete or move source before cloud verification");
            Assert(File.Exists(video) && File.Exists(task.OutputPath) && File.Exists(task.TemporaryOutputPath),
                "source, temporary and final media must remain for cloud cleanup");
            Assert(automation.DeleteCurrentDraftCalls == 1, "draft may be deleted only after verified final output");
        });
    }

    private static void TestExportFailurePreservesSource()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "source.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            string sample = Path.Combine(dir, "sample.mp4");
            File.WriteAllText(video, "video"); File.WriteAllText(audio, "audio"); File.WriteAllText(sample, "sample");
            VideoTask task = NewGroupTask(video, audio, sample, Path.Combine(dir, "out", "done.mp4"), 1);
            task.SourceRootPath = dir;
            task.TaskOrigin = TaskOrigins.CategoryScan;
            task.DeleteSourceAfterSuccess = true;
            var automation = new MockAutomation { FailExport = true };
            var recycler = new MockRecycler(automation);
            var runner = new BatchRunner(AppConfig.CreateDefault(), automation,
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), recycler, new MockPostProcessor());
            runner.RunBlocking(new List<VideoTask> { task }, CancellationToken.None);
            Assert(task.GetState() == TaskState.Failed, "export failure must fail task");
            Assert(File.Exists(video), "export failure must preserve source");
            Assert(recycler.Paths.Count == 0, "export failure must not call recycler");
        });
    }

    private static void TestPostProcessFailurePreservesEverything()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "source.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            File.WriteAllText(video, "video");
            File.WriteAllText(audio, "audio");
            VideoTask task = NewGroupTask(video, audio, "", Path.Combine(dir, "out", "done.mp4"), 1);
            task.SourceRootPath = dir;
            task.DeleteSourceAfterSuccess = true;
            var automation = new MockAutomation();
            var processor = new MockPostProcessor { Fail = true };
            var recycler = new MockRecycler(automation);
            var runner = new BatchRunner(AppConfig.CreateDefault(), automation,
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), recycler, processor);
            runner.RunBlocking(new List<VideoTask> { task }, CancellationToken.None);
            Assert(task.GetState() == TaskState.Failed, "FFmpeg failure must fail the task");
            Assert(File.Exists(video), "FFmpeg failure must preserve source video");
            Assert(!String.IsNullOrWhiteSpace(task.TemporaryOutputPath) && File.Exists(task.TemporaryOutputPath),
                "FFmpeg failure must preserve CapCut temporary output");
            Assert(!File.Exists(task.OutputPath), "failed FFmpeg must not publish a final output");
            Assert(automation.DeleteCurrentDraftCalls == 0, "FFmpeg failure must preserve the draft");
            Assert(recycler.Paths.Count == 0, "FFmpeg failure must perform no source cleanup");
        });
    }

    private static void TestResumableTemporaryExportSkipsCapCut()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "source.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            File.WriteAllText(video, "video");
            File.WriteAllText(audio, "audio");
            VideoTask task = NewGroupTask(video, audio, "", Path.Combine(dir, "out", "done.mp4"), 1);
            task.Id = "82c7469c9f25fe8b8b8a824b";
            task.Attempts = 3;
            task.StartedAt = DateTime.Now.AddMinutes(-5).ToString("o");
            task.TemporaryOutputPath = PostProcessPaths.CapCutTemporaryPath(
                task.OutputPath, "a0003-" + task.Id);
            task.DirectDraftCheckpoint = new DirectDraftCheckpoint {
                State = "style_applied",
                DraftDirectory = Path.Combine(dir, "draft"),
                DraftId = "must-not-reopen",
                DraftName = "must-not-reopen",
                SourceVideoPath = video,
                CaptionCount = 1,
                UpdatedAt = DateTime.Now.AddMinutes(-10).ToString("o")
            };
            Directory.CreateDirectory(Path.GetDirectoryName(task.TemporaryOutputPath));
            File.WriteAllText(task.TemporaryOutputPath, new string('x', 2048));
            File.SetLastWriteTime(task.TemporaryOutputPath, DateTime.Now.AddHours(-8));

            var automation = new MockAutomation();
            var processor = new MockPostProcessor();
            var runner = new BatchRunner(AppConfig.CreateDefault(), automation,
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), null, processor);
            runner.RunBlocking(new List<VideoTask> { task }, CancellationToken.None);

            Assert(task.GetState() == TaskState.Completed, "valid historical temporary output should resume to completed; state=" + task.State + "; error=" + task.Error);
            Assert(automation.LaunchCalls == 0 && automation.CreateProjectCalls == 0 && automation.Exported.Count == 0,
                "resuming a verified temporary output must not reopen CapCut or export again; launch=" +
                automation.LaunchCalls + ", create=" + automation.CreateProjectCalls + ", export=" + automation.Exported.Count);
            Assert(Path.GetFileName(task.TemporaryOutputPath).Contains(".capcut-temp-a0003-82c746"),
                "attempt-first truncated task identity must remain recoverable");
            Assert(processor.TemporaryPaths.Count == 1 && processor.TemporaryPaths[0] == task.TemporaryOutputPath,
                "FFmpeg must receive the existing temporary output");
        });
    }

    private static void TestPreviousTemporaryAttemptResumes()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "source.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            File.WriteAllText(video, "video");
            File.WriteAllText(audio, "audio");
            VideoTask task = NewGroupTask(video, audio, "",
                Path.Combine(dir, "out", "done.mp4"), 1);
            task.Id = "938a2026ad5ba7ff24a02b29";
            task.Attempts = 2;
            task.TemporaryOutputPath = PostProcessPaths.CapCutTemporaryPath(
                task.OutputPath, "a0002-" + task.Id);
            string previousAttempt = PostProcessPaths.CapCutTemporaryPath(
                task.OutputPath, "a0001-" + task.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(previousAttempt));
            File.WriteAllText(previousAttempt, new string('y', 4096));

            var automation = new MockAutomation();
            var processor = new MockPostProcessor();
            var runner = new BatchRunner(AppConfig.CreateDefault(), automation,
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), null, processor);
            runner.RunBlocking(new List<VideoTask> { task }, CancellationToken.None);

            Assert(task.GetState() == TaskState.Completed,
                "a previous stable attempt should resume instead of exporting again: " + task.Error);
            Assert(automation.Exported.Count == 0,
                "completion-page lock recovery must not click Export again");
            Assert(Path.GetFileName(task.TemporaryOutputPath)
                    .Contains(".capcut-temp-a0002-938a20") &&
                    File.Exists(task.TemporaryOutputPath),
                "the previous task-scoped attempt was not safely promoted to the registered path: " +
                task.TemporaryOutputPath);
        });
    }

    private static void TestCapCutTemporaryExportNameLimit()
    {
        string path = PostProcessPaths.CapCutTemporaryPath(
            Path.Combine(Path.GetTempPath(), "FirstCaption-A-20260721-161715.mp4"),
            "26220de61525499ca1823c8daf27cd64");
        string stem = Path.GetFileNameWithoutExtension(path);
        Assert(stem.Length <= 50, "CapCut temporary export stem must not exceed 50 characters");
        Assert(stem.EndsWith(".capcut-temp-26220de61525", StringComparison.Ordinal),
            "CapCut temporary export name must retain its unique task suffix");
    }

    private static void TestCapCutRetryUsesFreshTemporaryExportName()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "source.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            File.WriteAllText(video, "video");
            File.WriteAllText(audio, "audio");
            VideoTask task = NewGroupTask(video, audio, "", Path.Combine(dir, "out", "final.mp4"), 1);
            task.Id = "e31211d40449450a89a18a0ddfd6574a";
            task.Attempts = 2;
            var automation = new MockAutomation();
            var runner = new BatchRunner(AppConfig.CreateDefault(), automation,
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), null, new MockPostProcessor());

            runner.RunBlocking(new List<VideoTask> { task }, CancellationToken.None);

            string tempName = Path.GetFileName(task.TemporaryOutputPath);
            Assert(tempName.IndexOf(".capcut-temp-a0003-", StringComparison.OrdinalIgnoreCase) >= 0,
                "retry export name must preserve incremented attempt before the truncated ID: " + tempName);
        });
    }

    private static void TestCapCutExportSkipsExistingCandidateName()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "source.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            File.WriteAllText(video, "video");
            File.WriteAllText(audio, "audio");
            VideoTask task = NewGroupTask(video, audio, "", Path.Combine(dir, "out", "final.mp4"), 1);
            task.Id = "e31211d40449450a89a18a0ddfd6574a";
            string occupied = PostProcessPaths.CapCutTemporaryPath(
                task.OutputPath, "a0001-" + task.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(occupied));
            File.WriteAllText(occupied, "old export that must not be overwritten");
            var automation = new MockAutomation();
            var runner = new BatchRunner(AppConfig.CreateDefault(), automation,
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), null, new MockPostProcessor());

            runner.RunBlocking(new List<VideoTask> { task }, CancellationToken.None);

            Assert(task.GetState() == TaskState.Completed,
                "an old temporary output must not block a fresh export");
            Assert(Path.GetFileName(task.TemporaryOutputPath).Contains(".capcut-temp-a0002-"),
                "the fresh export must skip occupied a0001 without opening an overwrite dialog: " +
                Path.GetFileName(task.TemporaryOutputPath));
            Assert(File.ReadAllText(occupied) == "old export that must not be overwritten",
                "the occupied temporary output must be preserved");
        });
    }

    private static void TestFirstFrameWorkingPathUsesRetryMarker()
    {
        string finalPath = Path.Combine(Path.GetTempPath(),
            "SubtitleStyleLab1-lab-rerun-20260726-024829.mp4");
        string second = PostProcessPaths.WorkingOutputPath(
            finalPath, "SubtitleStyleLab1.capcut-temp-a0002-e31211d40449");
        string third = PostProcessPaths.WorkingOutputPath(
            finalPath, "SubtitleStyleLab1.capcut-temp-a0003-e31211d40449");
        Assert(!String.Equals(second, third, StringComparison.OrdinalIgnoreCase),
            "FFmpeg working files must differ across retry attempts");
        Assert(third.IndexOf("a0003-e3121", StringComparison.OrdinalIgnoreCase) >= 0,
            "FFmpeg working file must retain the retry marker");
        Assert(Path.GetFileName(third).StartsWith("ff-first-frame-working-", StringComparison.OrdinalIgnoreCase),
            "FFmpeg working file must use a compact fixed stem");
        string veryLongFinal = Path.Combine(Path.GetTempPath(),
            new string('x', 190) + ".mp4");
        string compact = PostProcessPaths.WorkingOutputPath(
            veryLongFinal, "anything.capcut-temp-a0001-abcdef123456");
        Assert(Path.GetFileName(compact).Length < 50,
            "FFmpeg working filename must not inherit a long final-output stem: " + compact);
    }

    private static void TestIsolatedDraftRetentionIsNotReportedAsError()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "source.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            File.WriteAllText(video, "video");
            File.WriteAllText(audio, "audio");
            VideoTask task = NewGroupTask(video, audio, "", Path.Combine(dir, "out", "final.mp4"), 1);
            task.PreserveDraftAfterSuccess = true;
            var runner = new BatchRunner(AppConfig.CreateDefault(), new MockAutomation(),
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), null, new MockPostProcessor());

            runner.RunBlocking(new List<VideoTask> { task }, CancellationToken.None);

            Assert(task.GetState() == TaskState.Completed,
                "isolated task must remain completed when its draft is intentionally retained");
            Assert(String.IsNullOrWhiteSpace(task.Error),
                "intentional isolated draft retention must not populate the error column");
        });
    }

    private static void TestDraftDeletionFailurePreservesSource()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "source-1.mp4");
            string secondVideo = Path.Combine(dir, "source-2.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            string sample = Path.Combine(dir, "sample.mp4");
            File.WriteAllText(video, "video"); File.WriteAllText(secondVideo, "video");
            File.WriteAllText(audio, "audio"); File.WriteAllText(sample, "sample");
            VideoTask task = NewGroupTask(video, audio, sample, Path.Combine(dir, "out", "done.mp4"), 1);
            VideoTask secondTask = NewGroupTask(secondVideo, audio, sample, Path.Combine(dir, "out", "done-2.mp4"), 2);
            task.SourceRootPath = dir;
            secondTask.SourceRootPath = dir;
            task.TaskOrigin = TaskOrigins.TikHubHandoff;
            secondTask.TaskOrigin = TaskOrigins.TikHubHandoff;
            task.DeleteSourceAfterSuccess = true;
            secondTask.DeleteSourceAfterSuccess = true;
            var automation = new MockAutomation { FailDeleteDraft = true };
            var recycler = new MockRecycler(automation);
            var runner = new BatchRunner(AppConfig.CreateDefault(), automation,
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), recycler, new MockPostProcessor());
            runner.RunBlocking(new List<VideoTask> { task, secondTask }, CancellationToken.None);
            Assert(task.GetState() == TaskState.Completed && secondTask.GetState() == TaskState.Completed,
                "verified outputs must remain completed when draft cleanup is pending");
            Assert(automation.Exported.Count == 2, "draft cleanup failure must not stop the next task");
            Assert(File.Exists(video) && File.Exists(secondVideo), "draft deletion failure must preserve source");
            Assert(recycler.Paths.Count == 0, "draft deletion failure must not call recycler");
            Assert(File.Exists(task.OutputPath) && File.Exists(secondTask.OutputPath),
                "draft deletion failure must retain every verified final output for upload");
            Assert(String.IsNullOrWhiteSpace(task.Error) && String.IsNullOrWhiteSpace(secondTask.Error),
                "post-success draft cleanup must not populate the production error field");
            Assert(task.DraftCleanupStatus == "pending" && secondTask.DraftCleanupStatus == "pending",
                "completed tasks must expose pending draft cleanup separately");
            Assert(task.DraftCleanupWarning.Contains("草稿待清理") && secondTask.DraftCleanupWarning.Contains("草稿待清理"),
                "completed tasks must retain an explicit draft cleanup warning outside Error");
        });
    }

    private static void TestVerifiedOutputSurvivesDraftReleaseFailure()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "source.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            File.WriteAllText(video, "video");
            File.WriteAllText(audio, "audio");
            VideoTask task = NewGroupTask(video, audio, "",
                Path.Combine(dir, "out", "done.mp4"), 1);
            task.QualityStatus = "passed";
            var automation = new ScopedMockAutomation { FailRelease = true };
            var runner = new BatchRunner(AppConfig.CreateDefault(), automation,
                new TaskStore(Path.Combine(dir, "tasks.json")),
                new AppLogger(), null, new MockPostProcessor());
            runner.RunBlocking(new List<VideoTask> { task }, CancellationToken.None);
            Assert(task.GetState() == TaskState.Completed,
                "a verified output must stay completed when draft release fails");
            Assert(String.IsNullOrWhiteSpace(task.Error),
                "draft release failure must not populate the production error field");
            Assert(task.ProductionStatus == "completed" &&
                task.DraftReleaseStatus == "pending" &&
                task.DraftCleanupStatus == "release_pending",
                "production and draft release facts must remain separate");
            Assert(task.FailureFingerprint == "CAPCUT_DRAFT_RELEASE_PENDING",
                "release failure must expose a structured pending fingerprint");
            Assert(File.Exists(task.OutputPath),
                "verified output must remain available after draft release failure");
        });
    }

    private static void TestDraftArchivePathPreflight()
    {
        WithTempDirectory(delegate(string dir) {
            string source = Path.Combine(dir, "source");
            string nested = Path.Combine(source, new string('n', 80));
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "draft_content.json"), "{}");
            MethodInfo probe = typeof(TaskDraftRootLeaseManager).GetMethod(
                "ValidateCopyPathLengths", BindingFlags.NonPublic | BindingFlags.Static);
            bool rejected = false;
            try {
                probe.Invoke(null, new object[] {
                    source, Path.Combine(dir, new string('d', 220)) });
            } catch (TargetInvocationException ex) {
                rejected = ex.InnerException != null &&
                    ex.InnerException.Message.Contains(
                        "CAPCUT_DRAFT_ARCHIVE_PATH_TOO_LONG");
            }
            Assert(rejected,
                "archive preflight must reject a destination beyond the safe legacy path budget");
            probe.Invoke(null, new object[] { source, Path.Combine(dir, "short") });
        });
    }

    private static void TestProtectedMediaIsNeverRecycled()
    {
        WithTempDirectory(delegate(string dir) {
            string protectedRoot = Path.Combine(dir, "samples");
            Directory.CreateDirectory(protectedRoot);
            string video = Path.Combine(protectedRoot, "logo.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            string sample = Path.Combine(dir, "sample.mp4");
            File.WriteAllText(video, "video"); File.WriteAllText(audio, "audio"); File.WriteAllText(sample, "sample");
            VideoTask task = NewGroupTask(video, audio, sample, Path.Combine(dir, "out", "done.mp4"), 1);
            task.SourceRootPath = dir;
            task.TaskOrigin = TaskOrigins.CategoryScan;
            task.DeleteSourceAfterSuccess = true;
            AppConfig config = AppConfig.CreateDefault();
            config.SampleDirectory = protectedRoot;
            var automation = new MockAutomation();
            var recycler = new MockRecycler(automation);
            var runner = new BatchRunner(config, automation,
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), recycler, new MockPostProcessor());
            runner.RunBlocking(new List<VideoTask> { task }, CancellationToken.None);
            Assert(task.GetState() == TaskState.Completed, "local task should complete without attempting media cleanup");
            Assert(File.Exists(video) && File.Exists(audio) && File.Exists(sample) && File.Exists(task.OutputPath),
                "video/audio/sample/final files must all be retained");
            Assert(recycler.Paths.Count == 0, "protected media must not reach recycler");
        });
    }

    private static void TestSourceDeletionPolicy()
    {
        Assert(SourceDeletionPolicy.ShouldRecycle(TaskOrigins.TikHubHandoff, false), "handoff must register source for cloud-gated cleanup");
        Assert(SourceDeletionPolicy.ShouldRecycle(TaskOrigins.CategoryScan, false), "category scan must register source for cloud-gated cleanup");
        Assert(!SourceDeletionPolicy.ShouldRecycle(TaskOrigins.Manual, false), "manual task must default to preserving source");
        Assert(SourceDeletionPolicy.ShouldRecycle(TaskOrigins.Manual, true), "manual opt-in must enable recycling");
    }

    private static void TestWorkbenchCannotEnableDiagnostics()
    {
        bool blocked = false;
        try {
            DiagnosticLaunchPolicy.ValidateFirstCaptionQNoOp(true, true, "handoff.json");
        } catch (InvalidOperationException) {
            blocked = true;
        }
        Assert(blocked, "workbench/auto-start must not enable diagnostic Q no-op");
        DiagnosticLaunchPolicy.ValidateFirstCaptionQNoOp(true, false, "");
    }

    private static void TestLocalFirstCaptionTestLauncher()
    {
        WithTempDirectory(delegate(string dir) {
            File.WriteAllText(Path.Combine(dir, "A_主视频_首帧应有字幕.mp4"), "video");
            File.WriteAllText(Path.Combine(dir, "A_独立音频_无前置静音.wav"), "audio");
            File.WriteAllText(Path.Combine(dir, "B_主视频_首帧不应有字幕.mp4"), "video");
            File.WriteAllText(Path.Combine(dir, "B_独立音频_前置2秒静音.wav"), "audio");
            File.WriteAllText(Path.Combine(dir, "C_主视频_Q失败应拦截.mp4"), "video");
            File.WriteAllText(Path.Combine(dir, "C_独立音频_无前置静音.wav"), "audio");
            AppConfig config = AppConfig.CreateDefault();
            VideoTask a = LocalFirstCaptionTestFactory.Create("A", dir, config);
            VideoTask b = LocalFirstCaptionTestFactory.Create("B", dir, config);
            VideoTask c = LocalFirstCaptionTestFactory.Create("C", dir, config);
            Assert(a.DeleteSourceAfterSuccess && b.DeleteSourceAfterSuccess,
                "A and B must opt in to source recycling after all gates pass");
            Assert(!c.DeleteSourceAfterSuccess && c.PreserveDraftOnFailure,
                "C must preserve its source and diagnostic draft");
            bool blocked = false;
            try { LocalFirstCaptionTestFactory.ValidateLaunch("C", dir, false, ""); }
            catch (InvalidOperationException) { blocked = true; }
            Assert(blocked, "C without the local diagnostic switch must be rejected");
            blocked = false;
            try { LocalFirstCaptionTestFactory.ValidateLaunch("C", dir, true, "handoff.json"); }
            catch (InvalidOperationException) { blocked = true; }
            Assert(blocked, "local diagnostics must never mix with workbench handoff");
        });
    }

    private static void TestLocalSampleAppendTestIsolation()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "video.mp4");
            string audio = Path.Combine(dir, "audio.mp3");
            string sample = Path.Combine(dir, "sample.mp4");
            File.WriteAllText(video, "video"); File.WriteAllText(audio, "audio"); File.WriteAllText(sample, "sample");
            string formalStore = Path.Combine(dir, "formal-tasks.json");
            var original = new VideoTask {
                VideoPath = video, AudioPath = audio, SamplePath = sample,
                OutputPath = Path.Combine(dir, "formal.mp4"), TemporaryOutputPath = Path.Combine(dir, "formal-temp.mp4"),
                IsHandoffTask = true, HandoffStatusFile = Path.Combine(dir, "formal-status.json"),
                HandoffBatchId = "formal-batch", RemotePipelineTaskId = "formal-task", DeleteSourceAfterSuccess = true,
                SourceRootPath = dir, SampleSpeed = 2.5, Sequence = 1
            };
            new TaskStore(formalStore).Save(new[] { original });
            LocalSampleAppendTestRun run = LocalSampleAppendTestFactory.Create(formalStore, AppConfig.CreateDefault());
            Assert(run.Tasks.Count == 1, "isolated sample run should copy one task");
            VideoTask local = run.Tasks[0];
            Assert(!local.IsHandoffTask && String.IsNullOrWhiteSpace(local.HandoffStatusFile),
                "local sample test must not publish a handoff status");
            Assert(!local.DeleteSourceAfterSuccess && local.PreserveDraftOnFailure,
                "local sample test must preserve source and draft");
            Assert(local.TaskOrigin == TaskOrigins.Manual && local.OutputPath.IndexOf("local-sample-e2e", StringComparison.OrdinalIgnoreCase) >= 0,
                "local sample test must use an isolated manual output");
        });
    }

    private static void TestDiagnosticFailurePreservesDraftAndSource()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "C-video.mp4");
            string audio = Path.Combine(dir, "C-audio.wav");
            File.WriteAllText(video, "video");
            File.WriteAllText(audio, "audio");
            VideoTask task = NewGroupTask(video, audio, "", Path.Combine(dir, "out", "C.mp4"), 1);
            task.SampleSpeed = 0;
            task.PreserveDraftOnFailure = true;
            task.DeleteSourceAfterSuccess = false;
            var automation = new MockAutomation { FailTrimFirstCaption = true };
            var recycler = new MockRecycler(automation);
            var runner = new BatchRunner(AppConfig.CreateDefault(), automation,
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), recycler, new MockPostProcessor());
            runner.RunBlocking(new List<VideoTask> { task }, CancellationToken.None);
            Assert(task.GetState() == TaskState.Completed, "formal flow must no longer depend on Q or first-caption coordinates");
            Assert(automation.Exported.Count == 1, "formal flow must continue to temporary export");
            Assert(automation.DeleteCurrentDraftCalls == 1, "verified final output may clean its temporary draft");
            Assert(File.Exists(video), "diagnostic failure must preserve the source video");
            Assert(recycler.Paths.Count == 0, "diagnostic failure must not recycle source video");
        });
    }

    private static void TestQueueSizesCompleteIndependently()
    {
        VerifyQueueSize(1);
        VerifyQueueSize(2);
        VerifyQueueSize(4);
    }

    private static void VerifyQueueSize(int count)
    {
        WithTempDirectory(delegate(string dir) {
            string audio = Path.Combine(dir, "voice.mp3");
            string sample = Path.Combine(dir, "sample.mp4");
            File.WriteAllText(audio, "audio");
            File.WriteAllText(sample, "sample");
            var tasks = new List<VideoTask>();
            for (int index = 1; index <= count; index++) {
                string video = Path.Combine(dir, "video-" + index + ".mp4");
                File.WriteAllText(video, "video");
                tasks.Add(NewGroupTask(
                    video,
                    audio,
                    sample,
                    Path.Combine(dir, "out", "video-" + index + ".mp4"),
                    index));
            }

            AppConfig config = AppConfig.CreateDefault();
            config.RetryCount = 0;
            var automation = new MockAutomation();
            var runner = new BatchRunner(config, automation, new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), null, new MockPostProcessor());
            runner.RunBlocking(tasks, CancellationToken.None);

            Assert(tasks.All(task => task.GetState() == TaskState.Completed),
                count + "-task queue should complete every task");
            Assert(automation.LaunchCalls == count,
                count + "-task queue should verify or restore CapCut before every independent draft");
            Assert(automation.CreateProjectCalls == count,
                count + "-task queue should create one fresh draft per task");
            Assert(automation.DeleteCurrentDraftCalls == count,
                count + "-task queue should delete each temporary draft after export");
            Assert(automation.Exported.Count == count,
                count + "-task queue should retain one exported file per task");
            Assert(automation.CloseSessionCalls == count,
                count + "-task queue should close every owned session before starting the next task");
        });
    }

    private static void TestSubtitleStyleFailureBlocksExport()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "first.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            string sample = Path.Combine(dir, "sample.mp4");
            File.WriteAllText(video, "video");
            File.WriteAllText(audio, "audio");
            File.WriteAllText(sample, "sample");
            var tasks = new List<VideoTask> {
                NewGroupTask(video, audio, sample, Path.Combine(dir, "first_out.mp4"), 1)
            };
            AppConfig config = AppConfig.CreateDefault();
            config.RetryCount = 0;
            var automation = new MockAutomation { FailApplySubtitleStyle = true };
            var runner = new BatchRunner(config, automation, new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), null, new MockPostProcessor());
            runner.RunBlocking(tasks, CancellationToken.None);

            Assert(tasks[0].GetState() == TaskState.Failed, "style verification failure should fail the task");
            Assert(automation.Exported.Count == 0, "style verification failure must block export");
            Assert(automation.DeleteCurrentDraftCalls == 0, "failed task draft must be preserved");
        });
    }

    private static VideoTask NewGroupTask(string video, string audio, string sample, string output, int sequence)
    {
        return new VideoTask {
            VideoPath = video,
            AudioPath = audio,
            SamplePath = sample,
            OutputPath = output,
            GroupId = "group-one",
            ProjectName = "ProjectA",
            LanguageName = "西班牙",
            OutputPrefix = "ProjectA西班牙",
            ExportResolution = "2K",
            SubtitleStylePreset = "CompanyStandard",
            SampleSpeed = 3.63,
            Sequence = sequence
        };
    }

    private static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS  " + name); }
        catch (SkipTestException ex) { Console.WriteLine("SKIP  " + name + ": " + ex.Message); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL  " + name + ": " + ex.Message); }
    }

    private sealed class SkipTestException : Exception
    {
        public SkipTestException(string reason) : base(reason) { }
    }

    // The proprietary ZY Modern font is installed on production machines but
    // cannot be committed, so a machine without it (CI) skips these tests.
    private static void RequireCompanySubtitleFont()
    {
        try { DirectDraftSubtitleStylePatcher.ResolveFontPath(null); }
        catch (FileNotFoundException ex) { throw new SkipTestException(ex.Message); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void WithTempDirectory(Action<string> action)
    {
        string dir = Path.Combine(Path.GetTempPath(), "CapCutCoreTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { action(dir); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private class MockAutomation : ICapCutAutomation, ITaskSamplePreprocessor
    {
        public readonly List<string> Exported = new List<string>();
        public int CreateProjectCalls;
        public int AutoCaptionCalls;
        public int ApplySubtitleStyleCalls;
        public string LastSubtitleStyle;
        public int ImportSampleCalls;
        public int PreparedSampleCalls;
        public int ReplaceMainVideoCalls;
        public int ReplaceSampleCalls;
        public int ReturnToEditorCalls;
        public int ResetProjectCalls;
        public int DeleteCurrentDraftCalls;
        public int CloseSessionCalls;
        public int LaunchCalls;
        public bool FailApplySubtitleStyle;
        public bool FailExport;
        public bool FailDeleteDraft;
        public bool FailTrimFirstCaption;
        public void LaunchOrAttach(int timeoutSeconds, CancellationToken token) { LaunchCalls++; }
        public void CreateProject(int timeoutSeconds, CancellationToken token) { CreateProjectCalls++; }
        public void ImportVideo(string videoPath, int timeoutSeconds, CancellationToken token) {
            if (Path.GetFileName(videoPath) == "bad.mp4") throw new InvalidOperationException("simulated import failure");
        }
        public void PlaceOnTimeline(int timeoutSeconds, CancellationToken token) { }
        public void MuteOriginalVideo(int timeoutSeconds, CancellationToken token) { }
        public virtual void ImportAudio(string audioPath, int timeoutSeconds, CancellationToken token) { }
        public virtual void PlaceAudioOnTimeline(int timeoutSeconds, CancellationToken token) { }
        public void ReplaceMainVideo(string videoPath, int timeoutSeconds, CancellationToken token) { ReplaceMainVideoCalls++; }
        public void ReplaceSample(string samplePath, double speed, int timeoutSeconds, CancellationToken token) { ReplaceSampleCalls++; }
        public void Prepare(VideoTask task, int timeoutSeconds, CancellationToken token) {
            PreparedSampleCalls++;
            task.OriginalSamplePath = task.SamplePath;
            task.PreparedSamplePath = task.SamplePath;
            task.PreparedSampleTargetSeconds = 1.0;
            task.SampleSpeed = 1.0;
        }
        public void ImportSample(string samplePath, int timeoutSeconds, CancellationToken token) { ImportSampleCalls++; }
        public void PlaceSampleAtEnd(int timeoutSeconds, CancellationToken token) { }
        public void SetSampleSpeed(double speed, int timeoutSeconds, CancellationToken token) { }
        public void RunAutoCaption(int timeoutSeconds, CancellationToken token) { AutoCaptionCalls++; }
        public void ApplySubtitleStyle(string stylePreset, int timeoutSeconds, CancellationToken token) {
            ApplySubtitleStyleCalls++;
            LastSubtitleStyle = stylePreset;
            if (FailApplySubtitleStyle) throw new InvalidOperationException("simulated subtitle style verification failure");
        }
        public void TrimFirstCaptionIfNeeded(int timeoutSeconds, CancellationToken token) {
            if (FailTrimFirstCaption) throw new InvalidOperationException("simulated diagnostic Q no-op");
        }
        public void ExportVideo(string outputPath, int timeoutSeconds, CancellationToken token) {
            if (FailExport) throw new InvalidOperationException("simulated export failure");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllText(outputPath, "output");
            Exported.Add(Path.GetFileName(outputPath));
        }
        public void ReturnToEditorAfterExport(int timeoutSeconds, CancellationToken token) {
            ReturnToEditorCalls++;
        }
        public void ResetProject(int timeoutSeconds, CancellationToken token) { ResetProjectCalls++; }
        public void DeleteCurrentDraft(int timeoutSeconds, CancellationToken token) {
            DeleteCurrentDraftCalls++;
            if (FailDeleteDraft) throw new InvalidOperationException("simulated draft deletion failure");
        }
        public void CloseSession() { CloseSessionCalls++; }
    }

    private static void TestFreshSessionReopenClassifier()
    {
        Func<Exception, Exception> wrapped = inner =>
            new InvalidOperationException("重新打开同一草稿并验证字幕样式失败: " + inner.Message, inner);
        Assert(BatchRunner.IsFreshSessionRetryableReopenFailure(
            wrapped(new TimeoutException("等待剪映主窗口超时"))), "main window timeout must retry");
        Assert(BatchRunner.IsFreshSessionRetryableReopenFailure(
            wrapped(new TimeoutException("CAPCUT_HOME_NOT_READY: CapCut 主页入口尚未就绪；未点击任何草稿。"))),
            "unready homepage must retry");
        Assert(BatchRunner.IsFreshSessionRetryableReopenFailure(
            wrapped(new TimeoutException("CAPCUT_TASK_DRAFT_CARD_NOT_LOCATED: 未点击任何草稿。"))),
            "unlocated card must retry");
        Assert(BatchRunner.IsFreshSessionRetryableReopenFailure(
            wrapped(new RecoverableDraftReopenException("draft_editor_binding_failed", "未绑定"))),
            "unbound single click must retry");
        Assert(!BatchRunner.IsFreshSessionRetryableReopenFailure(
            wrapped(new TimeoutException("草稿卡只允许点击一次，但未以“draft_id + 源视频 + 字幕轨 + 导出入口”重新打开目标草稿；已停止导出"))),
            "a possibly wrong reopened draft must stop");
        Assert(!BatchRunner.IsFreshSessionRetryableReopenFailure(
            wrapped(new InvalidOperationException("草稿恢复检查点的draft_id、草稿名称或源视频身份已经变化，已阻止恢复"))),
            "identity changes must stop");
        Assert(!BatchRunner.IsFreshSessionRetryableReopenFailure(
            new CapCutOperationFailure("reopen", "CAPCUT_DRAFT_CHECKPOINT_HASH_MISMATCH",
                CapCutFailureClassification.HardSafety, "等待剪映主窗口超时", "", "",
                new TimeoutException("等待剪映主窗口超时"))),
            "a hard-safety failure must stop even with a retryable cause");
        Assert(!BatchRunner.IsFreshSessionRetryableReopenFailure(new OperationCanceledException()),
            "cancellation must stop");
    }

    private static void TestFreshSessionReopenRetriesWindowTimeout()
    {
        WithReopenRunner(new Exception[] {
            new TimeoutException("等待剪映主窗口超时"),
            new RecoverableDraftReopenException("draft_editor_binding_failed", "未绑定"),
            null
        }, delegate(BatchRunner runner, ReopenMockAutomation automation, VideoTask task) {
            runner.ReopenDirectDraftInFreshSessions(task, "重新打开同一草稿并验证字幕样式",
                automation, CancellationToken.None);
            Assert(automation.ReopenCalls == 3, "expected three reopen sessions, got " + automation.ReopenCalls);
            Assert(automation.CloseSessionCalls == 2, "each retry must close the previous session first");
            Assert(automation.RestoreCalls == 2, "each retry must re-verify the checkpoint");
            Assert(automation.RestoredCheckpoint == task.DirectDraftCheckpoint,
                "retry must restore this task's checkpoint");
        });
    }

    private static void TestFreshSessionReopenStopsWhenExhausted()
    {
        WithReopenRunner(new Exception[] {
            new TimeoutException("等待剪映主窗口超时"),
            new TimeoutException("等待剪映主窗口超时"),
            new TimeoutException("等待剪映主窗口超时"),
            null
        }, delegate(BatchRunner runner, ReopenMockAutomation automation, VideoTask task) {
            bool threw = false;
            try {
                runner.ReopenDirectDraftInFreshSessions(task, "重新打开同一草稿并验证字幕样式",
                    automation, CancellationToken.None);
            } catch (InvalidOperationException) { threw = true; }
            Assert(threw, "exhausted sessions must surface the failure");
            Assert(automation.ReopenCalls == 3, "RetryCount=2 allows exactly three sessions, got " + automation.ReopenCalls);
            Assert(automation.RestoreCalls == 2, "no restore after the final failure");
        });
    }

    private static void TestFreshSessionReopenSkipsUnsafeFailure()
    {
        WithReopenRunner(new Exception[] {
            new TimeoutException("草稿卡只允许点击一次，但未以“draft_id + 源视频 + 字幕轨 + 导出入口”重新打开目标草稿；已停止导出"),
            null
        }, delegate(BatchRunner runner, ReopenMockAutomation automation, VideoTask task) {
            bool threw = false;
            try {
                runner.ReopenDirectDraftInFreshSessions(task, "重新打开同一草稿并验证字幕样式",
                    automation, CancellationToken.None);
            } catch (InvalidOperationException) { threw = true; }
            Assert(threw, "unsafe failure must stop");
            Assert(automation.ReopenCalls == 1 && automation.CloseSessionCalls == 0 && automation.RestoreCalls == 0,
                "unsafe failure must not open another session");
        });
    }

    private static void TestFreshSessionReopenStopsWhenProcessRemains()
    {
        WithReopenRunner(new Exception[] {
            new TimeoutException("等待剪映主窗口超时"),
            null
        }, delegate(BatchRunner runner, ReopenMockAutomation automation, VideoTask task) {
            automation.LeaveOwnedProcessesOnClose = true;
            bool threw = false;
            try {
                runner.ReopenDirectDraftInFreshSessions(task, "重新打开同一草稿并验证字幕样式",
                    automation, CancellationToken.None);
            } catch (InvalidOperationException) { threw = true; }
            Assert(threw, "a CapCut process that did not exit must stop the retry");
            Assert(automation.ReopenCalls == 1 && automation.CloseSessionCalls == 1 && automation.RestoreCalls == 0,
                "no new session while the old CapCut process is still owned");
        });
    }

    private static void TestFreshSessionReopenStopsOnCheckpointFailure()
    {
        WithReopenRunner(new Exception[] {
            new RecoverableDraftReopenException("draft_card_location_failed", "未定位"),
            null
        }, delegate(BatchRunner runner, ReopenMockAutomation automation, VideoTask task) {
            automation.RestoreFailure = new InvalidOperationException("草稿恢复检查点的draft_id、草稿名称或源视频身份已经变化，已阻止恢复");
            bool threw = false;
            try {
                runner.ReopenDirectDraftInFreshSessions(task, "重新打开同一草稿并验证字幕样式",
                    automation, CancellationToken.None);
            } catch (InvalidOperationException ex) { threw = ex.Message.Contains("已阻止恢复"); }
            Assert(threw, "a checkpoint that no longer verifies must stop with its own error");
            Assert(automation.ReopenCalls == 1, "no reopen after a failed checkpoint check");
        });
    }

    private static void TestReleaseOnlyImportKeepsQuality()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "video.mp4");
            string audio = Path.Combine(dir, "ProjectC_碳岩板_英语_001.mp3");
            string output = Path.Combine(dir, "out", "final.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllText(video, "video");
            File.WriteAllText(audio, "audio");
            File.WriteAllBytes(output, new byte[4096]);
            string manifest = Path.Combine(dir, "release.json");
            Func<string, Dictionary<string, object>> payload = manifestId => new Dictionary<string, object> {
                { "schemaVersion", 2 },
                { "releaseOnly", true },
                { "batchId", "10.9-release" },
                { "manifestId", manifestId },
                { "attemptId", "attempt-" + manifestId },
                { "statusFile", Path.Combine(dir, "release.status.json") },
                { "tasks", new[] {
                    new Dictionary<string, object> {
                        { "taskId", "339fbc7ba94f51c16b6e2a7e" },
                        { "project", "ProjectC" }, { "material", "碳岩板" }, { "language", "英语" },
                        { "number", "001" }, { "sequence", 1 },
                        { "videoPath", video }, { "audioPath", audio }, { "finalOutputPath", output },
                        { "status", "completed" }, { "productionStatus", "completed" },
                        { "draftReleaseStatus", "pending" }, { "draftCleanupStatus", "pending" },
                        { "qualityStatus", "passed" }, { "releaseOnly", true }
                    }
                } }
            };
            File.WriteAllText(manifest, new JavaScriptSerializer().Serialize(payload("capcut-release-1")));
            var tasks = new List<VideoTask>();
            HandoffImportResult result = HandoffManifestImporter.Import(manifest, AppConfig.CreateDefault(), tasks);
            Assert(result.Issues.Count == 0, "release-only import must not report issues: " + String.Join("; ", result.Issues.ToArray()));
            Assert(tasks.Count == 1 && tasks[0].QualityStatus == "passed",
                "the workbench's verified quality result must be imported, got '" + (tasks.Count == 1 ? tasks[0].QualityStatus : "?") + "'");
            Assert(BatchRunner.IsDraftReleaseOnlyTask(tasks[0]), "the task must count as draft-release work");

            // 2026-10-09: the local record of the same task had no quality
            // result; a second release manifest must still be actionable.
            tasks[0].QualityStatus = "";
            File.WriteAllText(manifest, new JavaScriptSerializer().Serialize(payload("capcut-release-2")));
            HandoffManifestImporter.Import(manifest, AppConfig.CreateDefault(), tasks);
            VideoTask current = tasks.Last();
            Assert(current.QualityStatus == "passed" && BatchRunner.IsDraftReleaseOnlyTask(current),
                "an empty older local value must not erase the manifest's quality result");
        });
    }

    private static void TestDurableFileWrites()
    {
        WithTempDirectory(delegate(string dir) {
            string legacy = Path.Combine(dir, "legacy.json");
            string durable = Path.Combine(dir, "durable.json");
            string text = "{\"状态\":\"完成\",\"long\":\"" + new string('x', 5000) + "\"}";
            File.WriteAllText(legacy, text, new UTF8Encoding(false));
            DurableFile.WriteAllText(durable, text, new UTF8Encoding(false));
            Assert(File.ReadAllBytes(legacy).SequenceEqual(File.ReadAllBytes(durable)),
                "durable write must produce the same bytes as File.WriteAllText (no BOM)");
            DurableFile.WriteAllText(durable, "{}", new UTF8Encoding(false));
            Assert(File.ReadAllText(durable) == "{}", "a shorter write must replace the whole file");
            DurableFile.WriteAllBytes(durable, new byte[] { 1, 2, 3 });
            Assert(File.ReadAllBytes(durable).SequenceEqual(new byte[] { 1, 2, 3 }), "bytes are written as given");
        });
    }

    private static Exception AudioMissing()
    {
        return new DraftTimelineValidationException("CAPCUT_DRAFT_AUDIO_MISSING", "草稿尚未保存当前任务独立音频轨");
    }

    private static void TestAudioMissingRetriesInFreshSession()
    {
        WithAudioRunner(new Exception[] { AudioMissing(), null },
            delegate(BatchRunner runner, AudioMockAutomation automation, VideoTask task) {
            TimelineAssemblyCheckpoint video = task.TimelineAssemblyCheckpoint;
            bool adopted = runner.PlaceAudioInFreshSessions(task, CancellationToken.None);
            Assert(!adopted, "a redone drag continues on the normal audio path");
            Assert(automation.PlaceCalls == 2 && automation.ImportCalls == 2,
                "the fresh session imports and drags once more, got " + automation.ImportCalls + "/" + automation.PlaceCalls);
            Assert(automation.CloseSessionCalls == 1, "the failed session is closed first");
            Assert(automation.AudioProbeCalls == 1, "the draft is probed for late audio before any new drag");
            Assert(automation.VideoRestores == 1 && automation.RestoredVideo == video,
                "the fresh session reopens this task's video checkpoint");
            Assert(automation.AudioRestores == 0, "no audio checkpoint to reopen");
        });
    }

    private static void TestAudioSavedLateIsAdopted()
    {
        WithAudioRunner(new Exception[] { AudioMissing(), null },
            delegate(BatchRunner runner, AudioMockAutomation automation, VideoTask task) {
            automation.LateAudio = new TimelineAssemblyCheckpoint { State = "audio_timeline_committed" };
            bool adopted = runner.PlaceAudioInFreshSessions(task, CancellationToken.None);
            Assert(adopted, "late audio must be adopted");
            Assert(automation.PlaceCalls == 1, "late audio must never be dragged again, got " + automation.PlaceCalls);
            Assert(automation.VideoRestores == 0 && automation.AudioRestores == 1,
                "the draft is reopened at its audio stage");
            Assert(task.TimelineAssemblyCheckpoint == automation.LateAudio &&
                task.DraftTransactionPhase == "audio_timeline_committed",
                "the adopted audio stage is persisted on the task");
        });
    }

    private static void TestAudioPlacementStopsOnOtherFailures()
    {
        foreach (Exception failure in new Exception[] {
                new DraftTimelineValidationException("CAPCUT_DRAFT_AUDIO_DUPLICATED", "两条音频轨"),
                new DraftTimelineValidationException("CAPCUT_DRAFT_AUDIO_OFFSET", "未从零点开始"),
                new InvalidOperationException("CAPCUT_DRAFT_AUDIO_IDENTITY_MISMATCH: 当前任务缺少主视频或独立音频路径"),
                new OperationCanceledException() }) {
            Exception current = failure;
            WithAudioRunner(new Exception[] { current, null },
                delegate(BatchRunner runner, AudioMockAutomation automation, VideoTask task) {
                bool threw = false;
                try { runner.PlaceAudioInFreshSessions(task, CancellationToken.None); }
                catch (Exception) { threw = true; }
                Assert(threw, "must stop on " + current.GetType().Name + " " + current.Message);
                Assert(automation.PlaceCalls == 1 && automation.CloseSessionCalls == 0 &&
                    automation.VideoRestores == 0, "no new session for " + current.Message);
            });
        }
        WithAudioRunner(new Exception[] { AudioMissing(), null },
            delegate(BatchRunner runner, AudioMockAutomation automation, VideoTask task) {
            task.TimelineAssemblyCheckpoint = null;
            bool threw = false;
            try { runner.PlaceAudioInFreshSessions(task, CancellationToken.None); }
            catch (Exception) { threw = true; }
            Assert(threw && automation.CloseSessionCalls == 0, "no video checkpoint means no fresh session");
        });
    }

    private static void TestAudioPlacementStopsWhenExhausted()
    {
        WithAudioRunner(new Exception[] { AudioMissing(), AudioMissing(), AudioMissing(), null },
            delegate(BatchRunner runner, AudioMockAutomation automation, VideoTask task) {
            bool threw = false;
            try { runner.PlaceAudioInFreshSessions(task, CancellationToken.None); }
            catch (Exception) { threw = true; }
            Assert(threw, "exhausted sessions must surface the failure");
            Assert(automation.PlaceCalls == 3, "RetryCount=2 allows exactly three sessions, got " + automation.PlaceCalls);
            Assert(automation.VideoRestores == 2, "no reopen after the final failure");
        });
    }

    private static void TestAudioPlacementStopsWhenProcessRemains()
    {
        WithAudioRunner(new Exception[] { AudioMissing(), null },
            delegate(BatchRunner runner, AudioMockAutomation automation, VideoTask task) {
            automation.LeaveOwnedProcessesOnClose = true;
            bool threw = false;
            try { runner.PlaceAudioInFreshSessions(task, CancellationToken.None); }
            catch (Exception) { threw = true; }
            Assert(threw, "a CapCut process that did not exit must stop the retry");
            Assert(automation.CloseSessionCalls == 1 && automation.AudioProbeCalls == 0 &&
                automation.VideoRestores == 0, "no probe or new session while CapCut is still running");
        });
    }

    private static void WithAudioRunner(Exception[] placeOutcomes,
        Action<BatchRunner, AudioMockAutomation, VideoTask> test)
    {
        WithTempDirectory(delegate(string dir) {
            var automation = new AudioMockAutomation(placeOutcomes);
            var runner = new BatchRunner(AppConfig.CreateDefault(), automation,
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), null, new MockPostProcessor());
            runner.FreshReopenSessionDelayMilliseconds = 0;
            VideoTask task = NewGroupTask(Path.Combine(dir, "video.mp4"), Path.Combine(dir, "audio.mp3"), "",
                Path.Combine(dir, "out", "done.mp4"), 1);
            task.TimelineAssemblyCheckpoint = new TimelineAssemblyCheckpoint { State = "video_timeline_committed" };
            typeof(BatchRunner).GetField("activeTasks",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(runner, new List<VideoTask> { task });
            test(runner, automation, task);
        });
    }

    private sealed class AudioMockAutomation : MockAutomation,
        ITimelineAssemblyCheckpointing, ICapCutSessionClosure
    {
        private readonly Queue<Exception> placeOutcomes;
        public int ImportCalls;
        public int PlaceCalls;
        public int AudioProbeCalls;
        public int VideoRestores;
        public int AudioRestores;
        public TimelineAssemblyCheckpoint RestoredVideo;
        public TimelineAssemblyCheckpoint LateAudio;
        public bool LeaveOwnedProcessesOnClose;

        public AudioMockAutomation(IEnumerable<Exception> outcomes)
        {
            placeOutcomes = new Queue<Exception>(outcomes);
        }

        public bool LastCloseLeftOwnedProcesses { get { return LeaveOwnedProcessesOnClose && CloseSessionCalls > 0; } }
        public override void ImportAudio(string audioPath, int timeoutSeconds, CancellationToken token) { ImportCalls++; }

        public override void PlaceAudioOnTimeline(int timeoutSeconds, CancellationToken token)
        {
            PlaceCalls++;
            Exception outcome = placeOutcomes.Count == 0 ? null : placeOutcomes.Dequeue();
            if (outcome != null) throw outcome;
        }

        public bool TryCaptureExistingAudioTimelineCheckpoint(VideoTask task, out TimelineAssemblyCheckpoint checkpoint)
        {
            AudioProbeCalls++;
            checkpoint = LateAudio;
            return LateAudio != null;
        }

        public void RestoreAndReopenVideoTimeline(TimelineAssemblyCheckpoint checkpoint, VideoTask task, int timeoutSeconds, CancellationToken token)
        {
            VideoRestores++;
            RestoredVideo = checkpoint;
        }

        public void RestoreAndReopenAudioTimeline(TimelineAssemblyCheckpoint checkpoint, VideoTask task, int timeoutSeconds, CancellationToken token)
        {
            AudioRestores++;
        }

        public TimelineAssemblyCheckpoint CaptureVideoTimelineCheckpoint(VideoTask task) { return null; }
        public TimelineAssemblyCheckpoint CaptureAudioTimelineCheckpoint(VideoTask task) { return null; }
        public bool TryCaptureExistingVideoTimelineCheckpoint(VideoTask task, out TimelineAssemblyCheckpoint checkpoint) { checkpoint = null; return false; }
        public bool TryCaptureExistingTimelineAssemblyCheckpoint(VideoTask task, out TimelineAssemblyCheckpoint checkpoint) { checkpoint = null; return false; }
        public TimelineAssemblyCheckpoint CaptureTimelineAssemblyCheckpoint(VideoTask task) { return null; }
        public void RestoreAndReopenTimelineAssembly(TimelineAssemblyCheckpoint checkpoint, VideoTask task, int timeoutSeconds, CancellationToken token) { }
    }

    private static void WithReopenRunner(Exception[] reopenOutcomes,
        Action<BatchRunner, ReopenMockAutomation, VideoTask> test)
    {
        WithTempDirectory(delegate(string dir) {
            var automation = new ReopenMockAutomation(reopenOutcomes);
            var runner = new BatchRunner(AppConfig.CreateDefault(), automation,
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), null, new MockPostProcessor());
            runner.FreshReopenSessionDelayMilliseconds = 0;
            VideoTask task = NewGroupTask(Path.Combine(dir, "video.mp4"), "", "",
                Path.Combine(dir, "out", "done.mp4"), 1);
            task.DirectDraftCheckpoint = new DirectDraftCheckpoint { State = "style_applied" };
            typeof(BatchRunner).GetField("activeTasks",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(runner, new List<VideoTask> { task });
            test(runner, automation, task);
        });
    }

    private sealed class ReopenMockAutomation : MockAutomation,
        IDirectDraftSubtitleStyling, ICapCutSessionClosure
    {
        private readonly Queue<Exception> reopenOutcomes;
        public int ReopenCalls;
        public int RestoreCalls;
        public DirectDraftCheckpoint RestoredCheckpoint;
        public Exception RestoreFailure;
        public bool LeaveOwnedProcessesOnClose;

        public ReopenMockAutomation(IEnumerable<Exception> outcomes)
        {
            reopenOutcomes = new Queue<Exception>(outcomes);
        }

        public bool LastCloseLeftOwnedProcesses { get { return LeaveOwnedProcessesOnClose && CloseSessionCalls > 0; } }
        public bool UsesDirectDraftSubtitleStyle(string stylePreset) { return true; }
        public void SaveDraftForDirectSubtitleStyle(int timeoutSeconds, CancellationToken token) { }
        public CaptionDraftCheckpoint CaptureCaptionDraftCheckpoint(VideoTask task) { return null; }
        public void RestoreCaptionDraftCheckpoint(CaptionDraftCheckpoint checkpoint, VideoTask task) { }
        public void ApplyDirectDraftSubtitleStyle(int timeoutSeconds, CancellationToken token) { }
        public DirectDraftCheckpoint CaptureDirectDraftCheckpoint() { return null; }

        public void RestoreDirectDraftCheckpoint(DirectDraftCheckpoint checkpoint, string expectedVideoPath)
        {
            RestoreCalls++;
            RestoredCheckpoint = checkpoint;
            if (RestoreFailure != null) throw RestoreFailure;
        }

        public void ReopenAndVerifyDirectDraftSubtitleStyle(int timeoutSeconds, CancellationToken token)
        {
            ReopenCalls++;
            Exception outcome = reopenOutcomes.Count == 0 ? null : reopenOutcomes.Dequeue();
            if (outcome != null) throw outcome;
        }
    }

    private sealed class ScopedMockAutomation : MockAutomation,
        ITaskScopedDraftRoot, IDraftTaskIdentity
    {
        public int AcquireCalls;
        public int ReleaseCalls;
        public int CloseCallsSeenAtRelease;
        public bool FailRelease;
        public bool IdentitySetBeforeAcquire;
        public readonly List<string> Stages = new List<string>();
        private bool identitySet;

        public bool HasTaskDraftRootLease { get; private set; }
        public DraftRootLeaseRecord CurrentDraftRootLease { get { return null; } }

        public void SetDraftTaskIdentity(string videoPath)
        {
            identitySet = !String.IsNullOrWhiteSpace(videoPath);
        }

        public void AcquireTaskDraftRoot(VideoTask task,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            AcquireCalls++;
            IdentitySetBeforeAcquire = identitySet;
            HasTaskDraftRootLease = true;
        }

        public void ReleaseTaskDraftRoot(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ReleaseCalls++;
            CloseCallsSeenAtRelease = CloseSessionCalls;
            if (FailRelease) throw new InvalidOperationException(
                "CAPCUT_DRAFT_RELEASE_PENDING: simulated release failure");
            HasTaskDraftRootLease = false;
        }

        public void UpdateTaskDraftRootStage(string stage)
        {
            Stages.Add(stage ?? "");
        }
    }

    private static void TestDirectDraftSubtitleStyle()
    {
        RequireCompanySubtitleFont();
        WithTempDirectory(delegate(string dir) {
            string draft = Path.Combine(dir, "draft-one");
            string timeline = Path.Combine(draft, "Timelines", "timeline-one");
            Directory.CreateDirectory(timeline);
            string original = CreateMinimalCaptionDraftJson();
            File.WriteAllText(Path.Combine(draft, "draft_content.json"), original, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(draft, "template-2.tmp"), original, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(timeline, "draft_content.json"), original, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(timeline, "template-2.tmp"), original, new UTF8Encoding(false));

            var patcher = new DirectDraftSubtitleStylePatcher();
            DirectDraftStyleResult result = patcher.Apply(draft, new AppLogger());
            Assert(result.CaptionCount == 1 && result.PatchedFiles.Length == 4, "direct style patch must cover root, timeline and template copies");
            patcher.VerifyPersisted(draft, result.PatchedFiles);
            string patched = File.ReadAllText(Path.Combine(draft, "draft_content.json"), Encoding.UTF8);
            Assert(patched.Contains("ZY Modern"), "direct style patch must set the configured font");
            Assert(patched.Contains("caption-stable-id"), "direct style patch must preserve caption identity");
            Assert(patched.Contains("segment-stable-id"), "direct style patch must preserve timeline segments");
        });
    }

    private static void TestDirectDraftSubtitleStyleSkipsNonCaptionMirror()
    {
        RequireCompanySubtitleFont();
        WithTempDirectory(delegate(string dir) {
            string draft = Path.Combine(dir, "draft-with-cache-mirror");
            string timeline = Path.Combine(draft, "Timelines", "timeline-one");
            Directory.CreateDirectory(timeline);
            string original = CreateMinimalCaptionDraftJson();
            File.WriteAllText(Path.Combine(draft, "draft_content.json"), original, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(draft, "template-2.tmp"), original, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(timeline, "draft_content.json"), original, new UTF8Encoding(false));
            // CapCut may leave this sidecar without a text track.  It must be
            // preserved, but it is not a style target.
            string nonCaptionMirror = "{\"version\":360000,\"materials\":{\"texts\":[]},\"tracks\":[{\"type\":\"video\",\"segments\":[]}] }";
            File.WriteAllText(Path.Combine(timeline, "template-2.tmp"), nonCaptionMirror, new UTF8Encoding(false));

            var patcher = new DirectDraftSubtitleStylePatcher();
            DirectDraftStyleResult result = patcher.Apply(draft, new AppLogger());
            Assert(result.CaptionCount == 1 && result.PatchedFiles.Length == 3,
                "non-caption timeline mirror must not be treated as a subtitle style target");
            patcher.VerifyPersisted(draft, result.PatchedFiles);
            Assert(String.Equals(
                File.ReadAllText(Path.Combine(timeline, "template-2.tmp"), Encoding.UTF8),
                nonCaptionMirror,
                StringComparison.Ordinal),
                "non-caption timeline mirror must remain unchanged");
        });
    }

    private static void TestCapCut92DraftCompatibility()
    {
        RequireCompanySubtitleFont();
        WithTempDirectory(delegate(string dir) {
            string draft = Path.Combine(dir, "draft-92");
            string timeline = Path.Combine(draft, "Timelines", "timeline-92");
            Directory.CreateDirectory(timeline);
            string original = CreateMinimalCaptionDraftJson("9.2.0");
            foreach (string path in new[] {
                Path.Combine(draft, "draft_content.json"),
                Path.Combine(draft, "template-2.tmp"),
                Path.Combine(timeline, "draft_content.json"),
                Path.Combine(timeline, "template-2.tmp")
            }) File.WriteAllText(path, original, new UTF8Encoding(false));

            var engine = new CapCutCliDraftStyleEngine();
            CapCutCliDraftStyleEngineResult before = engine.Inspect(draft,
                new AppLogger());
            DirectDraftStyleResult result =
                new DirectDraftSubtitleStylePatcher().Apply(draft, new AppLogger());
            CapCutCliDraftStyleEngineResult after = engine.Inspect(draft,
                new AppLogger());
            Assert(result.CaptionCount == 1 && before.Groups.Length == 2 &&
                after.Groups.Length == 2, "9.2 root and active timeline must both be verified");
            Assert(before.Groups.All(item => item.AppVersion == "9.2.0" &&
                    item.SchemaVersion == 360000 && item.NewVersion == "181.0.0"),
                "9.2 compatibility must require its exact version fingerprint");
            for (int index = 0; index < before.Groups.Length; index++) {
                Assert(before.Groups[index].CaptionIdentityHash == after.Groups[index].CaptionIdentityHash &&
                    before.Groups[index].TimingAndTrackHash == after.Groups[index].TimingAndTrackHash &&
                    before.Groups[index].ProtectedMediaHash == after.Groups[index].ProtectedMediaHash,
                    "9.2 style write changed protected subtitle, timeline or media data");
            }

            string unknown = Path.Combine(dir, "draft-93");
            Directory.CreateDirectory(unknown);
            File.WriteAllText(Path.Combine(unknown, "draft_content.json"),
                CreateMinimalCaptionDraftJson("9.3.0"), new UTF8Encoding(false));
            bool blocked = false;
            try {
                new DirectDraftSubtitleStylePatcher().Apply(unknown, new AppLogger());
            } catch (Exception ex) {
                blocked = ex.ToString().Contains("CAPCUT_DRAFT_VERSION_UNSUPPORTED");
            }
            Assert(blocked, "unknown CapCut 9.3 drafts must be blocked before a live write");
        });
    }

    private static void TestDirectDraftSubtitlePlacement()
    {
        RequireCompanySubtitleFont();
        WithTempDirectory(delegate(string dir) {
            string draft = Path.Combine(dir, "draft-position");
            string timeline = Path.Combine(draft, "Timelines", "timeline-position");
            Directory.CreateDirectory(timeline);
            string original = CreateMinimalCaptionDraftJson();
            foreach (string path in new[] {
                Path.Combine(draft, "draft_content.json"),
                Path.Combine(draft, "template-2.tmp"),
                Path.Combine(timeline, "draft_content.json"),
                Path.Combine(timeline, "template-2.tmp")
            }) File.WriteAllText(path, original, new UTF8Encoding(false));

            var patcher = new DirectDraftSubtitleStylePatcher(null, -0.40625);
            DirectDraftStyleResult result = patcher.Apply(draft, new AppLogger());
            patcher.VerifyPersisted(draft, result.PatchedFiles);
            string patched = File.ReadAllText(
                Path.Combine(draft, "draft_content.json"), Encoding.UTF8);
            Assert(patched.Contains("-0.40625"),
                "caption target y must be persisted in the subtitle segment");
            Assert(patched.Contains("segment-stable-id") &&
                patched.Contains("caption-stable-id"),
                "moving captions must preserve segment and caption identity");
        });
    }

    private static void TestIsolatedHandoffUsesDirectDraftStyle()
    {
        Assert(!RuntimeModes.UseDirectDraftSubtitleStyle(),
            "normal runtime must keep production style handling unchanged");
        Assert(RuntimeModes.ShouldUseDirectDraftSubtitleStyle("CompanyStandard"),
            "formal CompanyStandard tasks must use the verified direct-draft workflow");
        Assert(!RuntimeModes.ShouldUseDirectDraftSubtitleStyle("Default"),
            "manual Default tasks must keep CapCut's original behavior");

        RuntimeModes.EnableIsolatedLabHandoff();
        Assert(RuntimeModes.UseDirectDraftSubtitleStyle(),
            "isolated handoff must use draft styling even when its persisted manifest says CompanyStandard");
        Assert(RuntimeModes.SubtitleStyleLab && RuntimeModes.IsolatedLabHandoff,
            "isolated handoff must enable the explicit style-lab runtime boundary");
    }

    private static void TestDirectDraftReopenPolicy()
    {
        Assert(DirectDraftReopenPolicy.CanAcceptEditor(true, false, true, true, true),
            "an explicitly opened editor must also prove that CapCut activated the exact locked draft");
        Assert(!DirectDraftReopenPolicy.CanAcceptEditor(false, false, true, true, true),
            "an editor that was not opened through the locked draft card must not be accepted");
        Assert(!DirectDraftReopenPolicy.CanAcceptEditor(true, true, true, true, true),
            "the CapCut home page must never be accepted as a reopened draft");
        Assert(!DirectDraftReopenPolicy.CanAcceptEditor(true, false, false, true, true),
            "a changed draft id must block export");
        Assert(!DirectDraftReopenPolicy.CanAcceptEditor(true, false, true, false, true),
            "a changed source-video identity must block export");
        Assert(!DirectDraftReopenPolicy.CanAcceptEditor(true, false, true, true, false),
            "opening an old visible editor while the target folder remains valid must block export");
    }

    private static void TestDraftActivationEvidencePolicy()
    {
        DateTime attempt = new DateTime(2026, 7, 26, 4, 11, 7, DateTimeKind.Utc);
        DateTime old = attempt.AddMinutes(-1);
        DateTime[] before = { old, old, old, old, old, old, old, old };
        DateTime[] after = { old, old, old, old, attempt.AddSeconds(7), attempt.AddSeconds(1), attempt.AddSeconds(2), attempt.AddSeconds(2) };
        Assert(DraftActivationEvidencePolicy.ChangedAfterAttempt(attempt, before, after),
            "CapCut reopen metadata such as draft_biz_config and timeline_layout must prove activation even when .locked is unchanged");
        Assert(!DraftActivationEvidencePolicy.ChangedAfterAttempt(attempt, before, before),
            "unchanged draft metadata must not be accepted as an activation");
        DateTime[] stale = { old, old, old, old, old.AddSeconds(1), old, old, old };
        Assert(!DraftActivationEvidencePolicy.ChangedAfterAttempt(attempt, before, stale),
            "metadata changes that predate the click must not prove activation");
    }

    private static string CreateMinimalCaptionDraftJson()
    {
        return CreateMinimalCaptionDraftJson("9.1.0");
    }

    private static string CreateMinimalCaptionDraftJson(string appVersion)
    {
        var serializer = new JavaScriptSerializer();
        var content = new Dictionary<string, object> {
            { "styles", new object[] { new Dictionary<string, object> {
                { "fill", new Dictionary<string, object> { { "alpha", 1.0 } } },
                { "font", new Dictionary<string, object> { { "id", "" }, { "path", "old.ttf" } } },
                { "range", new object[] { 0, 11 } }, { "size", 20.0 }
            } } },
            { "text", "hello world" }
        };
        var root = new Dictionary<string, object> {
            { "platform", new Dictionary<string, object> { { "app_version", appVersion } } },
            { "version", 360000 },
            { "new_version", String.Equals(appVersion, "9.2.0", StringComparison.Ordinal)
                ? "181.0.0" : "" },
            { "materials", new Dictionary<string, object> {
                { "texts", new object[] { new Dictionary<string, object> {
                    { "id", "caption-stable-id" }, { "type", "subtitle" },
                    { "recognize_text", "hello world" }, { "content", serializer.Serialize(content) },
                    { "words", new Dictionary<string, object> { { "start_time", new object[] { 0, 100 } }, { "end_time", new object[] { 100, 200 } }, { "text", new object[] { "hello", "world" } } } },
                    { "current_words", new Dictionary<string, object> { { "start_time", new object[0] }, { "end_time", new object[0] }, { "text", new object[0] } } },
                    { "font_title", "old" }, { "font_size", 20.0 }, { "font_path", "old.ttf" }, { "background_style", 0 }
                } } },
                { "videos", new object[0] },
                { "audios", new object[0] }
            } },
            { "tracks", new object[] { new Dictionary<string, object> {
                { "id", "track-stable-id" },
                { "type", "text" },
                { "segments", new object[] { new Dictionary<string, object> {
                    { "id", "segment-stable-id" },
                    { "material_id", "caption-stable-id" },
                    { "target_timerange", new Dictionary<string, object> {
                        { "start", 0 }, { "duration", 200 }
                    } }
                } } }
            } } },
            { "segments", new object[] { new Dictionary<string, object> { { "id", "segment-stable-id" }, { "target_timerange", new Dictionary<string, object> { { "start", 0 }, { "duration", 200 } } } } } }
        };
        return serializer.Serialize(root);
    }

    private static void TestPartialMaterialHandoffIsBlocked()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "one.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            string sample = Path.Combine(dir, "sample.mp4");
            File.WriteAllText(video, "video");
            File.WriteAllText(audio, "audio");
            File.WriteAllText(sample, "sample");
            VideoTask task = NewGroupTask(video, audio, sample, Path.Combine(dir, "out.mp4"), 1);
            task.Id = "material-one";
            task.IsHandoffTask = true;
            task.HandoffBatchId = "material-batch";
            task.RemotePipelineTaskId = "receiver-material-task";
            task.ExpectedTaskCount = 2;
            var tasks = new List<VideoTask> { task };
            var automation = new MockAutomation();
            var runner = new BatchRunner(
                AppConfig.CreateDefault(), automation,
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), null,
                new MockPostProcessor());

            runner.RunBlocking(tasks, CancellationToken.None);

            Assert(task.GetState() == TaskState.Failed, "incomplete handoff must be marked failed");
            Assert(task.Error.Contains("计划 2 条") && task.Error.Contains("当前唯一任务 1 条"),
                "incomplete handoff must report exact expected and actual counts");
            Assert(automation.LaunchCalls == 0, "incomplete handoff must not launch CapCut");
        });
    }

    private static void TestPartialRetryManifestRoundTrip()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "retry.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            File.WriteAllText(video, "video");
            File.WriteAllText(audio, "audio");
            string manifest = Path.Combine(dir, "handoff.json");
            var payload = new Dictionary<string, object> {
                { "schemaVersion", 2 },
                { "batchId", "8.25-test" },
                { "manifestId", "manifest-roundtrip" },
                { "attemptId", "attempt-roundtrip" },
                { "productionDate", "8.25" },
                { "statusFile", Path.Combine(dir, "status.json") },
                { "remotePipelineTaskId", "receiver-task" },
                { "expectedTaskCount", 2 },
                { "projectExpectedCount", 2 },
                { "attemptTaskCount", 1 },
                { "projectTaskIds", new[] { "done-task", "retry-task" } },
                { "completedTaskIds", new[] { "done-task" } },
                { "tasks", new[] { new Dictionary<string, object> {
                    { "taskId", "retry-task" }, { "project", "ProjectA" },
                    { "material", "碳岩板" }, { "language", "英语" },
                    { "number", "001" }, { "sequence", 1 },
                    { "videoPath", video }, { "audioPath", audio },
                    { "finalOutputPath", Path.Combine(dir, "out.mp4") }
                } } }
            };
            File.WriteAllText(manifest, new JavaScriptSerializer().Serialize(payload));
            var imported = new List<VideoTask>();
            HandoffManifestImporter.Import(manifest, AppConfig.CreateDefault(), imported);
            Assert(imported.Count == 1, "partial retry manifest should import one attempt task");
            Assert(imported[0].ProjectExpectedCount == 2 && imported[0].AttemptTaskCount == 1,
                "manifest counts must survive round-trip");
            Assert(imported[0].ProjectTaskIds.SequenceEqual(new[] { "done-task", "retry-task" }) &&
                   imported[0].CompletedTaskIds.SequenceEqual(new[] { "done-task" }),
                "manifest project/completed identities must survive round-trip");
        });
    }
    private static void TestPartialMaterialRetryWithCompletedTaskIsAllowed()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "retry.mp4");
            string audio = Path.Combine(dir, "voice.mp3");
            string sample = Path.Combine(dir, "sample.mp4");
            File.WriteAllText(video, "video");
            File.WriteAllText(audio, "audio");
            File.WriteAllText(sample, "sample");
            VideoTask task = NewGroupTask(video, audio, sample, Path.Combine(dir, "out.mp4"), 2);
            task.Id = "material-retry";
            task.IsHandoffTask = true;
            task.HandoffBatchId = "material-retry-batch";
            task.RemotePipelineTaskId = "receiver-material-task";
            task.ExpectedTaskCount = 2;
            task.ProjectExpectedCount = 2;
            task.AttemptTaskCount = 1;
            task.ProjectTaskIds = new List<string> { "material-completed", "material-retry" };
            task.CompletedTaskIds = new List<string> { "material-completed" };
            var tasks = new List<VideoTask> { task };
            var automation = new MockAutomation();
            var runner = new BatchRunner(
                AppConfig.CreateDefault(), automation,
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), null,
                new MockPostProcessor());

            runner.RunBlocking(tasks, CancellationToken.None);

            Assert(task.GetState() == TaskState.Completed, "partial retry with a completed sibling must run");
            Assert(automation.LaunchCalls == 1, "only the retry task should launch CapCut");
        });
    }

    // 10-19 SPC German: attempt of 3 (de, it, en); de finished, then a
    // release-only manifest moved it to its own batch to delete its draft.
    private static List<VideoTask> ReleaseSplitAttempt(string dir, string releasedOwner)
    {
        string audio = Path.Combine(dir, "voice.mp3");
        string sample = Path.Combine(dir, "sample.mp4");
        File.WriteAllText(audio, "audio");
        File.WriteAllText(sample, "sample");
        var project = new List<string> { "done-earlier", "released-de", "retry-it", "retry-en" };
        var tasks = new List<VideoTask>();
        foreach (string id in new[] { "retry-it", "retry-en" }) {
            string video = Path.Combine(dir, id + ".mp4");
            File.WriteAllText(video, "video");
            VideoTask task = NewGroupTask(video, audio, sample, Path.Combine(dir, id + "-out.mp4"), tasks.Count + 2);
            task.Id = id;
            task.IsHandoffTask = true;
            task.HandoffBatchId = "attempt-batch";
            task.RemotePipelineTaskId = "receiver-material-task";
            task.ExpectedTaskCount = 4;
            task.ProjectExpectedCount = 4;
            task.AttemptTaskCount = 3;
            task.ProjectTaskIds = new List<string>(project);
            task.CompletedTaskIds = new List<string> { "done-earlier" };
            tasks.Add(task);
        }
        string releasedVideo = Path.Combine(dir, "released-de.mp4");
        File.WriteAllText(releasedVideo, "video");
        VideoTask released = NewGroupTask(releasedVideo, audio, sample, Path.Combine(dir, "released-de-out.mp4"), 1);
        released.Id = "released-de";
        released.IsHandoffTask = true;
        released.ReleaseOnly = true;
        released.HandoffBatchId = "release-batch";
        released.RemotePipelineTaskId = releasedOwner;
        released.ProductionStatus = "completed";
        released.ExpectedTaskCount = 1;
        released.ProjectExpectedCount = 1;
        released.AttemptTaskCount = 1;
        released.ProjectTaskIds = new List<string> { "released-de" };
        released.SetState(TaskState.Failed);
        tasks.Add(released);
        return tasks;
    }

    private static void TestPartialRetryCountsSiblingReleasedFromAttempt()
    {
        WithTempDirectory(delegate(string dir) {
            List<VideoTask> tasks = ReleaseSplitAttempt(dir, "receiver-material-task");
            var automation = new MockAutomation();
            var runner = new BatchRunner(
                AppConfig.CreateDefault(), automation,
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), null,
                new MockPostProcessor());

            runner.RunBlocking(tasks, CancellationToken.None);

            Assert(tasks[0].GetState() == TaskState.Completed && tasks[1].GetState() == TaskState.Completed,
                "siblings of a task released from the same attempt must run: " +
                tasks[0].Error + " / " + tasks[1].Error);
        });
    }

    private static void TestPartialRetryIgnoresForeignReleaseOnlyTask()
    {
        WithTempDirectory(delegate(string dir) {
            List<VideoTask> tasks = ReleaseSplitAttempt(dir, "another-material-task");
            var automation = new MockAutomation();
            var runner = new BatchRunner(
                AppConfig.CreateDefault(), automation,
                new TaskStore(Path.Combine(dir, "tasks.json")), new AppLogger(), null,
                new MockPostProcessor());

            runner.RunBlocking(tasks, CancellationToken.None);

            Assert(automation.LaunchCalls == 0, "a release-only task of another project must not fill the attempt");
            Assert(tasks.Any(task => (task.Error ?? "").Contains("补跑清单数量不一致")),
                "the count mismatch must still be reported");
        });
    }

    private static void TestStableCapCutIdentitySurvivesAttemptChange()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "identity.mp4");
            File.WriteAllText(video, "video");
            VideoTask task = new VideoTask {
                Id = "stable-local-id",
                RemotePipelineTaskId = "canonical-id",
                ProductionDate = "2026-09-02",
                VideoPath = video,
                HandoffAttemptId = "attempt-a"
            };
            string first = CapCutTaskIdentity.FromTask(task).StableTaskArchiveKey;
            task.HandoffAttemptId = "attempt-b";
            string second = CapCutTaskIdentity.FromTask(task).StableTaskArchiveKey;
            Assert(String.Equals(first, second, StringComparison.Ordinal),
                "manifest/attempt changes must not move the stable draft archive");
        });
    }

    private static void TestCapCutDraftTransactionPersistence()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "transaction.mp4");
            File.WriteAllText(video, "video");
            VideoTask task = new VideoTask {
                Id = "transaction-task",
                ProductionDate = "2026-09-02",
                VideoPath = video,
                HandoffAttemptId = "attempt-1"
            };
            CapCutDraftTransactionStore store = new CapCutDraftTransactionStore(
                Path.Combine(dir, "transactions"));
            CapCutDraftTransactionCoordinator coordinator =
                new CapCutDraftTransactionCoordinator(store, new AppLogger());
            coordinator.Mark(task, "baseline_committed", "attempt-1");
            DraftCreationCheckpoint checkpoint = new DraftCreationCheckpoint {
                StableTaskArchiveKey = CapCutTaskIdentity.FromTask(task).StableTaskArchiveKey,
                CreationPhase = "create_intent_committed",
                CreateCommandId = "create-command-1",
                BaselineDraftIds = new string[0]
            };
            coordinator.CaptureCreation(task, checkpoint);
            CapCutDraftTransaction loaded = store.Load(task);
            Assert(loaded != null && loaded.DraftCreationCheckpoint != null &&
                   loaded.DraftCreationCheckpoint.CreateCommandId == "create-command-1" &&
                   loaded.DraftCreationCheckpoint.CreationPhase == "create_intent_committed" &&
                   task.StableTaskArchiveKey == loaded.StableTaskArchiveKey,
                "create intent and checkpoint must survive a client restart");

            // The nested creation sub-state must not move the outer lifecycle
            // backwards (or turn creation_completed into a terminal phase)
            // after media has already been committed.
            coordinator.Mark(task, "video_committed", "attempt-1");
            checkpoint.CreationPhase = "creation_completed";
            coordinator.CaptureCreation(task, checkpoint);
            loaded = store.Load(task);
            Assert(loaded != null && loaded.Phase == "video_committed",
                "creation checkpoint updates must never regress a committed media phase");

            coordinator.Complete(task, "completed", "pending");
            loaded = store.Load(task);
            Assert(loaded != null && loaded.Phase == "release_pending" &&
                   loaded.DraftReleaseStatus == "pending",
                "a completed production with pending draft release must remain recoverable (phase=" +
                    (loaded == null ? "<null>" : loaded.Phase) + ", release=" +
                    (loaded == null ? "<null>" : loaded.DraftReleaseStatus) + ")");
            coordinator.Mark(task, "original_draft_restored", "attempt-1");
            coordinator.Complete(task, "completed", "released");
            loaded = store.Load(task);
            Assert(loaded != null && loaded.Phase == "completed" &&
                   loaded.DraftReleaseStatus == "released",
                "the transaction becomes terminal only after draft release is committed");
        });
    }

    private static void TestCapCutTransactionRotatesAttemptErrors()
    {
        WithTempDirectory(delegate(string dir) {
            string video = Path.Combine(dir, "attempt-errors.mp4");
            File.WriteAllText(video, "video");
            VideoTask task = new VideoTask {
                Id = "attempt-error-task",
                ProductionDate = "2026-09-04",
                VideoPath = video,
                HandoffAttemptId = "attempt-1"
            };
            CapCutDraftTransactionStore store = new CapCutDraftTransactionStore(
                Path.Combine(dir, "transactions"));
            CapCutDraftTransactionCoordinator coordinator =
                new CapCutDraftTransactionCoordinator(store, new AppLogger());
            coordinator.MarkFailure(task, new CapCutOperationFailure(
                "draft_creation", "CAPCUT_EDITOR_WINDOW_NOT_READY",
                CapCutFailureClassification.Transient, "old attempt"), "attempt-1");
            coordinator.Mark(task, "draft_creation", "attempt-2");
            coordinator.MarkFailure(task, new CapCutOperationFailure(
                "draft_creation", "CAPCUT_DRAFT_CREATED_EDITOR_PENDING",
                CapCutFailureClassification.Transient, "current attempt"), "attempt-2");
            CapCutDraftTransaction loaded = store.Load(task);
            Assert(loaded != null &&
                loaded.PrimaryErrorCode == "CAPCUT_DRAFT_CREATED_EDITOR_PENDING" &&
                loaded.PrimaryErrorAttemptId == "attempt-2" &&
                loaded.ErrorHistory != null && loaded.ErrorHistory.Count == 1 &&
                loaded.ErrorHistory[0].ErrorCode == "CAPCUT_EDITOR_WINDOW_NOT_READY" &&
                loaded.ErrorHistory[0].AttemptId == "attempt-1",
                "a new attempt must expose its own active error while retaining the prior error as history");
        });
    }

    private static void TestTypedCapCutFailureClassification()
    {
        CapCutOperationFailure failure = new CapCutOperationFailure(
            "draft_creation", "CAPCUT_ACTIVE_DRAFT_ROOT_IDENTITY_MISMATCH",
            CapCutFailureClassification.HardSafety, "identity mismatch");
        CapCutOperationFailure promoted = CapCutOperationFailure.FromLegacy(
            failure, "draft_creation");
        Assert(promoted != null &&
               promoted.Classification == CapCutFailureClassification.HardSafety &&
               promoted.ErrorCode == "CAPCUT_ACTIVE_DRAFT_ROOT_IDENTITY_MISMATCH",
            "typed hard failure must remain the primary error");
    }

    private static void TestTransactionGenerationReset()
    {
        string dir = Path.Combine(Path.GetTempPath(), "capcut-generation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string video = Path.Combine(dir, "video.mp4");
        File.WriteAllText(video, "video");
        VideoTask task = NewDraftRootTask("attempt-1");
        task.VideoPath = video;
        task.Id = "generation-task";
        CapCutDraftTransactionStore store = new CapCutDraftTransactionStore(Path.Combine(dir, "tx"));
        CapCutDraftTransactionCoordinator coordinator = new CapCutDraftTransactionCoordinator(store, new AppLogger());
        coordinator.RecordImportProgress(task, "native_dialog_detected", "attempt-1", "old-command", video);
        coordinator.Mark(task, "original_draft_restored", "attempt-1");
        CapCutDraftTransaction before = coordinator.Get(task);
        coordinator.BeginOrResumeAttempt(task, "attempt-2");
        CapCutDraftTransaction after = coordinator.Get(task);
        Assert(before != null && after != null && after.Generation == before.Generation + 1 &&
            after.Phase == "waiting" && after.ActiveAttemptId == "attempt-2",
            "a released prior attempt must roll to a new generation");
        Assert(task.ImportPhase == "import_waiting" && task.ImportCommandId == "" && task.ImportPath == "",
            "task-facing import binding must reset together with the released transaction generation");
        coordinator.RecordImportProgress(task, "shortcut_sent", "attempt-2", "new-command", video);
        coordinator.BeginOrResumeAttempt(task, "attempt-2");
        Assert(task.ImportPhase == "shortcut_sent" && task.ImportCommandId == "new-command",
            "same live generation must retain its unresolved command");
        coordinator.Mark(task, "lease_acquired", "attempt-2");
        after = coordinator.Get(task);
        Assert(after.Phase == "lease_acquired", "new generation must allow lease acquisition");

        // Legacy transactions created before ActiveAttemptId was persisted
        // can still be at the restored boundary with a blank owner.  They
        // must enter a fresh lifecycle as well; otherwise every new lease or
        // editor mark is rejected as a phase regression.
        VideoTask legacyTask = NewDraftRootTask("");
        legacyTask.VideoPath = video;
        legacyTask.Id = "legacy-generation-task";
        coordinator.Mark(legacyTask, "original_draft_restored", "old-attempt");
        CapCutDraftTransaction legacyBefore = coordinator.Get(legacyTask);
        legacyBefore.ActiveAttemptId = "";
        legacyBefore.AttemptId = "";
        legacyBefore.LeaseId = "stale-lease";
        legacyBefore.DraftReleaseStatus = "released";
        legacyBefore.ProductionStatus = "pending";
        legacyTask.DraftReleaseStatus = "released";
        store.Save(legacyBefore);
        coordinator.BeginOrResumeAttempt(legacyTask, "new-attempt");
        CapCutDraftTransaction legacyAfter = coordinator.Get(legacyTask);
        Assert(legacyAfter != null && legacyAfter.Generation == legacyBefore.Generation + 1 &&
            legacyAfter.Phase == "waiting" &&
            legacyAfter.ActiveAttemptId == "new-attempt",
            "a legacy restored transaction without an active attempt must roll to a new generation");
        try { Directory.Delete(dir, true); } catch { }
    }

    private sealed class MockPostProcessor : IFinalVideoPostProcessor
    {
        public bool Fail;
        public readonly List<string> TemporaryPaths = new List<string>();
        public readonly List<string> FinalPaths = new List<string>();

        public void ReplaceFirstFrame(
            string capCutTemporaryPath,
            string originalSourcePath,
            string finalOutputPath,
            int timeoutSeconds,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            TemporaryPaths.Add(capCutTemporaryPath);
            FinalPaths.Add(finalOutputPath);
            if (Fail) throw new InvalidOperationException("simulated FFmpeg failure");
            Directory.CreateDirectory(Path.GetDirectoryName(finalOutputPath));
            File.Copy(capCutTemporaryPath, finalOutputPath, false);
            // File.Copy preserves the source timestamp, unlike FFmpeg.  The
            // production gate intentionally requires a newly-written final file.
            File.SetLastWriteTimeUtc(finalOutputPath, DateTime.UtcNow);
        }
    }

    private sealed class MockRecycler : ISourceVideoRecycler
    {
        private readonly MockAutomation automation;
        public readonly List<string> Paths = new List<string>();

        public MockRecycler(MockAutomation automation)
        {
            this.automation = automation;
        }

        public void Recycle(string sourcePath, string sourceRootPath)
        {
            Assert(automation.DeleteCurrentDraftCalls > 0, "draft cleanup must happen before recycling the source");
            Paths.Add(sourcePath);
        }
    }
}

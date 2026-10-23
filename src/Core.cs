using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

[assembly: System.Reflection.AssemblyFileVersion("0.6.26.0")]

namespace CapCutBatchClient
{
    // Write a file and flush it to the disk before returning.  Without the
    // flush a power cut can leave a file at its full size but all zero bytes
    // (20 workbench state files on 2026-10-08).  Callers that need atomicity
    // still stage into a temporary file and replace the target.
    public static class DurableFile
    {
        public static void WriteAllBytes(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough)) {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        public static void WriteAllText(string path, string text, Encoding encoding)
        {
            WriteAllBytes(path, encoding.GetBytes(text ?? ""));
        }
    }

    public static class ClientRuntimeIdentity
    {
        public const string Version = "0.6.26";
        public static readonly string ExecutablePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        private static string buildHash;
        public static string BuildHash {
            get {
                if (buildHash == null) {
                    using (var stream = File.OpenRead(ExecutablePath))
                    using (var hash = SHA256.Create())
                        buildHash = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                }
                return buildHash;
            }
        }
    }

    internal sealed class StableExportArtifactException : IOException
    {
        internal StableExportArtifactException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    internal sealed class RecoverableDraftReopenException : InvalidOperationException
    {
        internal RecoverableDraftReopenException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode ?? "draft_reopen_waiting";
        }

        internal string ErrorCode { get; private set; }
    }

    public enum CapCutFailureClassification
    {
        Transient,
        HardSafety,
        AlreadyCommitted,
        MaintenancePending,
        UserActionRequired
    }

    // Typed failure envelope used by every CapCut lifecycle operation.  The
    // runner may keep a small compatibility parser for legacy adapters, but
    // new automation code must throw this type so a later timeout can never
    // replace the original cause.
    public sealed class CapCutOperationFailure : InvalidOperationException
    {
        public string Phase { get; private set; }
        public string ErrorCode { get; private set; }
        public CapCutFailureClassification Classification { get; private set; }
        public string StableTaskArchiveKey { get; private set; }
        public string AttemptId { get; private set; }
        public string Diagnostic { get; private set; }
        public DateTime OccurredAtUtc { get; private set; }

        public CapCutOperationFailure(string phase, string errorCode,
            CapCutFailureClassification classification, string diagnostic,
            string stableTaskArchiveKey = "", string attemptId = "",
            Exception inner = null)
            : base((errorCode ?? "CAPCUT_OPERATION_FAILED") + ": " +
                (diagnostic ?? ""), inner)
        {
            Phase = phase ?? "unknown";
            ErrorCode = String.IsNullOrWhiteSpace(errorCode)
                ? "CAPCUT_OPERATION_FAILED" : errorCode;
            Classification = classification;
            StableTaskArchiveKey = stableTaskArchiveKey ?? "";
            AttemptId = attemptId ?? "";
            Diagnostic = diagnostic ?? "";
            OccurredAtUtc = DateTime.UtcNow;
        }

        public static CapCutOperationFailure Hard(string phase,
            string errorCode, string diagnostic)
        {
            return new CapCutOperationFailure(phase, errorCode,
                CapCutFailureClassification.HardSafety, diagnostic);
        }

        // Compatibility boundary for older automation adapters.  All new
        // operations throw the typed exception directly; this method keeps
        // legacy messages from being retried blindly while the adapters are
        // migrated one by one.
        public static CapCutOperationFailure FromLegacy(Exception error,
            string phase)
        {
            if (error == null) return null;
            CapCutOperationFailure typed = error as CapCutOperationFailure;
            if (typed != null) return typed;
            string message = error.Message ?? "";
            string[] hardCodes = {
                "CAPCUT_ACTIVE_DRAFT_ROOT_MARKER_MISSING",
                "CAPCUT_ACTIVE_DRAFT_ROOT_MARKER_UNREADABLE",
                "CAPCUT_ACTIVE_DRAFT_ROOT_IDENTITY_MISMATCH",
                "CAPCUT_ACTIVE_DRAFT_ROOT_PATH_MISMATCH",
                "CAPCUT_ACTIVE_DRAFT_ROOT_REPARSE_POINT_BLOCKED",
                "CAPCUT_ACTIVE_DRAFT_ROOT_MISSING",
                "CAPCUT_DRAFT_PATH_OPEN_FAILED",
                "CAPCUT_DRAFT_CHECKPOINT_ARCHIVE_NOT_FOUND",
                "CAPCUT_DRAFT_CHECKPOINT_ARCHIVE_IDENTITY_MISMATCH",
                "CAPCUT_DRAFT_CHECKPOINT_HASH_MISMATCH",
                "CAPCUT_UNKNOWN_STALE_DRAFT_BLOCKED",
                "CAPCUT_DRAFT_CREATION_MULTIPLE_DRAFTS_BLOCKED",
                "CAPCUT_DRAFT_CREATION_CHECKPOINT_MISSING",
                "CAPCUT_DRAFT_CREATION_RECOVERY_UNAVAILABLE",
                "CAPCUT_FILE_DIALOG_IDENTITY_MISMATCH",
                "CAPCUT_FILE_DIALOG_FILENAME_FIELD_MISSING",
                "CAPCUT_VIDEO_PLACEMENT_UNCONFIRMED",
                "CAPCUT_VIDEO_SEGMENT_MISSING",
                "CAPCUT_VIDEO_SEGMENT_DUPLICATED",
                "CAPCUT_VIDEO_MUTE_UNCONFIRMED",
                "CAPCUT_AUDIO_PLACEMENT_UNCONFIRMED",
                "CAPCUT_SAMPLE_PLACEMENT_UNCONFIRMED",
                "CAPCUT_DRAFT_SNAPSHOT_CONFLICT",
                "CAPCUT_UI_WORKER_TIMEOUT",
                "CAPCUT_TRANSACTION_STAGE_CONFLICT",
                "CAPCUT_STATUS_WRITE_FAILED",
                "CAPCUT_UNEXPECTED_ACTIVE_DRAFT_BLOCKED",
                "CAPCUT_TASK_IDENTITY_CONFLICT",
                "draft_root_switch_visible_capcut"
            };
            string code = hardCodes.FirstOrDefault(item =>
                message.IndexOf(item, StringComparison.OrdinalIgnoreCase) >= 0);
            if (String.IsNullOrWhiteSpace(code)) return null;
            return new CapCutOperationFailure(phase, code,
                CapCutFailureClassification.HardSafety, message, "", "", error);
        }
    }

    public interface ICapCutAutomation
    {
        void LaunchOrAttach(int timeoutSeconds, CancellationToken token);
        void CreateProject(int timeoutSeconds, CancellationToken token);
        void ImportVideo(string videoPath, int timeoutSeconds, CancellationToken token);
        void ReplaceMainVideo(string videoPath, int timeoutSeconds, CancellationToken token);
        void ReplaceSample(string samplePath, double speed, int timeoutSeconds, CancellationToken token);
        void PlaceOnTimeline(int timeoutSeconds, CancellationToken token);
        void MuteOriginalVideo(int timeoutSeconds, CancellationToken token);
        void ImportAudio(string audioPath, int timeoutSeconds, CancellationToken token);
        void PlaceAudioOnTimeline(int timeoutSeconds, CancellationToken token);
        void ImportSample(string samplePath, int timeoutSeconds, CancellationToken token);
        void PlaceSampleAtEnd(int timeoutSeconds, CancellationToken token);
        void SetSampleSpeed(double speed, int timeoutSeconds, CancellationToken token);
        void RunAutoCaption(int timeoutSeconds, CancellationToken token);
        void ApplySubtitleStyle(string stylePreset, int timeoutSeconds, CancellationToken token);
        void TrimFirstCaptionIfNeeded(int timeoutSeconds, CancellationToken token);
        void ExportVideo(string outputPath, int timeoutSeconds, CancellationToken token);
        void ReturnToEditorAfterExport(int timeoutSeconds, CancellationToken token);
        void DeleteCurrentDraft(int timeoutSeconds, CancellationToken token);
        void CloseSession();
        void ResetProject(int timeoutSeconds, CancellationToken token);
    }

    // Optional capability: the batch runner gives the desktop automation the
    // exact source video for the task it is about to create.  This is kept out
    // of ICapCutAutomation so existing test doubles and third-party adapters
    // remain compatible.
    public interface IDraftTaskIdentity
    {
        void SetDraftTaskIdentity(string videoPath);
    }

    // Optional capability: whether the last CloseSession() left a CapCut
    // process owned by this automation running.  The batch runner opens a
    // fresh draft-reopen session only after the previous one fully closed.
    public interface ICapCutSessionClosure
    {
        bool LastCloseLeftOwnedProcesses { get; }
    }

    // Optional capability used by the batch runner to persist the exact
    // Create Project transaction.  This is deliberately separate from the
    // generic automation interface so existing adapters remain compatible.
    public interface IDraftCreationState
    {
        string DraftCreationPhase { get; }
        DraftCreationCheckpoint CaptureDraftCreationCheckpoint();
    }

    // Optional persistence hook.  The callback is invoked before and after
    // every create-project side effect, allowing the runner to survive a
    // process crash without clicking "开始创作" a second time.
    public interface IDraftCreationPersistence
    {
        void BindDraftCreationPersistence(Action<DraftCreationCheckpoint> callback);
    }

    // Durable media-import lifecycle hook.  The runner binds this to the
    // task/transaction store so a blocked file picker can be resumed without
    // repeating Ctrl+I or path submission.
    public interface IMediaImportPersistence
    {
        void BindMediaImportPersistence(Action<string, string, string> callback,
            string restoredPhase, string restoredCommandId,
            string restoredImportPath);
    }

    // Optional recovery hook used when a previous attempt persisted a create
    // intent or discovered draft.  Implementations must observe the existing
    // editor only; they must never click the create command again.
    public interface IResumableDraftCreation
    {
        void RestoreDraftCreationCheckpoint(DraftCreationCheckpoint checkpoint);
        void ResumeDraftCreation(int timeoutSeconds, CancellationToken token);
    }

    // Optional task-scoped caption placement supplied by the workbench after
    // subtitle-removal verification.  Keeping it separate from the generic
    // automation contract preserves third-party/test adapters.
    public interface ICaptionPlacementTask
    {
        void SetCaptionPlacement(string placement, double targetY);
    }

    // Optional capability for a CapCut adapter that can isolate every queue
    // item in its own draft root. The runner acquires the lease before any
    // CapCut process is launched and releases it only after the owned CapCut
    // session has completely exited.
    public interface ITaskScopedDraftRoot
    {
        void AcquireTaskDraftRoot(VideoTask task, CancellationToken token);
        void UpdateTaskDraftRootStage(string stage);
        void ReleaseTaskDraftRoot(CancellationToken token);
        bool HasTaskDraftRootLease { get; }
        DraftRootLeaseRecord CurrentDraftRootLease { get; }
    }

    // Exposes the result of the filesystem-only preparation transaction.  An
    // empty CapCut bootstrap draft is still a durable create result: it must
    // be re-bound on the next attempt, but it must not be treated as a media
    // or production checkpoint.  Keeping this optional preserves compatibility
    // with test and third-party adapters that only implement the lease API.
    public interface IDraftRootPreparation
    {
        DraftRootPreparationResult CurrentDraftRootPreparation { get; }
    }

    // Optional capability used only by the isolated subtitle-style laboratory.
    // It deliberately keeps direct draft-file operations out of the generic
    // automation contract so existing production adapters remain compatible
    // until the laboratory has passed all three validation stages.
    public interface IDirectDraftSubtitleStyling
    {
        bool UsesDirectDraftSubtitleStyle(string stylePreset);
        void SaveDraftForDirectSubtitleStyle(int timeoutSeconds, CancellationToken token);
        CaptionDraftCheckpoint CaptureCaptionDraftCheckpoint(VideoTask task);
        void RestoreCaptionDraftCheckpoint(CaptionDraftCheckpoint checkpoint,
            VideoTask task);
        void ApplyDirectDraftSubtitleStyle(int timeoutSeconds, CancellationToken token);
        DirectDraftCheckpoint CaptureDirectDraftCheckpoint();
        void RestoreDirectDraftCheckpoint(DirectDraftCheckpoint checkpoint, string expectedVideoPath);
        void ReopenAndVerifyDirectDraftSubtitleStyle(int timeoutSeconds, CancellationToken token);
    }

    // Persisted after recognized captions are saved and the exact draft is
    // locked, but before the company visual style is written.  It lets a
    // version/style failure resume without importing media or recognizing
    // captions again.
    public sealed class CaptionDraftCheckpoint
    {
        public string State { get; set; }
        public string DraftDirectory { get; set; }
        public string DraftId { get; set; }
        public string DraftName { get; set; }
        public string SourceVideoPath { get; set; }
        public string AudioPath { get; set; }
        public string SamplePath { get; set; }
        public int CaptionCount { get; set; }
        public string CaptionIdentityHash { get; set; }
        public string TimingAndTrackHash { get; set; }
        public string ProtectedMediaHash { get; set; }
        public string[] VerifiedFiles { get; set; }
        public string AppVersion { get; set; }
        public int SchemaVersion { get; set; }
        public string NewVersion { get; set; }
        public string DraftRootLeaseId { get; set; }
        public string TaskDraftRoot { get; set; }
        public string BatchId { get; set; }
        public string CanonicalTaskId { get; set; }
        public string TaskId { get; set; }
        public string ProductionDate { get; set; }
        public string ManifestId { get; set; }
        public string AttemptId { get; set; }
        // Durable location for cross-attempt recovery.  DraftDirectory and
        // TaskDraftRoot describe the live CapCut session only; they may point
        // at the ephemeral C: directory after that session exits.
        public string ArchiveStorageVersion { get; set; }
        public string StableTaskArchiveKey { get; set; }
        public string ArchiveDraftRoot { get; set; }
        public string ArchiveManifestHash { get; set; }
        public string ArchiveVerifiedAt { get; set; }
        public string DraftRelativePath { get; set; }
        public string UpdatedAt { get; set; }
    }

    public sealed class DraftCreationCheckpoint
    {
        public string StableTaskArchiveKey { get; set; }
        public string LeaseId { get; set; }
        public string AttemptId { get; set; }
        public string BaselineIndexHash { get; set; }
        public string[] BaselineDraftIds { get; set; }
        public string CreateCommandIssuedAt { get; set; }
        public string CreateCommandId { get; set; }
        public string DiscoveredDraftId { get; set; }
        public string DiscoveredDraftPath { get; set; }
        public string CreationPhase { get; set; }
        public string VerifiedAt { get; set; }
        public long EditorWindowHandle { get; set; }
        public int EditorProcessId { get; set; }
        public string EditorWindowRole { get; set; }
        public string DraftRoot { get; set; }
        public string EditorVerifiedAt { get; set; }
    }

    public sealed class CapCutTransactionErrorRecord
    {
        public string AttemptId { get; set; }
        public string ErrorCode { get; set; }
        public string Diagnostic { get; set; }
        public string RecordedAtUtc { get; set; }
    }

    public sealed class CapCutDraftTransaction
    {
        public string SchemaVersion { get; set; }
        public string TransactionId { get; set; }
        public string StableTaskArchiveKey { get; set; }
        public string CanonicalTaskId { get; set; }
        public string ProductionDate { get; set; }
        public string LocalTaskId { get; set; }
        public string NormalizedSourcePath { get; set; }
        public string ManifestId { get; set; }
        public string AttemptId { get; set; }
        public string CommandId { get; set; }
        public string LeaseId { get; set; }
        public string Phase { get; set; }
        public string PrimaryErrorCode { get; set; }
        public string PrimaryErrorDiagnostic { get; set; }
        public string PrimaryErrorAttemptId { get; set; }
        public string FailureClassification { get; set; }
        public List<CapCutTransactionErrorRecord> ErrorHistory { get; set; }
        public string ProductionStatus { get; set; }
        public string DraftReleaseStatus { get; set; }
        // These fields are the durable source of truth for cancellation and
        // recovery.  Older transactions deserialize with empty values and are
        // upgraded by the coordinator without changing their stable identity.
        public string StopState { get; set; }
        public string NextAction { get; set; }
        public string MediaSemanticHash { get; set; }
        public string CheckpointJson { get; set; }
        public string UpdatedAtUtc { get; set; }
        public DraftCreationCheckpoint DraftCreationCheckpoint { get; set; }
        // Import progress is durable as well as idempotent.  A client crash
        // after Ctrl+I or path submission must be resumed from the same
        // command rather than issuing a second native-dialog action.
        public string ImportPhase { get; set; }
        public string ImportCommandId { get; set; }
        public string ImportPath { get; set; }
        public int Generation { get; set; }
        public string ActiveAttemptId { get; set; }
        public List<string> PreviousAttemptHistory { get; set; }

        public CapCutDraftTransaction()
        {
            SchemaVersion = "1";
            TransactionId = Guid.NewGuid().ToString("N");
            Phase = "waiting";
            ProductionStatus = "pending";
            DraftReleaseStatus = "pending";
            StopState = "none";
            NextAction = "acquire_lease";
            ErrorHistory = new List<CapCutTransactionErrorRecord>();
            PreviousAttemptHistory = new List<string>();
            ImportPhase = "import_waiting";
            ImportCommandId = "";
            ImportPath = "";
            Generation = 0;
            ActiveAttemptId = "";
            UpdatedAtUtc = DateTime.UtcNow.ToString("o");
        }
    }

    // Optional semantic checkpoint taken after the task video, independent
    // audio and prepared sample have been proven in CapCut's persisted draft.
    // It allows safe-continue to resume at caption recognition without
    // importing or dragging the same media a second time.
    public interface ITimelineAssemblyCheckpointing
    {
        TimelineAssemblyCheckpoint CaptureVideoTimelineCheckpoint(VideoTask task);
        TimelineAssemblyCheckpoint CaptureAudioTimelineCheckpoint(VideoTask task);
        bool TryCaptureExistingVideoTimelineCheckpoint(VideoTask task,
            out TimelineAssemblyCheckpoint checkpoint);
        bool TryCaptureExistingAudioTimelineCheckpoint(VideoTask task,
            out TimelineAssemblyCheckpoint checkpoint);
        // Probe the persisted draft for a fully assembled video+audio+sample
        // timeline.  Recovery must try this before lower media-stage probes so
        // a sample that was committed before a crash is adopted rather than
        // misclassified as an extra video segment.
        bool TryCaptureExistingTimelineAssemblyCheckpoint(VideoTask task,
            out TimelineAssemblyCheckpoint checkpoint);
        void RestoreAndReopenVideoTimeline(TimelineAssemblyCheckpoint checkpoint,
            VideoTask task, int timeoutSeconds, CancellationToken token);
        void RestoreAndReopenAudioTimeline(TimelineAssemblyCheckpoint checkpoint,
            VideoTask task, int timeoutSeconds, CancellationToken token);
        TimelineAssemblyCheckpoint CaptureTimelineAssemblyCheckpoint(VideoTask task);
        void RestoreAndReopenTimelineAssembly(TimelineAssemblyCheckpoint checkpoint,
            VideoTask task, int timeoutSeconds, CancellationToken token);
    }

    public sealed class TimelineAssemblyCheckpoint
    {
        public string State { get; set; }
        public string DraftDirectory { get; set; }
        public string DraftId { get; set; }
        public string DraftName { get; set; }
        public string MainVideoPath { get; set; }
        public string AudioPath { get; set; }
        public string SamplePath { get; set; }
        public string MainMaterialId { get; set; }
        public string SampleMaterialId { get; set; }
        public string AudioMaterialId { get; set; }
        public string MainSegmentId { get; set; }
        public string SampleSegmentId { get; set; }
        public string AudioSegmentId { get; set; }
        public string SemanticHash { get; set; }
        public string[] VerifiedFiles { get; set; }
        public string DraftRootLeaseId { get; set; }
        public string TaskDraftRoot { get; set; }
        public string BatchId { get; set; }
        public string CanonicalTaskId { get; set; }
        public string TaskId { get; set; }
        public string ProductionDate { get; set; }
        public string ManifestId { get; set; }
        public string AttemptId { get; set; }
        // Durable location for cross-attempt recovery; never rely on the
        // temporary fixed CapCut path after a lease has been released.
        public string ArchiveStorageVersion { get; set; }
        public string StableTaskArchiveKey { get; set; }
        public string ArchiveDraftRoot { get; set; }
        public string ArchiveManifestHash { get; set; }
        public string ArchiveVerifiedAt { get; set; }
        public string DraftRelativePath { get; set; }
        public string UpdatedAt { get; set; }
        public int VideoTrackCount { get; set; }
        public int AudioTrackCount { get; set; }
        public bool MuteVerified { get; set; }
    }

    // A safe-continue handoff owns a new batch/manifest/attempt envelope, but
    // it still represents the same media task. The manifest carries the public
    // sample-library path while a verified timeline carries the task-scoped
    // FFmpeg prepared sample, so the prepared path is the effective identity.
    internal static class TimelineCheckpointIdentityPolicy
    {
        public static string EffectiveSamplePath(VideoTask task)
        {
            if (task == null) return "";
            return String.IsNullOrWhiteSpace(task.PreparedSamplePath)
                ? task.SamplePath ?? ""
                : task.PreparedSamplePath;
        }

        public static bool StableIdentityMatches(TimelineAssemblyCheckpoint checkpoint,
            VideoTask task)
        {
            if (checkpoint == null || task == null) return false;
            bool envelopeMatches = SamePath(checkpoint.MainVideoPath, task.VideoPath) &&
                SamePath(checkpoint.AudioPath, task.AudioPath) &&
                SameValue(checkpoint.CanonicalTaskId, task.RemotePipelineTaskId) &&
                SameValue(checkpoint.TaskId, task.Id) &&
                SameValue(checkpoint.ProductionDate, task.ProductionDate);
            if (!envelopeMatches) return false;
            // At video_timeline_committed the sample has not entered CapCut.
            // Its planned path is not a committed-media identity yet and may
            // be rebound to the durable SampleAssignment before import.
            if (String.Equals(checkpoint.State, "video_timeline_committed",
                    StringComparison.OrdinalIgnoreCase) ||
                String.Equals(checkpoint.State, "audio_timeline_committed",
                    StringComparison.OrdinalIgnoreCase)) return true;
            return SamePath(checkpoint.SamplePath, EffectiveSamplePath(task));
        }

        public static bool CaptionStableIdentityMatches(CaptionDraftCheckpoint checkpoint,
            VideoTask task)
        {
            if (checkpoint == null || task == null) return false;
            return SamePath(checkpoint.SourceVideoPath, task.VideoPath) &&
                SamePath(checkpoint.AudioPath, task.AudioPath) &&
                SamePath(checkpoint.SamplePath, EffectiveSamplePath(task)) &&
                SameValue(checkpoint.CanonicalTaskId, task.RemotePipelineTaskId) &&
                SameValue(checkpoint.TaskId, task.Id) &&
                SameValue(checkpoint.ProductionDate, task.ProductionDate);
        }

        public static void RebindTimelineEnvelope(TimelineAssemblyCheckpoint checkpoint,
            VideoTask task)
        {
            CapCutTaskIdentity identity = CapCutTaskIdentity.FromTask(task);
            checkpoint.BatchId = task.HandoffBatchId ?? "";
            checkpoint.ManifestId = task.HandoffManifestId ?? "";
            checkpoint.AttemptId = task.HandoffAttemptId ?? "";
            checkpoint.StableTaskArchiveKey = identity.StableTaskArchiveKey;
            if (String.IsNullOrWhiteSpace(checkpoint.DraftRelativePath) &&
                !String.IsNullOrWhiteSpace(checkpoint.DraftDirectory))
                checkpoint.DraftRelativePath = Path.GetFileName(
                    checkpoint.DraftDirectory.TrimEnd('\\', '/'));
            if (String.IsNullOrWhiteSpace(checkpoint.ArchiveDraftRoot))
                checkpoint.ArchiveDraftRoot = Path.Combine(
                    AppPaths.ShortDraftArchiveDirectory,
                    identity.StableTaskArchiveKey, "live");
            checkpoint.UpdatedAt = DateTime.Now.ToString("o");
        }

        public static void RebindCaptionEnvelope(CaptionDraftCheckpoint checkpoint,
            VideoTask task)
        {
            CapCutTaskIdentity identity = CapCutTaskIdentity.FromTask(task);
            checkpoint.BatchId = task.HandoffBatchId ?? "";
            checkpoint.ManifestId = task.HandoffManifestId ?? "";
            checkpoint.AttemptId = task.HandoffAttemptId ?? "";
            checkpoint.StableTaskArchiveKey = identity.StableTaskArchiveKey;
            if (String.IsNullOrWhiteSpace(checkpoint.DraftRelativePath) &&
                !String.IsNullOrWhiteSpace(checkpoint.DraftDirectory))
                checkpoint.DraftRelativePath = Path.GetFileName(
                    checkpoint.DraftDirectory.TrimEnd('\\', '/'));
            if (String.IsNullOrWhiteSpace(checkpoint.ArchiveDraftRoot))
                checkpoint.ArchiveDraftRoot = Path.Combine(
                    AppPaths.ShortDraftArchiveDirectory,
                    identity.StableTaskArchiveKey, "live");
            checkpoint.UpdatedAt = DateTime.Now.ToString("o");
        }

        private static bool SameValue(string left, string right)
        {
            return String.Equals((left ?? "").Trim(), (right ?? "").Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool SamePath(string left, string right)
        {
            return String.Equals(NormalizePath(left), NormalizePath(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            try { value = Path.GetFullPath(value); } catch { }
            return (value ?? "").Trim().Replace('\\', '/').TrimEnd('/');
        }
    }

    internal static class DraftIndexIdentityPolicy
    {
        public static bool TryResolveSoleMatchingDraft(string draftDirectory,
            out string draftId, out string draftName, out long createdOrdinal)
        {
            draftId = "";
            draftName = "";
            createdOrdinal = 0;
            if (String.IsNullOrWhiteSpace(draftDirectory)) return false;
            string fullDirectory = Path.GetFullPath(draftDirectory);
            string parent = Path.GetDirectoryName(fullDirectory);
            if (String.IsNullOrWhiteSpace(parent)) return false;
            string indexPath = Path.Combine(parent, "root_meta_info.json");
            if (!File.Exists(indexPath)) return false;
            var serializer = new JavaScriptSerializer {
                MaxJsonLength = Int32.MaxValue,
                RecursionLimit = 256
            };
            Dictionary<string, object> root = serializer.DeserializeObject(
                File.ReadAllText(indexPath, Encoding.UTF8))
                as Dictionary<string, object>;
            object rawStores;
            object[] stores = root != null &&
                root.TryGetValue("all_draft_store", out rawStores)
                ? rawStores as object[] : null;
            if (stores == null) return false;
            string folderName = Path.GetFileName(fullDirectory);
            Dictionary<string, object>[] matches = stores
                .Select(value => value as Dictionary<string, object>)
                .Where(value => value != null)
                .Where(value => {
                    object rawPath;
                    string indexedPath = value.TryGetValue("draft_fold_path",
                        out rawPath) ? Convert.ToString(rawPath) : "";
                    return !String.IsNullOrWhiteSpace(indexedPath) &&
                        String.Equals(Path.GetFileName(indexedPath.TrimEnd('\\', '/')),
                            folderName, StringComparison.OrdinalIgnoreCase);
                }).ToArray();
            if (matches.Length != 1) return false;
            Dictionary<string, object> match = matches[0];
            object rawId, rawName, rawCreated;
            draftId = match.TryGetValue("draft_id", out rawId)
                ? Convert.ToString(rawId) : "";
            draftName = match.TryGetValue("draft_name", out rawName)
                ? Convert.ToString(rawName) : "";
            if (String.IsNullOrWhiteSpace(draftId) ||
                String.IsNullOrWhiteSpace(draftName)) return false;
            try {
                if (match.TryGetValue("tm_draft_create", out rawCreated) &&
                    rawCreated != null)
                    createdOrdinal = Convert.ToInt64(rawCreated);
            } catch { createdOrdinal = 0; }
            return true;
        }
    }

    // Persisted only in the local task store.  It lets a safe-continue attempt
    // resume at the exact draft that already contains captions and the verified
    // company style, instead of creating a new draft after a visual reopen
    // failure.  Every field is revalidated against the live draft before use.
    public sealed class DirectDraftCheckpoint
    {
        public string State { get; set; }
        public string DraftDirectory { get; set; }
        public string DraftId { get; set; }
        public string DraftName { get; set; }
        public string SourceVideoPath { get; set; }
        public string BackupDirectory { get; set; }
        public int CaptionCount { get; set; }
        public string[] PatchedFiles { get; set; }
        public string DraftRootLeaseId { get; set; }
        public string TaskDraftRoot { get; set; }
        // Durable location for cross-attempt recovery.  The live C: path is
        // retained only as a diagnostic/compatibility field.
        public string ArchiveStorageVersion { get; set; }
        public string StableTaskArchiveKey { get; set; }
        public string ArchiveDraftRoot { get; set; }
        public string ArchiveManifestHash { get; set; }
        public string ArchiveVerifiedAt { get; set; }
        public string DraftRelativePath { get; set; }
        public string UpdatedAt { get; set; }
    }

    // The isolated draft-style test can prove the on-disk identity of a draft,
    // but current CapCut builds do not reliably expose text-track colours or
    // the top-right Export button to UI Automation after a preset is applied.
    // Do not turn those presentation details into a false negative: acceptance
    // requires an explicit click on the uniquely-created card, leaving the home
    // page, and an unchanged draft/source identity.  The patcher then verifies
    // the style files before the runner is allowed to export.
    public static class DirectDraftReopenPolicy
    {
        public static bool CanAcceptEditor(bool explicitlyOpenedExactCard, bool homePageVisible,
            bool draftIdMatches, bool sourceIdentityMatches, bool actualDraftActivated)
        {
            return explicitlyOpenedExactCard && !homePageVisible &&
                draftIdMatches && sourceIdentityMatches && actualDraftActivated;
        }
    }

    public static class DraftActivationEvidencePolicy
    {
        public static bool ChangedAfterAttempt(DateTime attemptUtc,
            IEnumerable<DateTime> beforeValues, IEnumerable<DateTime> afterValues)
        {
            DateTime[] before = (beforeValues ?? Enumerable.Empty<DateTime>()).ToArray();
            DateTime[] after = (afterValues ?? Enumerable.Empty<DateTime>()).ToArray();
            if (before.Length == 0 || before.Length != after.Length) return false;
            bool changed = false;
            for (int index = 0; index < before.Length; index++) {
                if (before[index] != after[index]) changed = true;
            }
            return changed && after.Max() >= attemptUtc.AddSeconds(-1);
        }
    }

    // A named mutex is deliberately based on an absolute local path. It keeps
    // a formal installation, an isolated laboratory copy and a second machine
    // independent, while preventing two windows of the same installation from
    // loading and later overwriting the same task JSON.
    public static class InterprocessMutexNames
    {
        public static string ForPath(string scope, string path)
        {
            string value = (scope ?? "") + "|" + Path.GetFullPath(path ?? "").ToUpperInvariant();
            using (SHA1 sha1 = SHA1.Create()) {
                byte[] bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(value));
                var builder = new StringBuilder();
                foreach (byte item in bytes) builder.Append(item.ToString("x2"));
                return "Global\\CapCutBatchClient." + (scope ?? "lock") + "." + builder;
            }
        }
    }

    public enum TaskState
    {
        Waiting,
        AcquiringDraftRoot,
        Launching,
        CreatingProject,
        Importing,
        PlacingOnTimeline,
        MutingOriginal,
        ImportingAudio,
        PlacingAudioOnTimeline,
        ImportingSample,
        PreparingSample,
        PlacingSample,
        VerifyingSampleDuration,
        // Kept for persisted legacy task records. New runs use
        // VerifyingSampleDuration and never open CapCut's speed panel.
        AdjustingSampleSpeed,
        RecognizingCaptions,
        SavingDraft,
        ApplyingSubtitleStyle,
        VerifyingSubtitleStyle,
        TrimmingFirstCaption,
        ReplacingMainVideo,
        Exporting,
        RecoveringExportArtifact,
        PostProcessing,
        VerifyingOutputQuality,
        CleaningDraft,
        RestoringDraftRoot,
        RecyclingSource,
        Completed,
        Failed
    }

    public static class TaskStateText
    {
        public static string ToStorage(TaskState state) { return state.ToString(); }

        public static TaskState FromStorage(string value)
        {
            TaskState state;
            return Enum.TryParse(value, true, out state) ? state : TaskState.Waiting;
        }

        public static string ToDisplay(TaskState state)
        {
            switch (state) {
                case TaskState.AcquiringDraftRoot: return "Acquire task draft lease";
                case TaskState.RestoringDraftRoot: return "Restore original draft directory";
                case TaskState.PreparingSample: return "FFmpeg sample preparation";
                case TaskState.VerifyingSampleDuration: return "Verify prepared sample duration";
                case TaskState.Launching: return "启动剪映";
                case TaskState.CreatingProject: return "创建项目";
                case TaskState.Importing: return "导入视频";
                case TaskState.PlacingOnTimeline: return "加入时间线";
                case TaskState.MutingOriginal: return "静音原轨";
                case TaskState.ImportingAudio: return "导入音频";
                case TaskState.PlacingAudioOnTimeline: return "加入音频轨";
                case TaskState.ImportingSample: return "导入结尾样品";
                case TaskState.PlacingSample: return "样品接到末尾";
                case TaskState.AdjustingSampleSpeed: return "调整样品速度";
                case TaskState.RecognizingCaptions: return "识别字幕";
                case TaskState.SavingDraft: return "保存字幕草稿";
                case TaskState.ApplyingSubtitleStyle: return "应用字幕样式";
                case TaskState.VerifyingSubtitleStyle: return "验证字幕样式";
                case TaskState.TrimmingFirstCaption: return "检查首帧字幕";
                case TaskState.ReplacingMainVideo: return "替换主视频";
                case TaskState.Exporting: return "导出视频";
                case TaskState.RecoveringExportArtifact: return "接续已验证的CapCut临时成片";
                case TaskState.PostProcessing: return "FFmpeg替换第0帧";
                case TaskState.VerifyingOutputQuality: return "成片确定性质检";
                case TaskState.CleaningDraft: return "删除本条草稿";
                case TaskState.RecyclingSource: return "归档原视频";
                case TaskState.Completed: return "已完成";
                case TaskState.Failed: return "失败";
                default: return "等待";
            }
        }
    }

    public sealed class VideoTask
    {
        public string Id { get; set; }
        public string VideoPath { get; set; }
        public string AudioPath { get; set; }
        public string SamplePath { get; set; }
        // The shared sample source is never modified. Each task imports a
        // separately verified FFmpeg output instead.
        public string OriginalSamplePath { get; set; }
        // Durable first-choice sample plan.  This is intentionally separate
        // from the timeline checkpoint: a video-only checkpoint has not yet
        // committed the sample into CapCut, but the plan must still be stable.
        public SampleAssignment SampleAssignment { get; set; }
        public string PreparedSamplePath { get; set; }
        public double PreparedSampleTargetSeconds { get; set; }
        public string OutputPath { get; set; }
        public string TemporaryOutputPath { get; set; }
        // Exact task-scoped FFmpeg artifact. Cleanup must only consume this
        // registered path after live cloud verification.
        public string PostProcessWorkingPath { get; set; }
        public string QualityReportPath { get; set; }
        public string QualityStatus { get; set; }
        public string QualityCheckedAt { get; set; }
        public List<string> QualityWarnings { get; set; }
        // Production completion and draft housekeeping are independent facts.
        // A verified final file must not become a production failure merely
        // because the CapCut root needs another release attempt.
        public string ProductionStatus { get; set; }
        public string DraftReleaseStatus { get; set; }
        public string GroupId { get; set; }
        public string ProjectName { get; set; }
        public string MaterialName { get; set; }
        public string LanguageName { get; set; }
        public string VoiceTaskNumber { get; set; }
        public string OutputPrefix { get; set; }
        public string ExportResolution { get; set; }
        public string SubtitleStylePreset { get; set; }
        // "original_band" moves the newly generated captions over the band
        // where the source captions were successfully removed.  A blur
        // fallback deliberately keeps the company default position.
        public string CaptionPlacement { get; set; }
        public string SubtitleTreatment { get; set; }
        public double CaptionTargetY { get; set; }
        public double SampleSpeed { get; set; }
        public int Sequence { get; set; }
        public string SourceRootPath { get; set; }
        public string TaskOrigin { get; set; }
        public bool DeleteSourceAfterSuccess { get; set; }
        public bool SourceRecycled { get; set; }
        public bool PreserveDraftOnFailure { get; set; }
        // Isolated diagnostic clients retain their own draft after a verified
        // export so visual evidence can be reviewed without touching formal
        // production drafts.
        public bool PreserveDraftAfterSuccess { get; set; }
        public bool IsHandoffTask { get; set; }
        // A release-only handoff repairs a post-production CapCut lease.  It
        // must never re-import media or rerun export for an already verified
        // final output.
        public bool ReleaseOnly { get; set; }
        public string HandoffBatchId { get; set; }
        public string ProductionDate { get; set; }
        // Immutable identity for the manifest attempt that owns this task.
        // A later retry must never be overwritten by a status write from an
        // older client process.
        public string HandoffManifestId { get; set; }
        public string HandoffAttemptId { get; set; }
        public string HandoffStatusFile { get; set; }
        // Task-scoped contract identity supplied by the workbench.  The
        // launch batch id above remains the CapCut attempt envelope; these
        // fields identify the immutable production task inside that envelope.
        public string CanonicalTaskId { get; set; }
        public string RunGroupId { get; set; }
        public string ContractBatchId { get; set; }
        public string BatchKind { get; set; }
        // Stable owner/count contract supplied by the workbench.  These values
        // make it impossible for a date-level dashboard aggregate to claim a
        // remote project finished with a partial CapCut queue.
        public string RemotePipelineTaskId { get; set; }
        public int ExpectedTaskCount { get; set; }
        // A retry manifest can intentionally contain only the unfinished
        // subset of a formal remote project.  Keep the complete project
        // contract beside each queued retry item so preflight can distinguish
        // "one of two remaining" from an incomplete handoff.
        public int ProjectExpectedCount { get; set; }
        public int AttemptTaskCount { get; set; }
        public List<string> ProjectTaskIds { get; set; }
        public List<string> CompletedTaskIds { get; set; }
        public string State { get; set; }
        public int Attempts { get; set; }
        public string Error { get; set; }
        public string FailureFingerprint { get; set; }
        // Once assigned, this key is immutable for the task and survives
        // changes to manifest/attempt envelopes during Safe Continue.
        public string StableTaskArchiveKey { get; set; }
        public string DraftCreationPhase { get; set; }
        public DraftCreationCheckpoint DraftCreationCheckpoint { get; set; }
        // Durable CapCut lifecycle transaction.  This is intentionally kept
        // beside the task record so Safe Continue can recover even when the
        // desktop client itself was terminated between UI side effects.
        public string DraftTransactionId { get; set; }
        public string DraftTransactionPhase { get; set; }
        public string DraftTransactionErrorCode { get; set; }
        public string DraftTransactionFailureClassification { get; set; }
        public string DraftTransactionUpdatedAt { get; set; }
        public string ImportPhase { get; set; }
        public string ImportCommandId { get; set; }
        public string ImportPath { get; set; }
        // Post-success housekeeping is intentionally separate from production
        // failure.  A verified output must remain successful even when CapCut's
        // draft index is locked and the draft has to be cleaned later.
        public string DraftCleanupStatus { get; set; }
        public string DraftCleanupWarning { get; set; }
        public string DraftCleanupUpdatedAt { get; set; }
        public string CreatedAt { get; set; }
        public string StartedAt { get; set; }
        public string FinishedAt { get; set; }
        public string UpdatedAt { get; set; }
        public DirectDraftCheckpoint DirectDraftCheckpoint { get; set; }
        public CaptionDraftCheckpoint CaptionDraftCheckpoint { get; set; }
        public TimelineAssemblyCheckpoint TimelineAssemblyCheckpoint { get; set; }

        public VideoTask()
        {
            Id = Guid.NewGuid().ToString("N");
            State = TaskState.Waiting.ToString();
            Error = "";
            DraftCleanupStatus = "pending";
            DraftCleanupWarning = "";
            DraftCleanupUpdatedAt = "";
            SampleSpeed = 1.0;
            Sequence = 1;
            ExportResolution = "2K";
            SubtitleStylePreset = "Default";
            CaptionPlacement = "company_default";
            SubtitleTreatment = "";
            CaptionTargetY = 0.0;
            TaskOrigin = TaskOrigins.Manual;
            ProjectTaskIds = new List<string>();
            CompletedTaskIds = new List<string>();
            QualityWarnings = new List<string>();
            QualityStatus = "";
            ProductionStatus = "pending";
            DraftReleaseStatus = "pending";
            DraftCreationPhase = "idle";
            DraftTransactionPhase = "waiting";
            DraftTransactionErrorCode = "";
            DraftTransactionFailureClassification = "";
            CreatedAt = DateTime.Now.ToString("o");
            DraftTransactionUpdatedAt = CreatedAt;
            ImportPhase = "import_waiting";
            ImportCommandId = "";
            ImportPath = "";
            UpdatedAt = CreatedAt;
        }

        public TaskState GetState() { return TaskStateText.FromStorage(State); }
        public void SetState(TaskState state) { State = TaskStateText.ToStorage(state); UpdatedAt = DateTime.Now.ToString("o"); }
        public bool NormalizeLegacyCompletedCleanupWarning()
        {
            if (GetState() != TaskState.Completed || String.IsNullOrWhiteSpace(Error)) return false;
            string[] cleanupMarkers = {
                "草稿待清理",
                "草稿索引更新失败",
                "返回首页并删除本条唯一新增草稿失败"
            };
            if (!cleanupMarkers.Any(marker => Error.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)) return false;

            DraftCleanupStatus = "pending";
            DraftCleanupWarning = Error;
            DraftCleanupUpdatedAt = String.IsNullOrWhiteSpace(UpdatedAt) ? DateTime.Now.ToString("o") : UpdatedAt;
            Error = "";
            FailureFingerprint = "";
            return true;
        }
        public string GetGroupKey()
        {
            return String.IsNullOrWhiteSpace(GroupId) ? Id : GroupId;
        }
    }

    public sealed class LocatorRule
    {
        public List<string> Names { get; set; }
        public List<string> AutomationIds { get; set; }
        public double RelativeX { get; set; }
        public double RelativeY { get; set; }

        public LocatorRule()
        {
            Names = new List<string>();
            AutomationIds = new List<string>();
            RelativeX = -1;
            RelativeY = -1;
        }

        public bool HasCoordinate { get { return RelativeX >= 0 && RelativeX <= 1 && RelativeY >= 0 && RelativeY <= 1; } }
        public bool HasSemanticSelector { get { return Names.Count > 0 || AutomationIds.Count > 0; } }
    }

    public sealed class AutomationTimeouts
    {
        public int LaunchSeconds { get; set; }
        public int CreateProjectSeconds { get; set; }
        public int ImportSeconds { get; set; }
        public int TimelineSeconds { get; set; }
        public int CaptionSeconds { get; set; }
        public int ExportSeconds { get; set; }
        public int ControlSeconds { get; set; }
    }

    public sealed class AppConfig
    {
        public string CapCutPath { get; set; }
        public string CapCutArguments { get; set; }
        public bool RequirePinnedCapCutVersion { get; set; }
        public string PinnedCapCutVersion { get; set; }
        public string PinnedCapCutSha256 { get; set; }
        public string DisabledCapCutUpdaterPath { get; set; }
        public string DisabledCapCutUpdaterSha256 { get; set; }
        public string DraftRoot { get; set; }
        // Rollout gate. Existing production configs deserialize this as false,
        // so the formal client is not changed until an isolated validation and
        // one-task canary explicitly enable it.
        public bool EnableTaskScopedDraftRoot { get; set; }
        // CapCut 9.2 can show a draft-path error when the fixed path is a
        // cross-volume junction.  Production uses a real directory at the
        // configured path while the task is running and archives it after the
        // process exits.  The legacy junction mode remains available for
        // compatibility with old test fixtures and historical leases.
        public bool UseLocalActiveDraftDirectory { get; set; }
        public string OutputDirectory { get; set; }
        // Optional local staging directory used only when a particular CapCut
        // installation has a fixed export preference. Production configs leave
        // this empty and continue to use OutputDirectory.
        public string CapCutExportStagingDirectory { get; set; }
        public string ExportExtension { get; set; }
        public string DefaultProjectName { get; set; }
        public string DefaultMaterialName { get; set; }
        public string DefaultLanguageName { get; set; }
        public string DefaultOutputPrefix { get; set; }
        public string SharedAudioPath { get; set; }
        public string SampleDirectory { get; set; }
        public string SamplePath { get; set; }
        public string LastCategoryName { get; set; }
        public Dictionary<string, string> CategoryVideoDirectories { get; set; }
        public Dictionary<string, string> CategoryAudioDirectories { get; set; }
        public string ExampleBrandCSampleDirectory { get; set; }
        public string EntityNormalSampleDirectory { get; set; }
        public string EntityPuSampleDirectory { get; set; }
        public double SampleSpeed { get; set; }
        public int StartSequence { get; set; }
        public string ExportResolution { get; set; }
        public string DefaultSubtitleStylePreset { get; set; }
        public string CompanySubtitleFont { get; set; }
        public string CompanySubtitleFontPath { get; set; }
        public int CompanySubtitleFontSize { get; set; }
        public string Language { get; set; }
        public string[] ProcessNames { get; set; }
        public string[] MainWindowNames { get; set; }
        public int RetryCount { get; set; }
        public int CaptionFallbackSeconds { get; set; }
        public AutomationTimeouts Timeouts { get; set; }
        public Dictionary<string, LocatorRule> Locators { get; set; }

        public static AppConfig CreateDefault()
        {
            var config = new AppConfig {
                CapCutPath = "",
                CapCutArguments = "",
                RequirePinnedCapCutVersion = false,
                PinnedCapCutVersion = "",
                PinnedCapCutSha256 = "",
                DisabledCapCutUpdaterPath = "",
                DisabledCapCutUpdaterSha256 = "",
                DraftRoot = "",
                EnableTaskScopedDraftRoot = true,
                UseLocalActiveDraftDirectory = true,
                OutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "CapCutExports"),
                ExportExtension = ".mp4",
                DefaultProjectName = "ProjectA",
                DefaultMaterialName = "",
                DefaultLanguageName = "西班牙",
                DefaultOutputPrefix = "ProjectA西班牙",
                SharedAudioPath = "",
                SampleDirectory = "",
                SamplePath = "",
                LastCategoryName = "ProjectA",
                CategoryVideoDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                CategoryAudioDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExampleBrandCSampleDirectory = "",
                EntityNormalSampleDirectory = "",
                EntityPuSampleDirectory = "",
                SampleSpeed = 3.63,
                StartSequence = 1,
                ExportResolution = "2K",
                DefaultSubtitleStylePreset = "Default",
                // Company style keeps the user's current CapCut font.  Only the
                // size and calibrated preset are production requirements.
                CompanySubtitleFont = "",
                CompanySubtitleFontPath = "",
                CompanySubtitleFontSize = 10,
                Language = "zh-CN",
                ProcessNames = new[] { "JianyingPro", "CapCut" },
                MainWindowNames = new[] { "剪映专业版", "剪映", "CapCut" },
                RetryCount = 2,
                CaptionFallbackSeconds = 35,
                Timeouts = new AutomationTimeouts {
                    LaunchSeconds = 60,
                    CreateProjectSeconds = 30,
                    ImportSeconds = 90,
                    TimelineSeconds = 30,
                    CaptionSeconds = 600,
                    ExportSeconds = 900,
                    ControlSeconds = 12
                },
                Locators = new Dictionary<string, LocatorRule>(StringComparer.OrdinalIgnoreCase)
            };

            config.Locators["CreateProject"] = Rule(new[] { "开始创作", "新建项目", "Create project", "New project" });
            config.Locators["HomeProClose"] = Rule(new[] { "关闭", "Close" });
            config.Locators["DialogDismiss"] = Rule(new[] { "知道了", "确定", "OK", "Got it" });
            config.Locators["SubscriptionClose"] = Rule(new[] { "关闭", "Close" });
            config.Locators["ConfirmNewProject"] = Rule(new[] { "确认", "是", "Yes", "Confirm" });
            config.Locators["Import"] = Rule(new[] { "导入", "导入媒体", "Import", "Import media" });
            config.Locators["ImportedMedia"] = Rule(new[] { "已导入", "Imported media" });
            config.Locators["MediaItem"] = Rule(new string[0]);
            config.Locators["MediaSearchField"] = Rule(new string[0]);
            config.Locators["TimelineDrop"] = Rule(new string[0]);
            config.Locators["VideoTrackMute"] = Rule(new[] { "静音", "Mute" });
            config.Locators["AudioItem"] = Rule(new string[0]);
            config.Locators["AudioTimelineDrop"] = Rule(new string[0]);
            config.Locators["SampleItem"] = Rule(new string[0]);
            config.Locators["SampleTimelineEndDrop"] = Rule(new string[0]);
            config.Locators["SampleClip"] = Rule(new string[0]);
            config.Locators["SpeedPanel"] = Rule(new[] { "变速", "速度", "Speed" });
            config.Locators["NormalSpeed"] = Rule(new[] { "正常变速", "正常", "Normal speed", "Normal" });
            config.Locators["SpeedValue"] = Rule(new[] { "速度", "倍速", "Speed" });
            config.Locators["MainVideoClip"] = Rule(new string[0]);
            config.Locators["TimelineZeroDrop"] = Rule(new string[0]);
            config.Locators["AddToTimeline"] = Rule(new[] { "添加到轨道", "添加到时间线", "Add to timeline" });
            config.Locators["TextPanel"] = Rule(new[] { "文本", "字幕", "Text", "Captions" });
            config.Locators["AutoCaption"] = Rule(new[] { "智能字幕", "识别字幕", "自动字幕", "Auto captions" });
            config.Locators["StartRecognition"] = Rule(new[] { "开始识别", "识别", "Generate", "Start" });
            config.Locators["Recognizing"] = Rule(new[] { "正在识别", "识别中", "Generating captions" });
            config.Locators["SubtitleTrack"] = Rule(new[] { "字幕轨道", "文本轨道", "Caption track", "Subtitle track" });
            config.Locators["AutoCaptionComplete"] = Rule(new[] { "重新识别", "识别完成", "Regenerate" });
            config.Locators["SubtitleTrackProbe"] = new LocatorRule { RelativeX = 0.15, RelativeY = 0.793 };
            config.Locators["CaptionApplyAll"] = Rule(new[] { "对全部主字幕生效", "Apply to all main captions", "Apply to all captions" });
            config.Locators["CaptionFontField"] = Rule(new[] { "字体", "Font" });
            config.Locators["CaptionFontSizeField"] = Rule(new[] { "字号", "Font size", "Size" });
            config.Locators["CaptionCompanyPreset"] = Rule(new string[0]);
            config.Locators["FirstCaptionAtZeroProbe"] = new LocatorRule();
            config.Locators["FirstCaptionLeftEdge"] = new LocatorRule();
            config.Locators["FirstCaptionOneFrameRight"] = new LocatorRule();
            config.Locators["Export"] = Rule(new[] { "导出", "Export" });
            config.Locators["ExportResolution"] = Rule(new[] { "分辨率", "Resolution" });
            config.Locators["ExportResolution2K"] = Rule(new[] { "2K" });
            config.Locators["ExportPathField"] = Rule(new[] { "导出至", "保存至", "位置", "Save to", "Location" });
            config.Locators["ExportNameField"] = Rule(new[] { "名称", "文件名", "Name", "File name" });
            config.Locators["ExportConfirm"] = Rule(new[] { "导出", "Export" });
            config.Locators["ExportDone"] = Rule(new[] { "完成", "关闭", "返回首页", "Done", "Close", "Back to home" });
            config.Locators["BackToHome"] = Rule(new[] { "返回首页", "主页", "Home", "Back" });
            return config;
        }

        private static LocatorRule Rule(IEnumerable<string> names)
        {
            return new LocatorRule { Names = names.ToList() };
        }

        public LocatorRule GetRule(string action)
        {
            LocatorRule rule;
            if (Locators != null && Locators.TryGetValue(action, out rule)) return rule;
            return new LocatorRule();
        }

        public bool ApplyLocalVisibleBoundsFirstCaptionCalibration()
        {
            LocatorRule probe = GetRule("FirstCaptionAtZeroProbe");
            LocatorRule left = GetRule("FirstCaptionLeftEdge");
            LocatorRule oneFrame = GetRule("FirstCaptionOneFrameRight");
            if (!probe.HasCoordinate || !left.HasCoordinate || !oneFrame.HasCoordinate) return false;

            // This is a one-time migration of this home's three legacy coordinates.
            // The old values were captured against GetWindowRect (-8,-8,2576x1408),
            // while automation now measures the visible UIA bounds (0,0,2560x1392).
            // Requiring the exact legacy triplet prevents touching any other machine
            // or any independently recalibrated first-caption values.
            if (Math.Abs(probe.RelativeX - 0.078804001212120056) > 0.0000001 ||
                Math.Abs(left.RelativeX - 0.074146002531051636) > 0.0000001 ||
                Math.Abs(oneFrame.RelativeX - 0.074922002851963043) > 0.0000001) return false;

            probe.RelativeX = 0.0761715262196958;
            probe.RelativeY = 0.78448275862069;
            left.RelativeX = 0.0714844150468707;
            left.RelativeY = 0.78448275862069;
            oneFrame.RelativeX = 0.0722652653697878;
            oneFrame.RelativeY = 0.642959550879468;
            Locators["FirstCaptionAtZeroProbe"] = probe;
            Locators["FirstCaptionLeftEdge"] = left;
            Locators["FirstCaptionOneFrameRight"] = oneFrame;
            return true;
        }

        public static AppConfig LoadOrCreate(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!File.Exists(path)) {
                string examplePath = Path.Combine(Path.GetDirectoryName(path), "capcut.config.example.json");
                if (File.Exists(examplePath)) File.Copy(examplePath, path, false);
                else {
                    var created = CreateDefault();
                    created.Save(path);
                    return created;
                }
            }

            try {
                var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
                var raw = File.ReadAllText(path, Encoding.UTF8);
                var loaded = serializer.Deserialize<AppConfig>(raw);
                if (loaded == null) throw new InvalidDataException("配置文件内容为空");
                // Configurations created before local active draft storage was
                // introduced have no property at all.  Treat those legacy
                // files as opted in to the safe local-directory mode so a
                // stale false default cannot silently recreate a cross-drive
                // junction on the next run.  An explicit false remains a
                // supported opt-out for laboratory/rollback configurations.
                if (raw.IndexOf("\"UseLocalActiveDraftDirectory\"", StringComparison.OrdinalIgnoreCase) < 0)
                    loaded.UseLocalActiveDraftDirectory = true;
                loaded.Normalize();
                return loaded;
            } catch (Exception ex) {
                throw new InvalidDataException("无法读取配置文件 " + path + ": " + ex.Message, ex);
            }
        }

        public void Save(string path)
        {
            Normalize();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
            DurableFile.WriteAllText(path, serializer.Serialize(this), new UTF8Encoding(false));
        }

        private void Normalize()
        {
            if (String.IsNullOrWhiteSpace(OutputDirectory)) OutputDirectory = CreateDefault().OutputDirectory;
            else OutputDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(OutputDirectory));
            if (!String.IsNullOrWhiteSpace(CapCutExportStagingDirectory)) {
                CapCutExportStagingDirectory = Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(CapCutExportStagingDirectory));
            }
            if (String.IsNullOrWhiteSpace(ExportExtension)) ExportExtension = ".mp4";
            if (!ExportExtension.StartsWith(".")) ExportExtension = "." + ExportExtension;
            if (String.IsNullOrWhiteSpace(DefaultProjectName)) DefaultProjectName = "ProjectA";
            if (String.IsNullOrWhiteSpace(DefaultLanguageName)) DefaultLanguageName = "西班牙";
            if (String.IsNullOrWhiteSpace(DefaultOutputPrefix)) DefaultOutputPrefix = DefaultProjectName + DefaultLanguageName;
            if (!CategoryCatalog.IsKnownCategory(LastCategoryName)) LastCategoryName = "ProjectA";
            if (CategoryVideoDirectories == null) CategoryVideoDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (CategoryAudioDirectories == null) CategoryAudioDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrWhiteSpace(SampleDirectory) && !String.IsNullOrWhiteSpace(SamplePath) && Directory.Exists(SamplePath)) {
                SampleDirectory = SamplePath;
            }
            if (String.IsNullOrWhiteSpace(SampleDirectory) && !String.IsNullOrWhiteSpace(SamplePath) && File.Exists(SamplePath)) {
                SampleDirectory = Path.GetDirectoryName(Path.GetFullPath(SamplePath));
            }
            if (SampleSpeed <= 0) SampleSpeed = 1.0;
            if (StartSequence < 1) StartSequence = 1;
            if (String.IsNullOrWhiteSpace(ExportResolution)) ExportResolution = "2K";
            if (!String.Equals(DefaultSubtitleStylePreset, "CompanyStandard", StringComparison.OrdinalIgnoreCase)) DefaultSubtitleStylePreset = "Default";
            // An empty font is intentional: legacy settings must not restore a
            // fixed-font requirement during normalization.
            if (!String.IsNullOrWhiteSpace(CompanySubtitleFontPath))
                CompanySubtitleFontPath = Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(CompanySubtitleFontPath));
            if (CompanySubtitleFontSize < 1) CompanySubtitleFontSize = 10;
            if (ProcessNames == null || ProcessNames.Length == 0) ProcessNames = new[] { "JianyingPro", "CapCut" };
            if (MainWindowNames == null) MainWindowNames = new[] { "剪映", "CapCut" };
            if (Timeouts == null) Timeouts = CreateDefault().Timeouts;
            if (Locators == null) Locators = new Dictionary<string, LocatorRule>(StringComparer.OrdinalIgnoreCase);
            var defaults = CreateDefault().Locators;
            foreach (var pair in defaults) if (!Locators.ContainsKey(pair.Key)) Locators[pair.Key] = pair.Value;
            if (RetryCount < 0) RetryCount = 0;
            if (CaptionFallbackSeconds < 5) CaptionFallbackSeconds = 35;
        }
    }

    public static class AppPaths
    {
        public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        public static readonly string ConfigFile = Path.Combine(Root, "capcut.config.json");
        public static readonly string DataDirectory = Path.Combine(Root, "data");
        public static readonly string TaskFile = Path.Combine(DataDirectory, "capcut-tasks.json");
        public static readonly string LogDirectory = Path.Combine(Root, "logs");
        public static readonly string TaskDraftRootsDirectory =
            Path.Combine(DataDirectory, "task-draft-roots");
        // Durable draft archives deliberately live below a short path.  The
        // CapCut tree can contain relative paths close to MAX_PATH; keeping
        // this root at the volume root leaves enough headroom for every
        // checkpoint and diagnostic filename.
        public static readonly string ShortDraftArchiveDirectory =
            Path.Combine(Path.GetPathRoot(Root), ".capcut-drafts");
        public static readonly string DraftRootLeaseFile =
            Path.Combine(DataDirectory, "draft-root-lease.json");
        public static readonly string CapCutTransactionsDirectory =
            Path.Combine(DataDirectory, "capcut-transactions");
        // This marker is deployed only next to the isolated laboratory executable.
        // A normal production client never sees it and therefore cannot enter the
        // direct draft-file workflow by accident.
        public static readonly string IsolatedLabProfileFile = Path.Combine(Root, "isolated-subtitle-style-lab.profile");
    }

    // The style laboratory is intentionally process-local.  Its executable is
    // built in a separate directory, so its config, task store, logs and
    // outputs cannot be discovered by the formal workbench client.
    public static class RuntimeModes
    {
        public static bool SubtitleStyleLab { get; private set; }
        public static bool IsolatedLabRuntime { get; private set; }
        public static bool IsolatedLabHandoff { get; private set; }
        public static string IsolatedLabProfileSource { get; private set; }

        public static bool TryEnableInstalledIsolatedLabProfile()
        {
            if (!File.Exists(AppPaths.IsolatedLabProfileFile)) return false;
            string value = File.ReadAllText(AppPaths.IsolatedLabProfileFile, Encoding.UTF8)
                .Trim()
                .TrimStart('\uFEFF');
            if (!String.Equals(value, "isolated_subtitle_style_lab_v1", StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "隔离客户端运行身份文件无效，已拒绝以不明模式启动: " +
                    AppPaths.IsolatedLabProfileFile);
            }
            EnableIsolatedManualLab(AppPaths.IsolatedLabProfileFile);
            return true;
        }

        public static void EnableIsolatedManualLab(string profileSource)
        {
            SubtitleStyleLab = true;
            IsolatedLabRuntime = true;
            IsolatedLabHandoff = false;
            IsolatedLabProfileSource = profileSource ?? "manual";
        }

        public static void EnableSubtitleStyleLab()
        {
            SubtitleStyleLab = true;
            IsolatedLabRuntime = false;
        }

        public static void EnableIsolatedLabHandoff()
        {
            SubtitleStyleLab = true;
            IsolatedLabRuntime = true;
            IsolatedLabHandoff = true;
            IsolatedLabProfileSource = "command-line-handoff";
        }

        public static bool UseDirectDraftSubtitleStyle()
        {
            return SubtitleStyleLab;
        }

        public static bool ShouldUseDirectDraftSubtitleStyle(string stylePreset)
        {
            if (SubtitleStyleLab) return true;
            return String.Equals(stylePreset, "CompanyStandard", StringComparison.OrdinalIgnoreCase);
        }

        public static void ValidateDirectDraftSubtitleStyleDependencies(AppConfig config)
        {
            CapCutVersionGuard.Validate(config);
            DirectDraftSubtitleStylePatcher.ResolveFontPath(
                config == null ? null : config.CompanySubtitleFontPath);
            CapCutCliDraftStyleEngine.ValidateRuntime();
        }

        public static string GetIsolatedLabRoot()
        {
            if (!IsolatedLabRuntime) return "";
            DirectoryInfo client = new DirectoryInfo(AppPaths.Root);
            DirectoryInfo capcut = client.Parent;
            DirectoryInfo lab = capcut == null ? null : capcut.Parent;
            if (lab == null) throw new InvalidOperationException("无法推断隔离内测根目录。");
            return lab.FullName;
        }

        public static bool NormalizeWaitingIsolatedLabTasks(IEnumerable<VideoTask> tasks)
        {
            if (!IsolatedLabRuntime || tasks == null) return false;
            bool changed = false;
            foreach (VideoTask task in tasks) {
                if (task == null || task.GetState() != TaskState.Waiting) continue;
                if (!String.Equals(task.SubtitleStylePreset, "LabZYModern", StringComparison.OrdinalIgnoreCase)) {
                    task.SubtitleStylePreset = "LabZYModern";
                    changed = true;
                }
                if (!String.IsNullOrWhiteSpace(task.Error) &&
                    (task.Error.IndexOf("已停止", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     task.Error.IndexOf("可再次启动", StringComparison.OrdinalIgnoreCase) >= 0)) {
                    task.Error = "";
                    task.FinishedAt = "";
                    changed = true;
                }
            }
            return changed;
        }

        public static void ValidateIsolatedLabTaskAndDependencies(VideoTask task, AppConfig config)
        {
            if (!IsolatedLabRuntime) return;
            if (task == null) throw new InvalidOperationException("隔离内测缺少任务。");
            string labRoot = GetIsolatedLabRoot();
            RequirePathInsideLab("源视频", task.VideoPath, labRoot);
            RequirePathInsideLab("独立音频", task.AudioPath, labRoot);
            RequirePathInsideLab("最终输出", task.OutputPath, labRoot);
            ValidateDirectDraftSubtitleStyleDependencies(config);
        }

        private static void RequirePathInsideLab(string label, string path, string labRoot)
        {
            if (String.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("隔离内测缺少" + label + "路径。");
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetFullPath(labRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException(
                    "隔离内测拒绝使用根目录外的" + label + "路径: " + fullPath);
            }
        }

        public static void ApplySubtitleStyleLabDefaults(AppConfig config)
        {
            if (!SubtitleStyleLab || config == null) return;
            config.DefaultProjectName = "SubtitleStyleLab";
            config.DefaultMaterialName = "IsolatedTest";
            config.DefaultLanguageName = "English";
            config.DefaultOutputPrefix = "SubtitleStyleLab";
            config.DefaultSubtitleStylePreset = "LabZYModern";
            config.CompanySubtitleFont = "ZY Modern";
            config.CompanySubtitleFontSize = 10;
            if (IsolatedLabRuntime) {
                // Keep the copied workbench's task-scoped staging and output
                // paths.  The standalone lab below owns its separate sandbox.
                return;
            }
            config.OutputDirectory = Path.Combine(AppPaths.DataDirectory, "subtitle-style-lab-output");
            // This lab runs from bin\subtitle-style-lab-draft-direct while the
            // installed CapCut instance retains its own, user-level export
            // destination under the workspace.  Treat it only as an additional
            // read-only discovery candidate; production never receives this value.
            DirectoryInfo binDirectory = Directory.GetParent(AppPaths.Root);
            DirectoryInfo clientDirectory = binDirectory == null ? null : binDirectory.Parent;
            DirectoryInfo workspaceDirectory = clientDirectory == null ? null : clientDirectory.Parent;
            if (workspaceDirectory != null) {
                config.CapCutExportStagingDirectory = Path.Combine(workspaceDirectory.FullName,
                    "data", "home-capcut-test-v020", "output");
            }
            config.RetryCount = 0;
        }

        public static void ValidateLaunch(bool autoStart, string handoffPath)
        {
            if (!SubtitleStyleLab) return;
            if (IsolatedLabHandoff) {
                if (!autoStart || String.IsNullOrWhiteSpace(handoffPath))
                    throw new InvalidOperationException(
                        "The isolated handoff laboratory requires both --handoff and --auto-start.");
                return;
            }
            if (IsolatedLabRuntime) {
                if (autoStart || !String.IsNullOrWhiteSpace(handoffPath)) {
                    throw new InvalidOperationException(
                        "隔离手工客户端不能接受工作台交接或自动启动参数。");
                }
                return;
            }
            if (autoStart || !String.IsNullOrWhiteSpace(handoffPath))
                throw new InvalidOperationException(
                    "The isolated subtitle-style laboratory cannot auto-start or accept a workbench handoff.");
        }
    }

    public sealed class AppLogger
    {
        private readonly object sync = new object();
        public event Action<string> LineWritten;

        public void Info(string message) { Write("INFO", message); }
        public void Warn(string message) { Write("WARN", message); }
        public void Error(string message) { Write("ERROR", message); }

        private void Write(string level, string message)
        {
            string line = String.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}", DateTime.Now, level, message);
            lock (sync) {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                string path = Path.Combine(AppPaths.LogDirectory, "capcut-batch-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            var handler = LineWritten;
            if (handler != null) handler(line);
        }
    }

    public sealed class TaskStore
    {
        private readonly string path;
        private readonly object sync = new object();

        public TaskStore(string path) { this.path = path; }

        public List<VideoTask> Load()
        {
            lock (sync) using (AcquireFileMutex()) {
                if (!File.Exists(path)) return new List<VideoTask>();
                try {
                    var serializer = new JavaScriptSerializer { MaxJsonLength = 10 * 1024 * 1024 };
                    List<VideoTask> tasks = serializer.Deserialize<List<VideoTask>>(File.ReadAllText(path, Encoding.UTF8)) ?? new List<VideoTask>();
                    bool migrated = tasks.Any(task => task != null && task.NormalizeLegacyCompletedCleanupWarning());
                    if (migrated) WriteWithoutLock(tasks, serializer);
                    return tasks;
                } catch {
                    string backup = path + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    File.Copy(path, backup, true);
                    return new List<VideoTask>();
                }
            }
        }

        public void Save(IEnumerable<VideoTask> tasks)
        {
            lock (sync) using (AcquireFileMutex()) {
                var serializer = new JavaScriptSerializer { MaxJsonLength = 10 * 1024 * 1024 };
                WriteWithoutLock(tasks, serializer);
            }
        }

        private void WriteWithoutLock(IEnumerable<VideoTask> tasks, JavaScriptSerializer serializer)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            DurableFile.WriteAllText(temp, serializer.Serialize(tasks.ToList()), new UTF8Encoding(false));
            if (File.Exists(path)) {
                try { File.Replace(temp, path, null); }
                catch { File.Copy(temp, path, true); File.Delete(temp); }
            } else {
                File.Move(temp, path);
            }
        }

        private IDisposable AcquireFileMutex()
        {
            var mutex = new Mutex(false, InterprocessMutexNames.ForPath("task-store", path));
            bool acquired = false;
            try {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(12));
                if (!acquired) throw new TimeoutException("CapCut 任务表正被另一客户端占用，已拒绝读取或覆盖: " + path);
                return new MutexLease(mutex);
            } catch {
                if (!acquired) mutex.Dispose();
                throw;
            }
        }

        private sealed class MutexLease : IDisposable
        {
            private Mutex mutex;
            public MutexLease(Mutex mutex) { this.mutex = mutex; }
            public void Dispose()
            {
                Mutex current = Interlocked.Exchange(ref mutex, null);
                if (current == null) return;
                try { current.ReleaseMutex(); } catch { }
                current.Dispose();
            }
        }
    }

    public static class OutputNaming
    {
        public static string LabRerunPath(string previousOutputPath, string attemptStamp)
        {
            string fullPrevious = Path.GetFullPath(previousOutputPath);
            string directory = Path.GetDirectoryName(fullPrevious);
            string extension = Path.GetExtension(fullPrevious);
            string stem = Path.GetFileNameWithoutExtension(fullPrevious);
            const string rerunMarker = "-lab-rerun-";
            int existingMarker = stem.IndexOf(rerunMarker, StringComparison.OrdinalIgnoreCase);
            if (existingMarker >= 0) stem = stem.Substring(0, existingMarker);

            string suffix = rerunMarker + attemptStamp;
            // Keep ample room below the legacy MAX_PATH boundary for CapCut
            // temporary exports and FFmpeg working artifacts.
            const int safeFullPathLength = 220;
            int availableStem = safeFullPathLength -
                directory.Length - 1 - suffix.Length - extension.Length;
            if (availableStem < 8)
                throw new PathTooLongException("隔离内测输出目录过长，无法创建安全的重跑文件名");
            if (stem.Length > availableStem) stem = stem.Substring(0, availableStem);
            return Path.Combine(directory, stem + suffix + extension);
        }

        public static string ProjectBatchPath(string outputPrefix, int sequence, string outputDirectory, string extension)
        {
            string safePrefix = SanitizeFileName(String.IsNullOrWhiteSpace(outputPrefix) ? "成片" : outputPrefix.Trim());
            return Path.GetFullPath(Path.Combine(outputDirectory, safePrefix + Math.Max(1, sequence) + extension));
        }

        public static string UniqueCaptionPath(string videoPath, string outputDirectory, string extension, IEnumerable<string> reservedPaths)
        {
            string baseName = Path.GetFileNameWithoutExtension(videoPath) + "_字幕版";
            var reserved = new HashSet<string>((reservedPaths ?? new string[0]).Where(p => !String.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            int suffix = 1;
            while (true) {
                string name = suffix == 1 ? baseName : String.Format("{0} ({1})", baseName, suffix);
                string candidate = Path.GetFullPath(Path.Combine(outputDirectory, name + extension));
                if (!File.Exists(candidate) && !reserved.Contains(candidate)) return candidate;
                suffix++;
            }
        }

        private static string SanitizeFileName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return value.Trim();
        }
    }

    public static class TaskOrigins
    {
        public const string Manual = "Manual";
        public const string TikHubHandoff = "TikHubHandoff";
        public const string CategoryScan = "CategoryScan";
    }

    public static class SourceDeletionPolicy
    {
        public static bool ShouldRecycle(string taskOrigin, bool manualOptIn)
        {
            if (String.Equals(taskOrigin, TaskOrigins.TikHubHandoff, StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(taskOrigin, TaskOrigins.CategoryScan, StringComparison.OrdinalIgnoreCase)) return true;
            return String.Equals(taskOrigin, TaskOrigins.Manual, StringComparison.OrdinalIgnoreCase) && manualOptIn;
        }
    }

    public static class DiagnosticLaunchPolicy
    {
        public static void ValidateFirstCaptionQNoOp(bool enabled, bool autoStart, string handoffPath)
        {
            if (enabled && (autoStart || !String.IsNullOrWhiteSpace(handoffPath))) {
                throw new InvalidOperationException("首帧 Q 失效诊断模式不能与工作台交接或自动启动同时使用");
            }
        }

    }

    public static class LocalFirstCaptionTestFactory
    {
        public static void ValidateLaunch(string testCase, string packDirectory,
            bool diagnosticQNoOp, string handoffPath)
        {
            if (String.IsNullOrWhiteSpace(testCase)) return;
            string normalized = testCase.Trim().ToUpperInvariant();
            if (normalized != "A" && normalized != "B" && normalized != "C")
                throw new InvalidOperationException("本机首帧测试只允许 A、B、C");
            if (!String.IsNullOrWhiteSpace(handoffPath))
                throw new InvalidOperationException("本机首帧测试不能与工作台交接同时启动");
            if (normalized == "C" && !diagnosticQNoOp)
                throw new InvalidOperationException("C 测试必须启用 Q 不生效诊断开关");
            if (normalized != "C" && diagnosticQNoOp)
                throw new InvalidOperationException("Q 不生效诊断开关只能用于 C 测试");
            if (String.IsNullOrWhiteSpace(packDirectory) || !Directory.Exists(packDirectory))
                throw new DirectoryNotFoundException("本机首帧测试包不存在: " + packDirectory);
        }

        public static VideoTask Create(string testCase, string packDirectory, AppConfig config)
        {
            ValidateLaunch(testCase, packDirectory,
                String.Equals(testCase, "C", StringComparison.OrdinalIgnoreCase), "");
            string normalized = testCase.Trim().ToUpperInvariant();
            string videoName;
            string audioName;
            if (normalized == "A") {
                videoName = "A_主视频_首帧应有字幕.mp4";
                audioName = "A_独立音频_无前置静音.wav";
            } else if (normalized == "B") {
                videoName = "B_主视频_首帧不应有字幕.mp4";
                audioName = "B_独立音频_前置2秒静音.wav";
            } else {
                videoName = "C_主视频_Q失败应拦截.mp4";
                audioName = "C_独立音频_无前置静音.wav";
            }
            string videoPath = Path.Combine(packDirectory, videoName);
            string audioPath = Path.Combine(packDirectory, audioName);
            if (!File.Exists(videoPath)) throw new FileNotFoundException("首帧测试视频不存在", videoPath);
            if (!File.Exists(audioPath)) throw new FileNotFoundException("首帧测试音频不存在", audioPath);
            string outputRoot = Path.Combine(AppPaths.DataDirectory, "first-caption-e2e-output");
            Directory.CreateDirectory(outputRoot);
            return new VideoTask {
                Id = Guid.NewGuid().ToString("N"),
                VideoPath = videoPath,
                AudioPath = audioPath,
                SamplePath = "",
                OutputPath = Path.Combine(outputRoot,
                    "FirstCaption-" + normalized + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".mp4"),
                GroupId = "local-first-caption-" + normalized + "-" + Guid.NewGuid().ToString("N"),
                ProjectName = "首帧测试",
                MaterialName = normalized,
                LanguageName = "英语",
                OutputPrefix = "FirstCaption-" + normalized,
                ExportResolution = String.IsNullOrWhiteSpace(config.ExportResolution) ? "2K" : config.ExportResolution,
                SubtitleStylePreset = RuntimeModes.SubtitleStyleLab ? "LabZYModern" : "Default",
                SampleSpeed = 0,
                Sequence = 1,
                SourceRootPath = Path.GetFullPath(packDirectory),
                TaskOrigin = TaskOrigins.Manual,
                DeleteSourceAfterSuccess = !RuntimeModes.SubtitleStyleLab && (normalized == "A" || normalized == "B"),
                PreserveDraftOnFailure = RuntimeModes.SubtitleStyleLab || normalized == "C",
                PreserveDraftAfterSuccess = RuntimeModes.SubtitleStyleLab
            };
        }
    }

    // A local sample-append test must never reuse the formal task store or its
    // workbench handoff status.  It reads formal media identities only, then creates
    // isolated manual tasks with independent outputs and no cleanup permission.
    public sealed class LocalSampleAppendTestRun
    {
        public string StorePath { get; set; }
        public string RunDirectory { get; set; }
        public List<VideoTask> Tasks { get; set; }
    }

    public static class LocalSampleAppendTestFactory
    {
        public static LocalSampleAppendTestRun Create(string formalTaskFile, AppConfig config)
        {
            return Create(formalTaskFile, config, "", false);
        }

        public static void ValidateLaunch(string taskFile, string handoffPath,
            string sampleOverridePath, bool withoutSample)
        {
            if (String.IsNullOrWhiteSpace(taskFile)) return;
            if (!String.IsNullOrWhiteSpace(handoffPath)) {
                throw new InvalidOperationException("本机样品追加测试不能与工作台交接同时启动");
            }
            if (!File.Exists(taskFile)) throw new FileNotFoundException("本机样品测试任务表不存在", taskFile);
            if (withoutSample && !String.IsNullOrWhiteSpace(sampleOverridePath)) {
                throw new InvalidOperationException("本机测试不能同时指定样品覆盖和无样品模式");
            }
            if (!String.IsNullOrWhiteSpace(sampleOverridePath) && !File.Exists(sampleOverridePath)) {
                throw new FileNotFoundException("本机测试指定的样品不存在", sampleOverridePath);
            }
        }

        public static LocalSampleAppendTestRun Create(string formalTaskFile, AppConfig config,
            string sampleOverridePath, bool withoutSample)
        {
            ValidateLaunch(formalTaskFile, "", sampleOverridePath, withoutSample);
            List<VideoTask> originals = new TaskStore(formalTaskFile).Load()
                .Where(task => withoutSample || !String.IsNullOrWhiteSpace(task.SamplePath) ||
                    !String.IsNullOrWhiteSpace(sampleOverridePath))
                .OrderBy(task => task.Sequence)
                .ToList();
            if (originals.Count == 0) {
                throw new InvalidOperationException("测试任务表中没有带结尾样品的任务");
            }
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string runDirectory = Path.Combine(AppPaths.DataDirectory, "local-sample-e2e", "runs", stamp);
            string outputDirectory = Path.Combine(runDirectory, "output");
            Directory.CreateDirectory(outputDirectory);
            var tasks = new List<VideoTask>();
            bool externalSampleMode = !String.IsNullOrWhiteSpace(sampleOverridePath) || withoutSample;
            if (externalSampleMode) originals = originals.Take(1).ToList();
            foreach (VideoTask source in originals) {
                if (!File.Exists(source.VideoPath)) throw new FileNotFoundException("本机样品测试主视频不存在", source.VideoPath);
                if (!File.Exists(source.AudioPath)) throw new FileNotFoundException("本机样品测试音频不存在", source.AudioPath);
                string effectiveSamplePath = withoutSample ? "" :
                    (String.IsNullOrWhiteSpace(sampleOverridePath) ? source.SamplePath : sampleOverridePath);
                if (!String.IsNullOrWhiteSpace(effectiveSamplePath) && !File.Exists(effectiveSamplePath))
                    throw new FileNotFoundException("本机样品测试样品不存在", effectiveSamplePath);
                double effectiveSampleSpeed = source.SampleSpeed;
                // Formal task entries already contain their planned sample speed.
                // Recalculate only for an explicitly supplied external test sample.
                if (!String.IsNullOrWhiteSpace(sampleOverridePath)) {
                    MediaMetadata video = WindowsMediaMetadataReader.Read(source.VideoPath);
                    MediaMetadata audio = WindowsMediaMetadataReader.Read(source.AudioPath);
                    MediaMetadata sample = WindowsMediaMetadataReader.Read(effectiveSamplePath);
                    double available = audio.DurationSeconds - video.DurationSeconds;
                    if (available <= 0.10) {
                        throw new InvalidOperationException("本机样品测试音频不长于主视频，无法验证样品追加");
                    }
                    effectiveSampleSpeed = SamplePlanner.CalculateSpeed(sample.DurationSeconds, available);
                }
                string id = "local-sample-" + Guid.NewGuid().ToString("N");
                string extension = String.IsNullOrWhiteSpace(config.ExportExtension) ? ".mp4" : config.ExportExtension;
                tasks.Add(new VideoTask {
                    Id = id,
                    VideoPath = source.VideoPath,
                    AudioPath = source.AudioPath,
                    SamplePath = effectiveSamplePath,
                    OutputPath = Path.Combine(outputDirectory, "LocalSample-" + source.Sequence + "-" + stamp + extension),
                    TemporaryOutputPath = Path.Combine(outputDirectory, "LocalSample-" + source.Sequence + "-" + stamp + ".capcut-temp-" + id + extension),
                    GroupId = "local-sample-" + stamp,
                    ProjectName = withoutSample ? "本机无样品测试" :
                        (String.IsNullOrWhiteSpace(sampleOverridePath) ? "本机样品追加测试" : "本机样品覆盖测试"),
                    MaterialName = source.MaterialName,
                    LanguageName = source.LanguageName,
                    VoiceTaskNumber = source.VoiceTaskNumber,
                    OutputPrefix = "LocalSample-" + source.Sequence,
                    ExportResolution = String.IsNullOrWhiteSpace(source.ExportResolution) ? config.ExportResolution : source.ExportResolution,
                    SubtitleStylePreset = source.SubtitleStylePreset,
                    SampleSpeed = effectiveSampleSpeed,
                    Sequence = source.Sequence,
                    SourceRootPath = source.SourceRootPath,
                    TaskOrigin = TaskOrigins.Manual,
                    DeleteSourceAfterSuccess = false,
                    PreserveDraftOnFailure = true,
                    IsHandoffTask = false,
                    HandoffBatchId = "",
                    HandoffStatusFile = "",
                    RemotePipelineTaskId = "",
                    ExpectedTaskCount = originals.Count,
                    State = TaskState.Waiting.ToString(),
                    Attempts = 0,
                    Error = ""
                });
            }
            return new LocalSampleAppendTestRun {
                RunDirectory = runDirectory,
                StorePath = Path.Combine(runDirectory, "tasks.json"),
                Tasks = tasks
            };
        }
    }

    // Pure first-caption frame arithmetic.  Keeping this outside UI automation
    // makes the safety decisions testable without a live CapCut window.
    public static class FirstCaptionFrameValidation
    {
        public static double CaptionScanLeftRatio(double calibratedZeroRatio, bool hasCoordinate)
        {
            // The scan must begin to the left of the calibrated timeline zero.
            // Beginning at 7.5% hid the real x=183 caption edge on a 2560-wide
            // maximized window and made the detector report the scan boundary.
            return hasCoordinate
                ? Math.Max(0.02, calibratedZeroRatio - 0.01)
                : 0.06;
        }

        public static int ExpectedFramePixels(int expectedZeroX, int expectedOneFrameX)
        {
            int pixels = expectedOneFrameX - expectedZeroX;
            if (pixels <= 0) {
                throw new InvalidOperationException("首帧和下一帧定位无效：下一帧必须位于时间线零点右侧");
            }
            return pixels;
        }

        public static bool StartsAtZero(int beforeLeft, int expectedZeroX, int expectedFramePixels)
        {
            if (expectedFramePixels <= 0) return false;
            // CapCut draws a rounded/anti-aliased inset at the visible left edge of
            // a caption bar.  The colour detector therefore starts a few pixels to
            // the right of the logical zero playhead.  Accept only an inset smaller
            // than one calibrated frame.  A caption that really starts one frame
            // late is outside this interval and is still skipped.
            int delta = beforeLeft - expectedZeroX;
            return delta >= 0 && delta < expectedFramePixels;
        }

        public static int MovedPixels(int beforeLeft, int afterLeft)
        {
            return afterLeft - beforeLeft;
        }

        public static void RequireExactlyOneFrame(int movedPixels, int expectedFramePixels)
        {
            if (movedPixels != expectedFramePixels) {
                throw new InvalidOperationException(String.Format(
                    "首帧字幕左边缘移动距离不等于一帧：期望 {0}px，实际 {1}px；已阻止导出",
                    expectedFramePixels, movedPixels));
            }
        }

        public static void RequireLeftEdgeOnly(
            int beforeLeft,
            int beforeRight,
            int afterLeft,
            int afterRight,
            int expectedFramePixels)
        {
            RequireExactlyOneFrame(MovedPixels(beforeLeft, afterLeft), expectedFramePixels);
            if (afterRight != beforeRight) {
                throw new InvalidOperationException(String.Format(
                    "首帧字幕右边缘发生变化：裁剪前 {0}px，裁剪后 {1}px；已阻止导出",
                    beforeRight, afterRight));
            }
            if (afterLeft >= afterRight) {
                throw new InvalidOperationException("首帧字幕被移动或删除，而不是只缩短左边缘；已阻止导出");
            }
        }
    }

    // Stable identity is deliberately independent from manifest/attempt IDs.
    // It is the only key used for the durable CapCut transaction file.
    public sealed class CapCutTaskIdentity
    {
        public string CanonicalTaskId { get; private set; }
        public string ProductionDate { get; private set; }
        public string LocalTaskId { get; private set; }
        public string NormalizedSourcePath { get; private set; }
        public string StableTaskArchiveKey { get; private set; }

        private CapCutTaskIdentity() { }

        public static CapCutTaskIdentity FromTask(VideoTask task)
        {
            if (task == null) throw new ArgumentNullException("task");
            string canonical = String.IsNullOrWhiteSpace(task.RemotePipelineTaskId)
                ? (task.Id ?? "") : task.RemotePipelineTaskId;
            string source = NormalizePath(task.VideoPath);
            string productionDate = task.ProductionDate ?? "";
            string local = task.Id ?? "";
            string material = canonical + "|" + productionDate + "|" + local + "|" + source;
            return new CapCutTaskIdentity {
                CanonicalTaskId = canonical,
                ProductionDate = productionDate,
                LocalTaskId = local,
                NormalizedSourcePath = source,
                StableTaskArchiveKey = String.IsNullOrWhiteSpace(task.StableTaskArchiveKey)
                    ? "t-" + ShortHash(material, 24)
                    : task.StableTaskArchiveKey.Trim()
            };
        }

        public bool Matches(CapCutTaskIdentity other)
        {
            return other != null &&
                String.Equals(CanonicalTaskId, other.CanonicalTaskId, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(ProductionDate, other.ProductionDate, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(LocalTaskId, other.LocalTaskId, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(NormalizedSourcePath, other.NormalizedSourcePath, StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizePath(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim())).TrimEnd('\\', '/').ToUpperInvariant(); }
            catch { return value.Trim().Replace('/', '\\').ToUpperInvariant(); }
        }

        private static string ShortHash(string value, int length)
        {
            using (SHA256 sha = SHA256.Create()) {
                string hex = BitConverter.ToString(sha.ComputeHash(
                    Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
                return hex.Substring(0, Math.Min(length, hex.Length));
            }
        }
    }

    public sealed class CapCutDraftTransactionStore
    {
        private readonly object sync = new object();
        private readonly string root;

        public CapCutDraftTransactionStore() : this(AppPaths.CapCutTransactionsDirectory) { }

        internal CapCutDraftTransactionStore(string root) { this.root = Path.GetFullPath(root); }

        public CapCutDraftTransaction Load(VideoTask task)
        {
            if (task == null) return null;
            CapCutTaskIdentity identity = CapCutTaskIdentity.FromTask(task);
            string path = Path.Combine(root, identity.StableTaskArchiveKey + ".json");
            lock (sync) {
                if (!File.Exists(path)) return null;
                try {
                    JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 10 * 1024 * 1024 };
                    CapCutDraftTransaction transaction = serializer.Deserialize<CapCutDraftTransaction>(File.ReadAllText(path, Encoding.UTF8));
                    if (transaction == null || !String.Equals(transaction.StableTaskArchiveKey,
                        identity.StableTaskArchiveKey, StringComparison.OrdinalIgnoreCase)) return null;
                    return transaction;
                } catch { return null; }
            }
        }

        public void Save(CapCutDraftTransaction transaction)
        {
            if (transaction == null || String.IsNullOrWhiteSpace(transaction.StableTaskArchiveKey))
                throw new ArgumentNullException("transaction");
            lock (sync) {
                Directory.CreateDirectory(root);
                transaction.UpdatedAtUtc = DateTime.UtcNow.ToString("o");
                JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 10 * 1024 * 1024 };
                string path = Path.Combine(root, transaction.StableTaskArchiveKey + ".json");
                string temp = Path.Combine(root, transaction.StableTaskArchiveKey + "." + ProcessIdentity() + ".tmp");
                DurableFile.WriteAllText(temp, serializer.Serialize(transaction), new UTF8Encoding(false));
                try {
                    if (File.Exists(path)) {
                        try { File.Replace(temp, path, null); }
                        catch { File.Copy(temp, path, true); File.Delete(temp); }
                    } else File.Move(temp, path);
                } finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            }
        }

        private static string ProcessIdentity()
        {
            return System.Diagnostics.Process.GetCurrentProcess().Id + "-" +
                Thread.CurrentThread.ManagedThreadId + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }
    }

    public sealed class CapCutDraftTransactionCoordinator
    {
        private readonly CapCutDraftTransactionStore store;
        private readonly AppLogger logger;

        public CapCutDraftTransactionCoordinator(AppLogger logger)
            : this(new CapCutDraftTransactionStore(), logger) { }

        internal CapCutDraftTransactionCoordinator(CapCutDraftTransactionStore store, AppLogger logger)
        {
            this.store = store;
            this.logger = logger;
        }

        public CapCutDraftTransaction LoadOrCreate(VideoTask task)
        {
            CapCutTaskIdentity identity = CapCutTaskIdentity.FromTask(task);
            if (String.IsNullOrWhiteSpace(task.StableTaskArchiveKey))
                task.StableTaskArchiveKey = identity.StableTaskArchiveKey;
            CapCutDraftTransaction transaction = store.Load(task);
            if (transaction != null &&
                (!String.Equals(transaction.CanonicalTaskId ?? "", identity.CanonicalTaskId ?? "", StringComparison.OrdinalIgnoreCase) ||
                 !String.Equals(transaction.ProductionDate ?? "", identity.ProductionDate ?? "", StringComparison.OrdinalIgnoreCase) ||
                 !String.Equals(transaction.LocalTaskId ?? "", identity.LocalTaskId ?? "", StringComparison.OrdinalIgnoreCase) ||
                 !String.Equals(transaction.NormalizedSourcePath ?? "", identity.NormalizedSourcePath ?? "", StringComparison.OrdinalIgnoreCase)))
                throw new CapCutOperationFailure("identity",
                    "CAPCUT_TASK_IDENTITY_CONFLICT",
                    CapCutFailureClassification.HardSafety,
                    "持久化CapCut事务与当前任务身份不一致");
            if (transaction == null) {
                transaction = new CapCutDraftTransaction {
                    StableTaskArchiveKey = identity.StableTaskArchiveKey,
                    CanonicalTaskId = identity.CanonicalTaskId,
                    ProductionDate = identity.ProductionDate,
                    LocalTaskId = identity.LocalTaskId,
                    NormalizedSourcePath = identity.NormalizedSourcePath,
                    ImportPhase = task.ImportPhase ?? "import_waiting",
                    ImportCommandId = task.ImportCommandId ?? "",
                    ImportPath = task.ImportPath ?? ""
                };
                Save(transaction);
            }
            SyncTask(task, transaction);
            return transaction;
        }

        // Begin a new attempt without losing the stable task transaction.  A
        // prior attempt that reached release/restore is a completed lifecycle
        // generation, not an instruction to reject the next lease/editor
        // phases as a regression.  Media checkpoints remain intact.
        public CapCutDraftTransaction BeginOrResumeAttempt(VideoTask task, string attemptId)
        {
            CapCutDraftTransaction transaction = LoadOrCreate(task);
            string current = transaction.ActiveAttemptId ?? transaction.AttemptId ?? "";
            bool same = String.Equals(current, attemptId,
                StringComparison.OrdinalIgnoreCase);
            bool releaseBoundary = PhaseRank(transaction.Phase) >=
                PhaseRank("original_draft_restored");
            bool productionDone = String.Equals(transaction.ProductionStatus,
                "completed", StringComparison.OrdinalIgnoreCase);
            bool taskReleaseCommitted = task != null &&
                (String.Equals(task.DraftReleaseStatus, "released",
                    StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(task.DraftCleanupStatus, "completed",
                    StringComparison.OrdinalIgnoreCase));
            bool transactionReleaseCommitted =
                String.Equals(transaction.DraftReleaseStatus, "released",
                    StringComparison.OrdinalIgnoreCase) ||
                String.Equals(transaction.DraftReleaseStatus, "ready",
                    StringComparison.OrdinalIgnoreCase);
            // A restored transaction is a lifecycle boundary.  Older builds
            // sometimes persisted no ActiveAttemptId (or persisted the new
            // one before rotating the phase), which made the old guard skip
            // the generation reset and every subsequent lease/media mark was
            // logged as a phase regression.  The release fact is now derived
            // from both task and transaction state; a stale lease id alone is
            // not evidence that a live lease still owns the directory.
            bool priorLeaseIsReleased = taskReleaseCommitted ||
                transactionReleaseCommitted ||
                String.IsNullOrWhiteSpace(transaction.LeaseId);
            bool newLifecycleRequired = releaseBoundary && !productionDone &&
                priorLeaseIsReleased;
            if (newLifecycleRequired) {
                if (transaction.PreviousAttemptHistory == null)
                    transaction.PreviousAttemptHistory = new List<string>();
                if (!String.IsNullOrWhiteSpace(current) && !same)
                    transaction.PreviousAttemptHistory.Add(current);
                while (transaction.PreviousAttemptHistory.Count > 20)
                    transaction.PreviousAttemptHistory.RemoveAt(0);
                transaction.Generation = Math.Max(0, transaction.Generation) + 1;
                transaction.Phase = "waiting";
                transaction.StopState = "none";
                transaction.NextAction = "acquire_lease";
                transaction.PrimaryErrorCode = "";
                transaction.PrimaryErrorDiagnostic = "";
                transaction.PrimaryErrorAttemptId = "";
                transaction.FailureClassification = "";
                transaction.ImportPhase = "import_waiting";
                transaction.ImportCommandId = "";
                transaction.ImportPath = "";
                // Keep the durable creation fact. A released lease does not
                // mean that the create command never produced a draft; the
                // next attempt must rebind that draft or reconcile its real
                // media stage instead of clicking Start again.
            }
            if (!String.IsNullOrWhiteSpace(attemptId)) {
                transaction.ActiveAttemptId = attemptId;
                transaction.AttemptId = attemptId;
            }
            SyncTask(task, transaction);
            Save(transaction);
            return transaction;
        }

        public void Mark(VideoTask task, string phase, string attemptId,
            string commandId = "", CapCutFailureClassification? classification = null,
            string errorCode = "", string diagnostic = "")
        {
            CapCutDraftTransaction transaction = LoadOrCreate(task);
            if (!String.IsNullOrWhiteSpace(phase) &&
                CanAdvancePhase(transaction.Phase, phase)) {
                transaction.Phase = phase;
                transaction.NextAction = NextActionForPhase(phase);
            } else if (!String.IsNullOrWhiteSpace(phase) &&
                !String.Equals(transaction.Phase, phase, StringComparison.OrdinalIgnoreCase) &&
                logger != null) {
                logger.Warn("CapCut事务拒绝阶段倒退: current=" +
                    (transaction.Phase ?? "") + "; requested=" + phase);
            }
            if (!String.IsNullOrWhiteSpace(attemptId)) {
                RotateAttemptErrorIfNeeded(transaction, attemptId);
                transaction.AttemptId = attemptId;
                transaction.ActiveAttemptId = attemptId;
            }
            if (!String.IsNullOrWhiteSpace(commandId)) transaction.CommandId = commandId;
            if (classification.HasValue) transaction.FailureClassification = classification.Value.ToString();
            if (!String.IsNullOrWhiteSpace(errorCode) && String.IsNullOrWhiteSpace(transaction.PrimaryErrorCode)) {
                transaction.PrimaryErrorCode = errorCode;
                transaction.PrimaryErrorDiagnostic = diagnostic ?? "";
                transaction.PrimaryErrorAttemptId = attemptId ?? transaction.AttemptId ?? "";
            }
            SyncTask(task, transaction);
            Save(transaction);
        }

        // Import phases are a sub-transaction.  They must not be fed through
        // the monotonic top-level media phase reducer: a retry can legitimately
        // resume from import_waiting while the outer transaction is already in
        // video_assembly.  Recording them separately prevents false
        // "phase regression" blocks after a client restart.
        public void RecordImportProgress(VideoTask task, string phase,
            string attemptId, string commandId, string importPath)
        {
            CapCutDraftTransaction transaction = LoadOrCreate(task);
            transaction.ImportPhase = String.IsNullOrWhiteSpace(phase)
                ? "import_waiting" : phase;
            transaction.ImportCommandId = commandId ?? "";
            transaction.ImportPath = importPath ?? "";
            if (!String.IsNullOrWhiteSpace(attemptId)) {
                transaction.AttemptId = attemptId;
                transaction.ActiveAttemptId = attemptId;
            }
            SyncTask(task, transaction);
            Save(transaction);
        }

        public void RequestStop(VideoTask task, string attemptId, string nextAction)
        {
            CapCutDraftTransaction transaction = LoadOrCreate(task);
            transaction.StopState = "stop_requested";
            if (!String.IsNullOrWhiteSpace(attemptId)) transaction.AttemptId = attemptId;
            transaction.NextAction = String.IsNullOrWhiteSpace(nextAction)
                ? "recover_and_release_lease" : nextAction;
            SyncTask(task, transaction);
            Save(transaction);
        }

        public void ResetReleasedCreationForNewAttempt(VideoTask task,
            string attemptId, string diagnostic)
        {
            CapCutDraftTransaction transaction = LoadOrCreate(task);
            if (transaction == null) return;
            string phase = transaction.Phase ?? "";
            bool released = phase.IndexOf("release", StringComparison.OrdinalIgnoreCase) >= 0 ||
                phase.IndexOf("restore", StringComparison.OrdinalIgnoreCase) >= 0 ||
                String.Equals(transaction.DraftReleaseStatus, "released",
                    StringComparison.OrdinalIgnoreCase);
            if (!released) return;
            transaction.Generation = Math.Max(0, transaction.Generation) + 1;
            transaction.Phase = "waiting";
            transaction.StopState = "none";
            transaction.NextAction = "acquire_lease";
            transaction.PrimaryErrorCode = "";
            transaction.PrimaryErrorDiagnostic = "";
            transaction.PrimaryErrorAttemptId = "";
            transaction.FailureClassification = "";
            transaction.DraftCreationCheckpoint = null;
            transaction.ImportPhase = "import_waiting";
            transaction.ImportCommandId = "";
            transaction.ImportPath = "";
            if (!String.IsNullOrWhiteSpace(attemptId)) {
                transaction.AttemptId = attemptId;
                transaction.ActiveAttemptId = attemptId;
            }
            if (logger != null && !String.IsNullOrWhiteSpace(diagnostic))
                logger.Warn("已清除上一轮释放后不可接管的草稿创建检查点: " + diagnostic);
            SyncTask(task, transaction);
            Save(transaction);
        }

        public void MarkLease(VideoTask task, string leaseId, string attemptId)
        {
            CapCutDraftTransaction transaction = LoadOrCreate(task);
            transaction.LeaseId = leaseId ?? "";
            if (!String.IsNullOrWhiteSpace(attemptId)) transaction.AttemptId = attemptId;
            SyncTask(task, transaction);
            Save(transaction);
        }

        public void CaptureCreation(VideoTask task, DraftCreationCheckpoint checkpoint)
        {
            if (task == null || checkpoint == null) return;
            CapCutDraftTransaction transaction = LoadOrCreate(task);
            transaction.DraftCreationCheckpoint = checkpoint;
            // CreationPhase is a detailed sub-state (baseline_captured,
            // editor_window_ready, creation_completed), not a top-level
            // transaction phase.  Persist it in the nested checkpoint while
            // keeping the outer lifecycle monotonic.  In particular,
            // "creation_completed" must never compare as a terminal
            // transaction and block the subsequent video/audio stages.
            if (PhaseRank(transaction.Phase) < PhaseRank("draft_creation"))
                transaction.Phase = "draft_creation";
            SyncTask(task, transaction);
            Save(transaction);
        }

        public void MarkFailure(VideoTask task, CapCutOperationFailure failure,
            string attemptId = "")
        {
            if (task == null || failure == null) return;
            CapCutDraftTransaction transaction = LoadOrCreate(task);
            string effectiveAttempt = !String.IsNullOrWhiteSpace(attemptId)
                ? attemptId : (failure.AttemptId ?? transaction.AttemptId ?? "");
            if (!String.IsNullOrWhiteSpace(effectiveAttempt))
                RotateAttemptErrorIfNeeded(transaction, effectiveAttempt);
            if (String.IsNullOrWhiteSpace(transaction.PrimaryErrorCode)) {
                transaction.PrimaryErrorCode = failure.ErrorCode;
                transaction.PrimaryErrorDiagnostic = failure.Diagnostic;
                transaction.PrimaryErrorAttemptId = effectiveAttempt;
                transaction.FailureClassification = failure.Classification.ToString();
            }
            SyncTask(task, transaction);
            Save(transaction);
        }

        public void Complete(VideoTask task, string productionStatus, string draftReleaseStatus)
        {
            CapCutDraftTransaction transaction = LoadOrCreate(task);
            transaction.ProductionStatus = productionStatus ?? "completed";
            transaction.DraftReleaseStatus = draftReleaseStatus ?? "released";
            bool releasePending = String.Equals(transaction.DraftReleaseStatus,
                "pending", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(transaction.DraftReleaseStatus, "release_pending",
                    StringComparison.OrdinalIgnoreCase);
            // A quality-passed production with unfinished draft housekeeping is
            // not terminal for the transaction: keep the release phase
            // recoverable so a later Safe Continue can restore the user's
            // original draft root.  Only clear the primary error and enter the
            // terminal phase after the release fact is committed.
            if (!releasePending) {
                transaction.Phase = "completed";
                transaction.PrimaryErrorCode = "";
                transaction.PrimaryErrorDiagnostic = "";
                transaction.PrimaryErrorAttemptId = "";
                transaction.FailureClassification = "";
            } else if (PhaseRank(transaction.Phase) < PhaseRank("release_pending")) {
                transaction.Phase = "release_pending";
                transaction.NextAction = "restore_original_draft_root";
            }
            SyncTask(task, transaction);
            Save(transaction);
        }

        public CapCutDraftTransaction Get(VideoTask task) { return store.Load(task); }

        private void SyncTask(VideoTask task, CapCutDraftTransaction transaction)
        {
            if (task == null || transaction == null) return;
            task.DraftTransactionId = transaction.TransactionId;
            task.DraftTransactionPhase = transaction.Phase ?? "waiting";
            task.DraftTransactionErrorCode = transaction.PrimaryErrorCode ?? "";
            task.DraftTransactionFailureClassification = transaction.FailureClassification ?? "";
            task.DraftTransactionUpdatedAt = transaction.UpdatedAtUtc ?? DateTime.UtcNow.ToString("o");
            task.ImportPhase = transaction.ImportPhase ?? "import_waiting";
            task.ImportCommandId = transaction.ImportCommandId ?? "";
            task.ImportPath = transaction.ImportPath ?? "";
        }

        private void Save(CapCutDraftTransaction transaction)
        {
            try { store.Save(transaction); }
            catch (Exception ex) {
                if (logger != null) logger.Warn("CapCut事务状态写入失败（不覆盖生产错误）: " + ex.Message);
            }
        }

        private void RotateAttemptErrorIfNeeded(CapCutDraftTransaction transaction,
            string attemptId)
        {
            if (transaction == null || String.IsNullOrWhiteSpace(attemptId) ||
                String.IsNullOrWhiteSpace(transaction.PrimaryErrorCode) ||
                String.Equals(transaction.PrimaryErrorAttemptId ?? "", attemptId,
                    StringComparison.OrdinalIgnoreCase)) return;
            if (transaction.ErrorHistory == null)
                transaction.ErrorHistory = new List<CapCutTransactionErrorRecord>();
            transaction.ErrorHistory.Add(new CapCutTransactionErrorRecord {
                AttemptId = transaction.PrimaryErrorAttemptId ?? transaction.AttemptId ?? "",
                ErrorCode = transaction.PrimaryErrorCode ?? "",
                Diagnostic = transaction.PrimaryErrorDiagnostic ?? "",
                RecordedAtUtc = DateTime.UtcNow.ToString("o")
            });
            while (transaction.ErrorHistory.Count > 20)
                transaction.ErrorHistory.RemoveAt(0);
            transaction.PrimaryErrorCode = "";
            transaction.PrimaryErrorDiagnostic = "";
            transaction.PrimaryErrorAttemptId = attemptId;
            transaction.FailureClassification = "";
        }

        private static bool CanAdvancePhase(string current, string requested)
        {
            if (String.IsNullOrWhiteSpace(current)) return true;
            if (String.Equals(current, requested, StringComparison.OrdinalIgnoreCase)) return true;
            int currentRank = PhaseRank(current);
            int requestedRank = PhaseRank(requested);
            // A transaction may move into a release/terminal state even when
            // the task state was reset to Waiting after cancellation.  It must
            // never move back to an earlier media stage.
            return requestedRank >= currentRank;
        }

        private static int PhaseRank(string phase)
        {
            string value = (phase ?? "").ToLowerInvariant();
            value = value.Replace(":intent", "").Replace(":committed", "")
                .Replace(":completed", "");
            if (value.Contains("waiting")) return 0;
            // Check release before lease: the word "release" contains the
            // substring "lease", and must rank as post-production recovery,
            // not as an early lease-acquisition phase.
            if (value.Contains("release") || value.Contains("restore")) return 100;
            if (value.Contains("lease") || value.Contains("acquir")) return 10;
            if (value.Contains("launch") || value.Contains("editor")) return 20;
            if (value.Contains("draft_creation") || value.Contains("creation") ||
                value.Contains("creating")) return 30;
            if (value.Contains("video") || value.Contains("importing") || value.Contains("placing")) return 40;
            if (value.Contains("audio")) return 50;
            if (value.Contains("sample") || value.Contains("timeline")) return 60;
            if (value.Contains("caption")) return 70;
            if (value.Contains("style")) return 80;
            if (value.Contains("export") || value.Contains("quality")) return 90;
            if (value.Contains("completed")) return 110;
            return 5;
        }

        private static string NextActionForPhase(string phase)
        {
            string value = phase ?? "";
            if (value.IndexOf("release", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("restore", StringComparison.OrdinalIgnoreCase) >= 0)
                return "restore_original_draft_root";
            if (value.IndexOf("export", StringComparison.OrdinalIgnoreCase) >= 0)
                return "verify_export";
            if (value.IndexOf("caption", StringComparison.OrdinalIgnoreCase) >= 0)
                return "save_captions";
            if (value.IndexOf("sample", StringComparison.OrdinalIgnoreCase) >= 0)
                return "prepare_or_append_sample";
            if (value.IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0)
                return "import_or_verify_audio";
            if (value.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("importing", StringComparison.OrdinalIgnoreCase) >= 0)
                return "import_or_verify_video";
            if (value.IndexOf("draft", StringComparison.OrdinalIgnoreCase) >= 0)
                return "create_or_recover_draft";
            return "recover_transaction";
        }
    }

    public sealed class CapCutDraftMigrationItem
    {
        public string Category { get; set; }
        public string StableTaskArchiveKey { get; set; }
        public string TaskId { get; set; }
        public string Diagnostic { get; set; }
    }

    public sealed class CapCutDraftMigrationReport
    {
        public List<CapCutDraftMigrationItem> Items { get; private set; }
        public DateTime GeneratedAtUtc { get; private set; }

        public CapCutDraftMigrationReport()
        {
            Items = new List<CapCutDraftMigrationItem>();
            GeneratedAtUtc = DateTime.UtcNow;
        }
    }

    // Read-only migration classifier used before a rollout.  It deliberately
    // performs no deletion or move: only a transaction with a provable
    // identity and a verified production artifact is eligible for automatic
    // reconciliation. Unknown content remains blocked for manual review.
    public sealed class CapCutDraftTransactionMigrator
    {
        private readonly CapCutDraftTransactionCoordinator coordinator;

        public CapCutDraftTransactionMigrator(AppLogger logger)
        {
            coordinator = new CapCutDraftTransactionCoordinator(logger);
        }

        public CapCutDraftMigrationReport Scan(IEnumerable<VideoTask> tasks)
        {
            var report = new CapCutDraftMigrationReport();
            foreach (VideoTask task in (tasks ?? new VideoTask[0]).Where(item => item != null)) {
                CapCutTaskIdentity identity = CapCutTaskIdentity.FromTask(task);
                CapCutDraftTransaction transaction = coordinator.Get(task);
                string category;
                string diagnostic;
                if (task.GetState() == TaskState.Completed &&
                    String.Equals(task.ProductionStatus ?? "", "completed", StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(task.QualityStatus ?? "", "passed", StringComparison.OrdinalIgnoreCase) &&
                    !String.IsNullOrWhiteSpace(task.OutputPath) && File.Exists(task.OutputPath)) {
                    category = "completed_output_reconcile";
                    diagnostic = "verified quality-passed output";
                } else if (transaction != null && transaction.DraftCreationCheckpoint != null) {
                    category = "verified_checkpoint_rebind";
                    diagnostic = transaction.DraftCreationCheckpoint.CreationPhase ?? "checkpoint present";
                } else if (task.GetState() == TaskState.Waiting) {
                    category = "clean_waiting";
                    diagnostic = "no durable CapCut transaction";
                } else {
                    category = "unknown_content_blocked";
                    diagnostic = "task needs explicit transaction evidence";
                }
                report.Items.Add(new CapCutDraftMigrationItem {
                    Category = category,
                    StableTaskArchiveKey = identity.StableTaskArchiveKey,
                    TaskId = task.Id ?? "",
                    Diagnostic = diagnostic
                });
            }
            return report;
        }
    }
}

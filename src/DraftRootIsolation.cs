using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CapCutBatchClient
{
    public sealed class DraftRootLeaseRecord
    {
        public string SchemaVersion { get; set; }
        public string LeaseId { get; set; }
        public string TaskIdentity { get; set; }
        // TaskIdentity is attempt-scoped and intentionally changes on every
        // safe-continue.  StableTaskArchiveKey is the durable production
        // identity used to find the same archived draft across attempts.
        public string LeaseAttemptIdentity { get; set; }
        public string StableTaskArchiveKey { get; set; }
        public string BatchId { get; set; }
        public string CanonicalTaskId { get; set; }
        public string ProductionDate { get; set; }
        public string ManifestId { get; set; }
        public string AttemptId { get; set; }
        public string LocalTaskId { get; set; }
        public string OriginalDraftRoot { get; set; }
        public string TaskDraftRoot { get; set; }
        public string ActiveDraftRoot { get; set; }
        public string ArchiveDraftRoot { get; set; }
        // Version 3 stores the durable archive below a short, volume-local
        // root.  The old ArchiveDraftRoot value is retained in
        // LegacyArchiveDraftRoot when a lease is upgraded so historical
        // checkpoints remain recoverable without being used as a new target.
        public string ArchiveStorageVersion { get; set; }
        public string ShortArchiveRoot { get; set; }
        public string ArchiveStageRoot { get; set; }
        public string ArchivePreviousRoot { get; set; }
        public string DiagnosticRoot { get; set; }
        public string LegacyArchiveDraftRoot { get; set; }
        public string ArchiveState { get; set; }
        public string ArchiveManifestHash { get; set; }
        public string ArchiveVerifiedAt { get; set; }
        public string StorageMode { get; set; }
        public string ProtectedOriginalDraftRoot { get; set; }
        public string JunctionMarkerName { get; set; }
        public bool JunctionMounted { get; set; }
        public string GlobalSettingPath { get; set; }
        public string GlobalSettingBackupPath { get; set; }
        public string Stage { get; set; }
        // Durable lifecycle fields.  These are intentionally independent from
        // TaskIdentity: a safe-continue creates a new attempt but continues
        // the same stable task lease/transaction.
        public string TransactionId { get; set; }
        public string StopState { get; set; }
        public string NextAction { get; set; }
        public string LastKnownPhase { get; set; }
        public string OwnerProcessStartTimeUtc { get; set; }
        // True only after this lease has actually taken ownership of the
        // fixed CapCut directory.  A lease may fail before that point while
        // resolving a checkpoint; cleanup must then leave the user's original
        // directory untouched.
        public bool ActiveRootOwnershipCommitted { get; set; }
        public string ActiveRootOwnershipCommittedAt { get; set; }
        public int OwnerPid { get; set; }
        public string AcquiredAt { get; set; }
        public string HeartbeatAt { get; set; }
        public string ReleasedAt { get; set; }
        public string RecoveryCode { get; set; }
    }

    public enum DraftRootOwnershipStatus
    {
        ValidStableIdentity,
        ValidAttemptIdentity,
        ValidLegacyIdentity,
        LeaseMissing,
        ActiveRootMissing,
        ActiveRootReparsePoint,
        MarkerMissing,
        MarkerUnreadable,
        IdentityMismatch,
        ActiveRootPathMismatch
    }

    // All callers that need to prove ownership of the CapCut active root use
    // this result.  Keeping the marker semantics here prevents creation and
    // import gates from drifting apart when the durable identity changes.
    public sealed class DraftRootOwnershipValidationResult
    {
        public DraftRootOwnershipStatus Status { get; set; }
        public string ErrorCode { get; set; }
        public string MarkerKind { get; set; }
        public string ExpectedIdentityHash { get; set; }
        public string ActualIdentityHash { get; set; }
        public string ActiveDraftRoot { get; set; }
        public string Diagnostic { get; set; }
        public bool IsValid
        {
            get { return Status == DraftRootOwnershipStatus.ValidStableIdentity ||
                Status == DraftRootOwnershipStatus.ValidAttemptIdentity ||
                Status == DraftRootOwnershipStatus.ValidLegacyIdentity; }
        }
    }

    // Describes the exact filesystem state produced by the current Acquire
    // transaction. Validation consumes this result instead of inferring
    // recovery solely from checkpoints that predated the acquisition.
    public sealed class DraftRootPreparationResult
    {
        public string Mode { get; set; }
        public string DraftId { get; set; }
        public string DraftName { get; set; }
        public string ActiveDraftPath { get; set; }
        public string ArchiveDraftPath { get; set; }
        public string StableTaskArchiveKey { get; set; }
        public string SourceVideoIdentity { get; set; }

        public bool HasPreparedDraft
        {
            get { return String.Equals(Mode, "restored_checkpoint",
                    StringComparison.OrdinalIgnoreCase) ||
                String.Equals(Mode, "archived_candidate",
                    StringComparison.OrdinalIgnoreCase) ||
                String.Equals(Mode, "empty_bootstrap",
                    StringComparison.OrdinalIgnoreCase); }
        }
    }

    // Pure filesystem/configuration component. It never launches or kills
    // CapCut. The caller must prove CapCut is fully stopped before Acquire or
    // Release. This makes crash recovery explicit instead of racing CapCut's
    // own configuration writer.
    public sealed class TaskDraftRootLeaseManager
    {
        private const string SettingKey = "currentCustomDraftPath=";
        private readonly AppConfig config;
        private readonly AppLogger logger;
        private readonly string leaseFile;
        private readonly string rootsDirectory;
        private readonly string shortArchiveRootsDirectory;
        private readonly bool shortArchiveRootWasOverridden;
        private readonly string settingsPathOverride;
        private readonly Func<int, bool> leaseOwnerIsRunning;
        private readonly Func<bool> capCutIsRunning;
        private DraftRootLeaseRecord current;
        private VideoTask currentTask;
        private DraftRootPreparationResult currentPreparation;

        public TaskDraftRootLeaseManager(AppConfig config, AppLogger logger)
            : this(config, logger, AppPaths.DraftRootLeaseFile, AppPaths.TaskDraftRootsDirectory)
        {
        }

        internal TaskDraftRootLeaseManager(AppConfig config, AppLogger logger,
            string leaseFile, string rootsDirectory,
            string settingsPathOverride = null,
            Func<int, bool> leaseOwnerIsRunning = null,
            Func<bool> capCutIsRunning = null,
            string shortArchiveRootsOverride = null)
        {
            this.config = config;
            this.logger = logger;
            this.leaseFile = Path.GetFullPath(leaseFile);
            this.rootsDirectory = Path.GetFullPath(rootsDirectory);
            this.shortArchiveRootWasOverridden = !String.IsNullOrWhiteSpace(
                shortArchiveRootsOverride);
            this.shortArchiveRootsDirectory = !String.IsNullOrWhiteSpace(
                shortArchiveRootsOverride)
                ? Path.GetFullPath(shortArchiveRootsOverride)
                : (String.IsNullOrWhiteSpace(settingsPathOverride)
                    ? AppPaths.ShortDraftArchiveDirectory
                    : Path.Combine(Path.GetPathRoot(this.rootsDirectory),
                        ".capcut-drafts"));
            this.settingsPathOverride =
                String.IsNullOrWhiteSpace(settingsPathOverride) ? "" :
                Path.GetFullPath(settingsPathOverride);
            this.leaseOwnerIsRunning = leaseOwnerIsRunning ?? IsLeaseOwnerRunning;
            this.capCutIsRunning = capCutIsRunning ?? IsCapCutRunning;
        }

        public bool HasLease { get { return current != null; } }
        public DraftRootLeaseRecord Current { get { return current; } }
        public DraftRootPreparationResult CurrentPreparation
        {
            get { return currentPreparation; }
        }

        public DraftRootOwnershipValidationResult ValidateActiveRootOwnership(
            DraftRootLeaseRecord lease)
        {
            var result = new DraftRootOwnershipValidationResult {
                Status = DraftRootOwnershipStatus.LeaseMissing,
                ErrorCode = "CAPCUT_ACTIVE_DRAFT_ROOT_PROBE_FAILED",
                MarkerKind = "",
                ExpectedIdentityHash = ShortHash(
                    lease == null ? "" : MarkerValue(lease), 16),
                ActualIdentityHash = "",
                ActiveDraftRoot = lease == null ? "" : (lease.ActiveDraftRoot ?? ""),
                Diagnostic = "活动草稿租约不存在"
            };
            if (lease == null) return result;

            string active = lease.ActiveDraftRoot ?? "";
            if (String.IsNullOrWhiteSpace(active)) {
                result.Status = DraftRootOwnershipStatus.ActiveRootMissing;
                result.Diagnostic = "活动草稿目录路径为空";
                return result;
            }
            try {
                active = Path.GetFullPath(active);
                result.ActiveDraftRoot = active;
                if (!Directory.Exists(active)) {
                    result.Status = DraftRootOwnershipStatus.ActiveRootMissing;
                    result.Diagnostic = "活动草稿目录不存在: " + active;
                    return result;
                }
                if ((File.GetAttributes(active) & FileAttributes.ReparsePoint) != 0) {
                    result.Status = DraftRootOwnershipStatus.ActiveRootReparsePoint;
                    result.ErrorCode = "CAPCUT_ACTIVE_DRAFT_ROOT_REPARSE_POINT_BLOCKED";
                    result.Diagnostic = "活动草稿目录不能是联接或符号链接: " + active;
                    return result;
                }
                if (!String.IsNullOrWhiteSpace(lease.TaskDraftRoot) &&
                    !SamePath(active, lease.TaskDraftRoot)) {
                    result.Status = DraftRootOwnershipStatus.ActiveRootPathMismatch;
                    result.ErrorCode = "CAPCUT_ACTIVE_DRAFT_ROOT_PATH_MISMATCH";
                    result.Diagnostic = "活动目录与租约目录不一致";
                    return result;
                }
            } catch (Exception ex) {
                result.Status = DraftRootOwnershipStatus.ActiveRootMissing;
                result.Diagnostic = "活动草稿目录无法读取: " + ex.Message;
                return result;
            }

            string markerName = lease.JunctionMarkerName ?? "";
            if (String.IsNullOrWhiteSpace(markerName)) {
                result.Status = DraftRootOwnershipStatus.MarkerMissing;
                result.ErrorCode = "CAPCUT_ACTIVE_DRAFT_ROOT_MARKER_MISSING";
                result.Diagnostic = "租约未定义身份标记文件名";
                return result;
            }
            string marker = Path.Combine(active, markerName);
            if (!File.Exists(marker)) {
                result.Status = DraftRootOwnershipStatus.MarkerMissing;
                result.ErrorCode = "CAPCUT_ACTIVE_DRAFT_ROOT_MARKER_MISSING";
                result.Diagnostic = "活动目录缺少身份标记";
                return result;
            }

            string value;
            try {
                value = File.ReadAllText(marker, Encoding.UTF8).Trim();
            } catch (Exception ex) {
                result.Status = DraftRootOwnershipStatus.MarkerUnreadable;
                result.ErrorCode = "CAPCUT_ACTIVE_DRAFT_ROOT_MARKER_UNREADABLE";
                result.Diagnostic = "身份标记无法读取: " + ex.Message;
                return result;
            }
            result.ActualIdentityHash = ShortHash(value, 16);
            if (String.Equals(value, lease.StableTaskArchiveKey ?? "",
                    StringComparison.Ordinal)) {
                result.Status = DraftRootOwnershipStatus.ValidStableIdentity;
                result.MarkerKind = "stable";
                result.ErrorCode = "";
                result.Diagnostic = "稳定任务身份标记有效";
                return result;
            }
            if (String.Equals(value, lease.TaskIdentity ?? "",
                    StringComparison.Ordinal)) {
                result.Status = DraftRootOwnershipStatus.ValidAttemptIdentity;
                result.MarkerKind = "attempt";
                result.ErrorCode = "";
                result.Diagnostic = "本轮尝试身份标记有效";
                return result;
            }
            if (MarkerValueMatches(value, lease)) {
                result.Status = DraftRootOwnershipStatus.ValidLegacyIdentity;
                result.MarkerKind = "legacy";
                result.ErrorCode = "";
                result.Diagnostic = "同一任务的旧版身份标记有效";
                return result;
            }
            result.Status = DraftRootOwnershipStatus.IdentityMismatch;
            result.ErrorCode = "CAPCUT_ACTIVE_DRAFT_ROOT_IDENTITY_MISMATCH";
            result.Diagnostic = "活动目录身份标记不属于当前任务";
            return result;
        }

        public bool IsEnabled
        {
            get { return config.EnableTaskScopedDraftRoot || RuntimeModes.IsolatedLabRuntime; }
        }

        private bool UseLocalActiveDirectory
        {
            // Test fixtures intentionally use an isolated settings file and
            // exercise the legacy junction contract.  Production/default
            // construction has no override and uses the C: local directory
            // mode introduced for CapCut 9.2.
            get { return config.UseLocalActiveDraftDirectory &&
                (String.IsNullOrWhiteSpace(settingsPathOverride) ||
                    shortArchiveRootWasOverridden); }
        }

        private static bool IsLocalStorage(DraftRootLeaseRecord lease)
        {
            return lease != null && String.Equals(lease.StorageMode,
                "local_active_directory_v2", StringComparison.OrdinalIgnoreCase);
        }

        public DraftRootLeaseRecord Acquire(VideoTask task, CancellationToken token)
        {
            if (!IsEnabled) return null;
            if (task == null) throw new ArgumentNullException("task");
            token.ThrowIfCancellationRequested();
            currentPreparation = null;

            Directory.CreateDirectory(Path.GetDirectoryName(leaseFile));
            Directory.CreateDirectory(rootsDirectory);
            string identity = BuildTaskIdentity(task);
            string stableArchiveKey = BuildStableTaskArchiveKey(task);
            DraftRootLeaseRecord existing = LoadLease(leaseFile);
            if (existing != null && String.IsNullOrWhiteSpace(existing.ReleasedAt)) {
                // A lease belongs to a stable production task, not to a
                // particular manifest/attempt.  The previous implementation
                // compared TaskIdentity first and consequently rejected a
                // safe-continue of the same task as "another task".
                if (!LeaseBelongsToStableTask(existing, task)) {
                    // A stale lease that is provably empty is safe to retire
                    // even when a new task is waiting.  Any real/unknown
                    // content remains protected and blocks the new task.
                    if (!leaseOwnerIsRunning(existing.OwnerPid) &&
                        !capCutIsRunning()) {
                        string emptyReason;
                        if (TryRecoverStaleEmptyLease(existing, token,
                                out emptyReason)) {
                            existing = null;
                        }
                    }
                    if (existing != null) {
                        string blockedCode = leaseOwnerIsRunning(existing.OwnerPid)
                            ? "draft_root_lease_conflict: "
                            : "CAPCUT_UNKNOWN_STALE_DRAFT_BLOCKED: ";
                        throw new InvalidOperationException(blockedCode +
                            "another task still owns the CapCut draft directory");
                    }
                }
                if (existing != null && !String.Equals(existing.TaskIdentity, identity, StringComparison.Ordinal)) {
                    string reason;
                    if (TryRecoverStaleEmptyLease(existing, token, out reason)) {
                        existing = null;
                    } else {
                        string checkpointReason;
                        if (!CanReclaimForNewAttempt(existing, task,
                                out checkpointReason)) {
                            string code = String.Equals(reason,
                                "unknown_stale_draft", StringComparison.Ordinal)
                                ? "CAPCUT_UNKNOWN_STALE_DRAFT_BLOCKED: "
                                : "CAPCUT_DRAFT_LEASE_RECOVERY_BLOCKED: ";
                            throw new InvalidOperationException(
                                code + "same stable task has an unrecovered previous attempt (" +
                                (String.IsNullOrWhiteSpace(checkpointReason)
                                    ? reason : checkpointReason) + ")");
                        }
                        string previousAttempt = existing.AttemptId ?? "";
                        existing.TaskIdentity = identity;
                        existing.LeaseAttemptIdentity = identity;
                        existing.StableTaskArchiveKey = stableArchiveKey;
                        existing.BatchId = task.HandoffBatchId ?? "";
                        existing.CanonicalTaskId = StableCanonicalTaskId(task);
                        existing.ProductionDate = task.ProductionDate ?? "";
                        existing.ManifestId = task.HandoffManifestId ?? "";
                        existing.AttemptId = task.HandoffAttemptId ?? "";
                        existing.LocalTaskId = task.Id ?? "";
                        existing.Stage = "reclaiming_same_task_attempt";
                        existing.LastKnownPhase = existing.Stage;
                        existing.StopState = "recovered";
                        existing.NextAction = "resume_transaction";
                        existing.OwnerPid = Process.GetCurrentProcess().Id;
                        existing.OwnerProcessStartTimeUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o");
                        existing.HeartbeatAt = DateTime.Now.ToString("o");
                        SaveLease(existing);
                        logger.Warn("Reclaimed stale task-scoped CapCut draft lease for the same production task: " +
                            existing.LeaseId + "; previousAttempt=" + previousAttempt +
                            "; currentAttempt=" + existing.AttemptId);
                    }
                }
                if (existing != null && existing.OwnerPid != Process.GetCurrentProcess().Id &&
                    leaseOwnerIsRunning(existing.OwnerPid)) {
                    throw new InvalidOperationException(
                        "CAPCUT_DRAFT_LEASE_OWNER_RUNNING: the previous CapCut client still owns this task lease");
                }
                if (existing != null && existing.OwnerPid != Process.GetCurrentProcess().Id &&
                    capCutIsRunning()) {
                    throw new InvalidOperationException(
                        "CAPCUT_DRAFT_LEASE_CAPCUT_RUNNING: CapCut is still running for the previous task lease");
                }
                if (existing == null) {
                    // The old empty lease was safely released. Continue below
                    // and acquire a completely new task identity and root.
                } else {
                current = existing;
                if (String.IsNullOrWhiteSpace(existing.StableTaskArchiveKey))
                    existing.StableTaskArchiveKey = stableArchiveKey;
                if (String.IsNullOrWhiteSpace(existing.LeaseAttemptIdentity))
                    existing.LeaseAttemptIdentity = existing.TaskIdentity ?? "";
                ValidateLeasePaths(existing);
                currentTask = task;
                if (IsLocalStorage(existing)) {
                    currentPreparation = PrepareLocalActiveDirectory(
                        task, existing, token);
                    existing.OwnerPid = Process.GetCurrentProcess().Id;
                    existing.OwnerProcessStartTimeUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o");
                    existing.Stage = "recovered_local_active_directory";
                    existing.LastKnownPhase = existing.Stage;
                    existing.NextAction = "resume_transaction";
                    existing.HeartbeatAt = DateTime.Now.ToString("o");
                    SaveLease(existing);
                    ValidateTaskRoot(task, existing, true);
                    logger.Info("Recovered local CapCut draft directory: " + existing.LeaseId);
                    return existing;
                }
                // A previous CapCut launch may have appended a second index
                // entry for the junction-visible path. Rebuild the task index
                // from the trusted checkpoint before remounting/reusing it.
                PrepareTaskRoot(task, existing, token);
                EnsureDraftRootJunction(existing);
                existing.OwnerPid = System.Diagnostics.Process.GetCurrentProcess().Id;
                existing.Stage = "recovered";
                existing.HeartbeatAt = DateTime.Now.ToString("o");
                SaveLease(existing);
                RebindCheckpointToTaskRoot(task, existing);
                ValidateTaskRoot(task, existing, true);
                logger.Info("Recovered task-scoped CapCut draft lease: " + existing.LeaseId);
                return existing;
                }
            }

            string settingsPath = String.IsNullOrWhiteSpace(
                settingsPathOverride)
                ? ResolveGlobalSettingPath(config) : settingsPathOverride;
            if (!File.Exists(settingsPath))
                throw new FileNotFoundException(
                    "draft_root_setting_missing: CapCut globalSetting was not found", settingsPath);
            string originalRoot = ReadCustomDraftPath(settingsPath);
            if (String.IsNullOrWhiteSpace(originalRoot)) originalRoot = ResolveDefaultDraftRoot(config);
            originalRoot = Path.GetFullPath(originalRoot);

            // The archive directory is keyed by the stable production task,
            // never by a manifest/attempt.  A retry therefore finds the same
            // persisted draft instead of creating a second empty archive.
            string shortId = ShortHash(stableArchiveKey, 16);
            string attemptShort = ShortHash(identity, 8);
            string taskRoot = Path.Combine(rootsDirectory, "task-" + shortId);
            string shortArchiveRoot = Path.Combine(shortArchiveRootsDirectory,
                "t-" + shortId);
            string protectedOriginalRoot = originalRoot.TrimEnd('\\') +
                ".codex-protected-" + shortId;
            string backup = Path.Combine(Path.GetDirectoryName(leaseFile),
                "globalSetting.before-draft-lease." + shortId + ".bak");
            if (!File.Exists(backup)) File.Copy(settingsPath, backup, false);

            var lease = new DraftRootLeaseRecord {
                SchemaVersion = UseLocalActiveDirectory ? "5" : "3",
                LeaseId = "draft-lease-" + shortId + "-" + attemptShort,
                TaskIdentity = identity,
                LeaseAttemptIdentity = identity,
                StableTaskArchiveKey = stableArchiveKey,
                BatchId = task.HandoffBatchId ?? "",
                CanonicalTaskId = StableCanonicalTaskId(task),
                ProductionDate = task.ProductionDate ?? "",
                ManifestId = task.HandoffManifestId ?? "",
                AttemptId = task.HandoffAttemptId ?? "",
                LocalTaskId = task.Id ?? "",
                OriginalDraftRoot = originalRoot,
                TaskDraftRoot = UseLocalActiveDirectory
                    ? Path.GetFullPath(originalRoot)
                    : Path.GetFullPath(taskRoot),
                ActiveDraftRoot = UseLocalActiveDirectory
                    ? Path.GetFullPath(originalRoot) : "",
                ArchiveDraftRoot = UseLocalActiveDirectory
                    ? Path.Combine(shortArchiveRoot, "live") : "",
                ArchiveStorageVersion = UseLocalActiveDirectory ? "3" : "",
                ShortArchiveRoot = UseLocalActiveDirectory
                    ? Path.GetFullPath(shortArchiveRoot) : "",
                ArchiveStageRoot = UseLocalActiveDirectory
                    ? Path.Combine(shortArchiveRoot, "stg") : "",
                ArchivePreviousRoot = UseLocalActiveDirectory
                    ? Path.Combine(shortArchiveRoot, "prev") : "",
                DiagnosticRoot = UseLocalActiveDirectory
                    ? Path.Combine(shortArchiveRoot, "evidence") : "",
                LegacyArchiveDraftRoot = "",
                ArchiveState = "pending",
                ArchiveManifestHash = "",
                ArchiveVerifiedAt = "",
                StorageMode = UseLocalActiveDirectory
                    ? "local_active_directory_v2" : "junction_v3",
                ProtectedOriginalDraftRoot = Path.GetFullPath(protectedOriginalRoot),
                // Keep the marker name stable across attempts.  Its content
                // is validated against the stable key and current attempt.
                JunctionMarkerName = ".codex-draft-root-" + shortId + ".marker",
                JunctionMounted = false,
                GlobalSettingPath = Path.GetFullPath(settingsPath),
                GlobalSettingBackupPath = Path.GetFullPath(backup),
                Stage = "acquiring",
                TransactionId = "",
                StopState = "none",
                NextAction = "prepare_active_root",
                LastKnownPhase = "lease_preparing",
                OwnerProcessStartTimeUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o"),
                ActiveRootOwnershipCommitted = false,
                ActiveRootOwnershipCommittedAt = "",
                OwnerPid = System.Diagnostics.Process.GetCurrentProcess().Id,
                AcquiredAt = DateTime.Now.ToString("o"),
                HeartbeatAt = DateTime.Now.ToString("o"),
                ReleasedAt = ""
            };
            SaveLease(lease);
            current = lease;
            currentTask = task;

            if (IsLocalStorage(lease)) {
                currentPreparation = PrepareLocalActiveDirectory(
                    task, lease, token);
                ValidateTaskRoot(task, lease, false);
                lease.Stage = "active";
                lease.HeartbeatAt = DateTime.Now.ToString("o");
                SaveLease(lease);
                logger.Info("Acquired local CapCut draft directory: " + lease.LeaseId);
                return lease;
            }

            PrepareTaskRoot(task, lease, token);
            ValidateTaskRoot(task, lease, false);
            // CapCut 2026.7 can ignore a freshly written currentCustomDraftPath
            // and continue scanning the fixed com.lveditor.draft directory.
            // Keep the configured path stable and mount the task root at that
            // exact filesystem location instead.  This is the identity boundary
            // that CapCut itself cannot bypass.
            SetCustomDraftPath(lease.GlobalSettingPath, lease.OriginalDraftRoot);
            EnsureDraftRootJunction(lease);
            lease.Stage = "active";
            lease.HeartbeatAt = DateTime.Now.ToString("o");
            SaveLease(lease);
            logger.Info("Acquired task-scoped CapCut draft lease: " + lease.LeaseId);
            return lease;
        }

        private bool TryRecoverStaleEmptyLease(DraftRootLeaseRecord lease,
            CancellationToken token, out string reason)
        {
            reason = "identity_mismatch";
            if (lease == null) return false;
            if (current != null && String.Equals(current.LeaseId,
                    lease.LeaseId, StringComparison.Ordinal)) {
                reason = "lease_owner_active";
                return false;
            }
            reason = "previous_client_still_running";
            if (leaseOwnerIsRunning(lease.OwnerPid)) return false;
            reason = "capcut_still_running";
            if (capCutIsRunning()) return false;
            reason = "invalid_lease_paths";
            try { ValidateLeasePaths(lease); }
            catch { return false; }
            reason = "unknown_stale_draft";
            if (!TaskRootIsProvablyEmpty(lease)) return false;

            current = lease;
            // Keep the storage schema when archiving a stale lease.  Local
            // active-directory leases use schema 4; downgrading the record
            // here would make the archived history look like the legacy
            // junction mode even though Release() still used local storage.
            if (String.IsNullOrWhiteSpace(lease.SchemaVersion))
                lease.SchemaVersion = IsLocalStorage(lease) ? "4" : "3";
            lease.Stage = "recovering_stale_empty_lease";
            lease.RecoveryCode = "CAPCUT_STALE_EMPTY_LEASE_RECOVERED";
            lease.HeartbeatAt = DateTime.Now.ToString("o");
            SaveLease(lease);
            Release(token);
            logger.Warn("CAPCUT_STALE_EMPTY_LEASE_RECOVERED: restored the original CapCut draft directory and archived empty lease " +
                lease.LeaseId);
            reason = "stale_empty_lease_recovered";
            return true;
        }

        private static bool IsProvablyBlankDraftDirectory(string directory)
        {
            try {
                if (!LooksLikeDraftDirectory(directory)) return false;
                string content = Path.Combine(directory, "draft_content.json");
                if (!File.Exists(content)) {
                    string timelines = Path.Combine(directory, "Timelines");
                    if (!Directory.Exists(timelines)) return false;
                    content = Directory.GetDirectories(timelines)
                        .Select(path => Path.Combine(path, "draft_content.json"))
                        .FirstOrDefault(File.Exists);
                }
                if (String.IsNullOrWhiteSpace(content) || !File.Exists(content)) return false;
                Dictionary<string, object> value = ReadJsonObject(content);
                object tracks;
                if (value.TryGetValue("tracks", out tracks) &&
                    ToObjectArray(tracks).Length != 0) return false;
                object materials;
                if (value.TryGetValue("materials", out materials)) {
                    Dictionary<string, object> materialMap = materials as Dictionary<string, object>;
                    if (materialMap != null && materialMap.Values.Any(item =>
                        ToObjectArray(item).Length != 0)) return false;
                }
                object duration;
                if (value.TryGetValue("duration", out duration) &&
                    Convert.ToDouble(duration) > 0.001) return false;
                return true;
            } catch {
                return false;
            }
        }
        private bool TaskRootIsProvablyEmpty(DraftRootLeaseRecord lease)
        {
            try {
                if (IsLocalStorage(lease))
                    return TaskRootIsProvablyEmptyLocal(lease);
                if (!lease.JunctionMounted ||
                    String.IsNullOrWhiteSpace(lease.JunctionMarkerName) ||
                    !Directory.Exists(lease.TaskDraftRoot) ||
                    !Directory.Exists(lease.OriginalDraftRoot) ||
                    !IsReparsePoint(lease.OriginalDraftRoot)) return false;
                VerifyMountedJunction(lease);

                foreach (string directory in Directory.GetDirectories(
                        lease.TaskDraftRoot)) {
                    string name = Path.GetFileName(directory);
                    if (String.Equals(name, ".recycle_bin",
                            StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(name, "Cache",
                            StringComparison.OrdinalIgnoreCase)) continue;
                    // CapCut may create a bootstrap draft before the real task
                    // draft exists. Reclaim only a recognizable draft with no
                    // tracks, media, or duration; unknown content stays protected.
                    if (!IsProvablyBlankDraftDirectory(directory)) return false;
                }

                string indexPath = Path.Combine(lease.TaskDraftRoot,
                    "root_meta_info.json");
                if (!File.Exists(indexPath)) return false;
                Dictionary<string, object> root = ReadJsonObject(indexPath);
                object rawStores;
                object[] stores = root.TryGetValue("all_draft_store",
                    out rawStores) ? ToObjectArray(rawStores) : new object[0];
                // CapCut registers its bootstrap draft in the root index even
                // when the draft has no tracks, media or duration. Accept only
                // index entries that point to already-proven blank bootstrap
                // directories; any real or unknown entry keeps the lease safe.
                string[] blankDirectories = Directory.GetDirectories(
                    lease.TaskDraftRoot)
                    .Where(path => {
                        string name = Path.GetFileName(path);
                        return !String.Equals(name, ".recycle_bin",
                                StringComparison.OrdinalIgnoreCase) &&
                            !String.Equals(name, "Cache",
                                StringComparison.OrdinalIgnoreCase) &&
                            IsProvablyBlankDraftDirectory(path);
                    }).ToArray();
                if (stores.Length != blankDirectories.Length) return false;
                foreach (object storeValue in stores) {
                    Dictionary<string, object> store =
                        storeValue as Dictionary<string, object>;
                    string folder = store == null ? "" :
                        Text(store, "draft_fold_path");
                    string indexedName = String.IsNullOrWhiteSpace(folder)
                        ? "" : Path.GetFileName(folder.TrimEnd('\\', '/'));
                    if (String.IsNullOrWhiteSpace(indexedName) ||
                        !blankDirectories.Any(path => String.Equals(
                            Path.GetFileName(path), indexedName,
                            StringComparison.OrdinalIgnoreCase))) return false;
                }

                foreach (string file in Directory.GetFiles(
                        lease.TaskDraftRoot)) {
                    string name = Path.GetFileName(file);
                    if (String.Equals(name, "root_meta_info.json",
                            StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(name, lease.JunctionMarkerName,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    return false;
                }
                return true;
            } catch {
                return false;
            }
        }

        private bool CanReclaimForNewAttempt(DraftRootLeaseRecord lease,
            VideoTask task, out string reason)
        {
            reason = "identity_mismatch";
            if (lease == null || task == null || !LeaseBelongsToStableTask(lease, task))
                return false;
            reason = "previous_client_still_running";
            if (leaseOwnerIsRunning(lease.OwnerPid)) return false;
            reason = "capcut_still_running";
            if (capCutIsRunning()) return false;

            // A client can stop after a media operation but before the task
            // table/checkpoint is flushed.  Reclaiming the same stable lease
            // must therefore be based on filesystem facts as well as the
            // checkpoint.  A valid marker, a provably empty owned root, or a
            // durable archive is enough to resume the transaction; the next
            // preparation/validation step still performs the strict media
            // identity check before CapCut is launched.
            try {
                if (ValidateActiveRootOwnership(lease).IsValid) {
                    reason = "same_task_stale_attempt";
                    return true;
                }
                if (IsLocalStorage(lease) && TaskRootIsProvablyEmptyLocal(lease)) {
                    reason = "same_task_stale_empty_root";
                    return true;
                }
                if (!String.IsNullOrWhiteSpace(lease.ArchiveDraftRoot) &&
                    File.Exists(Path.Combine(lease.ArchiveDraftRoot,
                        "root_meta_info.json"))) {
                    reason = "same_task_durable_archive";
                    return true;
                }
            } catch { }

            DirectDraftCheckpoint checkpoint = RecoveryCheckpoint(task);
            if (checkpoint == null) return false;
            reason = "checkpoint_mismatch";
            string resolvedCheckpoint = ResolveCheckpointDraftSource(
                task, lease, checkpoint);
            bool legacyExactCheckpoint = Directory.Exists(
                    checkpoint.DraftDirectory ?? "") &&
                String.Equals(checkpoint.DraftRootLeaseId ?? "",
                    lease.LeaseId ?? "", StringComparison.Ordinal) &&
                SamePath(checkpoint.TaskDraftRoot, lease.TaskDraftRoot) &&
                IsInside(checkpoint.DraftDirectory, lease.TaskDraftRoot) &&
                CheckpointIndexContainsDraft(lease.TaskDraftRoot,
                    checkpoint.DraftId);
            if (String.IsNullOrWhiteSpace(checkpoint.DraftId) ||
                String.IsNullOrWhiteSpace(checkpoint.SourceVideoPath) ||
                !SamePath(checkpoint.SourceVideoPath, task.VideoPath) ||
                (String.IsNullOrWhiteSpace(resolvedCheckpoint) &&
                    !legacyExactCheckpoint))
                return false;
            reason = "same_task_stale_attempt";
            return true;
        }

        private static bool LeaseBelongsToStableTask(DraftRootLeaseRecord lease,
            VideoTask task)
        {
            if (lease == null || task == null) return false;
            string stable = BuildStableTaskArchiveKey(task);
            if (!String.IsNullOrWhiteSpace(lease.StableTaskArchiveKey)) {
                if (String.Equals(lease.StableTaskArchiveKey, stable,
                        StringComparison.Ordinal)) return true;
                // v3/v4 leases persisted the pre-v0.4 raw identity instead of
                // the shared t-<hash> key.  Accept that exact legacy form only
                // when all stable task fields still match; never fall back to
                // a directory name or a recent timestamp.
                string legacy = String.Join("|", new[] {
                    StableCanonicalTaskId(task),
                    task.ProductionDate ?? "",
                    task.Id ?? "",
                    NormalizeIdentityPath(task.VideoPath)
                });
                if (String.Equals(lease.StableTaskArchiveKey, legacy,
                        StringComparison.Ordinal)) return true;
                string legacyWithoutSource = String.Join("|", new[] {
                    StableCanonicalTaskId(task),
                    task.ProductionDate ?? "",
                    task.Id ?? "",
                    ""
                });
                return String.Equals(lease.StableTaskArchiveKey,
                    legacyWithoutSource, StringComparison.Ordinal);
            }
            string oldLocalTaskId = !String.IsNullOrWhiteSpace(lease.LocalTaskId)
                ? lease.LocalTaskId : LocalTaskIdFromIdentity(lease.TaskIdentity);
            return String.Equals(lease.CanonicalTaskId ?? "",
                    StableCanonicalTaskId(task), StringComparison.Ordinal) &&
                String.Equals(lease.ProductionDate ?? "",
                    task.ProductionDate ?? "", StringComparison.Ordinal) &&
                String.Equals(oldLocalTaskId, task.Id ?? "",
                    StringComparison.Ordinal);
        }

        private string ResolveCheckpointDraftSource(VideoTask task,
            DraftRootLeaseRecord lease, DirectDraftCheckpoint checkpoint)
        {
            if (task == null || lease == null || checkpoint == null) return "";
            var roots = new List<string>();
            AddCandidateRoot(roots, checkpoint.ArchiveDraftRoot);
            AddCandidateRoot(roots, lease.ArchiveDraftRoot);
            AddCandidateRoot(roots, lease.LegacyArchiveDraftRoot);

            // A v4 lease did not persist the stable key.  Its history still
            // records canonical/local task identity and the durable archive
            // root, which is sufficient to migrate the verified draft.
            string historyDirectory = Path.Combine(
                Path.GetDirectoryName(leaseFile), "draft-root-lease-history");
            if (Directory.Exists(historyDirectory)) {
                foreach (string historyFile in Directory.GetFiles(
                        historyDirectory, "*.json")) {
                    DraftRootLeaseRecord historical;
                    try { historical = LoadLease(historyFile); }
                    catch { continue; }
                    if (historical == null || !LeaseBelongsToStableTask(
                            historical, task)) continue;
                    AddCandidateRoot(roots, historical.ArchiveDraftRoot);
                    AddCandidateRoot(roots, historical.LegacyArchiveDraftRoot);
                    AddCandidateRoot(roots, historical.ShortArchiveRoot == null
                        ? "" : Path.Combine(historical.ShortArchiveRoot, "live"));
                }
            }
            AddCandidateRoot(roots, checkpoint.DraftDirectory);
            string relative = checkpoint.DraftRelativePath;
            if (String.IsNullOrWhiteSpace(relative) &&
                !String.IsNullOrWhiteSpace(checkpoint.DraftDirectory))
                relative = Path.GetFileName(checkpoint.DraftDirectory.TrimEnd(
                    '\\', '/'));
            foreach (string root in roots) {
                string candidate = root;
                if (!String.IsNullOrWhiteSpace(relative) &&
                    !String.Equals(Path.GetFileName(root.TrimEnd('\\', '/')),
                        relative, StringComparison.OrdinalIgnoreCase))
                    candidate = Path.Combine(root, relative);
                if (VerifyCheckpointCandidate(candidate, checkpoint, task))
                    return Path.GetFullPath(candidate);
            }
            return "";
        }

        private static void AddCandidateRoot(List<string> roots, string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return;
            string full;
            try { full = Path.GetFullPath(value); } catch { return; }
            if (!roots.Any(item => SamePath(item, full))) roots.Add(full);
        }

        private static bool VerifyCheckpointCandidate(string candidate,
            DirectDraftCheckpoint checkpoint, VideoTask task)
        {
            try {
                if (!Directory.Exists(candidate) ||
                    String.IsNullOrWhiteSpace(checkpoint.DraftId) ||
                    !File.Exists(Path.Combine(Path.GetDirectoryName(candidate),
                        "root_meta_info.json")) &&
                    !File.Exists(Path.Combine(candidate, "root_meta_info.json")))
                    return false;
                string draftDirectory = candidate;
                string root = Path.GetDirectoryName(candidate);
                if (File.Exists(Path.Combine(candidate, "root_meta_info.json")))
                    root = candidate;
                string idMeta = Path.Combine(draftDirectory, "draft_meta_info.json");
                if (!File.Exists(idMeta)) return false;
                if (!DraftIdentityMatchesCheckpoint(draftDirectory, checkpoint))
                    return false;
                return DraftContainsSource(draftDirectory,
                    task.VideoPath ?? checkpoint.SourceVideoPath);
            } catch { return false; }
        }

        private static string LocalTaskIdFromIdentity(string identity)
        {
            string[] parts = (identity ?? "").Split('|');
            return parts.Length == 0 ? "" : parts[parts.Length - 1];
        }

        private static bool IsLeaseOwnerRunning(int pid)
        {
            if (pid <= 0 || pid == Process.GetCurrentProcess().Id) return false;
            try {
                Process process = Process.GetProcessById(pid);
                if (process.HasExited) return false;
                return process.ProcessName.IndexOf("CapCutBatchClient",
                    StringComparison.OrdinalIgnoreCase) >= 0;
            } catch { return false; }
        }

        private static bool IsCapCutRunning()
        {
            foreach (string name in new[] { "CapCut", "JianyingPro" }) {
                try {
                    if (Process.GetProcessesByName(name).Any(process =>
                            !process.HasExited)) return true;
                } catch { }
            }
            return false;
        }

        public void Touch(string stage)
        {
            if (current == null) return;
            current.Stage = String.IsNullOrWhiteSpace(stage) ? current.Stage : stage;
            current.HeartbeatAt = DateTime.Now.ToString("o");
            SaveLease(current);
        }

        public void Release(CancellationToken token)
        {
            if (current == null) {
                DraftRootLeaseRecord existing = LoadLease(leaseFile);
                if (existing == null || !String.IsNullOrWhiteSpace(existing.ReleasedAt)) return;
                current = existing;
            }
            token.ThrowIfCancellationRequested();
            ValidateLeasePaths(current);
            current.StopState = String.IsNullOrWhiteSpace(current.StopState)
                ? "release_requested" : current.StopState;
            current.NextAction = "release_and_restore_original";
            current.LastKnownPhase = "release_pending";
            SaveLease(current);
            if (!File.Exists(current.GlobalSettingBackupPath))
                throw new FileNotFoundException(
                    "draft_root_restore_backup_missing: cannot restore CapCut draft directory",
                    current.GlobalSettingBackupPath);

            if (IsLocalStorage(current)) {
                DraftRootOwnershipValidationResult ownership =
                    ValidateActiveRootOwnership(current);
                if (!ownership.IsValid && IsUnownedAcquireRootSafe(current)) {
                    // Acquire failed before the ownership marker was
                    // committed (most commonly while rejecting a stale or
                    // wrong-source archive).  The previous implementation
                    // entered ArchiveLocalActiveDirectory here and replaced
                    // the real root cause with MARKER_MISSING, leaving the
                    // lease behind for the next project.  Finalize this
                    // pre-ownership lease without moving the user's folder.
                    if (Directory.Exists(current.ProtectedOriginalDraftRoot)) {
                        ReleaseEmptyLocalRoot(current);
                        RestoreLocalOriginalDraftRoot(current);
                    }
                    FinalizeUnownedAcquireLease(current);
                    return;
                }
                // A failed acquire may leave only the lease marker (or an
                // indexed zero-track bootstrap).  There is no production
                // draft to archive in that case; preserve the evidence and
                // restore the user's original root directly.
                if (TaskRootIsProvablyEmptyLocal(current))
                    ReleaseEmptyLocalRoot(current);
                else
                    ArchiveLocalActiveDirectory(current, token);
                RestoreLocalOriginalDraftRoot(current);
            } else {
                RestoreOriginalDraftRoot(current);
            }
            RestoreExactFile(current.GlobalSettingBackupPath, current.GlobalSettingPath);
            string restored = ReadCustomDraftPath(current.GlobalSettingPath);
            if (!SamePath(restored, current.OriginalDraftRoot))
                throw new InvalidDataException(
                    "draft_root_restore_mismatch: CapCut original draft directory was not restored");

            current.ArchiveState = "released";
            current.Stage = "released";
            current.LastKnownPhase = "original_restored";
            current.StopState = "released";
            current.NextAction = "none";
            current.JunctionMounted = false;
            current.ReleasedAt = DateTime.Now.ToString("o");
            current.HeartbeatAt = current.ReleasedAt;
            SaveLease(current);
            WriteLeaseHistory(current);
            try { File.Delete(leaseFile); } catch { }
            logger.Info("Restored original CapCut draft directory after task lease: " + current.LeaseId);
            current = null;
            currentTask = null;
            currentPreparation = null;
        }

        private void ReleaseEmptyLocalRoot(DraftRootLeaseRecord lease)
        {
            string active = Path.GetFullPath(lease.ActiveDraftRoot ??
                lease.OriginalDraftRoot ?? "");
            try {
                lease.ArchiveState = "acquire_failed_empty";
                lease.Stage = "acquire_failed_empty";
                lease.RecoveryCode = "CAPCUT_ACQUIRE_FAILED_EMPTY_ROOT_RECOVERED";
                SaveLease(lease);
                string evidenceRoot = Path.GetFullPath(String.IsNullOrWhiteSpace(
                    lease.DiagnosticRoot) ? Path.Combine(shortArchiveRootsDirectory,
                    "evidence") : lease.DiagnosticRoot);
                Directory.CreateDirectory(evidenceRoot);
                if (Directory.Exists(active)) {
                    string retained = UniqueShortChild(evidenceRoot, "empty-");
                    MoveDirectoryAcrossVolumesVerified(active, retained);
                }
                string staging = lease.ArchiveStageRoot ?? "";
                if (!String.IsNullOrWhiteSpace(staging) &&
                    Directory.Exists(staging)) {
                    string retainedStage = UniqueShortChild(evidenceRoot, "stg-");
                    MoveDirectoryAcrossVolumesVerified(staging, retainedStage);
                }
            } catch (Exception ex) {
                lease.ArchiveState = "original_restore_pending";
                lease.Stage = "original_restore_pending";
                lease.RecoveryCode = "CAPCUT_ACQUIRE_FAILED_UNKNOWN_ROOT_BLOCKED";
                try { SaveLease(lease); } catch { }
                throw new InvalidOperationException(
                    "CAPCUT_ACQUIRE_FAILED_UNKNOWN_ROOT_BLOCKED: unable to preserve empty-root evidence: " +
                    ex.Message, ex);
            }
        }

        public void ValidateTaskRoot(VideoTask task, DraftRootLeaseRecord lease, bool allowExisting)
        {
            if (lease == null) throw new ArgumentNullException("lease");
            ValidateLeasePaths(lease);
            if (IsLocalStorage(lease)) {
                ValidateLocalActiveDraftRoot(task, lease, allowExisting);
                return;
            }
            if (!Directory.Exists(lease.TaskDraftRoot))
                throw new DirectoryNotFoundException("draft_root_missing: " + lease.TaskDraftRoot);

            DirectDraftCheckpoint recoveryCheckpoint = RecoveryCheckpoint(task);
            if (recoveryCheckpoint != null)
                ReconcileCheckpointDrafts(task, lease);

            string[] draftDirectories = Directory.GetDirectories(lease.TaskDraftRoot)
                .Where(path => !String.Equals(Path.GetFileName(path), "Cache", StringComparison.OrdinalIgnoreCase))
                .Where(LooksLikeDraftDirectory)
                .ToArray();
            bool hasCheckpoint = recoveryCheckpoint != null;
            int expected = hasCheckpoint ? 1 : 0;
            if (draftDirectories.Length != expected) {
                if (allowExisting && !hasCheckpoint && draftDirectories.Length == 0) return;
                throw new InvalidDataException(
                    "draft_root_cardinality_mismatch: expected " + expected +
                    " registered draft(s), found " + draftDirectories.Length);
            }

            string indexPath = Path.Combine(lease.TaskDraftRoot, "root_meta_info.json");
            if (!File.Exists(indexPath)) {
                if (expected == 0) return;
                throw new FileNotFoundException("draft_root_index_missing", indexPath);
            }
            Dictionary<string, object> root = ReadJsonObject(indexPath);
            object rawStores;
            object[] stores = root.TryGetValue("all_draft_store", out rawStores)
                ? ToObjectArray(rawStores) : new object[0];
            if (stores.Length != expected)
                throw new InvalidDataException(
                    "draft_root_index_cardinality_mismatch: expected " + expected +
                    " index item(s), found " + stores.Length);
            if (expected == 1) {
                Dictionary<string, object> store = stores[0] as Dictionary<string, object>;
                string indexedId = Text(store, "draft_id");
                if (store == null || !String.Equals(indexedId,
                    recoveryCheckpoint.DraftId, StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException("draft_root_index_identity_mismatch");
                }
            }
        }

        private void ValidateLocalActiveDraftRoot(VideoTask task,
            DraftRootLeaseRecord lease, bool allowExisting)
        {
            string active = Path.GetFullPath(lease.ActiveDraftRoot ??
                lease.OriginalDraftRoot ?? "");
            if (!Directory.Exists(active))
                throw new DirectoryNotFoundException(
                    "CAPCUT_ACTIVE_DRAFT_ROOT_PROBE_FAILED: " + active);
            if ((File.GetAttributes(active) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "CAPCUT_ACTIVE_DRAFT_ROOT_PROBE_FAILED: activity root is a reparse point");
            DraftRootOwnershipValidationResult ownership =
                ValidateActiveRootOwnership(lease);
            if (!ownership.IsValid)
                throw new InvalidDataException(
                    ownership.ErrorCode + ": " + ownership.Diagnostic);
            DirectDraftCheckpoint checkpoint = RecoveryCheckpoint(task);
            string[] drafts = Directory.GetDirectories(active)
                .Where(path => !String.Equals(Path.GetFileName(path), "Cache",
                    StringComparison.OrdinalIgnoreCase))
                .Where(path => !String.Equals(Path.GetFileName(path), ".recycle_bin",
                    StringComparison.OrdinalIgnoreCase))
                .Where(LooksLikeDraftDirectory).ToArray();
            if (checkpoint == null) {
                if (CurrentPreparationBelongsTo(task, lease)) {
                    ValidatePreparedRecoveryDraft(active, drafts,
                        currentPreparation);
                    return;
                }
                if (drafts.Length != 0 && !allowExisting)
                    throw new InvalidDataException(
                        "CAPCUT_UNEXPECTED_ACTIVE_DRAFT_BLOCKED: unexpected draft exists before project creation");
                return;
            }
            if (drafts.Length != 1 || !SamePath(drafts[0],
                    checkpoint.DraftDirectory ?? ""))
                throw new InvalidDataException(
                    "CAPCUT_DRAFT_INDEX_NOT_STABLE: active root does not expose the checkpoint draft");
            string index = Path.Combine(active, "root_meta_info.json");
            if (!File.Exists(index))
                throw new FileNotFoundException(
                    "CAPCUT_DRAFT_INDEX_NOT_STABLE: root index missing", index);
        }

        private bool CurrentPreparationBelongsTo(VideoTask task,
            DraftRootLeaseRecord lease)
        {
            if (currentPreparation == null ||
                !currentPreparation.HasPreparedDraft || task == null ||
                lease == null) return false;
            if (!String.Equals(currentPreparation.StableTaskArchiveKey ?? "",
                    lease.StableTaskArchiveKey ?? "",
                    StringComparison.Ordinal)) return false;
            string expectedSource = String.IsNullOrWhiteSpace(task.VideoPath)
                ? "" : Path.GetFullPath(task.VideoPath);
            return SamePath(currentPreparation.SourceVideoIdentity,
                expectedSource);
        }

        private static void ValidatePreparedRecoveryDraft(string active,
            string[] drafts, DraftRootPreparationResult prepared)
        {
            if (prepared == null || drafts == null || drafts.Length != 1)
                throw new InvalidDataException(
                    "CAPCUT_RECOVERY_DRAFT_BINDING_LOST: prepared recovery draft is not singular");
            if (!SamePath(drafts[0], prepared.ActiveDraftPath ?? ""))
                throw new InvalidDataException(
                    "CAPCUT_RECOVERY_DRAFT_BINDING_LOST: active draft path differs from the prepared recovery result");
            string indexPath = Path.Combine(active, "root_meta_info.json");
            if (!File.Exists(indexPath))
                throw new FileNotFoundException(
                    "CAPCUT_RECOVERY_DRAFT_BINDING_LOST: prepared recovery root index is missing",
                    indexPath);
            Dictionary<string, object> root = ReadJsonObject(indexPath);
            object rawStores;
            object[] stores = root.TryGetValue("all_draft_store", out rawStores)
                ? ToObjectArray(rawStores) : new object[0];
            if (stores.Length != 1)
                throw new InvalidDataException(
                    "CAPCUT_RECOVERY_DRAFT_BINDING_LOST: prepared recovery index is not singular");
            Dictionary<string, object> store = stores[0]
                as Dictionary<string, object>;
            if (store == null || !String.Equals(Text(store, "draft_id"),
                    prepared.DraftId ?? "", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "CAPCUT_RECOVERY_DRAFT_IDENTITY_MISMATCH: prepared recovery draft id differs from the root index");
            string indexedPath = Text(store, "draft_fold_path");
            if (!String.IsNullOrWhiteSpace(indexedPath) &&
                !SamePath(indexedPath, prepared.ActiveDraftPath ?? ""))
                throw new InvalidDataException(
                    "CAPCUT_RECOVERY_DRAFT_IDENTITY_MISMATCH: prepared recovery path differs from the root index");
        }

        // CapCut may create one empty bootstrap draft before it creates the
        // source-bound project.  The safety invariant is therefore one and
        // only one draft matching the verified checkpoint and source video,
        // not one physical directory.  Extra folders are quarantined only
        // when their folder, internal id and index entry agree and their
        // material list proves that they never owned the current source.
        private void ReconcileCheckpointDrafts(VideoTask task,
            DraftRootLeaseRecord lease)
        {
            DirectDraftCheckpoint checkpoint = RecoveryCheckpoint(task);
            if (checkpoint == null) return;
            ReconcileCheckpointDrafts(checkpoint, task.VideoPath ?? "", lease);
        }

        // A bootstrap draft can be created after Acquire(), while CapCut is
        // creating the real source-bound draft in the same session.  Reconcile
        // again immediately before the homepage is reopened; waiting until the
        // next safe-continue would make every fresh task fail once.
        public void ReconcileActiveDraft(string draftId, string draftName,
            string draftDirectory, string expectedSource)
        {
            if (current == null)
                throw new InvalidOperationException(
                    "draft_root_reconcile_without_active_lease");
            if (String.IsNullOrWhiteSpace(draftId) ||
                String.IsNullOrWhiteSpace(draftDirectory) ||
                String.IsNullOrWhiteSpace(expectedSource))
                throw new InvalidDataException(
                    "draft_root_reconcile_identity_incomplete");
            var checkpoint = new DirectDraftCheckpoint {
                State = "style_applied",
                DraftId = draftId,
                DraftName = draftName ?? "",
                DraftDirectory = Path.GetFullPath(draftDirectory),
                TaskDraftRoot = current.TaskDraftRoot,
                DraftRootLeaseId = current.LeaseId,
                SourceVideoPath = expectedSource
            };
            ReconcileCheckpointDrafts(checkpoint, expectedSource, current);
        }

        private void ReconcileCheckpointDrafts(DirectDraftCheckpoint checkpoint,
            string expectedSource, DraftRootLeaseRecord lease)
        {
            string rootDirectory = Path.GetFullPath(lease.TaskDraftRoot);
            string indexPath = Path.Combine(rootDirectory,
                "root_meta_info.json");
            if (!File.Exists(indexPath))
                throw new FileNotFoundException("draft_root_index_missing",
                    indexPath);

            string[] directories = Directory.GetDirectories(rootDirectory)
                .Where(path => !String.Equals(Path.GetFileName(path),
                    "Cache", StringComparison.OrdinalIgnoreCase))
                .Where(path => !String.Equals(Path.GetFileName(path),
                    ".recycle_bin", StringComparison.OrdinalIgnoreCase))
                .Where(LooksLikeDraftDirectory).ToArray();
            if (directories.Length <= 1) return;

            Dictionary<string, object> index = ReadJsonObject(indexPath);
            object rawStores;
            object[] stores = index.TryGetValue("all_draft_store",
                out rawStores) ? ToObjectArray(rawStores) : new object[0];
            var storeList = stores.Select(value =>
                value as Dictionary<string, object>)
                .Where(value => value != null).ToArray();

            string target = directories.SingleOrDefault(path =>
                DraftIdentityMatchesCheckpoint(path, checkpoint));
            if (String.IsNullOrWhiteSpace(target))
                throw new InvalidDataException(
                    "CAPCUT_REOPEN_RECOVERABLE_FAILED: checkpoint draft directory was not uniquely identified");
            if (!DraftContainsSource(target, expectedSource))
                throw new InvalidDataException(
                    "CAPCUT_REOPEN_RECOVERABLE_FAILED: checkpoint draft does not contain the current source video");

            string[] sourceMatches = directories.Where(path =>
                DraftContainsSource(path, expectedSource)).ToArray();
            if (sourceMatches.Length != 1 || !SamePath(sourceMatches[0], target))
                throw new InvalidDataException(
                    "CAPCUT_MULTIPLE_TASK_DRAFTS: more than one draft matches the current source video");

            var extras = directories.Where(path => !SamePath(path, target))
                .ToArray();
            foreach (string extra in extras) {
                string bootstrapReason;
                if (!IsVerifiedBootstrapDraft(extra, storeList, lease,
                        expectedSource, out bootstrapReason))
                    throw new InvalidDataException(
                        "CAPCUT_UNKNOWN_EXTRA_DRAFT: an additional draft could not be proven to be an empty bootstrap draft (" +
                        bootstrapReason + ")");
            }

            string recycleRoot = Path.Combine(rootDirectory, ".recycle_bin");
            Directory.CreateDirectory(recycleRoot);
            // Keep diagnostics recoverable without allowing a long draft ID
            // or full timestamp to push the fixed CapCut root over MAX_PATH.
            // The original index is still preserved verbatim; only its
            // quarantine filename is shortened.
            string backup = UniqueShortFile(recycleRoot, "i-", ".json");
            File.Copy(indexPath, backup, false);
            foreach (string extra in extras) {
                Dictionary<string, object> meta = ReadJsonObject(
                    Path.Combine(extra, "draft_meta_info.json"));
                string id = ShortHash(Text(meta, "draft_id"), 12);
                string destination = UniqueBootstrapDirectory(recycleRoot, id);
                Directory.Move(extra, destination);
                logger.Warn("CAPCUT_BOOTSTRAP_DRAFT_QUARANTINED: " +
                    Path.GetFileName(extra) + " -> " +
                    Path.GetFileName(destination));
            }

            string mountedTarget = Path.Combine(lease.OriginalDraftRoot,
                Path.GetFileName(target));
            WriteSingleDraftIndex(rootDirectory, rootDirectory,
                checkpoint.DraftId, mountedTarget,
                lease.OriginalDraftRoot);
        }

        private static bool DraftIdentityMatchesCheckpoint(string directory,
            DirectDraftCheckpoint checkpoint)
        {
            if (checkpoint == null || String.IsNullOrWhiteSpace(directory))
                return false;
            try {
                string expectedFolder = Path.GetFileName(
                    (checkpoint.DraftDirectory ?? "").TrimEnd('\\', '/'));
                bool folderMatches = String.IsNullOrWhiteSpace(expectedFolder) ||
                    String.Equals(Path.GetFileName(directory), expectedFolder,
                        StringComparison.OrdinalIgnoreCase);
                if (!folderMatches) return false;

                // CapCut 9.2 can leave draft_meta_info.json empty after a
                // safe close even though the root index still has the exact
                // draft identity.  The previous resolver treated that valid
                // archive as missing, so the next Safe Continue stopped at
                // CAPCUT_DRAFT_CHECKPOINT_ARCHIVE_NOT_FOUND.  Use the root
                // index as a second, identity-locked source; require exactly
                // one matching entry and never infer identity from a folder
                // name alone.
                string metaPath = Path.Combine(directory, "draft_meta_info.json");
                if (File.Exists(metaPath) &&
                    new FileInfo(metaPath).Length > 0) {
                    Dictionary<string, object> meta = ReadJsonObject(metaPath);
                    return String.Equals(Text(meta, "draft_id"),
                            checkpoint.DraftId ?? "",
                            StringComparison.OrdinalIgnoreCase) &&
                        (String.IsNullOrWhiteSpace(checkpoint.DraftName) ||
                         String.Equals(Text(meta, "draft_name"),
                            checkpoint.DraftName, StringComparison.Ordinal));
                }

                string root = File.Exists(Path.Combine(directory,
                        "root_meta_info.json"))
                    ? directory : Path.GetDirectoryName(directory);
                string indexPath = String.IsNullOrWhiteSpace(root) ? "" :
                    Path.Combine(root, "root_meta_info.json");
                if (String.IsNullOrWhiteSpace(indexPath) ||
                    !File.Exists(indexPath)) return false;
                Dictionary<string, object> index = ReadJsonObject(indexPath);
                object rawStores;
                object[] stores = index.TryGetValue("all_draft_store",
                    out rawStores) ? ToObjectArray(rawStores) : new object[0];
                var matches = stores.Select(value =>
                        value as Dictionary<string, object>)
                    .Where(store => store != null &&
                        String.Equals(Text(store, "draft_id"),
                            checkpoint.DraftId ?? "",
                            StringComparison.OrdinalIgnoreCase) &&
                        (String.IsNullOrWhiteSpace(checkpoint.DraftName) ||
                         String.Equals(Text(store, "draft_name"),
                            checkpoint.DraftName, StringComparison.Ordinal)) &&
                        (String.IsNullOrWhiteSpace(Text(store,
                            "draft_fold_path")) ||
                         String.Equals(Path.GetFileName(Text(store,
                                "draft_fold_path").TrimEnd('\\', '/')),
                            Path.GetFileName(directory),
                            StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                return matches.Length == 1;
            } catch { return false; }
        }

        private static bool DraftContainsSource(string directory,
            string expectedSource)
        {
            if (String.IsNullOrWhiteSpace(directory) ||
                String.IsNullOrWhiteSpace(expectedSource)) return false;
            string full;
            try { full = Path.GetFullPath(expectedSource); }
            catch { full = expectedSource; }
            string slash = full.Replace('\\', '/');
            string escaped = full.Replace("\\", "\\\\");
            foreach (string file in Directory.GetFiles(directory, "*",
                    SearchOption.AllDirectories)) {
                string extension = Path.GetExtension(file);
                if (!String.Equals(extension, ".json",
                        StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(extension, ".tmp",
                        StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(extension, ".bak",
                        StringComparison.OrdinalIgnoreCase)) continue;
                try {
                    string text = File.ReadAllText(file, Encoding.UTF8);
                    if (text.IndexOf(full, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        text.IndexOf(slash, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        text.IndexOf(escaped, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                } catch { }
            }
            return false;
        }

        private static bool IsVerifiedBootstrapDraft(string directory,
            IEnumerable<Dictionary<string, object>> stores,
            DraftRootLeaseRecord lease, string expectedSource,
            out string reason)
        {
            reason = "unknown";
            try {
                if (DraftContainsSource(directory, expectedSource)) {
                    reason = "contains_current_source";
                    return false;
                }
                Dictionary<string, object> meta = ReadJsonObject(
                    Path.Combine(directory, "draft_meta_info.json"));
                string id = Text(meta, "draft_id");
                string name = Text(meta, "draft_name");
                if (String.IsNullOrWhiteSpace(id)) {
                    reason = "missing_draft_id";
                    return false;
                }
                if (String.IsNullOrWhiteSpace(name)) {
                    reason = "missing_draft_name";
                    return false;
                }
                if (ReadLong(meta, "tm_duration") != 0) {
                    reason = "nonzero_duration";
                    return false;
                }
                if (!DraftMaterialsAreEmpty(meta)) {
                    reason = "materials_not_empty";
                    return false;
                }
                Dictionary<string, object>[] matches = stores.Where(store =>
                    String.Equals(Text(store, "draft_id"), id,
                        StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(Path.GetFileName(Text(store,
                        "draft_fold_path").TrimEnd('\\', '/')),
                        Path.GetFileName(directory),
                        StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) {
                    reason = "index_identity_count_" + matches.Length;
                    return false;
                }
                // The task root itself is keyed by the immutable production
                // identity.  A safe-continue attempt may reclaim that root, so
                // comparing the folder timestamp with the newest lease time
                // would incorrectly reject a bootstrap created by the prior
                // attempt of the same task.
                reason = "verified_empty_bootstrap";
                return true;
            } catch (Exception ex) {
                reason = "inspection_failed_" + ex.GetType().Name + "_" +
                    ex.Message.Replace('\r', ' ').Replace('\n', ' ');
                return false;
            }
        }

        private static bool DraftMaterialsAreEmpty(
            Dictionary<string, object> meta)
        {
            object raw;
            object[] groups = meta.TryGetValue("draft_materials", out raw)
                ? ToObjectArray(raw) : new object[0];
            if (groups.Length == 0) return false;
            foreach (object item in groups) {
                Dictionary<string, object> group =
                    item as Dictionary<string, object>;
                object values;
                if (group == null || !group.TryGetValue("value", out values))
                    return false;
                if (ToObjectArray(values).Length != 0) return false;
            }
            return true;
        }

        private static long ReadLong(Dictionary<string, object> value,
            string key)
        {
            object raw;
            if (value == null || !value.TryGetValue(key, out raw) ||
                raw == null) return 0;
            long parsed;
            return Int64.TryParse(Convert.ToString(raw), out parsed)
                ? parsed : 0;
        }

        // CapCut 9.2 does not reliably open a custom draft root when that
        // root is a junction to another volume.  In the v2 storage mode the
        // fixed CapCut path is therefore a normal directory on C:.  The E:
        // task root remains the durable archive/checkpoint location.
        private DraftRootPreparationResult PrepareLocalActiveDirectory(VideoTask task,
            DraftRootLeaseRecord lease, CancellationToken token)
        {
            if (lease == null) throw new ArgumentNullException("lease");
            token.ThrowIfCancellationRequested();
            UpgradeLocalArchiveLayout(lease);
            string active = Path.GetFullPath(String.IsNullOrWhiteSpace(
                lease.ActiveDraftRoot) ? lease.OriginalDraftRoot :
                lease.ActiveDraftRoot);
            string archive = Path.GetFullPath(lease.ArchiveDraftRoot);
            lease.ActiveDraftRoot = active;
            lease.ArchiveDraftRoot = archive;
            lease.TaskDraftRoot = active;
            lease.JunctionMounted = false;

            DriveInfo drive = new DriveInfo(Path.GetPathRoot(active));
            const long minimumFreeBytes = 10L * 1024L * 1024L * 1024L;
            if (drive.AvailableFreeSpace < minimumFreeBytes)
                throw new IOException("CAPCUT_ACTIVE_DRAFT_ROOT_PROBE_FAILED: C盘活动草稿目录剩余空间低于10GB");

            Directory.CreateDirectory(archive);
            DirectDraftCheckpoint checkpoint = RecoveryCheckpoint(task);
            bool restoredExistingCheckpoint = checkpoint != null;
            if (checkpoint == null)
                checkpoint = DiscoverArchivedVideoTimelineCandidate(task, lease);
            bool discoveredArchivedCandidate = checkpoint != null &&
                !restoredExistingCheckpoint;
            DraftRootPreparationResult emptyBootstrap = checkpoint == null
                ? DiscoverArchivedEmptyBootstrap(archive, lease, task) : null;
            // Resolve the durable source before touching CapCut's fixed C:
            // directory.  A released attempt no longer has a draft at the
            // old C: path; its verified copy lives under the stable archive.
            string resolvedCheckpointSource = checkpoint == null ? "" :
                ResolveCheckpointDraftSource(task, lease, checkpoint);
            if (checkpoint != null && String.IsNullOrWhiteSpace(
                    resolvedCheckpointSource))
                throw new DirectoryNotFoundException(
                    "CAPCUT_DRAFT_CHECKPOINT_ARCHIVE_NOT_FOUND: verified draft archive could not be resolved");

            // On the first acquisition this is the user's real historical
            // directory.  Protect it before creating the task-owned active
            // directory.  On a crash-recovery acquisition the marker proves
            // that the existing directory already belongs to this lease.
            string marker = Path.Combine(active,
                lease.JunctionMarkerName ?? ".codex-draft-root.marker");
            if (Directory.Exists(active)) {
                bool owned = false;
                if (File.Exists(marker)) {
                    DraftRootOwnershipValidationResult ownership =
                        ValidateActiveRootOwnership(lease);
                    owned = ownership.IsValid;
                    if (owned && !lease.ActiveRootOwnershipCommitted) {
                        lease.ActiveRootOwnershipCommitted = true;
                        lease.ActiveRootOwnershipCommittedAt = DateTime.Now.ToString("o");
                        SaveLease(lease);
                    }
                }
                if (!owned) {
                    if (Directory.Exists(lease.ProtectedOriginalDraftRoot)) {
                        // A hard stop can leave the C: active root empty after
                        // its marker/index flush has been interrupted.  The
                        // protected original proves that this is not the
                        // user's live folder.  Reclaim only a provably empty
                        // root; any unknown file or real draft remains a hard
                        // safety stop and is never guessed or overwritten.
                        if (!TaskRootIsProvablyEmptyLocal(lease))
                            throw new InvalidOperationException(
                                "CAPCUT_ACTIVE_DRAFT_ROOT_PROBE_FAILED: 固定草稿目录存在未知内容");
                        string evidenceRoot = Path.GetFullPath(String.IsNullOrWhiteSpace(
                            lease.DiagnosticRoot) ? Path.Combine(shortArchiveRootsDirectory,
                            "evidence") : lease.DiagnosticRoot);
                        Directory.CreateDirectory(evidenceRoot);
                        if (Directory.GetFileSystemEntries(active).Length > 0) {
                            string retained = UniqueShortChild(evidenceRoot, "pre-");
                            MoveDirectoryAcrossVolumesVerified(active, retained);
                        } else {
                            Directory.Delete(active, false);
                        }
                    }
                    if (Directory.Exists(active) &&
                        (File.GetAttributes(active) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException(
                            "CAPCUT_ACTIVE_DRAFT_ROOT_PROBE_FAILED: 固定草稿目录是未知联接");
                    if (!Directory.Exists(lease.ProtectedOriginalDraftRoot) &&
                        Directory.Exists(active))
                        Directory.Move(active, lease.ProtectedOriginalDraftRoot);
                }
            }
            if (!Directory.Exists(active)) Directory.CreateDirectory(active);
            if ((File.GetAttributes(active) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "CAPCUT_ACTIVE_DRAFT_ROOT_PROBE_FAILED: 固定草稿目录不能是联接");
            File.WriteAllText(marker, MarkerValue(lease),
                new UTF8Encoding(false));
            lease.ActiveRootOwnershipCommitted = true;
            lease.ActiveRootOwnershipCommittedAt = DateTime.Now.ToString("o");
            // Persist the ownership boundary before checkpoint copying or
            // CapCut interaction.  Later failures can then be recovered
            // without confusing the user's untouched directory for ours.
            SaveLease(lease);

            if (checkpoint != null) {
                string source = String.IsNullOrWhiteSpace(resolvedCheckpointSource)
                    ? Path.GetFullPath(checkpoint.DraftDirectory ?? "")
                    : Path.GetFullPath(resolvedCheckpointSource);
                if (!Directory.Exists(source) &&
                    SamePath(source, active) &&
                    Directory.Exists(lease.ProtectedOriginalDraftRoot)) {
                    // If an old checkpoint described CapCut's fixed path,
                    // protecting that path above moves the source under the
                    // protected historical root.  Do not guess another draft;
                    // only the exact relative child is eligible.
                    string relative = source.TrimEnd('\\').Substring(
                        active.TrimEnd('\\').Length).TrimStart('\\');
                    source = String.IsNullOrWhiteSpace(relative)
                        ? lease.ProtectedOriginalDraftRoot
                        : Path.Combine(lease.ProtectedOriginalDraftRoot, relative);
                }
                string name = Path.GetFileName(source);
                string archiveDraft = Path.Combine(archive, name);
                string activeDraft = Path.Combine(active, name);
                // A checkpoint may still point at the previous E: task root.
                // Seed the durable archive whenever it is absent, regardless
                // of whether CapCut's fixed C: directory already existed.
                // This is what makes a restart recoverable after an interrupted
                // local-directory migration.
                if (!Directory.Exists(archiveDraft)) {
                    if (!Directory.Exists(source))
                        throw new DirectoryNotFoundException(
                            "draft_checkpoint_directory_missing: " + source);
                    CopyDirectory(source, archiveDraft, token);
                }
                if (!Directory.Exists(activeDraft))
                    CopyDirectory(archiveDraft, activeDraft, token);
                RewriteCopiedTextPaths(activeDraft, archiveDraft, activeDraft);
                WriteSingleDraftIndex(archive, active, checkpoint.DraftId,
                    activeDraft, active);
                checkpoint.DraftDirectory = activeDraft;
                checkpoint.TaskDraftRoot = active;
                checkpoint.DraftRootLeaseId = lease.LeaseId;
                SetCheckpointArchiveFields(checkpoint, lease,
                    Path.GetFileName(activeDraft));
                checkpoint.PatchedFiles = MapPaths(checkpoint.PatchedFiles,
                    source, activeDraft);
                SyncRecoveryCheckpoint(task, checkpoint);
            } else if (emptyBootstrap != null) {
                // A prior attempt may have created a valid but still empty
                // CapCut draft before the client stopped.  Preserve and bind
                // that exact draft rather than clicking "开始创作" again.
                string source = Path.GetFullPath(emptyBootstrap.ArchiveDraftPath);
                string name = Path.GetFileName(source);
                string activeDraft = Path.Combine(active, name);
                if (!Directory.Exists(activeDraft))
                    CopyDirectory(source, activeDraft, token);
                // The archive index is the trusted identity source.  The
                // active directory is intentionally still empty at this
                // point, so using it as the source would turn a valid
                // bootstrap recovery into draft_checkpoint_index_missing.
                WriteSingleDraftIndex(archive, active,
                    emptyBootstrap.DraftId, activeDraft, active);
                emptyBootstrap.ActiveDraftPath = activeDraft;
                emptyBootstrap.StableTaskArchiveKey = lease.StableTaskArchiveKey ?? "";
            } else {
                WriteEmptyIndex(active);
            }

            // A write/read/rename probe catches ACL and redirected-folder
            // failures before CapCut is launched.  The probe file is owned by
            // this lease and is removed immediately.
            string probe = Path.Combine(active, ".codex-active-draft-probe-" +
                Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, lease.TaskIdentity ?? "", Encoding.UTF8);
            string probeCopy = probe + ".copy";
            File.Copy(probe, probeCopy, false);
            if (!String.Equals(File.ReadAllText(probeCopy, Encoding.UTF8),
                lease.TaskIdentity ?? "", StringComparison.Ordinal))
                throw new IOException("CAPCUT_ACTIVE_DRAFT_ROOT_PROBE_FAILED: 活动草稿目录读回校验失败");
            File.Delete(probe);
            File.Delete(probeCopy);
            SaveLease(lease);
            if (checkpoint == null && emptyBootstrap == null) {
                return new DraftRootPreparationResult {
                    Mode = "empty_new",
                    StableTaskArchiveKey = lease.StableTaskArchiveKey ?? "",
                    SourceVideoIdentity = String.IsNullOrWhiteSpace(task.VideoPath)
                        ? "" : Path.GetFullPath(task.VideoPath)
                };
            }
            if (emptyBootstrap != null)
                return emptyBootstrap;
            string preparedName = checkpoint.DraftRelativePath;
            if (String.IsNullOrWhiteSpace(preparedName))
                preparedName = checkpoint.DraftName;
            return new DraftRootPreparationResult {
                Mode = discoveredArchivedCandidate
                    ? "archived_candidate" : "restored_checkpoint",
                DraftId = checkpoint.DraftId ?? "",
                DraftName = checkpoint.DraftName ?? "",
                ActiveDraftPath = Path.GetFullPath(
                    Path.Combine(active, preparedName ?? "")),
                ArchiveDraftPath = Path.GetFullPath(
                    Path.Combine(archive, preparedName ?? "")),
                StableTaskArchiveKey = lease.StableTaskArchiveKey ?? "",
                SourceVideoIdentity = String.IsNullOrWhiteSpace(task.VideoPath)
                    ? "" : Path.GetFullPath(task.VideoPath)
            };
        }

        // Discover only one archive entry that is provably an empty bootstrap
        // draft.  Unknown files, multiple entries, or a non-empty timeline are
        // deliberately left for the normal safety gate; we never guess which
        // draft to inherit based on name or modification time.
        private DraftRootPreparationResult DiscoverArchivedEmptyBootstrap(
            string archive, DraftRootLeaseRecord lease, VideoTask task)
        {
            try {
                string indexPath = Path.Combine(archive, "root_meta_info.json");
                if (!File.Exists(indexPath)) return null;
                Dictionary<string, object> root = ReadJsonObject(indexPath);
                object rawStores;
                object[] stores = root.TryGetValue("all_draft_store", out rawStores)
                    ? ToObjectArray(rawStores) : new object[0];
                if (stores.Length != 1) return null;
                Dictionary<string, object> store = stores[0]
                    as Dictionary<string, object>;
                if (store == null) return null;
                string id = Text(store, "draft_id");
                string folder = Text(store, "draft_fold_path");
                if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(folder))
                    return null;
                string draft = Path.GetFullPath(Path.IsPathRooted(folder)
                    ? folder : Path.Combine(archive, folder));
                if (!Directory.Exists(draft) || !IsProvablyBlankDraftDirectory(draft))
                    return null;
                string name = Text(store, "draft_name");
                if (String.IsNullOrWhiteSpace(name))
                    name = Path.GetFileName(draft);
                return new DraftRootPreparationResult {
                    Mode = "empty_bootstrap",
                    DraftId = id,
                    DraftName = name,
                    ArchiveDraftPath = draft,
                    ActiveDraftPath = "",
                    StableTaskArchiveKey = lease == null ? "" :
                        (lease.StableTaskArchiveKey ?? ""),
                    SourceVideoIdentity = task == null ||
                        String.IsNullOrWhiteSpace(task.VideoPath) ? "" :
                        Path.GetFullPath(task.VideoPath)
                };
            } catch { return null; }
        }

        private static DirectDraftCheckpoint DiscoverArchivedVideoTimelineCandidate(
            VideoTask task, DraftRootLeaseRecord lease)
        {
            if (task == null || lease == null ||
                String.IsNullOrWhiteSpace(lease.ArchiveDraftRoot) ||
                !Directory.Exists(lease.ArchiveDraftRoot)) return null;
            string archive = Path.GetFullPath(lease.ArchiveDraftRoot);
            string indexPath = Path.Combine(archive, "root_meta_info.json");
            if (!File.Exists(indexPath)) return null;
            Dictionary<string, object> root = ReadJsonObject(indexPath);
            object rawStores;
            object[] stores = root.TryGetValue("all_draft_store", out rawStores)
                ? ToObjectArray(rawStores) : new object[0];
            // An empty live archive is the normal state after an interrupted
            // bootstrap draft has been quarantined.  It means there is no
            // recoverable checkpoint, so the caller may create one new draft.
            // Do not report it as a "multiple drafts" safety conflict.
            if (stores.Length == 0) return null;
            if (stores.Length != 1) {
                // CapCut may leave more than one zero-content bootstrap card
                // after a create attempt is interrupted.  These cards are
                // evidence of a failed launch, not a usable checkpoint.  Do
                // not select the first one (or let the next attempt inherit
                // the ambiguity); treat the whole set as "no checkpoint" only
                // when every indexed card is independently proven blank.
                if (stores.Length > 1) {
                    bool allBlank = true;
                    foreach (object raw in stores) {
                        Dictionary<string, object> candidate =
                            raw as Dictionary<string, object>;
                        string value = Text(candidate, "draft_fold_path");
                        string candidateFolderName = Path.GetFileName(
                            (value ?? "").TrimEnd('\\', '/'));
                        if (String.IsNullOrWhiteSpace(candidateFolderName)) {
                            allBlank = false;
                            break;
                        }
                        string candidatePath = Path.Combine(archive, candidateFolderName);
                        if (!Directory.Exists(candidatePath) ||
                            !IsProvablyBlankDraftDirectory(candidatePath)) {
                            allBlank = false;
                            break;
                        }
                    }
                    if (allBlank) {
                        return null;
                    }
                }
                throw new InvalidDataException(
                    "CAPCUT_VIDEO_TIMELINE_MULTIPLE_DRAFTS_BLOCKED: stable archive index is not singular");
            }
            Dictionary<string, object> store =
                stores[0] as Dictionary<string, object>;
            string folderValue = Text(store, "draft_fold_path");
            string folderName = Path.GetFileName(
                (folderValue ?? "").TrimEnd('\\', '/'));
            if (String.IsNullOrWhiteSpace(folderName))
                throw new InvalidDataException(
                    "CAPCUT_VIDEO_TIMELINE_CHECKPOINT_INVALID: stable archive index has no draft path");
            string draft = Path.Combine(archive, folderName);
            if (!Directory.Exists(draft))
                throw new InvalidDataException(
                    "CAPCUT_VIDEO_TIMELINE_CHECKPOINT_INVALID: stable archive draft directory is missing");
            if (!DraftContainsSource(draft, task.VideoPath)) {
                // A previous create attempt may have archived CapCut's empty
                // bootstrap project before any media was inserted.  It is not
                // a recovery checkpoint and must not block the next attempt.
                // Keep the archive/evidence for audit, but treat this exact
                // zero-track/zero-media shape as "no checkpoint".  Any
                // non-empty wrong-source archive remains a hard identity
                // conflict and is never guessed or reused.
                if (IsProvablyBlankDraftDirectory(draft)) return null;
                throw new InvalidDataException(
                    "CAPCUT_VIDEO_TIMELINE_CHECKPOINT_INVALID: stable archive does not belong to the current source video");
            }
            Dictionary<string, object> meta = ReadJsonObject(
                Path.Combine(draft, "draft_meta_info.json"));
            string draftId = Text(meta, "draft_id");
            string draftName = Text(meta, "draft_name");
            if (String.IsNullOrWhiteSpace(draftId) ||
                !String.Equals(draftId, Text(store, "draft_id"),
                    StringComparison.OrdinalIgnoreCase) ||
                (!String.IsNullOrWhiteSpace(Text(store, "draft_name")) &&
                 !String.Equals(draftName, Text(store, "draft_name"),
                    StringComparison.Ordinal)))
                throw new InvalidDataException(
                    "CAPCUT_VIDEO_TIMELINE_CHECKPOINT_INVALID: stable archive draft identity is inconsistent");
            string[] verifiedFiles = Directory.GetFiles(draft,
                    "draft_content.json", SearchOption.AllDirectories)
                .Select(Path.GetFullPath).ToArray();
            if (verifiedFiles.Length == 0)
                throw new InvalidDataException(
                    "CAPCUT_VIDEO_TIMELINE_CHECKPOINT_INVALID: stable archive has no timeline content");
            return new DirectDraftCheckpoint {
                State = "video_timeline_candidate",
                DraftDirectory = draft,
                DraftId = draftId,
                DraftName = draftName,
                SourceVideoPath = Path.GetFullPath(task.VideoPath),
                PatchedFiles = verifiedFiles,
                TaskDraftRoot = archive,
                ArchiveStorageVersion = lease.ArchiveStorageVersion,
                StableTaskArchiveKey = lease.StableTaskArchiveKey,
                ArchiveDraftRoot = archive,
                DraftRelativePath = folderName,
                UpdatedAt = DateTime.Now.ToString("o")
            };
        }

        private void UpgradeLocalArchiveLayout(DraftRootLeaseRecord lease)
        {
            if (lease == null) throw new ArgumentNullException("lease");
            string stableKey = !String.IsNullOrWhiteSpace(
                lease.StableTaskArchiveKey) ? lease.StableTaskArchiveKey :
                BuildStableKeyFromLease(lease);
            string identityShort = ShortHash(stableKey ??
                lease.TaskIdentity ?? lease.LeaseId, 16);
            string expectedShortRoot = Path.Combine(shortArchiveRootsDirectory,
                "t-" + identityShort);
            string configuredShortRoot = String.IsNullOrWhiteSpace(
                lease.ShortArchiveRoot) ? "" : Path.GetFullPath(
                    lease.ShortArchiveRoot);
            string shortRoot = String.IsNullOrWhiteSpace(configuredShortRoot) ||
                !String.Equals(Path.GetFileName(configuredShortRoot.TrimEnd(
                    '\\', '/')), "t-" + identityShort,
                    StringComparison.OrdinalIgnoreCase)
                ? expectedShortRoot : configuredShortRoot;
            string legacy = lease.ArchiveDraftRoot ?? "";
            string live = Path.Combine(shortRoot, "live");
            if (!String.IsNullOrWhiteSpace(legacy) &&
                !SamePath(legacy, live) &&
                String.IsNullOrWhiteSpace(lease.LegacyArchiveDraftRoot))
                lease.LegacyArchiveDraftRoot = Path.GetFullPath(legacy);
            lease.StableTaskArchiveKey = stableKey;
            lease.ArchiveStorageVersion = "3";
            lease.ShortArchiveRoot = shortRoot;
            lease.ArchiveDraftRoot = live;
            lease.ArchiveStageRoot = Path.Combine(shortRoot, "stg");
            lease.ArchivePreviousRoot = Path.Combine(shortRoot, "prev");
            lease.DiagnosticRoot = Path.Combine(shortRoot, "evidence");
            if (String.IsNullOrWhiteSpace(lease.ArchiveState))
                lease.ArchiveState = "pending";
        }

        // A failure can happen before PrepareLocalActiveDirectory writes the
        // marker.  In that window the fixed path is still the user's ordinary
        // directory, not a lease-owned root.  Treating it as an owned root
        // makes Release() raise MARKER_MISSING and masks the real checkpoint
        // error, leaving a stale lease that breaks the next task.
        private bool IsUnownedAcquireRootSafe(DraftRootLeaseRecord lease)
        {
            if (lease == null || lease.ActiveRootOwnershipCommitted) return false;
            string active = lease.ActiveDraftRoot ?? "";
            string original = lease.OriginalDraftRoot ?? "";
            if (String.IsNullOrWhiteSpace(active) || String.IsNullOrWhiteSpace(original))
                return false;
            try {
                active = Path.GetFullPath(active);
                original = Path.GetFullPath(original);
                if (!SamePath(active, original)) return false;
                if (Directory.Exists(lease.ProtectedOriginalDraftRoot)) {
                    // The original was moved, so the active path is potentially
                    // ours.  It is safe only when it is an empty bootstrap root.
                    if (!Directory.Exists(active)) return true;
                    if ((File.GetAttributes(active) & FileAttributes.ReparsePoint) != 0)
                        return false;
                    foreach (string entry in Directory.GetFileSystemEntries(active)) {
                        string name = Path.GetFileName(entry);
                        if (String.Equals(name, lease.JunctionMarkerName,
                                StringComparison.OrdinalIgnoreCase) ||
                            String.Equals(name, "root_meta_info.json",
                                StringComparison.OrdinalIgnoreCase) ||
                            String.Equals(name, "Cache",
                                StringComparison.OrdinalIgnoreCase) ||
                            String.Equals(name, ".recycle_bin",
                                StringComparison.OrdinalIgnoreCase)) continue;
                        if (Directory.Exists(entry) && IsProvablyBlankDraftDirectory(entry))
                            continue;
                        return false;
                    }
                    return true;
                }
                // No protected copy means the user's original directory was
                // never moved.  Do not inspect or move its contents.
                return Directory.Exists(active) &&
                    (File.GetAttributes(active) & FileAttributes.ReparsePoint) == 0;
            } catch { return false; }
        }

        private void FinalizeUnownedAcquireLease(DraftRootLeaseRecord lease)
        {
            if (lease == null) return;
            lease.ArchiveState = "acquire_failed_unowned";
            lease.Stage = "acquire_failed_unowned";
            lease.RecoveryCode = "CAPCUT_ACQUIRE_FAILED_BEFORE_OWNERSHIP";
            // Local mode never changes globalSetting, but restoring the saved
            // value here also makes this safe if a future preparation step is
            // inserted before the ownership commit.
            if (File.Exists(lease.GlobalSettingBackupPath))
                RestoreExactFile(lease.GlobalSettingBackupPath, lease.GlobalSettingPath);
            lease.ArchiveState = "released";
            lease.Stage = "released";
            lease.ReleasedAt = DateTime.Now.ToString("o");
            lease.HeartbeatAt = lease.ReleasedAt;
            SaveLease(lease);
            WriteLeaseHistory(lease);
            try { File.Delete(leaseFile); } catch { }
            logger.Info("Released a pre-ownership CapCut lease without touching the user's draft directory: " + lease.LeaseId);
            current = null;
            currentTask = null;
            currentPreparation = null;
        }

        private bool TaskRootIsProvablyEmptyLocal(DraftRootLeaseRecord lease)
        {
            try {
                string active = Path.GetFullPath(lease.ActiveDraftRoot ??
                    lease.OriginalDraftRoot ?? "");
                if (!Directory.Exists(active) ||
                    (File.GetAttributes(active) & FileAttributes.ReparsePoint) != 0)
                    return false;
            DraftRootOwnershipValidationResult ownership =
                ValidateActiveRootOwnership(lease);
            if (!ownership.IsValid) {
                // A hard stop can occur after ownership was committed but
                // before the marker/index flush reaches disk.  If the
                // protected original root exists and the active root contains
                // no unknown files, it is still safe to recover this stale
                // same-task lease.  Never accept an unmarked root when the
                // user's original directory was not protected.
                if (!Directory.Exists(lease.ProtectedOriginalDraftRoot) ||
                    (File.GetAttributes(lease.ProtectedOriginalDraftRoot) &
                        FileAttributes.ReparsePoint) != 0)
                    return false;
            }
                string[] entries = Directory.GetFileSystemEntries(active);
                foreach (string entry in entries) {
                    string name = Path.GetFileName(entry);
                    if (String.Equals(name, lease.JunctionMarkerName,
                            StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(name, "root_meta_info.json",
                            StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(name, "Cache",
                            StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(name, ".recycle_bin",
                            StringComparison.OrdinalIgnoreCase)) continue;
                    if (Directory.Exists(entry) &&
                        IsProvablyBlankDraftDirectory(entry)) continue;
                    return false;
                }
                string index = Path.Combine(active, "root_meta_info.json");
                // A failed acquire can leave only the ownership marker.  It
                // is safe to release that directory after all other entries
                // have been ruled out; a real draft still requires a root
                // index and is never treated as empty.
                if (!File.Exists(index)) return true;
                Dictionary<string, object> root = ReadJsonObject(index);
                object raw;
                return !root.TryGetValue("all_draft_store", out raw) ||
                    ToObjectArray(raw).Length == 0;
            } catch { return false; }
        }

        private void ArchiveLocalActiveDirectory(DraftRootLeaseRecord lease,
            CancellationToken token)
        {
            string active = Path.GetFullPath(lease.ActiveDraftRoot ??
                lease.OriginalDraftRoot ?? "");
            if (!Directory.Exists(active) ||
                (File.GetAttributes(active) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "CAPCUT_DRAFT_ARCHIVE_VERIFY_FAILED: 活动草稿目录不存在或不是普通目录");
            DraftRootOwnershipValidationResult ownership =
                ValidateActiveRootOwnership(lease);
            if (!ownership.IsValid)
                throw new InvalidDataException(
                    ownership.ErrorCode + ": " + ownership.Diagnostic);

            UpgradeLocalArchiveLayout(lease);
            string archive = Path.GetFullPath(lease.ArchiveDraftRoot);
            string staging = Path.GetFullPath(lease.ArchiveStageRoot);
            string diagnostics = Path.GetFullPath(lease.DiagnosticRoot);
            if (String.Equals(lease.ArchiveState, "archive_committed",
                    StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(lease.ArchiveDraftRoot) &&
                File.Exists(Path.Combine(lease.ArchiveDraftRoot,
                    "root_meta_info.json"))) return;
            try {
                lease.ArchiveState = "path_preflight";
                lease.Stage = "path_preflight";
                SaveLease(lease);
                ValidateCopyPathLengths(active, staging);
                if (Directory.Exists(staging)) {
                    string stale = UniqueShortChild(diagnostics, "stg-");
                    Directory.CreateDirectory(diagnostics);
                    Directory.Move(staging, stale);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(staging));
                lease.ArchiveState = "archive_copying";
                lease.Stage = "archive_copying";
                SaveLease(lease);
                CopyDirectory(active, staging, token);

                // Diagnostics are retained, but are not part of the trusted
                // production checkpoint.  Copy the recycle area to its
                // sibling evidence directory so long internal names never
                // become part of the normal archive path.
                string activeRecycle = Path.Combine(active, ".recycle_bin");
                if (Directory.Exists(activeRecycle)) {
                    string evidence = UniqueShortChild(diagnostics, "r-");
                    Directory.CreateDirectory(diagnostics);
                    ValidateCopyPathLengths(activeRecycle, evidence);
                    CopyDirectory(activeRecycle, evidence, token);
                }
                string stagedIndex = Path.Combine(staging, "root_meta_info.json");
                if (!File.Exists(stagedIndex))
                    throw new InvalidDataException(
                        "CAPCUT_DRAFT_ARCHIVE_VERIFY_FAILED: 归档缺少根索引");
                ValidateArchiveCopy(active, staging);
                lease.ArchiveManifestHash = ComputeArchiveManifestHash(staging);
                lease.ArchiveVerifiedAt = DateTime.Now.ToString("o");
                lease.ArchiveState = "archive_verifying";
                lease.Stage = "archive_verifying";
                SaveLease(lease);

                if (Directory.Exists(archive)) {
                    Directory.CreateDirectory(lease.ArchivePreviousRoot);
                    string previous = UniqueShortChild(lease.ArchivePreviousRoot, "p-");
                    Directory.Move(archive, previous);
                }
                Directory.Move(staging, archive);
                lease.ArchiveState = "archive_committed";
                lease.Stage = "active_directory_archived";
                SaveLease(lease);
                PersistArchiveCheckpoint(currentTask, lease);
            } catch (Exception ex) {
                lease.ArchiveState = ex.Message.IndexOf("CAPCUT_DRAFT_ARCHIVE_PATH_TOO_LONG",
                    StringComparison.OrdinalIgnoreCase) >= 0
                    ? "path_preflight_failed" : "archive_pending";
                lease.Stage = "archive_pending";
                try { SaveLease(lease); } catch { }
                throw;
            }
        }

        private void RestoreLocalOriginalDraftRoot(DraftRootLeaseRecord lease)
        {
            string active = Path.GetFullPath(lease.ActiveDraftRoot ??
                lease.OriginalDraftRoot ?? "");
            try {
                lease.ArchiveState = "restoring_original";
                lease.Stage = "restoring_original";
                SaveLease(lease);
                if (Directory.Exists(active)) {
                    string retained = active.TrimEnd('\\') + ".codex-retained-" +
                        ShortHash(lease.LeaseId ?? lease.TaskIdentity, 12);
                    if (Directory.Exists(retained))
                        retained = active.TrimEnd('\\') + ".codex-retained-" +
                            ShortHash(lease.LeaseId ?? lease.TaskIdentity, 12) + "-" +
                            DateTime.Now.ToString("yyMMddHHmmss");
                    Directory.Move(active, retained);
                }
                if (!Directory.Exists(lease.ProtectedOriginalDraftRoot) &&
                    Directory.Exists(lease.OriginalDraftRoot) &&
                    (File.GetAttributes(lease.OriginalDraftRoot) & FileAttributes.ReparsePoint) == 0) {
                    lease.ArchiveState = "original_verified";
                    lease.Stage = "original_verified";
                    SaveLease(lease);
                    return;
                }
                if (!Directory.Exists(lease.ProtectedOriginalDraftRoot))
                    throw new DirectoryNotFoundException(
                        "CAPCUT_ORIGINAL_DRAFT_RESTORE_FAILED: protected historical root missing");
                Directory.Move(lease.ProtectedOriginalDraftRoot, lease.OriginalDraftRoot);
                if (!Directory.Exists(lease.OriginalDraftRoot) ||
                    (File.GetAttributes(lease.OriginalDraftRoot) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(
                        "CAPCUT_ORIGINAL_DRAFT_RESTORE_FAILED: restored root verification failed");
                lease.ArchiveState = "original_verified";
                lease.Stage = "original_verified";
                SaveLease(lease);
            } catch {
                lease.ArchiveState = "original_restore_pending";
                lease.Stage = "original_restore_pending";
                try { SaveLease(lease); } catch { }
                throw;
            }
        }

        private static string SafePathPart(string value)
        {
            string text = String.IsNullOrWhiteSpace(value) ? "unknown" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                text = text.Replace(invalid, '_');
            return text.Length <= 48 ? text : text.Substring(0, 48);
        }

        private void PrepareTaskRoot(VideoTask task, DraftRootLeaseRecord lease,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(lease.TaskDraftRoot);
            DirectDraftCheckpoint checkpoint = RecoveryCheckpoint(task);
            if (checkpoint == null) {
                string[] existingEntries = Directory.GetFileSystemEntries(
                    lease.TaskDraftRoot);
                bool onlyEmptyIndex = existingEntries.All(path =>
                    String.Equals(Path.GetFileName(path), "root_meta_info.json",
                        StringComparison.OrdinalIgnoreCase));
                if (!onlyEmptyIndex) {
                    string retained = RetainIncompleteTaskRoot(lease, token);
                    logger.Warn("Retained an unverified interrupted task draft before a clean retry: " +
                        retained);
                }
                WriteEmptyIndex(lease.TaskDraftRoot);
                return;
            }

            string oldDirectory = Path.GetFullPath(checkpoint.DraftDirectory ?? "");
            if (!Directory.Exists(oldDirectory))
                throw new DirectoryNotFoundException(
                    "draft_checkpoint_directory_missing: " + oldDirectory);
            string destination = Path.Combine(lease.TaskDraftRoot, Path.GetFileName(oldDirectory));
            if (SamePath(oldDirectory, destination)) {
                RebindCheckpointToTaskRoot(task, lease);
                // Reconcile while CapCut's full index still contains both the
                // bootstrap card and the source-bound checkpoint.  Normalizing
                // first would discard the bootstrap index evidence needed for
                // a safe quarantine decision.
                ReconcileCheckpointDrafts(task, lease);
                NormalizeCheckpointForMountedPath(lease, checkpoint,
                    destination);
                return;
            }
            if (Directory.EnumerateFileSystemEntries(lease.TaskDraftRoot).Any())
                throw new InvalidDataException(
                    "draft_root_not_empty: cannot migrate a checkpoint into a non-empty task root");

            CopyDirectory(oldDirectory, destination, token);
            string mountedDestination = Path.Combine(lease.OriginalDraftRoot,
                Path.GetFileName(destination));
            // CapCut sees this physical folder through the fixed-path
            // junction. Advertising the backing path makes CapCut append the
            // same folder as a second draft with a new ID.
            RewriteCopiedTextPaths(destination, oldDirectory,
                mountedDestination);
            string checkpointIndexRoot = ResolveCheckpointIndexRoot(
                checkpoint, oldDirectory, lease);
            WriteSingleDraftIndex(checkpointIndexRoot,
                lease.TaskDraftRoot, checkpoint.DraftId,
                mountedDestination, lease.OriginalDraftRoot);
            checkpoint.DraftDirectory = destination;
            checkpoint.TaskDraftRoot = lease.TaskDraftRoot;
            checkpoint.DraftRootLeaseId = lease.LeaseId;
            checkpoint.PatchedFiles = MapPaths(checkpoint.PatchedFiles, oldDirectory, destination);
            SyncRecoveryCheckpoint(task, checkpoint);
        }

        private string RetainIncompleteTaskRoot(DraftRootLeaseRecord lease,
            CancellationToken token)
        {
            string retainedRoot = Path.Combine(rootsDirectory,
                "retained-incomplete",
                Path.GetFileName(lease.TaskDraftRoot) + "-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));
            Directory.CreateDirectory(retainedRoot);
            foreach (string entry in Directory.GetFileSystemEntries(
                lease.TaskDraftRoot)) {
                token.ThrowIfCancellationRequested();
                string destination = Path.Combine(retainedRoot,
                    Path.GetFileName(entry));
                if (Directory.Exists(entry)) Directory.Move(entry, destination);
                else File.Move(entry, destination);
            }
            var evidence = new Dictionary<string, object> {
                { "schemaVersion", 1 },
                { "retainedAt", DateTime.Now.ToString("o") },
                { "reason", "no_verified_direct_draft_checkpoint" },
                { "leaseId", lease.LeaseId ?? "" },
                { "taskIdentity", lease.TaskIdentity ?? "" },
                { "lastStage", lease.Stage ?? "" },
                { "sourceTaskRoot", lease.TaskDraftRoot ?? "" }
            };
            WriteJsonAtomic(Path.Combine(retainedRoot,
                "retained-incomplete.json"), evidence);
            return retainedRoot;
        }

        private void EnsureDraftRootJunction(DraftRootLeaseRecord lease)
        {
            ValidateLeasePaths(lease);
            string original = Path.GetFullPath(lease.OriginalDraftRoot);
            string taskRoot = Path.GetFullPath(lease.TaskDraftRoot);
            string protectedOriginal = Path.GetFullPath(
                lease.ProtectedOriginalDraftRoot);
            string markerName = lease.JunctionMarkerName;
            if (String.IsNullOrWhiteSpace(markerName))
                throw new InvalidDataException("draft_root_junction_marker_missing");
            string markerTarget = Path.Combine(taskRoot, markerName);
            File.WriteAllText(markerTarget, MarkerValue(lease),
                new UTF8Encoding(false));

            if (Directory.Exists(original) && IsReparsePoint(original)) {
                VerifyMountedJunction(lease);
                lease.JunctionMounted = true;
                SaveLease(lease);
                return;
            }
            if (Directory.Exists(protectedOriginal)) {
                if (Directory.Exists(original))
                    throw new InvalidOperationException(
                        "draft_root_protected_original_conflict: both the historical root and its protected copy exist");
            } else {
                if (!Directory.Exists(original))
                    throw new DirectoryNotFoundException(
                        "draft_root_original_missing: " + original);
                if (IsReparsePoint(original))
                    throw new InvalidOperationException(
                        "draft_root_unowned_reparse_point: refusing to replace an unknown junction");
                Directory.Move(original, protectedOriginal);
            }

            try {
                CreateDirectoryJunction(original, taskRoot);
                VerifyMountedJunction(lease);
            } catch {
                try {
                    if (Directory.Exists(original) && IsReparsePoint(original))
                        Directory.Delete(original);
                    if (!Directory.Exists(original) &&
                        Directory.Exists(protectedOriginal))
                        Directory.Move(protectedOriginal, original);
                } catch { }
                throw;
            }
            lease.JunctionMounted = true;
            SaveLease(lease);
            logger.Info("Mounted task-scoped CapCut draft root at the fixed CapCut path: " +
                lease.LeaseId);
        }

        private void RestoreOriginalDraftRoot(DraftRootLeaseRecord lease)
        {
            ValidateLeasePaths(lease);
            string original = Path.GetFullPath(lease.OriginalDraftRoot);
            string protectedOriginal = Path.GetFullPath(
                lease.ProtectedOriginalDraftRoot);

            if (Directory.Exists(original) && IsReparsePoint(original)) {
                VerifyMountedJunction(lease);
                Directory.Delete(original);
            } else if (Directory.Exists(original)) {
                if (Directory.Exists(protectedOriginal))
                    throw new InvalidOperationException(
                        "draft_root_restore_conflict: fixed path is not the owned junction");
                return;
            }
            if (Directory.Exists(original))
                throw new IOException(
                    "draft_root_junction_remove_failed: " + original);
            if (!Directory.Exists(protectedOriginal))
                throw new DirectoryNotFoundException(
                    "draft_root_protected_original_missing: " +
                    protectedOriginal);
            Directory.Move(protectedOriginal, original);
            if (!Directory.Exists(original) || IsReparsePoint(original))
                throw new InvalidDataException(
                    "draft_root_original_restore_verification_failed");
            logger.Info("Restored protected historical CapCut draft root: " +
                lease.LeaseId);
        }

        private static void VerifyMountedJunction(DraftRootLeaseRecord lease)
        {
            string original = Path.GetFullPath(lease.OriginalDraftRoot);
            string markerAtFixedPath = Path.Combine(original,
                lease.JunctionMarkerName ?? "");
            string markerAtTarget = Path.Combine(
                Path.GetFullPath(lease.TaskDraftRoot),
                lease.JunctionMarkerName ?? "");
            if (!Directory.Exists(original) || !IsReparsePoint(original) ||
                !File.Exists(markerAtFixedPath) || !File.Exists(markerAtTarget))
                throw new InvalidDataException(
                    "draft_root_junction_verification_failed");
            string fixedValue = File.ReadAllText(markerAtFixedPath, Encoding.UTF8);
            string targetValue = File.ReadAllText(markerAtTarget, Encoding.UTF8);
            if (!String.Equals(fixedValue, targetValue,
                StringComparison.Ordinal) ||
                !MarkerValueMatches(fixedValue, lease))
                throw new InvalidDataException(
                    "draft_root_junction_identity_mismatch");
        }

        private static void CreateDirectoryJunction(string linkPath,
            string targetPath)
        {
            if (Directory.Exists(linkPath) || File.Exists(linkPath))
                throw new IOException(
                    "draft_root_junction_path_not_empty: " + linkPath);
            var start = new ProcessStartInfo {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ??
                    "cmd.exe",
                Arguments = "/d /c mklink /J \"" + linkPath + "\" \"" +
                    targetPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process process = Process.Start(start)) {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(15000)) {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException(
                        "draft_root_junction_create_timeout");
                }
                if (process.ExitCode != 0)
                    throw new IOException(
                        "draft_root_junction_create_failed: " +
                        (String.IsNullOrWhiteSpace(error) ? output : error).Trim());
            }
        }

        private static bool IsReparsePoint(string path)
        {
            if (String.IsNullOrWhiteSpace(path) ||
                !Directory.Exists(path)) return false;
            return (File.GetAttributes(path) &
                FileAttributes.ReparsePoint) != 0;
        }

        private void RebindCheckpointToTaskRoot(VideoTask task, DraftRootLeaseRecord lease)
        {
            DirectDraftCheckpoint checkpoint = RecoveryCheckpoint(task);
            if (checkpoint == null) return;
            string currentDirectory = Path.GetFullPath(checkpoint.DraftDirectory ?? "");
            if (IsInside(currentDirectory, lease.TaskDraftRoot)) {
                checkpoint.TaskDraftRoot = lease.TaskDraftRoot;
                checkpoint.DraftRootLeaseId = lease.LeaseId;
                SyncRecoveryCheckpoint(task, checkpoint);
                return;
            }

            string rebound = Path.Combine(lease.TaskDraftRoot, Path.GetFileName(currentDirectory));
            if (!Directory.Exists(rebound))
                throw new DirectoryNotFoundException(
                    "draft_checkpoint_rebind_missing: " + rebound);
            checkpoint.PatchedFiles = MapPaths(checkpoint.PatchedFiles, currentDirectory, rebound);
            checkpoint.DraftDirectory = rebound;
            checkpoint.TaskDraftRoot = lease.TaskDraftRoot;
            checkpoint.DraftRootLeaseId = lease.LeaseId;
            SyncRecoveryCheckpoint(task, checkpoint);
        }

        private static DirectDraftCheckpoint RecoveryCheckpoint(VideoTask task)
        {
            if (task == null) return null;
            if (task.DirectDraftCheckpoint != null)
                return task.DirectDraftCheckpoint;
            CaptionDraftCheckpoint caption = task.CaptionDraftCheckpoint;
            if (caption != null) {
                return new DirectDraftCheckpoint {
                    State = "captions_saved",
                    DraftDirectory = caption.DraftDirectory,
                    DraftId = caption.DraftId,
                    DraftName = caption.DraftName,
                    SourceVideoPath = caption.SourceVideoPath,
                    CaptionCount = caption.CaptionCount,
                    PatchedFiles = caption.VerifiedFiles == null
                        ? new string[0] : caption.VerifiedFiles.ToArray(),
                    DraftRootLeaseId = caption.DraftRootLeaseId,
                    TaskDraftRoot = caption.TaskDraftRoot,
                    ArchiveStorageVersion = caption.ArchiveStorageVersion,
                    StableTaskArchiveKey = caption.StableTaskArchiveKey,
                    ArchiveDraftRoot = caption.ArchiveDraftRoot,
                    ArchiveManifestHash = caption.ArchiveManifestHash,
                    ArchiveVerifiedAt = caption.ArchiveVerifiedAt,
                    DraftRelativePath = caption.DraftRelativePath,
                    UpdatedAt = caption.UpdatedAt
                };
            }

            // Timeline assembly is the first durable draft checkpoint.  A
            // safe-continue attempt owns a new lease/root even when captions
            // have not been saved yet, so the verified draft must be migrated
            // just like caption/style checkpoints.  Treating it as "no
            // checkpoint" creates an empty root and makes recovery fail before
            // CapCut can reopen the already verified draft.
            TimelineAssemblyCheckpoint timeline =
                task.TimelineAssemblyCheckpoint;
            if (timeline == null ||
                (!String.Equals(timeline.State, "timeline_assembled",
                    StringComparison.OrdinalIgnoreCase) &&
                 !String.Equals(timeline.State, "video_timeline_committed",
                    StringComparison.OrdinalIgnoreCase) &&
                 !String.Equals(timeline.State, "audio_timeline_committed",
                    StringComparison.OrdinalIgnoreCase))) return null;
            return new DirectDraftCheckpoint {
                State = timeline.State,
                DraftDirectory = timeline.DraftDirectory,
                DraftId = timeline.DraftId,
                DraftName = timeline.DraftName,
                SourceVideoPath = timeline.MainVideoPath,
                CaptionCount = 0,
                PatchedFiles = timeline.VerifiedFiles == null
                    ? new string[0] : timeline.VerifiedFiles.ToArray(),
                DraftRootLeaseId = timeline.DraftRootLeaseId,
                TaskDraftRoot = timeline.TaskDraftRoot,
                ArchiveStorageVersion = timeline.ArchiveStorageVersion,
                StableTaskArchiveKey = timeline.StableTaskArchiveKey,
                ArchiveDraftRoot = timeline.ArchiveDraftRoot,
                ArchiveManifestHash = timeline.ArchiveManifestHash,
                ArchiveVerifiedAt = timeline.ArchiveVerifiedAt,
                DraftRelativePath = timeline.DraftRelativePath,
                UpdatedAt = timeline.UpdatedAt
            };
        }

        private static void SyncRecoveryCheckpoint(VideoTask task,
            DirectDraftCheckpoint checkpoint)
        {
            if (task == null || checkpoint == null) return;
            // A single CapCut draft advances through timeline, caption and
            // style checkpoints.  Safe-continue migrates that physical draft
            // to a new task-scoped lease.  Rebinding only the most advanced
            // checkpoint leaves the earlier checkpoints pointing at the old
            // lease, so the next identity check correctly (but unnecessarily)
            // reports a cross-draft conflict.  Rebind every checkpoint that
            // names the same draft as one atomic in-memory operation.
            DirectDraftCheckpoint direct = task.DirectDraftCheckpoint;
            if (direct != null && SameCheckpointDraft(direct.DraftId,
                    direct.DraftName, checkpoint)) {
                string previous = direct.DraftDirectory;
                direct.DraftDirectory = checkpoint.DraftDirectory;
                direct.DraftId = checkpoint.DraftId;
                direct.DraftName = checkpoint.DraftName;
                direct.PatchedFiles = RebaseCheckpointFiles(
                    direct.PatchedFiles, previous, checkpoint.DraftDirectory);
                direct.DraftRootLeaseId = checkpoint.DraftRootLeaseId;
                direct.TaskDraftRoot = checkpoint.TaskDraftRoot;
                direct.ArchiveStorageVersion = checkpoint.ArchiveStorageVersion;
                direct.StableTaskArchiveKey = checkpoint.StableTaskArchiveKey;
                direct.ArchiveDraftRoot = checkpoint.ArchiveDraftRoot;
                direct.ArchiveManifestHash = checkpoint.ArchiveManifestHash;
                direct.ArchiveVerifiedAt = checkpoint.ArchiveVerifiedAt;
                direct.DraftRelativePath = checkpoint.DraftRelativePath;
                direct.UpdatedAt = checkpoint.UpdatedAt;
            }
            CaptionDraftCheckpoint caption = task.CaptionDraftCheckpoint;
            if (caption != null && SameCheckpointDraft(caption.DraftId,
                    caption.DraftName, checkpoint)) {
                string previous = caption.DraftDirectory;
                caption.DraftDirectory = checkpoint.DraftDirectory;
                caption.DraftId = checkpoint.DraftId;
                caption.DraftName = checkpoint.DraftName;
                caption.VerifiedFiles = RebaseCheckpointFiles(
                    caption.VerifiedFiles, previous, checkpoint.DraftDirectory);
                caption.DraftRootLeaseId = checkpoint.DraftRootLeaseId;
                caption.TaskDraftRoot = checkpoint.TaskDraftRoot;
                caption.ArchiveStorageVersion = checkpoint.ArchiveStorageVersion;
                caption.StableTaskArchiveKey = checkpoint.StableTaskArchiveKey;
                caption.ArchiveDraftRoot = checkpoint.ArchiveDraftRoot;
                caption.ArchiveManifestHash = checkpoint.ArchiveManifestHash;
                caption.ArchiveVerifiedAt = checkpoint.ArchiveVerifiedAt;
                caption.DraftRelativePath = checkpoint.DraftRelativePath;
                caption.UpdatedAt = checkpoint.UpdatedAt;
            }

            TimelineAssemblyCheckpoint timeline =
                task.TimelineAssemblyCheckpoint;
            if (timeline != null && SameCheckpointDraft(timeline.DraftId,
                    timeline.DraftName, checkpoint)) {
                string previous = timeline.DraftDirectory;
                timeline.DraftDirectory = checkpoint.DraftDirectory;
                timeline.DraftId = checkpoint.DraftId;
                timeline.DraftName = checkpoint.DraftName;
                timeline.VerifiedFiles = RebaseCheckpointFiles(
                    timeline.VerifiedFiles, previous, checkpoint.DraftDirectory);
                timeline.DraftRootLeaseId = checkpoint.DraftRootLeaseId;
                timeline.TaskDraftRoot = checkpoint.TaskDraftRoot;
                timeline.ArchiveStorageVersion = checkpoint.ArchiveStorageVersion;
                timeline.StableTaskArchiveKey = checkpoint.StableTaskArchiveKey;
                timeline.ArchiveDraftRoot = checkpoint.ArchiveDraftRoot;
                timeline.ArchiveManifestHash = checkpoint.ArchiveManifestHash;
                timeline.ArchiveVerifiedAt = checkpoint.ArchiveVerifiedAt;
                timeline.DraftRelativePath = checkpoint.DraftRelativePath;
                timeline.UpdatedAt = checkpoint.UpdatedAt;
            }
        }

        private static void SetCheckpointArchiveFields(
            DirectDraftCheckpoint checkpoint, DraftRootLeaseRecord lease,
            string draftRelativePath)
        {
            if (checkpoint == null || lease == null) return;
            checkpoint.ArchiveStorageVersion = lease.ArchiveStorageVersion;
            checkpoint.StableTaskArchiveKey = lease.StableTaskArchiveKey;
            checkpoint.ArchiveDraftRoot = lease.ArchiveDraftRoot;
            checkpoint.ArchiveManifestHash = lease.ArchiveManifestHash;
            checkpoint.ArchiveVerifiedAt = lease.ArchiveVerifiedAt;
            checkpoint.DraftRelativePath = draftRelativePath ?? "";
        }

        private static void PersistArchiveCheckpoint(VideoTask task,
            DraftRootLeaseRecord lease)
        {
            if (task == null || lease == null) return;
            DirectDraftCheckpoint checkpoint = RecoveryCheckpoint(task);
            if (checkpoint == null) return;
            string relative = checkpoint.DraftRelativePath;
            if (String.IsNullOrWhiteSpace(relative) &&
                !String.IsNullOrWhiteSpace(checkpoint.DraftDirectory))
                relative = Path.GetFileName(checkpoint.DraftDirectory.TrimEnd(
                    '\\', '/'));
            SetCheckpointArchiveFields(checkpoint, lease, relative);
            SyncRecoveryCheckpoint(task, checkpoint);
        }

        private static bool SameCheckpointDraft(string draftId,
            string draftName, DirectDraftCheckpoint checkpoint)
        {
            if (checkpoint == null || String.IsNullOrWhiteSpace(draftId) ||
                String.IsNullOrWhiteSpace(checkpoint.DraftId)) return false;
            if (!String.Equals(draftId, checkpoint.DraftId,
                    StringComparison.OrdinalIgnoreCase)) return false;
            return String.IsNullOrWhiteSpace(draftName) ||
                String.IsNullOrWhiteSpace(checkpoint.DraftName) ||
                String.Equals(draftName, checkpoint.DraftName,
                    StringComparison.Ordinal);
        }

        private static string[] RebaseCheckpointFiles(IEnumerable<string> files,
            string previousDirectory, string currentDirectory)
        {
            if (String.IsNullOrWhiteSpace(previousDirectory) ||
                String.IsNullOrWhiteSpace(currentDirectory))
                return (files ?? new string[0]).ToArray();
            return MapPaths(files, Path.GetFullPath(previousDirectory),
                Path.GetFullPath(currentDirectory));
        }

        private void WriteEmptyIndex(string taskRoot)
        {
            var root = new Dictionary<string, object> {
                { "all_draft_store", new object[0] },
                { "draft_ids", 0 },
                { "root_path", taskRoot }
            };
            WriteJsonAtomic(Path.Combine(taskRoot, "root_meta_info.json"), root);
        }

        private void NormalizeCheckpointForMountedPath(
            DraftRootLeaseRecord lease, DirectDraftCheckpoint checkpoint,
            string destination)
        {
            string mountedDestination = Path.Combine(lease.OriginalDraftRoot,
                Path.GetFileName(destination));
            RewriteCopiedTextPaths(destination, lease.TaskDraftRoot,
                lease.OriginalDraftRoot);
            string sourceRoot = ResolveCheckpointIndexRoot(
                checkpoint, destination, lease);
            WriteSingleDraftIndex(sourceRoot, lease.TaskDraftRoot,
                checkpoint.DraftId, mountedDestination,
                lease.OriginalDraftRoot);
        }

        private string ResolveCheckpointIndexRoot(
            DirectDraftCheckpoint checkpoint, string draftDirectory,
            DraftRootLeaseRecord lease)
        {
            if (checkpoint == null)
                throw new ArgumentNullException("checkpoint");
            string draftId = checkpoint.DraftId ?? "";
            if (String.IsNullOrWhiteSpace(draftId))
                throw new InvalidDataException(
                    "draft_checkpoint_index_identity_missing: empty draft id");

            var candidates = new List<string>();
            AddCheckpointIndexCandidate(candidates,
                checkpoint.TaskDraftRoot);
            if (!String.IsNullOrWhiteSpace(draftDirectory)) {
                string fullDraftDirectory = Path.GetFullPath(draftDirectory);
                DirectoryInfo parent = Directory.GetParent(fullDraftDirectory);
                AddCheckpointIndexCandidate(candidates,
                    parent == null ? "" : parent.FullName);
            }
            AddCheckpointIndexCandidate(candidates,
                lease == null ? "" : lease.TaskDraftRoot);
            AddCheckpointIndexCandidate(candidates,
                lease == null ? "" : lease.OriginalDraftRoot);

            foreach (string candidate in candidates) {
                if (!CheckpointIndexContainsDraft(candidate, draftId))
                {
                    if (!CanRepairCheckpointIndex(candidate, checkpoint,
                            draftDirectory)) continue;
                    logger.Warn("CapCut rewrote the task-root index draft_id; " +
                        "the sole draft folder, internal draft_id, name and path still match. " +
                        "The index will be repaired from the verified checkpoint: " + candidate);
                }
                if (lease != null && !SamePath(candidate,
                        lease.OriginalDraftRoot)) {
                    logger.Info("Recovered checkpoint index from its task-scoped draft root: " +
                        candidate);
                }
                return candidate;
            }
            throw new InvalidDataException(
                "draft_checkpoint_index_identity_missing: " + draftId);
        }

        // CapCut 2026.7 may rewrite only root_meta_info.json with a new card ID
        // after the fixed draft path is mounted.  The draft folder itself keeps
        // the original ID captured by the verified style checkpoint.  Recover
        // only when every non-visual identity signal is unambiguous: the
        // candidate owns exactly one draft folder, that folder is the checkpoint
        // folder, its internal ID/name match, and the index has exactly one card
        // pointing at that same folder/name.  This is deliberately not a generic
        // "take the first card" fallback.
        private static bool CanRepairCheckpointIndex(string candidate,
            DirectDraftCheckpoint checkpoint, string draftDirectory)
        {
            if (checkpoint == null || String.IsNullOrWhiteSpace(candidate) ||
                String.IsNullOrWhiteSpace(draftDirectory) ||
                String.IsNullOrWhiteSpace(checkpoint.DraftId)) return false;
            try {
                string root = Path.GetFullPath(candidate);
                string draft = Path.GetFullPath(draftDirectory);
                if (!Directory.Exists(root) || !Directory.Exists(draft) ||
                    !IsInside(draft, root)) return false;
                string[] folders = Directory.GetDirectories(root)
                    .Where(path => !String.Equals(Path.GetFileName(path),
                        "Cache", StringComparison.OrdinalIgnoreCase))
                    .Where(LooksLikeDraftDirectory).ToArray();
                if (folders.Length != 1 || !SamePath(folders[0], draft))
                    return false;

                string metaPath = Path.Combine(draft, "draft_meta_info.json");
                string indexPath = Path.Combine(root, "root_meta_info.json");
                if (!File.Exists(metaPath) || !File.Exists(indexPath))
                    return false;
                Dictionary<string, object> meta = ReadJsonObject(metaPath);
                if (!String.Equals(Text(meta, "draft_id"),
                        checkpoint.DraftId, StringComparison.OrdinalIgnoreCase) ||
                    (!String.IsNullOrWhiteSpace(checkpoint.DraftName) &&
                     !String.Equals(Text(meta, "draft_name"),
                        checkpoint.DraftName, StringComparison.Ordinal)))
                    return false;

                Dictionary<string, object> index = ReadJsonObject(indexPath);
                object rawStores;
                object[] stores = index.TryGetValue("all_draft_store",
                    out rawStores) ? ToObjectArray(rawStores) : new object[0];
                if (stores.Length != 1) return false;
                Dictionary<string, object> store = stores[0] as Dictionary<string, object>;
                if (store == null) return false;
                string indexedName = Text(store, "draft_name");
                string indexedFolder = Text(store, "draft_fold_path");
                return (String.IsNullOrWhiteSpace(checkpoint.DraftName) ||
                        String.Equals(indexedName, checkpoint.DraftName,
                            StringComparison.Ordinal)) &&
                    !String.IsNullOrWhiteSpace(indexedFolder) &&
                    String.Equals(Path.GetFileName(indexedFolder.TrimEnd('\\', '/')),
                        Path.GetFileName(draft), StringComparison.OrdinalIgnoreCase);
            } catch { return false; }
        }

        private static void AddCheckpointIndexCandidate(
            IList<string> candidates, string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return;
            string full;
            try { full = Path.GetFullPath(value); }
            catch { return; }
            if (!candidates.Any(item => SamePath(item, full)))
                candidates.Add(full);
        }

        private static bool CheckpointIndexContainsDraft(
            string rootDirectory, string draftId)
        {
            if (String.IsNullOrWhiteSpace(rootDirectory) ||
                    String.IsNullOrWhiteSpace(draftId)) return false;
            string indexPath = Path.Combine(rootDirectory,
                "root_meta_info.json");
            if (!File.Exists(indexPath)) return false;
            try {
                Dictionary<string, object> root = ReadJsonObject(indexPath);
                object rawStores;
                object[] stores = root.TryGetValue("all_draft_store",
                    out rawStores) ? ToObjectArray(rawStores) : new object[0];
                return stores.Select(value =>
                        value as Dictionary<string, object>)
                    .Any(value => value != null && String.Equals(
                        Text(value, "draft_id"), draftId,
                        StringComparison.OrdinalIgnoreCase));
            } catch {
                return false;
            }
        }

        private void WriteSingleDraftIndex(string sourceRoot, string taskRoot,
            string draftId, string destination, string visibleRoot)
        {
            string sourceIndex = Path.Combine(sourceRoot, "root_meta_info.json");
            if (!File.Exists(sourceIndex))
                throw new FileNotFoundException(
                    "draft_checkpoint_index_missing: cannot prove checkpoint ownership", sourceIndex);
            Dictionary<string, object> root = ReadJsonObject(sourceIndex);
            object rawStores;
            object[] stores = root.TryGetValue("all_draft_store", out rawStores)
                ? ToObjectArray(rawStores) : new object[0];
            if (stores.Length > 1)
                logger.Warn("draft_root_duplicate_index: rebuilding the task index from the trusted checkpoint; previous entries=" +
                    stores.Length);
            Dictionary<string, object> match = stores
                .Select(value => value as Dictionary<string, object>)
                .SingleOrDefault(value => value != null &&
                    String.Equals(Text(value, "draft_id"), draftId,
                        StringComparison.OrdinalIgnoreCase));
            if (match == null && stores.Length == 1) {
                Dictionary<string, object> sole = stores[0] as Dictionary<string, object>;
                string indexedFolder = sole == null ? "" : Text(sole, "draft_fold_path");
                if (sole != null && !String.IsNullOrWhiteSpace(indexedFolder) &&
                    String.Equals(Path.GetFileName(indexedFolder.TrimEnd('\\', '/')),
                        Path.GetFileName(destination.TrimEnd('\\', '/')),
                        StringComparison.OrdinalIgnoreCase)) {
                    match = sole;
                    logger.Warn("Repaired CapCut's rewritten card ID from the verified internal draft checkpoint: " +
                        Text(sole, "draft_id") + " -> " + draftId);
                }
            }
            if (match == null) throw new InvalidDataException(
                "draft_checkpoint_index_identity_missing: " + draftId);

            Dictionary<string, object> copy = DeepClone(match);
            copy["draft_id"] = draftId;
            ReplaceKnownPath(copy, "draft_fold_path", destination);
            ReplaceKnownPath(copy, "draft_root_path", visibleRoot);
            root["all_draft_store"] = new object[] { copy };
            root["root_path"] = visibleRoot;
            WriteJsonAtomic(Path.Combine(taskRoot, "root_meta_info.json"), root);
        }

        private static void ReplaceKnownPath(Dictionary<string, object> value,
            string key, string replacement)
        {
            if (value == null || !value.ContainsKey(key)) return;
            value[key] = replacement;
        }

        private static void RewriteCopiedTextPaths(string root, string oldPath, string newPath)
        {
            string oldSlash = oldPath.Replace('\\', '/');
            string newSlash = newPath.Replace('\\', '/');
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) {
                string extension = Path.GetExtension(file);
                if (!String.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                string text;
                try { text = File.ReadAllText(file, Encoding.UTF8); }
                catch { continue; }
                string rewritten = text.Replace(oldPath, newPath)
                    .Replace(oldSlash, newSlash)
                    .Replace(oldPath.Replace("\\", "\\\\"), newPath.Replace("\\", "\\\\"));
                if (!String.Equals(text, rewritten, StringComparison.Ordinal))
                    File.WriteAllText(file, rewritten, new UTF8Encoding(false));
            }
        }

        private static string UniqueShortChild(string parent, string prefix)
        {
            Directory.CreateDirectory(parent);
            string safePrefix = String.IsNullOrWhiteSpace(prefix) ? "x-" : prefix;
            string stamp = DateTime.Now.ToString("yyMMddHHmmss");
            for (int index = 0; index < 1000; index++) {
                string suffix = index == 0 ? stamp : stamp + "-" + index;
                string candidate = Path.Combine(parent, safePrefix + suffix);
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                    return candidate;
            }
            throw new IOException("CAPCUT_DRAFT_ARCHIVE_STAGING_INCOMPLETE: unable to allocate a short archive path");
        }

        private static string UniqueShortFile(string parent, string prefix,
            string extension)
        {
            Directory.CreateDirectory(parent);
            string safePrefix = String.IsNullOrWhiteSpace(prefix) ? "x-" : prefix;
            string safeExtension = String.IsNullOrWhiteSpace(extension)
                ? "" : extension;
            string stamp = DateTime.Now.ToString("yyMMddHHmmss");
            for (int index = 0; index < 1000; index++) {
                string suffix = index == 0 ? stamp : stamp + "-" + index;
                string candidate = Path.Combine(parent,
                    safePrefix + suffix + safeExtension);
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                    return candidate;
            }
            throw new IOException("CAPCUT_DRAFT_ARCHIVE_STAGING_INCOMPLETE: unable to allocate a short archive file");
        }

        private static string UniqueBootstrapDirectory(string parent,
            string draftHash)
        {
            Directory.CreateDirectory(parent);
            string hash = String.IsNullOrWhiteSpace(draftHash)
                ? "unknown" : draftHash;
            for (int index = 1; index < 1000; index++) {
                string candidate = Path.Combine(parent,
                    "b-" + hash + "-" + index);
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                    return candidate;
            }
            throw new IOException("CAPCUT_DRAFT_ARCHIVE_STAGING_INCOMPLETE: unable to allocate a short bootstrap quarantine path");
        }

        private static bool IsRecycleRelative(string relative)
        {
            return String.Equals(relative, ".recycle_bin",
                    StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(".recycle_bin\\",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateCopyPathLengths(string source,
            string destination)
        {
            const int safeDirectoryLimit = 240;
            const int safeFileLimit = 252;
            string sourceRoot = source.TrimEnd('\\');
            if (destination.Length >= safeDirectoryLimit)
                throw new InvalidDataException(
                    "CAPCUT_DRAFT_ARCHIVE_PATH_TOO_LONG: destination directory length " +
                    destination.Length);
            try {
                foreach (string directory in Directory.GetDirectories(source,
                        "*", SearchOption.AllDirectories)) {
                    string relative = directory.Substring(sourceRoot.Length)
                        .TrimStart('\\');
                    if (IsRecycleRelative(relative)) continue;
                    string target = Path.Combine(destination, relative);
                    if (target.Length >= safeDirectoryLimit)
                        throw new InvalidDataException(
                            "CAPCUT_DRAFT_ARCHIVE_PATH_TOO_LONG: directory " +
                            target.Length + " chars");
                }
                foreach (string file in Directory.GetFiles(source, "*",
                        SearchOption.AllDirectories)) {
                    string relative = file.Substring(sourceRoot.Length)
                        .TrimStart('\\');
                    if (IsRecycleRelative(relative)) continue;
                    string target = Path.Combine(destination, relative);
                    if (target.Length >= safeFileLimit)
                        throw new InvalidDataException(
                            "CAPCUT_DRAFT_ARCHIVE_PATH_TOO_LONG: file " +
                            target.Length + " chars");
                }
            } catch (InvalidDataException) { throw; }
            catch (Exception ex) {
                throw new InvalidDataException(
                    "CAPCUT_DRAFT_ARCHIVE_PATH_TOO_LONG: unable to inspect source paths: " +
                    ex.Message, ex);
            }
        }

        private static void ValidateArchiveCopy(string source, string destination)
        {
            ValidateCopyPathLengths(source, destination);
            string sourceRoot = source.TrimEnd('\\');
            string destinationRoot = destination.TrimEnd('\\');
            var sourceFiles = Directory.GetFiles(source, "*",
                    SearchOption.AllDirectories)
                .Where(path => !IsRecycleRelative(path.Substring(
                    sourceRoot.Length).TrimStart('\\')))
                .ToDictionary(path => path.Substring(sourceRoot.Length)
                    .TrimStart('\\'), StringComparer.OrdinalIgnoreCase);
            var destinationFiles = Directory.GetFiles(destination, "*",
                    SearchOption.AllDirectories)
                .ToDictionary(path => path.Substring(destinationRoot.Length)
                    .TrimStart('\\'), StringComparer.OrdinalIgnoreCase);
            if (sourceFiles.Count != destinationFiles.Count ||
                sourceFiles.Keys.Except(destinationFiles.Keys,
                    StringComparer.OrdinalIgnoreCase).Any())
                throw new InvalidDataException(
                    "CAPCUT_DRAFT_ARCHIVE_STAGING_INCOMPLETE: archive file set differs from active draft");
            foreach (KeyValuePair<string, string> item in sourceFiles) {
                string target;
                if (!destinationFiles.TryGetValue(item.Key, out target) ||
                    new FileInfo(item.Value).Length != new FileInfo(target).Length)
                    throw new InvalidDataException(
                        "CAPCUT_DRAFT_ARCHIVE_VERIFY_FAILED: file size mismatch for " +
                        item.Key);
            }
        }

        private static string ComputeArchiveManifestHash(string root)
        {
            using (var sha = SHA256.Create()) {
                var builder = new StringBuilder();
                foreach (string file in Directory.GetFiles(root, "*",
                        SearchOption.AllDirectories)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)) {
                    string relative = file.Substring(root.TrimEnd('\\').Length)
                        .TrimStart('\\');
                    if (IsRecycleRelative(relative)) continue;
                    builder.Append(relative).Append('|')
                        .Append(new FileInfo(file).Length).Append('|');
                    if (String.Equals(Path.GetExtension(file), ".json",
                            StringComparison.OrdinalIgnoreCase))
                        builder.Append(Convert.ToBase64String(sha.ComputeHash(
                            File.ReadAllBytes(file))));
                    builder.AppendLine();
                }
                byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
                return BitConverter.ToString(sha.ComputeHash(bytes))
                    .Replace("-", "").ToLowerInvariant();
            }
        }

        private static void CopyDirectory(string source, string destination,
            CancellationToken token)
        {
            ValidateCopyPathLengths(source, destination);
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.GetDirectories(source, "*",
                SearchOption.AllDirectories)) {
                token.ThrowIfCancellationRequested();
                string relative = directory.Substring(source.TrimEnd('\\').Length)
                    .TrimStart('\\');
                if (IsRecycleRelative(relative)) continue;
                Directory.CreateDirectory(Path.Combine(destination,
                    relative));
            }
            foreach (string file in Directory.GetFiles(source, "*",
                SearchOption.AllDirectories)) {
                token.ThrowIfCancellationRequested();
                string relative = file.Substring(source.TrimEnd('\\').Length).TrimStart('\\');
                if (IsRecycleRelative(relative)) continue;
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, false);
            }
        }

        /// <summary>
        /// Preserve diagnostic directories without assuming source and target
        /// are on the same Windows volume. Directory.Move throws when the
        /// active CapCut root is on C: and evidence lives on E:. A cross-volume
        /// transfer is therefore committed as copy -> verify -> delete, while
        /// same-volume moves keep their atomic rename semantics.
        /// </summary>
        private static void MoveDirectoryAcrossVolumesVerified(string source,
            string destination)
        {
            string sourceFull = Path.GetFullPath(source);
            string destinationFull = Path.GetFullPath(destination);
            if (!Directory.Exists(sourceFull)) return;
            if (Directory.Exists(destinationFull) || File.Exists(destinationFull))
                throw new IOException("evidence destination already exists: " + destinationFull);

            string sourceRoot = Path.GetPathRoot(sourceFull) ?? "";
            string destinationRoot = Path.GetPathRoot(destinationFull) ?? "";
            if (String.Equals(sourceRoot, destinationRoot,
                    StringComparison.OrdinalIgnoreCase)) {
                Directory.Move(sourceFull, destinationFull);
                return;
            }

            CopyDirectoryExactForEvidence(sourceFull, destinationFull);
            VerifyDirectoryExactForEvidence(sourceFull, destinationFull);
            Directory.Delete(sourceFull, true);
        }

        private static void CopyDirectoryExactForEvidence(string source,
            string destination)
        {
            ValidateCopyPathLengths(source, destination);
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.GetDirectories(source, "*",
                    SearchOption.AllDirectories)) {
                string relative = directory.Substring(source.TrimEnd('\\').Length)
                    .TrimStart('\\');
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }
            foreach (string file in Directory.GetFiles(source, "*",
                    SearchOption.AllDirectories)) {
                string relative = file.Substring(source.TrimEnd('\\').Length)
                    .TrimStart('\\');
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, false);
            }
        }

        private static void VerifyDirectoryExactForEvidence(string source,
            string destination)
        {
            string sourcePrefix = source.TrimEnd('\\');
            string destinationPrefix = destination.TrimEnd('\\');
            string[] sourceFiles = Directory.GetFiles(source, "*",
                    SearchOption.AllDirectories)
                .Select(path => path.Substring(sourcePrefix.Length).TrimStart('\\'))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] destinationFiles = Directory.GetFiles(destination, "*",
                    SearchOption.AllDirectories)
                .Select(path => path.Substring(destinationPrefix.Length).TrimStart('\\'))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!sourceFiles.SequenceEqual(destinationFiles,
                    StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("cross-volume evidence file set mismatch");
            foreach (string relative in sourceFiles) {
                string sourceFile = Path.Combine(source, relative);
                string destinationFile = Path.Combine(destination, relative);
                if (new FileInfo(sourceFile).Length != new FileInfo(destinationFile).Length)
                    throw new InvalidDataException(
                        "cross-volume evidence file size mismatch: " + relative);
                using (var sha = SHA256.Create()) {
                    byte[] sourceHash = sha.ComputeHash(File.ReadAllBytes(sourceFile));
                    byte[] destinationHash = sha.ComputeHash(File.ReadAllBytes(destinationFile));
                    if (!sourceHash.SequenceEqual(destinationHash))
                        throw new InvalidDataException(
                            "cross-volume evidence file hash mismatch: " + relative);
                }
            }
        }

        private static string[] MapPaths(IEnumerable<string> paths,
            string oldRoot, string newRoot)
        {
            return (paths ?? new string[0]).Select(path => {
                string full = Path.GetFullPath(path);
                if (!IsInside(full, oldRoot)) return full;
                string relative = full.Substring(
                    Path.GetFullPath(oldRoot).TrimEnd('\\').Length).TrimStart('\\');
                return Path.Combine(newRoot, relative);
            }).ToArray();
        }

        public static string BuildTaskIdentity(VideoTask task)
        {
            if (task == null) throw new ArgumentNullException("task");
            return String.Join("|", new[] {
                task.HandoffBatchId ?? "",
                StableCanonicalTaskId(task),
                task.ProductionDate ?? "",
                task.HandoffManifestId ?? "",
                task.HandoffAttemptId ?? "",
                task.Id ?? ""
            });
        }

        // Stable identity deliberately excludes manifest/attempt/command
        // identifiers.  Those values describe one execution attempt, while
        // the archived CapCut draft belongs to the immutable media task.
        public static string BuildStableTaskArchiveKey(VideoTask task)
        {
            if (task == null) throw new ArgumentNullException("task");
            // CapCutTaskIdentity is the single source of truth for the stable
            // archive key.  Keeping this compatibility method as a delegating
            // wrapper prevents the lease manager and transaction store from
            // deriving different keys for the same task.
            return CapCutTaskIdentity.FromTask(task).StableTaskArchiveKey;
        }

        private static string BuildStableKeyFromLease(DraftRootLeaseRecord lease)
        {
            if (lease == null) return "";
            // Legacy v3/v4 leases did not persist the source path.  Keep a
            // deterministic compatibility key so their history can still be
            // matched by canonical/local task identity during migration.
            return String.Join("|", new[] {
                lease.CanonicalTaskId ?? "",
                lease.ProductionDate ?? "",
                lease.LocalTaskId ?? LocalTaskIdFromIdentity(lease.TaskIdentity),
                ""
            });
        }

        private static string NormalizeIdentityPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return "";
            try {
                return Path.GetFullPath(path).TrimEnd('\\', '/')
                    .Replace('/', '\\').ToLowerInvariant();
            } catch { return path.Trim().Replace('/', '\\').ToLowerInvariant(); }
        }

        private static string MarkerValue(DraftRootLeaseRecord lease)
        {
            return !String.IsNullOrWhiteSpace(lease.StableTaskArchiveKey)
                ? lease.StableTaskArchiveKey : lease.TaskIdentity ?? "";
        }

        private static bool MarkerMatchesLease(string markerPath,
            DraftRootLeaseRecord lease)
        {
            if (lease == null || String.IsNullOrWhiteSpace(markerPath) ||
                !File.Exists(markerPath)) return false;
            try {
                return MarkerValueMatches(File.ReadAllText(markerPath,
                    Encoding.UTF8), lease);
            } catch { return false; }
        }

        private static bool MarkerValueMatches(string value,
            DraftRootLeaseRecord lease)
        {
            string text = (value ?? "").Trim();
            if (String.Equals(text, lease.TaskIdentity ?? "",
                    StringComparison.Ordinal) ||
                String.Equals(text, lease.StableTaskArchiveKey ?? "",
                    StringComparison.Ordinal)) return true;
            // Accept a legacy attempt marker while reclaiming the same task.
            string[] parts = text.Split('|');
            if (parts.Length < 6) return false;
            string local = parts[parts.Length - 1];
            return String.Equals(parts[1], lease.CanonicalTaskId ?? "",
                    StringComparison.Ordinal) &&
                String.Equals(parts[2], lease.ProductionDate ?? "",
                    StringComparison.Ordinal) &&
                String.Equals(local, lease.LocalTaskId ?? "",
                    StringComparison.Ordinal);
        }

        private static string StableCanonicalTaskId(VideoTask task)
        {
            return !String.IsNullOrWhiteSpace(task.RemotePipelineTaskId)
                ? task.RemotePipelineTaskId : task.Id ?? "";
        }

        private static string ShortHash(string value, int characters)
        {
            using (SHA256 sha = SHA256.Create()) {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
                var text = new StringBuilder();
                foreach (byte item in hash) text.Append(item.ToString("x2"));
                return text.ToString(0, Math.Min(characters, text.Length));
            }
        }

        public static string ResolveGlobalSettingPath(AppConfig config)
        {
            string product = config != null &&
                !String.IsNullOrWhiteSpace(config.CapCutPath) &&
                config.CapCutPath.IndexOf("JianyingPro",
                    StringComparison.OrdinalIgnoreCase) >= 0
                ? "JianyingPro" : "CapCut";
            return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                product, "User Data", "Config", "globalSetting");
        }

        public static string ResolveDefaultDraftRoot(AppConfig config)
        {
            if (config != null && !String.IsNullOrWhiteSpace(config.DraftRoot))
                return Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(config.DraftRoot));
            string product = config != null &&
                !String.IsNullOrWhiteSpace(config.CapCutPath) &&
                config.CapCutPath.IndexOf("JianyingPro",
                    StringComparison.OrdinalIgnoreCase) >= 0
                ? "JianyingPro" : "CapCut";
            return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                product, "User Data", "Projects", "com.lveditor.draft");
        }

        public static string ReadCustomDraftPath(string settingsPath)
        {
            foreach (string line in File.ReadAllLines(settingsPath, Encoding.UTF8)) {
                if (!line.StartsWith(SettingKey,
                    StringComparison.OrdinalIgnoreCase)) continue;
                return DecodeSettingPath(line.Substring(SettingKey.Length));
            }
            return "";
        }

        public static void SetCustomDraftPath(string settingsPath, string path)
        {
            string[] lines = File.ReadAllLines(settingsPath, Encoding.UTF8);
            string replacement = SettingKey + EncodeSettingPath(Path.GetFullPath(path));
            bool found = false;
            for (int index = 0; index < lines.Length; index++) {
                if (!lines[index].StartsWith(SettingKey,
                    StringComparison.OrdinalIgnoreCase)) continue;
                lines[index] = replacement;
                found = true;
                break;
            }
            if (!found) {
                var list = lines.ToList();
                list.Add(replacement);
                lines = list.ToArray();
            }
            AtomicWriteBytes(settingsPath,
                new UTF8Encoding(false).GetBytes(String.Join(Environment.NewLine, lines)));
            string actual = ReadCustomDraftPath(settingsPath);
            if (!SamePath(actual, path))
                throw new InvalidDataException("draft_root_setting_write_mismatch");
        }

        private static string EncodeSettingPath(string value)
        {
            return (value ?? "").Replace("\\", "\\\\");
        }

        private static string DecodeSettingPath(string value)
        {
            return (value ?? "").Trim().Trim('"')
                .Replace("\\\\", "\\").Replace('/', '\\');
        }

        private static bool LooksLikeDraftDirectory(string path)
        {
            return File.Exists(Path.Combine(path, "draft_meta_info.json")) ||
                File.Exists(Path.Combine(path, "draft_content.json")) ||
                Directory.Exists(Path.Combine(path, "Timelines"));
        }

        private void ValidateLeasePaths(DraftRootLeaseRecord lease)
        {
            if (lease == null ||
                String.IsNullOrWhiteSpace(lease.GlobalSettingPath) ||
                String.IsNullOrWhiteSpace(lease.OriginalDraftRoot) ||
                String.IsNullOrWhiteSpace(lease.ProtectedOriginalDraftRoot) ||
                !SameParent(lease.OriginalDraftRoot,
                    lease.ProtectedOriginalDraftRoot))
                throw new InvalidDataException("draft_root_lease_path_invalid");

            if (IsLocalStorage(lease)) {
                // The active path is CapCut's fixed C: path.  Only the durable
                // archive remains under the task-roots directory; accepting a
                // junction or an archive outside that directory would defeat
                // the storage-mode boundary.
                bool archiveUnderShortRoot =
                    !String.IsNullOrWhiteSpace(lease.ShortArchiveRoot) &&
                    IsInside(lease.ArchiveDraftRoot, lease.ShortArchiveRoot);
                bool archiveUnderLegacyRoot =
                    IsInside(lease.ArchiveDraftRoot, rootsDirectory);
                if (String.IsNullOrWhiteSpace(lease.ActiveDraftRoot) ||
                    String.IsNullOrWhiteSpace(lease.ArchiveDraftRoot) ||
                    !SamePath(lease.ActiveDraftRoot, lease.OriginalDraftRoot) ||
                    !SamePath(lease.TaskDraftRoot, lease.ActiveDraftRoot) ||
                    (!archiveUnderShortRoot && !archiveUnderLegacyRoot))
                    throw new InvalidDataException("draft_root_lease_path_invalid");
                return;
            }

            if (!IsInside(lease.TaskDraftRoot, rootsDirectory))
                throw new InvalidDataException("draft_root_lease_path_invalid");
        }

        private static bool SameParent(string left, string right)
        {
            if (String.IsNullOrWhiteSpace(left) ||
                String.IsNullOrWhiteSpace(right)) return false;
            return String.Equals(
                Path.GetDirectoryName(Path.GetFullPath(left).TrimEnd('\\')),
                Path.GetDirectoryName(Path.GetFullPath(right).TrimEnd('\\')),
                StringComparison.OrdinalIgnoreCase);
        }

        private void SaveLease(DraftRootLeaseRecord lease)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
            AtomicWriteBytes(leaseFile,
                new UTF8Encoding(false).GetBytes(serializer.Serialize(lease)));
        }

        private void WriteLeaseHistory(DraftRootLeaseRecord lease)
        {
            string history = Path.Combine(Path.GetDirectoryName(leaseFile), "draft-root-lease-history");
            Directory.CreateDirectory(history);
            string file = Path.Combine(history,
                DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + "-" + lease.LeaseId + ".json");
            var serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
            DurableFile.WriteAllText(file, serializer.Serialize(lease), new UTF8Encoding(false));
        }

        public static DraftRootLeaseRecord LoadLease(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
            return serializer.Deserialize<DraftRootLeaseRecord>(
                File.ReadAllText(path, Encoding.UTF8));
        }

        private static Dictionary<string, object> ReadJsonObject(string path)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
            Dictionary<string, object> value = serializer.DeserializeObject(
                File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            if (value == null) throw new InvalidDataException("invalid JSON object: " + path);
            return value;
        }

        private static Dictionary<string, object> DeepClone(
            Dictionary<string, object> value)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
            return serializer.DeserializeObject(serializer.Serialize(value))
                as Dictionary<string, object>;
        }

        private static object[] ToObjectArray(object value)
        {
            object[] array = value as object[];
            if (array != null) return array;
            ArrayList list = value as ArrayList;
            return list == null ? new object[0] : list.Cast<object>().ToArray();
        }

        private static string Text(Dictionary<string, object> value, string key)
        {
            object result;
            return value != null && value.TryGetValue(key, out result) && result != null
                ? Convert.ToString(result).Trim() : "";
        }

        private static void WriteJsonAtomic(string path,
            Dictionary<string, object> value)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
            AtomicWriteBytes(path,
                new UTF8Encoding(false).GetBytes(serializer.Serialize(value)));
        }

        private static void RestoreExactFile(string backup, string destination)
        {
            AtomicWriteBytes(destination, File.ReadAllBytes(backup));
        }

        private static void AtomicWriteBytes(string destination, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            DurableFile.WriteAllBytes(temporary, bytes);
            try {
                if (File.Exists(destination)) {
                    string replaceBackup = destination + ".replace-backup";
                    try {
                        File.Replace(temporary, destination, replaceBackup, true);
                    } finally {
                        try { if (File.Exists(replaceBackup)) File.Delete(replaceBackup); } catch { }
                    }
                } else {
                    File.Move(temporary, destination);
                }
            } finally {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static bool IsInside(string path, string root)
        {
            if (String.IsNullOrWhiteSpace(path) || String.IsNullOrWhiteSpace(root)) return false;
            string fullPath = Path.GetFullPath(path).TrimEnd('\\');
            string fullRoot = Path.GetFullPath(root).TrimEnd('\\');
            return String.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(fullRoot + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SamePath(string left, string right)
        {
            if (String.IsNullOrWhiteSpace(left) || String.IsNullOrWhiteSpace(right)) return false;
            return String.Equals(Path.GetFullPath(left).TrimEnd('\\'),
                Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }
}

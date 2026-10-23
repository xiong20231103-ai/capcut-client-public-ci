using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace CapCutBatchClient
{
    public sealed class HandoffImportResult
    {
        public int Added { get; set; }
        public int Skipped { get; set; }
        public List<string> Issues { get; private set; }

        public HandoffImportResult()
        {
            Issues = new List<string>();
        }
    }

    public static class HandoffManifestImporter
    {
        public static HandoffImportResult Import(string manifestPath, AppConfig config, IList<VideoTask> existing)
        {
            if (String.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
                throw new FileNotFoundException("找不到工作台交接清单", manifestPath);

            var serializer = new JavaScriptSerializer { MaxJsonLength = 20 * 1024 * 1024 };
            var root = serializer.DeserializeObject(File.ReadAllText(manifestPath, Encoding.UTF8))
                as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("交接清单格式无效");
            string schemaVersion = Text(root, "schemaVersion");
            if (schemaVersion != "1" && schemaVersion != "2")
                throw new InvalidDataException("Unsupported handoff manifest schema: " + schemaVersion);

            string batchId = Text(root, "batchId");
            string manifestId = Text(root, "manifestId");
            string attemptId = Text(root, "attemptId");
            string productionDate = Text(root, "productionDate");
            if (String.IsNullOrWhiteSpace(manifestId)) manifestId = Path.GetFileNameWithoutExtension(manifestPath);
            if (String.IsNullOrWhiteSpace(attemptId)) attemptId = batchId;
            string statusFile = Text(root, "statusFile");
            string remotePipelineTaskId = Text(root, "remotePipelineTaskId");
            bool releaseOnly = Bool(root, "releaseOnly");
            int expectedTaskCount = Number(root, "expectedTaskCount", 0);
            int projectExpectedCount = Number(root, "projectExpectedCount", expectedTaskCount);
            int attemptTaskCount = Number(root, "attemptTaskCount", 0);
            List<string> projectTaskIds = StringList(root, "projectTaskIds");
            List<string> completedTaskIds = StringList(root, "completedTaskIds");
            if (String.IsNullOrWhiteSpace(statusFile))
                statusFile = Path.ChangeExtension(Path.GetFullPath(manifestPath), ".status.json");

            object rawTasks;
            var entries = root.TryGetValue("tasks", out rawTasks) ? rawTasks as object[] : null;
            if (entries == null) {
                var list = rawTasks as ArrayList;
                if (list != null) entries = list.Cast<object>().ToArray();
            }
            if (entries == null) throw new InvalidDataException("交接清单没有 tasks 数组");

            var incomingIds = new HashSet<string>(entries
                .Select(value => value as Dictionary<string, object>)
                .Where(value => value != null)
                .Select(value => Text(value, "taskId"))
                .Where(value => !String.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            var incomingPairs = new HashSet<string>(entries
                .Select(value => value as Dictionary<string, object>)
                .Where(value => value != null)
                .Select(value => PairKey(FullPath(Text(value, "videoPath")),
                    FullPath(Text(value, "finalOutputPath")))),
                StringComparer.OrdinalIgnoreCase);

            // This executable keeps its local queue between runs. A new
            // workbench batch must not republish terminal tasks from an older
            // manifest into their old status file, otherwise a retry appears
            // to affect unrelated projects. Keep manual items and any live
            // current-batch work intact; discard only finished stale handoffs.
            for (int index = existing.Count - 1; index >= 0; index--) {
                VideoTask candidate = existing[index];
                if (!candidate.IsHandoffTask ||
                    String.Equals(candidate.HandoffBatchId, batchId, StringComparison.OrdinalIgnoreCase)) continue;
                TaskState state = candidate.GetState();
                // A Dashboard retry may create a fresh batch/manifest ID for
                // the same durable media task. Keep its failed queue object
                // until the incoming entry has inherited the verified draft or
                // export checkpoint. Otherwise every retry rebuilds a new
                // draft and repeats all upstream UI operations.
                bool incomingRecovery = state == TaskState.Failed &&
                    (incomingIds.Contains(candidate.Id ?? "") ||
                     incomingPairs.Contains(PairKey(candidate.VideoPath, candidate.OutputPath)));
                if (incomingRecovery) continue;
                if (state == TaskState.Completed || state == TaskState.Failed) existing.RemoveAt(index);
            }

            var result = new HandoffImportResult();
            var existingById = existing
                .Where(item => !String.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var existingPairs = new HashSet<string>(
                existing.Select(item => PairKey(item.VideoPath, item.OutputPath)),
                StringComparer.OrdinalIgnoreCase);
            var samples = new Dictionary<string, List<MediaMetadata>>(StringComparer.OrdinalIgnoreCase);

            foreach (object raw in entries) {
                var item = raw as Dictionary<string, object>;
                if (item == null) {
                    result.Issues.Add("发现无法读取的任务记录");
                    continue;
                }
                string id = Text(item, "taskId");
                string video = FullPath(Text(item, "videoPath"));
                string audio = FullPath(Text(item, "audioPath"));
                string output = FullPath(Text(item, "finalOutputPath"));
                string project = Text(item, "project");
                string material = Text(item, "material");
                string language = Text(item, "language");
                string taskNumber = Text(item, "number");
                int sequence = Number(item, "sequence", 1);
                string captionPlacement = Text(item, "captionPlacement");
                string subtitleTreatment = Text(item, "subtitleTreatment");
                string explicitSamplePath = FullPath(Text(item, "originalSamplePath"));
                string canonicalTaskId = Text(item, "canonicalTaskId");
                string runGroupId = Text(item, "runGroupId");
                string contractBatchId = Text(item, "batchId");
                string batchKind = Text(item, "batchKind");
                if (String.IsNullOrWhiteSpace(canonicalTaskId)) canonicalTaskId = id;
                if (String.IsNullOrWhiteSpace(batchKind) &&
                    String.Equals(project, "ExampleBrand", StringComparison.OrdinalIgnoreCase))
                    batchKind = "examplebrand-natural-flow";
                double captionTargetY = DecimalNumber(item, "captionTargetY", 0.0);
                bool itemReleaseOnly = releaseOnly || Bool(item, "releaseOnly");

                var errors = new List<string>();
                if (String.IsNullOrWhiteSpace(id)) errors.Add("缺少 taskId");
                if (!CategoryCatalog.IsKnownCategory(project)) errors.Add("未知项目：" + project);
                if (!CategoryCatalog.IsMaterialAllowed(project, material)) errors.Add("材料与项目不匹配：" + material);
                if (String.IsNullOrWhiteSpace(language)) errors.Add("缺少语言");
                if (String.IsNullOrWhiteSpace(video) || !File.Exists(video)) errors.Add("视频不存在");
                if (String.IsNullOrWhiteSpace(audio) || !File.Exists(audio)) errors.Add("音频不存在");
                // A workbench handoff has an explicit durable task identity.
                // Its audio may be copied from a task-scoped folder and therefore
                // deliberately omit the legacy CapCut sequence suffix.  Do not
                // reject that authoritative pairing by filename.  Manual imports
                // still use the filename guard because they have no task owner.
                else if (String.IsNullOrWhiteSpace(remotePipelineTaskId) && !IsTaskNumberCompatible(audio, taskNumber))
                    errors.Add("音频编号与任务编号不一致");
                if (String.IsNullOrWhiteSpace(output)) errors.Add("缺少最终输出路径");
                else if (File.Exists(output) && !itemReleaseOnly) errors.Add("最终成片已存在，已阻止覆盖");
                if (!String.IsNullOrWhiteSpace(explicitSamplePath) && !File.Exists(explicitSamplePath))
                    errors.Add("任务专属结尾素材不存在");
                if (errors.Count > 0) {
                    string message = String.Join("；", errors.ToArray());
                    result.Issues.Add((String.IsNullOrWhiteSpace(id) ? "未编号任务" : id) + "：" + message);
                    RecordRejected(existing, existingById, id, video, audio, output, project, material,
                        language, taskNumber, sequence, batchId, productionDate, statusFile, remotePipelineTaskId, expectedTaskCount,
                        projectExpectedCount, attemptTaskCount, projectTaskIds, completedTaskIds, message);
                    continue;
                }
                VideoTask previous;
                existingById.TryGetValue(id, out previous);
                if (previous == null) {
                    previous = existing.FirstOrDefault(candidate =>
                        candidate.GetState() != TaskState.Completed &&
                        SameTaskPath(candidate.VideoPath, video) &&
                        SameTaskPath(candidate.OutputPath, output));
                }
                if (previous == null && existingPairs.Contains(PairKey(video, output))) {
                    result.Skipped++;
                    continue;
                }
                if (previous != null && previous.GetState() == TaskState.Completed && !itemReleaseOnly) {
                    result.Skipped++;
                    continue;
                }

                try {
                    string samplePath = "";
                    double sampleSpeed = 1.0;
                    SampleAssignment sampleAssignment = null;
                    if (!releaseOnly) {
                        if (!String.IsNullOrWhiteSpace(explicitSamplePath)) {
                            samplePath = explicitSamplePath;
                            sampleSpeed = 1.0;
                            sampleAssignment = CreateSampleAssignment(samplePath, sampleSpeed,
                                video, canonicalTaskId, id, "");
                        } else {
                        string sampleDirectory = CategoryCatalog.SampleDirectoryFor(
                            project, material, config.ExampleBrandCSampleDirectory,
                            config.EntityNormalSampleDirectory, config.EntityPuSampleDirectory);
                        if ((String.IsNullOrWhiteSpace(sampleDirectory) || !Directory.Exists(sampleDirectory)) &&
                            !String.IsNullOrWhiteSpace(config.SampleDirectory) &&
                            Directory.Exists(config.SampleDirectory)) {
                            sampleDirectory = config.SampleDirectory;
                        }
                        if (String.IsNullOrWhiteSpace(sampleDirectory) || !Directory.Exists(sampleDirectory))
                            throw new DirectoryNotFoundException("未配置可用样品库：" + sampleDirectory);
                        // A retry must reuse the first assignment even when a
                        // new manifest has a different sample-library order.
                        if (previous != null && previous.SampleAssignment != null) {
                            sampleAssignment = ValidateExistingSampleAssignment(previous.SampleAssignment,
                                video, canonicalTaskId, id);
                            samplePath = sampleAssignment.SamplePath;
                            sampleSpeed = sampleAssignment.SampleSpeed;
                        } else if (previous != null && !String.IsNullOrWhiteSpace(previous.SamplePath)) {
                            sampleAssignment = CreateSampleAssignment(previous.SamplePath,
                                previous.SampleSpeed, video, canonicalTaskId, id,
                                previous.StableTaskArchiveKey);
                            samplePath = sampleAssignment.SamplePath;
                            sampleSpeed = sampleAssignment.SampleSpeed;
                        } else {
                            List<MediaMetadata> sampleList;
                            if (!samples.TryGetValue(sampleDirectory, out sampleList)) {
                                sampleList = SamplePlanner.LoadVerticalSamples(sampleDirectory);
                                samples[sampleDirectory] = sampleList;
                            }
                            MediaMetadata videoMetadata = WindowsMediaMetadataReader.Read(video);
                            MediaMetadata audioMetadata = WindowsMediaMetadataReader.Read(audio);
                            string stableSelectionKey = BuildStableSelectionKey(
                                canonicalTaskId, productionDate, id, video);
                            SamplePlan plan = SamplePlanner.CreateDeterministic(
                                videoMetadata, audioMetadata, sampleList, stableSelectionKey);
                            samplePath = plan.SamplePath;
                            sampleSpeed = plan.Speed;
                            if (!String.IsNullOrWhiteSpace(samplePath))
                                sampleAssignment = CreateSampleAssignment(samplePath, sampleSpeed,
                                    video, canonicalTaskId, id, "");
                        }
                        }
                    }
                    string groupId = batchId + "|" + project + "|" + material + "|" + language + "|" + taskNumber;
                    var task = new VideoTask {
                        Id = id,
                        VideoPath = video,
                        AudioPath = audio,
                        SamplePath = samplePath,
                        OriginalSamplePath = samplePath,
                        SampleAssignment = sampleAssignment,
                        OutputPath = output,
                        GroupId = groupId,
                        ProjectName = project,
                        MaterialName = material,
                        LanguageName = language,
                        VoiceTaskNumber = taskNumber,
                        OutputPrefix = Path.GetFileNameWithoutExtension(output),
                        ExportResolution = "2K",
                        // Workbench handoffs are production jobs.  They must
                        // always apply the company caption style rather than
                        // silently inheriting a manual client's Default mode.
                        SubtitleStylePreset = "CompanyStandard",
                        CaptionPlacement = String.IsNullOrWhiteSpace(captionPlacement)
                            ? "company_default" : captionPlacement,
                        SubtitleTreatment = subtitleTreatment,
                        CaptionTargetY = captionTargetY,
                        SampleSpeed = sampleSpeed,
                        Sequence = Math.Max(1, sequence),
                        SourceRootPath = Path.GetDirectoryName(video),
                        TaskOrigin = TaskOrigins.TikHubHandoff,
                        DeleteSourceAfterSuccess = SourceDeletionPolicy.ShouldRecycle(TaskOrigins.TikHubHandoff, false),
                        IsHandoffTask = true,
                        HandoffBatchId = batchId,
                        ProductionDate = productionDate,
                        HandoffManifestId = manifestId,
                        HandoffAttemptId = attemptId,
                        HandoffStatusFile = Path.GetFullPath(statusFile),
                        CanonicalTaskId = canonicalTaskId,
                        RunGroupId = runGroupId,
                        ContractBatchId = contractBatchId,
                        BatchKind = batchKind,
                        RemotePipelineTaskId = remotePipelineTaskId,
                        ExpectedTaskCount = expectedTaskCount,
                        ProjectExpectedCount = projectExpectedCount,
                        AttemptTaskCount = attemptTaskCount,
                        ProjectTaskIds = new List<string>(projectTaskIds),
                        CompletedTaskIds = new List<string>(completedTaskIds)
                    };
                    task.ReleaseOnly = itemReleaseOnly;
                    string productionStatus = Text(item, "productionStatus");
                    string draftReleaseStatus = Text(item, "draftReleaseStatus");
                    string draftCleanupStatus = Text(item, "draftCleanupStatus");
                    string draftCleanupWarning = Text(item, "draftCleanupWarning");
                    if (!String.IsNullOrWhiteSpace(productionStatus))
                        task.ProductionStatus = productionStatus;
                    if (!String.IsNullOrWhiteSpace(draftReleaseStatus))
                        task.DraftReleaseStatus = draftReleaseStatus;
                    if (!String.IsNullOrWhiteSpace(draftCleanupStatus))
                        task.DraftCleanupStatus = draftCleanupStatus;
                    if (!String.IsNullOrWhiteSpace(draftCleanupWarning))
                        task.DraftCleanupWarning = draftCleanupWarning;
                    if (task.ReleaseOnly) {
                        // The workbench lists a task for draft release only
                        // after its final output passed quality control, and
                        // writes that result into the manifest.  Without it
                        // the task never counted as release work, so the
                        // client sat idle while the workbench waited for it
                        // (2026-10-09 18:33, 7 hours).
                        string qualityStatus = Text(item, "qualityStatus");
                        if (!String.IsNullOrWhiteSpace(qualityStatus)) {
                            task.QualityStatus = qualityStatus;
                            task.QualityReportPath = Text(item, "qualityReportPath");
                            task.QualityCheckedAt = Text(item, "qualityCheckedAt");
                        }
                        task.SetState(TaskState.Completed);
                        if (String.IsNullOrWhiteSpace(task.ProductionStatus))
                            task.ProductionStatus = "completed";
                        if (String.IsNullOrWhiteSpace(task.DraftReleaseStatus))
                            task.DraftReleaseStatus = "pending";
                        if (String.IsNullOrWhiteSpace(task.DraftCleanupStatus))
                            task.DraftCleanupStatus = "release_pending";
                    }
                    if (previous == null) {
                        existing.Add(task);
                    } else {
                        PreserveRecoveryState(previous, task);
                        int existingIndex = existing.IndexOf(previous);
                        existing[existingIndex] = task;
                        if (!String.Equals(previous.Id, id, StringComparison.OrdinalIgnoreCase) &&
                            !String.IsNullOrWhiteSpace(previous.Id)) existingById.Remove(previous.Id);
                    }
                    existingById[id] = task;
                    existingPairs.Add(PairKey(video, output));
                    result.Added++;
                } catch (Exception ex) {
                    string message = ex.Message;
                    result.Issues.Add(id + "：" + message);
                    RecordRejected(existing, existingById, id, video, audio, output, project, material,
                        language, taskNumber, sequence, batchId, productionDate, statusFile, remotePipelineTaskId, expectedTaskCount,
                        projectExpectedCount, attemptTaskCount, projectTaskIds, completedTaskIds, message);
                }
            }
            HandoffStatusWriter.WriteAll(existing, false, false);
            return result;
        }

        private static SampleAssignment ValidateExistingSampleAssignment(
            SampleAssignment assignment, string videoPath, string canonicalTaskId, string taskId)
        {
            if (assignment == null || String.IsNullOrWhiteSpace(assignment.SamplePath))
                throw new InvalidOperationException("CAPCUT_SAMPLE_ASSIGNMENT_MISSING: 任务没有稳定样品分配");
            if (!String.Equals(assignment.CanonicalTaskId ?? "", canonicalTaskId ?? "",
                    StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(assignment.TaskId ?? "", taskId ?? "",
                    StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(CapCutTaskIdentity.NormalizePath(assignment.SourceVideoPath),
                    CapCutTaskIdentity.NormalizePath(videoPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "CAPCUT_SAMPLE_ASSIGNMENT_CONFLICT: 稳定样品分配不属于当前任务");
            if (!File.Exists(assignment.SamplePath))
                throw new FileNotFoundException(
                    "CAPCUT_SAMPLE_ASSIGNMENT_FILE_MISSING: 已分配样品不存在", assignment.SamplePath);
            string actualHash = FileHash(assignment.SamplePath);
            if (!String.IsNullOrWhiteSpace(assignment.SampleFileHash) &&
                !String.Equals(actualHash, assignment.SampleFileHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "CAPCUT_SAMPLE_ASSIGNMENT_HASH_MISMATCH: 已分配样品内容已变化");
            return assignment;
        }

        private static SampleAssignment CreateSampleAssignment(string samplePath, double speed,
            string videoPath, string canonicalTaskId, string taskId, string stableKey)
        {
            if (!File.Exists(samplePath))
                throw new FileNotFoundException(
                    "CAPCUT_SAMPLE_ASSIGNMENT_FILE_MISSING: 已分配样品不存在", samplePath);
            MediaMetadata metadata = WindowsMediaMetadataReader.Read(samplePath);
            string key = String.IsNullOrWhiteSpace(stableKey)
                ? BuildStableSelectionKey(canonicalTaskId, "", taskId, videoPath) : stableKey;
            string stableArchiveKey = key;
            if (!key.StartsWith("t-", StringComparison.OrdinalIgnoreCase)) {
                using (SHA256 sha = SHA256.Create()) {
                    stableArchiveKey = "t-" + BitConverter.ToString(sha.ComputeHash(
                        Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant()
                        .Substring(0, 24);
                }
            }
            return new SampleAssignment {
                StableTaskArchiveKey = stableArchiveKey,
                CanonicalTaskId = canonicalTaskId ?? "",
                TaskId = taskId ?? "",
                SourceVideoPath = videoPath ?? "",
                SamplePath = Path.GetFullPath(samplePath),
                SampleFileHash = FileHash(samplePath),
                SampleDuration = metadata.DurationSeconds,
                SampleSpeed = speed,
                AssignmentVersion = "1",
                AssignedAt = DateTime.Now.ToString("o")
            };
        }

        private static string BuildStableSelectionKey(string canonicalTaskId,
            string productionDate, string taskId, string videoPath)
        {
            return (canonicalTaskId ?? "") + "|" + (productionDate ?? "") + "|" +
                (taskId ?? "") + "|" + CapCutTaskIdentity.NormalizePath(videoPath);
        }

        private static string FileHash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path)) {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
        }

        // A remote Fish batch keeps its durable receiver id and copy-library
        // suffix in the filename.  Those names are deliberately not the
        // legacy CapCut task number, so enforcing the old final-number rule
        // would reject every valid remote handoff.
        internal static bool IsTaskNumberCompatible(string audioPath, string taskNumber)
        {
            string stem = Path.GetFileNameWithoutExtension(audioPath ?? "");
            if (stem.IndexOf("remote-inbox-", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return String.Equals(LastNumericToken(stem), taskNumber ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static void RecordRejected(IList<VideoTask> existing, IDictionary<string, VideoTask> existingById,
            string id, string video, string audio, string output, string project, string material,
            string language, string taskNumber, int sequence, string batchId,
            string productionDate, string statusFile,
            string remotePipelineTaskId, int expectedTaskCount, int projectExpectedCount, int attemptTaskCount,
            IEnumerable<string> projectTaskIds, IEnumerable<string> completedTaskIds, string error)
        {
            if (String.IsNullOrWhiteSpace(id)) return;
            VideoTask previous;
            existingById.TryGetValue(id, out previous);
            if (previous != null && previous.GetState() == TaskState.Completed) return;
            var rejected = new VideoTask {
                Id = id,
                VideoPath = video,
                AudioPath = audio,
                OutputPath = output,
                ProjectName = project,
                MaterialName = material,
                LanguageName = language,
                VoiceTaskNumber = taskNumber,
                Sequence = Math.Max(1, sequence),
                IsHandoffTask = true,
                HandoffBatchId = batchId,
                ProductionDate = productionDate,
                HandoffStatusFile = Path.GetFullPath(statusFile),
                RemotePipelineTaskId = remotePipelineTaskId,
                ExpectedTaskCount = expectedTaskCount,
                ProjectExpectedCount = projectExpectedCount,
                AttemptTaskCount = attemptTaskCount,
                ProjectTaskIds = new List<string>(projectTaskIds ?? new string[0]),
                CompletedTaskIds = new List<string>(completedTaskIds ?? new string[0]),
                Error = error
            };
            rejected.SetState(TaskState.Failed);
            if (previous == null) existing.Add(rejected);
            else existing[existing.IndexOf(previous)] = rejected;
            existingById[id] = rejected;
        }

        private static string Text(Dictionary<string, object> item, string key)
        {
            object value;
            return item.TryGetValue(key, out value) && value != null ? Convert.ToString(value).Trim() : "";
        }

        private static int Number(Dictionary<string, object> item, string key, int fallback)
        {
            int value;
            return Int32.TryParse(Text(item, key), out value) ? value : fallback;
        }

        private static bool Bool(Dictionary<string, object> item, string key)
        {
            object raw;
            if (!item.TryGetValue(key, out raw) || raw == null) return false;
            if (raw is bool) return (bool)raw;
            bool value;
            return Boolean.TryParse(Convert.ToString(raw), out value) && value;
        }

        private static double DecimalNumber(Dictionary<string, object> item, string key, double fallback)
        {
            double value;
            return Double.TryParse(Text(item, key), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static List<string> StringList(Dictionary<string, object> item, string key)
        {
            object raw;
            if (!item.TryGetValue(key, out raw) || raw == null) return new List<string>();
            IEnumerable values = raw as IEnumerable;
            if (values == null || raw is string) return new List<string>();
            return values.Cast<object>()
                .Select(value => Convert.ToString(value) ?? "")
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string FullPath(string value)
        {
            return String.IsNullOrWhiteSpace(value) ? "" :
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
        }

        private static string PairKey(string video, string output)
        {
            return (video ?? "").Trim() + "|" + (output ?? "").Trim();
        }

        internal static void PreserveRecoveryState(VideoTask previous, VideoTask replacement)
        {
            if (previous == null || replacement == null) return;
            replacement.CreatedAt = previous.CreatedAt;
            replacement.Attempts = previous.Attempts;
            if (!SameTaskPath(previous.VideoPath, replacement.VideoPath)) return;
            if (!SameStableTaskIdentity(previous, replacement)) return;

            // Re-importing the same workbench task is how Dashboard safety
            // continue refreshes a manifest.  Replacing the queue object must
            // not discard a verified direct-draft or exported-file checkpoint.
            // A new handoff ownership identity is the durable proof that the
            // operator issued a fresh recovery command: retain the checkpoint
            // but make the failed item runnable.  A passive refresh of the
            // same identity must keep the failure terminal.
            bool retryOwnershipChanged = previous.GetState() == TaskState.Failed &&
                (!String.Equals(previous.HandoffBatchId ?? "", replacement.HandoffBatchId ?? "", StringComparison.OrdinalIgnoreCase) ||
                 !String.Equals(previous.HandoffManifestId ?? "", replacement.HandoffManifestId ?? "", StringComparison.OrdinalIgnoreCase) ||
                 !String.Equals(previous.HandoffAttemptId ?? "", replacement.HandoffAttemptId ?? "", StringComparison.OrdinalIgnoreCase));
            if (retryOwnershipChanged) {
                replacement.SetState(TaskState.Waiting);
                replacement.Error = "";
                replacement.StartedAt = "";
                replacement.FinishedAt = "";
            } else {
                replacement.State = previous.State;
                replacement.Error = previous.Error;
                replacement.StartedAt = previous.StartedAt;
                replacement.FinishedAt = previous.FinishedAt;
                replacement.UpdatedAt = previous.UpdatedAt;
            }
            replacement.DirectDraftCheckpoint = previous.DirectDraftCheckpoint;
            replacement.CaptionDraftCheckpoint = previous.CaptionDraftCheckpoint;
            replacement.TimelineAssemblyCheckpoint = previous.TimelineAssemblyCheckpoint;
            replacement.TemporaryOutputPath = previous.TemporaryOutputPath;
            replacement.PostProcessWorkingPath = previous.PostProcessWorkingPath;
            // Sample selection is a task fact even before the sample is
            // imported into CapCut.  Preserve it on every same-task retry;
            // waiting for PreparedSamplePath here caused a new random sample
            // to invalidate video-only checkpoints.
            replacement.SamplePath = previous.SamplePath;
            replacement.PreparedSamplePath = previous.PreparedSamplePath;
            replacement.OriginalSamplePath = previous.OriginalSamplePath;
            replacement.PreparedSampleTargetSeconds = previous.PreparedSampleTargetSeconds;
            replacement.SampleAssignment = previous.SampleAssignment;
            if (!String.IsNullOrWhiteSpace(replacement.PreparedSamplePath) &&
                (replacement.TimelineAssemblyCheckpoint != null ||
                 replacement.CaptionDraftCheckpoint != null ||
                 replacement.DirectDraftCheckpoint != null)) {
                replacement.SamplePath = replacement.PreparedSamplePath;
                replacement.SampleSpeed = 1.0;
            }
            // A release-only manifest carries the workbench's verified quality
            // result; an older local record without one must not erase it.
            if (!String.IsNullOrWhiteSpace(previous.QualityStatus) || !replacement.ReleaseOnly) {
                replacement.QualityReportPath = previous.QualityReportPath;
                replacement.QualityStatus = previous.QualityStatus;
                replacement.QualityCheckedAt = previous.QualityCheckedAt;
            }
            replacement.QualityWarnings = previous.QualityWarnings == null
                ? new List<string>()
                : new List<string>(previous.QualityWarnings);
            replacement.ProductionStatus = previous.ProductionStatus;
            replacement.DraftReleaseStatus = previous.DraftReleaseStatus;
            replacement.ReleaseOnly = replacement.ReleaseOnly || previous.ReleaseOnly;
            replacement.DraftCleanupStatus = previous.DraftCleanupStatus;
            replacement.DraftCleanupWarning = previous.DraftCleanupWarning;
            replacement.DraftCleanupUpdatedAt = previous.DraftCleanupUpdatedAt;
        }

        private static bool SameStableTaskIdentity(VideoTask previous,
            VideoTask replacement)
        {
            return String.Equals((previous.Id ?? "").Trim(),
                    (replacement.Id ?? "").Trim(),
                    StringComparison.OrdinalIgnoreCase) &&
                String.Equals((previous.RemotePipelineTaskId ?? "").Trim(),
                    (replacement.RemotePipelineTaskId ?? "").Trim(),
                    StringComparison.OrdinalIgnoreCase) &&
                String.Equals((previous.ProductionDate ?? "").Trim(),
                    (replacement.ProductionDate ?? "").Trim(),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameTaskPath(string left, string right)
        {
            try {
                return String.Equals(FullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    FullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            } catch {
                return String.Equals((left ?? "").Trim(), (right ?? "").Trim(),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string LastNumericToken(string value)
        {
            return (value ?? "").Split('_').Where(part => part.All(Char.IsDigit)).LastOrDefault() ?? "";
        }
    }

    public static class HandoffStatusWriter
    {
        public static void WriteAll(IEnumerable<VideoTask> allTasks, bool running, bool paused)
        {
            foreach (var group in (allTasks ?? new VideoTask[0])
                .Where(task => task.IsHandoffTask && !String.IsNullOrWhiteSpace(task.HandoffStatusFile))
                .GroupBy(task => task.HandoffStatusFile, StringComparer.OrdinalIgnoreCase)) {
                try {
                    Write(group.Key, group.ToList(), running, paused);
                } catch {
                    // 状态回传不得中断 CapCut 的实际处理。
                }
            }
        }

        private static void Write(string path, IList<VideoTask> tasks, bool running, bool paused)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var payload = new Dictionary<string, object> {
                { "schemaVersion", 2 },
                { "clientVersion", ClientRuntimeIdentity.Version },
                { "buildHash", ClientRuntimeIdentity.BuildHash },
                { "clientExecutable", ClientRuntimeIdentity.ExecutablePath },
                { "batchId", tasks.Select(task => task.HandoffBatchId).FirstOrDefault() ?? "" },
                { "manifestId", tasks.Select(task => task.HandoffManifestId).FirstOrDefault() ?? "" },
                { "attemptId", tasks.Select(task => task.HandoffAttemptId).FirstOrDefault() ?? "" },
                { "runGroupId", tasks.Select(task => task.RunGroupId).FirstOrDefault(value => !String.IsNullOrWhiteSpace(value)) ?? "" },
                { "batchKind", tasks.Select(task => task.BatchKind).FirstOrDefault(value => !String.IsNullOrWhiteSpace(value)) ?? "" },
                { "contractBatchId", tasks.Select(task => task.ContractBatchId).FirstOrDefault(value => !String.IsNullOrWhiteSpace(value)) ?? "" },
                { "remotePipelineTaskId", tasks.Select(task => task.RemotePipelineTaskId).FirstOrDefault() ?? "" },
                { "expectedTaskCount", tasks.Select(task => task.ExpectedTaskCount).FirstOrDefault() },
                { "projectExpectedCount", tasks.Select(task => task.ProjectExpectedCount > 0 ? task.ProjectExpectedCount : task.ExpectedTaskCount).FirstOrDefault() },
                { "attemptTaskCount", tasks.Select(task => task.AttemptTaskCount).FirstOrDefault() },
                { "projectTaskIds", tasks.Select(task => task.ProjectTaskIds ?? new List<string>()).FirstOrDefault() ?? new List<string>() },
                { "completedTaskIds", tasks.Select(task => task.CompletedTaskIds ?? new List<string>()).FirstOrDefault() ?? new List<string>() },
                { "updatedAt", DateTime.Now.ToString("o") },
                { "heartbeatAt", DateTime.Now.ToString("o") },
                { "clientPid", Process.GetCurrentProcess().Id },
                // Keep the task that actually needs attention visible.  The
                // previous filter skipped a failed first task and pointed the
                // dashboard at the next waiting task, hiding the root cause.
                { "currentTaskId", tasks.Where(task => task.GetState() != TaskState.Completed &&
                    task.GetState() != TaskState.Failed && task.GetState() != TaskState.Waiting)
                    .Select(task => task.Id).FirstOrDefault() ??
                    tasks.Where(task => task.GetState() == TaskState.Failed)
                    .Select(task => task.Id).FirstOrDefault() ??
                    tasks.Where(task => task.GetState() == TaskState.Waiting)
                    .Select(task => task.Id).FirstOrDefault() ?? "" },
                { "primaryErrorCode", tasks.Where(task => task.GetState() == TaskState.Failed)
                    .Select(task => task.FailureFingerprint).FirstOrDefault(value => !String.IsNullOrWhiteSpace(value)) ?? "" },
                { "primaryError", tasks.Where(task => task.GetState() == TaskState.Failed)
                    .Select(task => task.Error).FirstOrDefault(value => !String.IsNullOrWhiteSpace(value)) ?? "" },
                { "running", running },
                { "paused", paused },
                { "total", tasks.Count },
                { "completed", tasks.Count(task => task.GetState() == TaskState.Completed) },
                { "failed", tasks.Count(task => task.GetState() == TaskState.Failed) },
                { "tasks", tasks.Select(task => new Dictionary<string, object> {
                    { "taskId", task.Id },
                    { "canonicalTaskId", String.IsNullOrWhiteSpace(task.CanonicalTaskId) ? task.Id : task.CanonicalTaskId },
                    { "runGroupId", task.RunGroupId ?? "" },
                    { "batchId", task.ContractBatchId ?? "" },
                    { "batchKind", task.BatchKind ?? "" },
                    { "manifestId", task.HandoffManifestId ?? "" },
                    { "attemptId", task.HandoffAttemptId ?? "" },
                    { "state", task.State },
                    { "stateText", TaskStateText.ToDisplay(task.GetState()) },
                    { "project", task.ProjectName ?? "" },
                    { "material", task.MaterialName ?? "" },
                    { "language", task.LanguageName ?? "" },
                    { "videoPath", task.VideoPath ?? "" },
                    { "audioPath", task.AudioPath ?? "" },
                    { "outputPath", task.OutputPath ?? "" },
                    { "temporaryOutputPath", task.TemporaryOutputPath ?? "" },
                    { "postProcessWorkingPath", task.PostProcessWorkingPath ?? "" },
                    { "qualityReportPath", task.QualityReportPath ?? "" },
                    { "qualityStatus", task.QualityStatus ?? "" },
                    { "qualityCheckedAt", task.QualityCheckedAt ?? "" },
                    { "qualityWarnings", task.QualityWarnings ?? new List<string>() },
                    { "productionStatus", task.ProductionStatus ?? "" },
                    { "draftReleaseStatus", task.DraftReleaseStatus ?? "" },
                    { "releaseOnly", task.ReleaseOnly },
                    { "preparedSamplePath", task.PreparedSamplePath ?? "" },
                    { "originalSamplePath", task.OriginalSamplePath ?? "" },
                    { "remotePipelineTaskId", task.RemotePipelineTaskId ?? "" },
                    { "expectedTaskCount", task.ExpectedTaskCount },
                    { "projectExpectedCount", task.ProjectExpectedCount },
                    { "attemptTaskCount", task.AttemptTaskCount },
                    { "draftCleanupStatus", task.DraftCleanupStatus ?? "" },
                    { "draftCleanupWarning", task.DraftCleanupWarning ?? "" },
                    { "draftCleanupUpdatedAt", task.DraftCleanupUpdatedAt ?? "" },
                    { "error_code", task.FailureFingerprint ?? "" },
                    { "errorCode", task.FailureFingerprint ?? "" },
                    { "error", task.Error ?? "" }
                }).ToArray() }
            };
            var serializer = new JavaScriptSerializer { MaxJsonLength = 20 * 1024 * 1024 };
            string temp = path + "." + Process.GetCurrentProcess().Id + "-" +
                Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            DurableFile.WriteAllText(temp, serializer.Serialize(payload), new UTF8Encoding(false));
            try {
                if (File.Exists(path)) {
                    try { File.Replace(temp, path, null); }
                    catch { File.Copy(temp, path, true); File.Delete(temp); }
                } else File.Move(temp, path);
            } finally {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}

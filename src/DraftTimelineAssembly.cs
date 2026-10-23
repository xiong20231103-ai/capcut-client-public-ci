using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CapCutBatchClient
{
    internal sealed class DraftTimelineValidationException : InvalidOperationException
    {
        internal DraftTimelineValidationException(string errorCode, string message)
            : base(errorCode + ": " + message)
        {
            ErrorCode = errorCode;
        }

        internal DraftTimelineValidationException(string errorCode, string message, Exception inner)
            : base(errorCode + ": " + message, inner)
        {
            ErrorCode = errorCode;
        }

        internal string ErrorCode { get; private set; }
    }

    public sealed class DraftTimelineAssemblyResult
    {
        public string DraftDirectory { get; set; }
        public string MainMaterialId { get; set; }
        public string SampleMaterialId { get; set; }
        public string AudioMaterialId { get; set; }
        public string MainSegmentId { get; set; }
        public string SampleSegmentId { get; set; }
        public string AudioSegmentId { get; set; }
        public string SemanticHash { get; set; }
        public long MainDurationUs { get; set; }
        public long SampleDurationUs { get; set; }
        public long AudioDurationUs { get; set; }
        public string[] VerifiedFiles { get; set; }
        public string VerifiedAt { get; set; }
    }

    public sealed class DraftAudioPlacementResult
    {
        public string DraftDirectory { get; set; }
        public string MainMaterialId { get; set; }
        public string AudioMaterialId { get; set; }
        public string MainSegmentId { get; set; }
        public string AudioSegmentId { get; set; }
        public string SemanticHash { get; set; }
        public long MainDurationUs { get; set; }
        public long AudioDurationUs { get; set; }
        public string[] VerifiedFiles { get; set; }
        public string VerifiedAt { get; set; }
    }

    public sealed class DraftVideoTimelineResult
    {
        public string DraftDirectory { get; set; }
        public string MainMaterialId { get; set; }
        public string MainSegmentId { get; set; }
        public string SemanticHash { get; set; }
        public long MainDurationUs { get; set; }
        public int VideoTrackCount { get; set; }
        public int AudioTrackCount { get; set; }
        public bool MuteVerified { get; set; }
        public string[] VerifiedFiles { get; set; }
        public string VerifiedAt { get; set; }
    }

    public sealed class DraftTimelineRepairResult
    {
        public string BackupDirectory { get; set; }
        public string[] PatchedFiles { get; set; }
        public DraftTimelineAssemblyResult Assembly { get; set; }
    }

    // Repairs only the one unambiguous failure shape CapCut can produce when a
    // prepared sample is dropped at the main clip's visual edge: the current
    // task main clip and current task sample exist exactly once, but CapCut has
    // placed the sample on a second video track.  No fuzzy media matching is
    // permitted.  The caller must close its owned CapCut session before using
    // this class so CapCut cannot overwrite the atomic repair.
    public sealed class DraftTimelineAssemblyRepairer
    {
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer {
            MaxJsonLength = Int32.MaxValue,
            RecursionLimit = 256
        };

        public DraftTimelineRepairResult RepairMisplacedSample(string draftDirectory,
            string mainVideoPath, string audioPath, string samplePath,
            double mainSeconds, double audioSeconds, double sampleSeconds)
        {
            string directory = Path.GetFullPath(draftDirectory ?? "");
            if (!Directory.Exists(directory))
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    "当前任务草稿目录不存在，不能修复样品轨道");

            string[] targets = FindTargets(directory).ToArray();
            if (targets.Length < 2)
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    "根草稿与活动Timeline不完整，不能安全修复样品轨道");

            var patched = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string target in targets) {
                string json = File.ReadAllText(target, Encoding.UTF8);
                object document;
                try { document = serializer.DeserializeObject(json); }
                catch (Exception ex) {
                    throw new DraftTimelineValidationException(
                        "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                        "草稿JSON无法解析，未修改任何文件: " +
                        Path.GetFileName(target), ex);
                }
                RepairDocument(document, mainVideoPath, audioPath, samplePath);
                patched[target] = serializer.Serialize(document);
            }

            string backup = CreateBackup(directory, targets);
            try {
                foreach (KeyValuePair<string, string> item in patched)
                    AtomicWrite(item.Key, item.Value);

                DraftTimelineAssemblyResult verified =
                    new DraftTimelineAssemblyVerifier().VerifyOnce(directory,
                        mainVideoPath, audioPath, samplePath, mainSeconds,
                        audioSeconds, sampleSeconds);
                return new DraftTimelineRepairResult {
                    BackupDirectory = backup,
                    PatchedFiles = targets,
                    Assembly = verified
                };
            } catch {
                RestoreBackup(backup, directory, targets);
                throw;
            }
        }

        private void RepairDocument(object document, string mainVideoPath,
            string audioPath, string samplePath)
        {
            var root = document as Dictionary<string, object>;
            var materials = root == null ? null : Value(root, "materials")
                as Dictionary<string, object>;
            if (root == null || materials == null)
                throw Unsafe("草稿缺少materials结构");

            Dictionary<string, Dictionary<string, object>> videos =
                MaterialMap(Value(materials, "videos"));
            Dictionary<string, Dictionary<string, object>> audios =
                MaterialMap(Value(materials, "audios"));
            List<Dictionary<string, object>> tracks =
                Objects(Value(root, "tracks"));
            List<Dictionary<string, object>> videoTracks = tracks.Where(track =>
                String.Equals(Text(track, "type"), "video",
                    StringComparison.OrdinalIgnoreCase)).ToList();
            List<Dictionary<string, object>> audioTracks = tracks.Where(track =>
                String.Equals(Text(track, "type"), "audio",
                    StringComparison.OrdinalIgnoreCase)).ToList();
            if (videoTracks.Count != 2)
                throw Unsafe("只允许修复恰好两条视频轨的已知误落轨结构，实际为" +
                    videoTracks.Count);
            if (audioTracks.Count != 1)
                throw Unsafe("独立音频轨身份不唯一，禁止自动修复");

            string normalizedMain = Normalize(mainVideoPath);
            string normalizedSample = Normalize(samplePath);
            string normalizedAudio = Normalize(audioPath);
            Dictionary<string, object> mainTrack = null;
            Dictionary<string, object> sampleTrack = null;
            Dictionary<string, object> mainSegment = null;
            Dictionary<string, object> sampleSegment = null;
            foreach (Dictionary<string, object> track in videoTracks) {
                List<Dictionary<string, object>> segments =
                    Objects(Value(track, "segments"));
                if (segments.Count != 1)
                    throw Unsafe("误落轨修复前每条视频轨必须只有一个片段");
                Dictionary<string, object> segment = segments[0];
                Dictionary<string, object> material;
                if (!videos.TryGetValue(Text(segment, "material_id"), out material))
                    throw Unsafe("视频轨引用了未登记素材");
                string path = Normalize(Text(material, "path"));
                if (String.Equals(path, normalizedMain,
                        StringComparison.OrdinalIgnoreCase)) {
                    if (mainSegment != null) throw Unsafe("当前主视频重复出现");
                    mainTrack = track;
                    mainSegment = segment;
                } else if (String.Equals(path, normalizedSample,
                        StringComparison.OrdinalIgnoreCase)) {
                    if (sampleSegment != null) throw Unsafe("当前专属样品重复出现");
                    sampleTrack = track;
                    sampleSegment = segment;
                } else {
                    throw Unsafe("存在不属于当前任务的额外视频素材");
                }
            }
            if (mainSegment == null || sampleSegment == null ||
                Object.ReferenceEquals(mainTrack, sampleTrack))
                throw Unsafe("无法唯一锁定当前主视频与专属样品");

            Range mainRange = TargetRange(mainSegment);
            Range sampleRange = TargetRange(sampleSegment);
            if (mainRange.Start != 0 || mainRange.Duration <= 0 ||
                sampleRange.Duration <= 0)
                throw Unsafe("主视频或样品时间范围无效");

            List<Dictionary<string, object>> audioSegments =
                Objects(Value(audioTracks[0], "segments"));
            if (audioSegments.Count != 1)
                throw Unsafe("独立音频片段身份不唯一");
            Dictionary<string, object> audioSegment = audioSegments[0];
            Dictionary<string, object> audioMaterial;
            if (!audios.TryGetValue(Text(audioSegment, "material_id"),
                    out audioMaterial) ||
                !String.Equals(Normalize(Text(audioMaterial, "path")),
                    normalizedAudio, StringComparison.OrdinalIgnoreCase))
                throw Unsafe("音频轨不是当前任务登记的独立音频");
            Range audioRange = TargetRange(audioSegment);
            if (audioRange.Start != 0 || audioRange.Duration <= 0)
                throw Unsafe("独立音频时间范围无效");

            Dictionary<string, object> sampleTarget =
                Value(sampleSegment, "target_timerange")
                as Dictionary<string, object>;
            sampleTarget["start"] = mainRange.Start + mainRange.Duration;
            CopyIfPresent(mainSegment, sampleSegment, "render_index");
            CopyIfPresent(mainSegment, sampleSegment, "track_render_index");
            CopyIfPresent(mainSegment, sampleSegment, "track_attribute");
            mainTrack["segments"] = new object[] { mainSegment, sampleSegment };

            var rewrittenTracks = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> track in tracks)
                if (!Object.ReferenceEquals(track, sampleTrack))
                    rewrittenTracks.Add(track);
            root["tracks"] = rewrittenTracks.ToArray();
            long videoEnd = mainRange.Start + mainRange.Duration +
                sampleRange.Duration;
            long audioEnd = audioRange.Start + audioRange.Duration;
            root["duration"] = Math.Max(videoEnd, audioEnd);
        }

        private static DraftTimelineValidationException Unsafe(string message)
        {
            return new DraftTimelineValidationException(
                "CAPCUT_DRAFT_SAMPLE_REPAIR_UNSAFE", message);
        }

        private static void CopyIfPresent(Dictionary<string, object> source,
            Dictionary<string, object> target, string key)
        {
            object value;
            if (source.TryGetValue(key, out value)) target[key] = value;
        }

        private static IEnumerable<string> FindTargets(string draftDirectory)
        {
            string root = Path.Combine(draftDirectory, "draft_content.json");
            if (File.Exists(root)) yield return root;
            string rootTemplate = Path.Combine(draftDirectory, "template-2.tmp");
            if (File.Exists(rootTemplate)) yield return rootTemplate;
            string timelines = Path.Combine(draftDirectory, "Timelines");
            if (!Directory.Exists(timelines)) yield break;
            foreach (string file in Directory.GetFiles(timelines,
                    "draft_content.json", SearchOption.AllDirectories))
                yield return file;
            foreach (string file in Directory.GetFiles(timelines,
                    "template-2.tmp", SearchOption.AllDirectories))
                yield return file;
        }

        private static string CreateBackup(string draftDirectory,
            IEnumerable<string> targets)
        {
            string key = Path.GetFileName(draftDirectory) + "-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
            string root = Path.Combine(AppPaths.DataDirectory,
                "timeline-repair-backups", key);
            foreach (string source in targets) {
                string relative = RelativeTo(draftDirectory, source);
                string destination = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(source, destination, false);
            }
            return root;
        }

        private static void RestoreBackup(string backupRoot,
            string draftDirectory, IEnumerable<string> targets)
        {
            foreach (string target in targets) {
                string backup = Path.Combine(backupRoot,
                    RelativeTo(draftDirectory, target));
                if (!File.Exists(backup)) continue;
                string temp = target + ".restore-" + Guid.NewGuid().ToString("N");
                File.Copy(backup, temp, true);
                ReplaceAtomically(temp, target);
            }
        }

        private static string RelativeTo(string draftDirectory, string source)
        {
            Uri baseUri = new Uri(AppendSlash(Path.GetFullPath(draftDirectory)));
            Uri fileUri = new Uri(Path.GetFullPath(source));
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fileUri)
                .ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string AppendSlash(string path)
        {
            return path.EndsWith("\\", StringComparison.Ordinal)
                ? path : path + "\\";
        }

        private static void AtomicWrite(string path, string content)
        {
            string temp = path + ".timeline-new-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            ReplaceAtomically(temp, path);
        }

        private static void ReplaceAtomically(string temp, string path)
        {
            try { File.Replace(temp, path, null); }
            catch (PlatformNotSupportedException) { File.Move(temp, path); }
            catch (FileNotFoundException) { File.Move(temp, path); }
        }

        private static Dictionary<string, Dictionary<string, object>>
            MaterialMap(object raw)
        {
            return Objects(raw).Where(value =>
                !String.IsNullOrWhiteSpace(Text(value, "id")))
                .ToDictionary(value => Text(value, "id"), value => value,
                    StringComparer.OrdinalIgnoreCase);
        }

        private static List<Dictionary<string, object>> Objects(object raw)
        {
            if (raw == null) return new List<Dictionary<string, object>>();
            object[] array = raw as object[];
            if (array != null) return array.Select(value =>
                    value as Dictionary<string, object>)
                .Where(value => value != null).ToList();
            IEnumerable enumerable = raw as IEnumerable;
            var result = new List<Dictionary<string, object>>();
            if (enumerable != null && !(raw is string) &&
                !(raw is Dictionary<string, object>)) {
                foreach (object value in enumerable) {
                    Dictionary<string, object> item =
                        value as Dictionary<string, object>;
                    if (item != null) result.Add(item);
                }
                return result;
            }
            Dictionary<string, object> single = raw as Dictionary<string, object>;
            if (single != null) result.Add(single);
            return result;
        }

        private static object Value(Dictionary<string, object> values, string key)
        {
            object value;
            return values != null && values.TryGetValue(key, out value)
                ? value : null;
        }

        private static string Text(Dictionary<string, object> values, string key)
        {
            return Convert.ToString(Value(values, key),
                CultureInfo.InvariantCulture) ?? "";
        }

        private static string Normalize(string path)
        {
            return String.IsNullOrWhiteSpace(path) ? "" :
                path.Trim().Replace('/', '\\').TrimEnd('\\');
        }

        private struct Range { public long Start; public long Duration; }

        private static Range TargetRange(Dictionary<string, object> segment)
        {
            Dictionary<string, object> value =
                Value(segment, "target_timerange")
                as Dictionary<string, object>;
            if (value == null) throw Unsafe("轨道片段缺少target_timerange");
            return new Range {
                Start = Number(Value(value, "start")),
                Duration = Number(Value(value, "duration"))
            };
        }

        private static long Number(object value)
        {
            if (value == null) return 0;
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
    }

    // The UI is allowed to place a clip, but only CapCut's persisted timeline is
    // allowed to decide whether that operation was correct.  This eliminates
    // false failures caused by timeline zoom, auto-fit and track vertical shifts.
    public sealed class DraftTimelineAssemblyVerifier
    {
        public string ObserveVideoPlacement(string draftDirectory, string mainVideoPath, double mainSeconds)
        {
            string rootFile = Path.Combine(draftDirectory, "draft_content.json");
            if (!File.Exists(rootFile)) return "snapshot_pending";
            var files = new List<string> { rootFile };
            string timelines = Path.Combine(draftDirectory, "Timelines");
            if (Directory.Exists(timelines)) files.AddRange(Directory.GetFiles(timelines,
                "draft_content.json", SearchOption.AllDirectories));
            string previous = null;
            bool empty = true;
            foreach (string file in files) {
                Dictionary<string, object> document;
                try { document = serializer.DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>; }
                catch (IOException) { return "snapshot_pending"; }
                catch (ArgumentException) { return "snapshot_pending"; }
                if (document == null || Value(document, "tracks") == null) return "snapshot_pending";
                string signature = serializer.Serialize(Value(document, "tracks"));
                if (previous != null && previous != signature) return "snapshot_pending";
                previous = signature;
                var materials = Value(document, "materials") as Dictionary<string, object>;
                if (materials == null) return "snapshot_pending";
                var videos = MaterialMap(Value(materials, "videos"));
                int count = 0;
                foreach (var track in Objects(Value(document, "tracks"))) {
                    foreach (var segment in Objects(Value(track, "segments"))) {
                        empty = false;
                        Dictionary<string, object> material;
                        if (Text(track, "type") != "video" || !videos.TryGetValue(Text(segment, "material_id"), out material)
                            || !String.Equals(Normalize(Text(material, "path")), Normalize(mainVideoPath), StringComparison.OrdinalIgnoreCase))
                            return "identity_conflict";
                        count++;
                    }
                }
                if (count > 1) throw new DraftTimelineValidationException("CAPCUT_VIDEO_SEGMENT_DUPLICATED",
                    "当前主视频重复加入，片段数=" + count + "; file=" + file);
            }
            if (empty) return "empty_confirmed";
            try { VerifyVideoOnce(draftDirectory, mainVideoPath, mainSeconds, false); return "video_present_valid"; }
            catch (DraftTimelineValidationException) { return "video_present_invalid"; }
        }

        private const long TimelineToleranceUs = 100000; // two frames or 0.10s
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer {
            MaxJsonLength = Int32.MaxValue,
            RecursionLimit = 256
        };

        public DraftVideoTimelineResult VerifyVideoStable(string draftDirectory,
            string mainVideoPath, double mainSeconds, int timeoutSeconds,
            CancellationToken token)
        {
            return VerifyVideoStableInternal(draftDirectory, mainVideoPath,
                mainSeconds, timeoutSeconds, token, true);
        }

        // Placement and mute are separate commits.  The placement gate must
        // accept a temporary audible main clip; the mute gate below will
        // re-run the same persisted scan with requireMuted=true.
        public DraftVideoTimelineResult VerifyVideoPlacementStable(
            string draftDirectory, string mainVideoPath, double mainSeconds,
            int timeoutSeconds, CancellationToken token)
        {
            return VerifyVideoStableInternal(draftDirectory, mainVideoPath,
                mainSeconds, timeoutSeconds, token, false);
        }

        private DraftVideoTimelineResult VerifyVideoStableInternal(
            string draftDirectory, string mainVideoPath, double mainSeconds,
            int timeoutSeconds, CancellationToken token, bool requireMuted)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(2,
                Math.Min(30, timeoutSeconds)));
            string previous = "";
            int stable = 0;
            Exception last = null;
            while (DateTime.UtcNow < deadline) {
                token.ThrowIfCancellationRequested();
                try {
                    DraftVideoTimelineResult result = VerifyVideoOnce(
                        draftDirectory, mainVideoPath, mainSeconds, requireMuted);
                    string signature = result.SemanticHash + "|" + String.Join("|",
                        result.VerifiedFiles.Select(path =>
                            File.GetLastWriteTimeUtc(path).Ticks + ":" +
                            new FileInfo(path).Length));
                    stable = String.Equals(signature, previous,
                        StringComparison.Ordinal) ? stable + 1 : 1;
                    previous = signature;
                    if (stable >= 2) return result;
                    last = null;
                } catch (Exception ex) {
                    last = ex;
                    stable = 0;
                    previous = "";
                }
                Thread.Sleep(500);
            }
            DraftTimelineValidationException structural =
                last as DraftTimelineValidationException;
            if (structural != null) throw structural;
            throw new DraftTimelineValidationException(
                "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                "主视频时间线在等待窗口内未形成连续两次一致的持久结构", last);
        }

        public DraftVideoTimelineResult VerifyVideoOnce(string draftDirectory,
            string mainVideoPath, double mainSeconds)
        {
            return VerifyVideoOnce(draftDirectory, mainVideoPath, mainSeconds,
                true);
        }

        private DraftVideoTimelineResult VerifyVideoOnce(string draftDirectory,
            string mainVideoPath, double mainSeconds, bool requireMuted)
        {
            string directory = Path.GetFullPath(draftDirectory ?? "");
            string rootFile = Path.Combine(directory, "draft_content.json");
            if (!File.Exists(rootFile))
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    "根草稿draft_content.json尚未保存");
            var files = new List<string> { rootFile };
            string timelines = Path.Combine(directory, "Timelines");
            if (Directory.Exists(timelines))
                files.AddRange(Directory.GetFiles(timelines,
                    "draft_content.json", SearchOption.AllDirectories));
            files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var results = new List<DraftVideoTimelineResult>();
            foreach (string file in files) {
                object document;
                try {
                    document = serializer.DeserializeObject(
                        File.ReadAllText(file, Encoding.UTF8));
                } catch (Exception ex) {
                    throw new DraftTimelineValidationException(
                        "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                        "主视频草稿JSON尚未完整写入", ex);
                }
                results.Add(VerifyVideoDocument(document, mainVideoPath,
                    mainSeconds, requireMuted));
            }
            string semantic = results[0].SemanticHash;
            if (results.Any(item => !String.Equals(item.SemanticHash, semantic,
                    StringComparison.Ordinal)))
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    "根草稿与活动Timeline的主视频结构不一致");
            DraftVideoTimelineResult accepted = results[0];
            accepted.DraftDirectory = directory;
            accepted.VerifiedFiles = files.ToArray();
            accepted.VerifiedAt = DateTime.Now.ToString("o");
            return accepted;
        }

        private DraftVideoTimelineResult VerifyVideoDocument(object document,
            string mainVideoPath, double mainSeconds, bool requireMuted)
        {
            var root = document as Dictionary<string, object>;
            var materials = Value(root, "materials") as Dictionary<string, object>;
            if (root == null || materials == null)
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    "主视频草稿缺少materials结构");
            Dictionary<string, Dictionary<string, object>> videos =
                MaterialMap(Value(materials, "videos"));
            List<Dictionary<string, object>> tracks = Objects(Value(root, "tracks"));
            List<Dictionary<string, object>> videoTracks = tracks.Where(track =>
                String.Equals(Text(track, "type"), "video",
                    StringComparison.OrdinalIgnoreCase)).ToList();
            List<Dictionary<string, object>> audioTracks = tracks.Where(track =>
                String.Equals(Text(track, "type"), "audio",
                    StringComparison.OrdinalIgnoreCase)).ToList();
            if (videoTracks.Count != 1)
                throw new DraftTimelineValidationException(
                    videoTracks.Count > 1 ? "CAPCUT_VIDEO_SEGMENT_DUPLICATED" : "CAPCUT_VIDEO_PLACEMENT_RESULT_PENDING",
                    "主视频断点必须且只能有一条视频轨，实际为" + videoTracks.Count);
            List<Dictionary<string, object>> segments =
                Objects(Value(videoTracks[0], "segments"));
            if (segments.Count != 1)
                throw new DraftTimelineValidationException(
                    segments.Count > 1 ? "CAPCUT_VIDEO_SEGMENT_DUPLICATED" : "CAPCUT_VIDEO_PLACEMENT_RESULT_PENDING",
                    "主视频断点必须且只能有一个视频片段，实际为" + segments.Count);
            if (audioTracks.Count != 0)
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_UNEXPECTED_SEGMENT",
                    "主视频断点尚未进入音频阶段，不能含独立音频轨");
            Dictionary<string, object> segment = segments[0];
            string materialId = Text(segment, "material_id");
            Dictionary<string, object> material;
            if (!videos.TryGetValue(materialId, out material) ||
                !String.Equals(Normalize(Text(material, "path")),
                    Normalize(mainVideoPath), StringComparison.OrdinalIgnoreCase))
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_MAIN_REPLACED",
                    "视频轨不是当前任务登记的主视频");
            Range range = TargetRange(segment);
            if (range.Start != 0)
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_MAIN_REPLACED",
                    "主视频没有从时间线零点开始");
            RequireDuration("主视频", range.Duration, mainSeconds);
            bool hasAudio = false;
            object rawHasAudio = Value(material, "has_audio");
            if (rawHasAudio != null) {
                try { hasAudio = Convert.ToBoolean(rawHasAudio,
                    CultureInfo.InvariantCulture); } catch { hasAudio = false; }
            }
            double volume = 1.0;
            object rawVolume = Value(segment, "volume");
            if (rawVolume != null) {
                try { volume = Convert.ToDouble(rawVolume,
                    CultureInfo.InvariantCulture); } catch { volume = 1.0; }
            }
            bool muted = !hasAudio || volume <= 0.001;
            if (requireMuted && !muted)
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_MAIN_NOT_MUTED",
                    "主视频仍包含可播放原声，不能建立音频导入断点");
            string semantic = String.Join("|", new[] {
                Normalize(mainVideoPath), materialId, Text(segment, "id"),
                range.Start.ToString(CultureInfo.InvariantCulture),
                range.Duration.ToString(CultureInfo.InvariantCulture),
                muted ? "muted" : "audible"
            });
            return new DraftVideoTimelineResult {
                MainMaterialId = materialId,
                MainSegmentId = Text(segment, "id"),
                SemanticHash = Hash(semantic),
                MainDurationUs = range.Duration,
                VideoTrackCount = 1,
                AudioTrackCount = 0,
                MuteVerified = muted
            };
        }

        public DraftAudioPlacementResult VerifyAudioStable(string draftDirectory,
            string mainVideoPath, string audioPath, double mainSeconds,
            double audioSeconds, int timeoutSeconds, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(draftDirectory) || !Directory.Exists(draftDirectory))
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    "当前任务草稿目录不存在，无法读取独立音频轨");

            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(2,
                Math.Min(15, timeoutSeconds)));
            string previousSignature = "";
            int stableReads = 0;
            DraftAudioPlacementResult latest = null;
            Exception last = null;
            while (DateTime.UtcNow < deadline) {
                token.ThrowIfCancellationRequested();
                try {
                    latest = VerifyAudioOnce(draftDirectory, mainVideoPath,
                        audioPath, mainSeconds, audioSeconds);
                    string signature = latest.SemanticHash + "|" + String.Join("|",
                        latest.VerifiedFiles.Select(path =>
                            File.GetLastWriteTimeUtc(path).Ticks + ":" +
                            new FileInfo(path).Length));
                    stableReads = String.Equals(signature, previousSignature,
                        StringComparison.Ordinal) ? stableReads + 1 : 1;
                    previousSignature = signature;
                    if (stableReads >= 2) return latest;
                    last = null;
                } catch (Exception ex) {
                    last = ex;
                    stableReads = 0;
                    previousSignature = "";
                }
                Thread.Sleep(500);
            }

            DraftTimelineValidationException structural =
                last as DraftTimelineValidationException;
            if (structural != null) throw structural;
            throw new DraftTimelineValidationException(
                "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                "CapCut草稿在15秒内没有形成连续两次一致的独立音频轨结构", last);
        }

        public DraftAudioPlacementResult VerifyAudioOnce(string draftDirectory,
            string mainVideoPath, string audioPath, double mainSeconds,
            double audioSeconds)
        {
            string root = Path.Combine(Path.GetFullPath(draftDirectory),
                "draft_content.json");
            if (!File.Exists(root))
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    "根草稿draft_content.json尚未保存");

            var files = new List<string> { root };
            string timelines = Path.Combine(draftDirectory, "Timelines");
            if (Directory.Exists(timelines))
                files.AddRange(Directory.GetFiles(timelines,
                    "draft_content.json", SearchOption.AllDirectories));
            files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var results = new List<DraftAudioPlacementResult>();
            foreach (string file in files) {
                object document;
                try {
                    document = serializer.DeserializeObject(
                        File.ReadAllText(file, Encoding.UTF8));
                } catch (Exception ex) {
                    throw new DraftTimelineValidationException(
                        "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                        "草稿JSON尚未完整写入: " +
                        Path.GetFileName(Path.GetDirectoryName(file)), ex);
                }
                results.Add(VerifyAudioDocument(document, mainVideoPath,
                    audioPath, mainSeconds, audioSeconds));
            }
            if (results.Count == 0)
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    "没有找到可验证的草稿时间线文件");
            string hash = results[0].SemanticHash;
            if (results.Any(item => !String.Equals(item.SemanticHash, hash,
                    StringComparison.Ordinal)))
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    "根草稿与活动Timeline尚未保存为相同音频轨结构");

            DraftAudioPlacementResult result = results[0];
            result.DraftDirectory = Path.GetFullPath(draftDirectory);
            result.VerifiedFiles = files.ToArray();
            result.VerifiedAt = DateTime.Now.ToString("o");
            return result;
        }

        private DraftAudioPlacementResult VerifyAudioDocument(object document,
            string mainVideoPath, string audioPath, double mainSeconds,
            double audioSeconds)
        {
            var root = document as Dictionary<string, object>;
            var materials = Value(root, "materials") as Dictionary<string, object>;
            if (root == null || materials == null)
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    "草稿缺少materials结构");

            Dictionary<string, Dictionary<string, object>> videos =
                MaterialMap(Value(materials, "videos"));
            Dictionary<string, Dictionary<string, object>> audios =
                MaterialMap(Value(materials, "audios"));
            List<Dictionary<string, object>> tracks = Objects(Value(root, "tracks"));
            List<Dictionary<string, object>> videoTracks = tracks.Where(track =>
                String.Equals(Text(track, "type"), "video",
                    StringComparison.OrdinalIgnoreCase)).ToList();
            List<Dictionary<string, object>> audioTracks = tracks.Where(track =>
                String.Equals(Text(track, "type"), "audio",
                    StringComparison.OrdinalIgnoreCase)).ToList();

            if (videoTracks.Count != 1)
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_MAIN_REPLACED",
                    "独立音频验证前必须且只能有一条主视频轨，实际为" +
                    videoTracks.Count);
            List<Dictionary<string, object>> videoSegments =
                Objects(Value(videoTracks[0], "segments"));
            if (videoSegments.Count != 1)
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_UNEXPECTED_SEGMENT",
                    "独立音频验证前主视频轨必须只有当前主视频，实际片段为" +
                    videoSegments.Count);

            Dictionary<string, object> mainSegment = videoSegments[0];
            string mainId = Text(mainSegment, "material_id");
            Dictionary<string, object> mainMaterial;
            if (!videos.TryGetValue(mainId, out mainMaterial) ||
                !String.Equals(Normalize(Text(mainMaterial, "path")),
                    Normalize(mainVideoPath), StringComparison.OrdinalIgnoreCase))
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_MAIN_REPLACED",
                    "视频轨不是当前任务登记的主视频");
            Range mainRange = TargetRange(mainSegment);
            if (mainRange.Start != 0)
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_MAIN_REPLACED",
                    "主视频没有从时间线零点开始");

            if (audioTracks.Count == 0)
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_AUDIO_MISSING",
                    "草稿尚未保存当前任务独立音频轨");
            if (audioTracks.Count != 1)
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_AUDIO_DUPLICATED",
                    "独立音频轨必须且只能有一条，实际为" + audioTracks.Count);
            List<Dictionary<string, object>> audioSegments =
                Objects(Value(audioTracks[0], "segments"));
            if (audioSegments.Count != 1)
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_AUDIO_DUPLICATED",
                    "独立音频片段必须且只能有一个，实际为" +
                    audioSegments.Count);

            Dictionary<string, object> audioSegment = audioSegments[0];
            string audioId = Text(audioSegment, "material_id");
            Dictionary<string, object> audioMaterial;
            if (!audios.TryGetValue(audioId, out audioMaterial) ||
                !String.Equals(Normalize(Text(audioMaterial, "path")),
                    Normalize(audioPath), StringComparison.OrdinalIgnoreCase))
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_AUDIO_IDENTITY_MISMATCH",
                    "音频轨不是当前任务登记的独立音频");
            Range audioRange = TargetRange(audioSegment);
            if (audioRange.Start != 0)
                throw new DraftTimelineValidationException(
                    "CAPCUT_DRAFT_AUDIO_OFFSET",
                    "独立音频没有从时间线零点开始");

            RequireDuration("主视频", mainRange.Duration, mainSeconds);
            RequireDuration("独立音频", audioRange.Duration, audioSeconds);
            string semantic = String.Join("|", new[] {
                Normalize(mainVideoPath), mainId, Text(mainSegment, "id"),
                mainRange.Start.ToString(), mainRange.Duration.ToString(),
                Normalize(audioPath), audioId, Text(audioSegment, "id"),
                audioRange.Start.ToString(), audioRange.Duration.ToString()
            });
            return new DraftAudioPlacementResult {
                MainMaterialId = mainId,
                AudioMaterialId = audioId,
                MainSegmentId = Text(mainSegment, "id"),
                AudioSegmentId = Text(audioSegment, "id"),
                SemanticHash = Hash(semantic),
                MainDurationUs = mainRange.Duration,
                AudioDurationUs = audioRange.Duration
            };
        }

        public DraftTimelineAssemblyResult VerifyStable(string draftDirectory,
            string mainVideoPath, string audioPath, string samplePath,
            double mainSeconds, double audioSeconds, double sampleSeconds,
            int timeoutSeconds, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(draftDirectory) || !Directory.Exists(draftDirectory))
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    "当前任务草稿目录不存在，无法读取真实时间线");

            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(2, Math.Min(15, timeoutSeconds)));
            string previousSignature = "";
            int stableReads = 0;
            DraftTimelineAssemblyResult latest = null;
            Exception last = null;
            while (DateTime.UtcNow < deadline) {
                token.ThrowIfCancellationRequested();
                try {
                    latest = VerifyOnce(draftDirectory, mainVideoPath, audioPath, samplePath,
                        mainSeconds, audioSeconds, sampleSeconds);
                    string signature = latest.SemanticHash + "|" + String.Join("|",
                        latest.VerifiedFiles.Select(path => File.GetLastWriteTimeUtc(path).Ticks + ":" + new FileInfo(path).Length));
                    stableReads = String.Equals(signature, previousSignature, StringComparison.Ordinal)
                        ? stableReads + 1 : 1;
                    previousSignature = signature;
                    if (stableReads >= 2) return latest;
                    last = null;
                } catch (DraftTimelineValidationException ex) {
                    // A still-saving draft can be temporarily incomplete. Keep
                    // polling, but return the precise structural error at timeout.
                    last = ex;
                    stableReads = 0;
                    previousSignature = "";
                } catch (Exception ex) {
                    last = ex;
                    stableReads = 0;
                    previousSignature = "";
                }
                Thread.Sleep(500);
            }

            DraftTimelineValidationException structural = last as DraftTimelineValidationException;
            if (structural != null) throw structural;
            throw new DraftTimelineValidationException("CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                "CapCut草稿在15秒内没有形成连续两次一致的可验证轨道结构", last);
        }

        public DraftTimelineAssemblyResult VerifyOnce(string draftDirectory,
            string mainVideoPath, string audioPath, string samplePath,
            double mainSeconds, double audioSeconds, double sampleSeconds)
        {
            string root = Path.Combine(Path.GetFullPath(draftDirectory), "draft_content.json");
            if (!File.Exists(root))
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    "根草稿draft_content.json尚未保存");

            var files = new List<string> { root };
            string timelines = Path.Combine(draftDirectory, "Timelines");
            if (Directory.Exists(timelines))
                files.AddRange(Directory.GetFiles(timelines, "draft_content.json", SearchOption.AllDirectories));

            var results = new List<DraftTimelineAssemblyResult>();
            foreach (string file in files.Distinct(StringComparer.OrdinalIgnoreCase)) {
                object document;
                try {
                    document = serializer.DeserializeObject(File.ReadAllText(file, Encoding.UTF8));
                } catch (Exception ex) {
                    throw new DraftTimelineValidationException("CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                        "草稿JSON尚未完整写入: " + Path.GetFileName(Path.GetDirectoryName(file)), ex);
                }
                results.Add(VerifyDocument(document, draftDirectory, mainVideoPath, audioPath,
                    samplePath, mainSeconds, audioSeconds, sampleSeconds));
            }

            if (results.Count == 0)
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    "没有找到可验证的草稿时间线文件");
            string hash = results[0].SemanticHash;
            if (results.Any(item => !String.Equals(item.SemanticHash, hash, StringComparison.Ordinal)))
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    "根草稿与活动Timeline尚未保存为相同轨道结构");

            DraftTimelineAssemblyResult result = results[0];
            result.VerifiedFiles = files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            result.VerifiedAt = DateTime.Now.ToString("o");
            return result;
        }

        private DraftTimelineAssemblyResult VerifyDocument(object document, string draftDirectory,
            string mainVideoPath, string audioPath, string samplePath,
            double mainSeconds, double audioSeconds, double sampleSeconds)
        {
            var root = document as Dictionary<string, object>;
            var materials = Value(root, "materials") as Dictionary<string, object>;
            if (root == null || materials == null)
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    "草稿缺少materials结构");

            Dictionary<string, Dictionary<string, object>> videos = MaterialMap(Value(materials, "videos"));
            Dictionary<string, Dictionary<string, object>> audios = MaterialMap(Value(materials, "audios"));
            List<Dictionary<string, object>> tracks = Objects(Value(root, "tracks"));
            List<Dictionary<string, object>> videoTracks = tracks.Where(track =>
                String.Equals(Text(track, "type"), "video", StringComparison.OrdinalIgnoreCase)).ToList();
            List<Dictionary<string, object>> audioTracks = tracks.Where(track =>
                String.Equals(Text(track, "type"), "audio", StringComparison.OrdinalIgnoreCase)).ToList();

            int repeatedMain = videoTracks.SelectMany(track => Objects(Value(track, "segments")))
                .Count(segment => videos.ContainsKey(Text(segment, "material_id")) &&
                    String.Equals(Normalize(Text(videos[Text(segment, "material_id")], "path")),
                        Normalize(mainVideoPath), StringComparison.OrdinalIgnoreCase));
            if (repeatedMain > 1)
                throw new DraftTimelineValidationException("CAPCUT_VIDEO_SEGMENT_DUPLICATED",
                    "当前主视频重复加入，片段数=" + repeatedMain);
            if (videoTracks.Count != 1)
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_SAMPLE_WRONG_TRACK",
                    "当前草稿必须且只能有一条主视频轨，实际为" + videoTracks.Count);
            List<Dictionary<string, object>> videoSegments = Objects(Value(videoTracks[0], "segments"));
            if (videoSegments.Count < 2)
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_SAMPLE_MISSING",
                    "主视频轨没有形成“主视频→样品”两个连续片段");
            if (videoSegments.Count > 2)
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_UNEXPECTED_SEGMENT",
                    "主视频轨包含额外或重复片段，实际为" + videoSegments.Count);

            string normalizedMain = Normalize(mainVideoPath);
            string normalizedSample = Normalize(samplePath);
            string normalizedAudio = Normalize(audioPath);
            Dictionary<string, object> mainSegment = videoSegments[0];
            Dictionary<string, object> sampleSegment = videoSegments[1];
            string mainId = Text(mainSegment, "material_id");
            string sampleId = Text(sampleSegment, "material_id");
            Dictionary<string, object> mainMaterial;
            Dictionary<string, object> sampleMaterial;
            if (!videos.TryGetValue(mainId, out mainMaterial) ||
                !String.Equals(Normalize(Text(mainMaterial, "path")), normalizedMain, StringComparison.OrdinalIgnoreCase))
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_MAIN_REPLACED",
                    "第一段不再是当前任务主视频");
            if (!videos.TryGetValue(sampleId, out sampleMaterial) ||
                !String.Equals(Normalize(Text(sampleMaterial, "path")), normalizedSample, StringComparison.OrdinalIgnoreCase))
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_SAMPLE_IDENTITY_MISMATCH",
                    "第二段不是当前任务专属样品");

            Range mainRange = TargetRange(mainSegment);
            Range sampleRange = TargetRange(sampleSegment);
            if (mainRange.Start != 0)
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_MAIN_REPLACED",
                    "主视频没有从时间线零点开始");
            if (sampleRange.Start != mainRange.Start + mainRange.Duration)
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_SAMPLE_WRONG_TRACK",
                    "样品没有紧接在主视频末端");

            if (audioTracks.Count != 1)
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_AUDIO_DUPLICATED",
                    "独立音频轨必须且只能有一条，实际为" + audioTracks.Count);
            List<Dictionary<string, object>> audioSegments = Objects(Value(audioTracks[0], "segments"));
            if (audioSegments.Count != 1)
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_AUDIO_DUPLICATED",
                    "独立音频片段必须且只能有一个，实际为" + audioSegments.Count);
            Dictionary<string, object> audioSegment = audioSegments[0];
            string audioId = Text(audioSegment, "material_id");
            Dictionary<string, object> audioMaterial;
            if (!audios.TryGetValue(audioId, out audioMaterial) ||
                !String.Equals(Normalize(Text(audioMaterial, "path")), normalizedAudio, StringComparison.OrdinalIgnoreCase))
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_AUDIO_DUPLICATED",
                    "音频轨不是当前任务登记的独立音频");
            Range audioRange = TargetRange(audioSegment);
            if (audioRange.Start != 0)
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_TIMING_MISMATCH",
                    "独立音频没有从时间线零点开始");

            RequireDuration("主视频", mainRange.Duration, mainSeconds);
            RequireDuration("样品", sampleRange.Duration, sampleSeconds);
            RequireDuration("独立音频", audioRange.Duration, audioSeconds);
            long videoEnd = sampleRange.Start + sampleRange.Duration;
            long audioEnd = audioRange.Start + audioRange.Duration;
            if (Math.Abs(videoEnd - audioEnd) > TimelineToleranceUs)
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_TIMING_MISMATCH",
                    String.Format(CultureInfo.InvariantCulture,
                        "视频末端与音频末端相差{0:0.000}s，超过0.10s", Math.Abs(videoEnd - audioEnd) / 1000000.0));

            string semantic = String.Join("|", new[] {
                normalizedMain, mainId, Text(mainSegment, "id"), mainRange.Start.ToString(), mainRange.Duration.ToString(),
                normalizedSample, sampleId, Text(sampleSegment, "id"), sampleRange.Start.ToString(), sampleRange.Duration.ToString(),
                normalizedAudio, audioId, Text(audioSegment, "id"), audioRange.Start.ToString(), audioRange.Duration.ToString()
            });
            return new DraftTimelineAssemblyResult {
                DraftDirectory = Path.GetFullPath(draftDirectory),
                MainMaterialId = mainId,
                SampleMaterialId = sampleId,
                AudioMaterialId = audioId,
                MainSegmentId = Text(mainSegment, "id"),
                SampleSegmentId = Text(sampleSegment, "id"),
                AudioSegmentId = Text(audioSegment, "id"),
                SemanticHash = Hash(semantic),
                MainDurationUs = mainRange.Duration,
                SampleDurationUs = sampleRange.Duration,
                AudioDurationUs = audioRange.Duration
            };
        }

        private static void RequireDuration(string label, long actualUs, double expectedSeconds)
        {
            if (expectedSeconds <= 0) return;
            long expectedUs = (long)Math.Round(expectedSeconds * 1000000.0, MidpointRounding.AwayFromZero);
            if (Math.Abs(actualUs - expectedUs) > TimelineToleranceUs)
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_TIMING_MISMATCH",
                    String.Format(CultureInfo.InvariantCulture,
                        "{0}草稿时长{1:0.000}s与媒体时长{2:0.000}s不一致", label,
                        actualUs / 1000000.0, expectedSeconds));
        }

        private static Dictionary<string, Dictionary<string, object>> MaterialMap(object raw)
        {
            return Objects(raw).Where(value => !String.IsNullOrWhiteSpace(Text(value, "id")))
                .ToDictionary(value => Text(value, "id"), value => value, StringComparer.OrdinalIgnoreCase);
        }

        private static List<Dictionary<string, object>> Objects(object raw)
        {
            if (raw == null) return new List<Dictionary<string, object>>();
            object[] array = raw as object[];
            if (array != null) return array.Select(value => value as Dictionary<string, object>)
                .Where(value => value != null).ToList();
            IEnumerable enumerable = raw as IEnumerable;
            if (enumerable != null && !(raw is string) && !(raw is Dictionary<string, object>)) {
                var result = new List<Dictionary<string, object>>();
                foreach (object value in enumerable) {
                    Dictionary<string, object> item = value as Dictionary<string, object>;
                    if (item != null) result.Add(item);
                }
                return result;
            }
            Dictionary<string, object> single = raw as Dictionary<string, object>;
            return single == null ? new List<Dictionary<string, object>>() :
                new List<Dictionary<string, object>> { single };
        }

        private static object Value(Dictionary<string, object> values, string key)
        {
            object value;
            return values != null && values.TryGetValue(key, out value) ? value : null;
        }

        private static string Text(Dictionary<string, object> values, string key)
        {
            return Convert.ToString(Value(values, key), CultureInfo.InvariantCulture) ?? "";
        }

        private struct Range { public long Start; public long Duration; }

        private static Range TargetRange(Dictionary<string, object> segment)
        {
            Dictionary<string, object> value = Value(segment, "target_timerange") as Dictionary<string, object>;
            if (value == null)
                throw new DraftTimelineValidationException("CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED",
                    "轨道片段缺少target_timerange");
            return new Range { Start = Number(Value(value, "start")), Duration = Number(Value(value, "duration")) };
        }

        private static long Number(object value)
        {
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static string Normalize(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return "";
            try { return Path.GetFullPath(path.Trim()).Replace('\\', '/').TrimEnd('/'); }
            catch { return path.Trim().Replace('\\', '/').TrimEnd('/'); }
        }

        private static string Hash(string value)
        {
            using (SHA256 sha = SHA256.Create()) {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
                var builder = new StringBuilder();
                foreach (byte item in bytes) builder.Append(item.ToString("x2"));
                return builder.ToString();
            }
        }
    }
}

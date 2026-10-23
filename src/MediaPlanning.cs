using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CapCutBatchClient
{
    public sealed class MediaMetadata
    {
        public string Path { get; set; }
        public double DurationSeconds { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Orientation { get; set; }

        public int DisplayWidth
        {
            get { return Orientation == 90 || Orientation == 270 ? Height : Width; }
        }

        public int DisplayHeight
        {
            get { return Orientation == 90 || Orientation == 270 ? Width : Height; }
        }

        public bool IsVertical
        {
            get { return DisplayHeight > DisplayWidth && DisplayWidth > 0; }
        }
    }

    public sealed class SamplePlan
    {
        public string SamplePath { get; set; }
        public double Speed { get; set; }
        public double AvailableSeconds { get; set; }
        public double SampleDurationSeconds { get; set; }
        public bool RequiresSample { get { return !String.IsNullOrWhiteSpace(SamplePath); } }
    }

    // The sample is part of the durable task plan.  It must survive a new
    // handoff manifest or attempt; otherwise a video-only checkpoint can be
    // rejected merely because a not-yet-imported sample was reselected.
    public sealed class SampleAssignment
    {
        public string StableTaskArchiveKey { get; set; }
        public string CanonicalTaskId { get; set; }
        public string TaskId { get; set; }
        public string SourceVideoPath { get; set; }
        public string SamplePath { get; set; }
        public string SampleFileHash { get; set; }
        public double SampleDuration { get; set; }
        public double SampleSpeed { get; set; }
        public string AssignmentVersion { get; set; }
        public string AssignedAt { get; set; }
    }

    public static class WindowsMediaMetadataReader
    {
        public static MediaMetadata Read(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) {
                throw new FileNotFoundException("媒体文件不存在", path);
            }

            object shell = null;
            object folder = null;
            object item = null;
            try {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) throw new InvalidOperationException("Windows Shell 媒体信息服务不可用");
                shell = Activator.CreateInstance(shellType);
                string directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                string name = System.IO.Path.GetFileName(path);
                folder = Invoke(shell, "NameSpace", directory);
                if (folder == null) throw new InvalidOperationException("无法读取媒体目录: " + directory);
                item = Invoke(folder, "ParseName", name);
                if (item == null) throw new InvalidOperationException("无法读取媒体文件信息: " + name);

                long durationTicks = ToInt64(Invoke(item, "ExtendedProperty", "System.Media.Duration"));
                var metadata = new MediaMetadata {
                    Path = System.IO.Path.GetFullPath(path),
                    DurationSeconds = durationTicks / 10000000.0,
                    Width = ToInt32(Invoke(item, "ExtendedProperty", "System.Video.FrameWidth")),
                    Height = ToInt32(Invoke(item, "ExtendedProperty", "System.Video.FrameHeight")),
                    Orientation = NormalizeOrientation(ToInt32(Invoke(item, "ExtendedProperty", "System.Video.Orientation")))
                };
                if (metadata.DurationSeconds <= 0) {
                    throw new InvalidOperationException("Windows 无法读取媒体时长: " + name);
                }
                return metadata;
            } finally {
                ReleaseCom(item);
                ReleaseCom(folder);
                ReleaseCom(shell);
            }
        }

        private static object Invoke(object target, string name, params object[] arguments)
        {
            return target.GetType().InvokeMember(name,
                BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                null, target, arguments);
        }

        private static int NormalizeOrientation(int value)
        {
            int normalized = value % 360;
            if (normalized < 0) normalized += 360;
            return normalized;
        }

        private static long ToInt64(object value)
        {
            if (value == null || value is DBNull) return 0;
            try { return Convert.ToInt64(value); }
            catch { return 0; }
        }

        private static int ToInt32(object value)
        {
            if (value == null || value is DBNull) return 0;
            try { return Convert.ToInt32(value); }
            catch { return 0; }
        }

        private static void ReleaseCom(object value)
        {
            if (value == null || !Marshal.IsComObject(value)) return;
            try { Marshal.FinalReleaseComObject(value); }
            catch { }
        }
    }

    public static class SamplePlanner
    {
        private static readonly object RandomSync = new object();
        private static readonly Random Random = new Random();
        private static readonly HashSet<string> VideoExtensions = new HashSet<string>(new[] {
            ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v"
        }, StringComparer.OrdinalIgnoreCase);

        public static List<MediaMetadata> LoadVerticalSamples(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) {
                throw new DirectoryNotFoundException("结尾样品文件夹不存在: " + directory);
            }

            var samples = new List<MediaMetadata>();
            var unreadable = new List<string>();
            foreach (string path in Directory.GetFiles(directory)
                .Where(file => VideoExtensions.Contains(System.IO.Path.GetExtension(file)))) {
                try {
                    MediaMetadata metadata = WindowsMediaMetadataReader.Read(path);
                    if (metadata.IsVertical) samples.Add(metadata);
                } catch {
                    unreadable.Add(System.IO.Path.GetFileName(path));
                }
            }
            if (samples.Count == 0) {
                throw new InvalidOperationException("样品文件夹中没有可读取的真实竖屏视频");
            }
            return samples;
        }

        public static SamplePlan Create(MediaMetadata video, MediaMetadata audio, IList<MediaMetadata> verticalSamples)
        {
            if (video == null || audio == null) throw new ArgumentNullException("video");
            double available = audio.DurationSeconds - video.DurationSeconds;
            if (available <= 0.10) {
                return new SamplePlan {
                    SamplePath = "",
                    Speed = 0,
                    AvailableSeconds = Math.Max(0, available),
                    SampleDurationSeconds = 0
                };
            }
            if (verticalSamples == null || verticalSamples.Count == 0) {
                throw new InvalidOperationException("没有可用的竖屏结尾样品");
            }

            MediaMetadata sample;
            lock (RandomSync) sample = verticalSamples[Random.Next(verticalSamples.Count)];
            double speed = CalculateSpeed(sample.DurationSeconds, available);
            if (speed < 0.10 || speed > 100.0) {
                throw new InvalidOperationException(String.Format(
                    "自动计算的样品倍速 {0:0.00}x 超出 CapCut 正常变速范围", speed));
            }
            return new SamplePlan {
                SamplePath = sample.Path,
                Speed = speed,
                AvailableSeconds = available,
                SampleDurationSeconds = sample.DurationSeconds
            };
        }

        public static SamplePlan CreateDeterministic(MediaMetadata video, MediaMetadata audio,
            IList<MediaMetadata> verticalSamples, string stableSelectionKey)
        {
            if (video == null || audio == null) throw new ArgumentNullException("video");
            double available = audio.DurationSeconds - video.DurationSeconds;
            if (available <= 0.10) {
                return new SamplePlan { SamplePath = "", Speed = 0,
                    AvailableSeconds = Math.Max(0, available), SampleDurationSeconds = 0 };
            }
            if (verticalSamples == null || verticalSamples.Count == 0)
                throw new InvalidOperationException("没有可用的竖屏结尾样品");
            List<MediaMetadata> ordered = verticalSamples
                .Where(item => item != null && !String.IsNullOrWhiteSpace(item.Path))
                .OrderBy(item => CapCutTaskIdentity.NormalizePath(item.Path), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ordered.Count == 0) throw new InvalidOperationException("没有可用的竖屏结尾样品");
            int index = StableIndex(stableSelectionKey, ordered.Count);
            MediaMetadata sample = ordered[index];
            double speed = CalculateSpeed(sample.DurationSeconds, available);
            if (speed < 0.10 || speed > 100.0)
                throw new InvalidOperationException(String.Format(
                    "自动计算的样品倍速 {0:0.00}x 超出 CapCut 正常变速范围", speed));
            return new SamplePlan { SamplePath = sample.Path, Speed = speed,
                AvailableSeconds = available, SampleDurationSeconds = sample.DurationSeconds };
        }

        public static SampleAssignment BuildAssignment(SamplePlan plan, VideoTask task)
        {
            if (plan == null || task == null || String.IsNullOrWhiteSpace(plan.SamplePath)) return null;
            if (!File.Exists(plan.SamplePath))
                throw new FileNotFoundException("CAPCUT_SAMPLE_ASSIGNMENT_FILE_MISSING: 已分配样品不存在", plan.SamplePath);
            MediaMetadata metadata = WindowsMediaMetadataReader.Read(plan.SamplePath);
            string stableKey = CapCutTaskIdentity.FromTask(task).StableTaskArchiveKey;
            string hash;
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(plan.SamplePath)) {
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            return new SampleAssignment {
                StableTaskArchiveKey = stableKey,
                CanonicalTaskId = task.RemotePipelineTaskId ?? "",
                TaskId = task.Id ?? "",
                SourceVideoPath = task.VideoPath ?? "",
                SamplePath = Path.GetFullPath(plan.SamplePath),
                SampleFileHash = hash,
                SampleDuration = metadata.DurationSeconds,
                SampleSpeed = plan.Speed,
                AssignmentVersion = "1",
                AssignedAt = DateTime.Now.ToString("o")
            };
        }

        private static int StableIndex(string value, int count)
        {
            using (SHA256 sha = SHA256.Create()) {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
                uint number = BitConverter.ToUInt32(digest, 0);
                return (int)(number % (uint)count);
            }
        }

        public static double CalculateSpeed(double sampleDurationSeconds, double availableSeconds)
        {
            if (sampleDurationSeconds <= 0) throw new ArgumentOutOfRangeException("sampleDurationSeconds");
            if (availableSeconds <= 0) throw new ArgumentOutOfRangeException("availableSeconds");
            return Math.Round(sampleDurationSeconds / availableSeconds, 2, MidpointRounding.AwayFromZero);
        }
    }
}

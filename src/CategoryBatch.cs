using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CapCutBatchClient
{
    public static class CategoryCatalog
    {
        public static readonly string[] Categories = {
            "ProjectA", "ProjectB", "ProjectC", "ProjectD", "SPC地板", "SPC墙板", "ExampleBrand"
        };

        public static readonly string[] RotatingMaterials = {
            "净白板", "碳岩板", "金属板", "镜面板", "木饰面", "PU石", "PU软石"
        };

        public static readonly string[] ExamplebrandMaterials = {
            "清淤泵", "潜污泵", "离心泵", "轴流泵", "粉碎格栅", "一体化预制泵站", "大型泵站", "MaterialPump"
        };

        public static int ProcessingOrder(string category)
        {
            int index = Array.FindIndex(Categories,
                item => String.Equals(item, category ?? "", StringComparison.OrdinalIgnoreCase));
            return index < 0 ? Int32.MaxValue : index;
        }

        public static bool IsKnownCategory(string value)
        {
            return Categories.Contains(value ?? "", StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsMaterialAllowed(string category, string material)
        {
            if (String.Equals(category, "ExampleBrand", StringComparison.OrdinalIgnoreCase))
                return ExamplebrandMaterials.Contains(material ?? "", StringComparer.OrdinalIgnoreCase);
            if (String.Equals(category, "SPC地板", StringComparison.OrdinalIgnoreCase))
                return String.Equals(material, "塑木地板", StringComparison.OrdinalIgnoreCase);
            if (String.Equals(category, "SPC墙板", StringComparison.OrdinalIgnoreCase))
                return String.Equals(material, "晶岩墙板", StringComparison.OrdinalIgnoreCase);
            return RotatingMaterials.Contains(material ?? "", StringComparer.OrdinalIgnoreCase);
        }

        public static string SampleDirectoryFor(
            string category,
            string material,
            string hancaiDirectory,
            string entityNormalDirectory,
            string entityPuDirectory)
        {
            // ExampleBrand handoffs carry an explicit task-scoped ending source and
            // must never fall back to a wall-panel sample bank.
            if (String.Equals(category, "ExampleBrand", StringComparison.OrdinalIgnoreCase))
                return "";
            if (!String.Equals(category, "ProjectB", StringComparison.OrdinalIgnoreCase))
                return hancaiDirectory ?? "";
            return String.Equals(material, "PU石", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(material, "PU软石", StringComparison.OrdinalIgnoreCase)
                ? entityPuDirectory ?? ""
                : entityNormalDirectory ?? "";
        }

        public static string OutputLanguageAlias(string language)
        {
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "西班牙语", "西班牙" },
                { "印尼语", "印尼" },
                { "葡萄牙语", "葡萄牙" },
                { "意大利语", "意大利" },
                { "土耳其语", "土耳其" }
            };
            string alias;
            return aliases.TryGetValue(language ?? "", out alias) ? alias : language ?? "";
        }
    }

    public sealed class CategoryMediaGroup
    {
        public string Category { get; set; }
        public string Material { get; set; }
        public string Language { get; set; }
        public string TaskNumber { get; set; }
        public string AudioPath { get; set; }
        public List<CategoryVideoSource> Videos { get; set; }

        public CategoryMediaGroup()
        {
            Videos = new List<CategoryVideoSource>();
        }
    }

    public sealed class CategoryVideoSource
    {
        public string Path { get; set; }
        public int SourceSequence { get; set; }
    }

    public sealed class CategoryScanResult
    {
        public List<VideoTask> Tasks { get; private set; }
        public List<string> Issues { get; private set; }
        public List<CategoryMediaGroup> Groups { get; private set; }

        public CategoryScanResult()
        {
            Tasks = new List<VideoTask>();
            Issues = new List<string>();
            Groups = new List<CategoryMediaGroup>();
        }
    }

    public static class CategoryBatchMatcher
    {
        private static readonly Regex VideoSequencePattern =
            new Regex(@"_(?<sequence>\d{4})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static CategoryScanResult Discover(
            string category,
            IEnumerable<string> videoPaths,
            IEnumerable<string> audioPaths)
        {
            var result = new CategoryScanResult();
            if (!CategoryCatalog.IsKnownCategory(category)) {
                result.Issues.Add("未知类目: " + category);
                return result;
            }

            var audioRecords = new List<AudioRecord>();
            foreach (string audioPath in audioPaths ?? new string[0]) {
                AudioRecord record;
                string error;
                if (!TryParseAudio(category, audioPath, out record, out error)) {
                    if (!String.IsNullOrWhiteSpace(error)) result.Issues.Add(error);
                    continue;
                }
                audioRecords.Add(record);
            }

            foreach (IGrouping<string, AudioRecord> duplicate in audioRecords
                .GroupBy(item => item.Stem, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)) {
                result.Issues.Add("存在重复配音名称，无法唯一匹配: " + duplicate.Key);
            }
            var uniqueAudio = audioRecords
                .GroupBy(item => item.Stem, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .Select(group => group.Single())
                .ToList();

            var matchedVideoPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string videoPath in videoPaths ?? new string[0]) {
                string stem = Path.GetFileNameWithoutExtension(videoPath);
                Match sequenceMatch = VideoSequencePattern.Match(stem);
                if (!sequenceMatch.Success) continue;
                string withoutSequence = stem.Substring(0, sequenceMatch.Index);
                List<AudioRecord> matches = uniqueAudio
                    .Where(audio => withoutSequence.Equals(audio.Stem, StringComparison.OrdinalIgnoreCase) ||
                                    withoutSequence.EndsWith("_" + audio.Stem, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count == 0) continue;
                if (matches.Count > 1) {
                    result.Issues.Add("视频匹配到多个配音: " + Path.GetFileName(videoPath));
                    continue;
                }
                AudioRecord selected = matches[0];
                selected.Videos.Add(new CategoryVideoSource {
                    Path = Path.GetFullPath(videoPath),
                    SourceSequence = Int32.Parse(sequenceMatch.Groups["sequence"].Value)
                });
                matchedVideoPaths.Add(Path.GetFullPath(videoPath));
            }

            foreach (AudioRecord audio in uniqueAudio) {
                if (audio.Videos.Count == 0) continue;
                List<int> duplicateNumbers = audio.Videos
                    .GroupBy(item => item.SourceSequence)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToList();
                if (duplicateNumbers.Count > 0) {
                    result.Issues.Add(String.Format(
                        "{0} 存在重复视频编号: {1}",
                        Path.GetFileName(audio.Path), String.Join(", ", duplicateNumbers)));
                    continue;
                }
                result.Groups.Add(new CategoryMediaGroup {
                    Category = audio.Category,
                    Material = audio.Material,
                    Language = audio.Language,
                    TaskNumber = audio.TaskNumber,
                    AudioPath = audio.Path,
                    Videos = audio.Videos.OrderBy(item => item.SourceSequence).ToList()
                });
            }

            foreach (string videoPath in videoPaths ?? new string[0]) {
                string full = Path.GetFullPath(videoPath);
                string stem = Path.GetFileNameWithoutExtension(full);
                bool belongsToSelectedCategory =
                    stem.StartsWith(category + "_", StringComparison.OrdinalIgnoreCase) ||
                    stem.IndexOf("_" + category + "_", StringComparison.OrdinalIgnoreCase) >= 0;
                if (belongsToSelectedCategory &&
                    VideoSequencePattern.IsMatch(stem) &&
                    !matchedVideoPaths.Contains(full)) {
                    result.Issues.Add("未找到唯一配音的视频: " + Path.GetFileName(full));
                }
            }
            return result;
        }

        private static bool TryParseAudio(
            string selectedCategory,
            string audioPath,
            out AudioRecord record,
            out string error)
        {
            record = null;
            error = "";
            string stem = Path.GetFileNameWithoutExtension(audioPath);
            string[] parts = stem.Split(new[] { '_' }, StringSplitOptions.None);
            if (parts.Length < 4) return false;
            string category = parts[0].Trim();
            if (!String.Equals(category, selectedCategory, StringComparison.OrdinalIgnoreCase)) return false;
            string material = parts[1].Trim();
            string language = parts[2].Trim();
            string taskNumber = parts[3].Trim();
            if (!CategoryCatalog.IsMaterialAllowed(category, material)) {
                error = String.Format("配音材料不符合类目规则: {0} / {1}", Path.GetFileName(audioPath), material);
                return false;
            }
            if (String.IsNullOrWhiteSpace(language) || !Regex.IsMatch(taskNumber, @"^\d+$")) {
                error = "配音名称缺少语言或任务编号: " + Path.GetFileName(audioPath);
                return false;
            }
            record = new AudioRecord {
                Category = category,
                Material = material,
                Language = language,
                TaskNumber = taskNumber,
                Stem = stem,
                Path = Path.GetFullPath(audioPath)
            };
            return true;
        }

        private sealed class AudioRecord
        {
            public string Category;
            public string Material;
            public string Language;
            public string TaskNumber;
            public string Stem;
            public string Path;
            public readonly List<CategoryVideoSource> Videos = new List<CategoryVideoSource>();
        }
    }

    public sealed class CategoryBatchScanner
    {
        private static readonly HashSet<string> VideoExtensions =
            new HashSet<string>(new[] { ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v", ".flv", ".webm", ".ts", ".mts" },
                StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> AudioExtensions =
            new HashSet<string>(new[] { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".wma" },
                StringComparer.OrdinalIgnoreCase);

        public CategoryScanResult Scan(
            string category,
            string videoRoot,
            string audioRoot,
            string outputRoot,
            string hancaiSampleDirectory,
            string entityNormalSampleDirectory,
            string entityPuSampleDirectory,
            string subtitleStyle)
        {
            if (!Directory.Exists(videoRoot)) throw new DirectoryNotFoundException("生成视频目录不存在: " + videoRoot);
            if (!Directory.Exists(audioRoot)) throw new DirectoryNotFoundException("配音目录不存在: " + audioRoot);
            if (String.IsNullOrWhiteSpace(outputRoot)) throw new InvalidOperationException("成片输出目录不能为空");

            string fullVideoRoot = Path.GetFullPath(videoRoot);
            string fullOutputRoot = Path.GetFullPath(outputRoot);
            string completedSourceRoot = Path.Combine(fullVideoRoot, WindowsSourceVideoRecycler.CompletedDirectoryName);
            IEnumerable<string> videos = Directory.GetFiles(fullVideoRoot, "*.*", SearchOption.AllDirectories)
                .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
                .Where(path => !PathSafety.IsWithinRoot(path, fullOutputRoot))
                .Where(path => !PathSafety.IsWithinRoot(path, completedSourceRoot));
            IEnumerable<string> audios = Directory.GetFiles(audioRoot, "*.*", SearchOption.AllDirectories)
                .Where(path => AudioExtensions.Contains(Path.GetExtension(path)));
            CategoryScanResult result = CategoryBatchMatcher.Discover(category, videos, audios);

            var sampleCache = new Dictionary<string, List<MediaMetadata>>(StringComparer.OrdinalIgnoreCase);
            var outputCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (CategoryMediaGroup group in result.Groups
                .OrderBy(item => Array.IndexOf(CategoryCatalog.RotatingMaterials, item.Material))
                .ThenBy(item => item.Language, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.TaskNumber, StringComparer.OrdinalIgnoreCase)) {
                string sampleDirectory = CategoryCatalog.SampleDirectoryFor(
                    group.Category, group.Material,
                    hancaiSampleDirectory, entityNormalSampleDirectory, entityPuSampleDirectory);
                if (String.IsNullOrWhiteSpace(sampleDirectory) || !Directory.Exists(sampleDirectory)) {
                    result.Issues.Add(String.Format(
                        "{0}/{1}/{2} 缺少可用样品库: {3}",
                        group.Category, group.Material, group.Language, sampleDirectory));
                    continue;
                }
                List<MediaMetadata> samples;
                if (!sampleCache.TryGetValue(sampleDirectory, out samples)) {
                    samples = SamplePlanner.LoadVerticalSamples(sampleDirectory);
                    sampleCache[sampleDirectory] = samples;
                }

                MediaMetadata audioMetadata = WindowsMediaMetadataReader.Read(group.AudioPath);
                string counterKey = group.Category + "|" + group.Material + "|" + group.Language;
                int sequence;
                if (!outputCounters.TryGetValue(counterKey, out sequence)) sequence = 0;
                string outputDirectory = Path.Combine(fullOutputRoot, group.Category, group.Material);
                string prefix = group.Category + CategoryCatalog.OutputLanguageAlias(group.Language);
                string groupId = Guid.NewGuid().ToString("N");
                foreach (CategoryVideoSource source in group.Videos) {
                    sequence++;
                    MediaMetadata videoMetadata = WindowsMediaMetadataReader.Read(source.Path);
                    string selectionKey = group.Category + "|" + group.Material + "|" +
                        group.Language + "|" + CapCutTaskIdentity.NormalizePath(source.Path);
                    SamplePlan samplePlan = SamplePlanner.CreateDeterministic(videoMetadata,
                        audioMetadata, samples, selectionKey);
                    string outputPath = OutputNaming.ProjectBatchPath(prefix, sequence, outputDirectory, ".mp4");
                    if (File.Exists(outputPath)) {
                        result.Issues.Add("成片文件已存在，已阻止覆盖: " + outputPath);
                        continue;
                    }
                    var task = new VideoTask {
                        VideoPath = source.Path,
                        AudioPath = group.AudioPath,
                        SamplePath = samplePlan.SamplePath,
                        OutputPath = outputPath,
                        GroupId = groupId,
                        ProjectName = group.Category,
                        MaterialName = group.Material,
                        LanguageName = group.Language,
                        VoiceTaskNumber = group.TaskNumber,
                        OutputPrefix = prefix,
                        ExportResolution = "2K",
                        SubtitleStylePreset = subtitleStyle,
                        SampleSpeed = samplePlan.Speed,
                        Sequence = sequence,
                        SourceRootPath = fullVideoRoot,
                        TaskOrigin = TaskOrigins.CategoryScan,
                        DeleteSourceAfterSuccess = SourceDeletionPolicy.ShouldRecycle(TaskOrigins.CategoryScan, false)
                    };
                    task.SampleAssignment = SamplePlanner.BuildAssignment(samplePlan, task);
                    result.Tasks.Add(task);
                }
                outputCounters[counterKey] = sequence;
            }
            return result;
        }
    }

    public static class PathSafety
    {
        public static bool IsWithinRoot(string path, string root)
        {
            if (String.IsNullOrWhiteSpace(path) || String.IsNullOrWhiteSpace(root)) return false;
            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }

    public interface ISourceVideoRecycler
    {
        void Recycle(string sourcePath, string sourceRootPath);
    }

    public sealed class WindowsSourceVideoRecycler : ISourceVideoRecycler
    {
        public const string CompletedDirectoryName = "已完成源视频";

        public void Recycle(string sourcePath, string sourceRootPath)
        {
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("原生成视频不存在", sourcePath);
            if (!PathSafety.IsWithinRoot(sourcePath, sourceRootPath))
                throw new InvalidOperationException("原视频不在本次扫描或交接目录内，已阻止归档: " + sourcePath);
            string fullPath = Path.GetFullPath(sourcePath);
            string fullRoot = Path.GetFullPath(sourceRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string completedRoot = Path.Combine(fullRoot, CompletedDirectoryName);
            if (PathSafety.IsWithinRoot(fullPath, completedRoot))
                throw new InvalidOperationException("原视频已经位于已完成源视频目录: " + fullPath);

            string relative = fullPath.Substring(fullRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string destination = Path.Combine(completedRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            destination = UniqueDestination(destination);
            File.Move(fullPath, destination);
            if (File.Exists(fullPath) || !File.Exists(destination))
                throw new IOException("无法把原生成视频移动到已完成源视频目录: " + fullPath);
        }

        private static string UniqueDestination(string path)
        {
            if (!File.Exists(path)) return path;
            string directory = Path.GetDirectoryName(path);
            string stem = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);
            for (int index = 2; index < 10000; index++) {
                string candidate = Path.Combine(directory, stem + " (" + index + ")" + extension);
                if (!File.Exists(candidate)) return candidate;
            }
            throw new IOException("已完成源视频目录存在过多同名文件: " + path);
        }
    }
}

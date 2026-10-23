using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CapCutBatchClient
{
    public interface IFinalOutputQualityValidator
    {
        OutputQualityResult Validate(VideoTask task, int timeoutSeconds, CancellationToken token);
    }

    public sealed class OutputQualityResult
    {
        public string Status { get; set; }
        public string ReportPath { get; set; }
        public List<string> Warnings { get; set; }

        public OutputQualityResult()
        {
            Status = "passed";
            ReportPath = "";
            Warnings = new List<string>();
        }
    }

    // Deterministic upload gate. No model or network service is involved.
    public sealed class FfmpegOutputQualityValidator : IFinalOutputQualityValidator
    {
        private readonly AppLogger logger;
        private readonly string ffmpegPath;
        private readonly string ffprobePath;

        public FfmpegOutputQualityValidator(AppLogger logger)
        {
            this.logger = logger ?? new AppLogger();
            ffmpegPath = FindExecutable("ffmpeg.exe");
            ffprobePath = FindExecutable("ffprobe.exe");
        }

        public OutputQualityResult Validate(VideoTask task, int timeoutSeconds, CancellationToken token)
        {
            string output = task == null ? "" : Path.GetFullPath(task.OutputPath ?? "");
            string reportPath = String.IsNullOrWhiteSpace(output) ? "" : output + ".quality.json";
            try {
                return ValidateCore(task, timeoutSeconds, token);
            } catch (Exception error) {
                if (!String.IsNullOrWhiteSpace(reportPath)) {
                    try {
                        AtomicJson(reportPath, new Dictionary<string, object> {
                            { "schemaVersion", 1 },
                            { "modelAllowed", false },
                            { "checkedAt", DateTime.Now.ToString("o") },
                            { "status", "blocked" },
                            { "batchId", task == null ? "" : task.HandoffBatchId ?? "" },
                            { "canonicalTaskId", task == null ? "" : task.RemotePipelineTaskId ?? "" },
                            { "manifestId", task == null ? "" : task.HandoffManifestId ?? "" },
                            { "attemptId", task == null ? "" : task.HandoffAttemptId ?? "" },
                            { "taskId", task == null ? "" : task.Id ?? "" },
                            { "fileName", Path.GetFileName(output) },
                            { "checks", new[] { new Dictionary<string, object> {
                                { "code", "quality_gate" },
                                { "severity", "hard" },
                                { "ok", false },
                                { "message", error.Message }
                            }}}
                        });
                    } catch { }
                }
                throw;
            }
        }

        private OutputQualityResult ValidateCore(VideoTask task, int timeoutSeconds, CancellationToken token)
        {
            if (task == null) throw new ArgumentNullException("task");
            string output = Path.GetFullPath(task.OutputPath ?? "");
            if (!File.Exists(output)) throw new FileNotFoundException("最终成片不存在", output);
            ValidateIdentity(task);

            ProbeDocument probe = Probe(output, Math.Min(60, timeoutSeconds), token);
            ProbeStream video = probe.StreamItems.FirstOrDefault(item =>
                String.Equals(item.codec_type, "video", StringComparison.OrdinalIgnoreCase));
            ProbeStream audio = probe.StreamItems.FirstOrDefault(item =>
                String.Equals(item.codec_type, "audio", StringComparison.OrdinalIgnoreCase));
            if (video == null) throw new InvalidDataException("成片质检失败：缺少视频流");
            if (audio == null) throw new InvalidDataException("成片质检失败：缺少音频流");
            if (!String.Equals(video.codec_name, "h264", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("成片质检失败：视频编码不是 H.264");
            if (!String.Equals(audio.codec_name, "aac", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("成片质检失败：音频编码不是 AAC");
            if (video.width <= 0 || video.height <= 0 || video.height <= video.width)
                throw new InvalidDataException("成片质检失败：分辨率或竖屏方向错误");

            double frameRate = ParseRate(video.avg_frame_rate);
            if (frameRate < 20 || frameRate > 61)
                throw new InvalidDataException("成片质检失败：帧率超出允许范围");
            double duration = ParseDouble(probe.FormatInfo == null ? "" : probe.FormatInfo.duration);
            if (duration <= 0) throw new InvalidDataException("成片质检失败：无法读取有效时长");
            double expectedDuration = ProbeDuration(task.AudioPath, token);
            double tolerance = Math.Max(0.10, 2.0 / frameRate);
            if (expectedDuration > 0 && Math.Abs(duration - expectedDuration) > tolerance)
                throw new InvalidDataException(String.Format(CultureInfo.InvariantCulture,
                    "成片质检失败：实际时长 {0:0.000}s 与配音时长 {1:0.000}s 不一致",
                    duration, expectedDuration));

            // A complete decode is the final corruption/truncation gate.
            Run(ffmpegPath, "-hide_banner -nostdin -v error -xerror -i " + Quote(output) +
                " -map 0:v:0 -map 0:a:0 -f null NUL", Math.Max(120, timeoutSeconds), token, true);

            string visualLog = Run(ffmpegPath,
                "-hide_banner -nostdin -i " + Quote(output) +
                " -vf \"blackdetect=d=1.0:pic_th=0.98,freezedetect=n=-60dB:d=2.0\" -an -f null NUL",
                Math.Max(120, timeoutSeconds), token, false);
            string audioLog = Run(ffmpegPath,
                "-hide_banner -nostdin -i " + Quote(output) +
                " -af \"silencedetect=n=-50dB:d=2.0,volumedetect\" -vn -f null NUL",
                Math.Max(120, timeoutSeconds), token, false);

            double blackSeconds = SumDurations(visualLog, "black_duration:");
            double freezeSeconds = SumFreezeDurations(visualLog);
            double silenceSeconds = SumDurations(audioLog, "silence_duration:");
            var warnings = new List<string>();
            if (blackSeconds >= Math.Max(3.0, duration * 0.50))
                throw new InvalidDataException("成片质检失败：检测到长时间黑屏");
            if (freezeSeconds >= Math.Max(3.0, duration * 0.50))
                throw new InvalidDataException("成片质检失败：检测到长时间静帧");
            if (silenceSeconds >= Math.Max(2.0, duration - 0.50))
                throw new InvalidDataException("成片质检失败：音频基本全程静音");
            if (blackSeconds >= 1.0) warnings.Add("检测到短时黑屏");
            if (freezeSeconds >= 2.0) warnings.Add("检测到短时静帧");
            if (silenceSeconds >= 2.0) warnings.Add("检测到局部静音");
            double maxVolume = ParseMetric(audioLog, "max_volume:");
            if (!Double.IsNaN(maxVolume) && maxVolume >= -0.05)
                warnings.Add("峰值接近 0dB，建议抽检是否爆音");

            string reportPath = output + ".quality.json";
            var report = new Dictionary<string, object> {
                { "schemaVersion", 1 },
                { "modelAllowed", false },
                { "checkedAt", DateTime.Now.ToString("o") },
                { "status", warnings.Count == 0 ? "passed" : "warning" },
                { "batchId", task.HandoffBatchId ?? "" },
                { "canonicalTaskId", task.RemotePipelineTaskId ?? "" },
                { "manifestId", task.HandoffManifestId ?? "" },
                { "attemptId", task.HandoffAttemptId ?? "" },
                { "taskId", task.Id ?? "" },
                { "fileName", Path.GetFileName(output) },
                { "sha256", Sha256(output) },
                { "fileSize", new FileInfo(output).Length },
                { "videoCodec", video.codec_name ?? "" },
                { "audioCodec", audio.codec_name ?? "" },
                { "width", video.width },
                { "height", video.height },
                { "frameRate", frameRate },
                { "duration", duration },
                { "expectedDuration", expectedDuration },
                { "durationTolerance", tolerance },
                { "firstFrameReplacement", "verified_by_postprocessor" },
                { "blackSeconds", blackSeconds },
                { "freezeSeconds", freezeSeconds },
                { "silenceSeconds", silenceSeconds },
                { "maxVolumeDb", Double.IsNaN(maxVolume) ? (object)null : maxVolume },
                { "warnings", warnings.ToArray() },
                { "checks", warnings.Select(message => new Dictionary<string, object> {
                    { "code", "media_warning" },
                    { "severity", "warning" },
                    { "ok", false },
                    { "message", message }
                }).ToArray() }
            };
            AtomicJson(reportPath, report);
            logger.Info("成片确定性质检通过: " + Path.GetFileName(output) +
                (warnings.Count == 0 ? "" : "；警告 " + String.Join("、", warnings.ToArray())));
            return new OutputQualityResult {
                Status = warnings.Count == 0 ? "passed" : "warning",
                ReportPath = reportPath,
                Warnings = warnings
            };
        }

        private void ValidateIdentity(VideoTask task)
        {
            if (!task.IsHandoffTask) return;
            if (String.IsNullOrWhiteSpace(task.HandoffBatchId) ||
                String.IsNullOrWhiteSpace(task.HandoffManifestId) ||
                String.IsNullOrWhiteSpace(task.HandoffAttemptId) ||
                String.IsNullOrWhiteSpace(task.RemotePipelineTaskId))
                throw new InvalidDataException("成片质检失败：正式任务身份不完整");
        }

        private ProbeDocument Probe(string path, int timeoutSeconds, CancellationToken token)
        {
            string json = Run(ffprobePath,
                "-v error -show_entries format=duration:stream=codec_type,codec_name,width,height,avg_frame_rate " +
                "-of json " + Quote(path), timeoutSeconds, token, true);
            ProbeDocument result = DeserializeProbe(json);
            if (result == null || result.StreamItems == null) throw new InvalidDataException("ffprobe 未返回有效媒体信息");
            return result;
        }

        private static ProbeDocument DeserializeProbe(string json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
            return serializer.Deserialize<ProbeDocument>(json);
        }

        internal static bool CanDeserializeProbeJsonForTest(string json)
        {
            ProbeDocument result = DeserializeProbe(json);
            return result != null && result.StreamItems.Count > 0 && result.FormatInfo != null;
        }

        private double ProbeDuration(string path, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;
            string value = Run(ffprobePath, "-v error -show_entries format=duration -of default=nk=1:nw=1 " +
                Quote(path), 30, token, true);
            return ParseDouble(value.Trim());
        }

        private static string Run(string executable, string arguments, int timeoutSeconds,
            CancellationToken token, bool failOnExit)
        {
            var start = new ProcessStartInfo(executable, arguments) {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (Process process = Process.Start(start)) {
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, timeoutSeconds));
                while (!process.WaitForExit(200)) {
                    token.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow >= deadline) {
                        try { process.Kill(); } catch { }
                        throw new TimeoutException("媒体质检超时");
                    }
                }
                if (failOnExit && process.ExitCode != 0)
                    throw new InvalidDataException("媒体完整解码失败: " + Tail(stderr, 500));
                return stdout + Environment.NewLine + stderr;
            }
        }

        private static string FindExecutable(string name)
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new List<string> {
                Path.Combine(baseDirectory, name),
                Path.Combine(baseDirectory, "ffmpeg", "bin", name),
                Path.Combine("C:\\ffmpeg\\bin", name)
            };
            string environmentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            candidates.AddRange(environmentPath.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory.Trim(), name)));
            string found = candidates.FirstOrDefault(File.Exists);
            if (String.IsNullOrWhiteSpace(found)) throw new FileNotFoundException("未找到 " + name);
            return found;
        }

        private static double SumDurations(string text, string marker)
        {
            double sum = 0;
            int index = 0;
            while ((index = text.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase)) >= 0) {
                index += marker.Length;
                double value = ParseNumberAt(text, index);
                if (!Double.IsNaN(value)) sum += Math.Max(0, value);
            }
            return sum;
        }

        private static double SumFreezeDurations(string text)
        {
            var starts = new Queue<double>();
            double sum = 0;
            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                int start = line.IndexOf("freeze_start:", StringComparison.OrdinalIgnoreCase);
                if (start >= 0) {
                    double value = ParseNumberAt(line, start + "freeze_start:".Length);
                    if (!Double.IsNaN(value)) starts.Enqueue(value);
                }
                int end = line.IndexOf("freeze_end:", StringComparison.OrdinalIgnoreCase);
                if (end >= 0 && starts.Count > 0) {
                    double value = ParseNumberAt(line, end + "freeze_end:".Length);
                    double begin = starts.Dequeue();
                    if (!Double.IsNaN(value) && value >= begin) sum += value - begin;
                }
            }
            return sum;
        }

        private static double ParseMetric(string text, string marker)
        {
            int index = text.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return index < 0 ? Double.NaN : ParseNumberAt(text, index + marker.Length);
        }

        private static double ParseNumberAt(string text, int index)
        {
            while (index < text.Length && Char.IsWhiteSpace(text[index])) index++;
            int end = index;
            while (end < text.Length && (Char.IsDigit(text[end]) || text[end] == '.' || text[end] == '-')) end++;
            double value;
            return end > index && Double.TryParse(text.Substring(index, end - index),
                NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : Double.NaN;
        }

        private static double ParseDouble(string value)
        {
            double result;
            return Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        private static double ParseRate(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return 0;
            string[] parts = value.Split('/');
            if (parts.Length == 2) {
                double a = ParseDouble(parts[0]);
                double b = ParseDouble(parts[1]);
                return b == 0 ? 0 : a / b;
            }
            return ParseDouble(value);
        }

        private static string Sha256(string path)
        {
            using (FileStream input = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }

        private static void AtomicJson(string path, object value)
        {
            string temp = path + ".tmp";
            string json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Serialize(value);
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static string Quote(string value) { return "\"" + (value ?? "").Replace("\"", "\\\"") + "\""; }
        private static string Tail(string value, int length)
        {
            if (String.IsNullOrEmpty(value) || value.Length <= length) return value ?? "";
            return value.Substring(value.Length - length);
        }

        private sealed class ProbeDocument
        {
            public List<ProbeStream> streams { get; set; }
            public ProbeFormat format { get; set; }
            // Keep convenience accessors semantically distinct from ffprobe's
            // lower-case JSON member names. JavaScriptSerializer resolves
            // members case-insensitively on .NET Framework, so pairs such as
            // streams/Streams and format/Format throw AmbiguousMatchException.
            public List<ProbeStream> StreamItems { get { return streams ?? new List<ProbeStream>(); } }
            public ProbeFormat FormatInfo { get { return format; } }
        }

        private sealed class ProbeFormat { public string duration { get; set; } }

        private sealed class ProbeStream
        {
            public string codec_type { get; set; }
            public string codec_name { get; set; }
            public int width { get; set; }
            public int height { get; set; }
            public string avg_frame_rate { get; set; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CapCutBatchClient
{
    public interface ITaskSamplePreprocessor
    {
        void Prepare(VideoTask task, int timeoutSeconds, CancellationToken token);
    }

    // Converts a shared sample into a task-scoped, silent, constant-frame-rate
    // clip before CapCut sees it.  This replaces brittle Speed/Animation panel
    // clicks without ever touching the source material library.
    public interface ITaskSampleRecovery
    {
        bool TryRestorePreparedSample(VideoTask task, CancellationToken token);
    }

    internal static class PreparedSampleRecoveryPolicy
    {
        public static bool Restore(VideoTask task, string prepared, double targetSeconds,
            Func<string, bool> exists, Action<string, double> verify)
        {
            if (task == null || String.IsNullOrWhiteSpace(task.Id) ||
                String.IsNullOrWhiteSpace(task.SamplePath) ||
                !String.IsNullOrWhiteSpace(task.PreparedSamplePath) ||
                targetSeconds <= 0.10 || Double.IsNaN(targetSeconds) ||
                Double.IsInfinity(targetSeconds) || !exists(prepared)) return false;
            // Never change task facts until read-only media verification succeeds.
            verify(prepared, targetSeconds);
            if (String.IsNullOrWhiteSpace(task.OriginalSamplePath))
                task.OriginalSamplePath = task.SamplePath;
            task.PreparedSamplePath = prepared;
            task.PreparedSampleTargetSeconds = targetSeconds;
            task.SamplePath = prepared;
            task.SampleSpeed = 1.0;
            return true;
        }
    }

    public sealed class FfmpegSamplePreprocessor : ITaskSamplePreprocessor, ITaskSampleRecovery
    {
        private readonly AppLogger logger;
        private readonly string ffmpegPath;
        private readonly string ffprobePath;

        public FfmpegSamplePreprocessor(AppLogger logger)
        {
            this.logger = logger ?? new AppLogger();
            ffmpegPath = FindExecutable("ffmpeg.exe");
            ffprobePath = FindExecutable("ffprobe.exe");
        }

        public void Prepare(VideoTask task, int timeoutSeconds, CancellationToken token)
        {
            if (task == null) throw new ArgumentNullException("task");
            string source = String.IsNullOrWhiteSpace(task.OriginalSamplePath)
                ? task.SamplePath : task.OriginalSamplePath;
            if (String.IsNullOrWhiteSpace(source)) return;
            source = Path.GetFullPath(source);
            if (!File.Exists(source)) throw new FileNotFoundException("Sample source does not exist", source);

            MediaMetadata video = WindowsMediaMetadataReader.Read(task.VideoPath);
            MediaMetadata audio = WindowsMediaMetadataReader.Read(task.AudioPath);
            double targetSeconds = audio.DurationSeconds - video.DurationSeconds;
            if (targetSeconds <= 0.10) {
                task.OriginalSamplePath = source;
                task.PreparedSamplePath = "";
                task.PreparedSampleTargetSeconds = 0;
                task.SamplePath = "";
                task.SampleSpeed = 0;
                logger.Info("Prepared sample skipped because independent audio is not longer than the main video.");
                return;
            }

            string prepared = ResolvePreparedPath(task, targetSeconds);
            task.OriginalSamplePath = source;
            task.PreparedSamplePath = prepared;
            task.PreparedSampleTargetSeconds = targetSeconds;

            if (File.Exists(prepared)) {
                VerifyPrepared(prepared, targetSeconds, token);
                task.SamplePath = prepared;
                task.SampleSpeed = 1.0;
                logger.Info("Reusing verified task-scoped prepared sample: " + Path.GetFileName(prepared));
                return;
            }

            string partial = prepared + ".partial.mp4";
            if (File.Exists(partial)) {
                // A previous FFmpeg timeout/crash may have left the canonical partial
                // path behind.  Do not turn that stale artifact into a permanent
                // retry lock.  Reuse it only when it is a complete, valid sample;
                // otherwise quarantine it and regenerate a fresh artifact.
                if (TryReusePreparedPartial(partial, prepared, targetSeconds, token, task))
                    return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(prepared));
            string target = targetSeconds.ToString("0.000", CultureInfo.InvariantCulture);
            string filter = "scale=1080:1920:force_original_aspect_ratio=decrease," +
                "pad=1080:1920:(ow-iw)/2:(oh-ih)/2:black,setsar=1,fps=30,format=yuv420p";
            string arguments = "-hide_banner -nostdin -y -stream_loop -1 -i " + Quote(source) +
                " -t " + target + " -map 0:v:0 -an -vf " + Quote(filter) +
                " -r 30 -c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p -movflags +faststart " +
                Quote(partial);
            logger.Info(String.Format(CultureInfo.InvariantCulture,
                "FFmpeg preparing task-scoped sample: target={0:0.000}s source={1}",
                targetSeconds, Path.GetFileName(source)));
            RunProcess(ffmpegPath, arguments, Math.Max(60, timeoutSeconds), token, "FFmpeg sample preparation");
            VerifyPrepared(partial, targetSeconds, token);
            File.Move(partial, prepared);
            task.SamplePath = prepared;
            task.SampleSpeed = 1.0;
            logger.Info("Prepared sample verified: " + Path.GetFileName(prepared));
        }

        private bool TryReusePreparedPartial(string partial, string prepared, double targetSeconds,
            CancellationToken token, VideoTask task)
        {
            if (!CanOpenExclusively(partial))
                throw new IOException("Prepared sample partial file is still being written: " + partial);
            try {
                VerifyPrepared(partial, targetSeconds, token);
                if (File.Exists(prepared)) File.Delete(prepared);
                File.Move(partial, prepared);
                task.SamplePath = prepared;
                task.SampleSpeed = 1.0;
                logger.Info("Reused previously completed prepared sample partial: " + Path.GetFileName(prepared));
                return true;
            }
            catch (Exception ex) {
                string diagnostic = QuarantinePartial(partial);
                logger.Warn("Quarantined invalid prepared sample partial and will regenerate: " +
                    Path.GetFileName(diagnostic) + " (" + ex.Message + ")");
                return false;
            }
        }

        private static bool CanOpenExclusively(string path)
        {
            try {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static string QuarantinePartial(string partial)
        {
            string directory = Path.GetDirectoryName(partial);
            string stem = Path.GetFileName(partial);
            string diagnostic;
            do {
                diagnostic = Path.Combine(directory, stem + ".diagnostic-" +
                    DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture));
            } while (File.Exists(diagnostic));
            File.Move(partial, diagnostic);
            return diagnostic;
        }
        public bool TryRestorePreparedSample(VideoTask task, CancellationToken token)
        {
            if (task == null || String.IsNullOrWhiteSpace(task.Id) ||
                String.IsNullOrWhiteSpace(task.SamplePath) ||
                !String.IsNullOrWhiteSpace(task.PreparedSamplePath)) return false;
            token.ThrowIfCancellationRequested();
            double targetSeconds = WindowsMediaMetadataReader.Read(task.AudioPath).DurationSeconds
                - WindowsMediaMetadataReader.Read(task.VideoPath).DurationSeconds;
            if (targetSeconds <= 0.10) return false;
            // Same deterministic identity and validation as Prepare(), without
            // generating media or searching another task's artifacts.
            string prepared = ResolvePreparedPath(task, targetSeconds);
            bool restored = PreparedSampleRecoveryPolicy.Restore(task, prepared,
                targetSeconds, File.Exists,
                delegate(string path, double seconds) { VerifyPrepared(path, seconds, token); });
            if (restored) logger.Info("已恢复经媒体校验的当前任务专属样品身份；先核验完整时间线，不重复导入样品");
            return restored;
        }

        private string ResolvePreparedPath(VideoTask task, double targetSeconds)
        {
            string root = Path.Combine(AppPaths.DataDirectory, "prepared-samples");
            string id = String.IsNullOrWhiteSpace(task.Id) ? Guid.NewGuid().ToString("N") : task.Id;
            id = String.Concat(id.Where(char.IsLetterOrDigit));
            if (id.Length > 32) id = id.Substring(0, 32);
            string durationToken = Math.Round(targetSeconds * 1000.0, MidpointRounding.AwayFromZero)
                .ToString(CultureInfo.InvariantCulture);
            return Path.Combine(root, id + "-" + durationToken + "ms.mp4");
        }

        private void VerifyPrepared(string path, double targetSeconds, CancellationToken token)
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= 0)
                throw new InvalidDataException("Prepared sample was not created: " + path);
            ProbeDocument document = Probe(path, token);
            ProbeStream video = document.Streams == null ? null : document.Streams.FirstOrDefault(item =>
                String.Equals(item.codec_type, "video", StringComparison.OrdinalIgnoreCase));
            int audioCount = document.Streams == null ? 0 : document.Streams.Count(item =>
                String.Equals(item.codec_type, "audio", StringComparison.OrdinalIgnoreCase));
            if (video == null || video.width <= 0 || video.height <= 0 ||
                !String.Equals(video.codec_name, "h264", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(video.pix_fmt, "yuv420p", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Prepared sample is not a valid H.264 yuv420p video.");
            if (audioCount != 0) throw new InvalidDataException("Prepared sample must not contain an audio stream.");
            double frameRate = ParseRate(video.avg_frame_rate);
            double tolerance = Math.Max(0.10, frameRate > 0 ? 2.0 / frameRate : 0.10);
            double duration = document.Format == null ? 0 : ParseNumber(document.Format.duration);
            if (duration <= 0 || Math.Abs(duration - targetSeconds) > tolerance)
                throw new InvalidDataException(String.Format(CultureInfo.InvariantCulture,
                    "Prepared sample duration mismatch: expected {0:0.000}s, actual {1:0.000}s, tolerance {2:0.000}s",
                    targetSeconds, duration, tolerance));
        }

        private ProbeDocument Probe(string path, CancellationToken token)
        {
            string output = RunProcess(ffprobePath,
                "-v error -show_entries format=duration:stream=codec_type,codec_name,width,height,avg_frame_rate,pix_fmt -of json " + Quote(path),
                30, token, "ffprobe prepared sample");
            var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
            ProbeDocument document = serializer.Deserialize<ProbeDocument>(output);
            if (document == null || document.Streams == null)
                throw new InvalidDataException("ffprobe did not return valid prepared sample metadata.");
            return document;
        }

        private static string FindExecutable(string name)
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new List<string> {
                Path.Combine(baseDirectory, name),
                Path.Combine(baseDirectory, "ffmpeg", "bin", name),
                Path.Combine("C:\\ffmpeg\\bin", name)
            };
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            candidates.AddRange(path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory.Trim(), name)));
            string found = candidates.FirstOrDefault(File.Exists);
            if (String.IsNullOrWhiteSpace(found))
                throw new FileNotFoundException("FFmpeg executable was not found: " + name);
            return Path.GetFullPath(found);
        }

        private static string RunProcess(string executable, string arguments, int timeoutSeconds,
            CancellationToken token, string operation)
        {
            var output = new StringBuilder();
            var error = new StringBuilder();
            using (var process = new Process()) {
                process.StartInfo = new ProcessStartInfo {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data != null) lock (output) output.AppendLine(e.Data);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data != null) lock (error) error.AppendLine(e.Data);
                };
                if (!process.Start()) throw new InvalidOperationException("Could not start " + operation);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                Stopwatch watch = Stopwatch.StartNew();
                while (!process.WaitForExit(200)) {
                    if (token.IsCancellationRequested || watch.Elapsed > TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))) {
                        try { process.Kill(); } catch { }
                        token.ThrowIfCancellationRequested();
                        throw new TimeoutException(operation + " timed out");
                    }
                }
                process.WaitForExit();
                string stdout; string stderr;
                lock (output) stdout = output.ToString();
                lock (error) stderr = error.ToString();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(operation + " failed (exit " + process.ExitCode + "): " + Tail(stderr, 1200));
                return stdout;
            }
        }

        private static double ParseNumber(string value)
        {
            double parsed;
            return Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static double ParseRate(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return 0;
            string[] parts = value.Split('/');
            double numerator; double denominator;
            if (parts.Length == 2 && Double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out numerator) &&
                Double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator) && denominator != 0)
                return numerator / denominator;
            return ParseNumber(value);
        }

        private static string Tail(string value, int length)
        {
            if (String.IsNullOrWhiteSpace(value)) return "no process error output";
            return value.Length <= length ? value.Trim() : value.Substring(value.Length - length).Trim();
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private sealed class ProbeDocument
        {
            public List<ProbeStream> Streams { get; set; }
            public ProbeFormat Format { get; set; }
        }

        private sealed class ProbeStream
        {
            public string codec_type { get; set; }
            public string codec_name { get; set; }
            public int width { get; set; }
            public int height { get; set; }
            public string avg_frame_rate { get; set; }
            public string pix_fmt { get; set; }
        }

        private sealed class ProbeFormat
        {
            public string duration { get; set; }
        }
    }
}


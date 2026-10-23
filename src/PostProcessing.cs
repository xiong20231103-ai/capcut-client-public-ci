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
    public interface IFinalVideoPostProcessor
    {
        void ReplaceFirstFrame(
            string capCutTemporaryPath,
            string originalSourcePath,
            string finalOutputPath,
            int timeoutSeconds,
            CancellationToken token);
    }

    public static class PostProcessPaths
    {
        // CapCut 7.2 silently truncates the export-name field to 50 characters.
        // Keep the temporary stem within that limit so the file we wait for has
        // exactly the same name as the file CapCut creates.
        private const int CapCutExportStemLimit = 50;

        public static string CapCutTemporaryPath(string finalOutputPath, string taskId)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(finalOutputPath));
            string stem = Path.GetFileNameWithoutExtension(finalOutputPath);
            string extension = Path.GetExtension(finalOutputPath);
            string suffix = String.IsNullOrWhiteSpace(taskId) ? Guid.NewGuid().ToString("N") : taskId;
            if (suffix.Length > 12) suffix = suffix.Substring(0, 12);
            string marker = ".capcut-temp-";
            int availableStemLength = CapCutExportStemLimit - marker.Length - suffix.Length;
            if (availableStemLength < 1)
                throw new InvalidOperationException("CapCut 临时导出文件名无法压缩到安全长度");
            if (stem.Length > availableStemLength) stem = stem.Substring(0, availableStemLength);
            return Path.Combine(directory, stem + marker + suffix + extension);
        }

        public static string WorkingOutputPath(string finalOutputPath, string taskId)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(finalOutputPath));
            string extension = Path.GetExtension(finalOutputPath);
            string suffix = String.IsNullOrWhiteSpace(taskId) ? Guid.NewGuid().ToString("N") : taskId;
            // The post-processor receives the complete temporary export name.
            // Extract the marker that follows ".capcut-temp-" (for example,
            // a0003-e31211) instead of taking characters from the long output
            // stem.  That marker is generated per retry attempt.
            const string capCutMarker = ".capcut-temp-";
            int markerIndex = suffix.LastIndexOf(capCutMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                suffix = suffix.Substring(markerIndex + capCutMarker.Length);
            }
            if (suffix.Length > 12) suffix = suffix.Substring(0, 12);
            // Do not inherit the final output stem here.  Lab reruns and some
            // formal project names can approach MAX_PATH even though the final
            // path itself is still valid.  FFmpeg can create a longer path
            // while the .NET Framework client subsequently reports it as
            // missing, producing a false post-process failure.  The attempt
            // marker already uniquely identifies the task/retry, so a compact
            // same-directory name is both safe and atomically movable.
            return Path.Combine(directory, "ff-first-frame-working-" + suffix + extension);
        }
    }

    public sealed class FfmpegFirstFramePostProcessor : IFinalVideoPostProcessor
    {
        private readonly AppLogger logger;
        private readonly string ffmpegPath;
        private readonly string ffprobePath;

        public FfmpegFirstFramePostProcessor(AppLogger logger)
        {
            this.logger = logger ?? new AppLogger();
            ffmpegPath = FindExecutable("ffmpeg.exe");
            ffprobePath = FindExecutable("ffprobe.exe");
        }

        public void ReplaceFirstFrame(
            string capCutTemporaryPath,
            string originalSourcePath,
            string finalOutputPath,
            int timeoutSeconds,
            CancellationToken token)
        {
            string temporary = Path.GetFullPath(capCutTemporaryPath);
            string source = Path.GetFullPath(originalSourcePath);
            string final = Path.GetFullPath(finalOutputPath);
            if (!File.Exists(temporary)) throw new FileNotFoundException("CapCut 临时成片不存在", temporary);
            if (!File.Exists(source)) throw new FileNotFoundException("原主视频不存在", source);
            if (File.Exists(final)) throw new IOException("最终成片路径已存在，已阻止覆盖: " + final);

            string taskToken = Path.GetFileNameWithoutExtension(temporary);
            string working = PostProcessPaths.WorkingOutputPath(final, taskToken);
            if (File.Exists(working))
                throw new IOException("FFmpeg 工作文件已存在，已保留现场并停止: " + working);

            MediaProbe before = Probe(temporary, Math.Min(30, timeoutSeconds), token);
            if (before.Video == null || before.Video.width <= 0 || before.Video.height <= 0)
                throw new InvalidDataException("无法读取 CapCut 临时成片的视频参数");

            string filter = String.Format(CultureInfo.InvariantCulture,
                "[1:v:0]select='eq(n,0)',setpts=PTS-STARTPTS,scale={0}:{1}:flags=lanczos,setsar=1,format=yuv420p[first];" +
                "[0:v:0][first]overlay=0:0:enable='eq(n,0)':eof_action=pass:shortest=0,format=yuv420p[v]",
                before.Video.width, before.Video.height);
            string arguments = "-hide_banner -nostdin -y" +
                " -i " + Quote(temporary) +
                " -i " + Quote(source) +
                " -filter_complex " + Quote(filter) +
                " -map \"[v]\" -map 0:a? -map_metadata 0 -map_chapters 0" +
                " -c:v libx264 -preset medium -crf 18 -c:a copy -movflags +faststart " + Quote(working);

            logger.Info("FFmpeg 首帧替换开始: " + Path.GetFileName(final));
            RunProcess(ffmpegPath, arguments, Math.Max(60, timeoutSeconds), token, "FFmpeg 首帧替换");
            WaitForStableFile(working, token);
            MediaProbe after = Probe(working, Math.Min(30, timeoutSeconds), token);
            VerifyMediaUnchanged(before, after);
            VerifyFirstFrame(source, working, before.Video.width, before.Video.height,
                Math.Min(60, timeoutSeconds), token);

            File.Move(working, final);
            logger.Info(String.Format(
                "FFmpeg 首帧替换完成并通过验证: {0}，{1}x{2}，帧率 {3}，音频流 {4}",
                Path.GetFileName(final), after.Video.width, after.Video.height,
                after.Video.avg_frame_rate, after.Audio.Count));
        }

        private MediaProbe Probe(string path, int timeoutSeconds, CancellationToken token)
        {
            string arguments = "-v error -show_entries " +
                "format=duration:stream=index,codec_type,codec_name,width,height,avg_frame_rate,sample_rate,channels " +
                "-of json " + Quote(path);
            string json;
            // ffprobe is read-only.  On 10-16 15:37 one run hung for 30 s on a
            // file it read in 0.07 s afterwards; try a fresh process again.
            for (int attempt = 1; ; attempt++) {
                try {
                    json = RunProcess(ffprobePath, arguments, timeoutSeconds, token, "ffprobe 媒体验证");
                    break;
                } catch (TimeoutException) {
                    if (attempt >= 3) throw;
                    logger.Warn("ffprobe 媒体验证超时，重新读取一次（只读）: " + Path.GetFileName(path));
                }
            }
            var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
            ProbeDocument document = serializer.Deserialize<ProbeDocument>(json);
            if (document == null || document.Streams == null)
                throw new InvalidDataException("ffprobe 未返回有效媒体流: " + path);
            var result = new MediaProbe();
            result.Video = document.Streams.FirstOrDefault(item =>
                String.Equals(item.codec_type, "video", StringComparison.OrdinalIgnoreCase));
            result.Audio = document.Streams.Where(item =>
                String.Equals(item.codec_type, "audio", StringComparison.OrdinalIgnoreCase)).ToList();
            double duration;
            if (document.Format != null && Double.TryParse(document.Format.duration,
                NumberStyles.Float, CultureInfo.InvariantCulture, out duration)) result.Duration = duration;
            return result;
        }

        private static void VerifyMediaUnchanged(MediaProbe before, MediaProbe after)
        {
            if (after.Video == null) throw new InvalidDataException("FFmpeg 输出缺少视频流");
            if (before.Video.width != after.Video.width || before.Video.height != after.Video.height)
                throw new InvalidDataException("FFmpeg 输出分辨率发生变化");
            double beforeRate = ParseRate(before.Video.avg_frame_rate);
            double afterRate = ParseRate(after.Video.avg_frame_rate);
            if (beforeRate <= 0 || afterRate <= 0 || Math.Abs(beforeRate - afterRate) > 0.001)
                throw new InvalidDataException(String.Format(CultureInfo.InvariantCulture,
                    "FFmpeg 输出帧率发生变化: {0} -> {1}", before.Video.avg_frame_rate, after.Video.avg_frame_rate));
            if (before.Audio.Count != after.Audio.Count)
                throw new InvalidDataException("FFmpeg 输出音频流数量发生变化");
            for (int index = 0; index < before.Audio.Count; index++) {
                ProbeStream oldAudio = before.Audio[index];
                ProbeStream newAudio = after.Audio[index];
                if (!String.Equals(oldAudio.codec_name, newAudio.codec_name, StringComparison.OrdinalIgnoreCase) ||
                    oldAudio.sample_rate != newAudio.sample_rate || oldAudio.channels != newAudio.channels)
                    throw new InvalidDataException("FFmpeg 输出音频参数发生变化");
            }
            double frameSeconds = beforeRate > 0 ? 1.0 / beforeRate : 0.05;
            if (before.Duration > 0 && after.Duration > 0 &&
                Math.Abs(before.Duration - after.Duration) > Math.Max(0.10, frameSeconds * 2.0))
                throw new InvalidDataException(String.Format(CultureInfo.InvariantCulture,
                    "FFmpeg 输出时长发生变化: {0:0.000}s -> {1:0.000}s", before.Duration, after.Duration));
        }

        private void VerifyFirstFrame(
            string sourcePath,
            string outputPath,
            int width,
            int height,
            int timeoutSeconds,
            CancellationToken token)
        {
            string filter = String.Format(CultureInfo.InvariantCulture,
                "[0:v:0]select='eq(n,0)',setpts=PTS-STARTPTS,format=yuv420p[outfirst];" +
                "[1:v:0]select='eq(n,0)',setpts=PTS-STARTPTS,scale={0}:{1}:flags=lanczos,setsar=1,format=yuv420p[srcfirst];" +
                "[outfirst][srcfirst]ssim",
                width, height);
            string arguments = "-hide_banner -nostdin -i " + Quote(outputPath) +
                " -i " + Quote(sourcePath) + " -filter_complex " + Quote(filter) +
                " -frames:v 1 -f null NUL";
            string log = RunProcess(ffmpegPath, arguments, timeoutSeconds, token, "FFmpeg 第0帧验证", true);
            double ssim = ParseSsim(log);
            if (ssim < 0.90)
                throw new InvalidDataException(String.Format(CultureInfo.InvariantCulture,
                    "最终成片第0帧与原主视频第0帧不一致，SSIM={0:0.000000}", ssim));
            logger.Info(String.Format(CultureInfo.InvariantCulture,
                "最终成片第0帧验证通过: SSIM={0:0.000000}", ssim));
        }

        private static double ParseSsim(string text)
        {
            int marker = text.LastIndexOf("All:", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) throw new InvalidDataException("FFmpeg 未返回第0帧 SSIM 验证结果");
            marker += 4;
            int end = marker;
            while (end < text.Length && (Char.IsDigit(text[end]) || text[end] == '.' || text[end] == '-')) end++;
            double value;
            if (!Double.TryParse(text.Substring(marker, end - marker), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value))
                throw new InvalidDataException("无法解析第0帧 SSIM 验证结果");
            return value;
        }

        private static double ParseRate(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return 0;
            string[] parts = value.Split('/');
            double numerator;
            double denominator;
            if (parts.Length == 2 &&
                Double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out numerator) &&
                Double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator) &&
                denominator != 0) return numerator / denominator;
            return Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out numerator) ? numerator : 0;
        }

        private static void WaitForStableFile(string path, CancellationToken token)
        {
            long previous = -1;
            int stable = 0;
            for (int index = 0; index < 20; index++) {
                token.ThrowIfCancellationRequested();
                if (File.Exists(path)) {
                    long length = new FileInfo(path).Length;
                    stable = length > 0 && length == previous ? stable + 1 : 0;
                    previous = length;
                    if (stable >= 2) return;
                }
                Thread.Sleep(250);
            }
            throw new IOException("FFmpeg 输出文件不存在或大小尚未稳定: " + path);
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
                throw new FileNotFoundException("未找到 " + name + "；请安装 FFmpeg 或放入客户端 ffmpeg\\bin 目录");
            return Path.GetFullPath(found);
        }

        private static string RunProcess(
            string executable,
            string arguments,
            int timeoutSeconds,
            CancellationToken token,
            string operation,
            bool includeStandardErrorOnSuccess = false)
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
                if (!process.Start()) throw new InvalidOperationException("无法启动 " + operation);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                Stopwatch watch = Stopwatch.StartNew();
                while (!process.WaitForExit(200)) {
                    if (token.IsCancellationRequested || watch.Elapsed > TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))) {
                        try { process.Kill(); } catch { }
                        token.ThrowIfCancellationRequested();
                        throw new TimeoutException(operation + "超时");
                    }
                }
                process.WaitForExit();
                string stdout; string stderr;
                lock (output) stdout = output.ToString();
                lock (error) stderr = error.ToString();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(operation + "失败（退出码 " + process.ExitCode + "): " + Tail(stderr, 1800));
                return includeStandardErrorOnSuccess ? stdout + Environment.NewLine + stderr : stdout;
            }
        }

        private static string Tail(string value, int maxLength)
        {
            if (String.IsNullOrWhiteSpace(value)) return "没有错误输出";
            return value.Length <= maxLength ? value.Trim() : value.Substring(value.Length - maxLength).Trim();
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private sealed class MediaProbe
        {
            public ProbeStream Video;
            public List<ProbeStream> Audio = new List<ProbeStream>();
            public double Duration;
        }

        private sealed class ProbeDocument
        {
            public List<ProbeStream> Streams { get; set; }
            public ProbeFormat Format { get; set; }
        }

        private sealed class ProbeStream
        {
            public int index { get; set; }
            public string codec_type { get; set; }
            public string codec_name { get; set; }
            public int width { get; set; }
            public int height { get; set; }
            public string avg_frame_rate { get; set; }
            public string sample_rate { get; set; }
            public int channels { get; set; }
        }

        private sealed class ProbeFormat
        {
            public string duration { get; set; }
        }
    }
}

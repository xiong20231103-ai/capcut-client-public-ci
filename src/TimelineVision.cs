using System;
using System.Drawing;
using System.Windows;
using System.Windows.Automation;

namespace CapCutBatchClient
{
    // Local, dependency-free visual checks for the CapCut timeline.  It deliberately
    // verifies the result of a drag instead of assuming that a mouse drag succeeded.
    internal sealed class TimelineSnapshot
    {
        public Rectangle WindowBounds { get; set; }
        public Rectangle VideoRun { get; set; }
        public Rectangle AudioRun { get; set; }
        public int VideoTrackCenterY { get; set; }
        // Full contiguous timeline-run fingerprint is diagnostic only.  Once a
        // sample is appended, that run necessarily includes new pixels.
        public int VideoFingerprint { get; set; }
        // The fixed, pre-append portion of the main clip.  This is the only
        // fingerprint permitted to decide whether the original clip changed.
        public Rectangle MainClipFingerprintArea { get; set; }
        public int MainClipFingerprint { get; set; }
        public double PixelsPerSecond { get; set; }
        public double Confidence { get; set; }
        public bool HasVideo { get { return !VideoRun.IsEmpty && VideoRun.Width > 0; } }
        public bool HasAudio { get { return !AudioRun.IsEmpty && AudioRun.Width > 0; } }
    }

    internal static class TimelineAppendValidation
    {
        public static void RequireSampleAppend(TimelineSnapshot before, TimelineSnapshot after)
        {
            if (before == null || !before.HasVideo) {
                throw new InvalidOperationException("未识别到拖入前的主视频时间线片段，已停止样品追加");
            }
            if (after == null || !after.HasVideo) {
                throw new InvalidOperationException("样品拖入后未识别到视频时间线片段，已停止后续操作");
            }
            if (Math.Abs(after.VideoRun.Left - before.VideoRun.Left) > 8 ||
                Math.Abs(after.VideoTrackCenterY - before.VideoTrackCenterY) > 12) {
                throw new InvalidOperationException("样品拖入改变了主视频轨道或起点，疑似误替换，已停止导出");
            }
            if (after.VideoRun.Right <= before.VideoRun.Right + 12) {
                throw new InvalidOperationException("样品拖入后没有检测到主视频末尾新增片段；可能出现素材替换或拖入失败，已停止导出");
            }
            int beforeFingerprint = before.MainClipFingerprint != 0
                ? before.MainClipFingerprint : before.VideoFingerprint;
            int afterFingerprint = after.MainClipFingerprint != 0
                ? after.MainClipFingerprint : after.VideoFingerprint;
            if (beforeFingerprint != 0 && afterFingerprint != 0 &&
                FingerprintDistance(beforeFingerprint, afterFingerprint) > 54) {
                throw new InvalidOperationException("样品拖入后原主视频区间画面特征变化过大，疑似主视频被替换，已停止导出");
            }
        }

        public static void RequireSampleEndAligned(TimelineSnapshot snapshot, int sampleLeft, int sampleRight)
        {
            if (snapshot == null || !snapshot.HasAudio) {
                throw new InvalidOperationException("未识别到独立音频波形末端，无法安全验证样品时长");
            }
            if (sampleRight <= sampleLeft) throw new InvalidOperationException("样品片段边界无效");
            int tolerance = Math.Max(3, (int)Math.Ceiling(snapshot.PixelsPerSecond / 24.0));
            if (Math.Abs(sampleRight - snapshot.AudioRun.Right) > tolerance) {
                throw new InvalidOperationException(String.Format(
                    "样品末端未与独立音频末端对齐：样品X={0}，音频X={1}，允许误差={2}px，已停止导出",
                    sampleRight, snapshot.AudioRun.Right, tolerance));
            }
        }

        public static void RequireSampleEndAligned(TimelineSnapshot snapshot, int sampleLeft, int sampleRight,
            double mainVideoSeconds, double audioSeconds, double sampleSeconds, double sampleSpeed)
        {
            if (snapshot == null || !snapshot.HasAudio) {
                throw new InvalidOperationException("未识别到独立音频波形末端，无法安全验证样品时长");
            }
            if (sampleRight <= sampleLeft) throw new InvalidOperationException("样品片段边界无效");

            // CapCut draws the video and audio track ends on different rows. Their
            // anti-aliasing and rounded clip borders can differ by several pixels
            // even when the underlying media differs by only a few milliseconds.
            // The already-probed media durations are therefore the hard gate; the
            // pixel endpoints remain useful only as placement evidence/diagnostics.
            if (mainVideoSeconds > 0 && audioSeconds > 0 && sampleSeconds > 0 && sampleSpeed > 0) {
                double expectedEnd = mainVideoSeconds + sampleSeconds / sampleSpeed;
                const double durationToleranceSeconds = 0.10;
                double difference = Math.Abs(expectedEnd - audioSeconds);
                if (difference > durationToleranceSeconds) {
                    throw new InvalidOperationException(String.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "样品与独立音频真实时长不匹配：主视频={0:0.000}s，样品={1:0.000}s，速度={2:0.000}x，预期末端={3:0.000}s，音频末端={4:0.000}s，差值={5:0.000}s，允许误差={6:0.000}s，已停止导出",
                        mainVideoSeconds, sampleSeconds, sampleSpeed, expectedEnd,
                        audioSeconds, difference, durationToleranceSeconds));
                }
                return;
            }

            // Compatibility fallback for old/manual tasks whose media metadata
            // cannot be read. It remains fail-closed instead of silently exporting.
            RequireSampleEndAligned(snapshot, sampleLeft, sampleRight);
        }

        private static int FingerprintDistance(int left, int right)
        {
            return Math.Abs(left - right);
        }
    }

    // The current client already uses screenshot-based caption and waveform probes.
    // This class keeps the same no-network/no-new-runtime approach for video clips.
    internal sealed class TimelineVision
    {
        private readonly AppConfig config;
        private readonly AppLogger logger;

        public TimelineVision(AppConfig config, AppLogger logger)
        {
            this.config = config;
            this.logger = logger;
        }

        public TimelineSnapshot Capture(AutomationElement root, double mainVideoSeconds)
        {
            return Capture(root, mainVideoSeconds, null);
        }

        public TimelineSnapshot Capture(AutomationElement root, double mainVideoSeconds,
            TimelineSnapshot mainClipReference)
        {
            if (root == null) throw new InvalidOperationException("CapCut 主窗口不可用，无法识别时间线");
            Rect raw = root.Current.BoundingRectangle;
            if (raw.Width < 500 || raw.Height < 400) throw new InvalidOperationException("CapCut 窗口尺寸无效，无法识别时间线");
            var bounds = Rectangle.FromLTRB((int)raw.Left, (int)raw.Top, (int)raw.Right, (int)raw.Bottom);
            using (var bitmap = new Bitmap(bounds.Width, bounds.Height))
            using (Graphics graphics = Graphics.FromImage(bitmap)) {
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size);
                Rectangle video = FindVideoRun(bitmap, bounds);
                if (video.IsEmpty) throw new InvalidOperationException("未能在时间线识别主视频片段；已停止以避免按旧坐标拖入样品");
                Rectangle audio = FindAudioRun(bitmap, bounds, video);
                Rectangle fullRunFingerprintArea = Rectangle.FromLTRB(
                    video.Left - bounds.Left + 12,
                    Math.Min(bitmap.Height - 1, video.Top - bounds.Top + 16),
                    Math.Max(video.Left - bounds.Left + 13, video.Right - bounds.Left - 12),
                    Math.Min(bitmap.Height, video.Bottom - bounds.Top - 3));
                Rectangle mainClipFingerprintArea = RebaseFingerprintArea(
                    mainClipReference, bounds, fullRunFingerprintArea);
                int fingerprint = Fingerprint(bitmap, fullRunFingerprintArea);
                int mainClipFingerprint = Fingerprint(bitmap, mainClipFingerprintArea);
                double pixelsPerSecond = mainVideoSeconds > 0.01 ? video.Width / mainVideoSeconds : 0;
                var result = new TimelineSnapshot {
                    WindowBounds = bounds,
                    VideoRun = video,
                    AudioRun = audio,
                    VideoTrackCenterY = video.Top + Math.Max(4, video.Height / 2),
                    VideoFingerprint = fingerprint,
                    MainClipFingerprintArea = Rectangle.FromLTRB(
                        bounds.Left + mainClipFingerprintArea.Left,
                        bounds.Top + mainClipFingerprintArea.Top,
                        bounds.Left + mainClipFingerprintArea.Right,
                        bounds.Top + mainClipFingerprintArea.Bottom),
                    MainClipFingerprint = mainClipFingerprint,
                    PixelsPerSecond = pixelsPerSecond,
                    Confidence = audio.IsEmpty ? 0.72 : 0.90
                };
                logger.Info(String.Format(
                    "时间线视觉识别：主视频=[{0},{1},{2},{3}]，音频=[{4},{5},{6},{7}]，比例={8:0.00}px/s，置信度={9:0.00}，主片指纹={10}，整轨指纹={11}",
                    video.Left, video.Top, video.Right, video.Bottom,
                    audio.Left, audio.Top, audio.Right, audio.Bottom,
                    pixelsPerSecond, result.Confidence, mainClipFingerprint, fingerprint));
                return result;
            }
        }

        private static Rectangle RebaseFingerprintArea(TimelineSnapshot reference,
            Rectangle currentWindow, Rectangle fallback)
        {
            if (reference == null || reference.MainClipFingerprintArea.IsEmpty ||
                reference.WindowBounds.IsEmpty) return fallback;
            Rectangle previousWindow = reference.WindowBounds;
            Rectangle previousArea = reference.MainClipFingerprintArea;
            Rectangle rebased = Rectangle.FromLTRB(
                previousArea.Left - previousWindow.Left,
                previousArea.Top - previousWindow.Top,
                previousArea.Right - previousWindow.Left,
                previousArea.Bottom - previousWindow.Top);
            Rectangle currentBounds = Rectangle.FromLTRB(0, 0, currentWindow.Width, currentWindow.Height);
            rebased = Rectangle.Intersect(rebased, currentBounds);
            return rebased.Width >= 8 && rebased.Height >= 4 ? rebased : fallback;
        }

        private Rectangle FindVideoRun(Bitmap bitmap, Rectangle window)
        {
            LocatorRule anchor = config.GetRule("TimelineDrop");
            int expectedY = anchor != null && anchor.HasCoordinate
                ? (int)(bitmap.Height * anchor.RelativeY)
                : (int)(bitmap.Height * 0.78);
            int expectedX = anchor != null && anchor.HasCoordinate
                ? (int)(bitmap.Width * anchor.RelativeX)
                : (int)(bitmap.Width * 0.08);
            int yStart = Math.Max((int)(bitmap.Height * 0.55), expectedY - Math.Max(65, bitmap.Height / 12));
            int yEnd = Math.Min((int)(bitmap.Height * 0.88), expectedY + Math.Max(65, bitmap.Height / 12));
            Rectangle best = Rectangle.Empty;
            int bestScore = Int32.MinValue;
            for (int y = yStart; y < yEnd; y += 2) {
                int runStart = -1;
                int lastVideoPixel = -1;
                int bestStart = -1;
                int bestEnd = -1;
                for (int x = Math.Max(0, expectedX - 45); x < (int)(bitmap.Width * 0.88); x++) {
                    if (IsVideoBand(bitmap.GetPixel(x, y))) {
                        if (runStart < 0) runStart = x;
                        lastVideoPixel = x;
                    } else if (runStart >= 0 && x - lastVideoPixel > 6) {
                        // Adjacent CapCut clips have a narrow dark separator.
                        // Keep a single same-track run across that separator so
                        // before/after verification sees the appended segment.
                        int runEnd = lastVideoPixel + 1;
                        if (runEnd - runStart > bestEnd - bestStart) { bestStart = runStart; bestEnd = runEnd; }
                        runStart = -1;
                        lastVideoPixel = -1;
                    }
                }
                if (runStart >= 0 && lastVideoPixel >= runStart && lastVideoPixel + 1 - runStart > bestEnd - bestStart) {
                    bestStart = runStart;
                    bestEnd = lastVideoPixel + 1;
                }
                if (bestStart < 0 || bestEnd - bestStart < 80) continue;
                int distance = Math.Abs(y - expectedY) + Math.Abs(bestStart - expectedX) / 3;
                int score = (bestEnd - bestStart) - distance;
                if (score > bestScore) {
                    bestScore = score;
                    best = Rectangle.FromLTRB(window.Left + bestStart, window.Top + Math.Max(0, y - 5),
                        window.Left + bestEnd, window.Top + Math.Min(bitmap.Height, y + 36));
                }
            }
            return best;
        }

        private static Rectangle FindAudioRun(Bitmap bitmap, Rectangle window, Rectangle video)
        {
            int videoY = video.Top - window.Top;
            int videoLeft = video.Left - window.Left;
            int yStart = Math.Min(bitmap.Height - 1, Math.Max(videoY + video.Height + 16, (int)(bitmap.Height * 0.70)));
            int yEnd = Math.Min(bitmap.Height, (int)(bitmap.Height * 0.94));
            int bestLeft = -1;
            int bestRight = -1;
            int bestY = -1;
            for (int y = yStart; y < yEnd; y += 2) {
                int rowCount = 0;
                int rowLeft = Int32.MaxValue;
                int rowRight = -1;
                // The narration waveform begins at the same timeline zero as
                // the main clip.  Restrict the search to that lane instead of
                // aggregating unrelated blue toolbar glyphs on the right.
                int xStart = Math.Max(0, videoLeft - 42);
                int xEnd = Math.Min(bitmap.Width, (int)(bitmap.Width * 0.88));
                for (int x = xStart; x < xEnd; x++) {
                    if (!IsWaveformBlue(bitmap.GetPixel(x, y))) continue;
                    rowCount++;
                    rowLeft = Math.Min(rowLeft, x);
                    rowRight = Math.Max(rowRight, x);
                }
                if (rowCount >= 30 && rowRight > rowLeft && Math.Abs(rowLeft - videoLeft) <= 48) {
                    if (rowRight - rowLeft > bestRight - bestLeft) {
                        bestLeft = rowLeft;
                        bestRight = rowRight;
                        bestY = y;
                    }
                }
            }
            if (bestRight <= bestLeft || bestY < 0) return Rectangle.Empty;
            return Rectangle.FromLTRB(window.Left + bestLeft, window.Top + bestY - 4,
                window.Left + bestRight + 2, window.Top + bestY + 5);
        }

        private static bool IsVideoBand(Color color)
        {
            // CapCut's current dark theme renders the video-clip header as a
            // muted teal (#065156 in the real 2560x1440 test), not the bright
            // cyan used by older releases.  Audio waveforms are distinctly
            // bluer (their B channel is usually 35+ above G), so require the
            // two teal channels to stay close together.
            // An inline modal dims the editor to roughly 40% brightness while
            // it is being dismissed.  Keep the hue test valid in that state,
            // so a delayed repaint cannot turn a successful append into an
            // unverifiable one.
            return color.G >= 25 && color.B >= 28 && color.G >= color.R + 20 &&
                color.B >= color.G - 3 && color.B <= color.G + 34;
        }

        private static bool IsWaveformBlue(Color color)
        {
            return color.B >= 90 && color.B >= color.R + 35 && color.B >= color.G - 25;
        }

        private static int Fingerprint(Bitmap bitmap, Rectangle area)
        {
            if (area.Width < 8 || area.Height < 4) return 0;
            long sum = 0;
            int count = 0;
            for (int y = area.Top; y < area.Bottom; y += 6) {
                for (int x = area.Left; x < area.Right; x += 12) {
                    Color color = bitmap.GetPixel(x, y);
                    sum += color.R * 3 + color.G * 5 + color.B * 2;
                    count++;
                }
            }
            return count == 0 ? 0 : (int)(sum / count);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace CapCutBatchClient
{
    internal static class CaptionRecognitionStatePolicy
    {
        private static readonly string[] ProgressMarkers = {
            "字幕识别中", "正在识别", "识别中", "generatingcaptions",
            "recognizingcaptions", "generatingsubtitles"
        };

        internal static string Normalize(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            char[] compact = value.Trim().ToLowerInvariant()
                .Where(character => !Char.IsWhiteSpace(character) &&
                    character != '.' && character != '\u2026' &&
                    character != '\u00b7' && character != ':' &&
                    character != '\uff1a')
                .ToArray();
            return new string(compact);
        }

        internal static bool ContainsProgressMarker(string value)
        {
            string normalized = Normalize(value);
            return ProgressMarkers.Any(marker =>
                normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static bool IsCommandAccepted(bool progressSurface,
            bool subtitleTrack, bool completionMarker, int absentButtonFrames)
        {
            // A CapCut progress dialog is modal but leaves the disabled start
            // button painted behind it.  The visible button must therefore
            // never veto the stronger progress evidence.
            return progressSurface || subtitleTrack || completionMarker ||
                absentButtonFrames >= 2;
        }

        // Successful recognitions finish within 110 s (537 runs, 2026-10).
        // Past this point an idle panel means the first click never started.
        internal const int StallCheckSeconds = 150;

        // Re-click only when CapCut is visibly idle: no captions persisted,
        // no progress surface, and the start button is still offered.
        // Anything else (progress, unknown state) keeps waiting untouched.
        internal static bool ShouldReclickStalledRecognition(bool progressSurface,
            bool startButtonVisible, bool draftHasCaptions)
        {
            return !progressSurface && startButtonVisible && !draftHasCaptions;
        }
    }

    internal static class ExportCompletionClosePolicy
    {
        // A window close is only safe on a proven completion page: before the
        // export finishes the same window is the settings page, and closing it
        // would cancel the export.
        internal static bool MayPostClose(bool completionByOcr, bool completionByUi,
            bool stableOutputKnown, bool sameCapCutProcess)
        {
            return sameCapCutProcess && (completionByOcr || completionByUi || stableOutputKnown);
        }
    }

    internal sealed class AudioTimelineEvidence
    {
        public int LaneCount { get; set; }
        public int FirstWaveformX { get; set; }
        public int TimelineZeroX { get; set; }
        public Rectangle ScanArea { get; set; }
        public bool HasWaveform { get { return LaneCount > 0; } }
        public bool IsAligned { get { return HasWaveform && FirstWaveformX <= TimelineZeroX + 12; } }
    }

    internal static class AudioTimelineVision
    {
        private sealed class LaneBand
        {
            public int Top;
            public int Bottom;
            public int Left = Int32.MaxValue;
            public int Right = -1;
            public int Samples;
            public int Rows;
        }

        public static AudioTimelineEvidence Analyze(Bitmap bitmap, int timelineZeroX)
        {
            if (bitmap == null) throw new ArgumentNullException("bitmap");
            int left = Math.Max(0, timelineZeroX - 5);
            int right = Math.Min(bitmap.Width, (int)Math.Round(bitmap.Width * 0.60));
            int top = Math.Max(0, (int)Math.Round(bitmap.Height * 0.73));
            int bottom = Math.Min(bitmap.Height, (int)Math.Round(bitmap.Height * 0.96));
            var evidence = new AudioTimelineEvidence {
                FirstWaveformX = -1,
                TimelineZeroX = timelineZeroX,
                ScanArea = Rectangle.FromLTRB(left, top, right, bottom)
            };
            if (right <= left || bottom <= top) return evidence;

            var bands = new List<LaneBand>();
            LaneBand current = null;
            int lastEvidenceY = -100;
            for (int y = top; y < bottom; y += 2) {
                int rowSamples = 0;
                int rowLeft = Int32.MaxValue;
                int rowRight = -1;
                for (int x = left; x < right; x += 2) {
                    if (!IsAudioClipBlue(bitmap.GetPixel(x, y))) continue;
                    rowSamples++;
                    rowLeft = Math.Min(rowLeft, x);
                    rowRight = Math.Max(rowRight, x);
                }

                // A video clip has a short teal title strip and may contain many
                // incidental blue pixels in its thumbnails.  A narration clip,
                // however, has a broad dark-blue body for roughly an entire lane.
                // Count the body, not individual waveform spikes.
                int rowSlots = rowRight > rowLeft ? ((rowRight - rowLeft) / 2) + 1 : 0;
                double rowDensity = rowSlots > 0 ? (double)rowSamples / rowSlots : 0;
                // Timeline thumbnails can contain a wide blue object in the source
                // video (the current real failure has densities below 0.38).  An
                // independent audio clip has a continuous navy body behind its
                // waveform (the same capture is above 0.80).  Require that body
                // density so imported media or video thumbnails cannot masquerade
                // as an already-placed narration lane.
                bool rowHasWaveform = rowSamples >= 40 && rowRight - rowLeft >= 100 &&
                    rowDensity >= 0.55;
                if (!rowHasWaveform) continue;
                if (current == null || y - lastEvidenceY > 4) {
                    current = new LaneBand { Top = y, Bottom = y };
                    bands.Add(current);
                }
                current.Bottom = y;
                current.Left = Math.Min(current.Left, rowLeft);
                current.Right = Math.Max(current.Right, rowRight);
                current.Samples += rowSamples;
                current.Rows++;
                lastEvidenceY = y;
            }

            List<LaneBand> valid = bands
                .Where(band => band.Rows >= 10 && band.Bottom - band.Top >= 20 &&
                    band.Samples >= 500 && band.Right - band.Left >= 100)
                .ToList();
            evidence.LaneCount = valid.Count;
            if (valid.Count > 0) evidence.FirstWaveformX = valid.Min(band => band.Left);
            return evidence;
        }

        private static bool IsAudioClipBlue(Color color)
        {
            // Current CapCut narration lanes use a navy body (for example
            // 14,48,88) with brighter blue waveform peaks.  The main video title
            // bar is teal (for example 0,77,82) and must not be counted as audio.
            return color.B >= 72 && color.B >= color.R + 34 && color.B >= color.G + 22;
        }
    }

    internal static class CaptionRecognitionButtonVision
    {
        private sealed class Candidate
        {
            public int Top;
            public int Bottom;
            public int Left = Int32.MaxValue;
            public int Right = -1;
            public int Samples;
            public int Rows;
            public Rectangle Bounds {
                get {
                    return Rectangle.FromLTRB(Left, Top, Right + 1, Bottom + 1);
                }
            }
            public int Area { get { return Math.Max(0, Right - Left + 1) * Math.Max(0, Bottom - Top + 1); } }
        }

        public static bool TryFind(Bitmap bitmap, out Rectangle button, out string diagnostic)
        {
            button = Rectangle.Empty;
            diagnostic = "no cyan recognition button";
            if (bitmap == null || bitmap.Width < 320 || bitmap.Height < 240) return false;

            int left = (int)Math.Round(bitmap.Width * 0.03);
            int right = (int)Math.Round(bitmap.Width * 0.48);
            int top = (int)Math.Round(bitmap.Height * 0.30);
            int bottom = (int)Math.Round(bitmap.Height * 0.76);
            const int step = 2;
            int columns = Math.Max(1, (right - left + step - 1) / step);
            int rows = Math.Max(1, (bottom - top + step - 1) / step);
            var mask = new bool[columns, rows];
            var visited = new bool[columns, rows];
            for (int gridY = 0; gridY < rows; gridY++) {
                int y = Math.Min(bitmap.Height - 1, top + gridY * step);
                for (int gridX = 0; gridX < columns; gridX++) {
                    int x = Math.Min(bitmap.Width - 1, left + gridX * step);
                    mask[gridX, gridY] = IsActionCyan(bitmap.GetPixel(x, y));
                }
            }

            var candidates = new List<Candidate>();
            int[] deltaX = { -1, 0, 1, -1, 1, -1, 0, 1 };
            int[] deltaY = { -1, -1, -1, 0, 0, 1, 1, 1 };
            for (int seedY = 0; seedY < rows; seedY++) {
                for (int seedX = 0; seedX < columns; seedX++) {
                    if (!mask[seedX, seedY] || visited[seedX, seedY]) continue;
                    var queue = new Queue<Point>();
                    queue.Enqueue(new Point(seedX, seedY));
                    visited[seedX, seedY] = true;
                    var current = new Candidate {
                        Top = top + seedY * step,
                        Bottom = top + seedY * step,
                        Left = left + seedX * step,
                        Right = left + seedX * step
                    };
                    var componentRows = new HashSet<int>();
                    while (queue.Count > 0) {
                        Point point = queue.Dequeue();
                        int x = left + point.X * step;
                        int y = top + point.Y * step;
                        current.Left = Math.Min(current.Left, x);
                        current.Right = Math.Max(current.Right, x);
                        current.Top = Math.Min(current.Top, y);
                        current.Bottom = Math.Max(current.Bottom, y);
                        current.Samples++;
                        componentRows.Add(point.Y);
                        for (int direction = 0; direction < deltaX.Length; direction++) {
                            int nextX = point.X + deltaX[direction];
                            int nextY = point.Y + deltaY[direction];
                            if (nextX < 0 || nextY < 0 || nextX >= columns || nextY >= rows ||
                                visited[nextX, nextY] || !mask[nextX, nextY]) continue;
                            visited[nextX, nextY] = true;
                            queue.Enqueue(new Point(nextX, nextY));
                        }
                    }
                    current.Rows = componentRows.Count;
                    candidates.Add(current);
                }
            }

            List<Candidate> valid = candidates
                .Where(candidate => candidate.Rows >= 5 && candidate.Samples >= 45 &&
                    candidate.Bounds.Width >= 45 && candidate.Bounds.Height >= 8 &&
                    candidate.Bounds.Width >= candidate.Bounds.Height * 2)
                .OrderByDescending(candidate => candidate.Area)
                .ToList();
            if (valid.Count == 0) return false;
            if (valid.Count > 1 && valid[1].Area >= valid[0].Area * 0.78) {
                diagnostic = "multiple similarly sized cyan buttons in caption panel";
                return false;
            }
            button = valid[0].Bounds;
            diagnostic = String.Format("caption cyan button x={0}, y={1}, w={2}, h={3}, candidates={4}",
                button.X, button.Y, button.Width, button.Height, valid.Count);
            return true;
        }

        public static bool IsStable(Rectangle previous, Rectangle current, int tolerance)
        {
            if (previous.IsEmpty || current.IsEmpty) return false;
            return Math.Abs(previous.Left - current.Left) <= tolerance &&
                Math.Abs(previous.Top - current.Top) <= tolerance &&
                Math.Abs(previous.Width - current.Width) <= tolerance * 2 &&
                Math.Abs(previous.Height - current.Height) <= tolerance * 2;
        }

        private static bool IsActionCyan(Color color)
        {
            return color.G >= 120 && color.B >= 120 && color.R <= 115 &&
                color.G >= color.R + 25 && color.B >= color.R + 25;
        }
    }
}

using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CapCutBatchClient
{
    internal sealed class DraftTitleOcrMatch
    {
        internal string Text { get; set; }
        internal Rectangle Bounds { get; set; }
    }

    // CapCut's QML draft cards are not exposed to UI Automation. The visible
    // draft name is nevertheless a stable, task-specific anchor. Windows OCR
    // is executed by a bundled, local-only PowerShell helper so the production
    // executable does not depend on optional OCR packages or online services.
    internal static class DraftTitleOcr
    {
        internal static bool TryFindUnique(string imagePath, string expectedTitle,
            out DraftTitleOcrMatch match, out string diagnostic)
        {
            match = null;
            diagnostic = "";
            if (String.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) {
                diagnostic = "homepage screenshot is missing";
                return false;
            }
            if (String.IsNullOrWhiteSpace(expectedTitle)) {
                diagnostic = "expected draft title is empty";
                return false;
            }

            string helper = Path.Combine(AppPaths.Root, "tools", "find-draft-title-ocr.ps1");
            if (!File.Exists(helper)) {
                diagnostic = "bundled Windows OCR helper is missing";
                return false;
            }

            string powershell = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) {
                diagnostic = "Windows PowerShell runtime is missing";
                return false;
            }

            try {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                string request = Path.Combine(AppPaths.DataDirectory,
                    "draft-title-ocr-request.txt");
                string requestTemporary = request + ".tmp";
                File.WriteAllLines(requestTemporary, new[] {
                    Path.GetFullPath(imagePath),
                    expectedTitle
                }, new UTF8Encoding(false));
                if (File.Exists(request)) File.Replace(requestTemporary, request, null);
                else File.Move(requestTemporary, request);
                var start = new ProcessStartInfo {
                    FileName = powershell,
                    Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                        Quote(helper),
                    WorkingDirectory = AppPaths.Root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (Process process = Process.Start(start)) {
                    if (process == null) {
                        diagnostic = "Windows OCR helper could not be started";
                        return false;
                    }
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(15000)) {
                        try { process.Kill(); } catch { }
                        diagnostic = "Windows OCR helper timed out";
                        return false;
                    }
                    if (process.ExitCode != 0) {
                        diagnostic = "Windows OCR helper failed: " +
                            Compact(String.IsNullOrWhiteSpace(error) ? output : error);
                        return false;
                    }
                    string line = FirstUsefulLine(output);
                    string[] parts = line.Split(new[] { '\t' }, 5);
                    int x, y, width, height;
                    if (parts.Length != 5 ||
                        !Int32.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) ||
                        !Int32.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y) ||
                        !Int32.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) ||
                        !Int32.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out height) ||
                        width <= 0 || height <= 0) {
                        diagnostic = "Windows OCR helper returned an invalid exact-title result: " + line;
                        return false;
                    }
                    match = new DraftTitleOcrMatch {
                        Text = parts[4],
                        Bounds = new Rectangle(x, y, width, height)
                    };
                    diagnostic = String.Format(
                        "exact OCR title '{0}' at x={1}, y={2}, w={3}, h={4}",
                        match.Text, x, y, width, height);
                    return true;
                }
            } catch (Exception ex) {
                diagnostic = "Windows OCR failed: " + ex.Message;
                return false;
            }
        }

        internal static bool TryResolveThumbnailAboveTitle(Bitmap page, Rectangle titleBounds,
            Bitmap targetCover, out Rectangle thumbnail, out double score, out string diagnostic)
        {
            thumbnail = Rectangle.Empty;
            score = 0;
            diagnostic = "";
            if (page == null || targetCover == null || titleBounds.IsEmpty) {
                diagnostic = "missing page, title bounds or target cover";
                return false;
            }

            // Current CapCut cards are approximately square. Search only the
            // narrow column immediately above the exact OCR title. This cannot
            // drift into a sibling card or the More tools strip.
            int nominal = Math.Max(72, Math.Min(160, (int)Math.Round(page.Width * 0.0375)));
            int minimum = Math.Max(64, (int)Math.Round(nominal * 0.82));
            int maximum = Math.Min(180, (int)Math.Round(nominal * 1.18));
            Rectangle best = Rectangle.Empty;
            double bestScore = -1;
            for (int size = minimum; size <= maximum; size += 3) {
                for (int offsetX = -9; offsetX <= 9; offsetX += 3) {
                    for (int gap = 1; gap <= 12; gap += 2) {
                        int x = titleBounds.Left + offsetX;
                        int y = titleBounds.Top - gap - size;
                        var candidate = new Rectangle(x, y, size, size);
                        if (candidate.Left < 0 || candidate.Top < 0 ||
                            candidate.Right > page.Width || candidate.Bottom > page.Height) continue;
                        double candidateScore = DraftCardVision.PerceptualSimilarity(
                            page, candidate, targetCover);
                        if (candidateScore > bestScore) {
                            bestScore = candidateScore;
                            best = candidate;
                        }
                    }
                }
            }
            if (best.IsEmpty || bestScore < 0.42) {
                diagnostic = String.Format(
                    "no target-cover thumbnail immediately above exact OCR title; best={0:0.000}",
                    bestScore);
                return false;
            }
            thumbnail = best;
            score = bestScore;
            diagnostic = String.Format(
                "OCR-anchored thumbnail x={0}, y={1}, w={2}, h={3}, cover={4:0.000}",
                best.X, best.Y, best.Width, best.Height, bestScore);
            return true;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        }

        private static string FirstUsefulLine(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            string[] lines = value.Replace("\r", "").Split('\n');
            foreach (string line in lines) {
                string trimmed = line.Trim().TrimStart('\uFEFF');
                if (!String.IsNullOrWhiteSpace(trimmed)) return trimmed;
            }
            return "";
        }

        private static string Compact(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            string compact = value.Replace("\r", " ").Replace("\n", " ").Trim().TrimStart('\uFEFF');
            return compact.Length <= 1000 ? compact : compact.Substring(0, 1000);
        }

    }

    // Deterministic, local-only OCR for CapCut surfaces that do not expose
    // useful UI Automation names. Unlike DraftTitleOcr this performs a
    // contains-any match because CapCut can append punctuation or publishing
    // guidance to the export-success sentence.
    internal static class VisibleTextOcr
    {
        internal static bool TryContainsAny(string imagePath, string[] expectedTexts,
            out string matchedText, out string diagnostic, int timeoutMilliseconds = 15000)
        {
            matchedText = "";
            diagnostic = "";
            if (String.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) {
                diagnostic = "OCR screenshot is missing";
                return false;
            }
            string[] phrases = (expectedTexts ?? new string[0])
                .Where(value => !String.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (phrases.Length == 0) {
                diagnostic = "OCR marker list is empty";
                return false;
            }

            string helper = Path.Combine(AppPaths.Root, "tools", "find-visible-text-ocr.ps1");
            if (!File.Exists(helper)) {
                diagnostic = "bundled visible-text OCR helper is missing";
                return false;
            }
            string powershell = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) {
                diagnostic = "Windows PowerShell runtime is missing";
                return false;
            }

            try {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                string request = Path.Combine(AppPaths.DataDirectory,
                    "visible-text-ocr-request.txt");
                string temporary = request + ".tmp";
                File.WriteAllLines(temporary,
                    new[] { Path.GetFullPath(imagePath) }.Concat(phrases),
                    new UTF8Encoding(false));
                if (File.Exists(request)) File.Replace(temporary, request, null);
                else File.Move(temporary, request);

                var start = new ProcessStartInfo {
                    FileName = powershell,
                    Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                        Quote(helper),
                    WorkingDirectory = AppPaths.Root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (Process process = Process.Start(start)) {
                    if (process == null) {
                        diagnostic = "visible-text OCR helper could not be started";
                        return false;
                    }
                    var outputRead = process.StandardOutput.ReadToEndAsync();
                    var errorRead = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutMilliseconds)) {
                        try { process.Kill(); } catch { }
                        diagnostic = "visible-text OCR helper timed out";
                        return false;
                    }
                    if (!System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { outputRead, errorRead }, 1000)) {
                        diagnostic = "visible-text OCR streams timed out";
                        return false;
                    }
                    string output = outputRead.Result;
                    string error = errorRead.Result;
                    if (process.ExitCode != 0) {
                        diagnostic = "visible-text OCR helper did not find a marker: " +
                            Compact(String.IsNullOrWhiteSpace(error) ? output : error);
                        return false;
                    }
                    string line = FirstUsefulLine(output);
                    string[] fields = line.Split(new[] { '\t' }, 2);
                    matchedText = fields.Length == 0 ? "" : fields[0].Trim();
                    diagnostic = fields.Length > 1 ? fields[1].Trim() : matchedText;
                    return !String.IsNullOrWhiteSpace(matchedText);
                }
            } catch (Exception ex) {
                diagnostic = "visible-text OCR failed: " + ex.Message;
                return false;
            }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        }

        private static string FirstUsefulLine(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            foreach (string line in value.Replace("\r", "").Split('\n')) {
                string trimmed = line.Trim().TrimStart('\uFEFF');
                if (!String.IsNullOrWhiteSpace(trimmed)) return trimmed;
            }
            return "";
        }

        private static string Compact(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            string compact = value.Replace("\r", " ").Replace("\n", " ")
                .Trim().TrimStart('\uFEFF');
            return compact.Length <= 1000 ? compact : compact.Substring(0, 1000);
        }
    }
}

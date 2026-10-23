using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace CapCutBatchClient
{
    // Finds the first visible CapCut draft card from the rendered home page.  QML
    // cards are not exposed through UI Automation in the installed CapCut build,
    // so this deliberately uses the screenshot as a *locator* only.  The caller
    // still proves the actual activated draft by draft_id and source video after
    // the one permitted click.
    internal static class DraftCardVision
    {
        internal sealed class Detection
        {
            internal Rectangle Evidence { get; set; }
            internal Rectangle Thumbnail { get; set; }
            internal Rectangle[] TopRowCards { get; set; }
            internal int RowTop { get; set; }
            internal int RowBottom { get; set; }
        }

        internal sealed class TopRowStabilityTracker
        {
            private Rectangle anchor = Rectangle.Empty;
            private int stableCount;

            internal bool Observe(Rectangle candidate, int tolerancePixels, int requiredFrames,
                out Rectangle accepted, out string diagnostic)
            {
                accepted = Rectangle.Empty;
                diagnostic = "";
                if (candidate.IsEmpty) {
                    diagnostic = "no draft-card candidate";
                    return false;
                }
                if (anchor.IsEmpty || candidate.Top < anchor.Top - tolerancePixels) {
                    anchor = candidate;
                    stableCount = 1;
                } else if (candidate.Top > anchor.Top + tolerancePixels) {
                    // A QML animation may make row two visually stronger after
                    // row one has already appeared.  Never let that lower row
                    // replace the top-row anchor during the same reopen attempt.
                    diagnostic = String.Format("rejected lower row y={0}; top-row anchor y={1}",
                        candidate.Top, anchor.Top);
                    return false;
                } else if (IsStable(anchor, candidate, tolerancePixels)) {
                    anchor = candidate;
                    stableCount++;
                } else {
                    anchor = candidate;
                    stableCount = 1;
                }
                diagnostic = String.Format("top-row stable {0}/{1}; y={2}",
                    stableCount, requiredFrames, anchor.Top);
                if (stableCount < requiredFrames) return false;
                accepted = anchor;
                return true;
            }
        }

        // A task-scoped CapCut root contains exactly one filesystem-verified
        // draft. QML may still move that card while the home page finishes
        // laying out, so a later/lower observation is a reflow of the same
        // card, not a competing second row.
        internal sealed class UniqueCardStabilityTracker
        {
            private Rectangle anchor = Rectangle.Empty;
            private int stableCount;

            internal bool Observe(Rectangle candidate, int tolerancePixels,
                int requiredFrames, out Rectangle accepted,
                out string diagnostic)
            {
                accepted = Rectangle.Empty;
                if (candidate.IsEmpty) {
                    anchor = Rectangle.Empty;
                    stableCount = 0;
                    diagnostic = "no unique draft-card candidate";
                    return false;
                }

                if (anchor.IsEmpty || !IsStable(anchor, candidate,
                        tolerancePixels)) {
                    string movement = anchor.IsEmpty
                        ? "initial unique-card position"
                        : String.Format("unique-card QML reflow {0},{1} -> {2},{3}",
                            anchor.Left, anchor.Top, candidate.Left,
                            candidate.Top);
                    anchor = candidate;
                    stableCount = 1;
                    diagnostic = String.Format(
                        "{0}; unique-card stable {1}/{2}; x={3}, y={4}",
                        movement, stableCount, requiredFrames,
                        anchor.Left, anchor.Top);
                    return false;
                }

                anchor = candidate;
                stableCount++;
                diagnostic = String.Format(
                    "unique-card stable {0}/{1}; x={2}, y={3}",
                    stableCount, requiredFrames, anchor.Left, anchor.Top);
                if (stableCount < requiredFrames) return false;
                accepted = anchor;
                return true;
            }
        }

        // Exact-title OCR already proves which draft is being observed, so a
        // downward QML reflow is not a competing lower row.  Reset the anchor
        // on any material movement and wait for the exact same title/card to
        // remain still for several consecutive frames.
        internal sealed class ExactTitleStabilityTracker
        {
            private Rectangle anchor = Rectangle.Empty;
            private int stableCount;

            internal bool Observe(Rectangle candidate, int tolerancePixels, int requiredFrames,
                out Rectangle accepted, out string diagnostic)
            {
                accepted = Rectangle.Empty;
                if (candidate.IsEmpty) {
                    stableCount = 0;
                    anchor = Rectangle.Empty;
                    diagnostic = "no exact-title draft candidate";
                    return false;
                }

                if (anchor.IsEmpty || !IsStable(anchor, candidate, tolerancePixels)) {
                    string movement = anchor.IsEmpty
                        ? "initial exact-title position"
                        : String.Format("QML reflow {0},{1} -> {2},{3}",
                            anchor.Left, anchor.Top, candidate.Left, candidate.Top);
                    anchor = candidate;
                    stableCount = 1;
                    diagnostic = String.Format("{0}; exact-title stable {1}/{2}; y={3}",
                        movement, stableCount, requiredFrames, anchor.Top);
                    return false;
                }

                anchor = candidate;
                stableCount++;
                diagnostic = String.Format("exact-title stable {0}/{1}; y={2}",
                    stableCount, requiredFrames, anchor.Top);
                if (stableCount < requiredFrames) return false;
                accepted = anchor;
                return true;
            }
        }

        // Opening a QML draft card is a single-click action.  Keeping the count
        // next to the locator makes tests catch an accidental return to the old
        // double-click implementation.
        internal const int PhysicalOpenClickCount = 1;

        // Two identical frames are enough once root_meta_info has already
        // locked the target to the first item. CapCut's QML home animation can
        // expose the correct first row for only two samples before a lower row
        // becomes visually stronger; waiting for a third frame caused a safe
        // anchor to time out on every retry.
        internal const int RequiredStableFrames = 2;

        // The title-anchored locator can safely wait longer because it cannot
        // confuse another row with the target.  Three frames absorb the delayed
        // More-tools expansion observed on the production CapCut home page.
        internal const int RequiredExactTitleStableFrames = 3;

        // A filesystem-scoped cover fallback already proves that the active
        // lease contains exactly one draft and that the observed thumbnail is
        // the only unique match on the page.  OCR is unavailable in this path,
        // so requiring the third title frame adds no identity evidence and can
        // reject a card while CapCut is still reflowing its QML home surface.
        // Keep two identical observations as the minimum visual stability
        // requirement; the post-click draft_id/source/style verification remains
        // mandatory and is the final identity gate.
        internal const int RequiredUniqueCoverFallbackStableFrames = 2;

        // A CapCut top-level window can exist while the QML home surface is
        // still an empty shell.  A visible window alone is therefore never a
        // safe signal to click a draft card.  The caller must see both the
        // home action and one real draft-thumbnail sample first.
        internal static bool IsHomeReady(bool createProjectVisible, bool firstCardDetected)
        {
            return createProjectVisible && firstCardDetected;
        }

        internal static bool TryFindFirstCard(Bitmap image, out Rectangle card, out string diagnostic)
        {
            Detection detection;
            bool found = TryFindFirstCardDetailed(image, out detection, out diagnostic);
            card = found ? detection.Evidence : Rectangle.Empty;
            return found;
        }

        internal static bool TryFindFirstCardDetailed(Bitmap image, out Detection detection, out string diagnostic)
        {
            detection = null;
            diagnostic = "";
            if (image == null || image.Width < 400 || image.Height < 300) {
                diagnostic = "homepage screenshot is too small";
                return false;
            }

            // A newly saved CapCut draft may render as a completely black
            // thumbnail.  Looking only for bright/colourful pixels skips that
            // first card and can select a later colour-bar draft.
            //
            // IMPORTANT: find the top-most complete thumbnail ROW first.  Only
            // after the row is fixed may we select its left-most card.  The old
            // implementation searched the two axes in the opposite order.  If
            // row two had a stronger image at the same X it selected row two
            // (0726 (1)) while root_meta_info required row one (0726 (15)).
            int left = Math.Max(0, (int)(image.Width * 0.07));
            int right = Math.Min(image.Width - 1, (int)(image.Width * 0.42));
            int top = Math.Max(0, (int)(image.Height * 0.275));
            int bottom = Math.Min(image.Height - 8, (int)(image.Height * 0.47));
            int minimumWidth = Math.Max(38, (int)(image.Width * 0.018));
            int maximumGap = Math.Max(6, (int)(image.Width * 0.004));
            int minimumRowActiveSamples = Math.Max(12, minimumWidth / 4);
            // Toolbar icons and the "more tools" tiles also contain bright
            // pixels, but their rendered bands are only about 20-35 px high.
            // A real CapCut draft thumbnail occupies a substantially taller
            // continuous band (roughly 90-100 px at 2560x1440).  Requiring a
            // complete card-height band prevents the toolbar row from winning
            // merely because it appears above the drafts.
            int minimumRowBandHeight = Math.Max(42, (int)(image.Height * 0.032));
            int maximumRowGap = Math.Max(8, (int)(image.Height * 0.008));

            int rowStart = -1;
            int rowEnd = -1;
            int lastActiveY = -1;
            for (int y = top; y <= bottom; y += 2) {
                int activeSamples = 0;
                for (int x = left; x <= right; x += 2) {
                    if (IsThumbnailPixel(image, x, y)) activeSamples++;
                }
                if (activeSamples >= minimumRowActiveSamples) {
                    if (rowStart < 0) rowStart = y;
                    lastActiveY = y;
                    continue;
                }
                if (rowStart >= 0 && y - lastActiveY > maximumRowGap) {
                    rowEnd = lastActiveY + 2;
                    if (rowEnd - rowStart >= minimumRowBandHeight) break;
                    rowStart = -1;
                    rowEnd = -1;
                    lastActiveY = -1;
                }
            }
            if (rowStart >= 0 && rowEnd < 0) rowEnd = lastActiveY + 2;
            if (rowStart < 0 || rowEnd - rowStart < minimumRowBandHeight) {
                diagnostic = String.Format(
                    "no complete top draft-thumbnail row in scan x={0}-{1}, y={2}-{3}",
                    left, right, top, bottom);
                return false;
            }

            int sampledRows = Math.Max(1, (rowEnd - rowStart) / 3);
            int minimumActiveSamples = Math.Max(6, sampledRows / 9);

            int componentStart = -1;
            int lastActiveX = -1;
            var components = new List<Rectangle>();
            for (int x = left; x <= right; x += 2) {
                int activeSamples = 0;
                for (int y = rowStart; y <= rowEnd; y += 3) {
                    if (IsThumbnailPixel(image, x, y)) activeSamples++;
                }
                bool activeColumn = activeSamples >= minimumActiveSamples;
                if (activeColumn) {
                    if (componentStart < 0) componentStart = x;
                    lastActiveX = x;
                    continue;
                }
                if (componentStart >= 0 && x - lastActiveX > maximumGap) {
                    int componentEnd = lastActiveX + 2;
                    if (componentEnd - componentStart >= minimumWidth)
                        components.Add(new Rectangle(componentStart, rowStart,
                            componentEnd - componentStart, rowEnd - rowStart));
                    componentStart = -1;
                    lastActiveX = -1;
                }
            }
            if (componentStart >= 0) {
                int componentEnd = lastActiveX + 2;
                if (componentEnd - componentStart >= minimumWidth)
                    components.Add(new Rectangle(componentStart, rowStart,
                        componentEnd - componentStart, rowEnd - rowStart));
            }
            Rectangle firstComponent = components.OrderBy(value => value.Left).FirstOrDefault();
            if (firstComponent.IsEmpty) {
                diagnostic = String.Format(
                    "no left-most draft-thumbnail in top row x={0}-{1}, row={2}-{3}",
                    left, right, rowStart, rowEnd);
                return false;
            }

            // Pick a real observed row inside that component for diagnostics.
            // The evidence band proves that this is a rendered thumbnail, but
            // it is intentionally NOT the click target.  On current CapCut
            // builds that band can sit at the very top of the card where a
            // single click opens an AI/transition surface rather than the
            // draft.  The full observed component is the actual thumbnail.
            int bestY = -1;
            int bestScore = -1;
            for (int y = rowStart; y <= rowEnd; y += 2) {
                int score = 0;
                for (int x = firstComponent.Left; x <= firstComponent.Right; x += 2) {
                    if (IsThumbnailPixel(image, x, y)) score++;
                }
                if (score > bestScore) {
                    bestScore = score;
                    bestY = y;
                }
            }
            int minimumRowScore = Math.Max(8, firstComponent.Width / 12);
            if (bestY < 0 || bestScore < minimumRowScore) {
                diagnostic = String.Format("first component had no safe interior row x={0}-{1}, score={2}",
                    firstComponent.Left, firstComponent.Right, bestScore);
                return false;
            }

            int bandTop = Math.Max(rowStart, bestY - 3);
            Rectangle card = new Rectangle(firstComponent.Left, bandTop, firstComponent.Width, 7);
            detection = new Detection {
                Evidence = card,
                Thumbnail = firstComponent,
                TopRowCards = components.OrderBy(value => value.Left).ToArray(),
                RowTop = rowStart,
                RowBottom = rowEnd
            };
            diagnostic = String.Format(
                "vision top-row leftmost thumbnail band x={0}, y={1}, w={2}, h={3}, score={4}, row={5}-{6}, scan={7}-{8}",
                card.X, card.Y, card.Width, card.Height, bestScore,
                rowStart, rowEnd, top, bottom);
            return true;
        }

        // Task-scoped roots contain one filesystem-verified draft.  Unlike the
        // legacy homepage locator, this scans the complete draft area and lets
        // the task cover select the rendered card.  No row number or historical
        // Y anchor participates in the decision.
        internal static bool TryFindUniqueTaskCardByCover(Bitmap image,
            Bitmap targetCover, out Detection detection, out double bestScore,
            out string diagnostic)
        {
            return TryFindUniqueTaskCardByCoverWithinArea(image, targetCover,
                0.275, 0.68, out detection, out bestScore, out diagnostic);
        }

        // CapCut 9.1.0.3879 can place large AI feature cards above the Drafts
        // section and render the task title too small for Windows OCR.  Once the
        // task-scoped root has independently proved that exactly one draft
        // exists, search only the lower-left Drafts area for that draft's own
        // cover.  This deliberately excludes the AI feature strip that caused
        // the old page-wide cover matcher to click the wrong surface.
        internal static bool TryFindUniqueTaskCardByCoverInLowerDraftArea(Bitmap image,
            Bitmap targetCover, out Detection detection, out double bestScore,
            out string diagnostic)
        {
            return TryFindUniqueTaskCardByCoverWithinArea(image, targetCover,
                0.42, 0.86, out detection, out bestScore, out diagnostic);
        }

        private static bool TryFindUniqueTaskCardByCoverWithinArea(Bitmap image,
            Bitmap targetCover, double topRatio, double bottomRatio,
            out Detection detection, out double bestScore,
            out string diagnostic)
        {
            detection = null;
            bestScore = 0;
            diagnostic = "";
            if (image == null || targetCover == null || image.Width < 400 ||
                    image.Height < 300) {
                diagnostic = "draft page or target cover is unavailable";
                return false;
            }

            int left = Math.Max(0, (int)(image.Width * 0.07));
            int right = Math.Min(image.Width - 1,
                (int)(image.Width * 0.42));
            int top = Math.Max(0, (int)(image.Height * topRatio));
            int bottom = Math.Min(image.Height - 8,
                (int)(image.Height * bottomRatio));
            int minimumWidth = Math.Max(38,
                (int)(image.Width * 0.018));
            int maximumGap = Math.Max(6,
                (int)(image.Width * 0.004));
            int minimumRowActiveSamples = Math.Max(12,
                minimumWidth / 4);
            int minimumBandHeight = Math.Max(42,
                (int)(image.Height * 0.032));
            int maximumRowGap = Math.Max(8,
                (int)(image.Height * 0.008));

            // A dark, textured cover can be almost the same luminance as the
            // CapCut page background.  The old absolute-colour threshold then
            // discarded the card before cover matching ever ran.  Estimate the
            // page background from the mostly empty right side of each scan
            // row and detect local contrast instead.  This is still only a
            // candidate generator: task identity and the target-cover score
            // below remain mandatory, and the caller verifies the opened
            // draft_id/source before proceeding.
            Color[] rowBackgrounds = EstimateRowBackgrounds(image, top, bottom);
            const int minimumBackgroundDistanceSquared = 144; // RGB distance 12

            var bands = new List<Rectangle>();
            int rowStart = -1;
            int lastActiveY = -1;
            for (int y = top; y <= bottom; y += 2) {
                int activeSamples = 0;
                for (int x = left; x <= right; x += 2) {
                    if (IsDistinctFromPageBackground(image, x, y,
                            rowBackgrounds, top,
                            minimumBackgroundDistanceSquared)) activeSamples++;
                }
                if (activeSamples >= minimumRowActiveSamples) {
                    if (rowStart < 0) rowStart = y;
                    lastActiveY = y;
                    continue;
                }
                if (rowStart >= 0 && y - lastActiveY > maximumRowGap) {
                    int rowEnd = lastActiveY + 2;
                    if (rowEnd - rowStart >= minimumBandHeight)
                        bands.Add(new Rectangle(left, rowStart,
                            right - left + 1, rowEnd - rowStart));
                    rowStart = -1;
                    lastActiveY = -1;
                }
            }
            if (rowStart >= 0) {
                int rowEnd = lastActiveY + 2;
                if (rowEnd - rowStart >= minimumBandHeight)
                    bands.Add(new Rectangle(left, rowStart,
                        right - left + 1, rowEnd - rowStart));
            }

            var candidates = new List<Rectangle>();
            foreach (Rectangle band in bands) {
                int sampledRows = Math.Max(1, band.Height / 3);
                int minimumActiveSamples = Math.Max(6,
                    sampledRows / 9);
                int componentStart = -1;
                int lastActiveX = -1;
                for (int x = band.Left; x <= band.Right; x += 2) {
                    int activeSamples = 0;
                    for (int y = band.Top; y <= band.Bottom; y += 3) {
                        if (IsDistinctFromPageBackground(image, x, y,
                                rowBackgrounds, top,
                                minimumBackgroundDistanceSquared)) activeSamples++;
                    }
                    if (activeSamples >= minimumActiveSamples) {
                        if (componentStart < 0) componentStart = x;
                        lastActiveX = x;
                        continue;
                    }
                    if (componentStart >= 0 &&
                            x - lastActiveX > maximumGap) {
                        int componentEnd = lastActiveX + 2;
                        if (componentEnd - componentStart >= minimumWidth)
                            candidates.Add(new Rectangle(componentStart,
                                band.Top, componentEnd - componentStart,
                                band.Height));
                        componentStart = -1;
                        lastActiveX = -1;
                    }
                }
                if (componentStart >= 0) {
                    int componentEnd = lastActiveX + 2;
                    if (componentEnd - componentStart >= minimumWidth)
                        candidates.Add(new Rectangle(componentStart,
                            band.Top, componentEnd - componentStart,
                            band.Height));
                }
            }

            if (candidates.Count == 0) {
                diagnostic = String.Format(
                    "no task draft-card candidates in scan x={0}-{1}, y={2}-{3}; " +
                    "background-relative contrast threshold={4}",
                    left, right, top, bottom,
                    Math.Sqrt(minimumBackgroundDistanceSquared));
                return false;
            }
            var scored = candidates.Select(card => new {
                Card = card,
                Score = PerceptualSimilarity(image, card, targetCover)
            }).OrderByDescending(item => item.Score).ToList();
            bestScore = scored[0].Score;
            double second = scored.Skip(1).Select(item => item.Score)
                .DefaultIfEmpty(0).Max();
            diagnostic = String.Format(
                "unique task cover best={0:0.000}, second={1:0.000}, candidates={2}, card={3},{4},{5},{6}",
                bestScore, second, scored.Count, scored[0].Card.X,
                scored[0].Card.Y, scored[0].Card.Width,
                scored[0].Card.Height);
            if (bestScore < 0.42 ||
                    (scored.Count > 1 && bestScore < second + 0.0005)) {
                diagnostic = "draft cover was not a unique page match; " +
                    diagnostic;
                return false;
            }
            Rectangle winner = scored[0].Card;
            detection = new Detection {
                Evidence = winner,
                Thumbnail = winner,
                TopRowCards = new[] { winner },
                RowTop = winner.Top,
                RowBottom = winner.Bottom
            };
            return true;
        }

        private static Color[] EstimateRowBackgrounds(Bitmap image, int top,
            int bottom)
        {
            // Draft cards occupy the left portion of the home page.  Sampling
            // the far-right canvas avoids treating their dark pixels as the
            // page background.  Per-channel medians tolerate small controls,
            // cursor highlights and rendering noise in the sample strip.
            int sampleLeft = Math.Max(0, (int)(image.Width * 0.78));
            int sampleRight = Math.Min(image.Width - 1,
                (int)(image.Width * 0.96));
            int sampleStep = Math.Max(1, (sampleRight - sampleLeft) / 10);
            var backgrounds = new Color[bottom - top + 1];
            var red = new int[12];
            var green = new int[12];
            var blue = new int[12];
            for (int y = top; y <= bottom; y++) {
                int count = 0;
                for (int x = sampleLeft; x <= sampleRight && count < red.Length;
                        x += sampleStep) {
                    Color sample = image.GetPixel(x, y);
                    red[count] = sample.R;
                    green[count] = sample.G;
                    blue[count] = sample.B;
                    count++;
                }
                Array.Sort(red, 0, count);
                Array.Sort(green, 0, count);
                Array.Sort(blue, 0, count);
                int middle = count / 2;
                backgrounds[y - top] = Color.FromArgb(red[middle],
                    green[middle], blue[middle]);
            }
            return backgrounds;
        }

        private static bool IsDistinctFromPageBackground(Bitmap image, int x,
            int y, Color[] rowBackgrounds, int top,
            int minimumDistanceSquared)
        {
            Color pixel = image.GetPixel(x, y);
            Color background = rowBackgrounds[y - top];
            int red = pixel.R - background.R;
            int green = pixel.G - background.G;
            int blue = pixel.B - background.B;
            return red * red + green * green + blue * blue >=
                minimumDistanceSquared;
        }

        internal static bool TryVerifyFirstCardCover(Bitmap homePage, Detection detection, Bitmap targetCover,
            out double firstScore, out double nextScore, out string diagnostic)
        {
            firstScore = 0;
            nextScore = 0;
            diagnostic = "";
            if (homePage == null || targetCover == null || detection == null ||
                detection.TopRowCards == null || detection.TopRowCards.Length == 0) {
                diagnostic = "missing homepage, cover or top-row card candidates";
                return false;
            }

            double[] scores = detection.TopRowCards.Select(card =>
                PerceptualSimilarity(homePage, card, targetCover)).ToArray();
            firstScore = scores[0];
            nextScore = scores.Skip(1).DefaultIfEmpty(0).Max();
            bool unique = scores.Length == 1
                ? firstScore >= 0.42
                // The same source is often reused across neighbouring language
                // drafts, so their covers can be nearly identical.  Root-index
                // order and top-left geometry are independent hard gates; the
                // cover gate only needs the target to be the strict best match,
                // not artificially far from its siblings.
                : firstScore >= 0.42 && firstScore >= nextScore + 0.0005;
            diagnostic = String.Format("cover first={0:0.000}, next={1:0.000}, candidates={2}, scores=[{3}], cards=[{4}]",
                firstScore, nextScore, scores.Length,
                String.Join(",", scores.Select(value => value.ToString("0.000")).ToArray()),
                String.Join(";", detection.TopRowCards.Select(value =>
                    String.Format("{0},{1},{2},{3}", value.X, value.Y, value.Width, value.Height)).ToArray()));
            return unique;
        }

        // Production reopening already has two stronger identity gates:
        // root_meta_info must place the locked draft first, and the editor opened
        // after the click must match draft_id + name + source video.  Covers are
        // routinely identical for sibling language tasks, so the screenshot
        // cover is only a plausibility check for the first card, never a global
        // uniqueness requirement.
        internal static bool TryValidateFirstCardCover(Bitmap homePage, Detection detection, Bitmap targetCover,
            out double firstScore, out double nextScore, out string diagnostic)
        {
            firstScore = 0;
            nextScore = 0;
            diagnostic = "";
            if (homePage == null || targetCover == null || detection == null ||
                detection.TopRowCards == null || detection.TopRowCards.Length == 0) {
                diagnostic = "missing homepage, cover or top-row card candidates";
                return false;
            }
            double[] scores = detection.TopRowCards.Select(card =>
                PerceptualSimilarity(homePage, card, targetCover)).ToArray();
            firstScore = scores[0];
            nextScore = scores.Skip(1).DefaultIfEmpty(0).Max();
            double firstCardScore = firstScore;
            diagnostic = String.Format(
                "cover first={0:0.000}, sibling-max={1:0.000}, candidates={2}, duplicates={3}",
                firstScore, nextScore, scores.Length,
                scores.Skip(1).Any(value => Math.Abs(value - firstCardScore) < 0.0005));
            return firstScore >= 0.42;
        }

        internal static double PerceptualSimilarity(Bitmap page, Rectangle card, Bitmap cover)
        {
            // Compare low-frequency luminance and colour structure.  The cover
            // and the QML thumbnail use different resolutions and overlays, so
            // exact pixels are intentionally ignored.
            const int columns = 8;
            const int rows = 12;
            double squared = 0;
            double edgeSquared = 0;
            int count = 0;
            double[,] pageLuma = new double[rows, columns];
            double[,] coverLuma = new double[rows, columns];
            for (int row = 0; row < rows; row++) {
                for (int column = 0; column < columns; column++) {
                    Color a = Sample(page, card, column, row, columns, rows);
                    Color b = Sample(cover, new Rectangle(0, 0, cover.Width, cover.Height),
                        column, row, columns, rows);
                    double ar = a.R / 255.0, ag = a.G / 255.0, ab = a.B / 255.0;
                    double br = b.R / 255.0, bg = b.G / 255.0, bb = b.B / 255.0;
                    squared += (ar - br) * (ar - br) +
                        (ag - bg) * (ag - bg) + (ab - bb) * (ab - bb);
                    pageLuma[row, column] = ar * 0.299 + ag * 0.587 + ab * 0.114;
                    coverLuma[row, column] = br * 0.299 + bg * 0.587 + bb * 0.114;
                    count += 3;
                }
            }
            int edgeCount = 0;
            for (int row = 0; row < rows; row++) {
                for (int column = 0; column < columns; column++) {
                    if (column + 1 < columns) {
                        double pageEdge = pageLuma[row, column + 1] - pageLuma[row, column];
                        double coverEdge = coverLuma[row, column + 1] - coverLuma[row, column];
                        edgeSquared += (pageEdge - coverEdge) * (pageEdge - coverEdge);
                        edgeCount++;
                    }
                    if (row + 1 < rows) {
                        double pageEdge = pageLuma[row + 1, column] - pageLuma[row, column];
                        double coverEdge = coverLuma[row + 1, column] - coverLuma[row, column];
                        edgeSquared += (pageEdge - coverEdge) * (pageEdge - coverEdge);
                        edgeCount++;
                    }
                }
            }
            double colourSimilarity = Math.Max(0, 1.0 - Math.Sqrt(squared / Math.Max(1, count)));
            double edgeSimilarity = Math.Max(0, 1.0 - Math.Sqrt(edgeSquared / Math.Max(1, edgeCount)));
            return colourSimilarity * 0.35 + edgeSimilarity * 0.65;
        }

        private static Color Sample(Bitmap bitmap, Rectangle bounds, int column, int row,
            int columns, int rows)
        {
            int x = bounds.Left + (int)((column + 0.5) * bounds.Width / columns);
            int y = bounds.Top + (int)((row + 0.5) * bounds.Height / rows);
            x = Math.Max(0, Math.Min(bitmap.Width - 1, x));
            y = Math.Max(0, Math.Min(bitmap.Height - 1, y));
            return bitmap.GetPixel(x, y);
        }

        internal static bool IsClickInsideEvidence(Rectangle evidence, int x, int y)
        {
            return !evidence.IsEmpty &&
                x >= evidence.Left && x < evidence.Right &&
                y >= evidence.Top && y < evidence.Bottom;
        }

        internal static bool IsClickInsideThumbnail(Rectangle thumbnail, int x, int y)
        {
            return IsClickInsideEvidence(thumbnail, x, y);
        }

        private static bool IsThumbnailPixel(Bitmap image, int x, int y)
        {
            Color color = image.GetPixel(
                Math.Max(0, Math.Min(image.Width - 1, x)),
                Math.Max(0, Math.Min(image.Height - 1, y)));
            int maximum = Math.Max(color.R, Math.Max(color.G, color.B));
            int minimum = Math.Min(color.R, Math.Min(color.G, color.B));
            // CapCut's dark home background is around RGB 14-31, while an
            // unloaded/new draft thumbnail is true black (0-5).  A rendered
            // thumbnail may instead supply luminance or colour separation.
            // Both are evidence; the surrounding dark teal page is neither.
            bool trueBlackThumbnail = maximum <= 5;
            bool renderedThumbnail = maximum >= 52 && (maximum - minimum >= 7 || maximum >= 76);
            return trueBlackThumbnail || renderedThumbnail;
        }

        internal static bool IsStable(Rectangle previous, Rectangle current, int tolerancePixels)
        {
            if (previous.IsEmpty || current.IsEmpty) return false;
            return Math.Abs(previous.Left - current.Left) <= tolerancePixels &&
                Math.Abs(previous.Top - current.Top) <= tolerancePixels &&
                Math.Abs(previous.Width - current.Width) <= tolerancePixels;
        }

    }
}

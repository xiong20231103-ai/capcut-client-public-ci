using System;
using System.Drawing;

namespace CapCutBatchClient
{
    internal static class HdrNoticeVision
    {
        public static bool TryFindAcknowledgement(Bitmap bitmap, out Point point)
        {
            point = Point.Empty;
            if (bitmap == null || bitmap.Width < 800 || bitmap.Height < 600) return false;

            // Verified against CapCut's inline "unsupported HDR device" card.
            // Its acknowledgement sits just above the centre of the editor;
            // the previous 48% lower bound missed the real button near 44.6%.
            int left = (int)(bitmap.Width * 0.38);
            int right = (int)(bitmap.Width * 0.62);
            int top = (int)(bitmap.Height * 0.38);
            int bottom = (int)(bitmap.Height * 0.60);
            int minimumRun = Math.Max(110, (int)(bitmap.Width * 0.045));
            int maximumRun = Math.Max(minimumRun + 1, (int)(bitmap.Width * 0.18));

            for (int y = top; y < bottom; y += 2) {
                int runStart = -1;
                for (int x = left; x < right; x++) {
                    Color color = bitmap.GetPixel(x, y);
                    bool cyan = color.R <= 55 && color.G >= 145 && color.B >= 150;
                    if (cyan && runStart < 0) runStart = x;
                    if ((!cyan || x == right - 1) && runStart >= 0) {
                        int runEnd = cyan && x == right - 1 ? x + 1 : x;
                        int runWidth = runEnd - runStart;
                        if (runWidth >= minimumRun && runWidth <= maximumRun) {
                            point = new Point((runStart + runEnd) / 2, y);
                            return true;
                        }
                        runStart = -1;
                    }
                }
            }
            return false;
        }
    }
}

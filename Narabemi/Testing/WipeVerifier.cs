using System;
using Avalonia;
using Avalonia.Media.Imaging;

namespace Narabemi.Testing
{
    /// <summary>
    /// Quantitative wipe-alignment check. Player A's cropped output ends at source
    /// pixel x = round(W * ratio) - 1; Player B's cropped output starts at source
    /// pixel x = round(W * ratio). Those two columns are adjacent in the original
    /// source frame, so for the SAME video at the SAME timestamp on both players
    /// they should look nearly identical (modulo H.264 decoder noise and any
    /// per-player frame timing drift).
    /// </summary>
    internal static class WipeVerifier
    {
        /// <summary>
        /// Computes per-row sum of |B-B'| + |G-G'| + |R-R'| for one column from
        /// each PNG. Returns average and max across all rows.
        /// </summary>
        public static (double avgDiff, int maxDiff, int rows) DiffColumns(
            string pathA, int colA, string pathB, int colB)
        {
            var a = LoadBgra(pathA);
            var b = LoadBgra(pathB);
            var rows = Math.Min(a.height, b.height);
            if (rows <= 0 || colA < 0 || colA >= a.width || colB < 0 || colB >= b.width)
                return (-1, -1, 0);

            long total = 0;
            int max = 0;
            for (int y = 0; y < rows; y++)
            {
                var iA = y * a.stride + colA * 4;
                var iB = y * b.stride + colB * 4;
                int d = Math.Abs(a.pixels[iA]     - b.pixels[iB])
                      + Math.Abs(a.pixels[iA + 1] - b.pixels[iB + 1])
                      + Math.Abs(a.pixels[iA + 2] - b.pixels[iB + 2]);
                total += d;
                if (d > max) max = d;
            }
            return ((double)total / rows, max, rows);
        }

        private static (int width, int height, byte[] pixels, int stride) LoadBgra(string path)
        {
            using var bmp = new Bitmap(path);
            var w = bmp.PixelSize.Width;
            var h = bmp.PixelSize.Height;
            var stride = w * 4;
            var pixels = new byte[stride * h];
            unsafe
            {
                fixed (byte* p = pixels)
                {
                    bmp.CopyPixels(new PixelRect(0, 0, w, h), (IntPtr)p, stride * h, stride);
                }
            }
            return (w, h, pixels, stride);
        }
    }
}

using System;

namespace Narabemi.Gpu
{
    /// <summary>
    /// CPU-side video frame blending. Composites two BGRA frame buffers into a
    /// destination buffer using a horizontal or vertical split with an optional border.
    /// </summary>
    public static class CpuBlender
    {
        /// <summary>
        /// Horizontal split: left portion from srcA, right portion from srcB.
        /// </summary>
        public static unsafe void BlendHorizontal(
            byte* dst, int dstStride,
            byte* srcA, int strideA,
            byte* srcB, int strideB,
            int width, int height,
            float ratio,
            float borderWidth,
            byte borderR, byte borderG, byte borderB)
        {
            int splitX = (int)(ratio * width);
            int borderHalf = (int)(borderWidth * 0.5f);
            int borderLeft = Math.Max(0, splitX - borderHalf);
            int borderRight = Math.Min(width, splitX + borderHalf);

            // Precompute the border pixel (BGRA)
            uint borderPixel = (uint)(borderB | (borderG << 8) | (borderR << 16) | (0xFF << 24));

            for (int y = 0; y < height; y++)
            {
                byte* dstRow = dst + (long)y * dstStride;
                byte* rowA = srcA + (long)y * strideA;
                byte* rowB = srcB + (long)y * strideB;

                // Left portion (PlayerA)
                int leftBytes = splitX * 4;
                if (leftBytes > 0)
                    Buffer.MemoryCopy(rowA, dstRow, leftBytes, leftBytes);

                // Right portion (PlayerB)
                int rightBytes = (width - splitX) * 4;
                if (rightBytes > 0)
                    Buffer.MemoryCopy(rowB + splitX * 4, dstRow + splitX * 4, rightBytes, rightBytes);

                // Border overlay
                if (borderHalf > 0)
                {
                    uint* dstPixels = (uint*)dstRow;
                    for (int x = borderLeft; x < borderRight; x++)
                        dstPixels[x] = borderPixel;
                }
            }
        }

        /// <summary>
        /// Single source pass-through (when only one video is loaded).
        /// </summary>
        public static unsafe void CopyFrame(
            byte* dst, int dstStride,
            byte* src, int srcStride,
            int width, int height)
        {
            int rowBytes = width * 4;
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(
                    src + (long)y * srcStride,
                    dst + (long)y * dstStride,
                    dstStride, rowBytes);
            }
        }
    }
}

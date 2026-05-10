using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Narabemi.Testing
{
    /// <summary>
    /// Captures a window's client area using Win32 PrintWindow.
    /// Returns an Avalonia WriteableBitmap that can be saved as PNG.
    /// </summary>
    internal static partial class WindowCapture
    {
        private const uint PW_CLIENTONLY = 0x01;
        private const uint PW_RENDERFULLCONTENT = 0x02;
        private const uint DIB_RGB_COLORS = 0;
        private const int BI_RGB = 0;

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetClientRect(IntPtr hwnd, out RECT lpRect);

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetDC(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        private static partial int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [LibraryImport("gdi32.dll")]
        private static partial IntPtr CreateCompatibleDC(IntPtr hdc);

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeleteDC(IntPtr hdc);

        [LibraryImport("gdi32.dll")]
        private static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

        [LibraryImport("gdi32.dll")]
        private static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeleteObject(IntPtr ho);

        [LibraryImport("gdi32.dll")]
        private static partial int GetDIBits(
            IntPtr hdc, IntPtr hbm, uint start, uint cLines,
            IntPtr lpvBits, ref BITMAPINFO lpbmi, uint usage);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
        }

        public static WriteableBitmap? CaptureWindow(IntPtr hwnd)
        {
            if (!GetClientRect(hwnd, out var rect))
                return null;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return null;

            var screenDC = GetDC(IntPtr.Zero);
            var memDC = CreateCompatibleDC(screenDC);
            var hBitmap = CreateCompatibleBitmap(screenDC, width, height);
            var oldBitmap = SelectObject(memDC, hBitmap);

            try
            {
                PrintWindow(hwnd, memDC, PW_CLIENTONLY | PW_RENDERFULLCONTENT);

                var bmi = new BITMAPINFO
                {
                    bmiHeader =
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = width,
                        biHeight = -height, // top-down DIB
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = BI_RGB,
                    },
                };

                int stride = width * 4;
                var buffer = Marshal.AllocHGlobal(stride * height);
                try
                {
                    int result = GetDIBits(memDC, hBitmap, 0, (uint)height, buffer, ref bmi, DIB_RGB_COLORS);
                    if (result == 0)
                    {
                        // Fallback: try bottom-up layout
                        bmi.bmiHeader.biHeight = height;
                        result = GetDIBits(memDC, hBitmap, 0, (uint)height, buffer, ref bmi, DIB_RGB_COLORS);
                        if (result == 0)
                            return null;

                        FlipVertical(buffer, width, height, stride);
                    }

                    // PrintWindow may leave alpha as 0x00 on DWM-composited windows.
                    // Force opaque alpha so the saved PNG isn't invisible.
                    ForceOpaqueAlpha(buffer, stride * height);

                    var wb = new WriteableBitmap(
                        new PixelSize(width, height),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Premul);

                    using (var fb = wb.Lock())
                    {
                        unsafe
                        {
                            Buffer.MemoryCopy(
                                (void*)buffer,
                                (void*)fb.Address,
                                (long)fb.RowBytes * height,
                                (long)stride * height);
                        }
                    }

                    return wb;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                SelectObject(memDC, oldBitmap);
                DeleteObject(hBitmap);
                DeleteDC(memDC);
                ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        private static unsafe void ForceOpaqueAlpha(IntPtr buffer, int totalBytes)
        {
            var p = (byte*)buffer;
            for (int i = 3; i < totalBytes; i += 4)
                p[i] = 0xFF;
        }

        private static unsafe void FlipVertical(IntPtr buffer, int width, int height, int stride)
        {
            var p = (byte*)buffer;
            var tmp = stackalloc byte[stride];
            for (int y = 0; y < height / 2; y++)
            {
                var top = p + (long)y * stride;
                var bot = p + (long)(height - 1 - y) * stride;
                Buffer.MemoryCopy(top, tmp, stride, stride);
                Buffer.MemoryCopy(bot, top, stride, stride);
                Buffer.MemoryCopy(tmp, bot, stride, stride);
            }
        }
    }
}

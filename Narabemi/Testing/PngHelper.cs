using System.IO;

namespace Narabemi.Testing
{
    /// <summary>Minimal PNG header parser — we only need IHDR width / height.</summary>
    internal static class PngHelper
    {
        /// <summary>
        /// Returns (width, height) by reading the IHDR chunk; returns (0, 0) on any
        /// failure (file too short, missing signature, etc.).
        /// </summary>
        public static (int width, int height) ReadDimensions(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var buf = new byte[24];
                if (fs.Read(buf, 0, 24) != 24) return (0, 0);

                // PNG: 8-byte signature, then IHDR chunk: 4-byte length, "IHDR" tag,
                // 13-byte data starting with width(4) and height(4) — both big-endian.
                var w = (buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19];
                var h = (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23];
                if (w <= 0 || h <= 0) return (0, 0);
                return (w, h);
            }
            catch
            {
                return (0, 0);
            }
        }
    }
}

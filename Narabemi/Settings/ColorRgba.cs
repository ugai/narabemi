using System;

namespace Narabemi.Settings
{
    /// <summary>
    /// Platform-neutral RGBA color value, used in the Settings layer to avoid
    /// a dependency on UI framework color types.
    /// </summary>
    public record struct ColorRgba(byte R, byte G, byte B, byte A)
    {
        public static readonly ColorRgba White = new(255, 255, 255, 255);

        /// <summary>
        /// Parses a CSS-style hex string: <c>#RRGGBB</c> or <c>#AARRGGBB</c> (WPF format).
        /// Returns <see cref="White"/> on parse failure.
        /// </summary>
        public static ColorRgba FromHex(string hex)
        {
            var s = hex.AsSpan().TrimStart('#');
            try
            {
                return s.Length switch
                {
                    6 => new ColorRgba(
                        Convert.ToByte(s[0..2].ToString(), 16),
                        Convert.ToByte(s[2..4].ToString(), 16),
                        Convert.ToByte(s[4..6].ToString(), 16),
                        255),
                    8 => new ColorRgba(
                        Convert.ToByte(s[2..4].ToString(), 16),
                        Convert.ToByte(s[4..6].ToString(), 16),
                        Convert.ToByte(s[6..8].ToString(), 16),
                        Convert.ToByte(s[0..2].ToString(), 16)),
                    _ => White,
                };
            }
            catch (FormatException)
            {
                return White;
            }
        }

        /// <summary>
        /// Returns <c>#RRGGBB</c> when fully opaque, or <c>#AARRGGBB</c> otherwise.
        /// </summary>
        public override string ToString() =>
            A == 255
                ? $"#{R:X2}{G:X2}{B:X2}"
                : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
    }
}

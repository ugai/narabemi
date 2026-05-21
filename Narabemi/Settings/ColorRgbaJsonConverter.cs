using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Narabemi.Settings
{
    public class ColorRgbaJsonConverter : JsonConverter<ColorRgba>
    {
        // Called with a human-readable message whenever a value is null or
        // fails to parse and the converter falls back to White.
        private readonly Action<string>? _warn;

        /// <summary>
        /// Creates a converter that invokes <paramref name="warn"/> whenever a
        /// hex value is <see langword="null"/> or cannot be parsed.
        /// </summary>
        public ColorRgbaJsonConverter(Action<string> warn)
        {
            _warn = warn;
        }

        /// <summary>
        /// Creates a converter without a warning callback. Parse failures fall
        /// back to <see cref="ColorRgba.White"/> silently. Prefer the overload
        /// that accepts a callback so failures are observable.
        /// </summary>
        public ColorRgbaJsonConverter()
        {
        }

        public override ColorRgba Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var s = reader.GetString();

            if (s is null)
            {
                _warn?.Invoke($"{nameof(ColorRgbaJsonConverter)}: color value is null, defaulting to White.");
                return ColorRgba.White;
            }

            if (!ColorRgba.TryFromHex(s, out var color))
            {
                _warn?.Invoke($"{nameof(ColorRgbaJsonConverter)}: invalid color value '{s}', defaulting to White.");
                return ColorRgba.White;
            }

            return color;
        }

        public override void Write(Utf8JsonWriter writer, ColorRgba value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}

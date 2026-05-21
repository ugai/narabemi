using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Narabemi.Settings;

namespace Narabemi
{
    public class ColorRgbaJsonConverter : JsonConverter<ColorRgba>
    {
        public override ColorRgba Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var s = reader.GetString();
            return s is null ? ColorRgba.White : ColorRgba.FromHex(s);
        }

        public override void Write(Utf8JsonWriter writer, ColorRgba value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}

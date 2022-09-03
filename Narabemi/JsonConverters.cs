using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Narabemi
{
    public class ColorJsonConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            (Color)ColorConverter.ConvertFromString(reader.GetString());

        public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AICOS.BuildingBlocks.Http;

/// <summary>
/// Reads <see cref="TimeOnly"/> as "HH:mm" or "HH:mm:ss" (System.Text.Json in .NET 8 only accepts the latter)
/// and always writes "HH:mm" - the format shop opening hours, shifts and pickup slots are entered in.
/// </summary>
public sealed class TimeOnlyJsonConverter : JsonConverter<TimeOnly>
{
    private static readonly string[] Formats = ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss"];

    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (TimeOnly.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return time;
        }

        throw new JsonException($"'{text}' is not a valid time. Use the 24-hour format HH:mm, e.g. \"07:30\".");
    }

    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("HH:mm", CultureInfo.InvariantCulture));
}

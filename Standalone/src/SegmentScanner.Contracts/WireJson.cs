using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SegmentScanner.Contracts;

public static class WireJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    public static T Read<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("Empty JSON document.");

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string Hash<T>(T value)
    {
        using var document = JsonDocument.Parse(Write(value));
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            WriteCanonical(writer, document.RootElement);
        }

        return Convert.ToHexStringLower(SHA256.HashData(output.ToArray()));
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 32,
            WriteIndented = true,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        options.Converters.Add(new DecimalStringConverter());
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }

            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
            {
                WriteCanonical(writer, item);
            }

            writer.WriteEndArray();
        }
        else
        {
            element.WriteTo(writer);
        }
    }

    private sealed class DecimalStringConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (value is null || !long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
                || value != parsed.ToString(CultureInfo.InvariantCulture))
            {
                throw new JsonException("64-bit integers must be canonical decimal strings.");
            }

            return parsed;
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}

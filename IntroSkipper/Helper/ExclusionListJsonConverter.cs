// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using IntroSkipper.Data;

namespace IntroSkipper.Helper;

/// <summary>
/// JSON converter that writes structured exclusion arrays and reads legacy string values for resilience.
/// </summary>
public sealed class ExclusionListJsonConverter : JsonConverter<ExclusionList>
{
    /// <inheritdoc />
    public override ExclusionList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var list = new ExclusionList();
        if (reader.TokenType == JsonTokenType.String)
        {
            AddLegacyItems(list, reader.GetString());
            return list;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected an exclusion list array or legacy comma-separated string.");
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return list;
            }

            if (reader.TokenType == JsonTokenType.Null)
            {
                continue;
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected an exclusion list item string.");
            }

            list.Add(reader.GetString() ?? string.Empty);
        }

        throw new JsonException("Unexpected end of exclusion list array.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ExclusionList value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }

    private static void AddLegacyItems(ExclusionList list, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            list.Add(item);
        }
    }
}

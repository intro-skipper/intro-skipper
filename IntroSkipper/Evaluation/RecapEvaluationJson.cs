// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntroSkipper.Evaluation;

/// <summary>
/// Shared, cached <see cref="JsonSerializerOptions"/> for the recap evaluation harness.
/// Cached once to satisfy CA1869 and to keep dataset/detection files human-editable
/// (indented, camelCase, string enums, tolerant of casing).
/// </summary>
internal static class RecapEvaluationJson
{
    /// <summary>
    /// Gets the shared serializer options used for every dataset and detection file.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            // Populate the get-only Labels/Detections lists on deserialize instead of leaving them empty.
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

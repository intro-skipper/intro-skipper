// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;

namespace IntroSkipper.Subtitles;

/// <summary>
/// Parses <c>ffprobe -show_streams</c> JSON output into <see cref="SubtitleStreamInfo"/> values and
/// applies the text/image codec classification used to scope phrase detection.
/// </summary>
/// <remarks>
/// Expected invocation:
/// <c>ffprobe -v error -select_streams s -show_entries stream=index,codec_name,codec_type,disposition:stream_tags=language -of json &lt;path&gt;</c>.
/// </remarks>
public static class SubtitleProbe
{
    /// <summary>
    /// Parses ffprobe JSON into subtitle stream descriptors. Non-subtitle streams are ignored.
    /// </summary>
    /// <param name="ffprobeJson">The raw JSON emitted by ffprobe.</param>
    /// <returns>Subtitle streams in document order. Empty when none are present or parsing fails.</returns>
    public static IReadOnlyList<SubtitleStreamInfo> Parse(string? ffprobeJson)
    {
        if (string.IsNullOrWhiteSpace(ffprobeJson))
        {
            return [];
        }

        var streams = new List<SubtitleStreamInfo>();

        try
        {
            using var document = JsonDocument.Parse(ffprobeJson);
            if (!document.RootElement.TryGetProperty("streams", out var streamsElement) ||
                streamsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var stream in streamsElement.EnumerateArray())
            {
                if (!TryGetString(stream, "codec_type", out var codecType) ||
                    !string.Equals(codecType, "subtitle", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var index = stream.TryGetProperty("index", out var indexElement) &&
                            indexElement.TryGetInt32(out var parsedIndex)
                    ? parsedIndex
                    : -1;

                TryGetString(stream, "codec_name", out var codec);
                codec ??= string.Empty;

                string? language = null;
                if (stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                {
                    TryGetString(tags, "language", out language);
                }

                var forced = false;
                if (stream.TryGetProperty("disposition", out var disposition) &&
                    disposition.ValueKind == JsonValueKind.Object &&
                    disposition.TryGetProperty("forced", out var forcedElement) &&
                    forcedElement.TryGetInt32(out var forcedValue))
                {
                    forced = forcedValue != 0;
                }

                streams.Add(new SubtitleStreamInfo(
                    index,
                    codec,
                    language,
                    SubtitleCodec.IsTextBased(codec),
                    forced));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return streams;
    }

    private static bool TryGetString(JsonElement element, string property, out string? value)
    {
        value = null;
        if (element.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.String)
        {
            value = child.GetString();
            return value is not null;
        }

        return false;
    }
}

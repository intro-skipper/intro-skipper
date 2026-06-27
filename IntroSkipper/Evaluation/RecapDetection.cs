// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace IntroSkipper.Evaluation;

/// <summary>
/// One detector's output for a single episode, expressed independently of the production
/// <see cref="Data.Segment"/> type so the harness can be fed canned values in tests.
/// </summary>
/// <remarks>
/// To build this from a real analysis result, set <see cref="Detected"/> to the segment's
/// <see cref="Data.Segment.Valid"/> flag and copy <c>Start</c>/<c>End</c> into
/// <see cref="DetectedStart"/>/<see cref="DetectedEnd"/> (see <see cref="FromInterval"/>).
/// </remarks>
internal sealed class RecapDetection
{
    /// <summary>
    /// Gets or sets the series name.
    /// </summary>
    public string Series { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    public int Season { get; set; }

    /// <summary>
    /// Gets or sets the episode number.
    /// </summary>
    public int Episode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a recap was detected for this episode.
    /// </summary>
    public bool Detected { get; set; }

    /// <summary>
    /// Gets or sets the detected recap start, in seconds.
    /// </summary>
    public double DetectedStart { get; set; }

    /// <summary>
    /// Gets or sets the detected recap end, in seconds.
    /// </summary>
    public double DetectedEnd { get; set; }

    /// <summary>
    /// Gets or sets the signal that produced the detection (e.g. "chapter", "chromaprint",
    /// "blackframe", "subtitle"). Optional; for reporting and debugging only.
    /// </summary>
    public string? Signal { get; set; }

    /// <summary>
    /// Gets the detected interval, or an empty interval when nothing was detected.
    /// </summary>
    [JsonIgnore]
    public RecapInterval Interval => Detected ? new RecapInterval(DetectedStart, DetectedEnd) : RecapInterval.Empty;

    /// <summary>
    /// Gets the normalized join key for this detection.
    /// </summary>
    [JsonIgnore]
    public string Key => RecapEpisodeKey.For(Series, Season, Episode);

    /// <summary>
    /// Builds a detection from an interval. Pass <see cref="RecapInterval.Empty"/> (or any
    /// interval whose <see cref="RecapInterval.HasValue"/> is <see langword="false"/>) to record a non-detection.
    /// </summary>
    /// <param name="series">Series name.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="interval">Detected interval.</param>
    /// <param name="signal">Optional originating signal name.</param>
    /// <returns>A populated <see cref="RecapDetection"/>.</returns>
    public static RecapDetection FromInterval(string series, int season, int episode, RecapInterval interval, string? signal = null)
    {
        return new RecapDetection
        {
            Series = series,
            Season = season,
            Episode = episode,
            Detected = interval.HasValue,
            DetectedStart = interval.HasValue ? interval.Start : 0.0,
            DetectedEnd = interval.HasValue ? interval.End : 0.0,
            Signal = signal,
        };
    }
}

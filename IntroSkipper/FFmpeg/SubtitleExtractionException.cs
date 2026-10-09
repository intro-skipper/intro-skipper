// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Indicates that some subtitle sources failed while returning cues from the sources that were
/// readable, allowing callers to use a positive match without treating a partial scan as a no-match.
/// </summary>
internal sealed class SubtitleExtractionException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleExtractionException"/> class.
    /// </summary>
    /// <param name="message">Description of the incomplete extraction.</param>
    /// <param name="cues">Cues read successfully from available sources.</param>
    /// <param name="innerException">The first source or probe failure.</param>
    public SubtitleExtractionException(string message, SubtitleCue[] cues, Exception innerException)
        : base(message, innerException)
    {
        Cues = cues;
    }

    /// <summary>
    /// Gets the cues extracted successfully before the source failure was reported.
    /// </summary>
    public SubtitleCue[] Cues { get; }
}

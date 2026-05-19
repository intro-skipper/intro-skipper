// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Thrown when an FFmpeg media detection process completes with a nonzero exit code.
/// The stderr summary is placed in the exception <see cref="Exception.Message"/>.
/// </summary>
public class FFmpegDetectionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FFmpegDetectionException"/> class.
    /// </summary>
    public FFmpegDetectionException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FFmpegDetectionException"/> class.
    /// </summary>
    /// <param name="message">The error message that describes the failure.</param>
    public FFmpegDetectionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FFmpegDetectionException"/> class.
    /// </summary>
    /// <param name="message">The error message that describes the failure.</param>
    /// <param name="innerException">The inner exception.</param>
    public FFmpegDetectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets the FFmpeg process exit code.
    /// </summary>
    public int ExitCode { get; init; }
}

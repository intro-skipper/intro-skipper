// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Exception raised when ffmpeg reports that a filter a scan needs is not built into it.
/// Nothing from that run is cached, so the next scan asks again.
/// </summary>
public sealed class MissingFilterException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MissingFilterException"/> class.
    /// </summary>
    /// <param name="message">Exception message.</param>
    public MissingFilterException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MissingFilterException"/> class.
    /// </summary>
    /// <param name="message">Exception message.</param>
    /// <param name="inner">Inner exception.</param>
    public MissingFilterException(string message, Exception inner) : base(message, inner)
    {
    }
}

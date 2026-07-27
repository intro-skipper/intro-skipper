// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Thrown when a segment update would collide with another stored segment of the same
/// item and mode covering exactly the same range. Controllers translate this to HTTP 409.
/// </summary>
public sealed class SegmentConflictException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentConflictException"/> class.
    /// </summary>
    public SegmentConflictException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentConflictException"/> class.
    /// </summary>
    /// <param name="message">Exception message.</param>
    public SegmentConflictException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentConflictException"/> class.
    /// </summary>
    /// <param name="message">Exception message.</param>
    /// <param name="innerException">Inner exception.</param>
    public SegmentConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

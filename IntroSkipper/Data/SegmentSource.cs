// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Origin of a stored segment. Every write path supplies an explicit source;
/// <see cref="Unknown"/> can only originate from the one-time legacy database
/// import, whose rows predate provenance tracking.
/// </summary>
public enum SegmentSource
{
    /// <summary>
    /// Provenance not recorded — produced only by the legacy database import.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Detected by the chapter analyzer.
    /// </summary>
    Chapter = 1,

    /// <summary>
    /// Detected by the chromaprint (audio fingerprint) analyzer, including recap detection.
    /// </summary>
    Chromaprint = 2,

    /// <summary>
    /// Detected by a black-frame analyzer.
    /// </summary>
    BlackFrame = 3,

    /// <summary>
    /// Derived from the end of a detected credits segment (anime preview).
    /// </summary>
    CreditsDerived = 4,

    /// <summary>
    /// Provided by the user via the segment editor or the HTTP API.
    /// User segments are never overwritten or deleted by automatic analysis.
    /// </summary>
    User = 5
}
